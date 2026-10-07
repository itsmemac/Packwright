namespace Packwright.Core.Services;

/// <summary>
/// The known Prospero application SDK releases, surfaced for the UI. A release id is the full 64-bit executable SDK
/// identifier: the major and minor version in binary-coded decimal in the top two bytes, a build number below.
/// </summary>
public static class Ps5SdkVersions
{
    public readonly record struct Release(int Major, string Version, ulong ExecutableVersion);

    /// <summary>Known SDK releases in ascending major order.</summary>
    public static IReadOnlyList<Release> Releases { get; } =
    [
        new(1, "1.00.00.50", 72057937635311617UL),
        new(2, "2.00.00.26", 144115351284613121UL),
        new(3, "3.00.00.27", 216172949617508881UL),
        new(4, "4.00.00.31", 288230586605109777UL),
        new(5, "5.00.00.33", 360288189232971777UL),
        new(6, "6.00.00.38", 432345804745736193UL),
        new(7, "7.00.00.38", 504403398783664913UL),
        new(8, "8.00.00.41", 576461031476297729UL),
        new(9, "9.00.00.40", 648518621219259409UL),
        new(10, "10.00.00.40", 1152921779484753921UL),
        new(11, "11.00.00.40", 1224979373522681857UL),
    ];

    /// <summary>The executable SDK id at <paramref name="index"/>, or null when out of range.</summary>
    public static ulong? ExecutableVersionAt(int index) =>
        index >= 0 && index < Releases.Count ? Releases[index].ExecutableVersion : null;
}
