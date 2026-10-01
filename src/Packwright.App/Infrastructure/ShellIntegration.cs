using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Packwright.Infrastructure;

/// <summary>
/// Per-user (HKCU, no admin) File Explorer context menu integration for PS5 package and image files
/// and for folders. Adds an "Open with Packwright" verb that launches the app with
/// <c>--open "&lt;path&gt;"</c>. Windows only.
/// </summary>
[SupportedOSPlatform("windows")]
public static class ShellIntegration
{
    private static readonly string[] FileKeys =
    [
        @"Software\Classes\SystemFileAssociations\.pkg\shell\Packwright",
        @"Software\Classes\SystemFileAssociations\.ffpfsc\shell\Packwright",
        @"Software\Classes\SystemFileAssociations\.ffpkg\shell\Packwright",
        @"Software\Classes\SystemFileAssociations\.exfat\shell\Packwright",
        @"Software\Classes\Directory\shell\Packwright"
    ];

    private static string ExecutablePath =>
        Environment.ProcessPath ?? throw new InvalidOperationException("The application path is unknown.");

    public static bool IsInstalled()
    {
        foreach (string path in FileKeys)
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(path);
            if (key is not null) return true;
        }
        return false;
    }

    public static void Install()
    {
        string executable = ExecutablePath;
        foreach (string path in FileKeys)
        {
            using RegistryKey verb = Registry.CurrentUser.CreateSubKey(path, true)
                ?? throw new InvalidOperationException($"Unable to create {path}.");
            verb.SetValue("MUIVerb", "Open with Packwright");
            verb.SetValue("Icon", $"\"{executable}\"");
            using RegistryKey command = verb.CreateSubKey("command", true)
                ?? throw new InvalidOperationException("Unable to create the shell command key.");
            command.SetValue(null, $"\"{executable}\" --open \"%1\"");
        }
    }

    public static void Uninstall()
    {
        foreach (string path in FileKeys)
            Registry.CurrentUser.DeleteSubKeyTree(path, throwOnMissingSubKey: false);
    }
}
