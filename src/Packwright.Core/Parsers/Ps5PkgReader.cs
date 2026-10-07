using System.Buffers.Binary;
using Packwright.Core.Models;
using Packwright.Pkg.Reader;

namespace Packwright.Core.Parsers;

/// <summary>
/// Reads the public CNT/FIH metadata layer of a PS5 package with the Packwright package reader and maps it
/// onto Packwright's public package model.
/// </summary>
public sealed class Ps5PkgReader
{
    private const int BlockSize = 0x10000;
    private const int CntHeaderSize = 0x5A0;
    private const int FihHeaderProbeSize = 0x60;

    private static ReadOnlySpan<byte> CntMagic => [0x7F, (byte)'C', (byte)'N', (byte)'T'];
    private static ReadOnlySpan<byte> LihMagic => [0x7F, (byte)'L', (byte)'I', (byte)'H'];
    private static ReadOnlySpan<byte> FihMagic => [0x7F, (byte)'F', (byte)'I', (byte)'H'];

    public bool IsPs5Package(string path)
    {
        try
        {
            using FileStream stream = Open(path);
            Span<byte> magic = stackalloc byte[4];
            if (stream.Length < magic.Length || stream.Read(magic) != magic.Length) return false;
            return magic.SequenceEqual(CntMagic) || magic.SequenceEqual(FihMagic) || magic.SequenceEqual(LihMagic);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    public Ps5PkgSummary Read(string path) => Read(path, null);

    public Ps5PkgSummary Read(string path, string? debugPasscode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        long fileSize = new FileInfo(fullPath).Length;
        if (fileSize < 4) throw new InvalidDataException("The file is too small to be a PS5 package.");

        PkgPackage package;
        try
        {
            package = PkgPackage.Open(fullPath, debugPasscode);
        }
        catch (InvalidDataException)
        {
            // Metadata-less finalized images (CntOffset at/after EOF, e.g. retail disc PKGs) cannot be opened as a
            // full package; describe them from the FIH header alone.
            if (TryReadHeaderOnly(fullPath, fileSize, out Ps5PkgSummary? headerOnly)) return headerOnly!;
            throw;
        }

        Ps5EnginePackageAccess access = Ps5EnginePackageAccess.FromPackage(package, debugPasscode);
        return new Ps5PkgSummary
        {
            Kind = package.IsPatchImage ? Ps5PkgKind.FinalizedPatch
                : package.IsMetadataOnly ? Ps5PkgKind.MetadataContainer
                : package.IsRetail ? Ps5PkgKind.FinalizedRetail
                : IsPatch(package.ContentFlags) ? Ps5PkgKind.FinalizedPatch
                : Ps5PkgKind.FinalizedDebug,
            FileSize = fileSize,
            SignedByte = package.IsMetadataOnly && !package.IsPatchImage ? null : package.SignedByte,
            FormatVersion = package.IsMetadataOnly && !package.IsPatchImage ? null : package.FormatVersion,
            PfsImageOffset = (ulong)package.PfsOffset,
            PfsImageSize = (ulong)package.PfsSize,
            PfsSuperblockOffset = (ulong)package.SuperblockOffset,
            EmbeddedCntOffset = (ulong)package.CntOffset,
            HeaderFlags = package.HeaderFlags,
            SystemEntryCount = package.ScEntryCount,
            BodyOffset = package.BodyOffset,
            BodySize = package.BodySize,
            ContentId = package.ContentId,
            DrmType = package.DrmType,
            ContentType = package.ContentType,
            ContentFlags = package.ContentFlags,
            Entries = package.Entries.Select(MapEntry).ToArray(),
            Segments = package.IsMetadataOnly && !package.IsPatchImage
                ? []
                : package.Segments.Select(s => new Ps5PkgSegment(s.Name, s.Offset, s.Size)).ToArray(),
            NestedPfs = package.IsPatchImage
                ? new Ps5PfsSummary
                {
                    AccessState = Ps5PfsAccessState.UnsupportedLayout,
                    StatusMessage = "This package type is not supported by the file reader yet."
                }
                : BuildNestedPfs(access)
        };
    }

    // Content flags that mark a patch (first, patch-go and subsequent patches).
    private static bool IsPatch(uint contentFlags) => (contentFlags & (0x00100000u | 0x00200000u | 0x40000000u)) != 0;

    /// <summary>Returns the number of files in the package's inner filesystem.</summary>
    public int CountIndexedFiles(string path, string? debugPasscode = null)
    {
        using Ps5EnginePackageAccess access = Ps5EnginePackageAccess.Open(Path.GetFullPath(path), debugPasscode);
        return access.Files.Count();
    }

    public byte[] ReadEntryBytes(string path, Ps5PkgSummary package, Ps5PkgEntry entry, int maximumBytes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(package);
        ArgumentNullException.ThrowIfNull(entry);
        if (maximumBytes < 0) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (entry.IsEncrypted)
            throw new InvalidDataException($"PKG entry 0x{entry.Id:X4} is encrypted (key index {entry.KeyIndex}).");
        if (entry.DataSize > maximumBytes)
            throw new InvalidDataException($"PKG entry 0x{entry.Id:X4} is larger than the {maximumBytes:N0}-byte read limit.");

        using PkgPackage pkg = PkgPackage.Open(Path.GetFullPath(path));
        PkgEntry found = pkg.Entries.FirstOrDefault(candidate => candidate.Id == entry.Id)
            ?? throw new InvalidDataException($"PKG entry 0x{entry.Id:X4} is not present in the CNT metadata.");
        return pkg.ReadEntry(found);
    }

    private static bool TryReadHeaderOnly(string fullPath, long fileSize, out Ps5PkgSummary? summary)
    {
        summary = null;
        if (fileSize < FihHeaderProbeSize) return false;

        byte[] header = new byte[FihHeaderProbeSize];
        using (FileStream stream = Open(fullPath))
        {
            if (stream.Length < header.Length) return false;
            stream.ReadExactly(header);
        }

        if (!header.AsSpan(0, 4).SequenceEqual(FihMagic)) return false;
        ulong cntOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x58, 8));
        // Only the metadata-less layout (no embedded CNT header before EOF) is describable from the
        // FIH alone; a compact CNT that simply does not fit is treated the same way, matching the
        // reference reader's bound check.
        if (cntOffset <= (ulong)Math.Max(0, fileSize - CntHeaderSize)) return false;

        byte signedByte = header[0x05];
        Ps5PkgKind kind = signedByte switch
        {
            0x00 => Ps5PkgKind.FinalizedDebug,
            0x80 => Ps5PkgKind.FinalizedRetail,
            _ => throw new InvalidDataException($"Unknown PS5 FIH signed byte 0x{signedByte:X2}.")
        };
        ushort formatVersion = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(0x06, 2));
        ulong pfsOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x10, 8));
        ulong pfsSize = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x18, 8));
        ulong superblockOffset = BinaryPrimitives.ReadUInt64LittleEndian(header.AsSpan(0x20, 8));

        summary = new Ps5PkgSummary
        {
            Kind = kind,
            FileSize = fileSize,
            SignedByte = signedByte,
            FormatVersion = formatVersion,
            PfsImageOffset = pfsOffset,
            PfsImageSize = pfsSize,
            PfsSuperblockOffset = superblockOffset,
            EmbeddedCntOffset = cntOffset,
            Entries = [],
            NestedPfs = pfsSize > 0
                ? new Ps5PfsSummary
                {
                    AccessState = kind == Ps5PkgKind.FinalizedRetail
                        ? Ps5PfsAccessState.EncryptedKeyRequired
                        : Ps5PfsAccessState.UnsupportedLayout,
                    ImageOffset = (long)pfsOffset,
                    ImageSize = (long)pfsSize,
                    BlockSize = BlockSize,
                    StatusMessage = "The package has no embedded CNT metadata; the PFS image cannot be read on PC without the matching key."
                }
                : null
        };
        return true;
    }

    private static Ps5PfsSummary BuildNestedPfs(Ps5EnginePackageAccess access)
    {
        if (!access.HasInnerImage)
        {
            return new Ps5PfsSummary
            {
                AccessState = Ps5PfsAccessState.NotPresent,
                EngineAccess = access,
                StatusMessage = "The package has no embedded PFS image."
            };
        }

        // The inner image is decoded lazily by the access object. Scanning only parses the metadata, so this
        // summary stays cheap and the real state and file list are materialized when the game is selected.
        return new Ps5PfsSummary
        {
            AccessState = Ps5PfsAccessState.PlaintextIndexed,
            ImageOffset = access.PfsOffset,
            ImageSize = access.PfsSize,
            BlockSize = BlockSize,
            EngineAccess = access,
            StatusMessage = "Inner image, trophies, activities, and files load when the game is selected."
        };
    }

    private static Ps5PkgEntry MapEntry(PkgEntry entry) => new()
    {
        Id = entry.Id,
        NameTableOffset = entry.NameTableOffset,
        Flags1 = entry.Flags1,
        Flags2 = entry.Flags2,
        DataOffset = checked((uint)entry.Offset),
        DataSize = checked((uint)entry.Size),
        Name = entry.Name
    };

    internal static FileStream Open(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.ReadWrite | FileShare.Delete, 64 * 1024, FileOptions.RandomAccess);
}
