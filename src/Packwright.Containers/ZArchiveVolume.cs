using System.Buffers.Binary;
using System.Text;
using ZstdSharp;

namespace Packwright.Containers;

/// <summary>One file or directory inside a ZArchive.</summary>
public sealed record ZArchiveEntry(string Path, string Name, bool IsDirectory, long Size);

/// <summary>
/// Read-only access to a ZArchive (.zar): the file tree is loaded once, and file data is decompressed
/// block by block on demand, so a multi-gigabyte archive can be listed and browsed without unpacking it.
/// </summary>
public sealed class ZArchiveVolume : IDisposable
{
    private sealed record Node(string Path, string Name, bool IsDirectory, ulong Offset, ulong Size);

    private readonly FileStream _file;
    private readonly long _dataOffset;
    private readonly ulong _dataSize;
    private readonly byte[] _records;
    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ZArchiveEntry> _entries = [];
    private readonly Decompressor _decompressor = new();
    private readonly object _gate = new();
    private readonly byte[] _blockBuffer = new byte[ZArchiveWriter.BlockSize];
    private readonly byte[] _compressedBuffer = new byte[ZArchiveWriter.BlockSize];
    private long _cachedBlock = -1;

    public ZArchiveVolume(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _file = new FileStream(System.IO.Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            1024 * 1024, FileOptions.RandomAccess);
        try
        {
            long length = _file.Length;
            if (length < ZArchiveWriter.FooterLength)
                throw new InvalidDataException("The file is too small to be a ZArchive.");

            byte[] footer = new byte[ZArchiveWriter.FooterLength];
            _file.Position = length - footer.Length;
            _file.ReadExactly(footer);
            if (BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(140)) != ZArchiveWriter.FooterMagic ||
                BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(136)) != ZArchiveWriter.FooterVersion1)
                throw new InvalidDataException("This is not a ZArchive file.");
            if (BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(128)) != (ulong)length)
                throw new InvalidDataException("The ZArchive size does not match its footer.");

            (ulong Offset, ulong Size) Section(int index) => (
                BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(index * 16)),
                BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(index * 16 + 8)));
            var data = Section(0);
            var records = Section(1);
            var names = Section(2);
            var tree = Section(3);
            ulong limit = (ulong)length - ZArchiveWriter.FooterLength;
            foreach ((ulong offset, ulong size) in new[] { data, records, names, tree })
                if (offset + size > limit) throw new InvalidDataException("A ZArchive section lies outside the file.");
            if (records.Size % 40 != 0) throw new InvalidDataException("The ZArchive offset records are malformed.");
            if (tree.Size < 16 || tree.Size % 16 != 0) throw new InvalidDataException("The ZArchive file tree is malformed.");

            _dataOffset = (long)data.Offset;
            _dataSize = data.Size;
            _records = ReadSection(records);
            byte[] nameTable = ReadSection(names);
            byte[] fileTree = ReadSection(tree);
            LoadTree(fileTree, nameTable);
        }
        catch
        {
            _file.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ZArchiveEntry> Entries => _entries;

    /// <summary>Total uncompressed size of all files.</summary>
    public long TotalFileBytes => _entries.Where(entry => !entry.IsDirectory).Sum(entry => entry.Size);

    private byte[] ReadSection((ulong Offset, ulong Size) section)
    {
        byte[] bytes = new byte[section.Size];
        _file.Position = (long)section.Offset;
        _file.ReadExactly(bytes);
        return bytes;
    }

    private void LoadTree(byte[] tree, byte[] names)
    {
        uint count = (uint)(tree.Length / 16);
        (uint NameField, uint A, uint B, uint C) Read(uint index)
        {
            ReadOnlySpan<byte> entry = tree.AsSpan((int)index * 16, 16);
            return (BinaryPrimitives.ReadUInt32BigEndian(entry), BinaryPrimitives.ReadUInt32BigEndian(entry[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(entry[8..]), BinaryPrimitives.ReadUInt32BigEndian(entry[12..]));
        }

        string NameAt(uint offset)
        {
            if (offset >= names.Length) throw new InvalidDataException("A ZArchive name lies outside the name table.");
            int length = names[offset];
            int start = (int)offset + 1;
            if ((length & 0x80) != 0)
            {
                if (start >= names.Length) throw new InvalidDataException("A ZArchive name is truncated.");
                length = (length & 0x7F) | (names[start] << 7);
                start++;
            }
            if (start + length > names.Length) throw new InvalidDataException("A ZArchive name is truncated.");
            return Encoding.UTF8.GetString(names, start, length);
        }

        var stack = new Stack<(uint Index, string Path)>();
        stack.Push((0, string.Empty));
        int guard = 0;
        while (stack.Count > 0)
        {
            (uint index, string parent) = stack.Pop();
            if (++guard > count + 1) throw new InvalidDataException("The ZArchive file tree contains a loop.");
            (uint nameField, uint a, uint b, uint c) = Read(index);
            bool isFile = (nameField & 0x80000000) != 0;
            string name = index == 0 ? string.Empty : NameAt(nameField & 0x7FFFFFFF);
            string path = index == 0 ? string.Empty : parent.Length == 0 ? name : parent + "/" + name;
            if (isFile)
            {
                ulong offset = a | ((ulong)(c & 0xFFFF) << 32);
                ulong size = b | ((ulong)(c & 0xFFFF0000) << 16);
                _nodes[path] = new Node(path, name, false, offset, size);
                _entries.Add(new ZArchiveEntry(path, name, false, (long)size));
            }
            else
            {
                if ((ulong)a + b > count) throw new InvalidDataException("A ZArchive directory range lies outside the file tree.");
                if (index != 0)
                {
                    _nodes[path] = new Node(path, name, true, 0, 0);
                    _entries.Add(new ZArchiveEntry(path, name, true, 0));
                }
                for (uint child = a; child < a + b; child++) stack.Push((child, path));
            }
        }
        _entries.Sort((x, y) => string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase));
    }

    public bool FileExists(string path) =>
        _nodes.TryGetValue(Normalize(path), out Node? node) && !node.IsDirectory;

    public long GetSize(string path) => _nodes.TryGetValue(Normalize(path), out Node? node) && !node.IsDirectory
        ? (long)node.Size
        : throw new FileNotFoundException("The file was not found in the archive.", path);

    public Stream OpenFile(string path)
    {
        if (!_nodes.TryGetValue(Normalize(path), out Node? node) || node.IsDirectory)
            throw new FileNotFoundException("The file was not found in the archive.", path);
        return new FileStreamView(this, node.Offset, node.Size);
    }

    public byte[] ReadAllBytes(string path, int maximum = 64 * 1024 * 1024)
    {
        long size = GetSize(path);
        if (size > maximum) throw new InvalidDataException("The file is larger than the allowed read size.");
        using Stream stream = OpenFile(path);
        byte[] bytes = new byte[size];
        stream.ReadExactly(bytes);
        return bytes;
    }

    public string ReadAllText(string path) => Encoding.UTF8.GetString(ReadAllBytes(path, 16 * 1024 * 1024));

    private static string Normalize(string path) => path.Replace('\\', '/').Trim('/');

    /// <summary>Copies bytes from the uncompressed address space of the archive.</summary>
    private int ReadUncompressed(ulong position, Span<byte> destination)
    {
        int total = 0;
        lock (_gate)
        {
            while (total < destination.Length)
            {
                long block = (long)((position + (ulong)total) / ZArchiveWriter.BlockSize);
                int within = (int)((position + (ulong)total) % ZArchiveWriter.BlockSize);
                LoadBlock(block);
                int count = Math.Min(destination.Length - total, ZArchiveWriter.BlockSize - within);
                _blockBuffer.AsSpan(within, count).CopyTo(destination[total..]);
                total += count;
            }
        }
        return total;
    }

    private void LoadBlock(long block)
    {
        if (block == _cachedBlock) return;
        long record = block / ZArchiveWriter.EntriesPerOffsetRecord;
        int slot = (int)(block % ZArchiveWriter.EntriesPerOffsetRecord);
        if ((record + 1) * 40 > _records.Length) throw new InvalidDataException("A ZArchive block index is out of range.");
        ReadOnlySpan<byte> entry = _records.AsSpan((int)record * 40, 40);
        ulong offset = BinaryPrimitives.ReadUInt64BigEndian(entry);
        for (int i = 0; i < slot; i++)
            offset += (ulong)BinaryPrimitives.ReadUInt16BigEndian(entry[(8 + 2 * i)..]) + 1;
        int size = BinaryPrimitives.ReadUInt16BigEndian(entry[(8 + 2 * slot)..]) + 1;
        if (offset + (ulong)size > _dataSize) throw new InvalidDataException("A ZArchive block lies outside the data section.");
        _file.Position = _dataOffset + (long)offset;
        if (size == ZArchiveWriter.BlockSize)
        {
            _file.ReadExactly(_blockBuffer, 0, size);
        }
        else
        {
            _file.ReadExactly(_compressedBuffer, 0, size);
            int decoded = _decompressor.Unwrap(_compressedBuffer.AsSpan(0, size), _blockBuffer);
            if (decoded != ZArchiveWriter.BlockSize)
                throw new InvalidDataException($"ZArchive block {block} decodes to the wrong size.");
        }
        _cachedBlock = block;
    }

    /// <summary>A seekable view over one file's bytes.</summary>
    private sealed class FileStreamView(ZArchiveVolume volume, ulong start, ulong length) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => (long)length;

        public override long Position
        {
            get => _position;
            set => _position = Math.Clamp(value, 0, (long)length);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            long remaining = (long)length - _position;
            int wanted = (int)Math.Min(buffer.Length, Math.Max(0, remaining));
            if (wanted == 0) return 0;
            int read = volume.ReadUncompressed(start + (ulong)_position, buffer[..wanted]);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            Position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => (long)length + offset
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    public void Dispose()
    {
        _decompressor.Dispose();
        _file.Dispose();
    }
}
