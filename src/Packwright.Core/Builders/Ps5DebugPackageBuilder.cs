using Packwright.Core.Parsers;
using Packwright.Core.Services;
using Packwright.Pkg.Reader;
using Packwright.Pkg.Writer;
using Packwright.Pkg.Writer;

namespace Packwright.Core.Builders;

public sealed class Ps5DebugPackageBuildOptions
{
    public required string ContentId { get; init; }
    public string Passcode { get; init; } = Ps5DebugPackageCredentials.DefaultPasscode;

    /// <summary>Optional 16-byte outer-PFS seed for a byte-reproducible package.</summary>
    public byte[]? Seed { get; init; }

    /// <summary>Optional full 64-bit executable SDK id to stamp (param.json + .sceversion).
    /// Null preserves the source metadata.</summary>
    public ulong? SdkVersionOverride { get; init; }

    /// <summary>Optional sink for engine log lines.</summary>
    public Action<string>? Log { get; init; }

    /// <summary>Inner-image compression. Default is Auto (Kraken where it helps, stored otherwise).</summary>
    public Ps5InnerCompression Compression { get; init; } = Ps5InnerCompression.Auto;

    /// <summary>Kraken level (Oodle naming, -4..9). The encoder uses one fixed strategy, so the level does not change the output.</summary>
    public int KrakenLevel { get; init; } = 7;

    /// <summary>Spans compressed at the same time; 0 selects the processor count.</summary>
    public int KrakenThreads { get; init; }

    /// <summary>Play-go chunks, used only when the title brings no play-go files of its own.</summary>
    public int PlayGoChunkCount { get; init; } = 1;

    /// <summary>Optional DRM token to force in param.json. Null preserves the source token.</summary>
    public string? DrmTypeOverride { get; init; }
}

public readonly record struct Ps5DebugPackageProgress(string Stage, long CompletedBytes,
    long TotalBytes, string CurrentPath);

public sealed class Ps5DebugPackageBuildResult
{
    public required string OutputPath { get; init; }
    public required string ContentId { get; init; }
    public required string KeyFingerprint { get; init; }
    public required long PackageSize { get; init; }
    public required long SourceBytes { get; init; }
    public required int SourceFiles { get; init; }
    public required bool UsesDefaultPasscode { get; init; }
}

public sealed class Ps5DebugPackageValidationResult
{
    public required bool IsValid { get; init; }
    public required string Message { get; init; }
    public required int IndexedFiles { get; init; }
    public required long PackageSize { get; init; }
}

public static class Ps5DebugPackageBuilder
{
    public static Task<Ps5DebugPackageBuildResult> CreateFromDirectoryAsync(string sourceDirectory,
        string outputPath, Ps5DebugPackageBuildOptions options,
        IProgress<Ps5DebugPackageProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentNullException.ThrowIfNull(options);
        return Task.Run(() => CreateFromDirectory(sourceDirectory, outputPath, options, progress, cancellationToken),
            cancellationToken);
    }

    private static Ps5DebugPackageBuildResult CreateFromDirectory(string sourceDirectory, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress,
        CancellationToken cancellationToken)
    {
        string source = Path.GetFullPath(sourceDirectory);
        if (!Directory.Exists(source)) throw new DirectoryNotFoundException(source);
        progress?.Report(new Ps5DebugPackageProgress("Reading source folder", 0, 0, Path.GetFileName(source)));

        var files = new List<PkgSourceFile>();
        var directories = new List<string>();
        long sourceBytes = 0;
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            directories.Add(Path.GetRelativePath(source, directory).Replace('\\', '/'));
        string[] paths = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)
            .OrderBy(item => item, StringComparer.Ordinal).ToArray();
        int processed = 0;
        foreach (string path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string relative = Path.GetRelativePath(source, path).Replace('\\', '/');
            progress?.Report(new Ps5DebugPackageProgress("Reading source folder", ++processed, paths.Length, relative));
            if (SourceFolderValidator.IsPublishingArtifact(relative)) continue;
            long length = new FileInfo(path).Length;
            sourceBytes += length;
            string full = path;
            files.Add(new PkgSourceFile(relative, length,
                () => new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan)));
        }

        if (!files.Any(file => file.Path.Equals("sce_sys/param.json", StringComparison.Ordinal)))
            throw new InvalidDataException("The dump does not contain sce_sys/param.json, which is required to build a package.");
        if (files.Count == 0) throw new InvalidDataException("The dump does not contain any package files.");
        return PackwrightEngine.Run(files, directories, outputPath, options, progress, sourceBytes, cancellationToken);
    }
    public static Ps5DebugPackageValidationResult Validate(string packagePath,
        string passcode = Ps5DebugPackageCredentials.DefaultPasscode)
    {
        try
        {
            string fullPath = Path.GetFullPath(packagePath);
            using PkgPackage package = PkgPackage.Open(fullPath, passcode);
            PkgValidationReport report = PkgValidator.Validate(package);
            int files = package.IsMetadataOnly ? 0 : package.Files.Count;
            PkgCheck? content = report.Checks.FirstOrDefault(check => check.Name == "Content");
            bool valid = report.Passed && !package.IsRetail && content is { Status: PkgCheckStatus.Pass };
            string message;
            if (valid)
                message = $"Structure, metadata digests and file decoding verified ({files:N0} files).";
            else if (!report.Passed)
                message = string.Join("; ", report.Failures.Select(check => check.Name + ": " + check.Detail));
            else
                message = string.Join("; ", report.Checks.Where(check => check.Status == PkgCheckStatus.Warning)
                    .Select(check => check.Detail));
            if (string.IsNullOrWhiteSpace(message)) message = "The package structure is incomplete.";
            return new Ps5DebugPackageValidationResult
            {
                IsValid = valid,
                Message = message,
                IndexedFiles = files,
                PackageSize = new FileInfo(fullPath).Length
            };
        }
        catch (Exception ex)
        {
            return new Ps5DebugPackageValidationResult { IsValid = false, Message = ex.Message, IndexedFiles = 0,
                PackageSize = File.Exists(packagePath) ? new FileInfo(packagePath).Length : 0 };
        }
    }
}
