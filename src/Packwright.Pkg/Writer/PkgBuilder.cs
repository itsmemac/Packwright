// Packwright.Pkg - builds a package (.pkg) from a set of files: the whole pipeline in one call.
//
// The files are split into the ones that belong in the package's metadata container (sce_sys/param.json, artwork,
// protected title data, play-go files, see PkgEntryTable) and everything else, which becomes the inner image. The
// inner image is stored (compressed with Kraken or as it is), wrapped in the outer filesystem, framed by the container, the
// finalised-image header and the install metadata archive. Memory use does not depend on the size of the title: file
// data is streamed from the sources straight into the output.
#nullable enable
using Packwright.Pkg.Util;
using System.Security.Cryptography;
using System.Text.Json;
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Writer;

/// <summary>A file to put in the package. <paramref name="Path"/> is relative to the title root, with '/' separators.</summary>
public sealed record PkgSourceFile(string Path, long Length, Func<Stream> Open);

public sealed record PkgBuildProgress(string Stage, long Done, long Total, string Item = "");

public sealed class PkgBuildOptions
{
    public const string DefaultPasscode = "00000000000000000000000000000000";

    public required string ContentId { get; init; }
    public string Passcode { get; init; } = DefaultPasscode;
    /// <summary>Optional imported key material; the package is complete without it.</summary>
    public PkgKeySet Keys { get; init; } = PkgKeySet.None;
    /// <summary>Optional 16-byte seed for the outer image. Supplying one makes the output reproducible.</summary>
    public byte[]? Seed { get; init; }
    /// <summary>Time stamped into the filesystems, in seconds since 1970. Zero by default, which keeps builds reproducible.</summary>
    public long TimestampSeconds { get; init; }
    /// <summary>How the inner image is stored. Stored (the default) keeps every span as it is.</summary>
    public PkgCompression Compression { get; init; } = PkgCompression.Stored;
    /// <summary>Spans compressed at the same time; 0 uses every processor.</summary>
    public int CompressionThreads { get; init; }
    /// <summary>Play-go chunks for a title that brings no play-go files of its own (1 to 64).</summary>
    public int PlayGoChunks { get; init; } = 1;
    public Action<string>? Log { get; init; }
    public IProgress<PkgBuildProgress>? Progress { get; init; }
}

public sealed class PkgBuildResult
{
    public required string OutputPath { get; init; }
    public required long PackageSize { get; init; }
    public required int InnerFiles { get; init; }
    public required int MetadataEntries { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public static class PkgBuilder
{
    public static PkgBuildResult Build(IReadOnlyList<PkgSourceFile> files, IEnumerable<string>? directories, string outputPath,
        PkgBuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        if (options.ContentId.Length != 36) throw new ArgumentException("The content id must be exactly 36 characters.", nameof(options));
        if (options.Passcode.Length != 32) throw new ArgumentException("The passcode must be exactly 32 characters.", nameof(options));
        if (options.Seed is { Length: not 16 }) throw new ArgumentException("The seed must be 16 bytes.", nameof(options));
        var warnings = new List<string>();
        void Log(string message) => options.Log?.Invoke(message);
        void Report(string stage, long done = 0, long total = 0, string item = "") => options.Progress?.Report(new PkgBuildProgress(stage, done, total, item));

        // ---- 1. sort the files out ---------------------------------------------------------------------------------
        Report("Reading source folder");
        var entries = new Dictionary<uint, PkgSourceFile>();
        var inner = new List<PkgSourceFile>();
        foreach (PkgSourceFile f in files)
        {
            string path = f.Path.Replace('\\', '/').TrimStart('/');
            if (path.StartsWith("sce_sys/", StringComparison.Ordinal) && PkgEntryTable.IdOf(path["sce_sys/".Length..]) is { } id)
            {
                if (!entries.TryAdd(id, f with { Path = path })) warnings.Add($"'{path}' maps to the same package entry as '{entries[id].Path}' and was skipped.");
                continue;
            }
            if (path == "sce_sys/imagedigs.dat") continue;
            inner.Add(f with { Path = path });
        }
        if (!entries.TryGetValue(PkgEntryTable.ParamJson, out PkgSourceFile? paramSource))
            throw new InvalidDataException("The title has no sce_sys/param.json, which every package needs.");
        if (inner.Count == 0) throw new InvalidDataException("The title has no files for the inner image.");
        byte[] param = ReadAll(paramSource);
        (string? contentVersion, bool upgradable) = ReadParam(param);
        if (!inner.Any(f => f.Path == "sce_sys/keystone"))
            warnings.Add("These files are not present in the title, so the package is built without them: sce_sys/keystone.");

        if (!inner.Any(f => f.Path == "sce_sys/about/right.sprx"))
            warnings.Add("These files are not present in the title, so the package is built without them: sce_sys/about/right.sprx.");

        // data order: keystone, the rest of sce_sys, then everything else by name
        PkgSourceFile[] ordered = inner
            .OrderBy(f => f.Path == "sce_sys/keystone" ? 0 : f.Path.StartsWith("sce_sys/", StringComparison.Ordinal) ? 1 : 2)
            .ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();

        // ---- 2. plan the inner image ---------------------------------------------------------------------------
        Report("Inner image");
        Log("Planning the inner image...");
        var specs = new List<InnerFile>(ordered.Length);
        foreach (PkgSourceFile f in ordered)
            specs.Add(new InnerFile(f.Path, f.Length, IsModule(f)));
        InnerImagePlan plan = InnerImagePlanner.Plan(specs, directories, options.TimestampSeconds);
        var sources = new Dictionary<string, PkgSourceFile>(StringComparer.Ordinal);
        foreach (PkgSourceFile f in ordered) sources[f.Path] = f;

        // ---- 3. the outer image --------------------------------------------------------------------------------------
        string? directory = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        byte[] seed = options.Seed ?? RandomNumberGenerator.GetBytes(16);
        byte[] ekpfs = Crypto.ComputeKeys(options.ContentId, options.Passcode, 1, useSha3: true);
        long totalBytes = plan.MountSize;
        var progress = new Progress<long>(done => Report("Outer PFS", done, totalBytes));

        string partial = outputPath + ".packwright-part";
        try
        {
            OuterImageInfo outer;
            using var output = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            output.SetLength(FinalImage.HeaderSize);
            output.Position = FinalImage.HeaderSize;
            Log("Writing the outer image...");
            StoredImageResult stored;
            using (var writer = new OuterPfsWriter(output, ekpfs, seed, options.TimestampSeconds, 0, encrypt: true))
            {
                Report("NAPS");
                stored = StoredImageBuilder.Build(plan, index => sources[plan.Files[index].Path].Open(), writer,
                    options.Compression, options.CompressionThreads, progress, cancellationToken);
                outer = writer.Complete(stored.Layout, plan.MountSize);
            }
            Log($"Stored image: {stored.StoredSize:N0} bytes ({stored.CompressedSpans:N0} compressed spans, {stored.RawSpans:N0} stored as they are).");

            // ---- 4. the finalised-image header, then the container ---------------------------------------------
            byte[] header = FinalImage.BuildHeader(outer, stored.Layout, plan.MountSize, plan.MetadataOffset, plan.ContentInodes,
                plan.AppFileCount, FinalImage.ContentVersionBcd(contentVersion));
            var cntEntries = BuildEntries(entries, outer, plan, stored, options.ContentId, options.PlayGoChunks, warnings);
            Report("CNT");
            Log("Writing the metadata container...");
            long cntStart = FinalImage.HeaderSize + outer.PfsSize;
            CntResult cnt = CntBuilder.Build(output, cntStart, new CntParameters
            {
                ContentId = options.ContentId, Passcode = options.Passcode, Keys = options.Keys, Entries = cntEntries,
                PfsSize = outer.PfsSize, ImageSeed = seed, SuperblockDigest = Sha3.Hash(outer.Superblock),
                FihDigest = Sha3.Hash(header), Upgradable = upgradable,
            }, cancellationToken);

            // ---- 5. install metadata archive -------------------------------------------------------------------
            Report("Finalize");
            byte[] crc = new byte[(FinalImage.HeaderSize + outer.PfsSize + cnt.Size) / OuterPfsWriter.BlockSize * 4];
            var headerCrc = Util.Crc32C.Compute(header);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(crc, headerCrc);
            for (int i = 0; i < outer.BlockCount; i++)
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(crc.AsSpan(4 + i * 4), outer.BlockCrcs[i]);
            byte[] block = new byte[OuterPfsWriter.BlockSize];
            output.Position = cntStart;
            for (long at = 0; at < cnt.Size; at += block.Length)
            {
                output.ReadExactly(block);
                System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(crc.AsSpan((int)(4 + (outer.BlockCount + at / block.Length) * 4)), Util.Crc32C.Compute(block));
            }
            byte[]? playGo = cntEntries.FirstOrDefault(e => e.Id == PkgEntryTable.PlayGoChunk)?.Data;
            byte[]? scenario = cntEntries.FirstOrDefault(e => e.Id == 0x3000)?.Data;
            byte[]? meta18 = null;
            if (options.Keys.NapsMeta is not null)
                warnings.Add("The install metadata table (naps_meta_18) is not written by this version; the package omits it.");
            byte[] archive = FinalImage.BuildArchive(options.ContentId, stored.StoredSize, playGo, scenario, crc, meta18);
            output.Position = cntStart + cnt.Size;
            output.Write(archive, 0, archive.Length);

            output.Position = 0;
            output.Write(header, 0, header.Length);
            output.SetLength(cntStart + cnt.Size + archive.Length);
            output.Flush(true);

            Log("Done.");
            output.Dispose();
            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(partial, outputPath);
            return new PkgBuildResult
            {
                OutputPath = Path.GetFullPath(outputPath), PackageSize = new FileInfo(outputPath).Length, InnerFiles = plan.Files.Count,
                MetadataEntries = cnt.EntryCount, Warnings = warnings,
            };
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            throw;
        }
    }

    private static List<CntEntry> BuildEntries(Dictionary<uint, PkgSourceFile> sources, OuterImageInfo outer, InnerImagePlan plan,
        StoredImageResult stored, string contentId, int playGoChunks, List<string> warnings)
    {
        var list = new List<CntEntry>();
        foreach (var (id, file) in sources.OrderBy(kv => kv.Key))
        {
            if (id == PkgEntryTable.ImageDigests) continue;
            list.Add(new CntEntry { Id = id, Length = file.Length, Open = file.Open });
        }

        // the per-block digests of the outer image, each stored byte-reversed
        byte[] digests = (byte[])outer.BlockDigests.Clone();
        for (int at = 0; at + 32 <= digests.Length; at += 32) Array.Reverse(digests, at, 32);
        list.Add(new CntEntry { Id = PkgEntryTable.ImageDigests, Data = digests, Length = digests.Length });

        long mount = FinalImage.HeaderSize + outer.PfsSize;
        uint fileCount = (uint)plan.Files.Count + 3;
        ulong[]? parts = null;
        if (playGoChunks > 1)
        {
            try { parts = PlayGoMultiChunk.SplitExtent((ulong)stored.StoredSize, playGoChunks); }
            catch (ArgumentOutOfRangeException) { warnings.Add("The image is too small for " + playGoChunks + " play-go chunks; one chunk is used."); }
        }
        if (!sources.ContainsKey(PkgEntryTable.PlayGoChunk))
        {
            byte[] chunk = parts is null
                ? FinalImage.PlayGoChunk(contentId, (ulong)stored.StoredSize, (ulong)(mount - stored.StoredSize))
                : PlayGoMultiChunk.Descriptor(contentId, parts, (ulong)(mount - stored.StoredSize));
            list.Add(new CntEntry { Id = PkgEntryTable.PlayGoChunk, Data = chunk, Length = chunk.Length });
        }
        if (!sources.ContainsKey(PkgEntryTable.PlayGoHashTable))
        {
            byte[] table = FinalImage.PlayGoHashTable(fileCount / 2);
            list.Add(new CntEntry { Id = PkgEntryTable.PlayGoHashTable, Data = table, Length = table.Length });
        }
        if (!sources.ContainsKey(PkgEntryTable.PlayGoFicm))
        {
            byte[] ficm = parts is null ? FinalImage.PlayGoFicm(fileCount) : PlayGoMultiChunk.Ficm(PlayGoMultiChunk.FileChunkIds((int)fileCount, parts.Length));
            list.Add(new CntEntry { Id = PkgEntryTable.PlayGoFicm, Data = ficm, Length = ficm.Length });
        }
        if (!sources.ContainsKey(0x1200))
            warnings.Add("The title has no sce_sys/icon0.png; the package will show no icon.");
        return list;
    }

    private static byte[] ReadAll(PkgSourceFile file)
    {
        using Stream s = file.Open();
        byte[] bytes = new byte[file.Length];
        s.ReadExactly(bytes);
        return bytes;
    }

    // executable modules start on a block boundary and carry the module flag: SELF, signed module or ELF magic
    private static bool IsModule(PkgSourceFile file)
    {
        if (file.Length < 4) return false;
        using Stream s = file.Open();
        Span<byte> magic = stackalloc byte[4];
        s.ReadExactly(magic);
        uint m = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(magic);
        return m is 0x1D3D154F or 0xEEF51454 or 0x464C457F;
    }

    internal static (string? Version, bool Upgradable) ReadParam(byte[] param)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(param);
            JsonElement root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            return (Str("contentVersion"), string.Equals(Str("applicationDrmType"), "upgradable", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            throw new InvalidDataException("sce_sys/param.json is not valid JSON.");
        }
    }
}
