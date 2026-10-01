using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Packwright.App.Services;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

/// <summary>Sends a game, image or package to a console on the local network, as a background task.</summary>
public sealed class SendToConsoleWindow : Window
{
    private readonly AppServices _services = AppServices.Instance;
    private readonly string _source;
    private readonly string _title;
    private readonly bool _isPackage;
    private readonly ComboBox _console = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox _method = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBox _folder = new();
    private readonly TextBlock _where = new() { Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _folderPanel;
    private readonly Button _send = new() { Content = "Send", MinWidth = 110 };
    private bool _queued;

    private SendToConsoleWindow(string source, string title)
    {
        _source = source;
        _title = title;
        _isPackage = File.Exists(source) && source.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase);
        Title = "Send to console";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        string size = Directory.Exists(source) ? "folder" : Ps5LibraryHealth.FormatBytes(new FileInfo(source).Length);
        var manage = new Button { Content = "Manage consoles..." };
        manage.Click += async (_, _) =>
        {
            if (await ConsolesWindow.ShowAsync(this)) RefreshConsoles(_console.SelectedItem as string);
        };
        DockPanel.SetDock(manage, Dock.Right);
        manage.Margin = new Thickness(8, 0, 0, 0);
        _method.ItemsSource = _isPackage
            ? new[] { "Upload the file over FTP", "Install by URL (the console downloads it from this PC)" }
            : new[] { "Upload over FTP" };
        _method.SelectedIndex = 0;
        _console.SelectionChanged += (_, _) => FillFolder();
        _method.SelectionChanged += (_, _) => UpdateWhere();
        _folder.TextChanged += (_, _) => UpdateWhere();
        _send.Classes.Add("accent");
        _send.Click += async (_, _) => await SendAsync();
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        _folderPanel = new StackPanel
        {
            Spacing = 4,
            Children = { new TextBlock { Text = "Folder on the console", Classes = { "h" } }, _folder }
        };
        Content = new StackPanel
        {
            Margin = new Thickness(22),
            Spacing = 14,
            Children =
            {
                new TextBlock { Text = _title, FontSize = 18, FontWeight = Avalonia.Media.FontWeight.Bold, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = $"{source}  ·  {size}", Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 12 },
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Console", Classes = { "h" } },
                    new DockPanel { Children = { manage, _console } } } },
                new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "How", Classes = { "h" } }, _method } },
                _folderPanel,
                _where,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _send, cancel } }
            }
        };
        RefreshConsoles(null);
    }

    /// <summary>Opens the dialog; true when a task was queued.</summary>
    public static async Task<bool> ShowAsync(Window? owner, string source, string title)
    {
        var window = new SendToConsoleWindow(source, title);
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return window._queued;
    }

    private ConsoleProfile? Chosen => _services.Settings.Consoles.FirstOrDefault(profile => profile.Name == _console.SelectedItem as string);
    private bool UseInstall => _isPackage && _method.SelectedIndex == 1;

    private void RefreshConsoles(string? select)
    {
        List<string> names = _services.Settings.Consoles.Select(profile => profile.Name).ToList();
        _console.ItemsSource = names.Count > 0 ? names : new List<string> { "(no console yet: use Manage consoles...)" };
        _console.SelectedIndex = select is not null && names.Contains(select) ? names.IndexOf(select) : 0;
        _send.IsEnabled = names.Count > 0;
        FillFolder();
    }

    private void FillFolder()
    {
        _folder.Text = Chosen?.DefaultFolder ?? "/data";
        UpdateWhere();
    }

    private void UpdateWhere()
    {
        ConsoleProfile? profile = Chosen;
        _folderPanel.IsVisible = !UseInstall;
        if (profile is null) { _where.Text = "Add a console first."; return; }
        _where.Text = UseInstall
            ? $"Packwright will ask {profile.Name} ({profile.Host}) to install this package, and share the file from this PC for the length of the download. Your firewall may ask you to allow it."
            : $"It will be uploaded to {ConsoleSender.RemotePathFor(_folder.Text ?? profile.DefaultFolder, _source)} on {profile.Name} ({profile.Host}:{profile.FtpPort}).";
    }

    private async Task SendAsync()
    {
        ConsoleProfile? profile = Chosen;
        if (profile is null) return;
        if (string.IsNullOrWhiteSpace(profile.Host))
        {
            await Dialogs.ShowMessageAsync(this, "Send to console", "This console has no address yet. Open Manage consoles... and enter it.");
            return;
        }
        if (!await Dialogs.ConfirmAsync(this, "Send to console?", _where.Text ?? string.Empty, "Send", "Cancel")) return;
        var spec = new JobSpec
        {
            Kind = UseInstall ? JobSpec.SendInstall : JobSpec.SendFtp,
            Source = _source,
            Title = _title,
            Console = new ConsoleProfile
            {
                Name = profile.Name, Host = profile.Host, FtpPort = profile.FtpPort, DefaultFolder = profile.DefaultFolder,
                InstallUrl = profile.InstallUrl, InstallBody = profile.InstallBody, InstallContentType = profile.InstallContentType
            },
            RemoteFolder = UseInstall ? string.Empty : (_folder.Text ?? string.Empty).Trim()
        };
        string name = Path.GetFileName(_source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        _services.EnqueueJob(spec, $"Send {name} to {profile.Name}", "Send", Directory.Exists(_source) ? "dump folder" : Path.GetExtension(_source).TrimStart('.').ToUpperInvariant(),
            "Console", UseInstall ? "install by URL" : "FTP");
        _queued = true;
        Close();
    }
}
