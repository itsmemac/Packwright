using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Styling;
using Packwright.App.Services;
using Packwright.Core.Backends;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class SettingsView : UserControl
{
    private static readonly string[] Layouts = ["Cards", "List"];

    private readonly AppServices _services = AppServices.Instance;
    private readonly ObservableCollection<string> _folders = [];
    private HashSet<string> _manual = new(StringComparer.OrdinalIgnoreCase);

    public SettingsView()
    {
        InitializeComponent();
        FolderList.ItemsSource = _folders;
        ViewBox.ItemsSource = Layouts;
        BackendBox.ItemsSource = BackendRegistry.All.Select(backend =>
            backend.Id == BackendRegistry.LppId && !Jobs.PlatformSupportsLpp
                ? backend.DisplayName + " (Windows only)"
                : backend.DisplayName).ToList();
        RenameTokens.Text = "Tokens: " + string.Join(" ", Ps5RenameFormatter.TokenNames);
        Load();
        ApplyTheme(_services.Settings.Theme);
        _services.LibraryChanged += () => Avalonia.Threading.Dispatcher.UIThread.Post(Load);
        AddFolderButton.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, "Add a library folder") is { } folder &&
                !_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
                _folders.Add(folder);
        };
        RemoveFolderButton.Click += (_, _) =>
        {
            if (FolderList.SelectedItem is string folder) _folders.Remove(folder);
        };
        BrowseOutputButton.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, "Default output folder") is { } folder) OutputBox.Text = folder;
        };
        SaveButton.Click += async (_, _) => await SaveAsync();
        ConsolesButton.Click += async (_, _) => await ConsolesWindow.ShowAsync(Dialogs.OwnerOf(this));
        WindowsCard.IsVisible = OperatingSystem.IsWindows();
        if (OperatingSystem.IsWindows())
        {
            UpdateShellStatus();
            InstallShellButton.Click += (_, _) => ToggleShell(install: true);
            RemoveShellButton.Click += (_, _) => ToggleShell(install: false);
        }
        ExportButton.Click += async (_, _) => await ExportAsync();
        ImportButton.Click += async (_, _) => await ImportAsync();
    }

    public event Action<string>? StatusChanged;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private void UpdateShellStatus() =>
        ShellStatus.Text = ShellIntegration.IsInstalled() ? "Installed." : "Not installed.";

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private async void ToggleShell(bool install)
    {
        try
        {
            if (install) ShellIntegration.Install();
            else ShellIntegration.Uninstall();
            UpdateShellStatus();
        }
        catch (Exception ex)
        {
            Logger.Exception("Shell integration", ex);
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Explorer integration", ex.Message);
        }
    }

    /// <summary>The header's Cards/List buttons change this setting, so the page re-reads it when it is opened.</summary>
    public void RefreshLayoutChoice() =>
        ViewBox.SelectedItem = Layouts.Contains(_services.Settings.LibraryLayout) ? _services.Settings.LibraryLayout : "Cards";

    public static void ApplyTheme(string? theme)
    {
        if (Application.Current is null) return;
        Application.Current.RequestedThemeVariant = theme switch
        {
            "Light" => ThemeVariant.Light,
            "System" => ThemeVariant.Default,
            _ => ThemeVariant.Dark
        };
    }

    private void Load()
    {
        AppSettings settings = _services.Settings;
        _folders.Clear();
        _manual = settings.ManualSources.ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string folder in settings.LibraryFolders.Concat(settings.ManualSources)) _folders.Add(folder);
        RecursiveBox.IsChecked = settings.RecursiveScan;
        RefreshOnStartBox.IsChecked = settings.RefreshOnStartup;
        UpdateCheckBox.IsChecked = settings.CheckForUpdates;
        ViewBox.SelectedItem = Layouts.Contains(settings.LibraryLayout) ? settings.LibraryLayout : "Cards";
        ThumbnailsBox.IsChecked = settings.ShowThumbnails;
        OutputBox.Text = settings.OutputDirectory;
        RenameFormatBox.Text = settings.RenameFormat;
        OpenOutputBox.IsChecked = settings.OpenOutputAfterTask;
        PreviewMbBox.Value = settings.MaxPreviewMb;
        HexPageBox.Value = settings.HexPageKb;
        ShowPreviewBox.IsChecked = settings.ShowFilePreview;
        PasscodeBox.Text = settings.DebugPasscode;
        ConfirmDeleteBox.IsChecked = settings.ConfirmDelete;
        ConfirmMoveBox.IsChecked = settings.ConfirmMove;
        BackendBox.SelectedIndex = Math.Max(0, BackendRegistry.All.ToList()
            .FindIndex(backend => backend.Id == Jobs.ResolveBackend(settings.BuildBackend).Id));
    }

    private async Task SaveAsync()
    {
        string passcode = PasscodeBox.Text ?? string.Empty;
        if (passcode.Length > 0 && passcode.Length != 32)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Settings",
                "The passcode must be exactly 32 characters, or blank for the default.");
            return;
        }
        AppSettings settings = _services.Settings;
        // Files and items that were added individually stay manual sources; the rest are library folders.
        settings.ManualSources = _folders.Where(path => File.Exists(path) || _manual.Contains(path)).ToList();
        settings.LibraryFolders = _folders.Except(settings.ManualSources, StringComparer.OrdinalIgnoreCase).ToList();
        settings.RecursiveScan = RecursiveBox.IsChecked == true;
        settings.RefreshOnStartup = RefreshOnStartBox.IsChecked == true;
        settings.CheckForUpdates = UpdateCheckBox.IsChecked == true;
        settings.UpdateCheckAsked = true;
        settings.LibraryLayout = ViewBox.SelectedItem as string ?? "Cards";
        settings.ShowThumbnails = ThumbnailsBox.IsChecked == true;
        settings.OutputDirectory = OutputBox.Text?.Trim() ?? string.Empty;
        settings.RenameFormat = RenameFormatBox.Text?.Trim() ?? string.Empty;
        settings.OpenOutputAfterTask = OpenOutputBox.IsChecked == true;
        settings.MaxPreviewMb = (int)(PreviewMbBox.Value ?? 16);
        settings.HexPageKb = (int)(HexPageBox.Value ?? 16);
        settings.ShowFilePreview = ShowPreviewBox.IsChecked == true;
        settings.DebugPasscode = passcode;
        settings.ConfirmDelete = ConfirmDeleteBox.IsChecked == true;
        settings.ConfirmMove = ConfirmMoveBox.IsChecked == true;
        settings.BuildBackend = BackendRegistry.All[Math.Max(0, BackendBox.SelectedIndex)].Id;
        _services.ReplaceSettings(settings);
        ApplyTheme(_services.Settings.Theme);
        _services.NotifySettingsChanged();
        SavedText.Text = "Saved.";
        StatusChanged?.Invoke("Settings saved.");
    }

    private async Task ExportAsync()
    {
        string? file = await Dialogs.SaveFileAsync(this, "Export settings", "packwright-settings.json", ".json");
        if (file is null) return;
        // The passcode is left out unless the user chooses to include it.
        bool includePasscode = !string.IsNullOrEmpty(_services.Settings.DebugPasscode) &&
            await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Export settings",
                "Include the saved package passcode in the exported file?", "Include", "Leave out");
        AppSettings copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_services.Settings)) ?? new();
        if (!includePasscode) copy.DebugPasscode = string.Empty;
        foreach (ConsoleProfile console in copy.Consoles) console.FtpPassword = string.Empty;   // passwords are never exported
        try
        {
            await File.WriteAllTextAsync(file, JsonSerializer.Serialize(copy, new JsonSerializerOptions { WriteIndented = true }));
            StatusChanged?.Invoke("Settings exported.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Export settings", ex.Message);
        }
    }

    private async Task ImportAsync()
    {
        string? file = await Dialogs.PickFileAsync(this, "Import settings", ["*.json"]);
        if (file is null) return;
        try
        {
            AppSettings? imported = JsonSerializer.Deserialize<AppSettings>(await File.ReadAllTextAsync(file),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (imported is null) throw new InvalidDataException("The file does not contain settings.");
            if (string.IsNullOrEmpty(imported.DebugPasscode)) imported.DebugPasscode = _services.Settings.DebugPasscode;
            _services.ReplaceSettings(imported);
            Load();
            ApplyTheme(_services.Settings.Theme);
            _services.NotifySettingsChanged();
            SavedText.Text = "Imported and saved.";
        }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or UnauthorizedAccessException)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Import settings", "That file could not be imported: " + ex.Message);
        }
    }
}
