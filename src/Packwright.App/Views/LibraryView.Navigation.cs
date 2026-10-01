using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Packwright.App.Models;

namespace Packwright.App.Views;

public partial class LibraryView
{
    /// <summary>
    /// Shows the title stored at <paramref name="path"/>: clears the search and filters so it is listed, selects it
    /// (which opens its details) and scrolls to it. Returns false when the library does not hold it.
    /// </summary>
    public async Task<bool> ShowTitleAsync(string path)
    {
        if (!_items.Any(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase))) return false;
        ClearFilters();
        // Let the list rebuild before selecting in it.
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
        LibraryItem? item = _items.FirstOrDefault(candidate => string.Equals(candidate.Path, path, StringComparison.OrdinalIgnoreCase));
        if (item is null) return false;

        if (ListScroll.IsVisible)
        {
            ListGrid.SelectedItem = item;
            ListGrid.ScrollIntoView(item, null);
        }
        else
        {
            foreach (ListBox box in GroupsHost.GetVisualDescendants().OfType<ListBox>())
            {
                if (box.ItemsSource is not System.Collections.IEnumerable source || !source.Cast<object>().Contains(item)) continue;
                box.SelectedItem = item;
                box.ScrollIntoView(item);
                break;
            }
        }
        return true;
    }

    /// <summary>Shows the title and opens its Edit details window.</summary>
    public async Task EditTitleAsync(string path)
    {
        if (await ShowTitleAsync(path)) await EditSelectedAsync();
    }
}
