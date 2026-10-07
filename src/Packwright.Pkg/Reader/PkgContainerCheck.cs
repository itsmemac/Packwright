// Packwright.Pkg - recomputes the digests that tie a package's parts together and compares them with the stored ones:
// the container header's rollups and seal, the general digests entry, the finalised-image header's digests and the
// per-block digest table of the outer image. Needs no key (outer blocks are decrypted with the public passcode scheme).
#nullable enable
using Packwright.Pkg.Util;
using System.Buffers.Binary;
using System.Security.Cryptography;
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Reader;

public sealed partial class PkgPackage
{
    /// <summary>One result per digest: name, whether it matched, and what was compared.</summary>
    public IReadOnlyList<(string Name, bool Ok, string Detail)> CheckContainerDigests(CancellationToken cancellationToken = default)
    {
        var results = new List<(string, bool, string)>();
        void Result(string name, bool ok, string detail = "") => results.Add((name, ok, detail));
        bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        byte[] Read(long position, long length)
        {
            byte[] b = new byte[length];
            lock (_file) { _file.Position = position; _file.ReadExactly(b); }
            return b;
        }
        byte[] H3(byte[] data) => Util.Sha3.Hash(data);
        PkgEntry? Entry(uint id) => Entries.FirstOrDefault(e => e.Id == id);
        byte[] EntryBytes(uint id, long? length = null)
        {
            PkgEntry e = Entry(id) ?? throw new InvalidDataException($"entry 0x{id:X4} is missing");
            return Read(_cntOffset + e.Offset, length ?? e.Size);
        }

        byte[] header = Read(_cntOffset, 0x1000);
        byte[] fih = Read(0, 0x10000);

        // ---- container header ---------------------------------------------------------------------------------
        Result("Container seal", Same(H3(header[..0xFE0]), header[0xFE0..0x1000]));
        try
        {
            PkgEntry metas = Entry(0x0100)!, digests = Entry(0x0001)!;
            byte[] sc1 = [.. EntryBytes(0x0010), .. EntryBytes(0x0020), .. EntryBytes(0x0080), .. EntryBytes(0x0100), .. EntryBytes(0x0001)];
            byte[] sc2 = [.. EntryBytes(0x0010), .. EntryBytes(0x0020), .. EntryBytes(0x0080), .. EntryBytes(0x0100, ScEntryCount * 32L)];
            Result("System entries rollup 1", Same(H3(sc1), header[0x100..0x120]));
            Result("System entries rollup 2", Same(H3(sc2), header[0x120..0x140]));
            Result("Digest table rollup", Same(H3(EntryBytes(0x0001)), header[0x140..0x160]));
            Result("Body digest", Same(H3(Read(_cntOffset + (long)BodyOffset, (long)BodySize)), header[0x160..0x180]));
            _ = metas; _ = digests;

            uint keyOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x510)), keySize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x514));
            uint mandOffset = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x518)), mandSize = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0x51C));
            byte[] region1 = Read(_cntOffset + keyOffset, keySize), region2 = Read(_cntOffset + mandOffset, mandSize);
            Result("Image key digest", Same(H3(region1), Read(_cntOffset + 0x520, 32)));
            Result("Block digest table digest", Same(H3(region2), Read(_cntOffset + 0x540, 32)));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            Result("Container rollups", false, ex.Message);
        }

        // ---- general digests ------------------------------------------------------------------------------------
        try
        {
            byte[] general = EntryBytes(0x0080);
            uint set = BinaryPrimitives.ReadUInt32BigEndian(general.AsSpan(0x1C));
            byte[] Slot(int i) => general[(0x20 + i * 32)..(0x40 + i * 32)];
            byte[] game = H3(Read(SuperblockOffset, 0x10000));
            if ((set & (1u << 1)) != 0)
                Result("Content digest", Same(H3([.. header.AsSpan(0x40, 0x38), .. game, .. new byte[32]]), Slot(0)));
            if ((set & (1u << 2)) != 0) Result("Game digest", Same(game, Slot(1)));
            if ((set & (1u << 3)) != 0)
                Result("Header digest", Same(H3([.. header.AsSpan(0, 0x40), .. header.AsSpan(0x400, 0x80)]), Slot(2)));
            if ((set & (1u << 4)) != 0) Result("System digest", Same(ConcatDigest(Entries.Select(e => e.Id).Where(PkgSystemMedia.Contains).ToArray()), Slot(3)));
            if ((set & (1u << 6)) != 0) Result("Parameter digest", Same(H3(EntryBytes(0x2000)), Slot(5)));
            if ((set & (1u << 7)) != 0) Result("Play-go digest", Same(ConcatDigest([0x1001, 0x2010, 0x2011, 0x3000]), Slot(6)));
            if ((set & (1u << 12)) != 0) Result("Target digest", Same(game, Slot(11)));

            byte[] ConcatDigest(uint[] ids)
            {
                var parts = new List<byte>();
                foreach (uint id in ids.OrderBy(i => i))
                    if (Entry(id) is not null) parts.AddRange(H3(EntryBytes(id)));
                return H3([.. parts]);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException)
        {
            Result("General digests", false, ex.Message);
        }

        // ---- finalised-image header -----------------------------------------------------------------------------
        if (!IsMetadataOnly)
        {
            byte[] sbDigest = H3(Read(SuperblockOffset, 0x10000));
            Result("Header: outer superblock digest", Same(sbDigest, fih[0x30..0x50]) && Same(sbDigest, fih[0x70..0x90]) && Same(sbDigest, fih[0xD0..0xF0]));
            Result("Header: sealed by the container", Same(H3(fih), header[0x460..0x480]));
            Result("Header: superblock digest in the container", Same(sbDigest, header[0x440..0x460]));
            try
            {
                EnsureInner();
                byte[] layout = new byte[_layoutFile!.size];
                _layoutFile.GetView().Read(0, layout, 0, layout.Length);
                Result("Header: layout digest", Same(H3(layout), fih[0xB0..0xD0]));
                Result("Header: layout size", BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0xA8)) == (ulong)layout.Length);
                Result("Header: inner image size", BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0xA0)) == (ulong)InnerImageSize || BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0xA0)) == BinaryPrimitives.ReadUInt32LittleEndian(fih.AsSpan(0x90)) * 0x10000UL,
                    $"{BinaryPrimitives.ReadUInt64LittleEndian(fih.AsSpan(0xA0)):X} vs {InnerImageSize:X}");

                // per-block digest table of the outer image (each digest is stored byte-reversed)
                PkgEntry? table = Entry(0x040A);
                if (table is not null)
                {
                    byte[] stored = EntryBytes(0x040A);
                    long blocks = PfsSize / 0x10000;
                    int bad = 0;
                    IMemoryReader reader = (IMemoryReader?)_xtsReader ?? new FileReader(_file, PfsOffset);
                    byte[] block = new byte[0x10000];
                    for (long i = 0; i < blocks && i * 32 + 32 <= stored.Length; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        reader.Read(i * 0x10000, block, 0, block.Length);
                        byte[] want = H3(block);
                        Array.Reverse(want);
                        if (!want.AsSpan().SequenceEqual(stored.AsSpan((int)i * 32, 32))) bad++;
                    }
                    Result("Block digest table", bad == 0 && stored.Length == blocks * 32, $"{bad} of {blocks} blocks differ");
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or PkgNotSupportedException)
            {
                Result("Image digests", false, ex.Message);
            }
        }
        return results;
    }
}

/// <summary>The entries that feed the "system" digest: the artwork and sound files.</summary>
public static class PkgSystemMedia
{
    public static bool Contains(uint id) =>
        id is 0x1006 or 0x100D or 0x2040 or 0x2060 || (id >= 0x1200 && id <= 0x125F) || (id >= 0x1280 && id <= 0x12DF);
}
