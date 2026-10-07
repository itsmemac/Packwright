using Packwright.Core.Builders;
using Packwright.Core.Services;

namespace Packwright.Core.Backends;

/// <summary>
/// The package engine built into Packwright: builds packages from a folder or an image, validates them with the same
/// reader it extracts with, and extracts them. It carries no third-party engine and no keys.
/// </summary>
public sealed class PackwrightBackend : IPackageBackend
{
    public const string BackendId = "packwright";

    public string Id => BackendId;

    public string DisplayName => "Packwright";

    public BackendCapabilities Capabilities { get; } = new(
        BuildFromDirectory: true,
        BuildFromImage: true,
        Validate: true,
        Extract: true,
        SupportsLargeImages: true,
        SupportsSeed: true);

    public Task<Ps5DebugPackageBuildResult> BuildFromDirectoryAsync(string sourceDirectory, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        Ps5DebugPackageBuilder.CreateFromDirectoryAsync(sourceDirectory, outputPath, options, progress, cancellationToken);

    public Task<Ps5DebugPackageBuildResult> BuildFromImageAsync(string imagePath, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        VolumeDebugPackageBuilder.CreateFromImageAsync(imagePath, outputPath, options, progress, cancellationToken);

    public Ps5DebugPackageValidationResult Validate(string packagePath,
        string passcode = Ps5DebugPackageCredentials.DefaultPasscode) =>
        Ps5DebugPackageBuilder.Validate(packagePath, passcode);

    public Task<Ps5PackageExtractResult> ExtractAsync(string packagePath, string destination,
        string passcode = Ps5DebugPackageCredentials.DefaultPasscode,
        IProgress<Ps5PackageExtractProgress>? progress = null, CancellationToken cancellationToken = default) =>
        Ps5PackageExtraction.ExtractAsync(packagePath, destination, passcode, progress, cancellationToken);
}
