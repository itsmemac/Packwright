using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

/// <summary>Process-wide state shared by every view: settings, the library and the task queue.</summary>
public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    private readonly Ps5LibraryScanner _scanner = new();
    private readonly object _gameGate = new();

    private AppServices()
    {
        Store = new AppStateStore();
        SettingsLoadResult loaded = Store.LoadSettingsWithDiagnostics();
        Settings = loaded.Settings;
        SettingsWarning = loaded.Warning;
        Queue = new PackageTaskQueue();
        Queue.TasksChanged += (_, _) => LogTaskChanges();
        string taskFile = Path.Combine(Store.AppDataDirectory, "studio-tasks.json");
        Queue.SetPersistencePath(taskFile);
        Queue.RestoreFromDisk(RestoreTask);
        foreach (QueuedPackageTask restored in Queue.Tasks) _loggedStatus[restored.Id] = restored.Status;
        Queue.EnablePersistence(taskFile);
        Games = Store.LoadManifest().Games;
        Logger.Info($"Loaded {Games.Count:N0} cached library item(s); {Settings.LibraryFolders.Count} library folder(s), " +
                    $"{Settings.ManualSources.Count} added item(s).");
    }

    private readonly Dictionary<string, PackageTaskStatus> _loggedStatus = [];

    /// <summary>Writes one log line whenever a task starts, finishes, fails or is cancelled.</summary>
    private void LogTaskChanges()
    {
        foreach (QueuedPackageTask task in Queue.Tasks)
        {
            PackageTaskStatus previous;
            bool known;
            lock (_loggedStatus)
            {
                known = _loggedStatus.TryGetValue(task.Id, out previous);
                if (known && previous == task.Status) continue;
                _loggedStatus[task.Id] = task.Status;
            }
            string name = $"{task.DisplayName} ({task.Operation}{(task.FormatRoute.Length > 0 ? ", " + task.FormatRoute : string.Empty)})";
            switch (task.Status)
            {
                case PackageTaskStatus.Queued when !known:
                    Logger.Info($"Task queued: {name}. From {task.SourcePath}" +
                                (task.OutputPath.Length > 0 && task.OutputPath != task.SourcePath ? $" to {task.OutputPath}" : string.Empty));
                    break;
                case PackageTaskStatus.Running:
                    Logger.Info($"Task started: {name}");
                    break;
                case PackageTaskStatus.Completed:
                    Logger.Info($"Task completed: {name}");
                    break;
                case PackageTaskStatus.Failed:
                    Logger.Error($"Task failed: {name}: {task.Failure?.Message ?? task.Message}");
                    if (task.Failure is { } failure) Logger.Error(failure.ToString());
                    break;
                case PackageTaskStatus.Cancelled:
                    Logger.Warn($"Task cancelled: {name}");
                    break;
                case PackageTaskStatus.Interrupted when !known:
                    Logger.Warn($"Task interrupted in an earlier session: {name}");
                    break;
            }
        }
    }

    public AppStateStore Store { get; }
    public AppSettings Settings { get; private set; }
    public string? SettingsWarning { get; }
    public PackageTaskQueue Queue { get; }
    public List<Ps5GameInfo> Games { get; private set; }

    public event Action? LibraryChanged;

    /// <summary>Files the last scan found but could not read ("path: reason").</summary>
    public IReadOnlyList<string> ScanProblems { get; private set; } = [];

    /// <summary>Raised when another page wants the Library to show (and optionally edit) one title.</summary>
    public event Action<string, bool>? ShowTitleRequested;

    public void RequestShowTitle(string path, bool edit) => ShowTitleRequested?.Invoke(path, edit);

    /// <summary>Raised after settings were saved so views can re-read appearance options.</summary>
    public event Action? SettingsChanged;

    public void NotifySettingsChanged() => SettingsChanged?.Invoke();

    /// <summary>Raised when another view wants the Tools tab to work on a path.</summary>
    public event Action<string>? ToolsSourceRequested;

    public void RequestToolsSource(string path) => ToolsSourceRequested?.Invoke(path);

    /// <summary>Queues a job described by <paramref name="spec"/>; it is saved and restored across restarts.</summary>
    public QueuedPackageTask EnqueueJob(JobSpec spec, string displayName, string operation, string sourceFormat,
        string targetFormat, string qualifier = "")
    {
        var executor = spec.Executor() ?? throw new InvalidOperationException("This job cannot be run.");
        var task = new QueuedPackageTask
        {
            Id = Guid.NewGuid().ToString("N"),
            Type = spec.TaskType,
            DisplayName = displayName,
            SourcePath = spec.Source,
            OutputPath = spec.Output.Length > 0 ? spec.Output : spec.Source,
            PersistencePayload = spec.ToJson(),
            Operation = operation,
            SourceFormat = sourceFormat,
            TargetFormat = targetFormat,
            TargetQualifier = qualifier,
            StagePlan = spec.Plan(),
            Execute = executor
        };
        // Each task reports its own state changes (started, finished, failed); log them as they happen.
        task.Changed += (_, _) => LogTaskChanges();
        return Queue.Enqueue(task);
    }

    private QueuedPackageTask? RestoreTask(PersistedPackageTask entry)
    {
        JobSpec? spec = JobSpec.FromJson(entry.Payload);
        var task = new QueuedPackageTask
        {
            Id = entry.Id,
            Type = entry.Type,
            DisplayName = entry.DisplayName,
            SourcePath = entry.SourcePath,
            OutputPath = entry.OutputPath,
            PersistencePayload = entry.Payload,
            Operation = entry.Operation,
            SourceFormat = entry.SourceFormat,
            TargetFormat = entry.TargetFormat,
            TargetQualifier = entry.TargetQualifier,
            StagePlan = spec?.Plan() ?? [],
            Execute = spec?.Executor(),
            CreatedUtc = entry.CreatedUtc == default ? DateTime.UtcNow : entry.CreatedUtc
        };
        task.Changed += (_, _) => LogTaskChanges();
        return task;
    }

    public void SaveSettings()
    {
        try { Store.SaveSettings(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("Settings could not be saved: " + ex.Message);
        }
    }

    public void ReplaceSettings(AppSettings settings)
    {
        Settings = AppSettingsNormalizer.Normalize(settings);
        SaveSettings();
    }

    /// <summary>Library folders plus single items added by path (manual sources).</summary>
    public IReadOnlyList<string> ScanSources =>
        Settings.LibraryFolders.Concat(Settings.ManualSources).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public void AddManualSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Settings.ManualSources.Contains(path, StringComparer.OrdinalIgnoreCase))
            Settings.ManualSources.Add(path);
        SaveSettings();
    }

    public void AddLibraryFolder(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!Settings.LibraryFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
            Settings.LibraryFolders.Add(path);
        Settings.RecentFolders.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        Settings.RecentFolders.Insert(0, path);
        SaveSettings();
    }

    public void RemoveLibraryFolder(string path)
    {
        Settings.LibraryFolders.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        SaveSettings();
    }

    /// <summary>Rescans every library folder (reusing cached entries for unchanged sources).</summary>
    public async Task<Ps5ScanResult> RefreshLibraryAsync(IProgress<Ps5ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        Logger.Info($"Scanning {ScanSources.Count} source(s){(Settings.RecursiveScan ? " including sub-folders" : string.Empty)}...");
        Ps5ScanResult result = await _scanner.ScanAsync(ScanSources, Settings.RecursiveScan,
            Games, progress, cancellationToken).ConfigureAwait(false);
        lock (_gameGate) Games = result.Games;
        try { Store.SaveManifest(result.Games); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.Warn("The library manifest could not be saved: " + ex.Message);
        }
        ScanProblems = result.Errors.ToList();
        foreach (string error in result.Errors) Logger.Warn("Scan: " + error);
        Logger.Info($"Scan finished: {result.Games.Count:N0} item(s), {result.Errors.Count} problem(s).");
        LibraryChanged?.Invoke();
        return result;
    }
}
