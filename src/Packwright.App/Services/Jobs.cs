using Packwright.Core.Backends;
using Packwright.Core.Builders;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using Packwright.Containers;
using Packwright.Infrastructure;
using UFS2Tool;

namespace Packwright.App.Services;

/// <summary>Settings for a debug package (FPKG) build.</summary>
public sealed record PackageBuildSettings(
    Ps5InnerCompression Compression = Ps5InnerCompression.Auto,
    int KrakenLevel = 7,
    int KrakenThreads = 0,
    bool Deterministic = false,
    string? DrmType = "upgradable",
    ulong? SdkVersionOverride = null,
    int PlayGoChunks = 1);

/// <summary>
/// The long-running operations behind the Tools tab. Each method is UI-free and reports progress
/// through <see cref="PackageTaskProgress"/>, so it can run inside the shared task queue.
/// </summary>
public static class Jobs
{
    public static IProgress<PackageTaskProgress> NoProgress { get; } = new Progress<PackageTaskProgress>();

    public static IProgress<FfpfscProgress> Adapt(IProgress<PackageTaskProgress> target) =>
        new Progress<FfpfscProgress>(value => target.Report(new PackageTaskProgress(
            value.Stage, 0, 0, value.BytesProcessed, value.TotalBytes, 0, 0, string.Empty)));

    public static IProgress<Ufs2Progress> AdaptUfs2(IProgress<PackageTaskProgress> target) =>
        new Progress<Ufs2Progress>(value => target.Report(new PackageTaskProgress(
            value.Stage, 0, 0, value.BytesProcessed, value.TotalBytes,
            checked((int)Math.Min(value.Completed, int.MaxValue)),
            checked((int)Math.Min(value.Total, int.MaxValue)), value.Unit)));

    public static IProgress<Ps5ImageConversionProgress> AdaptConversion(IProgress<PackageTaskProgress> target) =>
        new Progress<Ps5ImageConversionProgress>(value => target.Report(new PackageTaskProgress(
            value.Stage, 0, 0, value.Completed, value.Total, 0, 0, string.Empty)));

    public static IProgress<Ps5PackageExtractProgress> AdaptExtract(IProgress<PackageTaskProgress> target) =>
        new Progress<Ps5PackageExtractProgress>(value => target.Report(new PackageTaskProgress(
            "Extracting", 0, 0, value.CompletedBytes, value.TotalBytes, 0, 0, value.CurrentPath)));

    private static bool _keysLoaded;

    /// <summary>Loads the imported compatibility key file (once) so package builds use it.</summary>
    public static void EnsureKeys()
    {
        if (_keysLoaded) return;
        _keysLoaded = true;
        string path = new AppStateStore().LoadSettings().KeyFile;
        if (string.IsNullOrWhiteSpace(path)) return;
        string? problem = Ps5BuildKeys.TryConfigure(path);
        if (problem is not null) Logger.Warn("The imported key file was not used: " + problem);
    }

    /// <summary>The package engine. There is one.</summary>
    public static IPackageBackend ResolveBackend(string? id) => BackendRegistry.Get(id);
    public static Task ConvertAsync(string source, string output, Ps5ImageConversionTarget target, bool overwrite,
        bool fromPackage, IProgress<PackageTaskProgress> progress, CancellationToken token,
        ExfatBuildOptions? exfatOptions = null, FfpfscBuildOptions? ffpfscOptions = null,
        FfpkgBuildOptions? ffpkgOptions = null)
    {
        IProgress<Ps5ImageConversionProgress> bridge = AdaptConversion(progress);
        return fromPackage
            ? Ps5PackageImageConversion.ConvertAsync(source, output, target, overwrite, bridge, token,
                exfatOptions, ffpfscOptions, ffpkgOptions)
            : Ps5ImageConversionService.ConvertAsync(source, output, target, overwrite, bridge, token,
                exfatOptions, ffpfscOptions, ffpkgOptions);
    }

    public static async Task ExtractImageAsync(string source, Ps5ImageFormat format, string output,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        switch (format)
        {
            case Ps5ImageFormat.Exfat:
                await ExfatImage.ExtractDirectoryAsync(source, output, Adapt(progress), token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Ufs2:
                await Ufs2Operations.ExtractAsync(source, output, AdaptUfs2(progress), token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Pfs:
                await FfpfscImage.ExtractToDirectoryAsync(source, output, overwrite: true, Adapt(progress), token)
                    .ConfigureAwait(false);
                break;
            case Ps5ImageFormat.ZArchive:
                await ZArchiveImage.ExtractToDirectoryAsync(source, output, overwrite: true, Adapt(progress), token)
                    .ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("Unsupported source image format.");
        }
    }

    public static async Task VerifyImageAsync(string source, Ps5ImageFormat format, CancellationToken token,
        IProgress<PackageTaskProgress>? progress = null)
    {
        // Report how much has been checked, so the task shows a bar and the size done, left and total.
        IProgress<FfpfscProgress>? bytes = progress is null ? null : Adapt(progress);
        switch (format)
        {
            case Ps5ImageFormat.Exfat:
                await ExfatImage.VerifyAsync(source, bytes, token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Ufs2:
                await Ufs2Operations.VerifyAsync(source, progress is null ? null : AdaptUfs2(progress), token).ConfigureAwait(false);
                break;
            case Ps5ImageFormat.Pfs:
                FfpfscVerificationResult result = await FfpfscImage.TryVerifyAsync(source, null, bytes, token)
                    .ConfigureAwait(false);
                if (!result.StructureValid)
                    throw new InvalidDataException("The FFPFSC structure is invalid. " + (result.Error ?? string.Empty));
                if (!result.EveryPfscBlockDecodes)
                    throw new InvalidDataException("The FFPFSC block decode failed. " + (result.Error ?? string.Empty));
                break;
            case Ps5ImageFormat.ZArchive:
                await ZArchiveImage.VerifyAsync(source, bytes, token).ConfigureAwait(false);
                break;
            default:
                throw new InvalidDataException("Unsupported image format.");
        }
    }

    public static async Task<Ps5PackageExtractResult> ExtractPackageAsync(string source, string output,
        string passcode, IProgress<PackageTaskProgress> progress, CancellationToken token) =>
        await Ps5PackageExtraction.ExtractAsync(source, output, passcode, AdaptExtract(progress), token)
            .ConfigureAwait(false);

    public static Ps5DebugPackageValidationResult VerifyPackage(string source, string passcode) =>
        Ps5DebugPackageBuilder.Validate(source, passcode);

    /// <summary>Reads the content ID from a dump folder or image so a package can be built from it.</summary>
    public static string ReadContentId(string source, Ps5ImageFormat format)
    {
        byte[]? bytes = TryReadParamBytes(source, format);
        if (bytes is null || bytes.Length == 0) return string.Empty;
        try
        {
            using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(bytes);
            return document.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("contentId", out System.Text.Json.JsonElement element) &&
                   element.ValueKind == System.Text.Json.JsonValueKind.String
                ? element.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (System.Text.Json.JsonException)
        {
            return string.Empty;
        }
    }

    private static byte[]? TryReadParamBytes(string source, Ps5ImageFormat format)
    {
        try
        {
            if (Directory.Exists(source))
            {
                string path = Path.Combine(source, "sce_sys", "param.json");
                return File.Exists(path) ? File.ReadAllBytes(path) : null;
            }
            switch (format)
            {
                case Ps5ImageFormat.Exfat:
                    using (var volume = new ExfatVolume(File.OpenRead(source)))
                        return volume.ReadAllBytes("sce_sys/param.json", 4 * 1024 * 1024);
                case Ps5ImageFormat.Ufs2:
                    using (var volume = new Ufs2Volume(source))
                        return volume.ReadAllBytes("sce_sys/param.json", 4 * 1024 * 1024);
                case Ps5ImageFormat.Pfs:
                    using (var volume = FfpfscVolume.Open(source))
                        return volume.ReadAllBytes("sce_sys/param.json", 4 * 1024 * 1024);
                case Ps5ImageFormat.ZArchive:
                    using (var volume = new ZArchiveVolume(source))
                    {
                        string param = volume.Entries.FirstOrDefault(entry => !entry.IsDirectory &&
                            (entry.Path.Equals("sce_sys/param.json", StringComparison.OrdinalIgnoreCase) ||
                             entry.Path.EndsWith("/sce_sys/param.json", StringComparison.OrdinalIgnoreCase)))?.Path ?? string.Empty;
                        return param.Length == 0 ? null : volume.ReadAllBytes(param, 4 * 1024 * 1024);
                    }
                default:
                    return null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                   ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static byte[] DeterministicSeed(string contentId, string passcode) =>
        System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(contentId + "\0" + passcode))[..16];

    public static async Task BuildPackageAsync(string source, string output, string contentId, string passcode,
        bool overwrite, PackageBuildSettings settings, IPackageBackend backend,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        if (!overwrite && File.Exists(output))
            throw new IOException($"The output file already exists: {output}");
        string outputDirectory = Path.GetDirectoryName(Path.GetFullPath(output)) ?? Directory.GetCurrentDirectory();
        Directory.CreateDirectory(outputDirectory);
        string jobDirectory = Path.Combine(outputDirectory, ".packwright-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(jobDirectory);
        string partial = Path.Combine(jobDirectory, Path.GetFileName(output) + ".partial");
        var options = new Ps5DebugPackageBuildOptions
        {
            ContentId = contentId,
            Passcode = passcode,
            SdkVersionOverride = settings.SdkVersionOverride,
            DrmTypeOverride = settings.DrmType,
            Log = Logger.Info,
            Compression = settings.Compression,
            KrakenLevel = settings.KrakenLevel,
            KrakenThreads = settings.KrakenThreads,
            PlayGoChunkCount = settings.PlayGoChunks,
            Seed = settings.Deterministic ? DeterministicSeed(contentId, passcode) : null
        };
        var bridge = new Progress<Ps5DebugPackageProgress>(value =>
            progress.Report(new PackageTaskProgress(value.Stage, 0, 0, value.CompletedBytes, value.TotalBytes,
                0, 0, value.CurrentPath)));
        EnsureKeys();
        bool isDirectory = Directory.Exists(source);
        try
        {
            if (isDirectory)
                await backend.BuildFromDirectoryAsync(source, partial, options, bridge, token).ConfigureAwait(false);
            else
                await backend.BuildFromImageAsync(source, partial, options, bridge, token).ConfigureAwait(false);
            PackageReaderVerificationResult result = PackageReaderVerification.Inspect(partial, passcode);
            Logger.Info($"Verified built package: {result.FileCount:N0} file(s), eboot.bin " +
                (result.HasEboot ? "present" : "missing") + ".");
            if (result.FileCount == 0 || !result.HasEboot)
                throw new InvalidDataException("The built package failed verification: " +
                    (result.FileCount == 0 ? "the filesystem is empty." : "eboot.bin is missing."));
            token.ThrowIfCancellationRequested();
            File.Move(partial, output, overwrite);
        }
        catch
        {
            TryDeleteFile(partial);
            throw;
        }
        finally
        {
            TryDeleteDirectory(jobDirectory);
        }
    }

    private static long ZArchiveLogicalBytes(string path)
    {
        using var volume = new ZArchiveVolume(path);
        return volume.TotalFileBytes;
    }

    public static string FindAvailablePath(string preferredPath)
    {
        string fullPath = Path.GetFullPath(preferredPath);
        if (!Directory.Exists(fullPath) && !File.Exists(fullPath)) return fullPath;
        for (int index = 2; index < 10_000; index++)
        {
            string candidate = fullPath + $" ({index})";
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
        throw new IOException("Unable to find an available output name.");
    }

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
