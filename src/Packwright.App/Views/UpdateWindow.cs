using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Packwright.App.Services;
using Packwright.Core.Services;

namespace Packwright.App.Views;

/// <summary>Shows a newer release: what is new, and a download for this system that is checked against the release's checksums.</summary>
public sealed class UpdateWindow : Window
{
    private readonly ReleaseInfo _release;
    private readonly ReleaseAsset? _asset;
    private readonly Button _download = new() { MinWidth = 150 };
    private readonly Button _show = new() { Content = "Show in folder", IsVisible = false };
    private readonly Button _install = new() { Content = "Install now", IsVisible = false, MinWidth = 130 };
    private readonly ProgressBar _progress = new() { IsVisible = false, Minimum = 0, Maximum = 100 };
    private readonly TextBlock _status = new() { Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
    private CancellationTokenSource? _cancellation;
    private string? _downloaded;

    private UpdateWindow(ReleaseInfo release)
    {
        _release = release;
        _asset = UpdateChecker.PickAsset(release);
        Title = "Update available";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        MaxHeight = 760;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        string when = release.PublishedUtc is { } published ? $"  ·  released {published.ToLocalTime():d MMMM yyyy}" : string.Empty;
        // The notes are Markdown (headings, bold, bullets): show them formatted, not as raw text.
        var notes = new Border
        {
            Classes = { "card" }, Padding = new Thickness(14, 10), MaxHeight = 300,
            Child = new ScrollViewer
            {
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = release.Notes.Trim().Length > 0
                    ? new Border { Padding = new Thickness(0, 0, 18, 0), Child = MarkdownLite.Render(release.Notes) }   // room for the scrollbar
                    : new TextBlock { Text = "No release notes were published.", Classes = { "muted" } }
            }
        };

        bool installer = _asset is not null && UpdateChecker.IsInstaller(_asset);
        _download.Content = _asset is null ? "No download for this system"
            : $"{(installer ? "Download installer" : "Download")} ({FormatBytes(_asset.Size)})";
        _install.Classes.Add("accent");
        _install.Click += async (_, _) => await InstallAsync();
        _download.IsEnabled = _asset is not null;
        _download.Classes.Add("accent");
        _download.Click += async (_, _) => await DownloadAsync();
        _show.Click += (_, _) => { if (_downloaded is not null) Dialogs.Reveal(_downloaded); };
        var page = new Button { Content = "Release page" };
        page.Click += (_, _) => Dialogs.OpenPath(release.PageUrl.Length > 0 ? release.PageUrl : AboutView.RepositoryUrl + "/releases");
        var skip = new Button { Content = "Skip this version" };
        skip.Click += (_, _) => { UpdateService.Skip(release); Close(); };
        var later = new Button { Content = "Later" };
        later.Click += (_, _) => { _cancellation?.Cancel(); Close(); };

        _status.Text = _asset is null
            ? "This release has no file for your system attached. Open the release page to see what is available."
            : installer
                ? $"{_asset.Name} installs for you without administrator rights. It is downloaded and checked first; then Packwright closes, updates and starts again."
                : $"The file is {_asset.Name}. It is not installed for you: {InstallHint(_asset)}";

        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = $"Packwright {release.Version} is available", FontSize = 20, FontWeight = FontWeight.Bold },
                new TextBlock { Text = $"You have {UpdateService.CurrentVersion}{when}", Classes = { "muted" } },
                notes,
                _progress,
                _status,
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children = { Spaced(_download), Spaced(_install), Spaced(_show), Spaced(page), Spaced(skip), Spaced(later) }
                }
            }
        };
        Closing += (_, _) => _cancellation?.Cancel();
    }

    private static Control Spaced(Control control)
    {
        control.Margin = new Thickness(0, 0, 8, 8);
        return control;
    }

    public static async Task ShowAsync(Window? owner, ReleaseInfo release)
    {
        var window = new UpdateWindow(release);
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
    }

    private async Task DownloadAsync()
    {
        if (_asset is null) return;
        _cancellation = new CancellationTokenSource();
        _download.IsEnabled = false;
        _show.IsVisible = false;
        _progress.IsVisible = true;
        _progress.Value = 0;
        _status.Text = "Downloading...";
        string folder = DownloadFolder();
        try
        {
            var progress = new Progress<(long Done, long Total)>(value =>
            {
                if (value.Total > 0) _progress.Value = value.Done * 100.0 / value.Total;
                _status.Text = $"Downloading... {FormatBytes(value.Done)} of {FormatBytes(value.Total)}";
            });
            DownloadResult result = await UpdateChecker.DownloadAsync(_release, _asset, folder, progress, _cancellation.Token);
            _downloaded = result.Path;
            _progress.Value = 100;
            if (result.Verified == false)
            {
                // A file that does not match its checksum is damaged or tampered with: it is removed, not kept.
                try { File.Delete(result.Path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                _status.Text = result.VerifyMessage + "\nThe file was deleted.";
                _download.IsEnabled = true;
                return;
            }
            bool isInstaller = UpdateChecker.IsInstaller(_asset);
            _status.Text = $"Saved to {result.Path}\n{result.VerifyMessage}" +
                           (result.Verified == false ? string.Empty
                            : isInstaller ? "\nPress Install now to update."
                            : "\n" + InstallHint(_asset));
            _show.IsVisible = true;
            _install.IsVisible = isInstaller && result.Verified != false && OperatingSystem.IsWindows();
            _download.IsVisible = !_install.IsVisible;
        }
        catch (OnlineLookupException ex)
        {
            _status.Text = ex.Message;
            _download.IsEnabled = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Download cancelled.";
            _download.IsEnabled = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _status.Text = "The file could not be saved: " + ex.Message;
            _download.IsEnabled = true;
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            _progress.IsVisible = false;
        }
    }

    /// <summary>Runs the downloaded installer silently (it shows its own progress), then closes Packwright so it can replace the files.</summary>
    private async Task InstallAsync()
    {
        if (_downloaded is null) return;
        AppServices services = AppServices.Instance;
        if (services.Queue.Tasks.Any(task => task.Status == Packwright.Core.Tasks.PackageTaskStatus.Running) &&
            !await Dialogs.ConfirmAsync(this, "Tasks are running",
                "Installing closes Packwright, which stops the tasks that are running now. Install anyway?", "Install", "Wait"))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_downloaded,
                "/SILENT /CLOSEAPPLICATIONS /NORESTART") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            _status.Text = "The installer could not be started: " + ex.Message;
            return;
        }
        Packwright.Infrastructure.Logger.Info("Started the installer " + _downloaded);
        Close();
        (Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    /// <summary>How to install a downloaded macOS or Linux package.</summary>
    private static string InstallHint(ReleaseAsset asset) => asset.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase)
        ? "when it has downloaded, close Packwright, open the disk image and drag Packwright onto Applications."
        : asset.Name.EndsWith(".deb", StringComparison.OrdinalIgnoreCase)
            ? $"when it has downloaded, close Packwright and install it with your software installer, or run: sudo apt install ./{asset.Name}"
            : asset.Name.EndsWith(".rpm", StringComparison.OrdinalIgnoreCase)
                ? $"when it has downloaded, close Packwright and install it with your software installer, or run: sudo dnf install ./{asset.Name}"
                : "when it has downloaded, close Packwright and install it.";

    private static string DownloadFolder()
    {
        string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(downloads) ? downloads : Path.GetTempPath();
    }

    private static string FormatBytes(long bytes) => Ps5LibraryHealth.FormatBytes(bytes);
}
