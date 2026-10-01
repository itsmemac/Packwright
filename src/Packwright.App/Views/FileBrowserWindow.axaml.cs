using Avalonia.Controls;
using Packwright.Core.Models;

namespace Packwright.App.Views;

/// <summary>A roomy, separate window for browsing one game's files with the preview beside the tree.</summary>
public partial class FileBrowserWindow : Window
{
    public FileBrowserWindow() : this(new Ps5GameInfo(), new Ps5FileInventory()) { }

    public FileBrowserWindow(Ps5GameInfo game, Ps5FileInventory inventory)
    {
        InitializeComponent();
        string title = string.IsNullOrWhiteSpace(game.Title) ? Path.GetFileName(game.RootPath) : game.Title;
        Title = "Files · " + title;
        TitleText.Text = title;
        SubText.Text = game.RootPath;
        Files.UseSideBySideLayout();
        Files.Active = true;
        Files.Load(game, inventory);
    }
}
