namespace Packwright.Core.Services;

/// <summary>
/// Finds the volume a path is stored on. On Windows that is the drive root ("D:\"). On Linux and macOS
/// <see cref="Path.GetPathRoot(string)"/> is always "/", which would measure the system disk (or, in a container, the
/// container's own file system) instead of the disk, share or mount the path is really on, so the mount point that
/// contains the path is used there.
/// </summary>
public static class Volumes
{
    /// <summary>The root of the volume that holds <paramref name="path"/>, or an empty string when it cannot be told.</summary>
    public static string RootOf(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return string.Empty; }
        if (OperatingSystem.IsWindows()) return Path.GetPathRoot(full) ?? string.Empty;

        string best = "/";
        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                string mount = drive.Name.Length > 1 ? drive.Name.TrimEnd('/') : drive.Name;
                if (mount.Length > best.Length && (full == mount || full.StartsWith(mount + "/", StringComparison.Ordinal)))
                    best = mount;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return best;
    }

    /// <summary>Free bytes on the volume that holds <paramref name="path"/>, or null when it cannot be read.</summary>
    public static long? FreeSpace(string path)
    {
        string root = RootOf(path);
        if (root.Length == 0 || root.StartsWith(@"\\", StringComparison.Ordinal)) return null;
        try { return new DriveInfo(root).AvailableFreeSpace; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }
}
