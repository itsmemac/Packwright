// Packwright.Pkg - builds the layout file (naps_pkg_layout.dat) of a package.
//
// The layout describes how the stored inner image expands to the logical image: a list of "spans" (a logical range
// and where its bytes are stored) plus the file boundaries and lookup tables around them. The record formats follow
// the LibProsperoPKG 1.2 reader (GPL-3.0-or-later, SvenGDK); the section geometry and the rules for run-base records
// were worked out from real packages and are checked by reading what is built back with the package reader.
#nullable enable
using System.Buffers.Binary;

namespace Packwright.Pkg.Writer;

/// <summary>One span of the stored inner image: a logical range of up to 256 KB and where its bytes are stored.</summary>
public readonly record struct LayoutSpan(long Logical, int Length, long Stored, int StoredLength, int EvenLength, byte Even, byte Odd)
{
    /// <summary>A span whose bytes are stored as they are (stored length equals logical length).</summary>
    public static LayoutSpan Raw(long logical, int length, long stored) =>
        new(logical, length, stored, length, Math.Min(length, 0x20000), 1, (byte)(length > 0x20000 ? 1 : 0));
}

public static class NapsLayoutBuilder
{
    public const int SpanSize = 0x40000;
    private const int RunInterval = 16;
    private const long Mask18 = 0x3FFFF;

    private readonly record struct Record(bool IsRun, bool IsTerminal, uint CoffStartMod, uint CoffEndMod, uint Uoff,
        uint ClenEvenMinus1, byte Even, byte Odd, uint Tweak, uint Coff256K);

    /// <summary>
    /// Builds the layout. <paramref name="fileStarts"/> are the logical start offsets of every data slot (files and any
    /// holes), in data order; the data region ends at <paramref name="dataEnd"/>, the filesystem metadata starts at
    /// <paramref name="metadataStart"/> and the whole image ends at <paramref name="mountEnd"/>. The spans must be in
    /// logical order and cover the image without gaps.
    /// </summary>
    public static byte[] Build(IReadOnlyList<long> fileStarts, long dataEnd, long metadataStart, long mountEnd,
        IReadOnlyList<LayoutSpan> spans, long storedSize)
    {
        if (spans.Count == 0) throw new ArgumentException("A layout needs at least one span.", nameof(spans));

        // ---- block records -------------------------------------------------------------------------------
        var records = new List<Record>(spans.Count + spans.Count / RunInterval + 4);
        var firstOfUBlock = new Dictionary<int, int>();
        long previousEnd = -1;
        for (int i = 0; i < spans.Count; i++)
        {
            LayoutSpan s = spans[i];
            if (s.Length <= 0 || s.Length > SpanSize || s.StoredLength <= 0)
                throw new ArgumentException($"Span {i} has an invalid size.", nameof(spans));
            bool continuous = previousEnd == s.Stored;
            bool previousIsRun = records.Count > 0 && records[^1].IsRun;
            // A run base starts every 16th record and wherever the stored position jumps; it tells the reader the
            // stored position of the span that follows. Its end field closes the span before it.
            if (!previousIsRun && (!continuous || records.Count % RunInterval == 0))
                records.Add(new Record(true, false, 0, previousEnd < 0 ? 0u : (uint)(previousEnd & Mask18), 0, 0, 0, 0,
                    (uint)(s.Stored >> 16), (uint)(s.Stored >> 18)));
            int ublock = (int)(s.Logical >> 18);
            firstOfUBlock.TryAdd(ublock, records.Count);
            records.Add(new Record(false, false, (uint)(s.Stored & Mask18), 0, (uint)(s.Logical & Mask18),
                (uint)(s.EvenLength - 1), s.Even, s.Odd, 0, 0));
            previousEnd = s.Stored + s.StoredLength;
        }
        // The terminator gives the last span its end offsets.
        if (records.Count % RunInterval == 0)
            records.Add(new Record(true, false, 0, (uint)(previousEnd & Mask18), 0, 0, 0, 0, 0, (uint)(previousEnd >> 18)));
        int terminalIndex = records.Count;
        records.Add(new Record(false, true, (uint)(previousEnd & Mask18), 0, (uint)(mountEnd & Mask18), 0, 0, 0, 0, 0));

        // ---- sizes ----------------------------------------------------------------------------------------
        int numUBlocks = (int)((mountEnd + SpanSize - 1) >> 18);
        int fileSlots = fileStarts.Count + 3;
        int u2cGroups = (numUBlocks + 8) >> 3;
        int outerBlocks = (int)((storedSize + 0xFFFF) >> 16);

        // ---- lookup table: first record of every logical block -------------------------------------------
        var first = new int[numUBlocks + 1];
        for (int u = numUBlocks; u >= 0; u--)
            first[u] = u == numUBlocks ? terminalIndex : firstOfUBlock.TryGetValue(u, out int f) ? f : first[u + 1];

        // ---- assemble -------------------------------------------------------------------------------------
        long pos = 16 + 8L * outerBlocks + 6L * fileSlots;
        long u2cStart = pos;
        pos += 10L * u2cGroups;
        long recordStart = (pos + 7) & ~7L;
        long total = ((recordStart + 9L * records.Count + 7) & ~7L) + 8;
        byte[] layout = new byte[total];

        ulong word0 = (ulong)(fileSlots - 1) | (2UL << 24) | ((ulong)numUBlocks << 32);
        ulong word1 = (ulong)outerBlocks | ((ulong)(records.Count - 2) << 24);
        BinaryPrimitives.WriteUInt64LittleEndian(layout, word0);
        BinaryPrimitives.WriteUInt64LittleEndian(layout.AsSpan(8), word1);
        // The outer-block records (8 bytes each) are zero in debug packages.

        long filePos = 16 + 8L * outerBlocks;
        void WriteFile(long offset, byte type)
        {
            for (int k = 0; k < 5; k++) layout[filePos + k] = (byte)(offset >> (8 * k));
            layout[filePos + 5] = type;
            filePos += 6;
        }
        foreach (long start in fileStarts) WriteFile(start, 0);
        WriteFile(dataEnd, 0);
        WriteFile(metadataStart, 0);
        WriteFile(mountEnd, 0x40);

        for (int g = 0; g < u2cGroups; g++)
        {
            int baseIndex = first[Math.Min(g * 8, numUBlocks)];
            long at = u2cStart + 10L * g;
            layout[at] = (byte)baseIndex; layout[at + 1] = (byte)(baseIndex >> 8); layout[at + 2] = (byte)(baseIndex >> 16);
            for (int j = 1; j < 8; j++)
            {
                int index = first[Math.Min(g * 8 + j, numUBlocks)];
                int delta = index - baseIndex;
                if (delta is < 0 or > 255)
                    throw new NotSupportedException("Too many small files share one block range for the layout table; this package shape is not supported yet.");
                layout[at + 2 + j] = (byte)delta;
            }
        }

        for (int i = 0; i < records.Count; i++)
            WriteRecord(layout.AsSpan((int)(recordStart + 9L * i), 9), records[i]);
        return layout;
    }

    // The 72-bit record: bit 18 tells a run base from a normal block.
    private static void WriteRecord(Span<byte> dst, Record r)
    {
        UInt128 v;
        if (r.IsRun)
            v = r.CoffEndMod | (UInt128.One << 18) | ((UInt128)r.Tweak << 20) | ((UInt128)r.Coff256K << 50);
        else
            v = r.CoffStartMod | ((UInt128)(r.IsTerminal ? 1 : 0) << 19) | ((UInt128)r.Uoff << 20)
                | ((UInt128)r.ClenEvenMinus1 << 38) | ((UInt128)r.Even << 55) | ((UInt128)r.Odd << 58);
        for (int i = 0; i < 9; i++) dst[i] = (byte)(v >> (8 * i));
    }
}
