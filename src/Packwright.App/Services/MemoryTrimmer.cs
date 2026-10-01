using System.Runtime;
using System.Runtime.InteropServices;
using Avalonia.Threading;

namespace Packwright.App.Services;

/// <summary>
/// Gives memory back after bursts of work (loading a game's details, scanning, switching pages): a short
/// idle delay, then a full collection that also compacts the large-object heap, and on Windows a working-set
/// trim so the figure Task Manager shows reflects what the app really needs.
/// </summary>
public static class MemoryTrimmer
{
    private static DispatcherTimer? _timer;

    /// <summary>Trims once the app has been quiet for <paramref name="seconds"/>; repeated calls restart the wait.</summary>
    public static void Schedule(int seconds = 4)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => Schedule(seconds));
            return;
        }
        _timer?.Stop();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
        _timer.Tick += (_, _) =>
        {
            _timer?.Stop();
            _ = Task.Run(TrimNow);
        };
        _timer.Start();
    }

    public static void TrimNow()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        if (OperatingSystem.IsWindows())
        {
            try { SetProcessWorkingSetSize(System.Diagnostics.Process.GetCurrentProcess().Handle, -1, -1); }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        }
    }

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr process, nint minimum, nint maximum);
}
