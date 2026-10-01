using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

/// <summary>
/// A file explorer for a game: folder tree with sizes and counts, filtering, folder or file extraction
/// and a preview for images, text and raw bytes.
/// </summary>
public partial class FilesPanel : UserControl
{
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    private static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".json", ".xml", ".cfg", ".ini", ".log", ".lua", ".js", ".md", ".csv", ".yml", ".yaml", ".html",
        ".htm", ".css", ".py", ".sh", ".conf", ".properties", ".sfo", ".tsv", ".meta"
    };

    private readonly AppServices _services = AppServices.Instance;
    private Ps5GameInfo? _game;
    private Ps5FileInventory? _inventory;
    private List<FileNode> _roots = [];
    private List<FileNode> _selected = [];
    private string? _previewPath;
    private long _pageOffset;
    private int _pageSize;
    private long _previewSize;
    private bool _sideBySide;
    private CancellationTokenSource? _previewCts;
    private bool _treePending;
    private bool _active;

    /// <summary>
    /// The tree (one row object per file) is only built while the panel is shown, so selecting a game with
    /// 100,000+ files does not build it for every click.
    /// </summary>
    public bool Active
    {
        get => _active;
        set
        {
            _active = value;
            if (value && _treePending) BuildTree();
        }
    }

    public FilesPanel()
    {
        InitializeComponent();

        ApplyLayout(sideBySide: false);
        FilterBox.TextChanged += (_, _) => BuildTree();
        Tree.SelectionChanged += async (_, _) => await OnSelectionAsync();
        ExpandButton.Click += async (_, _) => await ExpandAllAsync();
        BusyCancel.Click += (_, _) => _expandCts?.Cancel();
        CollapseButton.Click += (_, _) => FileNode.SetExpanded(_roots, false);
        PopOutButton.Click += (_, _) => PopOut();
        CopyPathButton.Click += async (_, _) => await CopyPathAsync();
        ExtractFileButton.Click += async (_, _) => await ExtractAsync(all: false);
        ExtractAllButton.Click += async (_, _) => await ExtractAsync(all: true);
        PrevPageButton.Click += async (_, _) => await ShowPageAsync(_pageOffset - _pageSize);
        NextPageButton.Click += async (_, _) => await ShowPageAsync(_pageOffset + _pageSize);
        ResetPreview();
    }

    public event Action<string>? StatusChanged;

    // The tree is not virtualized, so opening every folder of a game with 100,000+ files would build far too
    // many rows at once. Folders are opened level by level, in small batches so the window keeps painting
    // (with a spinner and a Stop button), up to a number of rows that stays responsive.
    private const int MaximumExpandedRows = 4000;
    private CancellationTokenSource? _expandCts;

    private async Task ExpandAllAsync()
    {
        if (_roots.Count == 0) return;
        _expandCts?.Cancel();
        var cts = _expandCts = new CancellationTokenSource();
        BusyText.Text = "Opening folders...";
        BusyOverlay.IsVisible = true;
        bool limited = false;
        try
        {
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            int rows = _roots.Count;
            int batch = 0;
            List<FileNode> level = _roots.Where(node => node.IsDirectory).ToList();
            while (level.Count > 0 && !limited)
            {
                var next = new List<FileNode>();
                foreach (FileNode node in level)
                {
                    if (rows >= MaximumExpandedRows) { limited = true; break; }
                    node.IsExpanded = true;
                    rows += node.Children.Count;
                    next.AddRange(node.Children.Where(child => child.IsDirectory));
                    if (++batch % 25 != 0) continue;
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    cts.Token.ThrowIfCancellationRequested();
                }
                level = next;
            }
            StatusChanged?.Invoke(limited
                ? "Opened folders as far as the list stays responsive. Use the filter to find a specific file."
                : "All folders opened.");
        }
        catch (OperationCanceledException)
        {
            StatusChanged?.Invoke("Stopped opening folders.");
        }
        finally
        {
            if (ReferenceEquals(_expandCts, cts))
            {
                BusyOverlay.IsVisible = false;
                _expandCts = null;
            }
        }
    }

    /// <summary>Tree on the left and preview on the right, for use in a wide window.</summary>
    public void UseSideBySideLayout() => ApplyLayout(sideBySide: true);

    private void ApplyLayout(bool sideBySide)
    {
        _sideBySide = sideBySide;
        PopOutButton.IsVisible = !sideBySide;
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();
        if (sideBySide)
        {
            Body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 240 });
            Body.ColumnDefinitions.Add(new ColumnDefinition(10, GridUnitType.Pixel));
            Body.ColumnDefinitions.Add(new ColumnDefinition(1.25, GridUnitType.Star) { MinWidth = 240 });
            Grid.SetColumn(TreeHost, 0); Grid.SetRow(TreeHost, 0);
            Grid.SetColumn(Splitter, 1); Grid.SetRow(Splitter, 0);
            Grid.SetColumn(PreviewHost, 2); Grid.SetRow(PreviewHost, 0);
            Splitter.ResizeDirection = GridResizeDirection.Columns;
            Splitter.Width = 10; Splitter.Height = double.NaN;
        }
        else
        {
            Body.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 120 });
            Body.RowDefinitions.Add(new RowDefinition(10, GridUnitType.Pixel));
            Body.RowDefinitions.Add(new RowDefinition(250, GridUnitType.Pixel) { MinHeight = 120 });
            Grid.SetColumn(TreeHost, 0); Grid.SetRow(TreeHost, 0);
            Grid.SetColumn(Splitter, 0); Grid.SetRow(Splitter, 1);
            Grid.SetColumn(PreviewHost, 0); Grid.SetRow(PreviewHost, 2);
            Splitter.ResizeDirection = GridResizeDirection.Rows;
            Splitter.Height = 10; Splitter.Width = double.NaN;
        }
    }

    public void Clear()
    {
        _game = null;
        _inventory = null;
        _treePending = false;
        _roots = [];
        _selected = [];
        Tree.ItemsSource = null;
        FilesSummary.Text = string.Empty;
        EmptyText.IsVisible = false;
        ResetPreview();
    }

    public void Load(Ps5GameInfo game, Ps5FileInventory inventory)
    {
        _game = game;
        _inventory = inventory;
        FilesSummary.Text = $"{inventory.FileCount:N0} file(s)  ·  {LibraryItem.FormatBytes(inventory.TotalSize)}";
        FilterBox.Text = string.Empty;
        BuildTree();
        ResetPreview();
    }

    private void BuildTree()
    {
        if (_inventory is null) return;
        if (!_active)
        {
            _treePending = true;
            return;
        }
        _treePending = false;
        string filter = FilterBox.Text?.Trim() ?? string.Empty;
        IEnumerable<Ps5FileInfo> files = filter.Length == 0
            ? _inventory.Files
            : _inventory.Files.Where(file => file.RelativePath.Contains(filter, StringComparison.OrdinalIgnoreCase));
        _roots = FileNode.BuildTree(files, expandAll: filter.Length > 0);
        Tree.ItemsSource = _roots;
        EmptyText.IsVisible = _roots.Count == 0;
    }

    // ---------------------------------------------------------------- preview

    private void ResetPreview()
    {
        _previewCts?.Cancel();
        _previewPath = null;
        PreviewImage.IsVisible = false;
        PreviewImage.Source = null;
        PreviewText.IsVisible = true;
        PreviewText.Text = "Select a file to preview it.";
        PreviewTitle.Text = "Preview";
        PreviewInfo.Text = string.Empty;
        PrevPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        CopyPathButton.IsEnabled = false;
    }

    private async Task OnSelectionAsync()
    {
        _selected = Tree.SelectedItems?.OfType<FileNode>().ToList() ?? [];
        CopyPathButton.IsEnabled = _selected.Count > 0;
        if (_selected.Count == 0) { ResetPreview(); return; }
        if (_selected.Count > 1)
        {
            ResetPreview();
            CopyPathButton.IsEnabled = true;
            int files = _selected.SelectMany(node => node.Files()).Distinct().Count();
            PreviewTitle.Text = $"{_selected.Count:N0} items selected";
            PreviewText.Text = $"{files:N0} file(s) would be extracted.";
            return;
        }

        FileNode node = _selected[0];
        PreviewInfo.Text = DescribeNode(node);
        if (node.IsDirectory)
        {
            _previewCts?.Cancel();
            _previewPath = null;
            PreviewImage.IsVisible = false;
            PreviewText.IsVisible = true;
            PreviewTitle.Text = node.Name;
            PreviewText.Text = $"Folder\n{node.FullPath}\n\n{node.FileCount:N0} file(s)\n{LibraryItem.FormatBytes(node.Size)}" +
                               $"\n\nUse \"Extract selected\" to extract the whole folder.";
            PrevPageButton.IsEnabled = NextPageButton.IsEnabled = false;
            return;
        }

        if (!_services.Settings.ShowFilePreview)
        {
            PreviewTitle.Text = node.Name;
            PreviewText.Text = "File preview is turned off in Settings.";
            return;
        }
        _previewPath = node.FullPath;
        _previewSize = node.Size;
        _pageSize = Math.Max(1, _services.Settings.HexPageKb) * 1024;
        PreviewTitle.Text = node.Name;
        if (_previewSize == 0)
        {
            PreviewImage.IsVisible = false;
            PreviewText.IsVisible = true;
            PreviewText.Text = "(empty file)";
            PrevPageButton.IsEnabled = NextPageButton.IsEnabled = false;
            return;
        }
        string extension = Path.GetExtension(node.Name);
        long limit = (long)Math.Max(1, _services.Settings.MaxPreviewMb) * 1024 * 1024;
        if ((ImageExtensions.Contains(extension) || extension.Equals(".dds", StringComparison.OrdinalIgnoreCase)) &&
            _previewSize <= limit)
        {
            await ShowImageAsync(node.FullPath, extension);
            return;
        }
        await ShowPageAsync(0);
    }

    private static string DescribeNode(FileNode node)
    {
        if (node.IsDirectory) return node.FullPath;
        var parts = new List<string> { node.FullPath, LibraryItem.FormatBytes(node.Size) };
        if (node.Info is { } info)
        {
            if (info.Origin.Length > 0) parts.Add(info.Origin);
            if (info.IsEncrypted) parts.Add("encrypted");
        }
        return string.Join("  ·  ", parts);
    }

    private async Task ShowImageAsync(string path, string extension)
    {
        Ps5GameInfo game = _game!;
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        CancellationToken token = _previewCts.Token;
        PrevPageButton.IsEnabled = NextPageButton.IsEnabled = false;
        try
        {
            Bitmap? bitmap = await Task.Run(() =>
            {
                using IReadOnlyGameFileSystem files = GameFileSystem.Open(game, token);
                using Stream stream = files.OpenRead(GameFileSystem.NormalizePath(path));
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                memory.Position = 0;
                if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
                {
                    (byte[] rgba, int width, int height) = Ps5ImageCodec.DecodeDdsToRgba(memory);
                    var bmp = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
                        PixelFormat.Rgba8888, AlphaFormat.Unpremul);
                    using ILockedFramebuffer buffer = bmp.Lock();
                    for (int y = 0; y < height; y++)
                        System.Runtime.InteropServices.Marshal.Copy(rgba, y * width * 4,
                            buffer.Address + y * buffer.RowBytes, width * 4);
                    return (Bitmap)bmp;
                }
                return new Bitmap(memory);
            }, token);
            if (token.IsCancellationRequested || _previewPath != path) return;
            PreviewImage.Source = bitmap;
            PreviewImage.IsVisible = true;
            PreviewText.IsVisible = false;
            if (bitmap is not null)
                PreviewInfo.Text = DescribeNode(_selected[0]) + $"  ·  {bitmap.PixelSize.Width}x{bitmap.PixelSize.Height}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreviewImage.IsVisible = false;
            PreviewText.IsVisible = true;
            PreviewText.Text = "Could not show this image: " + ex.Message;
        }
    }

    private async Task ShowPageAsync(long offset)
    {
        if (_game is null || _previewPath is null) return;
        Ps5GameInfo game = _game;
        string path = _previewPath;
        offset = Math.Clamp(offset, 0, Math.Max(0, _previewSize - 1));
        offset -= offset % _pageSize;
        _previewCts?.Cancel();
        _previewCts = new CancellationTokenSource();
        CancellationToken token = _previewCts.Token;
        try
        {
            GameFileChunk chunk = await Task.Run(() => GameFileSystem.ReadFileChunk(game, path, offset, _pageSize, token), token);
            if (token.IsCancellationRequested || _previewPath != path) return;
            _pageOffset = chunk.Offset;
            bool text = TextExtensions.Contains(Path.GetExtension(path)) && LooksLikeText(chunk.Data);
            PreviewText.Text = text ? Encoding.UTF8.GetString(chunk.Data) : HexDump(chunk.Data, chunk.Offset);
            PreviewImage.IsVisible = false;
            PreviewText.IsVisible = true;
            PrevPageButton.IsEnabled = chunk.HasPrevious;
            NextPageButton.IsEnabled = chunk.HasNext;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PreviewText.Text = "Could not read this file: " + ex.Message;
            PreviewText.IsVisible = true;
        }
    }

    private static bool LooksLikeText(byte[] data)
    {
        int control = 0;
        foreach (byte value in data.Take(4096))
            if (value < 0x09 || (value > 0x0D && value < 0x20)) control++;
        return control * 50 < Math.Min(data.Length, 4096) + 1;
    }

    private static string HexDump(byte[] data, long baseOffset)
    {
        var text = new StringBuilder(data.Length * 4);
        for (int row = 0; row < data.Length; row += 16)
        {
            text.Append((baseOffset + row).ToString("X8")).Append("  ");
            var ascii = new StringBuilder(16);
            for (int column = 0; column < 16; column++)
            {
                if (row + column < data.Length)
                {
                    byte value = data[row + column];
                    text.Append(value.ToString("X2")).Append(' ');
                    ascii.Append(value is >= 0x20 and < 0x7F ? (char)value : '.');
                }
                else text.Append("   ");
                if (column == 7) text.Append(' ');
            }
            text.Append(" |").Append(ascii).Append("|\n");
        }
        return text.ToString();
    }

    // ---------------------------------------------------------------- actions

    private async Task CopyPathAsync()
    {
        if (_selected.Count == 0) return;
        string text = string.Join(Environment.NewLine, _selected.Select(node => node.FullPath));
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
            await clipboard.SetTextAsync(text);
        StatusChanged?.Invoke("Copied " + (_selected.Count == 1 ? _selected[0].FullPath : $"{_selected.Count:N0} paths") + ".");
    }

    private void PopOut()
    {
        if (_game is null || _inventory is null) return;
        var window = new FileBrowserWindow(_game, _inventory);
        if (Dialogs.OwnerOf(this) is { } owner) window.Show(owner);
        else window.Show();
    }

    private async Task ExtractAsync(bool all)
    {
        Ps5GameInfo? game = _game;
        if (game is null || _inventory is null) return;
        List<string> wanted = all
            ? _inventory.Files.Select(file => file.RelativePath).ToList()
            : _selected.SelectMany(node => node.Files()).Select(node => node.FullPath).Distinct().ToList();
        if (wanted.Count == 0)
        {
            StatusChanged?.Invoke("Select files or folders to extract first.");
            return;
        }
        string? folder = await Dialogs.PickFolderAsync(this, "Extract to...");
        if (folder is null) return;
        StatusChanged?.Invoke($"Extracting {wanted.Count:N0} file(s)...");
        try
        {
            string root = Path.GetFullPath(folder);
            await Task.Run(async () =>
            {
                foreach (string relative in wanted)
                {
                    string target = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
                    if (!target.StartsWith(root, StringComparison.Ordinal))
                        throw new InvalidDataException("A path escapes the extraction folder: " + relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    await GameFileSystem.ExtractFileAsync(game, relative, target);
                }
            });
            StatusChanged?.Invoke($"Extracted {wanted.Count:N0} file(s) to {folder}.");
            if (_services.Settings.OpenOutputAfterTask) Dialogs.OpenPath(folder);
        }
        catch (Exception ex)
        {
            Logger.Exception("Extract files", ex);
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Extract", ex.Message);
        }
    }
}
