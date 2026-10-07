// Packwright.Pkg - reader for PS5 packages (finalized "FIH" images).
//
// A package is: a 0x10000-byte FIH header, an outer PFS image holding the inner image (pfs_image.dat) and its
// layout (naps_pkg_layout.dat), then the "CNT" metadata container. The container header, the entry table
// and the outer image are plain parsing; the inner image is expanded on demand by InnerMount.
// Format knowledge is based on the LibProsperoPKG documentation and source by SvenGDK (GPL-3.0-or-later).
#nullable enable
using System.Buffers.Binary;
using System.Text;
using LibProsperoPkg.PFS;
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Reader;

/// <summary>An entry of the package's metadata container (param.json, icons, trophies and so on).</summary>
public sealed record PkgEntry(uint Id, string Name, uint Flags1, uint Flags2, long Offset, long Size)
{
    /// <summary>True when the entry is stored encrypted (the 0x80000000 flag) and needs the passcode to read.</summary>
    public bool IsEncrypted => (Flags1 & 0x80000000u) != 0;

    public uint NameTableOffset { get; init; }
}

/// <summary>Raised when a package uses a layout this reader does not support (for example a retail image).</summary>
public sealed class PkgNotSupportedException(string message) : NotSupportedException(message);

/// <summary>Progress of an extraction.</summary>
public readonly record struct PkgExtractProgress(long CompletedBytes, long TotalBytes, string CurrentPath);

public sealed partial class PkgPackage : IDisposable
{
    private const int FihHeaderSize = 0x100;
    private const int CntHeaderSize = 0x5A0;
    private const int EntryRecordSize = 0x20;
    private const uint EntryNamesId = 0x0200;
    private const string PlainSeed = "PPRPLAIN-NOAUTH!";
    private static ReadOnlySpan<byte> FihMagic => [0x7F, (byte)(char)70, (byte)(char)73, (byte)(char)72];
    private static ReadOnlySpan<byte> LihMagic => [0x7F, (byte)(char)76, (byte)(char)73, (byte)(char)72];
    private static ReadOnlySpan<byte> CntMagic => [0x7F, (byte)(char)67, (byte)(char)78, (byte)(char)84];

    private readonly FileStream _file;
    private readonly long _cntOffset;
    private InnerMount? _mount;
    private IReadOnlyList<PkgFile>? _files;
    private IReadOnlyList<string>? _directories;
    private ProsperoPfsReader.File? _innerFile;
    private ProsperoPfsReader.File? _layoutFile;
    private ProsperoPfsReader? _outer;
    private OuterXtsReader? _xtsReader;

    private PkgPackage(FileStream file, string path)
    {
        _file = file;
        Path = path;
        FileSize = file.Length;

        byte[] fih = new byte[FihHeaderSize];
        if (FileSize < FihHeaderSize) throw new InvalidDataException("The file is too small to be a PS5 package.");
        file.ReadExactly(fih);
        if (fih.AsSpan(0, 4).SequenceEqual(CntMagic))
        {
            // A bare metadata container: entries only, no inner image.
            IsMetadataOnly = true;
            _cntOffset = 0;
        }
        else if (fih.AsSpan(0, 4).SequenceEqual(FihMagic))
        {
            IsRetail = fih[5] == 0x80;
            SignedByte = fih[5];
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(fih.AsSpan(6));
            PfsOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x10));
            PfsSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x18));
            SuperblockOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x20));
            _cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0x58));
            if (_cntOffset <= 0 || _cntOffset + CntHeaderSize > FileSize)
                throw new InvalidDataException("The package has no readable metadata container.");
        }
        else if (fih.AsSpan(0, 4).SequenceEqual(LihMagic))
        {
            // A patch image: a small leading header, then the finalized image, the container and the install data.
            // Only its metadata is described; the filesystem inside a patch is not read.
            IsMetadataOnly = true;
            IsPatchImage = true;
            byte[] lih = new byte[80];
            file.Position = 0;
            file.ReadExactly(lih);
            long fihOffset = BinaryPrimitives.ReadUInt32LittleEndian(lih.AsSpan(16));
            long fihSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(40));
            long cntOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(48));
            long cntSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(56));
            long siOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(64));
            long siSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(72));
            long lihSize = (long)BinaryPrimitives.ReadUInt64LittleEndian(lih.AsSpan(32));
            if (fihOffset < 0 || fihSize < 0 || cntOffset <= 0 || cntSize < 0 || siOffset < 0 || siSize < 0
                || fihOffset + fihSize > FileSize || cntOffset + CntHeaderSize > FileSize || siOffset + siSize > FileSize)
                throw new InvalidDataException("The patch image has an unreadable header.");
            byte[] inner = new byte[8];
            file.Position = fihOffset;
            file.ReadExactly(inner);
            if (!inner.AsSpan(0, 4).SequenceEqual(FihMagic)) throw new InvalidDataException("The patch image has no finalized image header.");
            SignedByte = inner[5];
            FormatVersion = BinaryPrimitives.ReadUInt16LittleEndian(inner.AsSpan(6));
            _cntOffset = cntOffset;
            long pfsStart = fihOffset + fihSize;
            _patchSegments =
            [
                ("LIH", 0, lihSize), ("FIH", fihOffset, fihSize), ("PFS", pfsStart, Math.Max(0, cntOffset - pfsStart)),
                ("SC", cntOffset, cntSize), ("SI", siOffset, siSize),
            ];
        }
        else
            throw new InvalidDataException("This is not a PS5 package (unknown header).");

        byte[] header = new byte[CntHeaderSize];
        file.Position = _cntOffset;
        file.ReadExactly(header);
        if (!header.AsSpan(0, 4).SequenceEqual(CntMagic))
            throw new InvalidDataException("The package metadata container has a bad signature.");

        ContentId = Encoding.ASCII.GetString(header, 0x40, 36).TrimEnd('\0');
        HeaderFlags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x04));
        ScEntryCount = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(0x14));
        BodyOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x20));
        BodySize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x28));
        ContentFlags = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x78));
        DrmType = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x70));
        ContentType = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x74));
        Entries = ReadEntries(header);
    }

    public string Path { get; }
    public long FileSize { get; }
    public bool IsRetail { get; }
    /// <summary>True for a bare metadata (CNT) container, which has entries but no inner image.</summary>
    public bool IsMetadataOnly { get; }
    /// <summary>True for a patch image (a package with a leading LIH header). Only its metadata is read.</summary>
    public bool IsPatchImage { get; }
    private readonly IReadOnlyList<(string Name, long Offset, long Size)>? _patchSegments;
    public ushort FormatVersion { get; private init; }
    public string ContentId { get; }
    public long CntOffset => _cntOffset;
    public byte SignedByte { get; private init; }
    public uint HeaderFlags { get; }
    public ushort ScEntryCount { get; }
    public ulong BodyOffset { get; }
    public ulong BodySize { get; }
    public uint ContentFlags { get; }

    /// <summary>The four regions of a finalized package: FIH header, PFS image, SC metadata container, SI install data.</summary>
    public IReadOnlyList<(string Name, long Offset, long Size)> Segments
    {
        get
        {
            if (_patchSegments is not null) return _patchSegments;
            long scSize = (long)(BodyOffset + BodySize);
            long siStart = _cntOffset + scSize;
            return
            [
                ("FIH", 0, 0x10000),
                ("PFS", PfsOffset, PfsSize),
                ("SC", _cntOffset, scSize),
                ("SI", siStart, Math.Max(0, FileSize - siStart)),
            ];
        }
    }
    public uint DrmType { get; }
    public uint ContentType { get; }
    public long PfsOffset { get; }
    public long PfsSize { get; }
    public long SuperblockOffset { get; }
    public IReadOnlyList<PkgEntry> Entries { get; }

    /// <summary>
    /// Opens a package. The passcode is only needed for packages that were built with one (it unlocks their
    /// protected metadata and encrypted outer image); packages downloaded as plain debug images use all zeros.
    /// </summary>
    public static PkgPackage Open(string path, string? passcode = null)
    {
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            64 * 1024, FileOptions.RandomAccess);
        try
        {
            var package = new PkgPackage(file, System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(passcode)) package.Passcode = passcode;
            return package;
        }
        catch { file.Dispose(); throw; }
    }

    /// <summary>The passcode used for protected entries and an encrypted outer image.</summary>
    public string Passcode { get; private set; } = PkgEntryCipher.DefaultPasscode;

    private IReadOnlyList<PkgEntry> ReadEntries(byte[] header)
    {
        uint count = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x10));
        uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x18));
        long room = (FileSize - (_cntOffset + tableOffset)) / EntryRecordSize;
        if (count > 0x10000 || count > room) throw new InvalidDataException("The package entry table is malformed.");

        byte[] table = new byte[count * EntryRecordSize];
        _file.Position = _cntOffset + tableOffset;
        _file.ReadExactly(table);

        var raw = new List<(uint Id, uint NameOffset, uint F1, uint F2, uint Off, uint Size)>((int)count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> r = table.AsSpan(i * EntryRecordSize, EntryRecordSize);
            raw.Add((BinaryPrimitives.ReadUInt32BigEndian(r), BinaryPrimitives.ReadUInt32BigEndian(r[4..]),
                BinaryPrimitives.ReadUInt32BigEndian(r[8..]), BinaryPrimitives.ReadUInt32BigEndian(r[12..]),
                BinaryPrimitives.ReadUInt32BigEndian(r[16..]), BinaryPrimitives.ReadUInt32BigEndian(r[20..])));
        }

        byte[] names = [];
        foreach (var e in raw)
            if (e.Id == EntryNamesId && e.Size > 0 && e.Size < 16 * 1024 * 1024)
            {
                names = new byte[e.Size];
                _file.Position = _cntOffset + e.Off;
                _file.ReadExactly(names);
            }

        var entries = new List<PkgEntry>(raw.Count);
        foreach (var e in raw)
        {
            string name = string.Empty;
            if (e.NameOffset > 0 && e.NameOffset < names.Length)
            {
                int end = Array.IndexOf(names, (byte)0, (int)e.NameOffset);
                if (end < 0) end = names.Length;
                name = Encoding.ASCII.GetString(names, (int)e.NameOffset, end - (int)e.NameOffset);
            }
            entries.Add(new PkgEntry(e.Id, name, e.F1, e.F2, e.Off, e.Size) { NameTableOffset = e.NameOffset });
        }
        return entries;
    }

    /// <summary>Reads a metadata entry that is stored in the clear.</summary>
    public byte[] ReadEntry(PkgEntry entry)
    {
        if (entry.IsEncrypted)
            throw new PkgNotSupportedException($"Entry 0x{entry.Id:X4} is stored encrypted.");
        if (entry.Size > int.MaxValue) throw new InvalidDataException("The entry is too large to read into memory.");
        byte[] data = new byte[entry.Size];
        lock (_file)
        {
            _file.Position = _cntOffset + entry.Offset;
            _file.ReadExactly(data);
        }
        return data;
    }

    /// <summary>
    /// Reads a metadata entry, decrypting it with <paramref name="passcode"/> when it is protected. Debug
    /// packages use the passcode of all zeros unless the publisher chose another.
    /// </summary>
    public byte[] ReadEntry(PkgEntry entry, string passcode)
    {
        if (!entry.IsEncrypted) return ReadEntry(entry);
        long stored = (entry.Size + 15) & ~15L;
        if (stored > int.MaxValue) throw new InvalidDataException("The entry is too large to read into memory.");
        byte[] data = new byte[stored];
        lock (_file)
        {
            _file.Position = _cntOffset + entry.Offset;
            _file.ReadExactly(data);
        }
        // Packages built with the publisher profile carry an entry-key table (0x0010) of at least 0xB80 bytes.
        bool publisher = Entries.Any(e => e.Id == 0x0010 && e.Size >= 2944);
        return PkgEntryCipher.Decrypt(data, entry, ContentId, passcode, publisher);
    }

    /// <summary>The files of the application filesystem. Opening the list decodes only the filesystem metadata.</summary>
    public IReadOnlyList<PkgFile> Files
    {
        get
        {
            EnsureInner();
            return _files!;
        }
    }

    /// <summary>Every directory below the user root, including empty ones.</summary>
    public IReadOnlyList<string> Directories
    {
        get
        {
            EnsureInner();
            return _directories!;
        }
    }

    /// <summary>Opens one file as a read-only, seekable stream. Only the blocks that are read are decoded.</summary>
    internal long InnerImageSize { get { EnsureInner(); return _mount!.MountSize; } }

    internal (Stream Mount, long Superblock) DebugMount() { EnsureInner(); return (_mount!, _mount!.SuperblockOffset); }

    public Stream OpenFile(PkgFile file)
    {
        EnsureInner();
        return new BoundedStream(_mount!, file.LogicalOffset, file.Size);
    }

    private void EnsureInner()
    {
        if (IsMetadataOnly) { _files ??= []; _directories ??= []; return; }
        if (_mount is not null) return;
        lock (_file)
        {
            if (_mount is not null) return;
            if (IsRetail)
                throw new PkgNotSupportedException("This is a retail package. Its contents are encrypted with a key that is not available.");

            OpenOuter();
            byte[] naps = new byte[_layoutFile!.size];
            _layoutFile.GetView().Read(0, naps, 0, naps.Length);
            var onDisk = new ReaderStream(_innerFile!.GetView(), _innerFile.size);
            InnerMount mount = new(onDisk, naps);
            (_files, _directories) = InnerFileTree.ReadAll(mount, mount.SuperblockOffset);
            _mount = mount;
        }
    }

    private void OpenOuter()
    {
        long sbRelative = SuperblockOffset - PfsOffset;
        // The outer image is read in place. Its header carries a fixed marker seed in these packages and
        // its blocks are stored plain, so no key is needed.
        var reader = new FileReader(_file, PfsOffset);
        byte[] head = new byte[0x400];
        reader.Read(sbRelative, head, 0, head.Length);
        ProsperoPfsHeader sb = ProsperoPfsHeader.ReadFromStream(new MemoryStream(head));
        if (sb.Seed is null) throw new InvalidDataException("The outer image has no seed.");
        if (Encoding.ASCII.GetString(sb.Seed) != PlainSeed)
        {
            OpenEncryptedOuter(sb.Seed, sbRelative);
            return;
        }

        _outer = new ProsperoPfsReader(reader, 0, null, null, null, sbRelative, skipDecryption: true);
        FindInnerFiles();
    }

    private void FindInnerFiles()
    {
        _innerFile = _outer!.GetFile("pfs_image.dat") ?? throw new InvalidDataException("The package has no inner image (pfs_image.dat).");
        _layoutFile = _outer.GetFile("naps_pkg_layout.dat") ?? throw new InvalidDataException("The package has no layout file (naps_pkg_layout.dat).");
    }

    // Packages built with a passcode encrypt the outer image block by block. The key is derived from the content id
    // and the passcode; the hash (SHA-256 or SHA3-256) and key schedule vary between builders, so each combination is
    // tried until the filesystem metadata decrypts into a coherent directory tree.
    private void OpenEncryptedOuter(byte[] seed, long sbRelative)
    {
        const int block = OuterXtsReader.BlockSize;
        if (PfsSize % block != 0 || sbRelative <= 0 || sbRelative % block != 0)
            throw new InvalidDataException("The outer image has an unexpected layout.");
        int total = (int)(PfsSize / block);
        int superblock = (int)(sbRelative / block);
        if (superblock >= total) throw new InvalidDataException("The outer superblock lies outside the outer image.");
        if (Passcode.Length != 32) throw new PkgNotSupportedException("The passcode must be 32 characters.");
        if (ContentId.Length != 36) throw new InvalidDataException("The package has no usable content id to derive the key from.");

        foreach (bool sha3 in new[] { true, false })
        {
            foreach (bool newCrypt in new[] { true, false })
            {
                byte[] ekpfs = Crypto.ComputeKeys(ContentId, Passcode, 1, sha3);
                var (tweak, data) = Crypto.PfsGenEncKey(ekpfs, seed, newCrypt);

                // First read the filesystem metadata, treating every block as metadata except the superblock.
                var kinds = new OuterBlockKind[total];
                Array.Fill(kinds, OuterBlockKind.Signed);
                kinds[superblock] = OuterBlockKind.Plain;
                var reader = new OuterXtsReader(_file, PfsOffset, tweak, data, kinds);
                try
                {
                    var probe = new ProsperoPfsReader(reader, 0, null, null, null, sbRelative, skipDecryption: true);
                    List<ProsperoPfsReader.File> all = probe.GetAllFiles().ToList();
                    ProsperoPfsReader.File image = probe.GetFile("pfs_image.dat")
                        ?? throw new InvalidDataException("no inner image");

                    // The inner image's data blocks run from its start to the next file or the superblock; they use
                    // the plain sector numbering.
                    long start = image.offset / block, end = superblock;
                    foreach (ProsperoPfsReader.File f in all)
                    {
                        long first = f.offset / block;
                        if (first > start && first < end) end = first;
                    }
                    for (long b = start; b < end && b < total; b++)
                        if (b != superblock) kinds[b] = OuterBlockKind.Data;
                    reader.ClearCache();

                    _outer = new ProsperoPfsReader(reader, 0, null, null, null, sbRelative, skipDecryption: true);
                    FindInnerFiles();
                    _xtsReader = reader;
                    return;
                }
                catch (Exception)
                {
                    // Wrong key or key schedule: the metadata did not decrypt. Try the next combination.
                    reader.Dispose();
                    _outer = null;
                }
            }
        }
        throw new InvalidDataException(
            "The outer image could not be decrypted with this passcode. Check that the passcode is the one the package was built with.");
    }

    /// <summary>
    /// Writes every file to <paramref name="destination"/>, which must not exist yet. With <paramref name="includeMetadata"/> the package's
    /// metadata entries (parameters, artwork, trophies and so on) are written too, under sce_sys, so the result is a complete title folder.
    /// </summary>
    public void ExtractTo(string destination, IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellationToken = default,
        bool includeMetadata = false)
    {
        string root = System.IO.Path.GetFullPath(destination);
        if (Directory.Exists(root)) throw new IOException("The extraction destination already exists: " + root);
        IReadOnlyList<PkgFile> files = Files;
        long total = files.Sum(f => f.Size), done = 0;
        string staging = root + ".extracting." + Guid.NewGuid().ToString("N");
        try
        {
            Directory.CreateDirectory(staging);
            byte[] buffer = new byte[1024 * 1024];
            foreach (PkgFile file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string target = ResolveContained(staging, file.Path);
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024);
                using Stream input = OpenFile(file);
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    output.Write(buffer, 0, read);
                    done += read;
                    progress?.Report(new PkgExtractProgress(done, total, file.Path));
                }
                if (output.Length != file.Size) throw new EndOfStreamException($"'{file.Path}' ended early.");
            }
            if (includeMetadata) WriteMetadataEntries(staging, cancellationToken);
            Directory.Move(staging, root);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch (IOException) { }
            throw;
        }
    }

    // The system entries are bookkeeping of the container itself; everything else is content of the title.
    private static readonly HashSet<uint> SystemEntryIds = [0x0001, 0x0010, 0x0020, 0x0080, 0x0100, 0x0200, 0x040A];

    private void WriteMetadataEntries(string staging, CancellationToken cancellationToken)
    {
        foreach (PkgEntry entry in Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (SystemEntryIds.Contains(entry.Id)) continue;
            string name = Writer.PkgEntryTable.FileNameOf(entry.Id) ?? $"entry_0x{entry.Id:X4}.bin";
            string target = ResolveContained(staging, "sce_sys/" + name);
            if (File.Exists(target)) continue;
            byte[] data = entry.IsEncrypted ? ReadEntry(entry, Passcode) : ReadEntry(entry);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, data);
        }
    }
    private static string ResolveContained(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || System.IO.Path.IsPathRooted(relative))
            throw new InvalidDataException("A package path is unsafe.");
        string rootFull = System.IO.Path.GetFullPath(root) + System.IO.Path.DirectorySeparatorChar;
        string target = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, relative));
        if (!target.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("A package path escapes the extraction folder.");
        return target;
    }

    public void Dispose()
    {
        _mount?.Dispose();
        _xtsReader?.Dispose();
        _file.Dispose();
    }

    // ---- small adapters ------------------------------------------------------------------------------

    private sealed class FileReader(FileStream file, long baseOffset) : IMemoryReader
    {
        public void Read(long pos, byte[] buf, int offset, int count)
        {
            lock (file)
            {
                file.Position = baseOffset + pos;
                file.ReadExactly(buf, offset, count);
            }
        }
        public void Dispose() { }
    }

    private sealed class ReaderStream(IMemoryReader reader, long length) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = (int)Math.Min(count, Math.Max(0, length - _pos));
            if (take > 0) reader.Read(_pos, buffer, offset, take);
            _pos += take;
            return take;
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => length + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    // A window over the shared image; disposing it must not close the image.
    private sealed class BoundedStream(Stream inner, long start, long length) : Stream
    {
        private long _pos;
        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => length;
        public override long Position { get => _pos; set => _pos = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            int take = (int)Math.Min(count, Math.Max(0, length - _pos));
            if (take <= 0) return 0;
            lock (inner)
            {
                inner.Position = start + _pos;
                int read = inner.Read(buffer, offset, take);
                _pos += read;
                return read;
            }
        }
        public override long Seek(long offset, SeekOrigin origin) =>
            _pos = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => _pos + offset, _ => length + offset };
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
