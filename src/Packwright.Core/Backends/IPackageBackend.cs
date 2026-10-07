using Packwright.Core.Builders;
using Packwright.Core.Services;

namespace Packwright.Core.Backends;

/// <summary>
/// The PS5 package engine: builds, validates and extracts packages. The engine that builds a package is also the one
/// that later validates and extracts it, so the reader always matches the writer.
/// </summary>
public interface IPackageBackend
{
    /// <summary>Stable id used in settings.</summary>
    string Id { get; }

    /// <summary>Human-readable name shown in the backend selector.</summary>
    string DisplayName { get; }

    /// <summary>What this backend can do.</summary>
    BackendCapabilities Capabilities { get; }

    /// <summary>Builds a package from an unpacked folder (a dump/app tree).</summary>
    Task<Ps5DebugPackageBuildResult> BuildFromDirectoryAsync(string sourceDirectory, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Builds a package from an image source (exFAT / FFPKG / FFPFSC).</summary>
    Task<Ps5DebugPackageBuildResult> BuildFromImageAsync(string imagePath, string outputPath,
        Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress = null,
        CancellationToken cancellationToken = default);

    /// <summary>Validates a package with this backend's own reader.</summary>
    Ps5DebugPackageValidationResult Validate(string packagePath,
        string passcode = Ps5DebugPackageCredentials.DefaultPasscode);

    /// <summary>Extracts a package's filesystem to <paramref name="destination"/>.</summary>
    Task<Ps5PackageExtractResult> ExtractAsync(string packagePath, string destination,
        string passcode = Ps5DebugPackageCredentials.DefaultPasscode,
        IProgress<Ps5PackageExtractProgress>? progress = null, CancellationToken cancellationToken = default);
}
