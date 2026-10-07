using System.Security.Cryptography;
using Packwright.Core.Services;
using Packwright.Pkg.Writer;

namespace Packwright.Core.Builders;

/// <summary>
/// The compatibility key material the user imported, if any. Packwright ships no keys; a package it builds is complete
/// and readable without them. With them, the few fields that are RSA-wrapped by the original tooling are filled in
/// properly. See <see cref="PkgKeySet"/> for the file format.
/// </summary>
public static class Ps5BuildKeys
{
    private static readonly object Gate = new();
    private static PkgKeySet _current = PkgKeySet.None;
    private static string? _path;

    public static PkgKeySet Current { get { lock (Gate) return _current; } }
    public static string? Path { get { lock (Gate) return _path; } }

    /// <summary>Loads the key file at <paramref name="path"/>; null or an empty path clears the imported keys. Throws <see cref="InvalidDataException"/> for an unacceptable file.</summary>
    public static PkgKeySet Configure(string? path)
    {
        PkgKeySet keys = string.IsNullOrWhiteSpace(path) ? PkgKeySet.None : PkgKeySet.Load(path);
        lock (Gate) { _current = keys; _path = string.IsNullOrWhiteSpace(path) ? null : path; }
        return keys;
    }

    /// <summary>Like <see cref="Configure"/> but never throws; returns the problem instead.</summary>
    public static string? TryConfigure(string? path)
    {
        try { Configure(path); return null; }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            lock (Gate) { _current = PkgKeySet.None; _path = null; }
            return ex.Message;
        }
    }
}

/// <summary>The shared part of every package build: overrides, repairs, key import, progress and the result.</summary>
internal static class PackwrightEngine
{
    public static Ps5DebugPackageBuildResult Run(IReadOnlyList<PkgSourceFile> sourceFiles, IEnumerable<string>? directories,
        string outputPath, Ps5DebugPackageBuildOptions options, IProgress<Ps5DebugPackageProgress>? progress,
        long sourceBytes, CancellationToken cancellationToken)
    {
        string destination = Path.GetFullPath(outputPath);
        Ps5DebugPackageCredentials credentials = Ps5DebugPackageCredentials.Create(options.ContentId, options.Passcode);

        List<PkgSourceFile> files = Prepare(sourceFiles, options);
        var relay = progress is null ? null : new Progress<PkgBuildProgress>(value =>
            progress.Report(new Ps5DebugPackageProgress(value.Stage, value.Done, value.Total, value.Item)));

        PkgBuildResult result = PkgBuilder.Build(files, directories, destination, new PkgBuildOptions
        {
            ContentId = credentials.ContentId,
            Passcode = credentials.Passcode,
            Keys = Ps5BuildKeys.Current,
            Seed = options.Seed,
            Compression = options.Compression == Ps5InnerCompression.Stored ? PkgCompression.Stored : PkgCompression.Kraken,
            CompressionThreads = options.KrakenThreads,
            PlayGoChunks = Math.Clamp(options.PlayGoChunkCount, 1, 64),
            Log = options.Log,
            Progress = relay,
        }, cancellationToken);

        foreach (string warning in result.Warnings) options.Log?.Invoke("warning: " + warning);
        return new Ps5DebugPackageBuildResult
        {
            OutputPath = destination,
            ContentId = credentials.ContentId,
            KeyFingerprint = credentials.Fingerprint,
            PackageSize = result.PackageSize,
            SourceBytes = sourceBytes,
            SourceFiles = sourceFiles.Count,
            UsesDefaultPasscode = credentials.UsesDefaultPasscode,
        };
    }

    // Applies the DRM and SDK overrides to param.json and refreshes the digest of trophy/activity archives.
    private static List<PkgSourceFile> Prepare(IReadOnlyList<PkgSourceFile> input, Ps5DebugPackageBuildOptions options)
    {
        var files = new List<PkgSourceFile>(input.Count);
        foreach (PkgSourceFile file in input)
        {
            string path = file.Path.Replace('\\', '/').TrimStart('/');
            if (path.Equals("sce_sys/param.json", StringComparison.Ordinal)
                && (!string.IsNullOrWhiteSpace(options.DrmTypeOverride) || options.SdkVersionOverride is not null))
            {
                byte[] data = ReadAll(file);
                if (!string.IsNullOrWhiteSpace(options.DrmTypeOverride)) data = Ps5ParamJson.ApplyDrmType(data, options.DrmTypeOverride);
                if (options.SdkVersionOverride is { } sdk) data = Ps5ParamJson.ApplySdkOverride(data, sdk);
                files.Add(file with { Path = path, Length = data.Length, Open = () => new MemoryStream(data, writable: false) });
            }
            else if (Ps5UcpArchive.IsRepairablePath(path) && file.Length <= 256 * 1024 * 1024)
            {
                byte[] data = Ps5UcpArchive.RepairIfNeeded(ReadAll(file));
                files.Add(file with { Path = path, Length = data.Length, Open = () => new MemoryStream(data, writable: false) });
            }
            else
                files.Add(file with { Path = path });
        }
        AddMissingTextures(files, options);
        return files;
    }

    // Every artwork PNG has a BC7 texture next to it in a package. A title that has the picture but no texture gets one.
    private static void AddMissingTextures(List<PkgSourceFile> files, Ps5DebugPackageBuildOptions options)
    {
        var paths = new HashSet<string>(files.Select(file => file.Path), StringComparer.Ordinal);
        foreach (string name in new[] { "icon0", "pic0", "pic1", "pic2" })
        {
            string png = $"sce_sys/{name}.png", dds = $"sce_sys/{name}.dds";
            if (!paths.Contains(png) || paths.Contains(dds)) continue;
            PkgSourceFile source = files.First(file => file.Path == png);
            if (source.Length > 64 * 1024 * 1024) continue;
            try
            {
                byte[] texture = Ps5DdsEncoder.EncodePng(ReadAll(source));
                files.Add(new PkgSourceFile(dds, texture.Length, () => new MemoryStream(texture, writable: false)));
                options.Log?.Invoke($"Generated {dds} from {png}.");
            }
            catch (Exception ex) when (ex is SixLabors.ImageSharp.ImageFormatException or NotSupportedException or ArgumentException or InvalidOperationException)
            {
                options.Log?.Invoke($"warning: {png} could not be converted to a texture ({ex.Message}); the package carries no {name}.dds.");
            }
        }
    }

    private static byte[] ReadAll(PkgSourceFile file)
    {
        using Stream stream = file.Open();
        byte[] data = new byte[file.Length];
        stream.ReadExactly(data);
        return data;
    }
}

/// <summary>Trophy and activity archives (<c>.ucp</c>) carry a SHA-1 of the whole file; an edited archive needs it refreshed.</summary>
internal static class Ps5UcpArchive
{
    private const uint Magic = 0xB228C60A;       // big-endian
    private const int HeaderSize = 96;
    private const int DigestOffset = 28;
    private const int DigestSize = 20;

    public static bool IsRepairablePath(string path) =>
        path.EndsWith(".ucp", StringComparison.OrdinalIgnoreCase)
        && (path.StartsWith("sce_sys/trophy2/", StringComparison.Ordinal) || path.StartsWith("sce_sys/uds/", StringComparison.Ordinal));

    public static byte[] RepairIfNeeded(byte[] data)
    {
        if (data.Length < HeaderSize || System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(data) != Magic) return data;
        byte[] zeroed = (byte[])data.Clone();
        Array.Clear(zeroed, DigestOffset, DigestSize);
        byte[] digest = SHA1.HashData(zeroed);
        if (digest.AsSpan().SequenceEqual(data.AsSpan(DigestOffset, DigestSize))) return data;
        digest.CopyTo(zeroed, DigestOffset);
        return zeroed;
    }
}
