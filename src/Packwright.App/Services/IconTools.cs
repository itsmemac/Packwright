using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Packwright.Core.Models;

namespace Packwright.App.Services;

/// <summary>Turns artwork into the 512x512 PNG a title uses as its icon (sce_sys/icon0.png).</summary>
public static class IconTools
{
    public const int IconSize = 512;

    /// <summary>Centre-crops <paramref name="source"/> to a square and scales it to 512x512. Runs on the UI thread.</summary>
    public static Task<byte[]?> SquarePngAsync(Ps5ImageData source) =>
        Dispatcher.UIThread.InvokeAsync(() => SquarePng(source)).GetTask();

    public static byte[]? SquarePng(Ps5ImageData source)
    {
        try
        {
            using Bitmap decoded = Decode(source);
            return Square(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static byte[]? SquarePng(string file)
    {
        try
        {
            using var decoded = new Bitmap(file);
            return Square(decoded);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static byte[] Square(Bitmap decoded)
    {
        using var square = new RenderTargetBitmap(new PixelSize(IconSize, IconSize));
        using (DrawingContext context = square.CreateDrawingContext())
        {
            double side = Math.Min(decoded.PixelSize.Width, decoded.PixelSize.Height);
            var crop = new Rect((decoded.PixelSize.Width - side) / 2, (decoded.PixelSize.Height - side) / 2, side, side);
            context.DrawImage(decoded, crop, new Rect(0, 0, IconSize, IconSize));
        }
        using var memory = new MemoryStream();
        square.Save(memory);
        return memory.ToArray();
    }

    private static Bitmap Decode(Ps5ImageData data)
    {
        if (!data.IsRgba) return new Bitmap(new MemoryStream(data.Bytes, writable: false));
        var bitmap = new WriteableBitmap(new PixelSize(data.Width, data.Height), new Vector(96, 96),
            PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer buffer = bitmap.Lock();
        int rowBytes = data.Width * 4;
        for (int y = 0; y < data.Height; y++)
            System.Runtime.InteropServices.Marshal.Copy(data.Bytes, y * rowBytes, buffer.Address + y * buffer.RowBytes, rowBytes);
        return bitmap;
    }
}
