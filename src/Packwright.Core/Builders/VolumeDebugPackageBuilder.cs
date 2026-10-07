using Packwright.Containers;
using Packwright.Pkg.Writer;
using UFS2Tool;

namespace Packwright.Core.Builders;

/// <summary>
/// Builds a PS5 debug package directly from a PS5 filesystem image (exFAT, UFS2/FFPKG or FFPFSC) without extracting
/// it first. Each file is streamed from the image into the package as it is written.
/// </summary>
public static class VolumeDebugPackageBuilder
{
    public static Task<Ps5DebugPackageBuildResult> CreateFromImageAsync(string imagePath, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => CreateFromImage(imagePath, outputPath, options, progress, cancellationToken),
            cancellationToken);
    }

    private static Ps5DebugPackageBuildResult CreateFromImage(string imagePath, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string source = Path.GetFullPath(imagePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The source image was not found.", source);
        progress?.Report(new Ps5DebugPackageProgress("Detecting image format", 0, 0, Path.GetFileName(source)));

        using IDisposable volume = OpenVolume(source, out IReadOnlyList<VolumeFile> entries,
            out Func<string, Stream> open, out string formatName);
        string readStage = $"Reading {formatName} filesystem";
        progress?.Report(new Ps5DebugPackageProgress(readStage, 0, entries.Count, Path.GetFileName(source)));

        var files = new List<PkgSourceFile>();
        var directories = new List<string>();
        long sourceBytes = 0;
        int processed = 0;
        foreach (VolumeFile entry in entries.OrderBy(item => item.Path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(new Ps5DebugPackageProgress(readStage, ++processed, entries.Count, entry.Path));
            if (entry.IsDirectory) { directories.Add(entry.Path); continue; }
            if (entry.IsSymlink) continue;
            string relative = entry.Path;
            if (SourceFolderValidator.IsPublishingArtifact(relative)) continue;
            sourceBytes += entry.Size;
            files.Add(new PkgSourceFile(relative, entry.Size, () => open(relative)));
        }

        if (!files.Any(file => file.Path.Equals("sce_sys/param.json", StringComparison.Ordinal)))
            throw new InvalidDataException("The image does not contain sce_sys/param.json, which is required to build a package.");
        if (files.Count == 0) throw new InvalidDataException("The image does not contain any package files.");
        return PackwrightEngine.Run(files, directories, outputPath, options, progress, sourceBytes, cancellationToken);
    }
    /// <summary>
    /// Sums the uncompressed payload bytes of an image (exFAT / UFS2-FFPKG / FFPFSC) without
    /// extracting or building it. Used to preflight the disk space an extract-then-build needs.
    /// </summary>
    public static long EstimatePayloadBytes(string imagePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imagePath);
        string source = Path.GetFullPath(imagePath);
        if (!File.Exists(source)) throw new FileNotFoundException("The source image was not found.", source);

        using IDisposable volume = OpenVolume(source, out IReadOnlyList<VolumeFile> entries, out _, out _);
        long total = 0;
        foreach (VolumeFile entry in entries)
        {
            if (!entry.IsDirectory && !entry.IsSymlink)
                total += entry.Size;
        }
        return total;
    }

    private static IDisposable OpenVolume(string imagePath, out IReadOnlyList<VolumeFile> entries,
        out Func<string, Stream> open, out string formatName)
    {
        var stream = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.RandomAccess);
        try
        {
            Ps5ImageFormat format = Ps5ImageFormatProbe.Detect(stream);
            stream.Position = 0;
            switch (format)
            {
                case Ps5ImageFormat.Pfs:
                {
                    formatName = "FFPFSC";
                    FfpfscVolume volume = FfpfscVolume.Open(stream, imagePath, leaveOpen: false);
                    if (volume.InnerFilesystemKind == FfpfscInnerFilesystemKind.Pfs)
                    {
                        volume.Dispose();
                        throw new InvalidDataException(
                            "The FFPFSC inner payload is a nested PFS container, not a file tree.");
                    }
                    entries = volume.Entries
                        .Select(item => new VolumeFile(Normalize(item.Path), item.IsDirectory, false, item.Size))
                        .ToArray();
                    open = volume.OpenFile;
                    return volume;
                }
                case Ps5ImageFormat.Ufs2:
                {
                    formatName = "FFPKG";
                    var volume = new Ufs2Volume(stream, imagePath, leaveOpen: false);
                    entries = volume.Entries
                        .Select(item => new VolumeFile(Normalize(item.Path), item.IsDirectory, item.IsSymlink,
                            item.Size))
                        .ToArray();
                    open = volume.OpenFile;
                    return volume;
                }
                case Ps5ImageFormat.Exfat:
                {
                    formatName = "exFAT";
                    var volume = new ExfatVolume(stream, leaveOpen: false);
                    entries = volume.Entries
                        .Select(item => new VolumeFile(Normalize(item.Path), item.IsDirectory, false, item.Size))
                        .ToArray();
                    open = volume.OpenFile;
                    return volume;
                }
                case Ps5ImageFormat.ZArchive:
                {
                    formatName = "ZArchive";
                    stream.Dispose();
                    var volume = new ZArchiveVolume(imagePath);
                    try
                    {
                        const string paramSuffix = "sce_sys/param.json";
                        ZArchiveEntry[] roots = volume.Entries
                            .Where(item => !item.IsDirectory && (item.Path.Equals(paramSuffix, StringComparison.OrdinalIgnoreCase)
                                || item.Path.EndsWith("/" + paramSuffix, StringComparison.OrdinalIgnoreCase))).ToArray();
                        if (roots.Length == 0) throw new InvalidDataException("The ZArchive has no sce_sys/param.json.");
                        if (roots.Length > 1) throw new InvalidDataException("The ZArchive contains multiple PS5 game roots.");
                        string root = roots[0].Path.Length == paramSuffix.Length ? string.Empty : roots[0].Path[..^(paramSuffix.Length + 1)];
                        string prefix = root.Length == 0 ? string.Empty : root + "/";
                        entries = volume.Entries
                            .Where(item => prefix.Length == 0 || item.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            .Select(item => new VolumeFile(Normalize(item.Path[prefix.Length..]), item.IsDirectory, false, item.Size))
                            .Where(item => item.Path.Length > 0)
                            .ToArray();
                        open = relative => volume.OpenFile(prefix + relative);
                        return volume;
                    }
                    catch
                    {
                        volume.Dispose();
                        throw;
                    }
                }
                default:
                    throw new InvalidDataException($"Unrecognised PS5 image format: {imagePath}");
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');

    private sealed record VolumeFile(string Path, bool IsDirectory, bool IsSymlink, long Size);
}
