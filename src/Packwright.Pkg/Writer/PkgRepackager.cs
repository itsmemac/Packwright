// Packwright.Pkg - rewrites the metadata of a finished package without touching its inner image.
//
// Editing a title's details (name, version, icon, DRM and so on) only changes entries of the metadata container. The
// outer image, with the inner image inside it, stays byte for byte as it is, so the edit costs one pass over the file
// instead of a rebuild. The container, the finalised-image header and the install metadata archive are written again so
// that every digest and seal matches the new content.
//
// This works while the content id and the passcode stay the same: the outer image is encrypted with a key derived from
// them. A change of content id needs a full rebuild.
#nullable enable
using System.Buffers.Binary;
using Packwright.Pkg.Reader;
using Packwright.Pkg.Util;

namespace Packwright.Pkg.Writer;

public static class PkgRepackager
{
    /// <summary>
    /// Writes a copy of <paramref name="source"/> in which the given metadata entries are replaced. Entries not named keep
    /// their content. <paramref name="replacements"/> maps entry ids (for example 0x2000 for param.json) to the new bytes.
    /// </summary>
    public static PkgBuildResult ReplaceEntries(PkgPackage source, string outputPath, IReadOnlyDictionary<uint, byte[]> replacements,
        PkgKeySet keys, string passcode, Action<string>? log = null, IProgress<PkgBuildProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (source.IsMetadataOnly || source.IsRetail)
            throw new NotSupportedException("Only a debug package with an inner image can be rewritten.");
        if (passcode.Length != 32) throw new ArgumentException("The passcode must be exactly 32 characters.", nameof(passcode));
        var warnings = new List<string>();

        // ---- entries: everything the source has, with the replacements applied ----------------------------------------
        var system = new HashSet<uint> { 0x0001, 0x0010, 0x0020, 0x0080, 0x0100, 0x0200 };
        var entries = new List<CntEntry>();
        var seen = new HashSet<uint>();
        foreach (PkgEntry e in source.Entries)
        {
            if (system.Contains(e.Id)) continue;
            seen.Add(e.Id);
            byte[] data = replacements.TryGetValue(e.Id, out byte[]? replacement)
                ? replacement
                : e.IsEncrypted ? source.ReadEntry(e, passcode) : source.ReadEntry(e);
            entries.Add(new CntEntry
            {
                Id = e.Id, Data = data, Length = data.Length,
                NameOverride = e.Name, Flags1 = e.Flags1, Flags2 = e.Flags2,
            });
        }
        foreach (var (id, bytes) in replacements)
            if (seen.Add(id)) entries.Add(new CntEntry { Id = id, Data = bytes, Length = bytes.Length });
        byte[] param = entries.FirstOrDefault(e => e.Id == PkgEntryTable.ParamJson)?.Data
            ?? throw new InvalidDataException("The package has no param.json.");
        (string? contentVersion, bool upgradable) = PkgBuilder.ReadParam(param);

        // ---- what describes the outer image, taken from the source ------------------------------------------------------
        long pfsSize = source.PfsSize;
        byte[] superblock = new byte[OuterPfsWriter.BlockSize];
        byte[] fih = new byte[FinalImage.HeaderSize];
        using (var read = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            read.ReadExactly(fih);
            read.Position = source.SuperblockOffset;
            read.ReadExactly(superblock);
        }
        byte[] seed = superblock.AsSpan(0x370, 16).ToArray();
        long storedSize = source.StoredImageSize;
        uint version = FinalImage.ContentVersionBcd(contentVersion);
        if (version != 0) BinaryPrimitives.WriteUInt32LittleEndian(fih.AsSpan(0x9C), version);

        string partial = outputPath + ".packwright-part";
        try
        {
            using var output = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 20);
            output.SetLength(FinalImage.HeaderSize);

            // ---- the outer image, copied as it is -------------------------------------------------------------------------
            long blocks = pfsSize / OuterPfsWriter.BlockSize;
            var crcs = new List<uint>((int)blocks + 1) { Crc32C.Compute(fih) };
            output.Position = FinalImage.HeaderSize;
            using (var read = new FileStream(source.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.SequentialScan))
            {
                read.Position = source.PfsOffset;
                byte[] block = new byte[OuterPfsWriter.BlockSize];
                for (long i = 0; i < blocks; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    read.ReadExactly(block);
                    output.Write(block, 0, block.Length);
                    crcs.Add(Crc32C.Compute(block));
                    if ((i & 255) == 0) progress?.Report(new PkgBuildProgress("Outer PFS", i * OuterPfsWriter.BlockSize, pfsSize));
                }
            }
            progress?.Report(new PkgBuildProgress("Outer PFS", pfsSize, pfsSize));

            // ---- container, header, archive -----------------------------------------------------------------------------
            progress?.Report(new PkgBuildProgress("CNT", 0, 0));
            long cntStart = FinalImage.HeaderSize + pfsSize;
            CntResult cnt = CntBuilder.Build(output, cntStart, new CntParameters
            {
                ContentId = source.ContentId, Passcode = passcode, Keys = keys, Entries = entries, PfsSize = pfsSize,
                ImageSeed = seed, SuperblockDigest = Sha3.Hash(superblock), FihDigest = Sha3.Hash(fih), Upgradable = upgradable,
            }, cancellationToken);

            progress?.Report(new PkgBuildProgress("Finalize", 0, 0));
            byte[] chunkCrc = new byte[(FinalImage.HeaderSize + pfsSize + cnt.Size) / OuterPfsWriter.BlockSize * 4];
            for (int i = 0; i < crcs.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(chunkCrc.AsSpan(i * 4), crcs[i]);
            byte[] cntBlock = new byte[OuterPfsWriter.BlockSize];
            output.Position = cntStart;
            for (long at = 0, index = crcs.Count; at < cnt.Size; at += cntBlock.Length, index++)
            {
                output.ReadExactly(cntBlock);
                BinaryPrimitives.WriteUInt32LittleEndian(chunkCrc.AsSpan((int)(index * 4)), Crc32C.Compute(cntBlock));
            }
            byte[]? playGo = entries.FirstOrDefault(e => e.Id == PkgEntryTable.PlayGoChunk)?.Data;
            byte[]? scenario = entries.FirstOrDefault(e => e.Id == 0x3000)?.Data;
            byte[] archive = FinalImage.BuildArchive(source.ContentId, storedSize, playGo, scenario, chunkCrc, null);
            output.Position = cntStart + cnt.Size;
            output.Write(archive, 0, archive.Length);
            output.Position = 0;
            output.Write(fih, 0, fih.Length);
            output.SetLength(cntStart + cnt.Size + archive.Length);
            output.Flush(true);
            output.Dispose();

            if (File.Exists(outputPath)) File.Delete(outputPath);
            File.Move(partial, outputPath);
            log?.Invoke("Done.");
            return new PkgBuildResult
            {
                OutputPath = Path.GetFullPath(outputPath), PackageSize = new FileInfo(outputPath).Length,
                InnerFiles = source.Files.Count, MetadataEntries = cnt.EntryCount, Warnings = warnings,
            };
        }
        catch
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch (IOException) { }
            throw;
        }
    }
}
