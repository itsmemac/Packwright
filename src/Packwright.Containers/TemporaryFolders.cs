namespace Packwright.Containers;

/// <summary>
/// Where large temporary work files go (an image unpacked to a folder, a copy of an image being edited). By default
/// that is the system temporary folder; another folder can be chosen, or the files can be kept next to the output, so
/// a big conversion does not fill the system drive.
/// </summary>
public static class TemporaryFolders
{
    private sealed record Choice(string Root, bool NextToOutput);

    private static volatile Choice _choice = new(string.Empty, false);

    /// <summary>The chosen folder, or empty for the system temporary folder.</summary>
    public static string Root => _choice.Root;

    /// <summary>True when work files are kept next to the output instead.</summary>
    public static bool NextToOutput => _choice.NextToOutput;

    /// <summary>Sets where later jobs write their work files. A job that is already running keeps its folder.</summary>
    public static void Configure(string? root, bool nextToOutput)
    {
        string folder = string.IsNullOrWhiteSpace(root) ? string.Empty : root.Trim();
        if (folder.Length > 0)
        {
            try { folder = Path.GetFullPath(folder); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { }
        }
        _choice = new Choice(folder, nextToOutput);
    }

    /// <summary>
    /// Creates a new, empty folder for one job and returns it. The caller deletes that folder when done; the chosen
    /// folder itself is never removed. <paramref name="outputPath"/> is the file or folder the job writes, used when
    /// work files are kept next to the output.
    /// </summary>
    public static string Create(string purpose, string outputPath)
    {
        Choice choice = _choice;
        string id = Guid.NewGuid().ToString("N");
        string? beside = choice.NextToOutput ? Path.GetDirectoryName(Path.GetFullPath(outputPath)) : null;
        string folder = !string.IsNullOrEmpty(beside)
            ? Path.Combine(beside, $".Packwright-{purpose}-{id}")
            : Path.Combine(choice.Root.Length > 0 ? choice.Root : Path.GetTempPath(), "Packwright", purpose, id);
        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Never fall back to the system drive on its own: running out of room there is what the setting avoids.
            throw new IOException($"The temporary folder could not be created: {folder}. {ex.Message.Trim()} " +
                                  "Choose another temporary folder.", ex);
        }
        return folder;
    }
}
