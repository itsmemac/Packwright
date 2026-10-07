// Packwright.Pkg - streaming reader for the inner PFS image of a PS5 package.
//
// How blocks are located and decoded follows the LibProsperoPKG 1.2 NAPS reader (GPL-3.0-or-later, SvenGDK).
// Unlike that reader this one never rebuilds the whole expanded image: it keeps the span table and decodes
// only the spans that are read, so a multi-gigabyte image costs a few megabytes of memory.
#nullable enable
using LibProsperoPkg.PFS.Compression.Oodle;

namespace Packwright.Pkg.Reader;

/// <summary>
/// The expanded (logical) inner PFS image of a PS5 package as a read-only, seekable stream. The stored
/// <c>pfs_image.dat</c> is a series of raw, Kraken-compressed or deduplicated blocks ("spans"); the layout file
/// says where each span is stored and which logical range it expands to. Ranges no span covers read as zeros.
/// </summary>
internal sealed class InnerMount : Stream
{
    private const long Ublock = 0x40000;       // logical blocks are 256K
    private const int Chunk128K = 0x20000;     // even/odd halves of a compressed span
    private const int CacheSlots = 4;

    private readonly Stream _onDisk;
    private readonly NapsLayout _layout;
    private readonly Span[] _spans;
    private readonly (int Index, byte[] Data)?[] _cache = new (int, byte[])?[CacheSlots];
    private int _nextSlot;
    private long _position;

    private readonly record struct Span(long Logical, int Length, long Stored, int StoredLength, int EvenLength,
        byte Even, byte Odd, byte Shuffle, byte Kde);

    /// <summary>Size of the expanded image in bytes.</summary>
    public long MountSize { get; }

    /// <summary>Offset of the inner PFS superblock inside the expanded image.</summary>
    public long SuperblockOffset { get; }

    public InnerMount(Stream onDisk, ReadOnlySpan<byte> naps)
    {
        _onDisk = onDisk;
        _layout = new NapsLayout(naps);
        MountSize = _layout.UncompressedSize;
        if (MountSize <= 0) throw new InvalidDataException("The package layout describes an empty inner image.");
        _spans = BuildSpans(_layout);
        SuperblockOffset = FindSuperblock();
    }

    // ---- span plan ---------------------------------------------------------------------------------------

    // Walks the files in order and, for each, consumes block records until the file's logical range is covered.
    // A span's stored position comes from the run-base record that precedes it (its first stored block number
    // and offset) or, for spans that follow another span directly, from the end of the previous span.
    private static Span[] BuildSpans(NapsLayout layout)
    {
        IReadOnlyList<NapsBlockInfo> blocks = layout.Blocks;
        var spans = new List<Span>(blocks.Count);
        long prevOffset = 0, prevLength = 0;
        long prevTweak = 0;
        bool havePrev = false;

        for (int f = 0; f + 1 < layout.Files.Count; f++)
        {
            NapsFileRecord file = layout.Files[f];
            long length = layout.Files[f + 1].UncompressedOffset - file.UncompressedOffset;
            if (length < 0) throw new InvalidDataException("The package layout has a file boundary that moves backwards.");
            if ((file.Type & 0x40) != 0) continue; // continuation of the previous file

            long position = file.UncompressedOffset, remaining = length;
            int index = remaining == 0 ? 0 : FindBlock(layout, position);
            while (remaining != 0)
            {
                if ((uint)index >= (uint)blocks.Count) throw new InvalidDataException("The package layout ends inside a file.");
                NapsBlockInfo cur = blocks[index];
                if (cur.IsRunBase) { index++; continue; }
                if (cur.IsTerminal) throw new InvalidDataException("The package layout ends before a file does.");

                int next = index + 1;
                if ((uint)next >= (uint)blocks.Count) throw new InvalidDataException("A layout block has no following boundary.");
                int boundary = blocks[next].IsRunBase ? next + 1 : next;
                if ((uint)boundary >= (uint)blocks.Count || blocks[boundary].IsRunBase)
                    throw new InvalidDataException("The package layout has an unexpected run of records.");

                int storedLength = Delta18(blocks[next].IsRunBase ? blocks[next].CoffsetEndMod256K : blocks[next].CoffsetStartMod256K,
                    cur.CoffsetStartMod256K);
                int logicalLength = Delta18(blocks[boundary].UoffsetStart, cur.UoffsetStart);
                if (logicalLength > remaining)
                    throw new InvalidDataException("A layout block crosses the end of its file.");

                long offset, tweak;
                if (index > 0 && blocks[index - 1].IsRunBase)
                {
                    NapsBlockInfo run = blocks[index - 1];
                    offset = ((long)run.CoffsetStart256K << 18) | cur.CoffsetStartMod256K;
                    tweak = run.TweakIdxStart;
                }
                else
                {
                    if (!havePrev) throw new InvalidDataException("The first layout block has no run base.");
                    offset = prevOffset + prevLength;
                    tweak = prevTweak + ((offset >> 16) - (prevOffset >> 16));
                }

                spans.Add(new Span(position, logicalLength, tweak * 0x10000 + (offset & 0xFFFF), storedLength,
                    (int)cur.ClenEvenMinus1 + 1, cur.Even, cur.Odd, cur.ShuffleIdx, cur.KdePredictor));
                prevOffset = offset; prevLength = storedLength; prevTweak = tweak; havePrev = true;
                position += logicalLength;
                remaining -= logicalLength;
                index++;
            }
        }
        return spans.ToArray();
    }

    // The block record that starts exactly at logical offset 'position', found through the per-block index.
    private static int FindBlock(NapsLayout layout, long position)
    {
        int ublock = checked((int)(position >> 18));
        uint first = layout.BlockIndexOfUBlock(ublock);
        uint end = ublock + 1 < layout.UBlockCount ? layout.BlockIndexOfUBlock(ublock + 1) : (uint)layout.Blocks.Count;
        long ublockStart = (long)ublock << 18;
        for (uint i = first; i < end && i < layout.Blocks.Count; i++)
        {
            NapsBlockInfo b = layout.Blocks[(int)i];
            if (!b.IsRunBase && !b.IsTerminal && position == ublockStart + b.UoffsetStart)
                return (int)i;
        }
        throw new InvalidDataException($"No layout block starts at logical offset 0x{position:X}.");
    }

    // Distance from 'previous' to 'next' modulo 256K; an equal pair means a whole 256K.
    private static int Delta18(uint next, uint previous)
    {
        int d = (int)next - (int)previous;
        return d <= 0 ? d + 0x40000 : d;
    }

    // ---- superblock --------------------------------------------------------------------------------------

    // The filesystem metadata starts at the beginning of the last real file; a scan of 64K boundaries is the fallback.
    private long FindSuperblock()
    {
        for (int i = _layout.Files.Count - 2; i >= 0 && i >= _layout.Files.Count - 8; i--)
            if (HasSuperblock(_layout.Files[i].UncompressedOffset)) return _layout.Files[i].UncompressedOffset;
        for (long p = (MountSize - 0x10000) & ~0xFFFFL; p >= 0; p -= 0x10000)
            if (HasSuperblock(p)) return p;
        throw new InvalidDataException("The inner image has no filesystem superblock.");
    }

    private bool HasSuperblock(long offset)
    {
        if (offset < 0 || offset + 16 > MountSize) return false;
        Span<byte> head = stackalloc byte[16];
        long saved = _position;
        try
        {
            _position = offset;
            return Read(head) == 16 &&
                   System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(head) == 2 &&
                   System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(head[8..]) == 20130315;
        }
        // A span nothing points at may be unreadable; that just means "not here".
        catch (InvalidDataException) { return false; }
        finally { _position = saved; }
    }

    // ---- Stream ------------------------------------------------------------------------------------------

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => MountSize;
    public override long Position { get => _position; set => _position = value; }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            _ => MountSize + offset
        };
        if (target < 0) throw new IOException("Cannot seek before the start of the image.");
        _position = target;
        return _position;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int total = 0;
        while (!buffer.IsEmpty && _position < MountSize)
        {
            int index = FindSpan(_position);
            int take;
            if (index < 0)
            {
                // Before the first span or in a gap: zeros up to the next span (or the end).
                int next = ~index;
                long limit = next < _spans.Length ? _spans[next].Logical : MountSize;
                take = (int)Math.Min(buffer.Length, limit - _position);
                buffer[..take].Clear();
            }
            else
            {
                Span span = _spans[index];
                int inside = (int)(_position - span.Logical);
                take = Math.Min(buffer.Length, span.Length - inside);
                GetSpanData(index).AsSpan(inside, take).CopyTo(buffer);
            }
            buffer = buffer[take..];
            _position += take;
            total += take;
        }
        return total;
    }

    // Index of the span containing 'pos', or the complement of the next span's index when it falls in a gap.
    private int FindSpan(long pos)
    {
        int lo = 0, hi = _spans.Length - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >>> 1;
            Span s = _spans[mid];
            if (pos < s.Logical) hi = mid - 1;
            else if (pos >= s.Logical + s.Length) lo = mid + 1;
            else return mid;
        }
        return ~lo;
    }

    private byte[] GetSpanData(int index)
    {
        foreach ((int Index, byte[] Data)? slot in _cache)
            if (slot is { } s && s.Index == index)
                return s.Data;

        byte[] data = Decode(_spans[index], index);
        _cache[_nextSlot] = (index, data);
        _nextSlot = (_nextSlot + 1) % CacheSlots;
        return data;
    }

    // ---- decoding ----------------------------------------------------------------------------------------

    private byte[] Decode(Span span, int index)
    {
        if (span.Kde != 0)
            throw new PkgNotSupportedException($"Layout block {index} needs predictor mode {span.Kde}, which is not supported.");
        if (HasShuffle(span.Shuffle))
            throw new PkgNotSupportedException($"Layout block {index} uses a byte shuffle, which is not supported.");

        var output = new byte[span.Length];
        // A deduplicated all-zero block stores only a stub; the expanded bytes are zeros.
        if (IsDeduplicatedZero(span)) return output;

        if (span.Stored < 0 || span.Stored + span.StoredLength > _onDisk.Length)
            throw new InvalidDataException($"Layout block {index} lies outside the inner image (offset 0x{span.Stored:X}, {span.StoredLength} bytes).");
        byte[] payload = new byte[span.StoredLength];
        _onDisk.Position = span.Stored;
        _onDisk.ReadExactly(payload);

        if (span.StoredLength == span.Length)
        {
            payload.CopyTo(output, 0);
            return output;
        }

        int firstChunk = span.Length > Chunk128K ? span.EvenLength : 0;
        int flags = (((span.Even & 4) != 0) ? 2 : 0) | (((span.Even & 2) != 0) ? 1 : 0)
                  | (((span.Odd & 4) != 0) ? 32 : 0) | (((span.Odd & 2) != 0) ? 16 : 0);
        KrakenDecodeStatus status = KrakenDecoder.DecodeBlock(payload, flags, firstChunk, output);
        if (status != KrakenDecodeStatus.Success)
            throw new InvalidDataException($"Layout block {index} could not be decompressed ({status}).");
        return output;
    }

    // A shuffle index names an entry of the pattern table; an all-zero pattern means "none".
    private bool HasShuffle(byte shuffleIndex)
    {
        if (shuffleIndex == 0) return false;
        if (shuffleIndex > _layout.ShufflePatterns.Count) throw new InvalidDataException("A layout block names a missing shuffle pattern.");
        return _layout.ShufflePatterns[shuffleIndex - 1].Any(b => b != 0);
    }

    private static bool IsDeduplicatedZero(Span s)
    {
        bool odd16 = s.Odd == 1 && s.StoredLength == 16 && s.Length > 16;
        bool even8 = s.Odd == 0 && s.StoredLength == 8 && s.Length > 8 && s.Length <= Chunk128K;
        return (odd16 || even8) && s.EvenLength == 8 && s.Even == 1 && s.Kde == 0 && s.Shuffle == 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _onDisk.Dispose();
        base.Dispose(disposing);
    }
}
