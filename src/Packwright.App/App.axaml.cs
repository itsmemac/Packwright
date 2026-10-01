using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Packwright.App.Views;

namespace Packwright.App;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        // A failure in a timer or event handler is logged and the app keeps running.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            Packwright.Infrastructure.Logger.Exception("UI thread", e.Exception);
            e.Handled = true;
        };
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow(desktop.Args ?? []);
            desktop.MainWindow = window;
            // Another launch of the app brings this window forward instead of opening a second copy.
            Program.Current?.Listen(paths => Avalonia.Threading.Dispatcher.UIThread.Post(() => window.BringToFront(paths)));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
