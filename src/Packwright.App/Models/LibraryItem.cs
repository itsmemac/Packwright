using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media.Imaging;
using Packwright.App.Services;
using Packwright.Core.Models;

namespace Packwright.App.Models;

/// <summary>A library row: display-ready values derived from a scanned <see cref="Ps5GameInfo"/>.</summary>
public sealed class LibraryItem : INotifyPropertyChanged
{
    private Bitmap? _thumbnail;
    private bool _thumbnailRequested;
    private string _groupKey = string.Empty;
    private string _role = "Unknown";

    public LibraryItem(Ps5GameInfo game)
    {
        Game = game;
        Format = game.SourceDescription;
        SizeText = game.SourceSize > 0 ? FormatBytes(game.SourceSize) : string.Empty;
        ApplyOverride(MetadataOverrides.Get(game.RootPath));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public Ps5GameInfo Game { get; }
    public string Title { get; private set; } = string.Empty;
    public string TitleId { get; private set; } = string.Empty;
    public string ContentId { get; private set; } = string.Empty;
    public string Category { get; private set; } = string.Empty;
    public string Version { get; private set; } = string.Empty;
    public string Region { get; private set; } = string.Empty;
    public string ConceptId { get; private set; } = string.Empty;
    public string MasterVersion { get; private set; } = string.Empty;
    public string SdkVersion { get; private set; } = string.Empty;
    public string Drm { get; private set; } = string.Empty;
    public string DefaultLanguage { get; private set; } = string.Empty;
    public string Created { get; private set; } = string.Empty;
    public string ToolVersion { get; private set; } = string.Empty;
    public IReadOnlyList<string> Features { get; private set; } = [];
    /// <summary>True when the user edited this item's details.</summary>
    public bool IsEdited { get; private set; }

    /// <summary>Details the title still lacks (after the user's edits), such as "Title ID" or "Firmware".</summary>
    public IReadOnlyList<string> MissingFields { get; private set; } = [];
    public bool HasMissing => MissingFields.Count > 0;
    public string MissingText => "Missing: " + string.Join(", ", MissingFields);
    public bool HasTitleId => TitleId.Length > 0;
    public string Format { get; }
    public long SizeBytes => Game.SourceSize;
    public string SizeText { get; }
    public string Firmware { get; private set; } = string.Empty;
    public string Path => Game.RootPath;
    public string FileName =>
        System.IO.Path.GetFileName(Game.RootPath.TrimEnd(System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>Recomputes the displayed values from the file's own metadata plus the user's edits.</summary>
    public void ApplyOverride(MetadataOverride? edit)
    {
        string Pick(string custom, string detected) => string.IsNullOrWhiteSpace(custom) ? detected : custom.Trim();
        Title = Pick(edit?.Title ?? string.Empty, string.IsNullOrWhiteSpace(Game.Title) ? FileName : Game.Title);
        TitleId = Pick(edit?.TitleId ?? string.Empty, Game.TitleId);
        ContentId = Pick(edit?.ContentId ?? string.Empty, Game.ContentId);
        Version = Pick(edit?.Version ?? string.Empty, Game.DisplayVersion);
        Firmware = Pick(edit?.Firmware ?? string.Empty, Game.RequiredSystemSoftware);
        Category = Pick(edit?.Category ?? string.Empty, CategoryOf(Game));
        ConceptId = Pick(edit?.ConceptId ?? string.Empty, Game.ConceptId);
        MasterVersion = Pick(edit?.MasterVersion ?? string.Empty, Game.MasterVersion);
        SdkVersion = Pick(edit?.Sdk ?? string.Empty, Game.SdkVersion);
        Drm = Pick(edit?.Drm ?? string.Empty, Game.DrmType);
        DefaultLanguage = Pick(edit?.DefaultLanguage ?? string.Empty, Game.DefaultLanguage);
        Created = Pick(edit?.Created ?? string.Empty, Game.CreationDate);
        ToolVersion = Pick(edit?.ToolVersion ?? string.Empty, Game.ToolVersion);
        Features = edit?.Features is { } features ? features : Game.DeclaredFeatures;
        Region = RegionOfId(ContentId.Length > 0 ? ContentId : TitleId);
        IsEdited = edit is { IsEmpty: false };
        var missing = new List<string>();
        if (Game.LocalizedTitles.Count == 0 && string.IsNullOrWhiteSpace(edit?.Title)) missing.Add("Title");
        if (TitleId.Length == 0) missing.Add("Title ID");
        if (ContentId.Length == 0) missing.Add("Content ID");
        if (Version.Length == 0) missing.Add("Version");
        if (Firmware.Length == 0) missing.Add("Firmware");
        MissingFields = missing;
        _role = Category switch
        {
            "Game" => "Base",
            "Patch" => "Update",
            "DLC" or "Add-on" => "DLC",
            "App" => "App",
            _ => "Unknown"
        };
        Raise(string.Empty);
    }

    /// <summary>The item's role in its title family: Base, Update, DLC or App.</summary>
    public string Role
    {
        get => _role;
        set { if (_role != value) { _role = value; Raise(); } }
    }

    /// <summary>Key of the group this row currently sits in (empty when grouping is off).</summary>
    public string GroupKey
    {
        get => _groupKey;
        set { if (_groupKey != value) { _groupKey = value; Raise(); } }
    }

    public string Subtitle
    {
        get
        {
            var parts = new List<string>();
            if (TitleId.Length > 0) parts.Add(TitleId);
            if (Version.Length > 0) parts.Add("v" + Version.TrimStart('v', 'V'));
            return string.Join("  ·  ", parts);
        }
    }

    /// <summary>Artwork, loaded lazily the first time a view asks for it.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (!_thumbnailRequested)
            {
                _thumbnailRequested = true;
                ThumbnailService.Request(this);
            }
            return _thumbnail;
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public void SetThumbnail(Bitmap? bitmap)
    {
        _thumbnail = bitmap;
        Raise(nameof(Thumbnail));
        Raise(nameof(HasThumbnail));
    }

    /// <summary>Forgets a cached bitmap so it can be loaded again after eviction.</summary>
    public void ResetThumbnail()
    {
        _thumbnail = null;
        _thumbnailRequested = false;
        Raise(nameof(Thumbnail));
        Raise(nameof(HasThumbnail));
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string RegionOf(Ps5GameInfo game) =>
        RegionOfId(!string.IsNullOrWhiteSpace(game.ContentId) ? game.ContentId : game.TitleId);

    /// <summary>The regions a Content ID can name, with the two letters it starts with.</summary>
    public static readonly (string Name, string Prefix)[] Regions =
    [
        ("Americas", "UP"), ("Europe", "EP"), ("Japan", "JP"), ("Korea", "KP"), ("Asia", "AP"), ("Hong Kong", "HP")
    ];

    public static string RegionOfId(string id)
    {
        if (id.Length < 1) return "Unknown";
        return char.ToUpperInvariant(id[0]) switch
        {
            'U' => "Americas",
            'E' => "Europe",
            'J' => "Japan",
            'K' => "Korea",
            'A' => "Asia",
            'H' => "Hong Kong",
            _ => "Other"
        };
    }

    public static string CategoryOf(Ps5GameInfo game)
    {
        string raw = game.ApplicationCategory;
        if (!string.IsNullOrWhiteSpace(raw))
        {
            string token = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? raw;
            if (int.TryParse(token, out int number))
                return number switch { 1 => "DLC", 2 => "Patch", 3 => "App", _ => "Game" };
            return raw;
        }
        return game.Package?.ContentType switch
        {
            0x20 => "Game",
            0x21 => "DLC",
            0x22 => "DLC",
            _ => game.SourceKind == Ps5SourceKind.LooseDump ? "Game" : "Unknown"
        };
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
}
