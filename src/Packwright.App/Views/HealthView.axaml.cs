using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

/// <summary>The library health page: a quick check of everything the library already knows, and an optional slower one.</summary>
public partial class HealthView : UserControl
{
    private sealed class IssueRow
    {
        public required HealthIssue Issue { get; init; }
        public IBrush Dot { get; init; } = Brushes.Gray;
        public string Title => Issue.Title;
        public string Kind => Issue.KindLabel;
        public string Message => Issue.Message;
        public string Path => Issue.ItemPath;
        public bool HasReclaim => Issue.ReclaimableBytes > 0;
        public string ReclaimText => "takes " + Ps5LibraryHealth.FormatBytes(Issue.ReclaimableBytes);
        public bool CanFix { get; init; }
        public bool CanShow { get; init; }
        public bool CanReveal { get; init; }
    }

    private static readonly IBrush Red = new SolidColorBrush(Color.Parse("#F0626B"));
    private static readonly IBrush Amber = new SolidColorBrush(Color.Parse("#F5B642"));
    private static readonly IBrush Blue = new SolidColorBrush(Color.Parse("#7C8CFF"));
    private static readonly IBrush Green = new SolidColorBrush(Color.Parse("#3DD68C"));

    private readonly AppServices _services = AppServices.Instance;
    private HealthReport? _report;
    private HealthSeverity? _filter;
    private CancellationTokenSource? _cancellation;
    private bool _ranOnce;

    public HealthView()
    {
        InitializeComponent();
        RunButton.Click += async (_, _) => await RunAsync(deep: false);
        DeepButton.Click += async (_, _) => await RunAsync(deep: true);
        StopButton.Click += (_, _) => _cancellation?.Cancel();
        ExportButton.Click += async (_, _) => await ExportAsync();
        foreach ((RadioButton button, HealthSeverity? severity) in new (RadioButton, HealthSeverity?)[]
                 { (FilterAll, null), (FilterProblems, HealthSeverity.Problem), (FilterWarnings, HealthSeverity.Warning), (FilterNotes, HealthSeverity.Info) })
            button.IsCheckedChanged += (_, _) =>
            {
                if (button.IsChecked != true) return;
                _filter = severity;
                ShowIssues();
            };
        // A refreshed library changes the answers, so a page that is on screen re-checks itself.
        _services.LibraryChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (IsEffectivelyVisible && _cancellation is null) _ = RunAsync(deep: false);
        });
    }

    public event Action<string>? StatusChanged;

    /// <summary>Called when the page is opened: the quick check is cheap, so it always shows current results.</summary>
    public void OnShown()
    {
        if (_cancellation is null) _ = RunAsync(deep: false);
    }

    internal static HealthItem ToHealthItem(LibraryItem item) => new(
        item.Path, item.FileName, item.Title, item.TitleId, item.ContentId, item.Version, item.Category,
        item.SizeBytes, item.Format,
        item.Game.RawParamJson.Length == 0
            ? string.Empty
            : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Game.RawParamJson))),
        item.MissingFields,
        item.Game.DataWarnings.FirstOrDefault(warning => warning.Contains("could not be read", StringComparison.OrdinalIgnoreCase)) ?? string.Empty);

    private async Task RunAsync(bool deep)
    {
        if (_cancellation is not null) return;
        _cancellation = new CancellationTokenSource();
        CancellationToken token = _cancellation.Token;
        SetBusy(true, deep ? "Checking the library..." : "Checking...");
        try
        {
            List<LibraryItem> items = _services.Games.Select(game => new LibraryItem(game)).ToList();
            List<HealthItem> health = items.Select(ToHealthItem).ToList();
            IReadOnlyList<string> problems = _services.ScanProblems;
            HealthReport report = await Task.Run(() => Ps5LibraryHealth.Analyze(health, problems), token);

            if (deep)
            {
                int done = 0;
                foreach (LibraryItem item in items)
                {
                    token.ThrowIfCancellationRequested();
                    ProgressText.Text = $"Looking inside {item.Title}...  {++done:N0} of {items.Count:N0}";
                    ProgressBar.Value = done * 100.0 / Math.Max(1, items.Count);
                    HealthItem healthItem = ToHealthItem(item);
                    List<HealthIssue> found = await Task.Run(() => DeepCheck(item, healthItem), token);
                    report.Issues.AddRange(found);
                }
                Ps5LibraryHealth.Sort(report);
            }

            _report = report;
            _ranOnce = true;
            ShowReport(report, deep);
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Health check stopped.");
            if (_report is not null) ShowReport(_report, deep: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Logger.Exception("Health check", ex);
            SubText.Text = "The check could not finish: " + ex.Message;
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false, string.Empty);
        }
    }

    /// <summary>The slower checks for one title: is there an icon, and (for an unpacked dump) can it launch.</summary>
    private static List<HealthIssue> DeepCheck(LibraryItem item, HealthItem healthItem)
    {
        var issues = new List<HealthIssue>();
        if (!File.Exists(item.Path) && !Directory.Exists(item.Path)) return issues;
        bool hasIcon = MetadataOverrides.IconPath(MetadataOverrides.Get(item.Path)) is not null;
        if (!hasIcon)
        {
            try
            {
                using IReadOnlyGameFileSystem files = GameFileSystem.Open(item.Game);
                hasIcon = files.FileExists("sce_sys/icon0.png");
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or
                                           NotSupportedException or ArgumentException)
            {
                // Unreadable titles are already reported by the quick check.
                return issues;
            }
        }
        if (Ps5LibraryHealth.IconIssue(healthItem, hasIcon) is { } iconIssue) issues.Add(iconIssue);
        if (item.Game.SourceKind == Ps5SourceKind.LooseDump) issues.AddRange(Ps5LibraryHealth.LaunchIssues(healthItem));
        return issues;
    }

    private void SetBusy(bool busy, string text)
    {
        ProgressHost.IsVisible = busy && text.Length > 0;
        ProgressText.Text = text;
        ProgressBar.IsIndeterminate = busy && text == "Checking...";
        if (!busy) ProgressBar.Value = 0;
        RunButton.IsEnabled = DeepButton.IsEnabled = !busy;
        StopButton.IsVisible = busy && text != "Checking...";
    }

    private void ShowReport(HealthReport report, bool deep)
    {
        int problems = report.Count(HealthSeverity.Problem), warnings = report.Count(HealthSeverity.Warning);
        CardTitles.Text = report.ItemCount.ToString("N0");
        CardSize.Text = Ps5LibraryHealth.FormatBytes(report.TotalBytes);
        CardProblems.Text = problems.ToString("N0");
        CardProblems.Foreground = problems > 0 ? Red : Green;
        CardWarnings.Text = warnings.ToString("N0");
        CardWarnings.Foreground = warnings > 0 ? Amber : Green;
        CardReclaim.Text = Ps5LibraryHealth.FormatBytes(report.ReclaimableBytes);
        SubText.Text = $"Checked {DateTime.Now:HH:mm}" + (deep ? ", including the inside of every title." : ". ") +
                       (deep ? string.Empty : "\"Check files too\" also looks for missing icons and launch problems.") +
                       (report.Drives.Count > 0
                           ? "  " + string.Join("  ", report.Drives.Select(drive =>
                               $"{drive.Root.TrimEnd('\\', '/')} {Ps5LibraryHealth.FormatBytes(drive.FreeBytes)} free"))
                           : string.Empty);
        ShowIssues();
        StatusChanged?.Invoke(report.Issues.Count == 0
            ? "Library health: nothing to fix."
            : $"Library health: {problems:N0} problem(s), {warnings:N0} warning(s).");
    }

    private void ShowIssues()
    {
        if (_report is null) return;
        List<IssueRow> rows = _report.Issues
            .Where(issue => _filter is null || issue.Severity == _filter)
            .Select(issue => new IssueRow
            {
                Issue = issue,
                Dot = issue.Severity switch { HealthSeverity.Problem => Red, HealthSeverity.Warning => Amber, _ => Blue },
                CanFix = issue.Kind is HealthKind.MissingDetails or HealthKind.NoIcon && IsInLibrary(issue.ItemPath),
                CanShow = issue.Kind != HealthKind.Unreadable && issue.Kind != HealthKind.LowDiskSpace && IsInLibrary(issue.ItemPath),
                CanReveal = issue.Kind != HealthKind.NotFound && (File.Exists(issue.ItemPath) || Directory.Exists(issue.ItemPath))
            }).ToList();
        IssueList.ItemsSource = rows;
        EmptyState.IsVisible = rows.Count == 0;
        IssueList.IsVisible = rows.Count > 0;
        EmptyTitle.Text = _report.Issues.Count == 0 ? "Nothing to fix" : "Nothing in this group";
        EmptyHint.Text = _report.Issues.Count == 0
            ? "The library looks healthy."
            : "Choose All, or another group, to see the rest.";
    }

    private bool IsInLibrary(string path) =>
        _services.Games.Any(game => string.Equals(game.RootPath, path, StringComparison.OrdinalIgnoreCase));

    private static IssueRow? RowOf(object? sender) => (sender as Control)?.DataContext as IssueRow;

    private void OnShow(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _services.RequestShowTitle(row.Path, edit: false);
    }

    private void OnFix(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) _services.RequestShowTitle(row.Path, edit: true);
    }

    private void OnReveal(object? sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } row) Dialogs.Reveal(row.Path);
    }

    private async Task ExportAsync()
    {
        if (!_ranOnce || _report is null) await RunAsync(deep: false);
        if (_report is null) return;
        string? file = await Dialogs.SaveFileAsync(this, "Export the health report", "library-health.md", ".md");
        if (file is null) return;
        try
        {
            await File.WriteAllTextAsync(file, Ps5LibraryHealth.ToMarkdown(_report, issue => issue.ItemPath), Encoding.UTF8);
            StatusChanged?.Invoke($"Saved the health report to {file}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Export", ex.Message);
        }
    }
}
