using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

/// <summary>
/// User-supplied details for one library item (a package, image or dump). Empty fields mean "use what the
/// file says". The file itself is never modified.
/// </summary>
public sealed class MetadataOverride
{
    public string Title { get; set; } = string.Empty;
    public string TitleId { get; set; } = string.Empty;
    public string ContentId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    /// <summary>Game, Patch, DLC or App. Empty keeps the detected category.</summary>
    public string Category { get; set; } = string.Empty;
    public string ConceptId { get; set; } = string.Empty;
    public string MasterVersion { get; set; } = string.Empty;
    public string Sdk { get; set; } = string.Empty;
    public string Drm { get; set; } = string.Empty;
    public string DefaultLanguage { get; set; } = string.Empty;
    public string Created { get; set; } = string.Empty;
    public string ToolVersion { get; set; } = string.Empty;
    /// <summary>The declared features (HDR, VRR, ...). Null keeps what the file declares; an empty list means none.</summary>
    public List<string>? Features { get; set; }
    /// <summary>Name of the custom icon file inside the overrides folder (empty = none).</summary>
    public string IconFile { get; set; } = string.Empty;

    public bool IsEmpty =>
        Title.Length == 0 && TitleId.Length == 0 && ContentId.Length == 0 && Version.Length == 0 &&
        Firmware.Length == 0 && Category.Length == 0 && IconFile.Length == 0 && ConceptId.Length == 0 &&
        MasterVersion.Length == 0 && Sdk.Length == 0 && Drm.Length == 0 && DefaultLanguage.Length == 0 &&
        Created.Length == 0 && ToolVersion.Length == 0 && Features is null;
}

/// <summary>Per-item detail overrides, saved next to the settings so they survive rescans and restarts.</summary>
public static class MetadataOverrides
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object Gate = new();
    private static Dictionary<string, MetadataOverride>? _items;

    private static string Folder => Path.Combine(AppServices.Instance.Store.AppDataDirectory, "overrides");
    private static string FilePath => Path.Combine(AppServices.Instance.Store.AppDataDirectory, "metadata-overrides.json");

    private static Dictionary<string, MetadataOverride> Items
    {
        get
        {
            if (_items is not null) return _items;
            var loaded = new Dictionary<string, MetadataOverride>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(FilePath))
                    loaded = new Dictionary<string, MetadataOverride>(
                        JsonSerializer.Deserialize<Dictionary<string, MetadataOverride>>(File.ReadAllText(FilePath)) ?? [],
                        StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                Logger.Warn("Edited details could not be read: " + ex.Message);
            }
            return _items = loaded;
        }
    }

    public static MetadataOverride? Get(string path)
    {
        lock (Gate) return Items.TryGetValue(path, out MetadataOverride? value) ? value : null;
    }

    /// <summary>Saves (or, when empty, removes) the override for <paramref name="path"/>.</summary>
    public static void Set(string path, MetadataOverride value)
    {
        lock (Gate)
        {
            if (value.IsEmpty) Items.Remove(path);
            else Items[path] = value;
            try
            {
                File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn("Edited details could not be saved: " + ex.Message);
            }
        }
    }

    public static string? IconPath(MetadataOverride? value)
    {
        if (value is null || value.IconFile.Length == 0) return null;
        string path = Path.Combine(Folder, value.IconFile);
        return File.Exists(path) ? path : null;
    }

    /// <summary>Copies an image into the overrides folder and returns the stored file name.</summary>
    public static string StoreIcon(string itemPath, byte[] imageBytes)
    {
        Directory.CreateDirectory(Folder);
        string name = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(itemPath.ToLowerInvariant())))[..24] +
                      "-" + DateTime.UtcNow.Ticks + ".png";
        File.WriteAllBytes(Path.Combine(Folder, name), imageBytes);
        return name;
    }

    public static void DeleteIcon(string? fileName)
    {
        if (string.IsNullOrEmpty(fileName)) return;
        try { File.Delete(Path.Combine(Folder, fileName)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
