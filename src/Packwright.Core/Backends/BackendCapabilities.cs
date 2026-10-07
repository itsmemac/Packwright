namespace Packwright.Core.Backends;

/// <summary>What a <see cref="IPackageBackend"/> can do. The UI uses this to gate actions and options.</summary>
public sealed record BackendCapabilities(
    bool BuildFromDirectory,
    bool BuildFromImage,
    bool Validate,
    bool Extract,
    bool SupportsLargeImages,
    bool SupportsSeed);
