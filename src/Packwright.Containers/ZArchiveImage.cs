using System.Buffers.Binary;
using System.Security.Cryptography;
using ZstdSharp;

namespace Packwright.Containers;

public sealed class ZArchiveBuildResult
{
    public required string OutputPath { get; init; }
    public required int FileCount { get; init; }
    public required int DirectoryCount { get; init; }
    public required long SourceBytes { get; init; }
    public required long ArchiveBytes { get; init; }
}

/// <summary>Builds and verifies ZArchive (.zar) files from a dump folder.</summary>
public static class ZArchiveImage
{
    private const int CopyBufferSize = 1024 * 1024;

    public static async Task<ZArchiveBuildResult> CreateFromDirectoryAsync(string sourceDirectory, string outputPath,
        IProgress<FfpfscProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        string root = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException("The source folder does not exist: " + root);

        var directories = new List<string>();
        var files = new List<(string Path, string Relative, long Size)>();
        Collect(root, string.Empty, directories, files);
        long total = files.Sum(file => file.Size);

        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new ZArchiveWriter(stream, leaveOpen: true);

        foreach (string directory in directories) writer.MakeDirectory(directory);

        long processed = 0;
        byte[] buffer = new byte[CopyBufferSize];
        foreach ((string path, string relative, long size) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.StartNewFile(relative);
            await using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
                CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long copied = 0;
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                writer.Append(buffer.AsSpan(0, read));
                copied += read;
                processed += read;
                progress?.Report(new FfpfscProgress("Writing ZArchive", processed, total));
            }
            if (copied != size)
                throw new IOException($"The file changed while it was being read: {relative}");
        }

        progress?.Report(new FfpfscProgress("Finalizing ZArchive", processed, total));
        writer.Finish();
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        return new ZArchiveBuildResult
        {
            OutputPath = Path.GetFullPath(outputPath),
            FileCount = files.Count,
            DirectoryCount = directories.Count,
            SourceBytes = total,
            ArchiveBytes = writer.OutputLength
        };
    }

    /// <summary>Unpacks every file of a ZArchive into <paramref name="destination"/>.</summary>
    public static async Task ExtractToDirectoryAsync(string archivePath, string destination, bool overwrite = true,
        IProgress<FfpfscProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        using var volume = new ZArchiveVolume(archivePath);
        string root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        long total = volume.TotalFileBytes;
        long processed = 0;
        byte[] buffer = new byte[CopyBufferSize];
        foreach (ZArchiveEntry entry in volume.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string target = Path.GetFullPath(Path.Combine(root, entry.Path.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.Ordinal))
                throw new InvalidDataException("An archive path escapes the extraction folder: " + entry.Path);
            if (entry.IsDirectory)
            {
                Directory.CreateDirectory(target);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (!overwrite && File.Exists(target)) continue;
            await using Stream input = volume.OpenFile(entry.Path);
            await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None,
                CopyBufferSize, FileOptions.Asynchronous);
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                processed += read;
                progress?.Report(new FfpfscProgress("Extracting ZArchive", processed, total));
            }
        }
    }

    /// <summary>
    /// Writes a copy of an archive in which some files have new contents (or are added when missing).
    /// Everything else is streamed across unchanged. Paths in <paramref name="replacements"/> are matched
    /// without regard to case, as the format itself is case-insensitive.
    /// </summary>
    public static async Task RewriteWithReplacementsAsync(string archivePath, string outputPath,
        IReadOnlyDictionary<string, byte[]> replacements, IProgress<FfpfscProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        var pending = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, byte[] value) in replacements) pending[key.Replace('\\', '/').Trim('/')] = value;

        using var volume = new ZArchiveVolume(archivePath);
        await using var stream = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var writer = new ZArchiveWriter(stream, leaveOpen: true);

        foreach (ZArchiveEntry entry in volume.Entries.Where(entry => entry.IsDirectory))
            writer.MakeDirectory(entry.Path);

        long total = volume.TotalFileBytes + pending.Values.Sum(value => (long)value.Length);
        long processed = 0;
        byte[] buffer = new byte[CopyBufferSize];
        foreach (ZArchiveEntry entry in volume.Entries.Where(entry => !entry.IsDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            writer.StartNewFile(entry.Path);
            if (pending.Remove(entry.Path, out byte[]? replacement))
            {
                writer.Append(replacement);
                processed += replacement.Length;
                continue;
            }
            await using Stream input = volume.OpenFile(entry.Path);
            while (true)
            {
                int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read == 0) break;
                writer.Append(buffer.AsSpan(0, read));
                processed += read;
                progress?.Report(new FfpfscProgress("Writing ZArchive", processed, total));
            }
        }

        // Files that were not in the archive yet (for example a first icon0.png).
        foreach ((string path, byte[] data) in pending)
        {
            int slash = path.LastIndexOf('/');
            if (slash > 0) writer.MakeDirectory(path[..slash]);
            writer.StartNewFile(path);
            writer.Append(data);
        }

        progress?.Report(new FfpfscProgress("Finalizing ZArchive", processed, total));
        writer.Finish();
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Collect(string directory, string relative, List<string> directories,
        List<(string Path, string Relative, long Size)> files)
    {
        foreach (string file in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(file);
            files.Add((file, relative.Length == 0 ? name : relative + "/" + name, new FileInfo(file).Length));
        }
        foreach (string sub in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            string name = Path.GetFileName(sub);
            string child = relative.Length == 0 ? name : relative + "/" + name;
            directories.Add(child);
            Collect(sub, child, directories, files);
        }
    }

    /// <summary>
    /// Checks the footer, the SHA-256 integrity hash, the offset records and file tree, and that every
    /// compressed block decodes to a full block.
    /// </summary>
    public static async Task VerifyAsync(string path, IProgress<FfpfscProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            CopyBufferSize, FileOptions.Asynchronous | FileOptions.RandomAccess);
        long length = stream.Length;
        if (length < ZArchiveWriter.FooterLength)
            throw new InvalidDataException("The file is too small to be a ZArchive.");

        byte[] footer = new byte[ZArchiveWriter.FooterLength];
        stream.Position = length - footer.Length;
        await stream.ReadExactlyAsync(footer, cancellationToken).ConfigureAwait(false);
        if (BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(140)) != ZArchiveWriter.FooterMagic ||
            BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(136)) != ZArchiveWriter.FooterVersion1)
            throw new InvalidDataException("The ZArchive footer signature is not valid.");
        if (BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(128)) != (ulong)length)
            throw new InvalidDataException("The ZArchive size does not match its footer.");

        (ulong Offset, ulong Size) Section(int index) => (
            BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(index * 16)),
            BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(index * 16 + 8)));
        (ulong dataOffset, ulong dataSize) = Section(0);
        (ulong recordOffset, ulong recordSize) = Section(1);
        (ulong nameOffset, ulong nameSize) = Section(2);
        (ulong treeOffset, ulong treeSize) = Section(3);
        foreach ((ulong offset, ulong size) in new[] { (dataOffset, dataSize), (recordOffset, recordSize),
                     (nameOffset, nameSize), (treeOffset, treeSize) })
            if (offset + size > (ulong)length - ZArchiveWriter.FooterLength)
                throw new InvalidDataException("A ZArchive section lies outside the file.");

        // Integrity hash: SHA-256 of everything with the footer hash field zeroed.
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            stream.Position = 0;
            byte[] buffer = new byte[CopyBufferSize];
            long remaining = length - footer.Length;
            long done = 0;
            while (remaining > 0)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0) throw new EndOfStreamException();
                hash.AppendData(buffer, 0, read);
                remaining -= read;
                done += read;
                progress?.Report(new FfpfscProgress("Hashing ZArchive", done, length));
            }
            byte[] zeroed = (byte[])footer.Clone();
            Array.Clear(zeroed, 96, 32);
            hash.AppendData(zeroed);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), footer.AsSpan(96, 32)))
                throw new InvalidDataException("The ZArchive integrity hash does not match.");
        }

        // Offset records: each block must lie inside the data section and decode to a full block.
        if (recordSize % 40 != 0)
            throw new InvalidDataException("The ZArchive offset records are malformed.");
        byte[] records = new byte[recordSize];
        stream.Position = (long)recordOffset;
        await stream.ReadExactlyAsync(records, cancellationToken).ConfigureAwait(false);
        using var decompressor = new Decompressor();
        byte[] compressed = new byte[ZArchiveWriter.BlockSize];
        byte[] plain = new byte[ZArchiveWriter.BlockSize];
        int recordCount = (int)(recordSize / 40);
        long blocksChecked = 0;
        for (int r = 0; r < recordCount; r++)
        {
            int recordBase = r * 40;
            ulong offset = BinaryPrimitives.ReadUInt64BigEndian(records.AsSpan(recordBase));
            for (int i = 0; i < ZArchiveWriter.EntriesPerOffsetRecord; i++)
            {
                int size = BinaryPrimitives.ReadUInt16BigEndian(records.AsSpan(recordBase + 8 + 2 * i)) + 1;
                // The unused tail of the last record stores zero, which reads back as size 1 at a
                // position that cannot hold data; stop once the data section is exhausted.
                if (offset >= dataSize) break;
                if (offset + (ulong)size > dataSize)
                    throw new InvalidDataException("A ZArchive block lies outside the data section.");
                stream.Position = (long)(dataOffset + offset);
                await stream.ReadExactlyAsync(compressed.AsMemory(0, size), cancellationToken).ConfigureAwait(false);
                if (size < ZArchiveWriter.BlockSize)
                {
                    int decoded;
                    try { decoded = decompressor.Unwrap(compressed.AsSpan(0, size), plain); }
                    catch (Exception ex) { throw new InvalidDataException($"ZArchive block {blocksChecked} is corrupt.", ex); }
                    if (decoded != ZArchiveWriter.BlockSize)
                        throw new InvalidDataException($"ZArchive block {blocksChecked} decodes to the wrong size.");
                }
                offset += (ulong)size;
                blocksChecked++;
            }
            progress?.Report(new FfpfscProgress("Verifying ZArchive blocks", r + 1, recordCount));
        }

        // File tree: the root is a directory and every directory range stays inside the tree.
        if (treeSize < 16 || treeSize % 16 != 0)
            throw new InvalidDataException("The ZArchive file tree is malformed.");
        byte[] tree = new byte[treeSize];
        stream.Position = (long)treeOffset;
        await stream.ReadExactlyAsync(tree, cancellationToken).ConfigureAwait(false);
        uint entryCount = (uint)(treeSize / 16);
        ulong uncompressedLimit = (ulong)blocksChecked * ZArchiveWriter.BlockSize;
        for (uint i = 0; i < entryCount; i++)
        {
            ReadOnlySpan<byte> entry = tree.AsSpan((int)i * 16, 16);
            uint flags = BinaryPrimitives.ReadUInt32BigEndian(entry);
            uint a = BinaryPrimitives.ReadUInt32BigEndian(entry[4..]);
            uint b = BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            uint c = BinaryPrimitives.ReadUInt32BigEndian(entry[12..]);
            if ((flags & 0x80000000) != 0)
            {
                ulong fileOffset = a | ((ulong)(c & 0xFFFF) << 32);
                ulong fileSize = b | ((ulong)(c & 0xFFFF0000) << 16);
                if (fileOffset + fileSize > uncompressedLimit)
                    throw new InvalidDataException("A ZArchive file extends past the stored data.");
                if ((flags & 0x7FFFFFFF) >= nameSize)
                    throw new InvalidDataException("A ZArchive file name lies outside the name table.");
            }
            else
            {
                if (i == 0 && (flags & 0x7FFFFFFF) != 0x7FFFFFFF)
                    throw new InvalidDataException("The ZArchive root entry is malformed.");
                if ((ulong)a + b > entryCount)
                    throw new InvalidDataException("A ZArchive directory range lies outside the file tree.");
            }
        }
        if ((tree[0] & 0x80) != 0)
            throw new InvalidDataException("The ZArchive root entry is not a directory.");
    }
}
