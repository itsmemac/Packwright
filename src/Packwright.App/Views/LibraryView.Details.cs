using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Packwright.Core.Tasks;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class LibraryView
{
    private sealed record Field(string Label, string Value);

    private sealed class TrophyRow
    {
        public string Name { get; init; } = string.Empty;
        public string Description { get; init; } = string.Empty;
        public string Grade { get; init; } = string.Empty;
        public Bitmap? Icon { get; init; }
    }

    private readonly List<Bitmap> _detailBitmaps = [];
    private readonly List<Bitmap> _trophyBitmaps = [];
    private CancellationTokenSource? _detailsCts;
    private Ps5GameInfo? _detailGame;
    private Ps5TrophySet? _trophySet;
    private Ps5Artwork? _artwork;

    // Below this width the details panel stops taking space and slides over the list instead, so the list
    // (and its scroll bar) always have room. The header buttons wrap under the title when the list is narrow.
    private const double DrawerBelowWidth = 1100;
    private const double DetailsWidth = 470;
    private const double WrapHeaderBelow = 600;
    private bool _drawerMode;

    private void UpdateAdaptive()
    {
        double width = LibraryRoot.Bounds.Width;
        if (width <= 0) return;
        bool drawer = width < DrawerBelowWidth;
        _drawerMode = drawer;
        if (drawer)
        {
            Grid.SetColumn(DetailHost, 0);
            Grid.SetColumnSpan(DetailHost, 2);
            DetailHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
            DetailHost.Width = Math.Min(DetailsWidth, Math.Max(320, width - 48));
            DetailHost.ZIndex = 10;
            DetailHost.IsVisible = _selection.Count > 0;
            // Start below the header so its buttons stay reachable while the panel is open.
            DetailHost.Margin = new Thickness(0, Math.Max(0, HeaderGrid.Bounds.Bottom), 0, 0);
        }
        else
        {
            Grid.SetColumn(DetailHost, 1);
            Grid.SetColumnSpan(DetailHost, 1);
            DetailHost.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
            DetailHost.Width = DetailsWidth;
            DetailHost.ZIndex = 0;
            DetailHost.IsVisible = true;
            DetailHost.Margin = default;
        }
        DetailHost.Classes.Set("drawer", drawer);
        DetailClose.IsVisible = drawer;

        bool wrap = (drawer ? width : width - DetailsWidth) < WrapHeaderBelow;
        Grid.SetColumn(HeaderActions, wrap ? 0 : 1);
        Grid.SetRow(HeaderActions, wrap ? 1 : 0);
        Grid.SetColumnSpan(HeaderActions, wrap ? 2 : 1);
        HeaderActions.HorizontalAlignment = wrap
            ? Avalonia.Layout.HorizontalAlignment.Left
            : Avalonia.Layout.HorizontalAlignment.Right;
        HeaderActions.Margin = wrap ? new Thickness(0, 12, 0, 0) : default;
    }

    private void InitializeDetails()
    {
        LibraryRoot.SizeChanged += (_, _) => UpdateAdaptive();
        HeaderGrid.SizeChanged += (_, _) => UpdateAdaptive();
        DetailClose.Click += (_, _) => ClearSelection();
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || !_drawerMode || _selection.Count == 0) return;
            ClearSelection();
            e.Handled = true;
        };
        ExportTrophiesButton.Click += async (_, _) => await ExportTrophiesAsync();
        FilesPane.StatusChanged += Status;
        DetailTabs.SelectionChanged += (_, e) =>
        {
            if (e.Source == DetailTabs) UpdateFilesActive();
        };
        EditDetailsButton.Click += async (_, _) => await EditSelectedAsync();
    }

    private async Task ShowSelectionAsync()
    {
        _detailsCts?.Cancel();
        UpdateAdaptive();
        LibraryItem? item = _selection.FirstOrDefault();
        ClearDetails();
        DetailEmpty.IsVisible = item is null;
        DetailContent.IsVisible = item is not null;
        if (item is null) return;
        if (_selection.Count > 1) Status($"{_selection.Count:N0} items selected.");

        Ps5GameInfo game = item.Game;
        _detailGame = game;
        DetailTitle.Text = item.Title;
        DetailSub.Text = string.Join("  ·  ", new[] { item.TitleId, item.Category, item.Format, item.SizeText }
            .Where(part => part.Length > 0));
        IconImage.Source = item.Thumbnail;
        OverviewList.ItemsSource = BuildOverview(item);
        RawText.Text = game.RawParamJson;
        PackageText.Text = game.Package is null ? "Not a package." : LibraryActions.Describe(game.Package);

        _detailsCts = new CancellationTokenSource();
        CancellationToken token = _detailsCts.Token;
        try
        {
            var artwork = new Progress<Ps5Artwork>(value =>
            {
                if (!token.IsCancellationRequested) ShowArtwork(value);
            });
            Ps5GameDetails details = await new Ps5DetailsLoader().LoadAsync(game, token, artwork);
            if (token.IsCancellationRequested) return;
            ShowArtwork(new Ps5Artwork(details.Icon, details.Background, details.Background1, details.Background2));
            ShowTrophies(details.TrophySet);
            FilesPane.Load(game, details.Files);
            UpdateFilesActive();
            StatsText.Text = BuildStats(details.Files);
            ExecutableText.Text = LibraryActions.Describe(details.Executable);
            ActivitiesText.Text = details.Uds is null ? "No activity data." : LibraryActions.Describe(details.Uds);
            if (details.Errors.Count > 0)
            {
                // Missing or unreadable trophy, activity or executable data is normal for stripped or retail dumps. It
                // is shown with the title (and logged), not in the status bar, which is for what you just did.
                Logger.Info($"Details of {item.FileName}: {string.Join("; ", details.Errors)}");
                List<Field> overview = BuildOverview(item);
                overview.Add(new Field("Read notes", string.Join("\n", details.Errors)));
                OverviewList.ItemsSource = overview;
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.Exception("Details", ex);
            Status("Could not read details: " + ex.Message);
        }
        finally
        {
            MemoryTrimmer.Schedule();
        }
    }

    private static void DisposeAll(List<Bitmap> bitmaps)
    {
        foreach (Bitmap bitmap in bitmaps) bitmap.Dispose();
        bitmaps.Clear();
    }

    private static Bitmap? Track(Bitmap? bitmap, List<Bitmap> list)
    {
        if (bitmap is not null) list.Add(bitmap);
        return bitmap;
    }

    /// <summary>The file tree is built only while the Files tab is the one on screen.</summary>
    private void UpdateFilesActive() =>
        FilesPane.Active = DetailTabs.SelectedItem is TabItem { Header: string header } && header == "Files";

    private void ClearDetails()
    {
        DetailTitle.Text = DetailSub.Text = string.Empty;
        IconImage.Source = null;
        BannerImage.Source = null;
        OverviewList.ItemsSource = null;
        ArtworkPanel.Children.Clear();
        TrophyList.ItemsSource = null;
        DisposeAll(_detailBitmaps);
        DisposeAll(_trophyBitmaps);
        TrophySummary.Text = string.Empty;
        FilesPane.Clear();
        PackageText.Text = ExecutableText.Text = ActivitiesText.Text = StatsText.Text = RawText.Text = string.Empty;
        _detailGame = null;
        _trophySet = null;
        _artwork = null;
    }

    private static List<Field> BuildOverview(LibraryItem item)
    {
        Ps5GameInfo game = item.Game;
        var fields = new List<Field>();
        if (item.IsEdited) fields.Add(new Field("Edited", "Some details below were changed by you (Edit details)."));
        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) fields.Add(new Field(label, value));
        }
        Add("Title", item.Title);
        Add("Title ID", item.TitleId);
        Add("Content ID", item.ContentId);
        Add("Concept ID", item.ConceptId);
        Add("Category", item.Category);
        Add("Region", item.Region);
        Add("Version", item.Version);
        Add("Master version", item.MasterVersion);
        Add("Required firmware", item.Firmware);
        Add("SDK version", item.SdkVersion);
        Add("DRM", item.Drm);
        Add("Default language", item.DefaultLanguage);
        Add("Created", item.Created);
        Add("Tool version", item.ToolVersion);
        Add("Format", game.SourceDescription);
        Add("Size", game.SourceSize > 0 ? LibraryItem.FormatBytes(game.SourceSize) : null);
        Add("Path", game.RootPath);
        if (item.Features.Count > 0) Add("Features", string.Join(", ", item.Features));
        if (game.DataWarnings.Count > 0) Add("Warnings", string.Join("\n", game.DataWarnings));
        return fields;
    }

    private static string BuildStats(Ps5FileInventory files)
    {
        var text = new StringBuilder();
        text.AppendLine($"Files        {files.FileCount:N0}");
        text.AppendLine($"Total size   {LibraryItem.FormatBytes(files.TotalSize)}");
        text.AppendLine();
        text.AppendLine("By type");
        foreach (var group in files.Files
                     .GroupBy(file => string.IsNullOrEmpty(file.Extension) ? "(none)" : file.Extension.ToLowerInvariant())
                     .Select(group => (Name: group.Key, Count: group.Count(), Size: group.Sum(file => file.Size)))
                     .OrderByDescending(group => group.Size).Take(12))
            text.AppendLine($"  {group.Name,-10} {group.Count,7:N0} files  {LibraryItem.FormatBytes(group.Size),12}");
        text.AppendLine();
        text.AppendLine("Largest files");
        foreach (Ps5FileInfo file in files.Files.OrderByDescending(file => file.Size).Take(15))
            text.AppendLine($"  {LibraryItem.FormatBytes(file.Size),10}  {file.RelativePath}");
        return text.ToString();
    }

    // ---------------------------------------------------------------- artwork and trophies

    private void ShowArtwork(Ps5Artwork art)
    {
        _artwork = art;
        // Artwork can be 4K. Previous bitmaps are released before new ones are made, and everything is shown at
        // display size, so a big game does not hold hundreds of megabytes of pixels.
        BannerImage.Source = null;
        ArtworkPanel.Children.Clear();
        IconImage.Source = null;
        DisposeAll(_detailBitmaps);
        if (MetadataOverrides.IconPath(MetadataOverrides.Get(_detailGame?.RootPath ?? string.Empty)) is { } custom)
            IconImage.Source = Track(new Bitmap(custom), _detailBitmaps);
        else if (Track(ToBitmap(art.Icon), _detailBitmaps) is { } icon) IconImage.Source = icon;
        Ps5ImageData? banner = art.Background ?? art.Background1 ?? art.Background2;
        if (Track(ToBitmap(banner), _detailBitmaps) is { } bannerBitmap) BannerImage.Source = bannerBitmap;
        foreach ((string name, string fileName, Ps5ImageData? data) in new[]
                 {
                     ("Icon", "icon0.png", art.Icon), ("Background", "pic0.png", art.Background),
                     ("Background 1", "pic1.png", art.Background1), ("Background 2", "pic2.png", art.Background2)
                 })
        {
            if (Track(ToBitmap(data), _detailBitmaps) is not { } bitmap) continue;
            var header = new DockPanel();
            var save = new Button { Content = "Save...", Classes = { "ghost" }, Padding = new Thickness(10, 3) };
            DockPanel.SetDock(save, Avalonia.Controls.Dock.Right);
            string captured = fileName;
            Ps5ImageData image = data!;
            save.Click += async (_, _) => await SaveArtworkAsync(image, captured);
            header.Children.Add(save);
            header.Children.Add(new TextBlock
            {
                Text = $"{name}  ({bitmap.PixelSize.Width}x{bitmap.PixelSize.Height})",
                Classes = { "muted" },
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            ArtworkPanel.Children.Add(header);
            ArtworkPanel.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(10),
                ClipToBounds = true,
                Child = new Image { Source = bitmap, MaxWidth = 420, Stretch = Avalonia.Media.Stretch.Uniform }
            });
        }
    }

    private async Task SaveArtworkAsync(Ps5ImageData data, string fileName)
    {
        string? file = await Dialogs.SaveFileAsync(this, "Save artwork", fileName, ".png");
        if (file is null) return;
        try
        {
            if (!data.IsRgba) await File.WriteAllBytesAsync(file, data.Bytes);
            else
            {
                // Converted artwork is shown reduced; read the original again so the saved file is full size.
                Ps5ImageData source = data;
                Ps5GameInfo? game = _detailGame;
                if (game is not null && fileName.StartsWith("pic", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(fileName.AsSpan(3, 1), out int index))
                    source = await Task.Run(() => Ps5DetailsLoader.LoadFullBackground(game, index)) ?? data;
                using Bitmap full = ToBitmap(source, int.MaxValue)!;
                await using FileStream stream = File.Create(file);
                full.Save(stream);
            }
            Status($"Saved {Path.GetFileName(file)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Save artwork", ex.Message);
        }
    }

    private const int MaximumDisplayWidth = 1280;

    private static Bitmap? ToBitmap(Ps5ImageData? data, int maximumWidth = MaximumDisplayWidth)
    {
        if (data is null || data.IsEmpty) return null;
        try
        {
            if (!data.IsRgba)
            {
                using var encoded = new MemoryStream(data.Bytes, writable: false);
                return data.Width > maximumWidth
                    ? Bitmap.DecodeToWidth(encoded, maximumWidth, BitmapInterpolationMode.MediumQuality)
                    : new Bitmap(encoded);
            }

            // Average whole blocks of pixels down to display size (a plain copy for small images).
            int factor = Math.Max(1, (int)Math.Ceiling(data.Width / (double)Math.Max(1, maximumWidth)));
            int width = data.Width / factor, height = data.Height / factor;
            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                PixelFormat.Rgba8888, AlphaFormat.Unpremul);
            using ILockedFramebuffer buffer = bitmap.Lock();
            if (factor == 1)
            {
                int rowBytes = data.Width * 4;
                for (int y = 0; y < data.Height; y++)
                    System.Runtime.InteropServices.Marshal.Copy(data.Bytes, y * rowBytes,
                        buffer.Address + y * buffer.RowBytes, rowBytes);
                return bitmap;
            }
            byte[] row = new byte[width * 4];
            int area = factor * factor;
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int dy = 0; dy < factor; dy++)
                    {
                        int index = ((y * factor + dy) * data.Width + x * factor) * 4;
                        for (int dx = 0; dx < factor; dx++, index += 4)
                        {
                            r += data.Bytes[index]; g += data.Bytes[index + 1];
                            b += data.Bytes[index + 2]; a += data.Bytes[index + 3];
                        }
                    }
                    int target = x * 4;
                    row[target] = (byte)(r / area); row[target + 1] = (byte)(g / area);
                    row[target + 2] = (byte)(b / area); row[target + 3] = (byte)(a / area);
                }
                System.Runtime.InteropServices.Marshal.Copy(row, 0, buffer.Address + y * buffer.RowBytes, row.Length);
            }
            return bitmap;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private void ShowTrophies(Ps5TrophySet? set)
    {
        _trophySet = set;
        TrophyList.ItemsSource = null;
        DisposeAll(_trophyBitmaps);
        TrophyList.ItemsSource = set?.Trophies.Select(trophy => new TrophyRow
        {
            Name = trophy.Hidden ? trophy.Name + " (hidden)" : trophy.Name,
            Description = trophy.Description,
            Grade = trophy.Grade,
            Icon = Track(ToBitmap(trophy.Icon), _trophyBitmaps)
        }).ToList();
        TrophySummary.Text = set is null
            ? "No trophy data."
            : $"{set.Trophies.Count:N0} trophies" + (set.IntegrityValid ? string.Empty : "  ·  integrity check failed");
    }

    private async Task ExportTrophiesAsync()
    {
        if (_trophySet is null || _trophySet.Trophies.Count == 0) return;
        string? folder = await Dialogs.PickFolderAsync(this, "Export trophy icons to...");
        if (folder is null) return;
        int written = 0;
        try
        {
            foreach (Ps5Trophy trophy in _trophySet.Trophies.Where(trophy => trophy.IconPng is { Length: > 0 }))
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, $"TROP{trophy.Id:D3}.png"), trophy.IconPng!);
                written++;
            }
            Status($"Exported {written:N0} trophy icon(s).");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Export trophies", ex.Message);
        }
    }

    // ---------------------------------------------------------------- context menu actions

    private void OnContextMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_selection.Count == 0) { e.Cancel = true; return; }
        if (sender is not ContextMenu menu) return;
        foreach (MenuItem item in menu.Items.OfType<MenuItem>().Where(item => item.Header as string == "Rename"))
        {
            item.Items.Clear();
            foreach (Ps5RenameFormat preset in Ps5RenameFormats.Presets)
            {
                string format = preset.Format;
                var entry = new MenuItem { Header = preset.Label };
                entry.Click += async (_, _) => await RenameSelectedAsync(format);
                item.Items.Add(entry);
            }
            item.Items.Add(new Separator());
            var custom = new MenuItem { Header = Ps5RenameFormats.CustomLabel };
            custom.Click += async (_, _) => await RenameSelectedAsync(LibraryActions.DefaultRenameFormat(_services.Settings));
            item.Items.Add(custom);
        }
    }

    private void OnMenuConvert(object? sender, RoutedEventArgs e)
    {
        if (_selection.FirstOrDefault() is { } item) _services.RequestToolsSource(item.Path, "convert");
    }

    private void OnMenuExtract(object? sender, RoutedEventArgs e)
    {
        if (_selection.FirstOrDefault() is { } item) _services.RequestToolsSource(item.Path, "extract");
    }

    private void OnMenuVerify(object? sender, RoutedEventArgs e)
    {
        if (_selection.FirstOrDefault() is { } item) _services.RequestToolsSource(item.Path, "verify");
    }

    private async void OnMenuEdit(object? sender, RoutedEventArgs e) => await EditSelectedAsync();

    private async void OnMenuSend(object? sender, RoutedEventArgs e)
    {
        if (_selection.FirstOrDefault() is not { } item) return;
        if (await SendToConsoleWindow.ShowAsync(Dialogs.OwnerOf(this), item.Path, item.Title))
            Status($"Queued: send {item.Title} to a console. See the Tasks page.");
    }

    private async Task EditSelectedAsync()
    {
        if (_selection.FirstOrDefault() is not { } item) return;
        EditOutcome? outcome = await EditDetailsWindow.ShowAsync(Dialogs.OwnerOf(this), item);
        if (outcome is null) return;
        if (outcome.Target == EditTarget.Library)
        {
            item.ResetThumbnail();
            _ = item.Thumbnail;
            AutoSizeColumns();
            ApplyFilters();
            await ShowSelectionAsync();
            Status($"Saved {item.Title} in the library only; {item.FileName} was not changed. " +
                   "Open Edit details and choose \"Write into the file itself\" to update the file.");
            return;
        }

        var spec = new JobSpec
        {
            Kind = JobSpec.EditDetails,
            Title = item.Title,
            Source = item.Path,
            Output = outcome.Output,
            FromPackage = item.Game.SourceKind == Ps5SourceKind.SonyPackage,
            Passcode = _services.Settings.DebugPasscode,
            Backend = _services.Settings.BuildBackend,
            Edit = outcome.Edit,
            Icon = outcome.Edit is { IconPath.Length: > 0 } edit
                ? Convert.ToBase64String(await File.ReadAllBytesAsync(edit.IconPath))
                : null
        };
        QueuedPackageTask task = _services.EnqueueJob(spec, item.Title, "Edit details", item.Format,
            outcome.Target == EditTarget.InPlace ? "In place" : "New copy");
        string written = outcome.Output;
        task.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (task.Status != PackageTaskStatus.Completed) return;
            if (written.Length > 0) _services.AddManualSource(written);
            _ = RefreshAsync();
        });
        Status("Edit queued. Follow it on the Tasks page.");
    }

    private void OnMenuReveal(object? sender, RoutedEventArgs e)
    {
        if (_selection.FirstOrDefault() is { } item) Dialogs.Reveal(item.Path);
    }

    private async void OnMenuCopyId(object? sender, RoutedEventArgs e) => await CopyAsync(_selection.FirstOrDefault()?.TitleId);
    private async void OnMenuCopyContent(object? sender, RoutedEventArgs e) => await CopyAsync(_selection.FirstOrDefault()?.ContentId);
    private async void OnMenuCopyPath(object? sender, RoutedEventArgs e) => await CopyAsync(_selection.FirstOrDefault()?.Path);
    private async void OnMenuMove(object? sender, RoutedEventArgs e) => await MoveSelectedAsync();
    private async void OnMenuDelete(object? sender, RoutedEventArgs e) => await DeleteSelectedAsync();

    private async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
    }

    private async Task RenameSelectedAsync(string format) =>
        await ApplyRenamePlansAsync(LibraryActions.PlanRenames(SelectedGames(), format));

    private async Task RenameByInstallOrderAsync()
    {
        List<Ps5GameInfo> packages = SelectedGames().Where(game => game.SourceKind == Ps5SourceKind.SonyPackage).ToList();
        if (packages.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Rename by install order", "Select one or more package (.pkg) files first.");
            return;
        }
        await ApplyRenamePlansAsync(LibraryActions.PlanInstallOrder(packages, LibraryActions.DefaultRenameFormat(_services.Settings)));
    }

    private async Task ApplyRenamePlansAsync(IReadOnlyList<RenamePlan> plans)
    {
        if (plans.Count == 0) return;
        int moves = plans.Count(plan => plan.Error is null && plan.Target.Length > 0);
        string header = $"{moves:N0} of {plans.Count:N0} item(s) will be renamed.";
        if (moves == 0)
        {
            await Dialogs.ConfirmTextAsync(Dialogs.OwnerOf(this), "Rename", header, LibraryActions.PreviewText(plans), "", "Close");
            return;
        }
        if (!await Dialogs.ConfirmTextAsync(Dialogs.OwnerOf(this), "Rename", header, LibraryActions.PreviewText(plans), "Rename"))
            return;
        int renamed = LibraryActions.ApplyRenames(plans);
        Status($"Renamed {renamed:N0} item(s).");
        await RefreshAsync();
    }

    private async Task MoveSelectedAsync()
    {
        List<Ps5GameInfo> games = SelectedGames();
        if (games.Count == 0) return;
        string? folder = await Dialogs.PickFolderAsync(this, "Move to folder...");
        if (folder is null) return;
        if (_services.Settings.ConfirmMove && !await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Move",
                $"Move {games.Count:N0} item(s) to {folder}?", "Move"))
            return;
        Status($"Moving {games.Count:N0} item(s)...");
        try
        {
            int moved = await Task.Run(() => LibraryActions.MoveAll(games, folder, CancellationToken.None));
            Status($"Moved {moved:N0} item(s) to {folder}.");
            _services.AddLibraryFolder(folder);
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            Logger.Exception("Move", ex);
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Move", ex.Message);
        }
    }

    private async Task DeleteSelectedAsync()
    {
        List<Ps5GameInfo> games = SelectedGames();
        if (games.Count == 0) return;
        if (!await Dialogs.ConfirmTextAsync(Dialogs.OwnerOf(this), "Delete",
                $"Permanently delete {games.Count:N0} item(s) from disk? This cannot be undone.",
                string.Join("\n", games.Select(game => game.RootPath)), "Delete permanently"))
            return;
        int deleted = await Task.Run(() => LibraryActions.DeleteAll(games));
        Status($"Deleted {deleted:N0} item(s).");
        await RefreshAsync();
    }

    /// <summary>Writes what is currently shown as a JSON file or a single web page.</summary>
    private async Task ExportReportAsync(bool html)
    {
        List<LibraryItem> items = _shown.ToList();
        if (items.Count == 0)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Export", "There is nothing to export. Add a folder or clear the filters first.");
            return;
        }
        bool paths = !await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Include file paths?",
            "The file paths contain your folder and user names. Leave them out if you plan to share the file.",
            "Leave paths out", "Include paths");
        string? file = await Dialogs.SaveFileAsync(this, html ? "Export a web page" : "Export JSON",
            html ? "ps5-library.html" : "ps5-library.json", html ? ".html" : ".json");
        if (file is null) return;
        try
        {
            string text;
            if (html)
            {
                var icons = new System.Collections.Concurrent.ConcurrentDictionary<string, byte[]>();
                int done = 0;
                await Task.Run(() => Parallel.ForEach(items, new ParallelOptions { MaxDegreeOfParallelism = 3 }, item =>
                {
                    if (LibraryExport.ReadSmallIcon(item) is { } png) icons[item.Path] = png;
                    int count = Interlocked.Increment(ref done);
                    if (count % 10 == 0) Dispatcher.UIThread.Post(() => Status($"Preparing artwork... {count:N0} of {items.Count:N0}"));
                }));
                text = LibraryExport.ToHtml(items, paths, icons);
            }
            else text = LibraryExport.ToJson(items, paths);
            await File.WriteAllTextAsync(file, text, new UTF8Encoding(false));
            Status($"Exported {items.Count:N0} title(s) to {file}.");
            if (html && await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Export finished", "Open the page now?", "Open", "Close"))
                Dialogs.OpenPath(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Export", ex.Message);
        }
    }

    private async Task ExportAsync()
    {
        string? file = await Dialogs.SaveFileAsync(this, "Export CSV", "ps5-library.csv", ".csv");
        if (file is null) return;
        try
        {
            await File.WriteAllTextAsync(file, LibraryActions.ToCsv(_shown), Encoding.UTF8);
            Status($"Exported {_shown.Count:N0} row(s) to {file}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Export", ex.Message);
        }
    }
}
