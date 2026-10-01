using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Packwright.App.Services;
using Packwright.Infrastructure;
using Packwright.Core.Tasks;

namespace Packwright.App.Views;

public partial class TasksView : UserControl
{
    private sealed class TaskRow
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string Operation { get; init; } = string.Empty;
        public string Source { get; init; } = string.Empty;
        public bool HasOutput { get; init; }
        public Bitmap? Icon { get; init; }
        public bool HasIcon => Icon is not null;
        public string Route { get; init; } = string.Empty;
        public string StatusText { get; init; } = string.Empty;
        public double Percent { get; init; }
        public string Message { get; init; } = string.Empty;
        public string Output { get; init; } = string.Empty;
        public string Details { get; init; } = string.Empty;
        public IBrush BarBrush { get; init; } = Brushes.Transparent;
        public bool Indeterminate { get; init; }
        public IBrush StatusBackground { get; init; } = Brushes.Transparent;
        public IBrush StatusForeground { get; init; } = Brushes.White;
    }

    private static (IBrush Background, IBrush Foreground) StatusBrushes(PackageTaskStatus status) => status switch
    {
        PackageTaskStatus.Completed => (new SolidColorBrush(Color.Parse("#2E3DD68C")), new SolidColorBrush(Color.Parse("#3DD68C"))),
        PackageTaskStatus.Failed => (new SolidColorBrush(Color.Parse("#2EFF6B7A")), new SolidColorBrush(Color.Parse("#FF6B7A"))),
        PackageTaskStatus.Cancelled or PackageTaskStatus.Interrupted or PackageTaskStatus.Cancelling =>
            (new SolidColorBrush(Color.Parse("#2EF5B642")), new SolidColorBrush(Color.Parse("#F5B642"))),
        PackageTaskStatus.Queued => (new SolidColorBrush(Color.Parse("#26808AA8")), new SolidColorBrush(Color.Parse("#8E94AE"))),
        _ => (new SolidColorBrush(Color.Parse("#267C8CFF")), new SolidColorBrush(Color.Parse("#7C8CFF")))
    };

    private readonly AppServices _services = AppServices.Instance;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private string _signature = string.Empty;
    private readonly Dictionary<string, Bitmap?> _icons = [];

    private Bitmap? IconFor(string id, JobSpec? spec)
    {
        if (_icons.TryGetValue(id, out Bitmap? cached)) return cached;
        Bitmap? bitmap = null;
        if (spec?.Icon is { Length: > 0 } encoded)
        {
            try { bitmap = new Bitmap(new MemoryStream(Convert.FromBase64String(encoded))); }
            catch (Exception ex) when (ex is FormatException or ArgumentException or IOException) { }
        }
        _icons[id] = bitmap;
        return bitmap;
    }

    public TasksView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        CancelButton.Click += (_, _) => { if (Selected() is { } row) _services.Queue.Cancel(row.Id); };
        RetryButton.Click += (_, _) => { if (Selected() is { } row) _services.Queue.Retry(row.Id); };
        RemoveButton.Click += (_, _) => { if (Selected() is { } row) _services.Queue.Remove(row.Id); Refresh(); };
        ClearButton.Click += (_, _) => { _services.Queue.ClearCompleted(); Refresh(); };
        OpenButton.Click += (_, _) =>
        {
            if (Selected() is { Output.Length: > 0 } row && (File.Exists(row.Output) || Directory.Exists(row.Output)))
                Dialogs.Reveal(row.Output);
        };
        AutoStartBox.IsCheckedChanged += (_, _) =>
        {
            bool auto = AutoStartBox.IsChecked == true;
            _services.Queue.AutoStart = auto;
            RunQueueButton.IsVisible = !auto;
        };
        RunQueueButton.Click += (_, _) => _services.Queue.RunQueue();
        TaskList.SelectionChanged += (_, _) => DetailText.Text = Selected()?.Details ?? string.Empty;
    }

    private TaskRow? Selected() => TaskList.SelectedItem as TaskRow;

    private static string StatusOf(PackageTaskStatus status) => status switch
    {
        PackageTaskStatus.Queued => "Queued",
        PackageTaskStatus.Running => "Running",
        PackageTaskStatus.Cancelling => "Cancelling",
        PackageTaskStatus.Completed => "Completed",
        PackageTaskStatus.Failed => "Failed",
        PackageTaskStatus.Cancelled => "Cancelled",
        _ => "Interrupted"
    };

    private void Refresh()
    {
        try
        {
            RefreshCore();
        }
        catch (Exception ex)
        {
            // Never let a display problem stop the list from updating on the next tick.
            Logger.Exception("Tasks page", ex);
        }
    }

    private void RefreshCore()
    {
        IReadOnlyList<QueuedPackageTask> tasks = _services.Queue.Tasks;
        var rows = tasks.Select(task =>
        {
            double percent = task.Status == PackageTaskStatus.Completed ? 1d : task.Progress.TaskPercent;
            string stage = task.IsTerminal ? string.Empty : task.Progress.Stage ?? string.Empty;
            string current = task.IsTerminal ? string.Empty : task.Progress.CurrentFile ?? string.Empty;
            string message = string.IsNullOrWhiteSpace(task.Message)
                ? stage
                : stage.Length > 0 ? stage + ": " + task.Message : task.Message;
            // "Starting..." is only a placeholder; once there is a real step to show, show that (and how much is done).
            if (!task.IsTerminal && stage.Length > 0 && task.Message == "Starting...") message = stage;
            if (!task.IsTerminal && task.Progress.TotalBytes > 0 && task.Status is PackageTaskStatus.Running or PackageTaskStatus.Cancelling)
            {
                // Processed, remaining and total size, for every task that knows how big its work is.
                long total = task.Progress.TotalBytes, done = Math.Clamp(task.Progress.CurrentBytes, 0, total);
                string Size(long bytes) => Packwright.Core.Services.Ps5LibraryHealth.FormatBytes(bytes);
                message += $"  ·  {Size(done)} done  ·  {Size(total - done)} left  ·  {Size(total)} total";
            }
            if (current.Length > 0) message = (message + "  " + current).Trim();
            string details = task.Failure is { } failure
                ? failure.Message
                : (task.Operation + "  " + task.FormatRoute).Trim();
            (IBrush statusBackground, IBrush statusForeground) = StatusBrushes(task.Status);
            // Fill the bar in the status colour; animate it until the first real progress arrives.
            bool starting = task.Status is PackageTaskStatus.Running or PackageTaskStatus.Cancelling && percent <= 0;
            JobSpec? spec = JobSpec.FromJson(task.PersistencePayload);
            string route = task.FormatRoute;
            string operation = route.Length > 0 ? $"{task.Operation}  ·  {route}" : task.Operation;
            bool hasOutput = task.OutputPath.Length > 0 &&
                             !string.Equals(task.OutputPath, task.SourcePath, StringComparison.OrdinalIgnoreCase);
            return new TaskRow
            {
                Title = string.IsNullOrWhiteSpace(spec?.Title) ? task.DisplayName : spec!.Title,
                Operation = operation,
                Source = task.SourcePath,
                HasOutput = hasOutput,
                Icon = IconFor(task.Id, spec),
                BarBrush = statusForeground,
                Indeterminate = starting,
                StatusBackground = statusBackground,
                StatusForeground = statusForeground,
                Id = task.Id,
                Name = task.DisplayName,
                Route = task.FormatRoute,
                StatusText = task.IsTerminal || task.Status == PackageTaskStatus.Queued
                    ? StatusOf(task.Status)
                    : (percent * 100d).ToString("0") + "%",
                Percent = percent * 100d,
                Message = message,
                Output = task.OutputPath,
                Details = details
            };
        }).ToList();
        EmptyState.IsVisible = rows.Count == 0;
        string signature = string.Join("|", rows.Select(row => row.Id + row.StatusText + row.Message + row.Percent));
        if (signature == _signature) return;
        _signature = signature;
        string? selectedId = Selected()?.Id;
        TaskList.ItemsSource = rows;
        TaskList.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
    }
}
