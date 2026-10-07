// Packwright.Pkg - the play-go chunk descriptor for a title split into several chunks. The layout follows the
// descriptors built by LibProsperoPKG (GPL-3.0-or-later, SvenGDK).
#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace Packwright.Pkg.Writer;

public static class PlayGoMultiChunk
{
    /// <summary>Splits a main extent into <paramref name="chunks"/> parts of whole 64 KB blocks; the last part takes the remainder.</summary>
    public static ulong[] SplitExtent(ulong size, int chunks)
    {
        ulong blocks = size / 65536, rest = size % 65536;
        if (chunks < 2 || chunks > 64) throw new ArgumentOutOfRangeException(nameof(chunks), "Chunks must be 2..64.");
        if (blocks < (ulong)chunks) throw new ArgumentOutOfRangeException(nameof(chunks), "The image is too small for that many chunks.");
        var parts = new ulong[chunks];
        for (int i = 0; i < chunks; i++) parts[i] = (blocks / (ulong)chunks + ((ulong)i < blocks % (ulong)chunks ? 1UL : 0)) * 65536;
        parts[^1] += rest;
        return parts;
    }

    /// <summary>Chunk number of every file, spread evenly in file order.</summary>
    public static byte[] FileChunkIds(int fileCount, int chunks)
    {
        var ids = new byte[fileCount];
        for (int i = 0; i < fileCount; i++) ids[i] = (byte)Math.Min(chunks - 1, (long)i * chunks / fileCount);
        return ids;
    }

    public static byte[] Ficm(IReadOnlyList<byte> chunkIds)
    {
        byte[] d = FinalImage.PlayGoFicm((uint)chunkIds.Count * 2);
        for (int i = 0; i < chunkIds.Count; i++) d[16 + i * 2] = chunkIds[i];
        return d;
    }

    public static byte[] Descriptor(string contentId, IReadOnlyList<ulong> mainSizes, ulong tailSize)
    {
        int n = mainSizes.Count;
        if (n < 2 || n > 64 || mainSizes.Any(s => s == 0)) throw new ArgumentException("Multi-chunk needs 2..64 non-empty chunks.", nameof(mainSizes));
        static int Align16(int v) => (v + 15) & -16;
        string[] labels = Enumerable.Range(0, n).Select(i => $"Chunk #{i}").ToArray();
        int[] labelAt = new int[n];
        int labelBytes = 0;
        for (int i = 0; i < n; i++) { labelAt[i] = labelBytes; labelBytes += labels[i].Length + 1; }

        int attrs = 256, attrBytes = n * 32;
        int mchunks = attrs + attrBytes, mchunkBytes = (n + 1) * 4;
        int labelsAt = Align16(mchunks + mchunkBytes);
        int extentsAt = Align16(labelsAt + labelBytes), extentBytes = (n + 1) * 16;
        int innerAt = extentsAt + extentBytes;
        int scenarioAt = innerAt + 32, scenarioBytes = n * 2;
        int scenarioLabelAt = Align16(scenarioAt + scenarioBytes);
        int total = Align16(scenarioLabelAt + "Scenario #0".Length + 1);

        byte[] d = new byte[total];
        Span<byte> s = d;
        Encoding.ASCII.GetBytes("plgx").CopyTo(s);
        BinaryPrimitives.WriteUInt16LittleEndian(s[4..], 0x1000);
        BinaryPrimitives.WriteUInt16LittleEndian(s[8..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(s[10..], (ushort)n);
        BinaryPrimitives.WriteUInt16LittleEndian(s[14..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(s[16..], (uint)total);
        BinaryPrimitives.WriteUInt16LittleEndian(s[22..], 1);
        s[30] = 0x85; s[32] = (byte)(n + 1); s[36] = 1; s[48] = 0x11;
        BinaryPrimitives.WriteUInt64LittleEndian(s[56..], ulong.MaxValue);
        Encoding.ASCII.GetBytes(contentId).CopyTo(s[0x40..]);
        void Ptr(int at, int offset, int size)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(at + 4), (uint)size);
        }
        Ptr(192, attrs, attrBytes); Ptr(200, mchunks, mchunkBytes); Ptr(208, labelsAt, labelBytes);
        Ptr(216, extentsAt, extentBytes); Ptr(224, innerAt, 32); Ptr(232, scenarioAt, scenarioBytes);
        Ptr(240, scenarioLabelAt, "Scenario #0".Length + 1);

        int mc = 0;
        for (int i = 0; i < n; i++)
        {
            int a = attrs + i * 32;
            s[a] = 0x80; s[a + 2] = 3;
            BinaryPrimitives.WriteUInt16LittleEndian(s[(a + 4)..], (ushort)(i == 0 ? 2 : 1));
            s[a + 8] = 0x11;
            BinaryPrimitives.WriteUInt64LittleEndian(s[(a + 16)..], ulong.MaxValue);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(a + 24)..], (uint)mc);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(a + 28)..], (uint)labelAt[i]);
            BinaryPrimitives.WriteUInt32LittleEndian(s[(mchunks + mc)..], (uint)i);
            mc += 4;
            if (i == 0) { BinaryPrimitives.WriteUInt32LittleEndian(s[(mchunks + mc)..], (uint)n); mc += 4; }
            Encoding.ASCII.GetBytes(labels[i]).CopyTo(s[(labelsAt + labelAt[i])..]);
        }
        ulong start = 0;
        for (int i = 0; i < n; i++)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(s[(extentsAt + i * 16)..], start);
            BinaryPrimitives.WriteUInt64LittleEndian(s[(extentsAt + i * 16 + 8)..], mainSizes[i]);
            start += mainSizes[i];
        }
        BinaryPrimitives.WriteUInt64LittleEndian(s[(extentsAt + n * 16)..], start);
        BinaryPrimitives.WriteUInt64LittleEndian(s[(extentsAt + n * 16 + 8)..], tailSize);
        BinaryPrimitives.WriteUInt64LittleEndian(s[innerAt..], 0x21);
        s[innerAt + 20] = 1; s[innerAt + 22] = (byte)n;
        for (int i = 0; i < n; i++) BinaryPrimitives.WriteUInt16LittleEndian(s[(scenarioAt + i * 2)..], (ushort)i);
        Encoding.ASCII.GetBytes("Scenario #0").CopyTo(s[scenarioLabelAt..]);
        return d;
    }
}
