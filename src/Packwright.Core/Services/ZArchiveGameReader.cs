using Packwright.Containers;
using Packwright.Core.Models;
using Packwright.Core.Parsers;

namespace Packwright.Core.Services;

/// <summary>Reads an unpacked PS5 game stored in a ZArchive (<c>.zar</c>) file.</summary>
public sealed class ZArchiveGameReader
{
    private const string ParamSuffix = "sce_sys/param.json";
    private readonly Ps5ParamReader _paramReader = new();

    public Ps5GameInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        using var volume = new ZArchiveVolume(fullPath);
        ZArchiveEntry[] parameters = volume.Entries.Where(entry => !entry.IsDirectory && IsParam(entry.Path)).ToArray();
        if (parameters.Length == 0)
            throw new InvalidDataException("The ZArchive has no sce_sys/param.json.");
        if (parameters.Length > 1)
            throw new InvalidDataException("The ZArchive contains multiple PS5 game roots.");

        ZArchiveEntry parameter = parameters[0];
        string virtualRoot = parameter.Path.Length == ParamSuffix.Length
            ? string.Empty
            : parameter.Path[..^(ParamSuffix.Length + 1)];
        string raw = volume.ReadAllText(parameter.Path);
        var fileInfo = new FileInfo(fullPath);
        Ps5GameInfo game = _paramReader.ReadJson(raw, parameter.Path, fullPath, fileInfo.LastWriteTimeUtc);
        game.SourceKind = Ps5SourceKind.ZArchive;
        game.RootPath = fullPath;
        game.ParamPath = parameter.Path;
        game.VirtualRoot = virtualRoot;
        game.SourceSize = fileInfo.Length;
        game.ContainerInnerFileName = fileInfo.Name;
        game.ContainerFileLength = fileInfo.Length;
        game.ContainerLogicalSize = volume.TotalFileBytes;
        game.ContainerStoredSize = fileInfo.Length;
        return game;
    }

    /// <summary>Structure-only record for an archive without one readable game root.</summary>
    public Ps5GameInfo ReadStructure(string path)
    {
        string fullPath = Path.GetFullPath(path);
        using var volume = new ZArchiveVolume(fullPath);
        int roots = volume.Entries.Count(entry => !entry.IsDirectory && IsParam(entry.Path));
        var fileInfo = new FileInfo(fullPath);
        return SourceStructure.Build(Ps5SourceKind.ZArchive, fullPath, fileInfo.Name, fileInfo.Length,
            volume.TotalFileBytes, fileInfo.Length, 0, "ZArchive", roots, fileInfo.LastWriteTimeUtc);
    }

    private static bool IsParam(string path) =>
        path.Equals(ParamSuffix, StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith("/" + ParamSuffix, StringComparison.OrdinalIgnoreCase);
}
