using Avalonia;
using Packwright.Infrastructure;

namespace Packwright.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Logger.Exception("AppDomain", e.ExceptionObject as Exception ?? new Exception("Unknown error"));
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Logger.Exception("Unobserved task", e.Exception);
            e.SetObserved();
        };
        if (Cli.IsCommand(args)) Environment.Exit(Cli.Run(args));
        using SingleInstance? instance = SingleInstance.TryAcquire(args);
        if (instance is null) return;
        Current = instance;
        Logger.Info($"Packwright {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)} started on {System.Runtime.InteropServices.RuntimeInformation.OSDescription}.");
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Logger.Info("Packwright exited.");
    }

    /// <summary>The lock that keeps this the only running copy.</summary>
    internal static SingleInstance? Current { get; private set; }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace()
            // Draw into a normal window surface. The default DirectComposition surface has no redirection
            // bitmap, which some screenshot and screen-recording tools capture only partly.
            .With(new Win32PlatformOptions { CompositionMode = [Win32CompositionMode.RedirectionSurface] })
            // The default lets Skia keep ~100 MB of GPU resources cached; this UI needs far less.
            .With(new SkiaOptions { MaxGpuResourceSizeBytes = 32 * 1024 * 1024 });
}
