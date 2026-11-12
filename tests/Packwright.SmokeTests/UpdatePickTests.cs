using Packwright.Core.Services;

namespace Packwright.SmokeTests;

/// <summary>
/// Which release download the update window offers on each system, with no network.
/// Usage: SmokeTests update-pick
/// </summary>
internal static class UpdatePickTests
{
    private static int _failures;

    public static int Run()
    {
        var release = new ReleaseInfo("1.2.0", "v1.2.0", "Packwright v1.2.0", string.Empty, string.Empty, null,
        [
            Asset("Packwright-Setup-v1.2.0-windows-x64.exe"),
            Asset("Packwright-v1.2.0-macos-arm64.dmg"),
            Asset("Packwright-v1.2.0-macos-x64.dmg"),
            Asset("Packwright-v1.2.0-linux-x64.deb"),
            Asset("Packwright-v1.2.0-linux-x64.rpm"),
            Asset("Packwright-v1.2.0-linux-x64.pkg.tar.zst"),
            Asset("Packwright-v1.2.0-linux-x64.AppImage"),
            Asset("SHA256SUMS.txt")
        ]);
        var old = release with { Assets = release.Assets.Where(a => !a.Name.EndsWith(".zst") && !a.Name.EndsWith(".AppImage")).ToList() };

        Console.WriteLine("== Linux download per distribution (os-release, not running from an AppImage)");
        Ext("Ubuntu", "ID=ubuntu\nID_LIKE=debian\n", ".deb");
        Ext("Linux Mint", "ID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", ".deb");
        Ext("Fedora", "ID=fedora\n", ".rpm");
        Ext("openSUSE", "ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", ".rpm");
        Ext("Arch", "NAME=\"Arch Linux\"\nID=arch\n", ".pkg.tar.zst");
        Ext("Manjaro", "ID=manjaro\nID_LIKE=arch\n", ".pkg.tar.zst");
        Ext("EndeavourOS", "ID=endeavouros\nID_LIKE=arch\n", ".pkg.tar.zst");
        Ext("CachyOS", "ID=cachyos\nID_LIKE=arch\n", ".pkg.tar.zst");
        Ext("Gentoo (no package)", "ID=gentoo\n", ".AppImage");
        Ext("no os-release", string.Empty, ".AppImage");
        Check("running from an AppImage on Ubuntu -> .AppImage",
            UpdateChecker.LinuxPackageExtension("ID=ubuntu\n", "/home/me/Packwright.AppImage") == ".AppImage");

        Console.WriteLine("\n== asset picked from a release");
        Pick(release, "windows-x64", null, "Packwright-Setup-v1.2.0-windows-x64.exe");
        Pick(release, "macos-arm64", null, "Packwright-v1.2.0-macos-arm64.dmg");
        Pick(release, "linux-x64", ".deb", "Packwright-v1.2.0-linux-x64.deb");
        Pick(release, "linux-x64", ".rpm", "Packwright-v1.2.0-linux-x64.rpm");
        Pick(release, "linux-x64", ".pkg.tar.zst", "Packwright-v1.2.0-linux-x64.pkg.tar.zst");
        Pick(release, "linux-x64", ".AppImage", "Packwright-v1.2.0-linux-x64.AppImage");
        Pick(release, "linux-arm64", ".AppImage", null);
        Pick(old, "linux-x64", ".AppImage", null);   // a release from before 1.2.0: the window points to the release page
        Pick(old, "linux-x64", ".deb", "Packwright-v1.2.0-linux-x64.deb");

        Console.WriteLine(_failures == 0 ? "\nALL PASSED" : $"\n{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static ReleaseAsset Asset(string name) => new(name, "https://github.com/itsmemac/Packwright/releases/download/v1.2.0/" + name, 1);

    private static void Ext(string system, string osRelease, string expected)
    {
        string got = UpdateChecker.LinuxPackageExtension(osRelease, appImage: string.Empty);
        Check($"{system} -> {expected}{(got == expected ? string.Empty : " (got " + got + ")")}", got == expected);
    }

    private static void Pick(ReleaseInfo release, string platform, string? package, string? expected)
    {
        string? got = UpdateChecker.PickAsset(release, platform, package)?.Name;
        Check($"{platform} {package} -> {expected ?? "nothing"}{(got == expected ? string.Empty : " (got " + (got ?? "nothing") + ")")}", got == expected);
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
