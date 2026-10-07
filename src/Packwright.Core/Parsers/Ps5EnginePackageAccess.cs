using Packwright.Core.Builders;
using Packwright.Pkg.Reader;

namespace Packwright.Core.Parsers;

/// <summary>
/// Adapts the Packwright package reader (<c>Packwright.Pkg</c>) to the package model the rest of the app
/// uses. Opening a package parses only its header and metadata; the inner filesystem is decoded on first use
/// and only the blocks that are read are expanded, so scanning a library stays cheap.
/// </summary>
internal sealed class Ps5EnginePackageAccess : IDisposable
{
    private readonly object _gate = new();
    private readonly string _passcode;
    private readonly IReadOnlyList<PkgEntry> _entries;
    private PkgPackage? _package;
    private IReadOnlyList<PkgFile>? _files;
    private string? _decodeError;
    private bool _decodeUnsupported;
    private bool _decodeAttempted;
    private bool _disposed;
    private int _leases;
    private bool _releasePending;

    private Ps5EnginePackageAccess(PkgPackage package, string? passcode)
    {
        PackagePath = package.Path;
        _package = package;
        _passcode = string.IsNullOrEmpty(passcode) ? Ps5DebugPackageCredentials.DefaultPasscode : passcode;
        _entries = package.Entries;
        HasInnerImage = !package.IsMetadataOnly && package.PfsSize > 0;
        IsDebug = !package.IsRetail;
        PfsOffset = package.PfsOffset;
        PfsSize = package.PfsSize;
    }

    public string PackagePath { get; }
    public bool HasInnerImage { get; }
    public bool IsDebug { get; }
    public long PfsOffset { get; }
    public long PfsSize { get; }

    /// <summary>Opens a package for reading. Throws <see cref="InvalidDataException"/> when it is not a PS5 package.</summary>
    public static Ps5EnginePackageAccess Open(string packagePath, string? passcode) =>
        new(PkgPackage.Open(Path.GetFullPath(packagePath), passcode), passcode);

    /// <summary>Wraps a package that was already opened (so a scan does not parse it twice).</summary>
    internal static Ps5EnginePackageAccess FromPackage(PkgPackage package, string? passcode) => new(package, passcode);

    /// <summary>The files of the inner filesystem. Decoding happens on first use.</summary>
    public IEnumerable<PkgFile> Files
    {
        get { EnsureDecoded(); return _files!; }
    }

    public string? DecodeError
    {
        get { EnsureDecoded(); return _decodeError; }
    }

    /// <summary>
    /// True when <see cref="DecodeError"/> is an unsupported format or layout rather than corruption, so the
    /// UI can report the real cause instead of blaming the passcode.
    /// </summary>
    public bool DecodeUnsupported
    {
        get { EnsureDecoded(); return _decodeUnsupported; }
    }

    private void EnsureDecoded()
    {
        lock (_gate)
        {
            if (_decodeAttempted) return;
            _decodeAttempted = true;
            // A package with no inner image, or a retail one (encrypted with a key we do not have), simply
            // has no browsable inner files; that is not an error.
            if (!HasInnerImage || !IsDebug) { _files = []; return; }
            try
            {
                _files = Acquire().Files;
            }
            catch (PkgNotSupportedException ex)
            {
                _files = [];
                _decodeError = ex.Message;
                _decodeUnsupported = true;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException
                                           or ArgumentException or OverflowException or InvalidOperationException)
            {
                _files = [];
                _decodeError = ex.Message;
            }
        }
    }

    // The package handle is opened on demand so it can be released while the app is idle.
    private PkgPackage Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _package ??= PkgPackage.Open(PackagePath, _passcode);
        }
    }

    // ---- metadata entries ----------------------------------------------------------------------------

    public byte[] ReadCntEntry(uint id, long? length = null)
    {
        PkgEntry entry = _entries.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new InvalidDataException($"CNT entry 0x{id:X4} is not present.");
        byte[] data;
        try { data = Acquire().ReadEntry(entry, _passcode); }
        catch (PkgNotSupportedException ex) { throw new InvalidDataException(ex.Message, ex); }
        return length is { } limit && limit >= 0 && limit < data.Length ? data[..(int)limit] : data;
    }

    /// <summary>
    /// Metadata entries exposed under the paths they have inside the inner image, so the metadata, artwork,
    /// trophy and activity readers work even when the inner image cannot be decoded.
    /// </summary>
    private static readonly (uint Id, string Path)[] ContentEntries =
    [
        (0x0402, "sce_sys/nptitle.dat"),
        (0x0403, "sce_sys/npbind.dat"),
        (0x040A, "sce_sys/imagedigs.dat"),
        (0x1000, "sce_sys/param.sfo"),
        (0x1001, "sce_sys/playgo-chunk.dat"),
        (0x1200, "sce_sys/icon0.png"),
        (0x1220, "sce_sys/pic0.png"),
        (0x1240, "sce_sys/snd0.at9"),
        (0x1280, "sce_sys/icon0.dds"),
        (0x12A0, "sce_sys/pic0.dds"),
        (0x12C0, "sce_sys/pic1.dds"),
        (0x1480, "sce_sys/trophy2/trophy00.ucp"),
        (0x14A0, "sce_sys/uds/uds00.ucp"),
        (0x2000, "sce_sys/param.json"),
        (0x2010, "sce_sys/playgo-hash-table.dat"),
        (0x2011, "sce_sys/playgo-ficm.dat"),
        (0x2020, "sce_sys/uds/npbind.dat"),
        (0x2021, "sce_sys/trophy2/npbind.dat"),
        (0x2060, "sce_sys/pic2.dds")
    ];

    public IEnumerable<(uint Id, string Path, long Size)> ContentFileEntries()
    {
        foreach ((uint id, string path) in ContentEntries)
        {
            PkgEntry? entry = _entries.FirstOrDefault(candidate => candidate.Id == id);
            if (entry is null) continue;
            yield return (id, path, entry.Size);
        }
    }

    /// <summary>
    /// Every metadata entry. Known entries are mapped to their inner-image paths; the rest keep their own
    /// name. Used so the file browser still shows what the package exposes when the inner image cannot be read.
    /// </summary>
    public IEnumerable<(uint Id, string Path, long Size)> ListReadableEntries()
    {
        foreach (PkgEntry entry in _entries)
        {
            string? mapped = null;
            foreach ((uint id, string path) in ContentEntries)
                if (id == entry.Id) { mapped = path; break; }
            string name = mapped
                ?? (!string.IsNullOrWhiteSpace(entry.Name) ? entry.Name : $"entry_0x{entry.Id:X4}.bin");
            yield return (entry.Id, name, entry.Size);
        }
    }

    // ---- inner files ---------------------------------------------------------------------------------

    public string ToRelativePath(PkgFile file) => Normalize(file.Path);

    public bool TryFindFile(string relativePath, out PkgFile file)
    {
        string key = Normalize(relativePath);
        foreach (PkgFile candidate in Files)
        {
            if (string.Equals(Normalize(candidate.Path), key, StringComparison.OrdinalIgnoreCase))
            {
                file = candidate;
                return true;
            }
        }
        file = null!;
        return false;
    }

    public byte[] ReadFile(PkgFile file)
    {
        if (file.Size is < 0 or > int.MaxValue)
            throw new InvalidDataException($"The inner file '{file.Path}' is too large to read into memory.");
        using Stream stream = OpenInnerFile(file);
        byte[] data = new byte[file.Size];
        stream.ReadExactly(data);
        return data;
    }

    public byte[] ReadFile(string relativePath)
    {
        if (!TryFindFile(relativePath, out PkgFile file))
            throw new InvalidDataException($"The inner file '{relativePath}' is not present in the package.");
        return ReadFile(file);
    }

    public byte[] ReadFileRange(PkgFile file, long offset, int count)
    {
        if (offset < 0 || count < 0 || offset > file.Size || count > file.Size - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        using Stream stream = OpenInnerFile(file);
        stream.Position = offset;
        byte[] result = new byte[count];
        stream.ReadExactly(result);
        return result;
    }

    /// <summary>Opens an inner file; the package stays open until the stream is disposed.</summary>
    public Stream OpenInnerFile(PkgFile file)
    {
        PkgPackage package;
        lock (_gate)
        {
            package = Acquire();
            _leases++;
        }
        try { return new LeasedStream(package.OpenFile(file), ReleaseLease); }
        catch { ReleaseLease(); throw; }
    }

    private void ReleaseLease()
    {
        lock (_gate)
        {
            if (--_leases != 0 || !_releasePending) return;
            _releasePending = false;
            DisposePackage();
        }
    }

    /// <summary>
    /// Releases the package file handle without discarding what has been decoded, so another process can
    /// replace or delete the package while this app is not reading it. It is reopened on the next read.
    /// </summary>
    public void ReleaseHandles()
    {
        lock (_gate)
        {
            if (_leases > 0) { _releasePending = true; return; }
            DisposePackage();
        }
    }

    private void DisposePackage()
    {
        PkgPackage? package = _package;
        _package = null;
        package?.Dispose();
    }

    private static string Normalize(string path)
    {
        string value = path.Replace('\\', '/').TrimStart('/');
        if (value.StartsWith("uroot/", StringComparison.OrdinalIgnoreCase)) value = value[6..];
        else if (string.Equals(value, "uroot", StringComparison.OrdinalIgnoreCase)) value = string.Empty;
        return value;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            DisposePackage();
        }
        GC.SuppressFinalize(this);
    }

    ~Ps5EnginePackageAccess() => Dispose();

    /// <summary>Notifies its owner once, when disposed, so the shared package handle can be released.</summary>
    private sealed class LeasedStream(Stream inner, Action onDispose) : Stream
    {
        private bool _disposed;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                inner.Dispose();
                onDispose();
            }
            base.Dispose(disposing);
        }
    }
}
