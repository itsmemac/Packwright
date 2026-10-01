using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Platform.Storage;

namespace Packwright.App.Services;

/// <summary>Small helpers for message boxes and the platform file/folder pickers.</summary>
public static class Dialogs
{
    public static Window? OwnerOf(Control control) => TopLevel.GetTopLevel(control) as Window;

    public static async Task ShowMessageAsync(Window? owner, string title, string message)
    {
        await ShowAsync(owner, title, message, ["OK"]);
    }

    public static async Task<bool> ConfirmAsync(Window? owner, string title, string message,
        string yes = "Yes", string no = "Cancel") =>
        await ShowAsync(owner, title, message, [yes, no]) == 0;

    private static async Task<int> ShowAsync(Window? owner, string title, string message, string[] buttons)
    {
        int result = -1;
        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            MinHeight = 140,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
        for (int i = 0; i < buttons.Length; i++)
        {
            int index = i;
            var button = new Button { Content = buttons[i], MinWidth = 80, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => { result = index; window.Close(); };
            if (i == 0) button.Classes.Add("accent");
            row.Children.Add(button);
        }
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 18,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                row
            }
        };
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return result;
    }

    /// <summary>Shows long text in a scrollable box; returns true when the user picks the first button.</summary>
    public static async Task<bool> ConfirmTextAsync(Window? owner, string title, string header, string body,
        string yes = "Apply", string no = "Cancel")
    {
        bool result = false;
        var window = new Window
        {
            Title = title,
            Width = 720,
            Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var apply = new Button { Content = yes, MinWidth = 90 };
        apply.Classes.Add("accent");
        apply.Click += (_, _) => { result = true; window.Close(); };
        var cancel = new Button { Content = no, MinWidth = 90 };
        cancel.Click += (_, _) => window.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        if (yes.Length > 0) buttons.Children.Add(apply);
        buttons.Children.Add(cancel);
        var grid = new Grid { Margin = new Thickness(16), RowDefinitions = new RowDefinitions("Auto,*,Auto") };
        grid.Children.Add(new TextBlock { Text = header, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10) });
        var box = new TextBox
        {
            Text = body,
            IsReadOnly = true,
            AcceptsReturn = true,
            FontFamily = new Avalonia.Media.FontFamily("Consolas,Menlo,monospace")
        };
        Grid.SetRow(box, 1);
        grid.Children.Add(box);
        Grid.SetRow(buttons, 2);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        grid.Children.Add(buttons);
        window.Content = grid;
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return result;
    }

    /// <summary>Asks for one line of text; returns null when cancelled.</summary>
    public static async Task<string?> PromptAsync(Window? owner, string title, string label, string initial = "")
    {
        string? result = null;
        var window = new Window
        {
            Title = title,
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var box = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", MinWidth = 80 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => { result = box.Text; window.Close(); };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => window.Close();
        box.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) { result = box.Text; window.Close(); } };
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = label },
                box,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return string.IsNullOrWhiteSpace(result) ? null : result.Trim();
    }

    /// <summary>Shows a list of checkboxes; returns the new states or null when cancelled.</summary>
    public static async Task<bool[]?> ChecklistAsync(Window? owner, string title, IReadOnlyList<(string Label, bool Checked)> items)
    {
        bool[]? result = null;
        var window = new Window
        {
            Title = title,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false
        };
        var boxes = items.Select(item => new CheckBox { Content = item.Label, IsChecked = item.Checked }).ToList();
        var ok = new Button { Content = "Apply", MinWidth = 80 };
        ok.Classes.Add("accent");
        ok.Click += (_, _) => { result = boxes.Select(box => box.IsChecked == true).ToArray(); window.Close(); };
        var cancel = new Button { Content = "Cancel", MinWidth = 80 };
        cancel.Click += (_, _) => window.Close();
        var list = new StackPanel { Spacing = 4 };
        foreach (CheckBox box in boxes) list.Children.Add(box);
        window.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 14,
            Children =
            {
                list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } }
            }
        };
        if (owner is not null) await window.ShowDialog(owner);
        else window.Show();
        return result;
    }

    public static async Task<string?> PickFolderAsync(Visual anchor, string title)
    {
        TopLevel? top = TopLevel.GetTopLevel(anchor);
        if (top is null) return null;
        IReadOnlyList<IStorageFolder> folders = await top.StorageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> PickFileAsync(Visual anchor, string title, string[]? patterns = null)
    {
        TopLevel? top = TopLevel.GetTopLevel(anchor);
        if (top is null) return null;
        IReadOnlyList<IStorageFile> files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(patterns is null ? "PS5 images and packages" : "Files")
                {
                    Patterns = patterns ?? ["*.pkg", "*.exfat", "*.ffpkg", "*.ffpfsc", "*.zar"]
                },
                FilePickerFileTypes.All
            ]
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public static async Task<string?> SaveFileAsync(Visual anchor, string title, string suggestedName, string extension)
    {
        TopLevel? top = TopLevel.GetTopLevel(anchor);
        if (top is null) return null;
        IStorageFile? file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            DefaultExtension = extension.TrimStart('.'),
            ShowOverwritePrompt = false
        });
        return file?.TryGetLocalPath();
    }

    public static void OpenPath(string path)
    {
        try
        {
            if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else if (OperatingSystem.IsMacOS())
                System.Diagnostics.Process.Start("open", [path]);
            else
                System.Diagnostics.Process.Start("xdg-open", [path]);
        }
        catch (Exception ex)
        {
            Packwright.Infrastructure.Logger.Warn("Could not open " + path + ": " + ex.Message);
        }
    }

    /// <summary>Shows a file or folder in the platform file manager.</summary>
    public static void Reveal(string path)
    {
        try
        {
            string folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path;
            if (OperatingSystem.IsWindows() && File.Exists(path))
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\"");
            else if (OperatingSystem.IsMacOS() && File.Exists(path))
                System.Diagnostics.Process.Start("open", ["-R", path]);
            else
                OpenPath(folder);
        }
        catch (Exception ex)
        {
            Packwright.Infrastructure.Logger.Warn("Could not reveal " + path + ": " + ex.Message);
        }
    }
}
