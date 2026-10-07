// Packwright.Pkg - the parts that wrap a package: the 64 KB header at the start of the file (the "finalised image"
// header), the install metadata archive at the end, and the stand-in files the package carries when the source has none.
//
// Layout of the file: [header 0x10000][outer image][metadata container][install metadata archive]. The header and the
// archive follow the packages built by LibProsperoPKG (GPL-3.0-or-later, SvenGDK) and were checked against real ones.
#nullable enable
using Packwright.Pkg.Util;
using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Packwright.Pkg.Writer;

public static class FinalImage
{
    public const int HeaderSize = 0x10000;

    /// <summary>The finalised-image header block. Offsets of the outer image, the container and the digests that tie them together.</summary>
    public static byte[] BuildHeader(OuterImageInfo outer, byte[] layout, long logicalSize, long metadataOffset, int contentInodes,
        int appFileCount, uint contentVersion)
    {
        byte[] h = new byte[HeaderSize];
        void U32(int at, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(at), v);
        void U64(int at, ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(h.AsSpan(at), v);

        h[0] = 0x7F; h[1] = (byte)'F'; h[2] = (byte)'I'; h[3] = (byte)'H';
        h[4] = 1; h[5] = 0; h[6] = 3;
        U32(0x08, 1);
        U64(0x10, HeaderSize);
        U64(0x18, (ulong)outer.PfsSize);
        U64(0x20, (ulong)(HeaderSize + (long)outer.SuperblockIndex * OuterPfsWriter.BlockSize));
        U64(0x28, HeaderSize);
        byte[] sb = Sha3.Hash(outer.Superblock);
        sb.CopyTo(h, 0x30);
        sb.CopyTo(h, 0x70);
        sb.CopyTo(h, 0xD0);
        U64(0x50, (ulong)(metadataOffset / OuterPfsWriter.BlockSize));
        U64(0x58, (ulong)(HeaderSize + outer.PfsSize));
        U64(0x60, HeaderSize);
        U64(0x68, 0x800000000000UL);
        U32(0x90, (uint)outer.ImageBlocks);
        U32(0x94, (uint)contentInodes);
        U32(0x98, (uint)contentInodes);
        U32(0x9C, contentVersion);
        U64(0xA0, (ulong)logicalSize);
        U64(0xA8, (ulong)layout.Length);
        Sha3.Hash(layout).CopyTo(h, 0xB0);
        U32(0xF0, (uint)appFileCount);
        U32(0xF8, 2);
        return h;
    }

    /// <summary>The content version of param.json ("MM.mmm.ppp") packed as BCD digits: major in the top byte, minor and patch below.</summary>
    public static uint ContentVersionBcd(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return 0;
        string[] parts = version.Split('.');
        if (!Bcd(parts[0], 2, out uint major)) return 0;
        uint minor = parts.Length > 1 && Bcd(parts[1], 3, out uint m) ? m : 0;
        uint patch = parts.Length > 2 && Bcd(parts[2], 3, out uint p) ? p : 0;
        return (major << 24) | (minor << 12) | patch;

        static bool Bcd(string field, int digits, out uint value)
        {
            value = 0;
            string s = field.Trim();
            if (s.Length == 0 || s.Length > digits) return false;
            foreach (char c in s)
            {
                if (c is < '0' or > '9') return false;
                value = (value << 4) | (uint)(c - '0');
            }
            return true;
        }
    }

    /// <summary>
    /// The install metadata archive appended after the container: the small descriptor records, the play-go files and the
    /// per-block checksum table of the whole image. Members are stored without compression.
    /// </summary>
    public static byte[] BuildArchive(string contentId, long storedImageSize, byte[]? playGoChunk, byte[]? playGoScenario,
        byte[] chunkChecksums, byte[]? napsMeta18)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Member(string name, byte[] data)
            {
                ZipArchiveEntry e = zip.CreateEntry(name, CompressionLevel.NoCompression);
                e.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
                using Stream s = e.Open();
                s.Write(data, 0, data.Length);
            }

            if (napsMeta18 is { Length: > 0 }) Member("common/etc/naps_meta_18.dat", napsMeta18);
            byte[] descriptor = NapsDescriptor(storedImageSize);
            foreach (int id in new[] { 300, 301, 302, 308 }) Member($"common/etc/naps_meta_{id}.dat", descriptor);
            if (playGoChunk is { Length: > 0 }) Member("common/etc/playgo-chunk.dat", playGoChunk);
            Member($"config/{contentId}/playgo-chunk.crc", chunkChecksums);
            if (playGoScenario is { Length: > 0 }) Member($"config/{contentId}/playgo-scenario.json", playGoScenario);
        }
        return ms.ToArray();
    }

    // six little-endian words: the size of the stored image without its two trailing blocks, a constant, the same size
    // again and the size of those two blocks
    private static byte[] NapsDescriptor(long storedImageSize)
    {
        byte[] d = new byte[48];
        ulong data = (ulong)(storedImageSize - 0x20000);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x10), data);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x18), 0x3E9);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x20), data);
        BinaryPrimitives.WriteUInt64LittleEndian(d.AsSpan(0x28), 0x20000);
        return d;
    }

    // ---- play-go files for sources that do not carry them ----------------------------------------------------

    /// <summary>The play-go chunk descriptor for a single chunk covering the stored image and a tail covering the rest.</summary>
    public static byte[] PlayGoChunk(string contentId, ulong imageSize, ulong tailSize)
    {
        byte[] d = new byte[0x1A0];
        Span<byte> s = d;
        Encoding.ASCII.GetBytes("plgx").CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], 0x1A0);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], 1);
        s[30] = 0x85; s[32] = 2; s[36] = 1; s[48] = 0x11;
        for (int i = 56; i < 64; i++) s[i] = 0xFF;
        Encoding.ASCII.GetBytes(contentId).CopyTo(s[0x40..]);
        void Ptr(int at, uint offset, uint size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), offset);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at + 4), size);
        }
        Ptr(0xC0, 0x100, 0x20); Ptr(0xC8, 0x120, 0x08); Ptr(0xD0, 0x130, 9); Ptr(0xD8, 0x140, 0x20);
        Ptr(0xE0, 0x160, 0x20); Ptr(0xE8, 0x180, 2); Ptr(0xF0, 0x190, 12);
        s[0x100] = 0x80; s[0x102] = 3; s[0x104] = 2; s[0x108] = 0x11;
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x110..], ulong.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(s[0x124..], 1);
        Encoding.ASCII.GetBytes("Chunk #0").CopyTo(s[0x130..]);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x148..], imageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x150..], imageSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x158..], tailSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s[0x160..], 0x21);
        s[0x174] = 1; s[0x176] = 1;
        Encoding.ASCII.GetBytes("Scenario #0").CopyTo(s[0x190..]);
        return d;
    }

    public static byte[] PlayGoFicm(uint fileCount)
    {
        byte[] d = new byte[16 + fileCount];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 16);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), fileCount);
        return d;
    }

    private static readonly byte[] HashTablePrefix =
        [0x51, 0x4F, 0xA2, 0x26, 0xAB, 0x8A, 0xCA, 0x92, 0x4D, 0xC4, 0x1B, 0xA4, 0x61, 0xB7, 0xBB, 0x09];

    private static readonly byte[][] HashTableEntries =
    [
        [0x8E, 0x54, 0xCB, 0x4D, 0x4A, 0xF6, 0x30, 0x0E], [0xF2, 0xBF, 0xF6, 0x27, 0xB9, 0x8F, 0x88, 0x53],
        [0xCB, 0xDC, 0xC6, 0x3E, 0xEC, 0xB3, 0xC4, 0xAE], [0x0B, 0xF4, 0xE9, 0xC5, 0xDA, 0xF8, 0xC9, 0xAE],
        [0x4C, 0xF7, 0x0C, 0x08, 0x17, 0x4D, 0xCB, 0xD3],
    ];

    public static byte[] PlayGoHashTable(uint chunkCount)
    {
        int table = (int)chunkCount * 8;
        byte[] d = new byte[0x38 + table];
        BinaryPrimitives.WriteUInt32LittleEndian(d, 1);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 0x08000000);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 0x38);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)table);
        d[0x18] = 0x7F; d[0x19] = (byte)'F'; d[0x1A] = (byte)'L'; d[0x1B] = (byte)'T';
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(0x24), chunkCount);
        HashTablePrefix.CopyTo(d, 0x28);
        for (int i = 0; i < chunkCount; i++) HashTableEntries[Math.Min(i, HashTableEntries.Length - 1)].CopyTo(d, 0x38 + i * 8);
        return d;
    }
}
