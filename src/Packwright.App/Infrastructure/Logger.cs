using System.Text;

namespace Packwright.Infrastructure;

public enum LogLevel
{
    Info,
    Warn,
    Error
}

/// <summary>One structured log record, raised through <see cref="Logger.Logged"/>.</summary>
public readonly record struct LogEntry(DateTime Time, LogLevel Level, string Message);

/// <summary>
/// Minimal thread-safe file logger. Writes to <c>%LocalAppData%\Packwright\logs</c>, rotates once at
/// a soft size cap, and never throws because logging must not break the application. The file is
/// the full record of every log; <see cref="Logged"/> feeds the in-app Log tab.
/// </summary>
public static class Logger
{
    private static readonly object Gate = new();
    private const long MaximumBytes = 5 * 1024 * 1024;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("PACKWRIGHT_DATA") is { Length: > 0 } custom
            ? Path.GetFullPath(custom)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packwright"),
        "logs");

    public static string LogPath { get; } = Path.Combine(LogDirectory, "Packwright.log");

    public static event Action<LogEntry>? Logged;

    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message) => Write(LogLevel.Warn, message);
    public static void Error(string message) => Write(LogLevel.Error, message);
    public static void Exception(string context, Exception exception) => Write(LogLevel.Error, $"{context}: {exception}");

    private static void Write(LogLevel level, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, message);
        string line = $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(level)}] {message}";
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(LogDirectory);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaximumBytes)
                    File.Move(LogPath, LogPath + ".1", true);
                // Shared write access, so a second running copy (or a viewer) never blocks logging.
                using var stream = new FileStream(LogPath, FileMode.Append, FileAccess.Write,
                    FileShare.ReadWrite | FileShare.Delete);
                byte[] bytes = Encoding.UTF8.GetBytes(line + Environment.NewLine);
                stream.Write(bytes, 0, bytes.Length);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Logging is best-effort only.
        }
        Logged?.Invoke(entry);
    }

    /// <summary>Formats an entry exactly as it is written to the log file.</summary>
    public static string Format(LogEntry entry) =>
        $"{entry.Time:yyyy-MM-dd HH:mm:ss.fff} [{LevelText(entry.Level)}] {entry.Message}";

    /// <summary>The last lines of the log file, oldest first, for showing history in the app.</summary>
    public static IReadOnlyList<string> ReadTail(int maximumLines)
    {
        try
        {
            if (!File.Exists(LogPath)) return [];
            using var stream = new FileStream(LogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            // Read only the end of the file; 64 KiB per 200 lines is ample for typical entries.
            long window = Math.Min(stream.Length, Math.Max(64 * 1024, maximumLines * 320L));
            stream.Position = stream.Length - window;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            string text = reader.ReadToEnd();
            List<string> lines = text.Split('\n').Select(line => line.TrimEnd('\r')).Where(line => line.Length > 0).ToList();
            if (window < stream.Length && lines.Count > 0) lines.RemoveAt(0); // the first line is usually cut off
            return lines.Count <= maximumLines ? lines : lines.GetRange(lines.Count - maximumLines, maximumLines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private static string LevelText(LogLevel level) => level switch
    {
        LogLevel.Warn => "WARN",
        LogLevel.Error => "ERROR",
        _ => "INFO"
    };
}
