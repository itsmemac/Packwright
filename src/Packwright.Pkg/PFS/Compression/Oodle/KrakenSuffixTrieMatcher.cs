// LibProsperoPkg - A library for building and inspecting PS5 packages.
// Copyright (C) 2026 SvenGDK
//
// ---------------------------------------------------------------------------------------------------
// Match finder for the Kraken (newLZ) level-7 parse: a compacted suffix trie with suffix links, built
// incrementally over one whole block, producing four (length, offset) pairs per position.
//
// The trie is built once per block and the whole per-position table is filled in a single left-to-right
// pass. Work per position is amortised constant: the descent resumes at the previous position's suffix
// link, the byte comparison starts at the previous match length minus one, and the output walk visits at
// most sixteen ancestors. That is what keeps a 256 KiB block linear instead of quadratic.
//
// Table layout: PairsPerPosition (4) pairs of two int32 per position, i.e. eight ints per position.
// Pair 0 is the longest match the trie represents at that position; each later pair is strictly shorter
// at a strictly smaller offset. A list shorter than four pairs ends with a length-0 entry. A position
// with no match writes a single 0. Lengths are NOT clipped to any tail reserve — the consumer applies
// its own match-end limit, so the ranking here is by the untruncated length.
// ---------------------------------------------------------------------------------------------------
#nullable enable
using System;

namespace LibProsperoPkg.PFS.Compression.Oodle;

/// <summary>
/// Compacted suffix trie over one block, filling a four-pair match table for every position.
/// One instance covers one block; matches never reach outside it.
/// </summary>
internal sealed class KrakenSuffixTrieMatcher
{
    /// <summary>Pairs reported per position.</summary>
    public const int PairsPerPosition = 4;

    /// <summary>Ints per position in the match table: one length and one offset per pair.</summary>
    public const int IntsPerPosition = PairsPerPosition * 2;

    // Two leading bytes index the hash table directly, so it is collision-free for any block this
    // format can hold (a block is at most 256 KiB, far below the 8 MiB three-byte threshold).
    private const int FirstBytes = 2;
    private const int HashBits = 16;
    private const int HashMask = (1 << HashBits) - 1;

    // The output walk refreshes at most this many ancestors, which is the one place the reported offset
    // can be older than the nearest occurrence.
    private const int AncestorWalkLimit = 16;

    // Ancestor pairs are never reported below this length.
    private const int MinAncestorLength = 3;

    private readonly byte[] _data;
    private readonly int _sizePlus1;
    private readonly int[] _hash;

    // Node fields, struct of arrays. Index 0 means "none"; a negative index is a leaf whose position is
    // the negated value.
    private readonly int[] _nodePos;
    private readonly int[] _nodeParent;
    private readonly int[] _nodeFollow;
    private readonly int[] _nodeDepth;

    // Children live in one growable slot pool. Lookup is by exact byte and a node never holds the same
    // byte twice, so the container shape cannot change which child is returned.
    private readonly int[] _childStart;
    private readonly int[] _childCount;
    private readonly int[] _childCapacity;
    private byte[] _childChar;
    private int[] _childNode;
    private int _childUsed;

    private int _pos = 1;          // 1-based; the byte at position p is _data[p - 1]
    private int _nextNode = 1;
    private int _lastMl;
    private int _cur;
    private int _nodeToUpdateFollow;
    private int _maxmlOffs;

    /// <summary>Creates a matcher over <paramref name="data"/>, which must be one whole block.</summary>
    public KrakenSuffixTrieMatcher(byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);
        _data = data;
        _sizePlus1 = data.Length + 1;
        _hash = new int[1 << HashBits];

        int nodeCapacity = data.Length + 2;
        _nodePos = new int[nodeCapacity];
        _nodeParent = new int[nodeCapacity];
        _nodeFollow = new int[nodeCapacity];
        _nodeDepth = new int[nodeCapacity];
        _childStart = new int[nodeCapacity];
        _childCount = new int[nodeCapacity];
        _childCapacity = new int[nodeCapacity];

        int slotCapacity = data.Length < 64 ? 256 : data.Length * 2;
        _childChar = new byte[slotCapacity];
        _childNode = new int[slotCapacity];
    }

    /// <summary>
    /// Fills <paramref name="matches"/> for every position of the block. The array must hold
    /// <c>data.Length * IntsPerPosition</c> ints.
    /// </summary>
    public void Run(int[] matches)
    {
        ArgumentNullException.ThrowIfNull(matches);
        int count = _data.Length;
        int startPos = _pos;
        int processEnd = Math.Min(_pos + count, _sizePlus1 - 4);
        int posEnd = Math.Min(_pos + count, _sizePlus1);

        while (_pos < processEnd)
        {
            if (_maxmlOffs != 0)
            {
                FillTerminated(matches, startPos, processEnd);
                break;
            }

            int pos = _pos;
            int maxml = _sizePlus1 - pos;
            int guaranteedMl = _lastMl > 1 ? _lastMl - 1 : 0;
            int parent = 0;
            int deepestNoFollow = 0;
            int deepestWithFollow = 0;
            int followLimit = _nodeToUpdateFollow == 0
                ? int.MaxValue
                : (_nodeFollow[_nodeToUpdateFollow] == 0 ? 0 : _nodeDepth[_nodeFollow[_nodeToUpdateFollow]]);

            int cur;
            int curml;
            int matchPos;   // 1-based position of the string the match was found against

            if (_cur < 1)
            {
                int h = (_data[pos - 1] | (_data[pos] << 8)) & HashMask;
                cur = _hash[h];
                if (cur == 0)
                {
                    // First time these two bytes occur: record the position and report nothing.
                    _hash[h] = -pos;
                    _lastMl = 0;
                    _cur = 0;
                    matches[(pos - startPos) * IntsPerPosition] = 0;
                    _pos = pos + 1;
                    continue;
                }

                curml = FirstBytes;
                if (cur < 0)
                {
                    _hash[h] = _nextNode;
                    goto SplitAgainstLeaf;
                }

                int rootDepth = _nodeDepth[cur];
                if (curml < guaranteedMl) curml = guaranteedMl;
                if (rootDepth <= curml)
                {
                    curml = rootDepth;
                    goto DescendStep;
                }
                int rootOther = _nodePos[cur];
                while (curml < rootDepth)
                {
                    if (curml == maxml) goto MaxmlReached;
                    if (_data[pos - 1 + curml] != _data[rootOther - 1 + curml])
                    {
                        _hash[h] = _nextNode;
                        goto SplitAgainstNode;
                    }
                    curml++;
                }
                goto DescendStep;
            }

            cur = _cur;
            curml = _nodeDepth[cur];

        DescendStep:
            while (true)
            {
                if (followLimit < curml)
                {
                    if (curml < _nodeDepth[_nodeToUpdateFollow])
                    {
                        followLimit = curml;
                        _nodeFollow[_nodeToUpdateFollow] = cur;
                    }
                    else
                    {
                        followLimit = int.MaxValue;
                    }
                }
                if (curml >= maxml) goto MaxmlReached;

                byte c = _data[pos - 1 + curml];
                if (_nodeFollow[cur] == 0) deepestNoFollow = cur; else deepestWithFollow = cur;

                int slot = FindChildSlot(cur, c);
                if (slot < 0)
                {
                    AddChild(cur, c, -pos);
                    // A new leaf hung off an existing node: that node is the match.
                    _nodeToUpdateFollow = deepestNoFollow > 0 ? deepestNoFollow : deepestWithFollow;
                    matchPos = _nodePos[cur];
                    parent = cur;
                    goto Emit;
                }

                int child = _childNode[slot];
                if (child < 0)
                {
                    _childNode[slot] = _nextNode;
                    parent = cur;
                    curml++;
                    cur = child;
                    goto SplitAgainstLeaf;
                }

                curml++;
                int childDepth = _nodeDepth[child];
                if (curml < guaranteedMl) curml = guaranteedMl;
                if (curml < childDepth)
                {
                    int other = _nodePos[child];
                    for (; curml < childDepth; curml++)
                    {
                        if (curml == maxml) { cur = child; goto MaxmlReached; }
                        if (_data[pos - 1 + curml] != _data[other - 1 + curml])
                        {
                            _childNode[slot] = _nextNode;
                            parent = cur;
                            cur = child;
                            goto SplitAgainstNode;
                        }
                    }
                }
                else
                {
                    curml = childDepth;
                }
                cur = child;
            }

        SplitAgainstLeaf:
            {
                int other = -cur;
                if (curml < guaranteedMl) curml = guaranteedMl;
                while (true)
                {
                    if (curml == maxml) goto MaxmlReached;
                    if (_data[pos - 1 + curml] != _data[other - 1 + curml]) break;
                    curml++;
                }
                matchPos = MakeNode(pos, curml, parent, cur, other, ref followLimit);
                goto Emit;
            }

        SplitAgainstNode:
            {
                int other = _nodePos[cur];
                _nodeParent[cur] = _nextNode;
                matchPos = MakeNode(pos, curml, parent, cur, other, ref followLimit);
                goto Emit;
            }

        MaxmlReached:
            {
                int otherPos = cur < 0 ? -cur : _nodePos[cur];
                _maxmlOffs = pos - otherPos;
                _lastMl = maxml;
                FillTerminated(matches, startPos, processEnd);
                _pos = processEnd;
                break;
            }

        Emit:
            {
                _cur = deepestWithFollow == 0 ? 0 : _nodeFollow[deepestWithFollow];
                _lastMl = curml;

                int outIndex = (pos - startPos) * IntsPerPosition;
                matches[outIndex] = curml;
                matches[outIndex + 1] = pos - matchPos;
                int numPairs = 1;
                int walked = 0;
                int p = parent;
                while (p != 0)
                {
                    if (numPairs < PairsPerPosition)
                    {
                        int newOffset = pos - _nodePos[p];
                        if (newOffset < matches[outIndex + (numPairs - 1) * 2 + 1])
                        {
                            int length = _nodeDepth[p];
                            if (length < MinAncestorLength) break;
                            matches[outIndex + numPairs * 2] = length;
                            matches[outIndex + numPairs * 2 + 1] = newOffset;
                            numPairs++;
                        }
                    }
                    if (++walked > AncestorWalkLimit) break;
                    _nodePos[p] = pos;
                    p = _nodeParent[p];
                }
                if (numPairs < PairsPerPosition)
                    matches[outIndex + numPairs * 2] = 0;
                _pos = pos + 1;
            }
        }

        // Positions past the trie loop carry no match.
        for (int p = _pos; p < posEnd; p++)
            matches[(p - startPos) * IntsPerPosition] = 0;
        _pos = posEnd;
    }

    // Once the current suffix matches an earlier occurrence to the end of the block the trie stops
    // being updated and every remaining position reports one pair at the same offset.
    private void FillTerminated(int[] matches, int startPos, int processEnd)
    {
        for (int p = _pos; p < processEnd; p++)
        {
            int outIndex = (p - startPos) * IntsPerPosition;
            matches[outIndex] = _sizePlus1 - p;
            matches[outIndex + 1] = _maxmlOffs;
            matches[outIndex + 2] = 0;
        }
        _pos = processEnd;
    }

    // Creates the branching node that separates the current suffix from the string it diverged from,
    // and returns that string's position.
    private int MakeNode(int pos, int curml, int parent, int cur, int otherPos, ref int followLimit)
    {
        int newIdx = _nextNode++;
        _nodeDepth[newIdx] = curml;
        _nodeFollow[newIdx] = 0;
        _nodePos[newIdx] = pos;
        _nodeParent[newIdx] = parent;
        _childStart[newIdx] = 0;
        _childCount[newIdx] = 0;
        _childCapacity[newIdx] = 0;
        AddChild(newIdx, _data[pos - 1 + curml], -pos);
        AddChild(newIdx, _data[otherPos - 1 + curml], cur);
        if (followLimit < curml)
        {
            if (curml < _nodeDepth[_nodeToUpdateFollow])
            {
                followLimit = curml;
                _nodeFollow[_nodeToUpdateFollow] = newIdx;
            }
            else
            {
                followLimit = int.MaxValue;
            }
        }
        _nodeToUpdateFollow = newIdx;
        return otherPos;
    }

    private int FindChildSlot(int node, byte c)
    {
        int start = _childStart[node];
        int count = _childCount[node];
        for (int i = 0; i < count; i++)
            if (_childChar[start + i] == c)
                return start + i;
        return -1;
    }

    private void AddChild(int node, byte c, int value)
    {
        int count = _childCount[node];
        int capacity = _childCapacity[node];
        if (count == capacity)
        {
            int newCapacity = capacity == 0 ? 2 : capacity * 2;
            EnsureSlots(_childUsed + newCapacity);
            int newStart = _childUsed;
            _childUsed += newCapacity;
            if (count > 0)
            {
                Array.Copy(_childChar, _childStart[node], _childChar, newStart, count);
                Array.Copy(_childNode, _childStart[node], _childNode, newStart, count);
            }
            _childStart[node] = newStart;
            _childCapacity[node] = newCapacity;
        }
        int slot = _childStart[node] + count;
        _childChar[slot] = c;
        _childNode[slot] = value;
        _childCount[node] = count + 1;
    }

    private void EnsureSlots(int needed)
    {
        if (needed <= _childChar.Length)
            return;
        int size = _childChar.Length;
        while (size < needed) size *= 2;
        Array.Resize(ref _childChar, size);
        Array.Resize(ref _childNode, size);
    }
}
