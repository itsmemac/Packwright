using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class LibraryView : UserControl
{
    private sealed class GroupVm
    {
        public string Title { get; init; } = string.Empty;
        public int Count => Items.Count;
        public bool ShowHeader { get; init; }
        public List<LibraryItem> Items { get; init; } = [];
    }

    private static readonly (string Label, string Key)[] GroupOptions =
    [
        ("No grouping", ""), ("Family (title)", "family"), ("Title ID", "titleid"), ("Category", "category"),
        ("Region", "region"), ("Format", "source"), ("Firmware", "firmware")
    ];

    private static readonly string[] SortOptions = ["Title", "Title ID", "Role", "Version", "Size", "Category", "Region", "Format", "Firmware", "Path"];
    private const string AnyFormat = "All formats";
    private const string AnyRegion = "All regions";
    private const string NoView = "Saved views";

    private readonly AppServices _services = AppServices.Instance;
    private List<LibraryItem> _items = [];
    private List<LibraryItem> _shown = [];
    private List<LibraryItem> _selection = [];
    private CancellationTokenSource? _scanCts;
    private bool _suppress;
    private bool _updatingSelection;
    private string _preset = "all";

    public LibraryView()
    {
        InitializeComponent();
        GroupBox.ItemsSource = GroupOptions.Select(option => option.Label).ToList();
        SortBox.ItemsSource = SortOptions;
        _suppress = true;
        GroupBox.SelectedIndex = Math.Max(0, Array.FindIndex(GroupOptions, option => option.Key == _services.Settings.DefaultGroupBy));
        SortBox.SelectedItem = SortOptions.Contains(_services.Settings.LibrarySortColumn) ? _services.Settings.LibrarySortColumn : "Title";
        SortDirection.IsChecked = !_services.Settings.LibrarySortAscending;
        SortDirection.Content = _services.Settings.LibrarySortAscending ? "A → Z" : "Z → A";
        SetLayout(_services.Settings.LibraryLayout != "List", persist: false);
        _suppress = false;

        AddFolderButton.Click += async (_, _) => await AddFolderAsync();
        RefreshButton.Click += async (_, _) => await RefreshAsync();
        MoreFixMissing.Click += OnMoreFixMissing;
        MoreLookUpOnline.Click += OnMoreLookUpOnline;
        SearchBox.TextChanged += (_, _) => ApplyFilters();
        ClearFiltersButton.Click += (_, _) => ClearFilters();
        foreach (ComboBox combo in new[] { FormatFilter, RegionFilter, GroupBox, SortBox })
            combo.SelectionChanged += (_, _) => { if (!_suppress) { PersistView(); ApplyFilters(); } };
        SortDirection.IsCheckedChanged += (_, _) =>
        {
            SortDirection.Content = SortDirection.IsChecked == true ? "Z → A" : "A → Z";
            if (!_suppress) { PersistView(); ApplyFilters(); }
        };
        foreach ((RadioButton button, string preset) in new[]
                 { (PresetAll, "all"), (PresetGames, "game"), (PresetUpdates, "patch"), (PresetDlc, "dlc") })
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked != true) return;
                _preset = preset;
                if (!_suppress) ApplyFilters();
            };
        CardsToggle.IsCheckedChanged += (_, _) => { if (CardsToggle.IsChecked == true && !_suppress) SetLayout(true, persist: true); };
        ListToggle.IsCheckedChanged += (_, _) => { if (ListToggle.IsChecked == true && !_suppress) SetLayout(false, persist: true); };

        ListGrid.SelectionChanged += (_, _) =>
        {
            if (_updatingSelection) return;
            _selection = ListGrid.SelectedItems.Cast<LibraryItem>().ToList();
            _ = ShowSelectionAsync();
        };
        ListGrid.DoubleTapped += (_, e) =>
        {
            if (IsOnItem(e.Source) && _selection.FirstOrDefault() is { } item) Dialogs.Reveal(item.Path);
        };
        // A click on empty space (not on a row, header or scroll bar) clears the selection.
        EventHandler<PointerPressedEventArgs> clearOnEmpty = (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && e.KeyModifiers == KeyModifiers.None &&
                !IsOnItem(e.Source) && !IsOn<ScrollBar>(e.Source) && !IsOn<DataGridColumnHeader>(e.Source))
                ClearSelection();
        };
        ListGrid.AddHandler(PointerPressedEvent, clearOnEmpty, RoutingStrategies.Bubble, handledEventsToo: true);
        CardsScroll.AddHandler(PointerPressedEvent, clearOnEmpty, RoutingStrategies.Bubble, handledEventsToo: true);

        ViewsBox.SelectionChanged += (_, _) => { if (!_suppress) ApplySavedView(); };
        SaveViewButton.Click += async (_, _) => await SaveViewAsync();
        MoreAddItem.Click += async (_, _) => await AddItemAsync();
        MoreExport.Click += async (_, _) => await ExportAsync();
        MoreExportJson.Click += async (_, _) => await ExportReportAsync(html: false);
        MoreExportHtml.Click += async (_, _) => await ExportReportAsync(html: true);
        MoreDuplicates.Click += async (_, _) => await Dialogs.ConfirmTextAsync(Dialogs.OwnerOf(this), "Duplicates",
            "Exact copies share the same param.json; the second list shares a content ID and version.",
            LibraryActions.DuplicatesReport(_items), "", "Close");
        MoreMissingBase.Click += async (_, _) => await Dialogs.ConfirmTextAsync(Dialogs.OwnerOf(this),
            "Missing base games", "Updates and add-ons without a base game:",
            LibraryActions.MissingBaseReport(_items), "", "Close");
        MoreInstallOrder.Click += async (_, _) => await RenameByInstallOrderAsync();
        MoreColumns.Click += async (_, _) => await ChooseColumnsAsync();
        MoreDeleteView.Click += (_, _) => DeleteSavedView();

        InitializeDetails();
        InitializeColumnHeaders();
        ApplyHiddenColumns();
        RebuildViewsBox();
        _services.LibraryChanged += () => Dispatcher.UIThread.Post(Rebuild);
        _services.SettingsChanged += () => Dispatcher.UIThread.Post(() =>
        {
            SetLayout(_services.Settings.LibraryLayout != "List", persist: false);
            ApplyFilters();
        });
    }

    public event Action<string>? StatusChanged;

    public async Task InitializeAsync()
    {
        Rebuild();
        if (_services.Settings.RefreshOnStartup || (_items.Count == 0 && _services.ScanSources.Count > 0))
            await RefreshAsync();
    }

    private void Status(string text) => StatusChanged?.Invoke(text);

    // ---------------------------------------------------------------- layout and scanning

    private void SetLayout(bool cards, bool persist)
    {
        bool wasSuppressed = _suppress;
        _suppress = true;
        CardsToggle.IsChecked = cards;
        ListToggle.IsChecked = !cards;
        _suppress = wasSuppressed;
        CardsScroll.IsVisible = cards;
        ListScroll.IsVisible = !cards;
        if (persist)
        {
            _services.Settings.LibraryLayout = cards ? "Cards" : "List";
            _services.SaveSettings();
            ApplyFilters();
        }
    }

    private async Task AddFolderAsync()
    {
        string? folder = await Dialogs.PickFolderAsync(this, "Select a folder with PS5 dumps, images or packages");
        if (folder is null) return;
        _services.AddLibraryFolder(folder);
        await RefreshAsync();
    }

    private async Task AddItemAsync()
    {
        string? file = await Dialogs.PickFileAsync(this, "Add a package or image to the library");
        if (file is null) return;
        _services.AddManualSource(file);
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_services.ScanSources.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Library",
                "Add a folder first. Packwright scans it for dumps, packages and images.");
            return;
        }
        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        CancellationToken token = _scanCts.Token;
        RefreshButton.IsEnabled = false;
        try
        {
            var progress = new Progress<Ps5ScanProgress>(value =>
                Status($"Scanning {value.Processed}/{value.Total}: {Path.GetFileName(value.CurrentPath)}"));
            Ps5ScanResult result = await _services.RefreshLibraryAsync(progress, token);
            Status($"Library: {result.Games.Count:N0} item(s)" +
                   (result.Errors.Count > 0 ? $", {result.Errors.Count} folder error(s), see Log." : "."));
        }
        catch (OperationCanceledException)
        {
            Status("Scan cancelled.");
        }
        catch (Exception ex)
        {
            Logger.Exception("Library scan", ex);
            Status("Scan failed: " + ex.Message);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    // ---------------------------------------------------------------- filtering, sorting, grouping

    private void Rebuild()
    {
        _items = _services.Games.Select(game => new LibraryItem(game)).ToList();
        MarkSupersededUpdates();
        bool wasSuppressed = _suppress;
        _suppress = true;
        try
        {
            Fill(FormatFilter, AnyFormat, _items.Select(item => item.Format));
            Fill(RegionFilter, AnyRegion, _items.Select(item => item.Region));
        }
        finally
        {
            _suppress = wasSuppressed;
        }
        AutoSizeColumns();
        ApplyFilters();
    }

    private static double TextWidth(string text, double size, FontWeight weight, FontFamily family) =>
        new FormattedText(text, System.Globalization.CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(family, FontStyle.Normal, weight), size, Brushes.White).Width;

    /// <summary>
    /// Sizes every column to its longest value (or its header when that is wider), so nothing is cut off and
    /// the list scrolls sideways when the window is narrower than the columns together.
    /// </summary>
    private void AutoSizeColumns()
    {
        FontFamily family = ListGrid.FontFamily;
        double size = ListGrid.FontSize > 0 ? ListGrid.FontSize : 14;
        const int Sample = 150;   // the longest values by length are enough, and keeps this fast for huge libraries

        double Widest(Func<LibraryItem, string> value, FontWeight weight, double fontSize) => _items
            .Select(value).Where(text => text.Length > 0).Distinct()
            .OrderByDescending(text => text.Length).Take(Sample)
            .Select(text => TextWidth(text, fontSize, weight, family)).DefaultIfEmpty(0).Max();

        var selectors = new Dictionary<string, Func<LibraryItem, string>>
        {
            ["Role"] = item => item.Role, ["Category"] = item => item.Category, ["Version"] = item => item.Version,
            ["Region"] = item => item.Region, ["Format"] = item => item.Format, ["Size"] = item => item.SizeText,
            ["Firmware"] = item => item.Firmware, ["Path"] = item => item.Path
        };
        foreach (DataGridColumn column in ListGrid.Columns)
        {
            string header = ColumnName(column);
            double headerWidth = TextWidth(header, 11, FontWeight.SemiBold, family) + 40;
            double width;
            if (header == "Title")
            {
                // thumbnail + gaps, then the wider of the title (semi-bold) and the title ID line
                double text = Math.Max(Widest(item => item.Title, FontWeight.SemiBold, size),
                    Widest(item => item.TitleId, FontWeight.Normal, 11));
                width = 6 + 38 + 12 + text + 24;
            }
            else if (selectors.TryGetValue(header, out Func<LibraryItem, string>? selector))
                width = Widest(selector, FontWeight.Normal, size) + 36;
            else
                continue;
            column.MinWidth = 60;
            column.Width = new DataGridLength(Math.Clamp(Math.Max(width, headerWidth), 60, 2400));
        }
        UpdateGridWidth();
    }

    private void MarkSupersededUpdates()
    {
        foreach (IGrouping<string, LibraryItem> family in _items
                     .Where(item => item.Category == "Patch" && item.TitleId.Length > 0)
                     .GroupBy(item => item.TitleId, StringComparer.OrdinalIgnoreCase))
        {
            LibraryItem? newest = family.OrderByDescending(item => VersionKey(item.Version)).FirstOrDefault();
            foreach (LibraryItem item in family)
                item.Role = item == newest ? "Update" : "Update (older)";
        }
    }

    private static string VersionKey(string version) =>
        string.Join(".", version.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, out int number) ? number.ToString("D8") : part));

    private static void Fill(ComboBox combo, string any, IEnumerable<string> values)
    {
        string? previous = combo.SelectedItem as string;
        List<string> options = [any];
        options.AddRange(values.Where(value => value.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase));
        combo.ItemsSource = options;
        combo.SelectedItem = previous is not null && options.Contains(previous) ? previous : options[0];
    }

    private static string? Chosen(ComboBox combo, string any) =>
        combo.SelectedItem is string value && value != any ? value : null;

    private string GroupKeyName => GroupOptions[Math.Max(0, GroupBox.SelectedIndex)].Key;

    private void ClearFilters()
    {
        _suppress = true;
        SearchBox.Text = string.Empty;
        FormatFilter.SelectedIndex = RegionFilter.SelectedIndex = 0;
        PresetAll.IsChecked = true;
        _preset = "all";
        ViewsBox.SelectedIndex = 0;
        _suppress = false;
        ApplyFilters();
    }

    private void ApplyFilters()
    {
        if (_suppress) return;
        LibraryQuery query = LibraryQuery.Parse(SearchBox.Text);
        string? format = Chosen(FormatFilter, AnyFormat), region = Chosen(RegionFilter, AnyRegion);
        IEnumerable<LibraryItem> filtered = _items.Where(item =>
            (format is null || item.Format == format) &&
            (region is null || item.Region == region) &&
            PresetMatches(item) &&
            query.Matches(item));

        Comparison<LibraryItem> compare = SortComparison(SortBox.SelectedItem as string ?? "Title");
        bool descending = SortDirection.IsChecked == true;
        List<LibraryItem> sorted = filtered.ToList();
        sorted.Sort((a, b) => descending ? compare(b, a) : compare(a, b));

        string groupBy = GroupKeyName;
        foreach (LibraryItem item in sorted) item.GroupKey = GroupOf(item, groupBy);
        if (groupBy.Length > 0)
        {
            Dictionary<string, string> order = sorted.GroupBy(item => item.GroupKey)
                .ToDictionary(group => group.Key, group => GroupSortKey(group.ToList(), groupBy));
            sorted = sorted.OrderBy(item => order[item.GroupKey], StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.GroupKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => groupBy == "family" ? RolePriority(item) : 0)
                .ThenBy(item => groupBy == "family" ? VersionKey(item.Version) : string.Empty, StringComparer.Ordinal)
                .ToList();
        }
        _shown = sorted;

        BuildViews(sorted, groupBy);
        CountText.Text = _items.Count == 0
            ? "No items yet"
            : $"{sorted.Count:N0} of {_items.Count:N0} item(s)" + (query.Problems.Count > 0
                ? $"  ·  check the query: {string.Join(", ", query.Problems.Distinct())}: is not a field" : string.Empty);
        bool empty = sorted.Count == 0;
        EmptyState.IsVisible = empty;
        if (empty)
        {
            bool none = _items.Count == 0;
            EmptyTitle.Text = none ? "Your library is empty" : "Nothing matches";
            EmptyHint.Text = none
                ? "Add a folder with PS5 dumps, images or packages to get started."
                : "Try a different search or reset the filters.";
        }
        UpdateSortHeaders();
        Status(CountText.Text);
    }

    private bool PresetMatches(LibraryItem item) => _preset switch
    {
        "game" => item.Category == "Game",
        "patch" => item.Category == "Patch",
        "dlc" => item.Category is "DLC" or "Add-on",
        _ => true
    };

    private static int RolePriority(LibraryItem item) => item.Category switch
    {
        "Game" => 0,
        "Patch" => 1,
        "DLC" or "Add-on" => 2,
        "App" => 3,
        _ => 4
    };

    private static Comparison<LibraryItem> SortComparison(string column) => column switch
    {
        "Title ID" => (a, b) => string.Compare(a.TitleId, b.TitleId, StringComparison.OrdinalIgnoreCase),
        "Role" => (a, b) => RolePriority(a).CompareTo(RolePriority(b)),
        "Version" => (a, b) => string.CompareOrdinal(VersionKey(a.Version), VersionKey(b.Version)),
        "Size" => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
        "Category" => (a, b) => string.Compare(a.Category, b.Category, StringComparison.OrdinalIgnoreCase),
        "Region" => (a, b) => string.Compare(a.Region, b.Region, StringComparison.OrdinalIgnoreCase),
        "Format" => (a, b) => string.Compare(a.Format, b.Format, StringComparison.OrdinalIgnoreCase),
        "Firmware" => (a, b) => Ps5LibraryHealth.CompareVersions(a.Firmware, b.Firmware),
        "Path" => (a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase),
        _ => (a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase)
    };

    private static string GroupOf(LibraryItem item, string key) => key switch
    {
        "family" => item.TitleId.Length > 0 ? item.TitleId : item.ContentId.Length > 0 ? item.ContentId : item.FileName,
        "titleid" => item.TitleId,
        "category" => item.Category,
        "region" => item.Region,
        "source" => item.Format,
        "firmware" => item.Firmware,
        _ => string.Empty
    };

    /// <summary>Families order by their base title so related items stay together; others by the key.</summary>
    private static string GroupSortKey(List<LibraryItem> group, string key)
    {
        if (key != "family") return group[0].GroupKey;
        return (group.FirstOrDefault(item => item.Category == "Game") ?? group[0]).Title;
    }

    private static string GroupTitle(List<LibraryItem> items, string key)
    {
        string groupKey = items[0].GroupKey;
        if (key == "family")
        {
            LibraryItem lead = items.FirstOrDefault(item => item.Category == "Game") ?? items[0];
            return groupKey.Length > 0 && lead.Title != groupKey ? $"{lead.Title}  ·  {groupKey}" : lead.Title;
        }
        return groupKey.Length == 0 ? "Unknown" : groupKey;
    }

    private void BuildViews(List<LibraryItem> sorted, string groupBy)
    {
        _updatingSelection = true;
        try
        {
            if (CardsScroll.IsVisible)
            {
                List<GroupVm> groups = groupBy.Length == 0
                    ? [new GroupVm { Items = sorted, ShowHeader = false }]
                    : sorted.GroupBy(item => item.GroupKey).Select(group => group.ToList())
                        .Select(list => new GroupVm { Title = GroupTitle(list, groupBy), Items = list, ShowHeader = true }).ToList();
                GroupsHost.ItemsSource = groups;
            }
            else
            {
                var view = new DataGridCollectionView(sorted);
                if (groupBy.Length > 0) view.GroupDescriptions.Add(new DataGridPathGroupDescription(nameof(LibraryItem.GroupKey)));
                ListGrid.ItemsSource = view;
                UpdateGridWidth();
            }
            _selection = [];
        }
        finally
        {
            _updatingSelection = false;
        }
        _ = ShowSelectionAsync();
    }

    // ---------------------------------------------------------------- selection

    private void OnCardsSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingSelection || sender is not ListBox box) return;
        _updatingSelection = true;
        try
        {
            if (box.SelectedItems?.Count > 0)
                foreach (ListBox other in GroupsHost.GetVisualDescendants().OfType<ListBox>().Where(other => other != box))
                    other.SelectedItems?.Clear();
            _selection = GroupsHost.GetVisualDescendants().OfType<ListBox>()
                .SelectMany(list => list.SelectedItems?.Cast<LibraryItem>() ?? []).ToList();
        }
        finally
        {
            _updatingSelection = false;
        }
        _ = ShowSelectionAsync();
    }

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (IsOnItem(e.Source) && _selection.FirstOrDefault() is { } item) Dialogs.Reveal(item.Path);
    }

    private static bool IsOn<T>(object? source) where T : class
    {
        for (Visual? visual = source as Visual; visual is not null; visual = visual.GetVisualParent())
            if (visual is T) return true;
        return false;
    }

    private static bool IsOnItem(object? source) => IsOn<ListBoxItem>(source) || IsOn<DataGridRow>(source);

    private void ClearSelection()
    {
        if (_selection.Count == 0) return;
        ListGrid.SelectedItems.Clear();
        foreach (ListBox list in GroupsHost.GetVisualDescendants().OfType<ListBox>())
            list.SelectedItems?.Clear();
    }

    private List<Ps5GameInfo> SelectedGames() => _selection.Select(item => item.Game).ToList();

    // ---------------------------------------------------------------- saved views

    private void RebuildViewsBox()
    {
        bool wasSuppressed = _suppress;
        _suppress = true;
        List<string> names = [NoView];
        names.AddRange(_services.Settings.SavedViews.Select(view => view.Name));
        ViewsBox.ItemsSource = names;
        ViewsBox.SelectedIndex = 0;
        _suppress = wasSuppressed;
    }

    private void PersistView()
    {
        _services.Settings.DefaultGroupBy = GroupKeyName;
        _services.Settings.LibrarySortColumn = SortBox.SelectedItem as string ?? "Title";
        _services.Settings.LibrarySortAscending = SortDirection.IsChecked != true;
        _services.SaveSettings();
    }

    private async Task SaveViewAsync()
    {
        string? name = await Dialogs.PromptAsync(Dialogs.OwnerOf(this), "Save view", "Name this view:");
        if (name is null) return;
        var view = new SavedLibraryView
        {
            Name = name,
            Query = (SearchBox.Text ?? string.Empty) + (_preset == "all" ? string.Empty : "\u0001" + _preset),
            Formats = Chosen(FormatFilter, AnyFormat) is { } format ? [format] : [],
            Regions = Chosen(RegionFilter, AnyRegion) is { } region ? [region] : [],
            GroupBy = GroupKeyName,
            SortKeys = [$"{SortBox.SelectedItem}:{(SortDirection.IsChecked == true ? "desc" : "asc")}"],
            HiddenColumns = _services.Settings.LibraryHiddenColumns.ToList()
        };
        _services.Settings.SavedViews.RemoveAll(existing => existing.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _services.Settings.SavedViews.Add(view);
        _services.SaveSettings();
        RebuildViewsBox();
        ViewsBox.SelectedItem = name;
        Status($"Saved view '{name}'.");
    }

    private void ApplySavedView()
    {
        if (ViewsBox.SelectedIndex <= 0) return;
        SavedLibraryView? view = _services.Settings.SavedViews.FirstOrDefault(saved => saved.Name == ViewsBox.SelectedItem as string);
        if (view is null) return;
        _suppress = true;
        string[] parts = view.Query.Split('\u0001');
        SearchBox.Text = parts[0];
        _preset = parts.Length > 1 ? parts[1] : "all";
        (_preset switch { "game" => PresetGames, "patch" => PresetUpdates, "dlc" => PresetDlc, _ => PresetAll }).IsChecked = true;
        FormatFilter.SelectedItem = view.Formats.FirstOrDefault() is { } format && FormatFilter.Items.Contains(format) ? format : AnyFormat;
        RegionFilter.SelectedItem = view.Regions.FirstOrDefault() is { } region && RegionFilter.Items.Contains(region) ? region : AnyRegion;
        GroupBox.SelectedIndex = Math.Max(0, Array.FindIndex(GroupOptions, option => option.Key == view.GroupBy));
        if (view.SortKeys.FirstOrDefault()?.Split(':') is { Length: 2 } sort)
        {
            SortBox.SelectedItem = SortOptions.Contains(sort[0]) ? sort[0] : "Title";
            SortDirection.IsChecked = sort[1] == "desc";
        }
        _services.Settings.LibraryHiddenColumns = view.HiddenColumns.ToList();
        ApplyHiddenColumns();
        _suppress = false;
        ApplyFilters();
    }

    private void DeleteSavedView()
    {
        if (ViewsBox.SelectedItem is not string name || ViewsBox.SelectedIndex <= 0) return;
        _services.Settings.SavedViews.RemoveAll(view => view.Name == name);
        _services.SaveSettings();
        RebuildViewsBox();
        Status($"Deleted view '{name}'.");
    }

    // ---------------------------------------------------------------- list columns

    /// <summary>
    /// Gives the list the width of its visible columns, so a window that is too narrow shows a horizontal scroll
    /// bar instead of squeezing the columns together.
    /// </summary>
    private void UpdateGridWidth()
    {
        double total = 0;
        foreach (DataGridColumn column in ListGrid.Columns.Where(column => column.IsVisible))
            total += column.Width.IsStar || column.Width.IsAuto ? Math.Max(column.MinWidth, 120) : column.Width.Value;
        ListGrid.MinWidth = total + 24;
    }

    private void ApplyHiddenColumns()
    {
        foreach (DataGridColumn column in ListGrid.Columns)
            column.IsVisible = !_services.Settings.LibraryHiddenColumns.Contains(ColumnName(column));
        UpdateGridWidth();
    }

    private async Task ChooseColumnsAsync()
    {
        List<DataGridColumn> columns = ListGrid.Columns.ToList();
        bool[]? result = await Dialogs.ChecklistAsync(Dialogs.OwnerOf(this), "Visible columns",
            columns.Select(column => (ColumnName(column), column.IsVisible)).ToList());
        if (result is null) return;
        _services.Settings.LibraryHiddenColumns = columns.Where((_, index) => !result[index])
            .Select(column => ColumnName(column)).ToList();
        _services.SaveSettings();
        ApplyHiddenColumns();
    }
}
