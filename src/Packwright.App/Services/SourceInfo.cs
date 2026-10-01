using Avalonia.Media.Imaging;
using Packwright.Core.Models;
using Packwright.Core.Parsers;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

/// <summary>A game's display name and a small icon, used to label tasks and the selected source.</summary>
public sealed record SourceSummary(string Title, byte[]? Icon)
{
    public static SourceSummary Empty { get; } = new(string.Empty, null);
}

/// <summary>Finds the title and artwork of a dump folder, image or package.</summary>
public static class SourceInfo
{
    private const int IconWidth = 96;

    public static Task<SourceSummary> DescribeAsync(string path) => Task.Run(() => Describe(path));

    private static SourceSummary Describe(string path)
    {
        try
        {
            Ps5GameInfo? game = FromLibrary(path) ?? Read(path);
            if (game is null) return new SourceSummary(NameOf(path), null);
            string title = string.IsNullOrWhiteSpace(game.Title) ? NameOf(path) : game.Title;
            return new SourceSummary(title, ReadIcon(game));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or
                                       NotSupportedException or ArgumentException or System.Text.Json.JsonException)
        {
            Logger.Warn($"Could not describe {path}: {ex.Message}");
            return new SourceSummary(NameOf(path), null);
        }
    }

    private static string NameOf(string path) =>
        Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

    private static Ps5GameInfo? FromLibrary(string path) =>
        AppServices.Instance.Games.FirstOrDefault(game =>
            string.Equals(game.RootPath, path, StringComparison.OrdinalIgnoreCase));

    internal static Ps5GameInfo? Read(string path)
    {
        if (Directory.Exists(path))
        {
            string param = Path.Combine(path, "sce_sys", "param.json");
            return File.Exists(param) ? new Ps5ParamReader().Read(param) : null;
        }
        if (!File.Exists(path)) return null;
        if (path.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase)) return new SonyPkgGameReader().Read(path);
        return Ps5ImageFormatProbeLookup(path);
    }

    private static Ps5GameInfo? Ps5ImageFormatProbeLookup(string path)
    {
        Packwright.Containers.Ps5ImageFormat format = Packwright.Containers.Ps5ImageFormatProbe.Detect(path);
        return format switch
        {
            Packwright.Containers.Ps5ImageFormat.Exfat => new FilesystemImageGameReader().Read(path),
            Packwright.Containers.Ps5ImageFormat.Ufs2 => new FfpkgGameReader().Read(path),
            Packwright.Containers.Ps5ImageFormat.Pfs => new FfpfscGameReader().Read(path),
            Packwright.Containers.Ps5ImageFormat.ZArchive => new ZArchiveGameReader().Read(path),
            _ => null
        };
    }

    private static byte[]? ReadIcon(Ps5GameInfo game)
    {
        using IReadOnlyGameFileSystem files = GameFileSystem.Open(game);
        if (!files.FileExists("sce_sys/icon0.png")) return null;
        using Stream stream = files.OpenRead("sce_sys/icon0.png");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        memory.Position = 0;
        using Bitmap small = Bitmap.DecodeToWidth(memory, IconWidth, BitmapInterpolationMode.HighQuality);
        using var output = new MemoryStream();
        small.Save(output);
        return output.ToArray();
    }
}
