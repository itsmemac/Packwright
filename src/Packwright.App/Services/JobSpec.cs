using System.Text.Json;
using Packwright.Core.Backends;
using Packwright.Core.Builders;
using Packwright.Core.Tasks;
using Packwright.Containers;
using Packwright.Infrastructure;
using UFS2Tool;

namespace Packwright.App.Services;

/// <summary>
/// A serializable description of one queued job. The same spec drives the job when it is first queued
/// and when it is restored after a restart, so there is one place that knows how to run each kind.
/// </summary>
public sealed class JobSpec
{
    public const string Convert = "convert";
    public const string BuildPackage = "build";
    public const string Extract = "extract";
    public const string Verify = "verify";
    public const string Repair = "repair";
    public const string RefreshAmpr = "ampr";
    public const string RebuildFfpkg = "rebuild";
    public const string EditDetails = "editmeta";
    public const string SendFtp = "sendftp";
    public const string SendInstall = "sendurl";

    public string Kind { get; set; } = string.Empty;

    /// <summary>The game's name and a small PNG icon (base64), shown on the task card.</summary>
    public string Title { get; set; } = string.Empty;
    public string? Icon { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Output { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;       // Ps5ImageFormat name of the source
    public string Target { get; set; } = string.Empty;       // Ps5ImageConversionTarget name
    public bool Overwrite { get; set; }
    public bool FromPackage { get; set; }
    public string Passcode { get; set; } = string.Empty;

    // Image options
    public int ClusterSize { get; set; }
    public bool GenerateAmpr { get; set; } = true;
    public int CompressionLevel { get; set; } = 7;
    public int MinimumGain { get; set; } = 1;
    public int BlockSize { get; set; } = 32768;
    public int FragmentSize { get; set; } = 4096;
    public int BytesPerInode { get; set; } = 262144;
    public int MinFreePercent { get; set; }

    /// <summary>For the send kinds: a copy of the console's settings at the time, and the folder on the console.</summary>
    public ConsoleProfile? Console { get; set; }
    public string RemoteFolder { get; set; } = string.Empty;

    /// <summary>For <see cref="EditDetails"/>: the details to write. Output empty means edit in place.</summary>
    public MetadataEdit? Edit { get; set; }

    // Package build
    public string ContentId { get; set; } = string.Empty;
    public string Backend { get; set; } = string.Empty;
    public string Compression { get; set; } = nameof(Ps5InnerCompression.Auto);
    public int KrakenLevel { get; set; } = 7;
    public int KrakenThreads { get; set; }
    public int PlayGoChunks { get; set; } = 1;
    public bool Deterministic { get; set; }
    public bool FakeSign { get; set; } = true;
    public bool RightSprx { get; set; } = true;
    public string? Drm { get; set; } = "upgradable";
    public ulong? Sdk { get; set; }
    public string? TempDirectory { get; set; }

    public string ToJson() => JsonSerializer.Serialize(this);

    public static JobSpec? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<JobSpec>(json); }
        catch (JsonException) { return null; }
    }

    private static string Ps5LibraryHealthSize(long bytes) => Packwright.Core.Services.Ps5LibraryHealth.FormatBytes(bytes);

    private static string Elapsed(System.Diagnostics.Stopwatch watch)
    {
        TimeSpan time = watch.Elapsed;
        return time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min {time.Seconds} s" : $"{Math.Max(1, (int)Math.Round(time.TotalSeconds))} s";
    }

    public PackageTaskStage[] Plan() => Kind switch
    {
        Convert => FromPackage ? PackageTaskPlans.ConvertPackage : PackageTaskPlans.ConvertImage,
        BuildPackage => PackageTaskPlans.BuildPackageFor(Source, Backend == BackendRegistry.LppId && !Directory.Exists(Source)),
        Extract => PackageTaskPlans.Extract,
        Verify => PackageTaskPlans.Verify,
        RefreshAmpr => PackageTaskPlans.Ampr,
        _ => PackageTaskPlans.Single
    };

    public string TaskType => Kind switch
    {
        Convert => PackageTaskTypes.ImageConvert,
        BuildPackage => PackageTaskTypes.ImageBuildPackage,
        Extract => PackageTaskTypes.PackageExtract,
        Verify => FromPackage ? PackageTaskTypes.PackageVerify : PackageTaskTypes.ImageVerify,
        Repair => PackageTaskTypes.ExfatRepair,
        RefreshAmpr => PackageTaskTypes.ExfatAmpr,
        EditDetails => PackageTaskTypes.EditDetails,
        SendFtp or SendInstall => PackageTaskTypes.SendToConsole,
        _ => PackageTaskTypes.FfpkgRebuild
    };

    private Ps5ImageFormat ImageFormat => Enum.TryParse(Format, out Ps5ImageFormat parsed) ? parsed : Ps5ImageFormat.Unknown;

    /// <summary>The delegate that runs this job inside the task queue.</summary>
    public Func<IProgress<PackageTaskProgress>, CancellationToken, Task>? Executor()
    {
        if (Source.Length == 0) return null;
        switch (Kind)
        {
            case Convert:
                if (Output.Length == 0 || !Enum.TryParse(Target, out Ps5ImageConversionTarget target)) return null;
                return (progress, token) => Jobs.ConvertAsync(Source, Output, target, Overwrite, FromPackage, progress, token,
                    target is Ps5ImageConversionTarget.Ffpkg or Ps5ImageConversionTarget.ZArchive
                        ? null
                        : new ExfatBuildOptions { ClusterSize = ClusterSize > 0 ? ClusterSize : null, GenerateAmprIndex = GenerateAmpr },
                    target == Ps5ImageConversionTarget.Ffpfsc
                        ? new FfpfscBuildOptions
                        {
                            Compression = new PfscCompressionOptions { CompressionLevel = CompressionLevel, MinimumGainPercent = MinimumGain }
                        }
                        : null,
                    target == Ps5ImageConversionTarget.Ffpkg
                        ? new FfpkgBuildOptions
                        {
                            BlockSize = BlockSize,
                            FragmentSize = FragmentSize,
                            BytesPerInode = BytesPerInode,
                            MinFreePercent = MinFreePercent
                        }
                        : null);
            case BuildPackage:
                if (Output.Length == 0 || ContentId.Length == 0) return null;
                var settings = new PackageBuildSettings(
                    Enum.TryParse(Compression, out Ps5InnerCompression compression) ? compression : Ps5InnerCompression.Auto,
                    KrakenLevel, KrakenThreads, PlayGoChunks, Deterministic, FakeSign, RightSprx, Drm, Sdk, TempDirectory);
                IPackageBackend backend = Jobs.ResolveBackend(Backend);
                string passcode = Passcode.Length > 0 ? Passcode : SonyDebugPackageCredentials.DefaultPasscode;
                return (progress, token) => Jobs.BuildPackageAsync(Source, Output, ContentId, passcode, Overwrite, settings,
                    backend, progress, token);
            case Extract:
                if (Output.Length == 0) return null;
                if (FromPackage)
                {
                    string code = Passcode.Length > 0 ? Passcode : SonyDebugPackageCredentials.DefaultPasscode;
                    return async (progress, token) =>
                        await Jobs.ExtractPackageAsync(Source, Output, code, progress, token).ConfigureAwait(false);
                }
                Ps5ImageFormat extractFormat = ImageFormat;
                return (progress, token) => Jobs.ExtractImageAsync(Source, extractFormat, Output, progress, token);
            case Verify:
                if (FromPackage)
                {
                    string code = Passcode.Length > 0 ? Passcode : SonyDebugPackageCredentials.DefaultPasscode;
                    return (progress, token) =>
                    {
                        var watch = System.Diagnostics.Stopwatch.StartNew();
                        SonyDebugPackageValidationResult result = Jobs.VerifyPackage(Source, code);
                        if (!result.IsValid)
                            throw new InvalidDataException("This package did not pass verification: " + result.Message.Trim().TrimEnd('.') +
                                                           ". The file may be damaged or incomplete (compare its size and checksum with where you got it).");
                        Logger.Info($"Package verified: {result.IndexedFiles:N0} indexed file(s).");
                        progress.Report(new PackageTaskProgress("Verified",
                            Result: $"Verified: no problems found. {result.IndexedFiles:N0} file(s) checked in {Elapsed(watch)}."));
                        return Task.CompletedTask;
                    };
                }
                Ps5ImageFormat verifyFormat = ImageFormat;
                return async (progress, token) =>
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    string label = verifyFormat switch
                    {
                        Ps5ImageFormat.Exfat => "exFAT image",
                        Ps5ImageFormat.Ufs2 => "FFPKG image",
                        Ps5ImageFormat.Pfs => "FFPFSC image",
                        Ps5ImageFormat.ZArchive => "ZArchive",
                        _ => "image"
                    };
                    try
                    {
                        await Jobs.VerifyImageAsync(Source, verifyFormat, token, progress).ConfigureAwait(false);
                    }
                    catch (InvalidDataException ex)
                    {
                        throw new InvalidDataException($"This {label} did not pass verification: {ex.Message.Trim().TrimEnd('.')}. " +
                                                       "The file may be damaged or incomplete (compare its size and checksum with where you got it).", ex);
                    }
                    long length = File.Exists(Source) ? new FileInfo(Source).Length : 0;
                    progress.Report(new PackageTaskProgress("Verified", Result: $"Verified: no problems found. The {label}" +
                        (length > 0 ? $" ({Ps5LibraryHealthSize(length)})" : string.Empty) + $" was checked in {Elapsed(watch)}."));
                };
            case Repair:
                return async (progress, token) =>
                    await ExfatImageMaintenance.RepairAsync(Source, Jobs.Adapt(progress), token).ConfigureAwait(false);
            case RefreshAmpr:
                return async (progress, token) =>
                    await ExfatAmprPatcher.RefreshAsync(Source, Jobs.Adapt(progress), token).ConfigureAwait(false);
            case RebuildFfpkg:
                return async (progress, token) =>
                    await Ufs2Operations.RebuildWithEditsAsync(Source, static _ => { }, Jobs.AdaptUfs2(progress), token)
                        .ConfigureAwait(false);
            case EditDetails:
                if (Edit is not { IsEmpty: false } edit) return null;
                string editCode = Passcode.Length > 0 ? Passcode : SonyDebugPackageCredentials.DefaultPasscode;
                return (progress, token) => MetadataEditor.ApplyAsync(Source, Output, FromPackage, edit, editCode,
                    Backend, progress, token);
            case SendFtp:
                if (Console is null) return null;
                ConsoleProfile ftpTarget = Console;
                string folder = RemoteFolder.Length > 0 ? RemoteFolder : ftpTarget.DefaultFolder;
                return (progress, token) => ConsoleSender.UploadAsync(ConsoleSender.WithSavedLogin(ftpTarget), Source, folder, progress, token);
            case SendInstall:
                if (Console is null) return null;
                ConsoleProfile installTarget = Console;
                return (progress, token) => ConsoleSender.InstallByUrlAsync(installTarget, Source, progress, token);
            default:
                return null;
        }
    }
}
