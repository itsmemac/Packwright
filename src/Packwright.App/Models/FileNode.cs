using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Packwright.Core.Models;

namespace Packwright.App.Models;

/// <summary>One folder or file in the game file browser tree.</summary>
public sealed class FileNode : INotifyPropertyChanged
{
    private static readonly Geometry FolderIcon = Geometry.Parse("M10 4H4c-1.1 0-1.99.9-1.99 2L2 18c0 1.1.9 2 2 2h16c1.1 0 2-.9 2-2V8c0-1.1-.9-2-2-2h-8l-2-2z");
    private static readonly Geometry FileIcon = Geometry.Parse("M14 2H6c-1.1 0-1.99.9-1.99 2L4 20c0 1.1.89 2 1.99 2H18c1.1 0 2-.9 2-2V8l-6-6zm2 16H8v-2h8v2zm0-4H8v-2h8v2zm-3-5V3.5L18.5 9H13z");
    private static readonly Geometry ImageIcon = Geometry.Parse("M21 19V5c0-1.1-.9-2-2-2H5c-1.1 0-2 .9-2 2v14c0 1.1.9 2 2 2h14c1.1 0 2-.9 2-2zM8.5 13.5l2.5 3.01L14.5 12l4.5 6H5l3.5-4.5z");
    private static readonly Geometry CodeIcon = Geometry.Parse("M9.4 16.6L4.8 12l4.6-4.6L8 6l-6 6 6 6 1.4-1.4zm5.2 0l4.6-4.6-4.6-4.6L16 6l6 6-6 6-1.4-1.4z");
    private static readonly Geometry MediaIcon = Geometry.Parse("M12 3v10.55c-.59-.34-1.27-.55-2-.55-2.21 0-4 1.79-4 4s1.79 4 4 4 4-1.79 4-4V7h4V3h-6z");
    private static readonly Geometry BinaryIcon = Geometry.Parse("M4 4h6v6H4zm10 0h6v6h-6zM4 14h6v6H4zm10 0h6v6h-6z");

    private static readonly IBrush FolderBrush = new SolidColorBrush(Color.Parse("#F5B642"));
    private static readonly IBrush ImageBrush = new SolidColorBrush(Color.Parse("#3DD68C"));
    private static readonly IBrush CodeBrush = new SolidColorBrush(Color.Parse("#7C8CFF"));
    private static readonly IBrush MediaBrush = new SolidColorBrush(Color.Parse("#C084FC"));
    private static readonly IBrush PlainBrush = new SolidColorBrush(Color.Parse("#8E94AE"));

    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".dds", ".tga", ".ico" };
    private static readonly HashSet<string> CodeTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".json", ".xml", ".cfg", ".ini", ".log", ".lua", ".js", ".md", ".csv", ".yml", ".yaml", ".html", ".css", ".py", ".sh", ".conf", ".sfo", ".meta", ".self", ".elf", ".sprx", ".prx" };
    private static readonly HashSet<string> MediaTypes = new(StringComparer.OrdinalIgnoreCase)
        { ".at9", ".mp3", ".ogg", ".wav", ".mp4", ".webm", ".mkv", ".avi", ".bk2", ".usm", ".wem", ".opus", ".m4a" };

    private bool _isExpanded;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; set; }
    public int FileCount { get; set; }
    public Ps5FileInfo? Info { get; init; }
    public List<FileNode> Children { get; } = [];

    public Geometry Icon { get; private set; } = FileIcon;
    public IBrush IconBrush { get; private set; } = PlainBrush;

    public string Detail => IsDirectory
        ? $"{FileCount:N0} file{(FileCount == 1 ? string.Empty : "s")}  ·  {LibraryItem.FormatBytes(Size)}"
        : LibraryItem.FormatBytes(Size);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
        }
    }

    public void ApplyIcon()
    {
        if (IsDirectory) { Icon = FolderIcon; IconBrush = FolderBrush; return; }
        string extension = Path.GetExtension(Name);
        (Icon, IconBrush) = extension switch
        {
            _ when ImageTypes.Contains(extension) => (ImageIcon, ImageBrush),
            _ when CodeTypes.Contains(extension) => (CodeIcon, CodeBrush),
            _ when MediaTypes.Contains(extension) => (MediaIcon, MediaBrush),
            ".bin" or ".dat" or ".pak" or ".pkg" or ".psarc" => (BinaryIcon, PlainBrush),
            _ => (FileIcon, PlainBrush)
        };
    }

    /// <summary>Builds the folder tree for a set of files and returns its top-level nodes.</summary>
    public static List<FileNode> BuildTree(IEnumerable<Ps5FileInfo> files, bool expandAll)
    {
        var roots = new List<FileNode>();
        var folders = new Dictionary<string, FileNode>(StringComparer.Ordinal);

        FileNode Folder(string path)
        {
            if (folders.TryGetValue(path, out FileNode? existing)) return existing;
            int slash = path.LastIndexOf('/');
            var node = new FileNode { Name = slash < 0 ? path : path[(slash + 1)..], FullPath = path, IsDirectory = true, _isExpanded = expandAll };
            node.ApplyIcon();
            folders[path] = node;
            (slash < 0 ? roots : Folder(path[..slash]).Children).Add(node);
            return node;
        }

        foreach (Ps5FileInfo file in files)
        {
            string path = file.RelativePath.Replace('\\', '/').Trim('/');
            if (path.Length == 0) continue;
            int slash = path.LastIndexOf('/');
            var leaf = new FileNode
            {
                Name = slash < 0 ? path : path[(slash + 1)..],
                FullPath = path,
                Size = file.Size,
                FileCount = 1,
                Info = file
            };
            leaf.ApplyIcon();
            (slash < 0 ? roots : Folder(path[..slash]).Children).Add(leaf);
        }

        Finish(roots);
        return roots;
    }

    private static void Finish(List<FileNode> nodes)
    {
        foreach (FileNode node in nodes.Where(node => node.IsDirectory))
        {
            Finish(node.Children);
            node.Size = node.Children.Sum(child => child.Size);
            node.FileCount = node.Children.Sum(child => child.FileCount);
        }
        nodes.Sort((a, b) => a.IsDirectory != b.IsDirectory
            ? a.IsDirectory ? -1 : 1
            : string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<FileNode> SelfAndDescendants()
    {
        yield return this;
        foreach (FileNode child in Children)
            foreach (FileNode inner in child.SelfAndDescendants())
                yield return inner;
    }

    public IEnumerable<FileNode> Files() => SelfAndDescendants().Where(node => !node.IsDirectory);

    public static void SetExpanded(IEnumerable<FileNode> nodes, bool expanded)
    {
        foreach (FileNode node in nodes)
            foreach (FileNode inner in node.SelfAndDescendants().Where(inner => inner.IsDirectory))
                inner.IsExpanded = expanded;
    }
}
