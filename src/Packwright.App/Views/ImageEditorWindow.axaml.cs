using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.Containers;
using Packwright.Infrastructure;
using UFS2Tool;

namespace Packwright.App.Views;

/// <summary>
/// Queues file changes for an exFAT or FFPKG image and applies them transactionally: the engines
/// rebuild beside the original and swap it in only after the result has been verified.
/// </summary>
public partial class ImageEditorWindow : Window
{
    private sealed record Entry(string Type, string Path, long Size)
    {
        public string SizeText => Type == "Directory" ? string.Empty : LibraryItem.FormatBytes(Size);
    }

    private sealed record Change(string Description, ExfatEditOperation? Exfat, Ufs2EditOperation? Ufs2);

    private readonly string _imagePath;
    private readonly Ps5ImageFormat _format;
    private readonly List<Change> _changes = [];
    private readonly ObservableCollection<string> _changeText = [];
    private CancellationTokenSource? _cancellation;

    public ImageEditorWindow() : this(string.Empty, Ps5ImageFormat.Exfat) { }

    public ImageEditorWindow(string imagePath, Ps5ImageFormat format)
    {
        InitializeComponent();
        _imagePath = imagePath;
        _format = format;
        ImageText.Text = imagePath;
        ChangesList.ItemsSource = _changeText;
        ReplaceButton.Click += async (_, _) => await ReplaceAsync();
        AddFileButton.Click += async (_, _) => await AddFileAsync();
        AddTreeButton.Click += async (_, _) => await AddTreeAsync();
        NewDirButton.Click += async (_, _) => await NewDirectoryAsync();
        DeleteButton.Click += async (_, _) => await DeleteAsync();
        UndoButton.Click += (_, _) => Undo();
        ApplyButton.Click += async (_, _) => await ApplyAsync();
        CancelOpButton.Click += (_, _) => _cancellation?.Cancel();
        CloseButton.Click += (_, _) => Close();
        EntryGrid.SelectionChanged += (_, _) =>
        {
            if (EntryGrid.SelectedItem is Entry entry)
                TargetBox.Text = entry.Type == "Directory" ? entry.Path : ParentPath(entry.Path);
        };
        if (imagePath.Length > 0) LoadEntries();
    }

    private void LoadEntries()
    {
        try
        {
            if (_format == Ps5ImageFormat.Exfat)
            {
                using FileStream stream = File.OpenRead(_imagePath);
                using var volume = new ExfatVolume(stream);
                EntryGrid.ItemsSource = volume.Entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new Entry(entry.IsDirectory ? "Directory" : "File", entry.Path, entry.Size)).ToList();
            }
            else
            {
                using var volume = new Ufs2Volume(_imagePath);
                EntryGrid.ItemsSource = volume.Entries.OrderBy(entry => entry.Path, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => new Entry(entry.IsDirectory ? "Directory" : "File", entry.Path, entry.Size)).ToList();
            }
        }
        catch (Exception ex)
        {
            Logger.Exception("Image editor", ex);
            StatusText.Text = "Could not read the image: " + ex.Message;
        }
    }

    private static string ParentPath(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash <= 0 ? string.Empty : path[..slash];
    }

    private string Normalize(string path) => _format == Ps5ImageFormat.Exfat
        ? ExfatImageMaintenance.NormalizeImagePath(path)
        : Ufs2Operations.NormalizeImagePath(path);

    private string Join(string name)
    {
        string parent = string.IsNullOrWhiteSpace(TargetBox.Text) ? string.Empty : Normalize(TargetBox.Text!);
        return Normalize(parent.Length == 0 ? name : parent + "/" + name);
    }

    private void Queue(string description, ExfatEditOperation? exfat, Ufs2EditOperation? ufs2)
    {
        _changes.Add(new Change(description, exfat, ufs2));
        _changeText.Add(description);
        StatusText.Text = $"{_changes.Count:N0} change(s) queued.";
    }

    private async Task<string?> PickSourceFileAsync(string title)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions { Title = title, AllowMultiple = false });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task ReplaceAsync()
    {
        if (EntryGrid.SelectedItem is not Entry { Type: "File" } entry)
        {
            StatusText.Text = "Select a file to replace.";
            return;
        }
        if (await PickSourceFileAsync("Replacement file") is not { } source) return;
        Queue($"Replace  {entry.Path}  <-  {source}",
            _format == Ps5ImageFormat.Exfat ? ExfatEditOperation.Replace(entry.Path, source) : null,
            _format == Ps5ImageFormat.Ufs2 ? Ufs2EditOperation.Replace(entry.Path, source) : null);
    }

    private async Task AddFileAsync()
    {
        if (await PickSourceFileAsync("File to add") is not { } source) return;
        string target = Join(Path.GetFileName(source));
        Queue($"Add file  {target}  <-  {source}",
            _format == Ps5ImageFormat.Exfat ? ExfatEditOperation.AddFile(target, source) : null,
            _format == Ps5ImageFormat.Ufs2 ? Ufs2EditOperation.AddFile(target, source) : null);
    }

    private async Task AddTreeAsync()
    {
        if (await Dialogs.PickFolderAsync(this, "Folder to import") is not { } source) return;
        string target = Join(new DirectoryInfo(source).Name);
        Queue($"Add tree  {target}  <-  {source}",
            _format == Ps5ImageFormat.Exfat ? ExfatEditOperation.AddDirectoryTree(target, source) : null,
            _format == Ps5ImageFormat.Ufs2 ? Ufs2EditOperation.AddDirectoryTree(target, source) : null);
    }

    private async Task NewDirectoryAsync()
    {
        string path = Normalize(TargetBox.Text ?? string.Empty);
        if (path.Length == 0)
        {
            await Dialogs.ShowMessageAsync(this, "New directory", "Type the full directory path in the target box.");
            return;
        }
        Queue($"New directory  {path}",
            _format == Ps5ImageFormat.Exfat ? ExfatEditOperation.AddDirectory(path) : null,
            _format == Ps5ImageFormat.Ufs2 ? Ufs2EditOperation.AddDirectory(path) : null);
    }

    private async Task DeleteAsync()
    {
        if (EntryGrid.SelectedItem is not Entry entry) return;
        if (!await Dialogs.ConfirmAsync(this, "Delete", $"Queue deletion of {entry.Path}?", "Queue delete")) return;
        Queue($"Delete  {entry.Path}",
            _format == Ps5ImageFormat.Exfat ? ExfatEditOperation.Delete(entry.Path) : null,
            _format == Ps5ImageFormat.Ufs2 ? Ufs2EditOperation.Delete(entry.Path) : null);
    }

    private void Undo()
    {
        if (_changes.Count == 0) return;
        _changes.RemoveAt(_changes.Count - 1);
        _changeText.RemoveAt(_changeText.Count - 1);
        StatusText.Text = $"{_changes.Count:N0} change(s) queued.";
    }

    private void SetBusy(bool busy)
    {
        foreach (Button button in new[] { ReplaceButton, AddFileButton, AddTreeButton, NewDirButton, DeleteButton,
                     UndoButton, ApplyButton, CloseButton })
            button.IsEnabled = !busy;
        CancelOpButton.IsEnabled = busy;
    }

    private async Task ApplyAsync()
    {
        if (_changes.Count == 0) return;
        if (!await Dialogs.ConfirmAsync(this, "Apply changes?",
                $"Apply {_changes.Count:N0} queued change(s)?\n\nThe image is rebuilt beside the original and replaced only after " +
                "full verification. Large game images need substantial free space and time.", "Apply"))
            return;

        _cancellation = new CancellationTokenSource();
        SetBusy(true);
        try
        {
            int files;
            if (_format == Ps5ImageFormat.Exfat)
            {
                var progress = new Progress<FfpfscProgress>(value =>
                    Report(value.Stage, value.BytesProcessed, value.TotalBytes));
                ExfatEditResult result = await ExfatImageMaintenance.ApplyEditsAsync(_imagePath,
                    _changes.Select(change => change.Exfat!).ToList(), progress, _cancellation.Token);
                files = result.Verification.FileCount;
            }
            else
            {
                var progress = new Progress<Ufs2Progress>(value =>
                    Report(value.Stage, value.BytesProcessed, value.TotalBytes));
                Ufs2EditResult result = await Ufs2Operations.ApplyEditsAsync(_imagePath,
                    _changes.Select(change => change.Ufs2!).ToList(), progress, _cancellation.Token);
                files = result.Verification.FileCount;
            }
            EditProgress.Value = 100;
            StatusText.Text = $"Completed and verified {files:N0} files.";
            _changes.Clear();
            _changeText.Clear();
            LoadEntries();
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Operation cancelled; the original image was preserved.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       ArgumentException)
        {
            StatusText.Text = "No unverified changes were kept.";
            await Dialogs.ShowMessageAsync(this, "Edit failed", ex.Message);
        }
        finally
        {
            _cancellation?.Dispose();
            _cancellation = null;
            SetBusy(false);
        }
    }

    private void Report(string stage, long done, long total)
    {
        EditProgress.Value = total <= 0 ? 0 : Math.Clamp(done * 100.0 / total, 0, 100);
        StatusText.Text = $"{stage} - {LibraryItem.FormatBytes(done)} / {LibraryItem.FormatBytes(total)}";
    }
}
