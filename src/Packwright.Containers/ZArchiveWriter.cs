using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ZstdSharp;

namespace Packwright.Containers;

/// <summary>
/// Managed writer for the ZArchive (.zar) container (https://github.com/Exzap/ZArchive). Data is only
/// ever appended: file payloads are cut into 64 KiB blocks that are zstd compressed, followed by the
/// offset records, name table, file tree and a footer carrying the SHA-256 of the whole archive.
/// </summary>
public sealed class ZArchiveWriter : IDisposable
{
    public const int BlockSize = 64 * 1024;
    public const int EntriesPerOffsetRecord = 16;
    public const uint FooterMagic = 0x169f52d6;
    public const uint FooterVersion1 = 0x61bf3a01;
    public const int FooterLength = 16 * 6 + 32 + 8 + 4 + 4;
    private const int CompressionLevel = 6;

    private sealed class Node(bool isFile, uint nameIndex)
    {
        public readonly bool IsFile = isFile;
        public readonly uint NameIndex = nameIndex;
        public readonly List<Node> Children = [];
        public readonly Dictionary<string, Node> ByName = new(StringComparer.Ordinal);
        public ulong FileOffset;
        public ulong FileSize;
        public uint NodeStartIndex;
    }

    private readonly Stream _output;
    private readonly bool _leaveOpen;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly Compressor _compressor = new(CompressionLevel);
    private readonly Node _root = new(false, uint.MaxValue);
    private readonly List<byte[]> _names = [];
    private readonly Dictionary<string, uint> _nameLookup = new(StringComparer.Ordinal);
    private readonly byte[] _block = new byte[BlockSize];
    private int _blockFill;
    private readonly List<(ulong BaseOffset, ushort[] Sizes)> _offsetRecords = [];
    private ulong _blockCount;
    private ulong _written;
    private ulong _inputOffset;
    private Node? _currentFile;
    private bool _finalized;

    public ZArchiveWriter(Stream output, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
        _leaveOpen = leaveOpen;
    }

    /// <summary>Total number of bytes written to the output so far.</summary>
    public long OutputLength => (long)_written;

    /// <summary>Creates a virtual file and makes it the target of <see cref="Append"/>.</summary>
    public void StartNewFile(string path)
    {
        _currentFile = null;
        string[] parts = SplitPath(path);
        if (parts.Length == 0) throw new ArgumentException("The file path is empty.", nameof(path));
        Node directory = _root;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            Node? next = Find(directory, parts[i]);
            if (next is null || next.IsFile)
                throw new DirectoryNotFoundException($"The directory does not exist in the archive: {parts[i]}");
            directory = next;
        }
        string name = parts[^1];
        if (Find(directory, name) is not null)
            throw new IOException($"The archive already contains: {path}");
        var node = new Node(true, CreateName(name)) { FileOffset = _inputOffset };
        Add(directory, name, node);
        _currentFile = node;
    }

    /// <summary>Creates a directory and any missing parents.</summary>
    public void MakeDirectory(string path)
    {
        Node current = _root;
        foreach (string part in SplitPath(path))
        {
            Node? next = Find(current, part);
            if (next is { IsFile: true })
                throw new IOException($"A file already exists at: {part}");
            if (next is null)
            {
                next = new Node(false, CreateName(part));
                Add(current, part, next);
            }
            current = next;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        ulong length = (ulong)data.Length;
        while (!data.IsEmpty)
        {
            int count = Math.Min(BlockSize - _blockFill, data.Length);
            data[..count].CopyTo(_block.AsSpan(_blockFill));
            _blockFill += count;
            data = data[count..];
            if (_blockFill == BlockSize)
            {
                StoreBlock();
                _blockFill = 0;
            }
        }
        if (_currentFile is not null) _currentFile.FileSize += length;
        _inputOffset += length;
    }

    public void Finish()
    {
        if (_finalized) throw new InvalidOperationException("The archive has already been finalized.");
        _finalized = true;
        _currentFile = null;
        if (_blockFill > 0)
        {
            Array.Clear(_block, _blockFill, BlockSize - _blockFill);
            StoreBlock();
            _blockFill = 0;
        }

        ulong compressedSize = _written;
        while (_written % 8 != 0) Write([0]);

        ulong recordsOffset = _written;
        Span<byte> record = stackalloc byte[8 + 2 * EntriesPerOffsetRecord];
        foreach ((ulong baseOffset, ushort[] sizes) in _offsetRecords)
        {
            BinaryPrimitives.WriteUInt64BigEndian(record, baseOffset);
            for (int i = 0; i < EntriesPerOffsetRecord; i++)
                BinaryPrimitives.WriteUInt16BigEndian(record[(8 + 2 * i)..], sizes[i]);
            Write(record);
        }
        ulong recordsSize = _written - recordsOffset;

        ulong namesOffset = _written;
        uint[] nameOffsets = new uint[_names.Count];
        uint nameTableOffset = 0;
        for (int i = 0; i < _names.Count; i++)
        {
            nameOffsets[i] = nameTableOffset;
            byte[] name = _names[i];
            int length = Math.Min(name.Length, 0x7FFF);
            if (length >= 0x80)
            {
                Write([(byte)((length & 0x7F) | 0x80), (byte)(length >> 7)]);
                nameTableOffset += 2;
            }
            else
            {
                Write([(byte)length]);
                nameTableOffset += 1;
            }
            Write(name.AsSpan(0, length));
            nameTableOffset += (uint)length;
        }
        ulong namesSize = _written - namesOffset;

        // Directory entries are numbered breadth first so every directory owns a contiguous range.
        var queue = new Queue<Node>();
        queue.Enqueue(_root);
        uint index = 1;
        while (queue.Count > 0)
        {
            Node node = queue.Dequeue();
            if (node.IsFile) { node.NodeStartIndex = uint.MaxValue; continue; }
            node.Children.Sort((a, b) => CompareNames(_names[(int)a.NameIndex], _names[(int)b.NameIndex]));
            node.NodeStartIndex = index;
            index += (uint)node.Children.Count;
            foreach (Node child in node.Children) queue.Enqueue(child);
        }

        ulong treeOffset = _written;
        Span<byte> entry = stackalloc byte[16];
        queue.Enqueue(_root);
        while (queue.Count > 0)
        {
            Node node = queue.Dequeue();
            uint nameField = node == _root ? 0x7FFFFFFF : nameOffsets[node.NameIndex] & 0x7FFFFFFF;
            if (node.IsFile) nameField |= 0x80000000;
            BinaryPrimitives.WriteUInt32BigEndian(entry, nameField);
            if (node.IsFile)
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry[4..], (uint)node.FileOffset);
                BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)node.FileSize);
                BinaryPrimitives.WriteUInt32BigEndian(entry[12..],
                    (uint)(((node.FileSize >> 16) & 0xFFFF0000) | ((node.FileOffset >> 32) & 0xFFFF)));
            }
            else
            {
                BinaryPrimitives.WriteUInt32BigEndian(entry[4..], node.NodeStartIndex);
                BinaryPrimitives.WriteUInt32BigEndian(entry[8..], (uint)node.Children.Count);
                BinaryPrimitives.WriteUInt32BigEndian(entry[12..], 0);
            }
            Write(entry);
            foreach (Node child in node.Children) queue.Enqueue(child);
        }
        ulong treeSize = _written - treeOffset;

        // The metadata sections are not used yet; they are empty and sit directly after the tree.
        ulong metaOffset = _written;
        ulong totalSize = _written + FooterLength;
        byte[] footer = new byte[FooterLength];
        WriteFooter(footer, compressedSize, recordsOffset, recordsSize, namesOffset, namesSize,
            treeOffset, treeSize, metaOffset, totalSize, null);
        _hash.AppendData(footer);
        byte[] digest = _hash.GetHashAndReset();
        WriteFooter(footer, compressedSize, recordsOffset, recordsSize, namesOffset, namesSize,
            treeOffset, treeSize, metaOffset, totalSize, digest);
        _output.Write(footer);
        _written += FooterLength;
        _output.Flush();
    }

    private static void WriteFooter(Span<byte> footer, ulong compressedSize, ulong recordsOffset, ulong recordsSize,
        ulong namesOffset, ulong namesSize, ulong treeOffset, ulong treeSize, ulong metaOffset, ulong totalSize,
        byte[]? digest)
    {
        footer.Clear();
        ulong[] sections =
        [
            0, compressedSize, recordsOffset, recordsSize, namesOffset, namesSize,
            treeOffset, treeSize, metaOffset, 0, metaOffset, 0
        ];
        for (int i = 0; i < sections.Length; i++)
            BinaryPrimitives.WriteUInt64BigEndian(footer[(i * 8)..], sections[i]);
        digest?.CopyTo(footer[96..]);
        BinaryPrimitives.WriteUInt64BigEndian(footer[128..], totalSize);
        BinaryPrimitives.WriteUInt32BigEndian(footer[136..], FooterVersion1);
        BinaryPrimitives.WriteUInt32BigEndian(footer[140..], FooterMagic);
    }

    private void StoreBlock()
    {
        ulong offset = _written;
        ReadOnlySpan<byte> compressed = _compressor.Wrap(_block);
        int stored = compressed.Length;
        if (stored >= BlockSize)
        {
            stored = BlockSize;
            Write(_block);
        }
        else
        {
            Write(compressed);
        }
        int slot = (int)(_blockCount % EntriesPerOffsetRecord);
        if (slot == 0) _offsetRecords.Add((offset, new ushort[EntriesPerOffsetRecord]));
        _offsetRecords[^1].Sizes[slot] = (ushort)(stored - 1);
        _blockCount++;
    }

    private void Write(ReadOnlySpan<byte> data)
    {
        _output.Write(data);
        _hash.AppendData(data);
        _written += (ulong)data.Length;
    }

    private uint CreateName(string name)
    {
        if (_nameLookup.TryGetValue(name, out uint existing)) return existing;
        uint index = (uint)_names.Count;
        _names.Add(Encoding.UTF8.GetBytes(name));
        _nameLookup.Add(name, index);
        return index;
    }

    private static void Add(Node parent, string name, Node child)
    {
        parent.Children.Add(child);
        parent.ByName.Add(LookupKey(name), child);
    }

    private static Node? Find(Node parent, string name) =>
        parent.ByName.GetValueOrDefault(LookupKey(name));

    // Names are matched case-insensitively over ASCII only, like the reference implementation.
    private static string LookupKey(string name) =>
        string.Create(name.Length, name, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = source[i] is >= 'A' and <= 'Z' ? (char)(source[i] + 32) : source[i];
        });

    private static string[] SplitPath(string path) =>
        path.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

    // The format compares names case-insensitively over ASCII only; shorter names sort first on a tie.
    private static int CompareNames(byte[] a, byte[] b)
    {
        int length = Math.Min(a.Length, b.Length);
        for (int i = 0; i < length; i++)
        {
            int x = Lower(a[i]), y = Lower(b[i]);
            if (x != y) return x - y;
        }
        return a.Length - b.Length;
    }

    private static int Lower(byte value) => value is >= (byte)'A' and <= (byte)'Z' ? value + 32 : value;

    public void Dispose()
    {
        _compressor.Dispose();
        _hash.Dispose();
        if (!_leaveOpen) _output.Dispose();
    }
}
