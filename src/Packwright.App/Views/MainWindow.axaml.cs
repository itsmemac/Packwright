using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Packwright.App.Services;
using Packwright.Core.Tasks;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Control[] _pages;
    private readonly RadioButton[] _nav;

    public MainWindow() : this([]) { }

    public MainWindow(string[] args)
    {
        InitializeComponent();
        AppServices services = AppServices.Instance;
        VersionLabel.Text = "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
        _pages = [LibraryPane, ToolsPane, TasksPane, HealthPane, LogPane, SettingsPane, AboutPane];
        _nav = [NavLibrary, NavTools, NavTasks, NavHealth, NavLog, NavSettings, NavAbout];
        for (int i = 0; i < _nav.Length; i++)
        {
            int index = i;
            _nav[i].IsCheckedChanged += (_, _) => { if (_nav[index].IsChecked == true) ShowPage(index); };
        }

        InitializeThemeButton(services);
        RootGrid.SizeChanged += (_, e) => ApplySidebarMode(e.NewSize.Width);
        RestoreWindowBounds(services);
        Closing += (_, _) => SaveWindowBounds(services);
        // Remember maximize as soon as it changes, so it survives even if the app is not closed cleanly.
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && IsLoaded) SaveWindowBounds(services);
        };
        // Hand memory back when the user switches to another program.
        Deactivated += (_, _) => MemoryTrimmer.Schedule(5);

        AuthorLink.Click += (_, _) => Dialogs.OpenPath(AboutView.RepositoryUrl);
        UpdateService.Changed += () => Dispatcher.UIThread.Post(RefreshUpdateLink);
        UpdateLink.Click += async (_, _) =>
        {
            if (UpdateService.Available is { } release) await UpdateWindow.ShowAsync(this, release);
        };
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        services.ToolsSourceRequested += (path, action) =>
        {
            NavTools.IsChecked = true;
            ToolsPane.SetSource(path);
            if (action is not null) ToolsPane.ChooseAction(action);
        };
        LibraryPane.StatusChanged += text => StatusText.Text = text;
        HealthPane.StatusChanged += text => StatusText.Text = text;
        services.ShowTitleRequested += async (path, edit) =>
        {
            NavLibrary.IsChecked = true;
            if (edit) await LibraryPane.EditTitleAsync(path);
            else await LibraryPane.ShowTitleAsync(path);
        };
        ToolsPane.StatusChanged += text => StatusText.Text = text;
        SettingsPane.StatusChanged += text => StatusText.Text = text;
        _timer.Tick += (_, _) => UpdateTaskSummary();
        _timer.Start();
        Opened += async (_, _) =>
        {
            if (services.SettingsWarning is { } warning)
                await Dialogs.ShowMessageAsync(this, "Settings", warning);
            await LibraryPane.InitializeAsync();
            MemoryTrimmer.Schedule(6);
            await OfferUpdateChecksAsync(services);
            string? opened = args.FirstOrDefault(arg => File.Exists(arg) || Directory.Exists(arg));
            if (opened is not null) services.RequestToolsSource(opened);
        };
    }

    private static readonly string[] ThemeModes = ["Dark", "Light", "System"];
    private bool? _compactSidebar;

    private static string ThemeMode(string? saved) =>
        ThemeModes.FirstOrDefault(mode => string.Equals(mode, saved, StringComparison.OrdinalIgnoreCase)) ?? "Dark";

    private void InitializeThemeButton(AppServices services)
    {
        UpdateThemeButton(services);
        ThemeButton.Click += (_, _) =>
        {
            string next = ThemeModes[(Array.IndexOf(ThemeModes, ThemeMode(services.Settings.Theme)) + 1) % ThemeModes.Length];
            services.Settings.Theme = next;
            SettingsView.ApplyTheme(next);
            services.SaveSettings();
            UpdateThemeButton(services);
            services.NotifySettingsChanged();
        };
        // Other places that change the theme (importing settings, for example) keep the button in step.
        services.SettingsChanged += () => Dispatcher.UIThread.Post(() => UpdateThemeButton(services));
    }

    private void UpdateThemeButton(AppServices services)
    {
        string mode = ThemeMode(services.Settings.Theme);
        ThemeIcon.Data = Avalonia.Media.Geometry.Parse(mode switch
        {
            // Material icons: sun, moon, monitor
            "Light" => "M6.76 4.84l-1.8-1.79-1.41 1.41 1.79 1.79 1.42-1.41zM4 10.5H1v2h3v-2zm9-9.95h-2V3.5h2V.55zm7.45 3.91l-1.41-1.41-1.79 1.79 1.41 1.41 1.79-1.79zm-3.21 13.7l1.79 1.8 1.41-1.41-1.8-1.79-1.4 1.4zM20 10.5v2h3v-2h-3zm-8-5c-3.31 0-6 2.69-6 6s2.69 6 6 6 6-2.69 6-6-2.69-6-6-6zm-1 16.95h2V19.5h-2v2.95zm-7.45-3.91l1.41 1.41 1.79-1.8-1.41-1.41-1.79 1.8z",
            "System" => "M21 2H3c-1.1 0-2 .9-2 2v12c0 1.1.9 2 2 2h7v2H8v2h8v-2h-2v-2h7c1.1 0 2-.9 2-2V4c0-1.1-.9-2-2-2zm0 14H3V4h18v12z",
            _ => "M12 3a9 9 0 1 0 9 9c0-.46-.04-.92-.1-1.36a5.389 5.389 0 0 1-4.4 2.26 5.403 5.403 0 0 1-3.14-9.8c-.44-.06-.9-.1-1.36-.1z"
        });
        ToolTip.SetTip(ThemeButton, $"Theme: {mode}. Click to switch (Dark, Light, System).");
    }

    /// <summary>Below about 1100 px the sidebar shrinks to icons so pages keep room to work.</summary>
    private void ApplySidebarMode(double width)
    {
        if (width <= 0) return;
        bool compact = _compactSidebar == true ? width < 1140 : width < 1100;
        if (_compactSidebar == compact) return;
        _compactSidebar = compact;

        RootGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 68 : 236);
        Sidebar.Padding = compact ? new Thickness(8, 18, 8, 14) : new Thickness(16, 18, 16, 14);
        BrandText.IsVisible = WorkspaceLabel.IsVisible = !compact;
        BrandLogo.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Left;
        BrandRow.Margin = compact ? new Thickness(0, 0, 0, 18) : new Thickness(4, 0, 0, 26);
        BrandRow.ColumnDefinitions = compact
            ? new ColumnDefinitions("*,0,0")
            : new ColumnDefinitions("Auto,*,Auto");
        Grid.SetColumn(ThemeButton, compact ? 0 : 2);
        Grid.SetRow(ThemeButton, compact ? 1 : 0);
        ThemeButton.HorizontalAlignment = compact ? Avalonia.Layout.HorizontalAlignment.Center : Avalonia.Layout.HorizontalAlignment.Stretch;
        ThemeButton.Margin = compact ? new Thickness(0, 10, 0, 0) : default;

        (RadioButton Button, TextBlock Label)[] items =
        [
            (NavLibrary, NavLibraryText), (NavTools, NavToolsText), (NavTasks, NavTasksText),
            (NavHealth, NavHealthText), (NavLog, NavLogText), (NavSettings, NavSettingsText), (NavAbout, NavAboutText)
        ];
        foreach ((RadioButton button, TextBlock label) in items)
        {
            label.IsVisible = !compact;
            ToolTip.SetTip(button, compact ? label.Text : null);
            if (button.Content is Control content)
                content.HorizontalAlignment = compact
                    ? Avalonia.Layout.HorizontalAlignment.Center
                    : Avalonia.Layout.HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = compact
                ? Avalonia.Layout.HorizontalAlignment.Center
                : Avalonia.Layout.HorizontalAlignment.Left;
            if (compact) button.Padding = new Thickness(0, 10);
            else button.ClearValue(Avalonia.Controls.Primitives.TemplatedControl.PaddingProperty);
        }
        // With the label hidden, the task count becomes a small badge on the corner of the icon; otherwise it sits at the right edge.
        TasksRow.ColumnDefinitions = compact ? new ColumnDefinitions("Auto") : new ColumnDefinitions("Auto,*,Auto");
        Grid.SetColumn(TaskBadge, compact ? 0 : 2);
        TaskBadge.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right;
        TaskBadge.VerticalAlignment = compact ? Avalonia.Layout.VerticalAlignment.Top : Avalonia.Layout.VerticalAlignment.Center;
        TaskBadge.Margin = compact ? new Thickness(0, -7, -9, 0) : default;
        TaskBadge.MinWidth = compact ? 16 : 20;
        TaskBadge.Height = compact ? 16 : 20;
        TaskBadge.CornerRadius = new CornerRadius(compact ? 8 : 10);
        TaskBadge.Padding = new Thickness(compact ? 4 : 6, 0);
        TaskBadge.ZIndex = 5;
        TaskBadgeText.FontSize = compact ? 10 : 11;
    }

    /// <summary>Puts the window back where, and as big as, the user left it.</summary>
    private void RestoreWindowBounds(AppServices services)
    {
        var settings = services.Settings;
        if (settings.WindowWidth >= MinWidth && settings.WindowHeight >= MinHeight)
        {
            Width = settings.WindowWidth;
            Height = settings.WindowHeight;
        }
        if (settings.WindowHasPosition)
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = new PixelPoint(settings.WindowX, settings.WindowY);
        }
        if (settings.WindowMaximized) WindowState = WindowState.Maximized;
        // A monitor may have been unplugged since: if the saved spot is off every screen, centre instead.
        Opened += (_, _) =>
        {
            if (WindowState != WindowState.Normal) return;
            // The display may be smaller than when the size was saved: never open bigger than the screen.
            if ((Screens.ScreenFromWindow(this) ?? Screens.Primary) is { } current)
            {
                // Sizes here are the client area; the title bar and borders come on top (FrameSize).
                double scale = RenderScaling > 0 ? RenderScaling : 1;
                Size outer = FrameSize ?? new Size(Bounds.Width, Bounds.Height);
                double chromeWidth = Math.Max(0, outer.Width - Bounds.Width), chromeHeight = Math.Max(0, outer.Height - Bounds.Height);
                double maxWidth = current.WorkingArea.Width / scale - chromeWidth;
                double maxHeight = current.WorkingArea.Height / scale - chromeHeight;
                if (Bounds.Width > maxWidth) Width = Math.Max(MinWidth, maxWidth);
                if (Bounds.Height > maxHeight) Height = Math.Max(MinHeight, maxHeight);
                int frameWidth = (int)((Math.Min(Bounds.Width, maxWidth) + chromeWidth) * scale);
                int frameHeight = (int)((Math.Min(Bounds.Height, maxHeight) + chromeHeight) * scale);
                Position = new PixelPoint(
                    Math.Clamp(Position.X, current.WorkingArea.X, Math.Max(current.WorkingArea.X, current.WorkingArea.Right - frameWidth)),
                    Math.Clamp(Position.Y, current.WorkingArea.Y, Math.Max(current.WorkingArea.Y, current.WorkingArea.Bottom - frameHeight)));
            }
            if (!settings.WindowHasPosition) return;
            var frame = new PixelRect(Position, new PixelSize(Math.Max(1, (int)(Bounds.Width * (RenderScaling))),
                Math.Max(1, (int)(Bounds.Height * RenderScaling))));
            if (Screens.All.Any(screen => screen.WorkingArea.Intersects(frame))) return;
            if (Screens.Primary is { } primary)
                Position = new PixelPoint(
                    primary.WorkingArea.X + Math.Max(0, (primary.WorkingArea.Width - frame.Width) / 2),
                    primary.WorkingArea.Y + Math.Max(0, (primary.WorkingArea.Height - frame.Height) / 2));
        };
    }

    private void SaveWindowBounds(AppServices services)
    {
        var settings = services.Settings;
        if (WindowState == WindowState.Minimized) return;
        settings.WindowMaximized = WindowState == WindowState.Maximized;
        if (WindowState == WindowState.Normal)
        {
            settings.WindowWidth = (int)Math.Round(Bounds.Width);
            settings.WindowHeight = (int)Math.Round(Bounds.Height);
            settings.WindowX = Position.X;
            settings.WindowY = Position.Y;
            settings.WindowHasPosition = true;
        }
        services.SaveSettings();
    }

    /// <summary>Raises the window (restoring it if minimized) and opens a file the second launch was given.</summary>
    public void BringToFront(string[] paths)
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;   // a plain Activate() is ignored by Windows when another app has the focus
        Topmost = false;
        string? opened = paths.FirstOrDefault(path => File.Exists(path) || Directory.Exists(path));
        if (opened is not null) AppServices.Instance.RequestToolsSource(opened);
    }

    private void RefreshUpdateLink()
    {
        UpdateLink.IsVisible = UpdateService.HasVisibleUpdate;
        if (UpdateService.Available is { } release) UpdateLinkText.Text = $"Update {release.Version} available";
    }

    /// <summary>Asks once whether to check for updates; after that, checks quietly once a day if the user said yes.</summary>
    private async Task OfferUpdateChecksAsync(AppServices services)
    {
        AppSettings settings = services.Settings;
        if (!settings.UpdateCheckAsked)
        {
            settings.UpdateCheckAsked = true;
            settings.CheckForUpdates = await Dialogs.ConfirmAsync(this, "Check for updates?",
                "Packwright can look for new versions on GitHub once a day and tell you when one is out.\n\n" +
                "It sends nothing but an ordinary web request to github.com (as any website sees your connection). " +
                "It never installs anything without you pressing a button. You can change this in Settings.",
                "Yes, check", "No thanks");
            services.SaveSettings();
        }
        if (UpdateService.ShouldCheckOnStartup()) _ = UpdateService.CheckAsync();
    }

    private void ShowPage(int index)
    {
        MemoryTrimmer.Schedule();
        if (index == Array.IndexOf(_pages, SettingsPane)) SettingsPane.RefreshLayoutChoice();
        if (index == Array.IndexOf(_pages, HealthPane)) HealthPane.OnShown();
        for (int i = 0; i < _pages.Length; i++) _pages[i].IsVisible = i == index;
    }

    private void UpdateTaskSummary()
    {
        try
        {
            UpdateTaskSummaryCore();
        }
        catch (Exception ex)
        {
            Packwright.Infrastructure.Logger.Exception("Task summary", ex);
        }
    }

    private void UpdateTaskSummaryCore()
    {
        IReadOnlyList<QueuedPackageTask> tasks = AppServices.Instance.Queue.Tasks;
        int running = tasks.Count(task => task.Status == PackageTaskStatus.Running);
        int queued = tasks.Count(task => task.Status == PackageTaskStatus.Queued);
        TaskSummary.Text = running + queued == 0 ? string.Empty : $"{running} running, {queued} queued";
        TaskBadge.IsVisible = running + queued > 0;
        TaskBadgeText.Text = running + queued > 99 ? "99+" : (running + queued).ToString();
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        IStorageItem? item = e.Data.GetFiles()?.FirstOrDefault();
        if (item?.TryGetLocalPath() is { } path)
            AppServices.Instance.RequestToolsSource(path);
    }
}
