using System.Collections.Concurrent;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Packwright.App.Models;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

/// <summary>
/// Loads game artwork (sce_sys/icon0.png) in the background, a couple at a time, decoded to a small
/// size. The number of cached bitmaps is capped; the oldest are released and loaded again on demand.
/// </summary>
public static class ThumbnailService
{
    private const int DecodeWidth = 168;
    private static readonly SemaphoreSlim Gate = new(2);
    private static readonly ConcurrentQueue<LibraryItem> Loaded = new();
    private static int _loadedCount;

    public static void Request(LibraryItem item)
    {
        if (!AppServices.Instance.Settings.ShowThumbnails) return;
        _ = Task.Run(() => LoadAsync(item));
    }

    private static async Task LoadAsync(LibraryItem item)
    {
        await Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            byte[]? bytes = ReadIcon(item);
            if (bytes is null || bytes.Length == 0) return;
            Bitmap? bitmap = Decode(bytes);
            if (bitmap is null) return;
            await Dispatcher.UIThread.InvokeAsync(() => item.SetThumbnail(bitmap));
            Loaded.Enqueue(item);
            if (Interlocked.Increment(ref _loadedCount) > AppServices.Instance.Settings.ThumbnailCacheCount &&
                Loaded.TryDequeue(out LibraryItem? oldest))
            {
                Interlocked.Decrement(ref _loadedCount);
                await Dispatcher.UIThread.InvokeAsync(oldest.ResetThumbnail);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            Logger.Warn($"No artwork for {item.FileName}: {ex.Message}");
        }
        finally
        {
            Gate.Release();
        }
    }

    internal static byte[]? ReadIcon(LibraryItem item)
    {
        if (MetadataOverrides.IconPath(MetadataOverrides.Get(item.Path)) is { } custom)
            return File.ReadAllBytes(custom);
        using IReadOnlyGameFileSystem files = GameFileSystem.Open(item.Game);
        if (!files.FileExists("sce_sys/icon0.png")) return null;
        using Stream stream = files.OpenRead("sce_sys/icon0.png");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static Bitmap? Decode(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            return Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.MediumQuality);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}
