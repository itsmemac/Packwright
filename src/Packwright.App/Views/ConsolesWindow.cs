using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Packwright.App.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

/// <summary>Add, edit and test the consoles that files can be sent to.</summary>
public sealed class ConsolesWindow : Window
{
    private readonly AppServices _services = AppServices.Instance;
    private readonly List<ConsoleProfile> _profiles;
    private readonly System.Collections.ObjectModel.ObservableCollection<string> _names = [];
    private readonly ListBox _list = new() { MinHeight = 360, MinWidth = 190, Background = Avalonia.Media.Brushes.Transparent };
    private bool _busy;
    private readonly TextBox _name = new();
    private readonly TextBox _host = new() { Watermark = "For example 192.168.1.50" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, FormatString = "0", Value = 1337 };
    private readonly TextBox _folder = new() { Watermark = "/data" };
    private readonly TextBox _user = new() { Watermark = "Leave empty if it lets anyone in" };
    private readonly TextBox _password = new() { PasswordChar = '•' };
    private readonly TextBox _installUrl = new();
    private readonly TextBox _installBody = new() { AcceptsReturn = true, TextWrapping = Avalonia.Media.TextWrapping.Wrap, MinHeight = 64 };
    private readonly TextBox _contentType = new();
    private readonly Button _test = new() { Content = "Test connection" };
    private readonly TextBlock _result = new() { Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap };
    private readonly StackPanel _form = new() { Spacing = 8, IsEnabled = false };
    private CancellationTokenSource? _testing;
    private int _shown = -1;
    private bool _saved;

    private ConsolesWindow()
    {
        _profiles = _services.Settings.Consoles.Select(Clone).ToList();
        Title = "Consoles";
        Width = 760;
        Height = 640;
        MinWidth = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;

        var add = new Button { Content = "Add console" };
        var remove = new Button { Content = "Remove" };
        add.Click += (_, _) =>
        {
            Commit();
            _profiles.Add(new ConsoleProfile { Name = UniqueName("My PS5") });
            Refresh(_profiles.Count - 1);
        };
        remove.Click += (_, _) =>
        {
            if (_list.SelectedIndex < 0) return;
            int index = _list.SelectedIndex;
            _shown = -1;
            _profiles.RemoveAt(index);
            Refresh(Math.Min(index, _profiles.Count - 1));
        };
        _list.ItemsSource = _names;
        _list.SelectionChanged += (_, _) =>
        {
            if (_busy || _list.SelectedIndex == _shown) return;
            Commit();
            Show(_list.SelectedIndex);
        };
        _test.Click += async (_, _) => await TestAsync();

        _form.Children.Add(Field("Name", _name));
        _form.Children.Add(Field("Address on your network", _host));
        _form.Children.Add(Field("FTP port", _port));
        _form.Children.Add(Field("User name", _user));
        _form.Children.Add(Field("Password", _password));
        _form.Children.Add(new TextBlock
        {
            Classes = { "muted" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Text = "Consoles with an FTP payload usually let anyone in, so leave these empty. A phone or PC running an FTP app " +
                   "shows the user name and password to use. The password is saved on this PC in Packwright's settings file, not encrypted."
        });
        _form.Children.Add(Field("Default folder on the console", _folder));
        _form.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = { _test } });
        _form.Children.Add(_result);
        _form.Children.Add(new Expander
        {
            Header = "Install by URL (advanced)",
            Content = new StackPanel
            {
                Spacing = 8, Margin = new Thickness(0, 8, 0, 0),
                Children =
                {
                    new TextBlock
                    {
                        Classes = { "muted" }, FontSize = 12, TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Text = "Some installer payloads on the console download a package from this PC when asked. They each expect their own " +
                               "request, so check your payload's documentation and adjust these to match. {host} is the console's address and " +
                               "{url} is the address of the file. The defaults follow the common \"remote package installer\" style and are untested."
                    },
                    Field("Request address (POST)", _installUrl),
                    Field("Request body", _installBody),
                    Field("Content type", _contentType)
                }
            }
        });

        var save = new Button { Content = "Save", MinWidth = 100 };
        save.Classes.Add("accent");
        save.Click += (_, _) =>
        {
            Commit();
            if (_profiles.Any(profile => string.IsNullOrWhiteSpace(profile.Host)))
            {
                _result.Text = "Enter an address for every console, or remove the ones you do not use.";
                return;
            }
            _services.Settings.Consoles = _profiles;
            _services.ReplaceSettings(_services.Settings);
            _saved = true;
            Close();
        };
        var cancel = new Button { Content = "Cancel" };
        cancel.Click += (_, _) => Close();

        var grid = new Grid { Margin = new Thickness(20), RowDefinitions = new RowDefinitions("Auto,*,Auto"), ColumnDefinitions = new ColumnDefinitions("220,18,*") };
        var intro = new TextBlock
        {
            Classes = { "muted" }, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14),
            Text = "Packwright sends files only to devices on your own network, and only when you press Send. No account is used: console FTP payloads accept anyone on the network."
        };
        Grid.SetColumnSpan(intro, 3);
        grid.Children.Add(intro);
        var left = new StackPanel { Spacing = 8, Children = { _list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, remove } } } };
        Grid.SetRow(left, 1);
        var right = new ScrollViewer { Content = _form };
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 2);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), Children = { save, cancel } };
        Grid.SetRow(buttons, 2);
        Grid.SetColumnSpan(buttons, 3);
        grid.Children.Add(left);
        grid.Children.Add(right);
        grid.Children.Add(buttons);
        Content = grid;
        Closing += (_, _) => _testing?.Cancel();
        Refresh(_profiles.Count > 0 ? 0 : -1);
    }

    /// <summary>Opens the window; true when the list was saved.</summary>
    public static async Task<bool> ShowAsync(Window? owner)
    {
        var window = new ConsolesWindow();
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return window._saved;
    }

    private static StackPanel Field(string label, Control input) => new()
    {
        Spacing = 4,
        Children = { new TextBlock { Text = label, Classes = { "h" } }, input }
    };

    private static ConsoleProfile Clone(ConsoleProfile source) => new()
    {
        Name = source.Name, Host = source.Host, FtpPort = source.FtpPort, DefaultFolder = source.DefaultFolder,
        FtpUser = source.FtpUser, FtpPassword = source.FtpPassword,
        InstallUrl = source.InstallUrl, InstallBody = source.InstallBody, InstallContentType = source.InstallContentType
    };

    private string UniqueName(string baseName)
    {
        string name = baseName;
        for (int index = 2; _profiles.Any(profile => profile.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); index++)
            name = $"{baseName} {index}";
        return name;
    }

    private void Refresh(int select)
    {
        _busy = true;
        _shown = -1;
        _names.Clear();
        foreach (ConsoleProfile profile in _profiles) _names.Add(profile.Name);
        _list.SelectedIndex = select;
        _busy = false;
        Show(select);
    }

    private void Show(int index)
    {
        _shown = index;
        _result.Text = string.Empty;
        _form.IsEnabled = index >= 0 && index < _profiles.Count;
        if (!_form.IsEnabled)
        {
            foreach (TextBox box in new[] { _name, _host, _folder, _user, _password, _installUrl, _installBody, _contentType }) box.Text = string.Empty;
            return;
        }
        ConsoleProfile profile = _profiles[index];
        _name.Text = profile.Name;
        _host.Text = profile.Host;
        _port.Value = profile.FtpPort;
        _folder.Text = profile.DefaultFolder;
        _user.Text = profile.FtpUser;
        _password.Text = profile.FtpPassword;
        _installUrl.Text = profile.InstallUrl;
        _installBody.Text = profile.InstallBody;
        _contentType.Text = profile.InstallContentType;
    }

    /// <summary>Writes the form back into the profile that was being shown.</summary>
    private void Commit()
    {
        if (_shown < 0 || _shown >= _profiles.Count) return;
        ConsoleProfile profile = _profiles[_shown];
        string name = (_name.Text ?? string.Empty).Trim();
        if (name.Length > 0 && !_profiles.Where((other, index) => index != _shown).Any(other => other.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            profile.Name = name;
        profile.Host = (_host.Text ?? string.Empty).Trim();
        profile.FtpPort = (int)(_port.Value ?? 1337);
        profile.DefaultFolder = string.IsNullOrWhiteSpace(_folder.Text) ? "/data" : _folder.Text.Trim();
        profile.FtpUser = (_user.Text ?? string.Empty).Trim();
        profile.FtpPassword = _password.Text ?? string.Empty;
        profile.InstallUrl = (_installUrl.Text ?? string.Empty).Trim();
        profile.InstallBody = _installBody.Text ?? string.Empty;
        profile.InstallContentType = string.IsNullOrWhiteSpace(_contentType.Text) ? "application/json" : _contentType.Text.Trim();
        if (_shown < _names.Count && _names[_shown] != profile.Name)
        {
            _busy = true;
            _names[_shown] = profile.Name;
            _list.SelectedIndex = _shown;
            _busy = false;
        }
    }

    private async Task TestAsync()
    {
        if (_shown < 0) return;
        Commit();
        ConsoleProfile profile = _profiles[_shown];
        _test.IsEnabled = false;
        _result.Text = "Connecting...";
        _testing = new CancellationTokenSource();
        try
        {
            _result.Text = await ConsoleSender.TestFtpAsync(profile, _testing.Token);
        }
        catch (OperationCanceledException)
        {
            _result.Text = "Cancelled.";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or System.Net.Sockets.SocketException)
        {
            _result.Text = ex.Message;
        }
        finally
        {
            _test.IsEnabled = true;
            _testing?.Dispose();
            _testing = null;
        }
    }
}
