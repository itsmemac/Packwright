namespace Packwright.Core.Backends;

/// <summary>The package engine the app uses. There is one: the engine built into Packwright.</summary>
public static class BackendRegistry
{
    public const string DefaultId = PackwrightBackend.BackendId;

    public static IReadOnlyList<IPackageBackend> All { get; } = [new PackwrightBackend()];

    /// <summary>The package engine.</summary>
    public static IPackageBackend Default => All[0];

    /// <summary>Resolves a backend by id. Settings written by older versions name engines that no longer exist; they resolve to the built-in one.</summary>
    public static IPackageBackend Get(string? id) => Default;

    /// <summary>True when <paramref name="id"/> names the built-in engine.</summary>
    public static bool IsKnown(string? id) =>
        string.Equals(id, PackwrightBackend.BackendId, StringComparison.OrdinalIgnoreCase);
}
