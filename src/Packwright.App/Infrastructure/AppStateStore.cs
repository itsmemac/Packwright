using System.Text.Json;
using Packwright.Core.Models;

namespace Packwright.Infrastructure;

/// <summary>A named snapshot of the library layout: filters, grouping, sorting and columns.</summary>
public sealed class SavedLibraryView
{
    public string Name { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public List<string> Categories { get; set; } = [];
    public List<string> Regions { get; set; } = [];
    public List<string> Formats { get; set; } = [];
    public string GroupBy { get; set; } = string.Empty;
    public List<string> SortKeys { get; set; } = [];
    public List<string> HiddenColumns { get; set; } = [];
    public List<string> ColumnOrder { get; set; } = [];
}

/// <summary>A named set of Image tools options (target format and its settings). The source and output paths are never part of it.</summary>
public sealed class ToolPreset
{
    public string Name { get; set; } = string.Empty;
    /// <summary>Exfat, Ffpkg, Ffpfsc, ZArchive or Fpkg; empty keeps whatever is chosen.</summary>
    public string Target { get; set; } = string.Empty;
    public int ClusterIndex { get; set; }
    public bool GenerateAmpr { get; set; } = true;
    public int Level { get; set; } = 7;
    public int Gain { get; set; } = 1;
    public int BlockIndex { get; set; }
    public int FragmentIndex { get; set; }
    public int DensityIndex { get; set; }
    public int MinFree { get; set; }
    public string Backend { get; set; } = string.Empty;
    public int CompressionIndex { get; set; }
    public int KrakenLevel { get; set; } = 7;
    public int KrakenThreads { get; set; }
    public int PlayGoChunks { get; set; } = 1;
    public bool Deterministic { get; set; }
    public bool FakeSign { get; set; } = true;
    public bool RightSprx { get; set; } = true;
    public int DrmIndex { get; set; }
    public string Sdk { get; set; } = string.Empty;
    public string TempDirectory { get; set; } = string.Empty;
}

/// <summary>A console on the local network that files can be sent to. Nothing is contacted until the user sends something.</summary>
public sealed class ConsoleProfile
{
    public string Name { get; set; } = string.Empty;
    /// <summary>The console's address on the local network, such as 192.168.1.50.</summary>
    public string Host { get; set; } = string.Empty;
    public int FtpPort { get; set; } = 1337;
    /// <summary>Where files go on the console unless another folder is chosen when sending.</summary>
    public string DefaultFolder { get; set; } = "/data";

    // "Install by URL": the console's installer is asked to download a package from this PC. Different installers
    // use different requests, so the request is a template. {host} is the console, {url} the address of the file.
    public string InstallUrl { get; set; } = "http://{host}:12800/api/install";
    public string InstallBody { get; set; } = "{\"type\":\"direct\",\"packages\":[\"{url}\"]}";
    public string InstallContentType { get; set; } = "application/json";
}

public sealed class AppSettings
{
    public List<string> LibraryFolders { get; set; } = [];
    public bool RecursiveScan { get; set; } = true;
    public List<string> RecentFolders { get; set; } = [];
    public List<string> ManualSources { get; set; } = [];
    public int WindowWidth { get; set; }
    public int WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public bool WindowHasPosition { get; set; }
    public int WindowX { get; set; }
    public int WindowY { get; set; }
    public string LibrarySortColumn { get; set; } = "Title";
    public bool LibrarySortAscending { get; set; } = true;
    /// <summary>Ordered sort keys ("Title:asc", "Size:desc") for multi-column sorting.</summary>
    public List<string> LibrarySortKeys { get; set; } = [];
    /// <summary>Named saved library views (filters, grouping, sorting and column layout).</summary>
    public List<SavedLibraryView> SavedViews { get; set; } = [];
    /// <summary>Named Image tools presets saved by the user.</summary>
    public List<ToolPreset> Presets { get; set; } = [];
    /// <summary>Consoles that files can be sent to.</summary>
    public List<ConsoleProfile> Consoles { get; set; } = [];
    public List<string> LibraryColumnOrder { get; set; } = [];
    public List<string> LibraryHiddenColumns { get; set; } = [];
    /// <summary>Remembered column fill weights by column name so user-resized widths survive a restart.</summary>
    public Dictionary<string, float> LibraryColumnWeights { get; set; } = [];

    // Appearance
    public string Theme { get; set; } = "Default (Charcoal)";
    /// <summary>Library presentation: "Cards" (artwork tiles) or "List".</summary>
    public string LibraryLayout { get; set; } = "Cards";
    public int GridRowHeight { get; set; } = 22;
    public bool ShowThumbnails { get; set; } = true;
    public bool ShowGridLines { get; set; } = true;
    /// <summary>Show the inline file preview pane in the Files tab (list-only when disabled).</summary>
    public bool ShowFilePreview { get; set; } = true;
    public string DefaultGroupBy { get; set; } = string.Empty;

    // Library & scanning
    public bool RefreshOnStartup { get; set; }
    public string RenameFormat { get; set; } = "{TITLE} [{TITLE_ID}]";
    public bool LogAutoScroll { get; set; } = true;

    // Files & preview
    public int MaxPreviewMb { get; set; } = 16;
    public int HexPageKb { get; set; } = 16;

    // Performance
    public int ThumbnailCacheCount { get; set; } = 512;

    // Paths & outputs
    public string OutputDirectory { get; set; } = string.Empty;
    public bool OpenOutputAfterTask { get; set; }

    // Tasks workspace
    /// <summary>Remembered vertical split between the task list and the details panel.</summary>
    public int TaskSplitterDistance { get; set; }
    /// <summary>True when the details panel is collapsed to give the list more height.</summary>
    public bool TaskDetailsCollapsed { get; set; }

    // Build defaults
    public string DebugPasscode { get; set; } = string.Empty;

    /// <summary>Selected package backend id ("lpp" or "ppt"). Empty/unknown falls back to the default.</summary>
    public string BuildBackend { get; set; } = string.Empty;

    // Updates (nothing is checked until the user allows it)
    public bool CheckForUpdates { get; set; }
    public bool UpdateCheckAsked { get; set; }
    public DateTime LastUpdateCheckUtc { get; set; }
    public string SkippedUpdateVersion { get; set; } = string.Empty;

    // Safety
    public bool ConfirmDelete { get; set; } = true;
    public bool ConfirmMove { get; set; } = true;
    public bool PermanentDelete { get; set; }
}

public sealed class LibraryManifest
{
    public DateTime CreatedUtc { get; set; }
    public List<Ps5GameInfo> Games { get; set; } = [];
}

/// <summary>Result of loading settings, including a non-fatal warning worth surfacing to the user.</summary>
public sealed record SettingsLoadResult(AppSettings Settings, string? Warning);

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public AppStateStore()
    {
        // PACKWRIGHT_DATA lets a test copy use its own folder instead of the real settings and library.
        string? custom = Environment.GetEnvironmentVariable("PACKWRIGHT_DATA");
        AppDataDirectory = !string.IsNullOrWhiteSpace(custom)
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packwright");
        Directory.CreateDirectory(AppDataDirectory);
    }

    public string AppDataDirectory { get; }
    public string SettingsPath => Path.Combine(AppDataDirectory, "settings.json");
    public string ManifestPath => Path.Combine(AppDataDirectory, "manifest.json");

    public AppSettings LoadSettings() => LoadSettingsWithDiagnostics().Settings;

    /// <summary>
    /// Loads and normalizes settings. A missing file simply yields defaults; an unreadable file is
    /// quarantined (renamed) rather than silently overwritten by a later automatic save, and a warning
    /// describing what happened is returned.
    /// </summary>
    public SettingsLoadResult LoadSettingsWithDiagnostics()
    {
        if (!File.Exists(SettingsPath)) return new SettingsLoadResult(new AppSettings(), null);
        try
        {
            AppSettings settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath), JsonOptions)
                ?? new AppSettings();
            return new SettingsLoadResult(AppSettingsNormalizer.Normalize(settings), null);
        }
        catch (JsonException ex)
        {
            string quarantine = SettingsPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(SettingsPath, quarantine, overwrite: true); }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) { }
            return new SettingsLoadResult(new AppSettings(),
                $"The settings file could not be read and was moved to {Path.GetFileName(quarantine)}. " +
                $"Defaults are in use. ({ex.Message})");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(new AppSettings(),
                $"Settings could not be read: {ex.Message}. Defaults are in use for this session.");
        }
    }

    public LibraryManifest LoadManifest()
    {
        if (!File.Exists(ManifestPath)) return new LibraryManifest();
        try
        {
            LibraryManifest manifest = JsonSerializer.Deserialize<LibraryManifest>(
                File.ReadAllText(ManifestPath), JsonOptions) ?? new LibraryManifest();
            manifest.Games ??= [];
            return manifest;
        }
        catch (JsonException ex)
        {
            // Quarantine rather than silently presenting an empty library (which looks like data
            // loss) and then overwriting the file on the next save.
            string quarantine = ManifestPath + ".invalid-" + DateTime.Now.ToString("yyyyMMddHHmmss");
            try { File.Move(ManifestPath, quarantine, overwrite: true); }
            catch (Exception moveEx) when (moveEx is IOException or UnauthorizedAccessException) { }
            Logger.Warn($"The library manifest could not be read and was moved to " +
                $"{Path.GetFileName(quarantine)}. The library will be rebuilt on the next refresh. ({ex.Message})");
            return new LibraryManifest();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn($"The library manifest could not be read: {ex.Message}. " +
                "The library may appear empty until a refresh.");
            return new LibraryManifest();
        }
    }

    public void SaveSettings(AppSettings settings) => Save(SettingsPath, AppSettingsNormalizer.Normalize(settings));
    public void SaveManifest(IReadOnlyCollection<Ps5GameInfo> games) => Save(ManifestPath, new LibraryManifest
    {
        CreatedUtc = DateTime.UtcNow,
        Games = games.ToList()
    });

    private static void Save<T>(string path, T value)
    {
        string tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(value, JsonOptions));
        File.Move(tempPath, path, true);
    }
}
