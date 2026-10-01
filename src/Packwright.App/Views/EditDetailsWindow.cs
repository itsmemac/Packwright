using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Packwright.App.Models;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.App.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public enum EditTarget { Library, InPlace, NewCopy }

/// <summary>What the dialog decided: the values to write and where. Empty output means in place.</summary>
public sealed record EditOutcome(EditTarget Target, MetadataEdit? Edit, string Output);

/// <summary>
/// Lets the user fill in or correct what the library shows for an item: title, IDs, version, required
/// firmware, category and icon. They can be kept in the library only, or written into the file itself
/// (or a new copy of it).
/// </summary>
public sealed class EditDetailsWindow : Window
{
    private static readonly string[] Categories = ["(detected)", "Game", "Patch", "DLC", "App"];
    private static readonly string[] DrmTypes = ["standard", "upgradable", "free"];
    private static readonly string[] Languages =
    [
        "en-US", "en-GB", "ja-JP", "fr-FR", "fr-CA", "es-ES", "es-419", "de-DE", "it-IT", "nl-NL", "pt-PT", "pt-BR", "ru-RU",
        "ko-KR", "zh-Hant", "zh-Hans", "fi-FI", "sv-SE", "da-DK", "no-NO", "pl-PL", "tr-TR", "ar-AE", "cs-CZ", "hu-HU",
        "el-GR", "ro-RO", "th-TH", "vi-VN", "id-ID"
    ];

    private readonly LibraryItem _item;
    private readonly MetadataOverride _edit;
    private readonly TextBox _title = new();
    private readonly TextBox _titleId = new();
    private readonly TextBox _contentId = new();
    private readonly TextBox _version = new();
    private readonly TextBox _firmware = new();
    private readonly TextBox _concept = new();
    private readonly TextBox _master = new();
    private readonly TextBox _sdk = new();
    private readonly TextBox _created = new() { Watermark = "For example 2025-03-14 12:00:00" };
    private readonly TextBox _toolVersion = new();
    private readonly ComboBox _region = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _drm = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true, ItemsSource = DrmTypes };
    private readonly ComboBox _language = new() { HorizontalAlignment = HorizontalAlignment.Stretch, IsEditable = true, ItemsSource = Languages };
    private readonly List<CheckBox> _features = [];
    private bool _syncingRegion;
    private readonly ComboBox _category = new() { ItemsSource = Categories, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly Image _preview = new() { Stretch = Stretch.UniformToFill };
    private readonly TextBlock _iconNote = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private readonly ComboBox _mode = new()
    {
        ItemsSource = new[]
        {
            "Library only (the file is not changed)",
            "Write into the file itself",
            "Write into a new copy of the file..."
        },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBox _query = new() { Watermark = "Or search by game name" };
    private readonly Button _searchOnline = new() { Content = "Search" };
    private readonly Button _searchById = new() { Content = "Search PlayStation Network" };
    private Expander? _onlineExpander;
    private readonly Button _useMatch = new() { Content = "Use this match", IsEnabled = false };
    private readonly ListBox _results = new() { MaxHeight = 170 };
    private readonly TextBlock _onlineNote = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly Button _save;
    private readonly Button _autofill;
    private readonly TextBlock _autofillNote = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
    private readonly TextBlock _modeNote = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private byte[]? _newIcon;
    private bool _removeIcon;
    private EditOutcome? _outcome;

    private EditDetailsWindow(LibraryItem item)
    {
        _item = item;
        MetadataOverride existing = MetadataOverrides.Get(item.Path) ?? new MetadataOverride();
        _edit = new MetadataOverride
        {
            Title = existing.Title, TitleId = existing.TitleId, ContentId = existing.ContentId,
            Version = existing.Version, Firmware = existing.Firmware, Category = existing.Category,
            IconFile = existing.IconFile, ConceptId = existing.ConceptId, MasterVersion = existing.MasterVersion,
            Sdk = existing.Sdk, Drm = existing.Drm, DefaultLanguage = existing.DefaultLanguage, Created = existing.Created,
            ToolVersion = existing.ToolVersion, Features = existing.Features?.ToList()
        };

        Title = "Edit details";
        Width = 1060;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var game = item.Game;
        Fill(_title, _edit.Title, string.IsNullOrWhiteSpace(game.Title) ? item.FileName : game.Title);
        Fill(_titleId, _edit.TitleId, game.TitleId);
        Fill(_contentId, _edit.ContentId, game.ContentId);
        Fill(_version, _edit.Version, game.DisplayVersion);
        Fill(_firmware, _edit.Firmware, game.RequiredSystemSoftware);
        Fill(_concept, _edit.ConceptId, game.ConceptId);
        Fill(_master, _edit.MasterVersion, game.MasterVersion);
        Fill(_sdk, _edit.Sdk, game.SdkVersion);
        Fill(_created, _edit.Created, game.CreationDate);
        Fill(_toolVersion, _edit.ToolVersion, game.ToolVersion);
        _drm.Text = _edit.Drm;
        _drm.PlaceholderText = string.IsNullOrWhiteSpace(game.DrmType) ? "(not available)" : game.DrmType;
        _language.Text = _edit.DefaultLanguage;
        _language.PlaceholderText = string.IsNullOrWhiteSpace(game.DefaultLanguage) ? "(not available)" : game.DefaultLanguage;
        int category = Array.FindIndex(Categories, c => string.Equals(c, _edit.Category, StringComparison.OrdinalIgnoreCase));
        _category.SelectedIndex = category < 0 ? 0 : category;
        _region.ItemsSource = LibraryItem.Regions.Select(region => $"{region.Name} ({region.Prefix})").ToList();
        _contentId.TextChanged += (_, _) => SyncRegionFromContentId();
        _region.SelectionChanged += (_, _) => ApplyRegionToContentId();
        SyncRegionFromContentId();

        var choose = new Button { Content = "Choose image..." };
        choose.Click += async (_, _) => await ChooseIconAsync();
        var remove = new Button { Content = "Use original icon" };
        remove.Click += (_, _) => { _newIcon = null; _removeIcon = true; ShowPreview(null); };

        var iconFrame = new Border
        {
            Width = 112, Height = 112, CornerRadius = new CornerRadius(16), ClipToBounds = true,
            Background = Brushes.Black, Child = _preview
        };
        _save = new Button { Content = "Save in library", MinWidth = 130 };
        _save.Classes.Add("accent");
        _save.Click += (_, _) => Save();
        var save = _save;
        var reset = new Button { Content = "Reset all" };
        reset.Click += (_, _) => ResetAll();
        _searchById.Classes.Add("accent");
        _searchById.Click += async (_, _) => await SearchByIdAsync();
        ToolTip.SetTip(_searchById, "Looks this title's Title ID up in a public list of PS5 titles, then shows the matching entries to pick from.");
        _autofill = new Button { Content = "Autofill missing" };
        _autofill.Click += async (_, _) => await AutofillAsync();
        ToolTip.SetTip(_autofill, "Fill what is missing from the file's own data and its file or folder name. Nothing is sent anywhere.");
        var cancel = new Button { Content = "Cancel", MinWidth = 90 };
        cancel.Click += (_, _) => Close();

        // Two columns of fields so the window is wide rather than tall.
        var form = new Grid { ColumnDefinitions = new ColumnDefinitions("125,*,24,125,*"), RowSpacing = 10 };
        int row = -1, slot = 0;   // slot 0 = left pair, 1 = right pair
        void Add(string label, Control control, bool wide = false)
        {
            if (wide || slot == 0)
            {
                row++;
                form.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                slot = 0;
            }
            int labelColumn = wide || slot == 0 ? 0 : 3;
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Classes = { "muted" } };
            Grid.SetRow(text, row);
            Grid.SetColumn(text, labelColumn);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, labelColumn + 1);
            if (wide) Grid.SetColumnSpan(control, 4);
            form.Children.Add(text);
            form.Children.Add(control);
            slot = wide ? 0 : 1 - slot;
        }
        Add("Title", _title, wide: true);
        Add("Title ID", _titleId);
        Add("Concept ID", _concept);
        Add("Content ID", _contentId, wide: true);
        Add("Region", _region);
        Add("Category", _category);
        Add("Version", _version);
        Add("Master version", _master);
        Add("Required firmware", _firmware);
        Add("SDK version", _sdk);
        Add("DRM", _drm);
        Add("Default language", _language);
        Add("Created", _created);
        Add("Tool version", _toolVersion);
        Add("Features", BuildFeatures(game), wide: true);
        Add("Apply to", _mode, wide: true);
        _mode.SelectionChanged += (_, _) => UpdateModeNote();
        UpdateModeNote();

        choose.HorizontalAlignment = remove.HorizontalAlignment = HorizontalAlignment.Stretch;
        choose.HorizontalContentAlignment = remove.HorizontalContentAlignment = HorizontalAlignment.Center;
        _searchById.HorizontalAlignment = _autofill.HorizontalAlignment = HorizontalAlignment.Stretch;
        _searchById.HorizontalContentAlignment = _autofill.HorizontalContentAlignment = HorizontalAlignment.Center;
        iconFrame.Width = iconFrame.Height = 150;
        iconFrame.HorizontalAlignment = HorizontalAlignment.Center;
        _iconNote.TextAlignment = TextAlignment.Center;
        var side = new StackPanel
        {
            Spacing = 8, Width = 230,
            Children =
            {
                iconFrame, choose, remove, _iconNote,
                new Border { Height = 1, Margin = new Thickness(0, 6), Background = Brushes.Gray, Opacity = 0.3 },
                _searchById, _autofill, _autofillNote
            }
        };
        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("230,28,*") };
        Grid.SetColumn(form, 2);
        body.Children.Add(side);
        body.Children.Add(form);

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock
                {
                    Text = item.FileName, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis
                },
                new TextBlock
                {
                    Text = "Leave a field empty to keep what the file reports (shown greyed out).",
                    Classes = { "muted" }, TextWrapping = TextWrapping.Wrap
                },
                body,
                BuildOnlineSection(),
                _modeNote,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                    Children =
                    {
                        reset,
                        new StackPanel
                        {
                            [Grid.ColumnProperty] = 1, Orientation = Orientation.Horizontal, Spacing = 8, Children = { save, cancel }
                        }
                    }
                }
            }
        };

        // Taller than the screen on small displays? Scroll instead of cutting the buttons off.
        Control inner = (Control)Content!;
        Content = null;
        Content = new ScrollViewer { Content = inner, VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Opened += (_, _) =>
        {
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } screen)
            {
                double scale = RenderScaling > 0 ? RenderScaling : 1;
                MaxHeight = Math.Max(480, screen.WorkingArea.Height / scale - 40);
                MaxWidth = Math.Max(640, screen.WorkingArea.Width / scale - 40);
            }
        };

        ShowPreview(MetadataOverrides.IconPath(_edit) is { } path ? new Bitmap(path) : item.Thumbnail,
            MetadataOverrides.IconPath(_edit) is not null);
    }

    /// <summary>Shows the dialog. Returns null when cancelled.</summary>
    public static async Task<EditOutcome?> ShowAsync(Window? owner, LibraryItem item)
    {
        var window = new EditDetailsWindow(item);
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return window._outcome;
    }

    /// <summary>The "Find online" section: search the PlayStation Store by name and pick the match.</summary>
    private Control BuildOnlineSection()
    {
        string guess = Ps5MetadataSuggester.CleanName(_item.Title);
        _query.Text = guess.Length > 0 ? guess : Ps5MetadataSuggester.CleanName(Path.GetFileNameWithoutExtension(_item.FileName));
        _query.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            e.Handled = true;
            _ = SearchOnlineAsync();
        };
        _searchOnline.Click += async (_, _) => await SearchOnlineAsync();
        _useMatch.Click += async (_, _) => await UseMatchAsync();
        _results.SelectionChanged += (_, _) => _useMatch.IsEnabled = _results.SelectedItem is StoreResult;
        _results.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<StoreResult>((result, _) =>
            result is null
                ? new TextBlock()
                : new StackPanel
                {
                    Spacing = 1, Margin = new Thickness(2, 4),
                    Children =
                    {
                        new TextBlock { Text = result.Name, FontWeight = FontWeight.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis },
                        new TextBlock
                        {
                            Text = $"{result.TitleId}  ·  {result.Region}  ·  {result.Publisher}", Classes = { "muted" }, FontSize = 11
                        }
                    }
                });

        var searchRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(_searchOnline, 1);
        searchRow.Children.Add(_query);
        searchRow.Children.Add(_searchOnline);
        _onlineNote.Text = "Press \"Search PlayStation Network\" above to look up this title's Title ID. Or search by name here: " +
                           "only the name above is sent to the PlayStation Store, and only when you press Search.";
        var panel = new StackPanel
        {
            Spacing = 8, Margin = new Thickness(0, 6, 0, 0),
            Children = { searchRow, _results, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _useMatch } }, _onlineNote }
        };
        _onlineExpander = new Expander
        {
            Header = "Results, and search by game name", Content = panel, IsExpanded = false,
            HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        return _onlineExpander;
    }

    /// <summary>The Title ID to look up: what is typed, else what the file or its name shows.</summary>
    private async Task<string> KnownTitleIdAsync()
    {
        string typed = (_titleId.Text ?? string.Empty).Trim();
        if (typed.Length > 0) return typed;
        if (_item.TitleId.Length > 0) return _item.TitleId;
        try { return (await Task.Run(() => Ps5MetadataSuggester.Suggest(_item.Game))).TitleId; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException) { return string.Empty; }
    }

    /// <summary>Looks the title's Title ID up in the public title list and shows the matching entries.</summary>
    private async Task SearchByIdAsync()
    {
        if (_onlineExpander is not null) _onlineExpander.IsExpanded = true;
        _searchById.IsEnabled = false;
        _searchOnline.IsEnabled = false;
        _useMatch.IsEnabled = false;
        try
        {
            string id = await KnownTitleIdAsync();
            if (id.Length == 0)
            {
                _onlineNote.Text = "No Title ID could be found for this file, so there is nothing to look up. " +
                                   "Type the game's name in the box and press Search instead.";
                return;
            }
            string cache = AppServices.Instance.Store.AppDataDirectory;
            _onlineNote.Text = Ps5TitleDatabase.IsCached(cache)
                ? $"Looking up {id}..."
                : $"Downloading the public PS5 title list (about 2 MB, once) and looking up {id}...";
            IReadOnlyList<DbTitle> rows = await Ps5TitleDatabase.LookupAsync(id, cache);
            var results = rows.Select(row => new StoreResult(row.Name, row.ContentId, row.TitleId, row.PublisherId,
                string.Empty, string.Empty, string.Empty, 1.0)).ToList();
            _results.ItemsSource = results;
            if (results.Count > 0) _results.SelectedIndex = 0;
            _onlineNote.Text = results.Count == 0
                ? $"{id} is not in the public title list (new or unusual titles can be missing). Try searching by name."
                : $"Found {results.Count:N0} entr{(results.Count == 1 ? "y" : "ies")} for {id}. Select the right one and press \"Use this match\".";
        }
        catch (OnlineLookupException ex)
        {
            _onlineNote.Text = ex.Message;
        }
        finally
        {
            _searchById.IsEnabled = true;
            _searchOnline.IsEnabled = true;
        }
    }

    private async Task SearchOnlineAsync()
    {
        string name = _query.Text?.Trim() ?? string.Empty;
        if (name.Length < 2)
        {
            _onlineNote.Text = "Type the game's name first.";
            return;
        }
        string known = (_titleId.Text ?? string.Empty).Trim();
        if (known.Length == 0) known = _item.TitleId;
        _searchOnline.IsEnabled = false;
        _useMatch.IsEnabled = false;
        _onlineNote.Text = "Searching the PlayStation Store...";
        try
        {
            IReadOnlyList<StoreResult> results = await Ps5StoreLookup.FindAsync(name, known);
            _results.ItemsSource = results;
            if (results.Count > 0) _results.SelectedIndex = 0;
            _onlineNote.Text = results.Count == 0
                ? $"No PS5 game named \"{name}\" was found. Try another spelling or a shorter name."
                : "Pick the entry that matches your copy. The same game has a different Title ID in each region" +
                  (known.Length > 0 ? $" (yours is {known})." : ".");
        }
        catch (OnlineLookupException ex)
        {
            _onlineNote.Text = ex.Message;
        }
        finally
        {
            _searchOnline.IsEnabled = true;
        }
    }

    /// <summary>Fills what is empty from the chosen store entry. Values already typed are kept.</summary>
    private async Task UseMatchAsync()
    {
        if (_results.SelectedItem is not StoreResult match) return;
        _useMatch.IsEnabled = false;
        var filled = new List<string>();
        var notes = new List<string>();
        try
        {
            if (string.IsNullOrWhiteSpace(_title.Text))
            {
                _title.Text = match.Name;
                filled.Add("Title");
            }
            string known = (_titleId.Text ?? string.Empty).Trim();
            if (known.Length == 0) known = _item.Game.TitleId;
            if (known.Length == 0 || known.Equals(match.TitleId, StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(_titleId.Text) && _item.Game.TitleId.Length == 0)
                {
                    _titleId.Text = match.TitleId;
                    filled.Add("Title ID");
                }
                if (string.IsNullOrWhiteSpace(_contentId.Text) && _item.Game.ContentId.Length == 0)
                {
                    _contentId.Text = match.ContentId;
                    filled.Add("Content ID");
                }
            }
            else
            {
                notes.Add($"Kept your Title ID {known}: this entry is {match.TitleId} ({match.Region}), a different region's release.");
            }

            string iconUrl = match.IconUrl;
            string about = match.Publisher;
            bool needIcon = !_item.HasThumbnail && _newIcon is null && _edit.IconFile.Length == 0;
            if (iconUrl.Length == 0 && needIcon)
            {
                // The title list has no artwork; the store's search, by the name found, does.
                try
                {
                    StoreResult? hit = await Ps5StoreLookup.FindArtworkAsync(match.Name, match.TitleId);
                    if (hit is not null)
                    {
                        iconUrl = hit.IconUrl;
                        about = hit.Publisher + (hit.ReleaseDate.Length >= 10 ? $", released {hit.ReleaseDate[..10]}" : string.Empty);
                    }
                }
                catch (OnlineLookupException) { notes.Add("The icon could not be fetched."); }
            }
            if (iconUrl.Length > 0 && needIcon)
            {
                byte[]? downloaded = await Ps5StoreLookup.DownloadImageAsync(iconUrl);
                byte[]? png = downloaded is null ? null : await IconTools.SquarePngAsync(Ps5ImageData.FromPng(downloaded));
                if (png is not null)
                {
                    _newIcon = png;
                    _removeIcon = false;
                    ShowPreview(new Bitmap(new MemoryStream(png)));
                    filled.Add("Icon");
                }
            }
            if (match.ReleaseDate.Length >= 10 && !about.Contains("released", StringComparison.Ordinal))
                about += (about.Length > 0 ? ", " : string.Empty) + $"released {match.ReleaseDate[..10]}";
            _onlineNote.Text = (filled.Count > 0 ? "Filled: " + string.Join(", ", filled) + ". " : "Nothing new to fill in. ") +
                               string.Join(" ", notes) + (about.Length > 0 ? $" ({about}.)" : string.Empty) +
                               " Check the fields above, then save.";
        }
        catch (OnlineLookupException ex)
        {
            _onlineNote.Text = ex.Message;
        }
        finally
        {
            _useMatch.IsEnabled = _results.SelectedItem is StoreResult;
        }
    }

    /// <summary>Fills the empty fields with what can be worked out offline about this title.</summary>
    private async Task AutofillAsync()
    {
        _autofill.IsEnabled = false;
        _autofillNote.Text = "Looking...";
        try
        {
            MetadataSuggestion s = await Task.Run(() => Ps5MetadataSuggester.Suggest(_item.Game));
            var filled = new List<string>();
            void Fill(TextBox box, string value, string field)
            {
                if (value.Length == 0 || !string.IsNullOrWhiteSpace(box.Text)) return;
                box.Text = value;
                filled.Add($"{field} ({s.Sources.GetValueOrDefault(field, "found")})");
            }
            Fill(_title, s.Title, "Title");
            Fill(_titleId, s.TitleId, "Title ID");
            Fill(_contentId, s.ContentId, "Content ID");
            Fill(_version, s.Version, "Version");
            Fill(_firmware, s.Firmware, "Firmware");
            if (s.Icon is not null && _newIcon is null && _edit.IconFile.Length == 0 &&
                await IconTools.SquarePngAsync(s.Icon) is { } png)
            {
                _newIcon = png;
                _removeIcon = false;
                ShowPreview(new Bitmap(new MemoryStream(png)));
                filled.Add($"Icon (made from sce_sys/{s.IconSource})");
            }
            _autofillNote.Text = filled.Count > 0
                ? "Filled: " + string.Join("; ", filled) + ". Check them, then save."
                : "Nothing more could be worked out from this title's files and its file or folder name.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                       ArgumentException or InvalidOperationException)
        {
            _autofillNote.Text = "Could not read the title: " + ex.Message;
        }
        finally
        {
            _autofill.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- region and features

    /// <summary>The Content ID starts with two letters that name the region, so the region shown follows the Content ID.</summary>
    private void SyncRegionFromContentId()
    {
        string id = (_contentId.Text ?? string.Empty).Trim();
        if (id.Length == 0) id = _item.ContentId.Length > 0 ? _item.ContentId : _item.TitleId;
        string region = LibraryItem.RegionOfId(id);
        int index = Array.FindIndex(LibraryItem.Regions, entry => entry.Name == region);
        _syncingRegion = true;
        _region.SelectedIndex = index;
        _syncingRegion = false;
    }

    /// <summary>Choosing a region rewrites the first two letters of the Content ID (it is the only place a region is stored).</summary>
    private void ApplyRegionToContentId()
    {
        if (_syncingRegion || _region.SelectedIndex < 0) return;
        string prefix = LibraryItem.Regions[_region.SelectedIndex].Prefix;
        string id = (_contentId.Text ?? string.Empty).Trim();
        if (id.Length == 0 && _item.ContentId.Length > 0) id = _item.ContentId;
        if (id.Length >= 2)
            _contentId.Text = prefix + id[2..];
        else
        {
            string titleId = (_titleId.Text ?? string.Empty).Trim().ToUpperInvariant();
            if (titleId.Length == 0) titleId = _item.TitleId;
            if (titleId.Length == 0)
            {
                _autofillNote.Text = "Enter a Content ID or Title ID first: the region is the first two letters of the Content ID.";
                SyncRegionFromContentId();
                return;
            }
            _contentId.Text = $"{prefix}0000-{titleId}_00-0000000000000000";
        }
    }

    private Control BuildFeatures(Ps5GameInfo game)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (string name in DeclaredFeatureFlags.Names)
        {
            var box = new CheckBox { Content = name, Margin = new Thickness(0, 0, 14, 0), Tag = name };
            _features.Add(box);
            panel.Children.Add(box);
        }
        SetFeatureBoxes(_edit.Features ?? game.DeclaredFeatures);
        return panel;
    }

    private void SetFeatureBoxes(IEnumerable<string> features)
    {
        var on = new HashSet<string>(features, StringComparer.OrdinalIgnoreCase);
        foreach (CheckBox box in _features) box.IsChecked = on.Contains((string)box.Tag!);
    }

    /// <summary>The ticked features, or null when they match what the file declares (nothing to change).</summary>
    private List<string>? ChosenFeatures()
    {
        List<string> chosen = _features.Where(box => box.IsChecked == true).Select(box => (string)box.Tag!).ToList();
        var detected = new HashSet<string>(_item.Game.DeclaredFeatures.Where(name => DeclaredFeatureFlags.Names.Contains(name)), StringComparer.OrdinalIgnoreCase);
        return detected.SetEquals(chosen) ? null : chosen;
    }

    private void UpdateModeNote()
    {
        // The button says exactly what will happen, so "Save" is never mistaken for changing the file.
        _save.Content = _mode.SelectedIndex switch
        {
            1 => "Write into the file",
            2 => "Write a new copy...",
            _ => "Save in library"
        };
        _modeNote.Text = _mode.SelectedIndex switch
        {
            1 => _item.Game.SourceKind == Ps5SourceKind.SonyPackage
                ? "The package is unpacked, changed and rebuilt as a debug package (FPKG), as a queued task. This needs free " +
                  "space for a temporary copy and works only for packages Packwright can read (not retail or encrypted ones). " +
                  "The builder sets the required firmware from the game's SDK, so a firmware edit does not stick on packages."
                : "The change is made as a queued task: images are rebuilt beside the original and swapped in only after " +
                  "they verify.",
            2 => "Same as above, but the original is left untouched and the result goes to a new file.",
            _ => "Only Packwright's library shows these values. The file itself is NOT changed: to update the " +
                 "file, choose \"Write into the file itself\" (or a new copy) above."
        };
    }

    private static void Fill(TextBox box, string value, string detected)
    {
        box.Text = value;
        box.Watermark = string.IsNullOrWhiteSpace(detected) ? "(not available)" : detected;
    }

    private void ShowPreview(Bitmap? bitmap, bool custom = false)
    {
        _preview.Source = bitmap;
        _iconNote.Text = bitmap is null
            ? "No icon found in the file. Choose a PNG or JPG image (square, 512x512 or larger works best)."
            : custom || _newIcon is not null
                ? "Custom icon. It is shown in the library and the details panel."
                : "Icon from the file. Choose an image to replace it.";
    }

    private async Task ChooseIconAsync()
    {
        string? file = await Dialogs.PickFileAsync(this, "Choose an icon image", ["*.png", "*.jpg", "*.jpeg", "*.bmp", "*.webp"]);
        if (file is null) return;
        try
        {
            byte[] png = IconTools.SquarePng(file) ?? throw new InvalidOperationException("The image could not be decoded.");
            _newIcon = png;
            _removeIcon = false;
            ShowPreview(new Bitmap(new MemoryStream(png)));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(this, "Edit details", "That image could not be read: " + ex.Message);
        }
    }

    private async void Save()
    {
        _edit.Title = (_title.Text ?? string.Empty).Trim();
        _edit.TitleId = (_titleId.Text ?? string.Empty).Trim().ToUpperInvariant();
        _edit.ContentId = (_contentId.Text ?? string.Empty).Trim().ToUpperInvariant();
        _edit.Version = (_version.Text ?? string.Empty).Trim();
        _edit.Firmware = (_firmware.Text ?? string.Empty).Trim();
        _edit.Category = _category.SelectedIndex <= 0 ? string.Empty : Categories[_category.SelectedIndex];
        _edit.ConceptId = (_concept.Text ?? string.Empty).Trim();
        _edit.MasterVersion = (_master.Text ?? string.Empty).Trim();
        _edit.Sdk = (_sdk.Text ?? string.Empty).Trim();
        _edit.Drm = (_drm.Text ?? string.Empty).Trim();
        _edit.DefaultLanguage = (_language.Text ?? string.Empty).Trim();
        _edit.Created = (_created.Text ?? string.Empty).Trim();
        _edit.ToolVersion = (_toolVersion.Text ?? string.Empty).Trim();
        _edit.Features = ChosenFeatures();

        string oldIcon = _edit.IconFile;
        if (_newIcon is not null)
            _edit.IconFile = MetadataOverrides.StoreIcon(_item.Path, _newIcon);
        else if (_removeIcon)
            _edit.IconFile = string.Empty;
        if (oldIcon.Length > 0 && oldIcon != _edit.IconFile) MetadataOverrides.DeleteIcon(oldIcon);

        if (_mode.SelectedIndex == 0)
        {
            MetadataOverrides.Set(_item.Path, _edit);
            _item.ApplyOverride(_edit.IsEmpty ? null : _edit);
            Logger.Info($"Edited library details for {_item.FileName}.");
            _outcome = new EditOutcome(EditTarget.Library, null, string.Empty);
            Close();
            return;
        }

        // Written into the file: only the fields that were filled in, plus the icon.
        var write = new MetadataEdit
        {
            Title = _edit.Title, TitleId = _edit.TitleId, ContentId = _edit.ContentId, Version = _edit.Version,
            Firmware = _edit.Firmware, Category = _edit.Category, ConceptId = _edit.ConceptId, MasterVersion = _edit.MasterVersion,
            Sdk = _edit.Sdk, Drm = _edit.Drm, DefaultLanguage = _edit.DefaultLanguage, Created = _edit.Created,
            ToolVersion = _edit.ToolVersion, Features = _edit.Features?.ToList(),
            IconPath = MetadataOverrides.IconPath(_edit) ?? string.Empty
        };
        if (write.IsEmpty)
        {
            await Dialogs.ShowMessageAsync(this, "Edit details", "Fill in at least one field or choose an icon to write.");
            return;
        }
        string output = string.Empty;
        if (_mode.SelectedIndex == 2)
        {
            bool folder = Directory.Exists(_item.Path);
            string stem = folder ? _item.FileName : Path.GetFileNameWithoutExtension(_item.FileName);
            string extension = folder ? string.Empty : Path.GetExtension(_item.FileName);
            string? picked = folder
                ? await Dialogs.PickFolderAsync(this, "Folder to create the edited copy in")
                : await Dialogs.SaveFileAsync(this, "Save the edited copy", stem + " (edited)" + extension, extension);
            if (picked is null) return;
            output = folder ? Path.Combine(picked, stem + " (edited)") : picked;
        }
        else if (!await Dialogs.ConfirmAsync(this, "Write into the file",
                     $"Change {_item.FileName} itself?\n\nIt is rebuilt beside the original and replaced only after the result " +
                     "verifies. Large images need free space and time.", "Write"))
            return;
        _outcome = new EditOutcome(_mode.SelectedIndex == 2 ? EditTarget.NewCopy : EditTarget.InPlace, write, output);
        Close();
    }

    private void ResetAll()
    {
        _title.Text = _titleId.Text = _contentId.Text = _version.Text = _firmware.Text = string.Empty;
        _concept.Text = _master.Text = _sdk.Text = _created.Text = _toolVersion.Text = string.Empty;
        _drm.Text = _language.Text = string.Empty;
        _category.SelectedIndex = 0;
        SetFeatureBoxes(_item.Game.DeclaredFeatures);
        SyncRegionFromContentId();
        _newIcon = null;
        _removeIcon = true;
        ShowPreview(null);
    }
}
