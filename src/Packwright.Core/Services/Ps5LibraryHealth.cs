using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Packwright.Core.Services;

public enum HealthSeverity { Problem, Warning, Info }

public enum HealthKind
{
    NotFound, Unreadable, NotLaunchable, MissingDetails, FileNameMismatch, UpdateWithoutBase, Duplicate, OlderCopy,
    SupersededUpdate, LowDiskSpace, NoIcon
}

/// <summary>What the health check needs to know about one title (the values the library actually shows).</summary>
public sealed record HealthItem(
    string Path, string FileName, string Title, string TitleId, string ContentId, string Version, string Category,
    long SizeBytes, string Format, string ParamHash, IReadOnlyList<string> MissingFields, string ReadError = "");

public sealed record HealthIssue(
    HealthKind Kind, HealthSeverity Severity, string Title, string Message, string ItemPath, long ReclaimableBytes = 0)
{
    public string KindLabel => Ps5LibraryHealth.Label(Kind);
}

public sealed record DriveHealth(string Root, long FreeBytes, long TotalBytes, int Titles, long TitleBytes);

public sealed class HealthReport
{
    public DateTime CheckedUtc { get; init; } = DateTime.UtcNow;
    public int ItemCount { get; init; }
    public long TotalBytes { get; init; }
    public List<HealthIssue> Issues { get; init; } = [];
    public List<DriveHealth> Drives { get; init; } = [];

    public int Count(HealthSeverity severity) => Issues.Count(issue => issue.Severity == severity);
    public long ReclaimableBytes => Issues.Sum(issue => issue.ReclaimableBytes);
}

/// <summary>
/// Looks over a library for things worth fixing: titles that cannot be found or read, missing details, updates
/// without their base game, duplicate or superseded copies that take space, names that disagree with the IDs, and
/// drives that are nearly full. It only reads what the library already knows (plus drive sizes); the slower
/// per-file checks are separate (<see cref="IconIssue"/>, <see cref="LaunchIssues"/>).
/// </summary>
public static partial class Ps5LibraryHealth
{
    public static string Label(HealthKind kind) => kind switch
    {
        HealthKind.NotFound => "File not found",
        HealthKind.Unreadable => "Could not be read",
        HealthKind.NotLaunchable => "May not launch",
        HealthKind.MissingDetails => "Missing details",
        HealthKind.FileNameMismatch => "Name does not match",
        HealthKind.UpdateWithoutBase => "Update without base game",
        HealthKind.Duplicate => "Duplicate",
        HealthKind.OlderCopy => "Older copy",
        HealthKind.SupersededUpdate => "Superseded update",
        HealthKind.LowDiskSpace => "Low disk space",
        HealthKind.NoIcon => "No icon",
        _ => kind.ToString()
    };

    public static HealthReport Analyze(IReadOnlyList<HealthItem> items, IReadOnlyList<string> scanProblems,
        bool checkDrives = true)
    {
        var report = new HealthReport { ItemCount = items.Count, TotalBytes = items.Sum(item => item.SizeBytes) };

        foreach (string problem in scanProblems)
            report.Issues.Add(new HealthIssue(HealthKind.Unreadable, HealthSeverity.Problem, ProblemName(problem),
                "It was found but could not be read: " + problem, ProblemPath(problem)));

        foreach (HealthItem item in items)
        {
            if (!File.Exists(item.Path) && !Directory.Exists(item.Path))
                report.Issues.Add(new HealthIssue(HealthKind.NotFound, HealthSeverity.Problem, item.Title,
                    "The file or folder is no longer there (moved, renamed or deleted). Refresh the library to drop it.", item.Path));

            if (item.ReadError.Length > 0)
            {
                // Found, but not readable: its details are missing because of that, so say only that.
                report.Issues.Add(new HealthIssue(HealthKind.Unreadable, HealthSeverity.Problem, item.Title,
                    "It could not be read: " + item.ReadError + " It may be damaged or not a PS5 file.", item.Path));
                continue;
            }

            if (item.MissingFields.Count > 0)
                report.Issues.Add(new HealthIssue(HealthKind.MissingDetails, HealthSeverity.Warning, item.Title,
                    "Missing: " + string.Join(", ", item.MissingFields) +
                    ". Fix missing details, or Edit details, can fill these in.", item.Path));

            Match nameId = TitleIdPattern().Match(item.FileName);
            if (nameId.Success && item.TitleId.Length > 0 &&
                !nameId.Value.Equals(item.TitleId, StringComparison.OrdinalIgnoreCase))
                report.Issues.Add(new HealthIssue(HealthKind.FileNameMismatch, HealthSeverity.Warning, item.Title,
                    $"The file name says {nameId.Value.ToUpperInvariant()}, but the title is {item.TitleId}. One of them is wrong.",
                    item.Path));
        }

        AddFamilyIssues(items, report);
        if (checkDrives) AddDriveIssues(items, report);
        Sort(report);
        return report;
    }

    /// <summary>Problems first, then warnings and notes; within a group by kind, then title.</summary>
    public static void Sort(HealthReport report) =>
        report.Issues.Sort((a, b) => a.Severity != b.Severity ? a.Severity.CompareTo(b.Severity)
            : a.Kind != b.Kind ? a.Kind.CompareTo(b.Kind)
            : string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

    private static void AddFamilyIssues(IReadOnlyList<HealthItem> items, HealthReport report)
    {
        var bases = items.Where(item => item.Category == "Game" && item.TitleId.Length > 0)
            .Select(item => item.TitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (HealthItem item in items.Where(item => item.Category is "Patch" or "DLC" or "Add-on" &&
                                                        item.TitleId.Length > 0 && !bases.Contains(item.TitleId)))
            report.Issues.Add(new HealthIssue(HealthKind.UpdateWithoutBase, HealthSeverity.Warning, item.Title,
                $"This {item.Category.ToLowerInvariant()} (v{item.Version.TrimStart('v', 'V')}) needs its base game " +
                $"({item.TitleId}), which is not in the library.", item.Path));

        // Exact duplicates: the same content ID and version (or byte-identical param.json), more than once.
        var duplicated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, HealthItem> group in items
                     .Where(item => item.ContentId.Length > 0)
                     .GroupBy(item => item.ContentId + "|" + item.Version, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
            AddDuplicates(group.OrderByDescending(item => item.SizeBytes).ThenBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                report, duplicated, "the same content ID and version");
        foreach (IGrouping<string, HealthItem> group in items
                     .Where(item => item.ParamHash.Length > 0)
                     .GroupBy(item => item.ParamHash, StringComparer.Ordinal)
                     .Where(group => group.Count() > 1))
            AddDuplicates(group.OrderByDescending(item => item.SizeBytes).ThenBy(item => item.Path.Length).ThenBy(item => item.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                report, duplicated, "an identical param.json");

        // The same title in several versions: all but the newest are older copies (updates are handled below).
        foreach (IGrouping<string, HealthItem> group in items
                     .Where(item => item.Category == "Game" && item.TitleId.Length > 0)
                     .GroupBy(item => item.TitleId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Select(item => item.Version).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1))
        {
            HealthItem newest = group.OrderByDescending(item => item.Version, Comparer<string>.Create(CompareVersions)).First();
            foreach (HealthItem older in group.Where(item => CompareVersions(item.Version, newest.Version) < 0 && !duplicated.Contains(item.Path)))
                report.Issues.Add(new HealthIssue(HealthKind.OlderCopy, HealthSeverity.Info, older.Title,
                    $"Version {older.Version} is older than {newest.Version}, which is also in the library " +
                    $"({System.IO.Path.GetFileName(newest.Path)}).", older.Path, older.SizeBytes));
        }

        foreach (IGrouping<string, HealthItem> group in items
                     .Where(item => item.Category == "Patch" && item.TitleId.Length > 0)
                     .GroupBy(item => item.TitleId, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            HealthItem newest = group.OrderByDescending(item => item.Version, Comparer<string>.Create(CompareVersions)).First();
            foreach (HealthItem older in group.Where(item => CompareVersions(item.Version, newest.Version) < 0 && !duplicated.Contains(item.Path)))
                report.Issues.Add(new HealthIssue(HealthKind.SupersededUpdate, HealthSeverity.Info, older.Title,
                    $"Update {older.Version} is replaced by {newest.Version}, which is also in the library. " +
                    "The newer update normally contains it.", older.Path, older.SizeBytes));
        }
    }

    private static void AddDuplicates(List<HealthItem> group, HealthReport report, HashSet<string> seen, string why)
    {
        HealthItem keep = group[0];
        foreach (HealthItem copy in group.Skip(1).Where(item => seen.Add(item.Path)))
            report.Issues.Add(new HealthIssue(HealthKind.Duplicate, HealthSeverity.Warning, copy.Title,
                $"A copy of {System.IO.Path.GetFileName(keep.Path)}: it has {why}. One of the two can go.", copy.Path, copy.SizeBytes));
    }

    private static void AddDriveIssues(IReadOnlyList<HealthItem> items, HealthReport report)
    {
        foreach (IGrouping<string, HealthItem> group in items.GroupBy(item => DriveRoot(item.Path), StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Key.Length > 0))
        {
            try
            {
                var drive = new DriveInfo(group.Key);
                if (!drive.IsReady || drive.TotalSize <= 0) continue;
                long free = drive.AvailableFreeSpace, total = drive.TotalSize;
                report.Drives.Add(new DriveHealth(group.Key, free, total, group.Count(), group.Sum(item => item.SizeBytes)));
                if (free < total / 10 || free < 20L * 1024 * 1024 * 1024)
                    report.Issues.Add(new HealthIssue(HealthKind.LowDiskSpace, HealthSeverity.Warning, group.Key,
                        $"{FormatBytes(free)} free of {FormatBytes(total)} ({free * 100.0 / total:0.#}%). " +
                        $"{group.Count():N0} title(s) are stored here, and converting or rebuilding one needs room for a copy.",
                        group.First().Path));
            }
            catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
        }
    }

    private static string DriveRoot(string path)
    {
        try { return System.IO.Path.GetPathRoot(path) ?? string.Empty; }
        catch (ArgumentException) { return string.Empty; }
    }

    // ------------------------------------------------------------------ the slower per-file checks

    /// <summary>A title whose icon is missing. <paramref name="hasIcon"/> comes from looking inside the title.</summary>
    public static HealthIssue? IconIssue(HealthItem item, bool hasIcon) => hasIcon
        ? null
        : new HealthIssue(HealthKind.NoIcon, HealthSeverity.Info, item.Title,
            "The title has no sce_sys/icon0.png. Edit details can add one (Search PlayStation Network fetches it).", item.Path);

    /// <summary>Launch problems for an unpacked dump folder (a missing eboot.bin, an unknown executable container, ...).</summary>
    public static IEnumerable<HealthIssue> LaunchIssues(HealthItem item)
    {
        Ps5LaunchReadinessReport report;
        try { report = Ps5LaunchReadiness.Inspect(item.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException or ArgumentException)
        {
            yield break;
        }
        foreach (string error in report.Errors)
            yield return new HealthIssue(HealthKind.NotLaunchable, HealthSeverity.Problem, item.Title, error, item.Path);
        foreach (string warning in report.Warnings)
            yield return new HealthIssue(HealthKind.NotLaunchable, HealthSeverity.Warning, item.Title, warning, item.Path);
    }

    // ------------------------------------------------------------------ text

    public static string ToMarkdown(HealthReport report, Func<HealthIssue, string>? where = null)
    {
        var text = new StringBuilder();
        text.AppendLine("# Library health report");
        text.AppendLine();
        text.AppendLine($"Checked {report.CheckedUtc.ToLocalTime():yyyy-MM-dd HH:mm}. {report.ItemCount:N0} title(s), {FormatBytes(report.TotalBytes)}.");
        text.AppendLine();
        text.AppendLine($"- Problems: {report.Count(HealthSeverity.Problem):N0}");
        text.AppendLine($"- Warnings: {report.Count(HealthSeverity.Warning):N0}");
        text.AppendLine($"- Notes: {report.Count(HealthSeverity.Info):N0}");
        if (report.ReclaimableBytes > 0) text.AppendLine($"- Space that duplicates and older copies take: {FormatBytes(report.ReclaimableBytes)}");
        foreach (DriveHealth drive in report.Drives)
            text.AppendLine($"- Drive {drive.Root}: {FormatBytes(drive.FreeBytes)} free of {FormatBytes(drive.TotalBytes)}");
        foreach ((HealthSeverity severity, string heading) in new[]
                 { (HealthSeverity.Problem, "Problems"), (HealthSeverity.Warning, "Warnings"), (HealthSeverity.Info, "Notes") })
        {
            List<HealthIssue> issues = report.Issues.Where(issue => issue.Severity == severity).ToList();
            if (issues.Count == 0) continue;
            text.AppendLine();
            text.AppendLine($"## {heading}");
            foreach (IGrouping<HealthKind, HealthIssue> group in issues.GroupBy(issue => issue.Kind))
            {
                text.AppendLine();
                text.AppendLine($"### {Label(group.Key)} ({group.Count():N0})");
                foreach (HealthIssue issue in group)
                    text.AppendLine($"- **{issue.Title}**: {issue.Message}" + (where is null ? string.Empty : $" `{where(issue)}`"));
            }
        }
        if (report.Issues.Count == 0) text.AppendLine().AppendLine("Nothing to fix.");
        return text.ToString();
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {units[unit]}");
    }

    /// <summary>Compares versions such as 01.007.000 and 1.2 numerically, part by part.</summary>
    public static int CompareVersions(string? left, string? right)
    {
        string[] a = (left ?? string.Empty).TrimStart('v', 'V').Split('.', StringSplitOptions.RemoveEmptyEntries);
        string[] b = (right ?? string.Empty).TrimStart('v', 'V').Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            long x = i < a.Length && long.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out long px) ? px : 0;
            long y = i < b.Length && long.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out long py) ? py : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    // Scan problems look like "D:\Games\x.zar: The ZArchive has no ..."; split the path off the message.
    private static string ProblemPath(string problem)
    {
        int colon = problem.IndexOf(": ", 2, StringComparison.Ordinal);
        return colon > 0 ? problem[..colon] : problem;
    }

    private static string ProblemName(string problem) => System.IO.Path.GetFileName(ProblemPath(problem).TrimEnd('\\', '/'));

    [GeneratedRegex(@"PPSA\d{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdPattern();
}
