using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Packwright.App.Models;
using Packwright.Core.Services;

namespace Packwright.App.Views;

/// <summary>The list view's column headers: click to sort, right-click to choose which columns are shown.</summary>
public partial class LibraryView
{
    /// <summary>The column's real name. The header text also carries the sort arrow, so the name lives in the tag.</summary>
    private static string ColumnName(DataGridColumn column) => column.Tag as string ?? column.Header?.ToString() ?? string.Empty;

    private void InitializeColumnHeaders()
    {
        foreach (DataGridColumn column in ListGrid.Columns) column.Tag = column.Header?.ToString() ?? string.Empty;
        ListGrid.Sorting += OnGridSorting;
        ListGrid.AddHandler(ContextRequestedEvent, OnGridContextRequested, RoutingStrategies.Tunnel);
        UpdateSortHeaders();
    }

    /// <summary>A click on a column header sorts by it; a second click reverses the order.</summary>
    private void OnGridSorting(object? sender, DataGridColumnEventArgs e)
    {
        e.Handled = true;   // the grid's own sorting is not used: the library sorts its list itself
        string name = ColumnName(e.Column);
        if (!SortOptions.Contains(name)) return;
        _suppress = true;
        if (SortBox.SelectedItem as string == name)
            SortDirection.IsChecked = SortDirection.IsChecked != true;
        else
        {
            SortBox.SelectedItem = name;
            SortDirection.IsChecked = false;
        }
        _suppress = false;
        PersistView();
        ApplyFilters();
    }

    /// <summary>Shows the sort arrow on the column the list is sorted by.</summary>
    private void UpdateSortHeaders()
    {
        string sorted = SortBox.SelectedItem as string ?? "Title";
        string arrow = SortDirection.IsChecked == true ? "  ▼" : "  ▲";
        foreach (DataGridColumn column in ListGrid.Columns)
        {
            string name = ColumnName(column);
            column.Header = name == sorted ? name + arrow : name;
        }
    }

    private void OnGridContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        DataGridColumnHeader? header = source.FindAncestorOfType<DataGridColumnHeader>(includeSelf: true);
        if (header is null) return;
        e.Handled = true;   // not the row menu
        BuildColumnMenu().Open(header);
    }

    private ContextMenu BuildColumnMenu()
    {
        var menu = new ContextMenu();
        foreach (DataGridColumn column in ListGrid.Columns.OrderBy(column => column.DisplayIndex))
        {
            string name = ColumnName(column);
            var item = new MenuItem
            {
                Header = name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = column.IsVisible,
                IsEnabled = name != "Title",   // the title is what identifies a row
                StaysOpenOnClick = true
            };
            item.Click += (_, _) => SetColumnVisible(name, item.IsChecked);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var all = new MenuItem { Header = "Show all columns" };
        all.Click += (_, _) =>
        {
            _services.Settings.LibraryHiddenColumns = [];
            _services.SaveSettings();
            ApplyHiddenColumns();
        };
        menu.Items.Add(all);
        return menu;
    }

    private void SetColumnVisible(string name, bool visible)
    {
        List<string> hidden = _services.Settings.LibraryHiddenColumns.ToList();
        hidden.RemoveAll(existing => existing == name);
        if (!visible) hidden.Add(name);
        _services.Settings.LibraryHiddenColumns = hidden;
        _services.SaveSettings();
        ApplyHiddenColumns();
    }
}
