using System.Reflection;
using Avalonia.Controls;
using Packwright.App.Services;

namespace Packwright.App.Views;

public partial class AboutView : UserControl
{
    public const string RepositoryUrl = "https://github.com/itsmemac/Packwright";
    public const string OriginalUrl = "https://github.com/pearlxcore/PS5PkgTool";

    public AboutView()
    {
        InitializeComponent();
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
        VersionText.Text = "Version " + version;
        UpdateButton.Click += async (_, _) => await CheckNowAsync();
        UpdateOpenButton.Click += async (_, _) =>
        {
            if (UpdateService.Available is { } release) await UpdateWindow.ShowAsync(Dialogs.OwnerOf(this), release);
        };
        RepoButton.Click += (_, _) => Dialogs.OpenPath(RepositoryUrl);
        IssuesButton.Click += (_, _) => Dialogs.OpenPath(RepositoryUrl + "/issues");
        LicenseButton.Click += (_, _) => OpenBundled("LICENSE");
        NoticesButton.Click += (_, _) => OpenBundled("THIRD_PARTY_NOTICES.md");
    }

    private async Task CheckNowAsync()
    {
        UpdateButton.IsEnabled = false;
        UpdateText.Text = "Checking GitHub...";
        UpdateOpenButton.IsVisible = false;
        try
        {
            UpdateResult result = await UpdateService.CheckAsync();
            UpdateText.Text = result.Message;
            UpdateOpenButton.IsVisible = result.State == UpdateState.Available;
            if (result.State == UpdateState.Available && result.Release is { } release)
                await UpdateWindow.ShowAsync(Dialogs.OwnerOf(this), release);
        }
        finally
        {
            UpdateButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Opens a license file shipped with the app (next to it, or in the macOS bundle's Resources folder),
    /// or the same file on GitHub when it is not found.
    /// </summary>
    private static void OpenBundled(string fileName)
    {
        foreach (string folder in new[] { AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "..", "Resources") })
        {
            string path = Path.GetFullPath(Path.Combine(folder, fileName));
            if (File.Exists(path))
            {
                Dialogs.OpenPath(path);
                return;
            }
        }
        Dialogs.OpenPath(RepositoryUrl + "/blob/main/" + fileName);
    }
}
