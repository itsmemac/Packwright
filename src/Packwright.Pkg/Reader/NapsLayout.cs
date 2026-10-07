// Packwright.Pkg - the "NAPS" layout file (naps_pkg_layout.dat) of a PS5 package.
//
// The layout says how the stored inner image (pfs_image.dat) expands to the logical inner filesystem image:
// which logical ranges belong to which file, and for every compressed block where its bytes are stored and
// how they were compressed. The record formats follow the LibProsperoPKG 1.2 reader (GPL-3.0-or-later,
// SvenGDK); the section geometry and the span plan were verified against real packages.
#nullable enable

namespace Packwright.Pkg.Reader;

/// <summary>One 9-byte block record of the layout (a "normal" block, a run base or a terminator).</summary>
internal readonly record struct NapsBlockInfo
{
    public bool IsRunBase { get; init; }

    // Normal block:
    public uint CoffsetStartMod256K { get; init; }
    public bool IsTerminal { get; init; }
    public uint UoffsetStart { get; init; }
    public uint ClenEvenMinus1 { get; init; }
    public byte Even { get; init; }
    public byte Odd { get; init; }
    public byte KdePredictor { get; init; }
    public byte ShuffleIdx { get; init; }

    // Run base:
    public uint CoffsetEndMod256K { get; init; }
    public uint TweakIdxStart { get; init; }
    public uint CoffsetStart256K { get; init; }
}

internal readonly record struct NapsFileRecord(byte Type, long UncompressedOffset);

internal sealed class NapsLayout
{
    private const int HeaderSize = 16;

    public int NumFiles { get; }
    public int NumUBlocks { get; }
    public int NumShufflePatterns { get; }
    public IReadOnlyList<NapsFileRecord> Files { get; }
    public IReadOnlyList<(uint Base, byte[] Deltas)> U2c { get; }
    public IReadOnlyList<NapsBlockInfo> Blocks { get; }
    public IReadOnlyList<byte[]> ShufflePatterns { get; }

    public int UBlockCount => NumUBlocks + 1;
    /// <summary>The size of the logical inner image: the offset recorded after the last file.</summary>
    public long UncompressedSize => Files[^1].UncompressedOffset;

    public NapsLayout(ReadOnlySpan<byte> blob)
    {
        if (blob.Length < HeaderSize) throw new InvalidDataException("The package layout file is too small.");

        // Header: two little-endian words with counts packed into bit fields.
        ulong w0 = ReadU64(blob, 0), w1 = ReadU64(blob, 8);
        NumFiles = (int)(w0 & 0xFFFFFF) + 1;
        NumShufflePatterns = (int)((w0 >> 28) & 0xF);
        NumUBlocks = (int)((w0 >> 32) & 0x7FFFFF);
        int numOuterBlocks = (int)(w1 & 0xFFFFFF);
        int numBlocks = (int)((w1 >> 24) & 0xFFFFFF) + 2;

        // Sections in order: header, outer-block records (8 bytes), shuffle patterns (8), file records (6),
        // u2c records (10, one per 8 blocks), padding to 8 bytes, then block records (9).
        long pos = HeaderSize + 8L * numOuterBlocks;
        long shuffleStart = pos;
        pos += 8L * NumShufflePatterns;
        long fileStart = pos;
        pos += 6L * NumFiles;
        long u2cStart = pos;
        int u2cCount = (NumUBlocks + 8) >> 3;
        pos += 10L * u2cCount;
        long blockStart = (pos + 7) & ~7L;
        if (blockStart + 9L * numBlocks > blob.Length)
            throw new InvalidDataException("The package layout file is shorter than its header says.");

        var shuffle = new List<byte[]>(NumShufflePatterns);
        for (int i = 0; i < NumShufflePatterns; i++)
            shuffle.Add(blob.Slice((int)(shuffleStart + 8L * i), 8).ToArray());
        ShufflePatterns = shuffle;

        var files = new NapsFileRecord[NumFiles];
        for (int i = 0; i < files.Length; i++)
        {
            ReadOnlySpan<byte> r = blob.Slice((int)(fileStart + 6L * i), 6);
            long offset = 0;
            for (int k = 0; k < 5; k++) offset |= (long)r[k] << (8 * k);
            files[i] = new NapsFileRecord(r[5], offset);
        }
        Files = files;

        var u2c = new (uint, byte[])[u2cCount];
        for (int i = 0; i < u2c.Length; i++)
        {
            ReadOnlySpan<byte> r = blob.Slice((int)(u2cStart + 10L * i), 10);
            u2c[i] = ((uint)(r[0] | (r[1] << 8) | (r[2] << 16)), r.Slice(3, 7).ToArray());
        }
        U2c = u2c;

        var blocks = new NapsBlockInfo[numBlocks];
        for (int i = 0; i < blocks.Length; i++)
            blocks[i] = DecodeBlock(blob.Slice((int)(blockStart + 9L * i), 9));
        Blocks = blocks;
    }

    // The 72-bit record: bit 18 tells a run base from a normal block.
    private static NapsBlockInfo DecodeBlock(ReadOnlySpan<byte> r)
    {
        UInt128 v = ReadU64(r, 0) | ((UInt128)r[8] << 64);
        uint Bits(int shift, int width) => (uint)((v >> shift) & ((1u << width) - 1));
        uint low = Bits(0, 18);
        if (((v >> 18) & 1) == 0)
            return new NapsBlockInfo
            {
                CoffsetStartMod256K = low,
                IsTerminal = ((v >> 19) & 1) != 0,
                UoffsetStart = Bits(20, 18),
                ClenEvenMinus1 = Bits(38, 17),
                Even = (byte)Bits(55, 3),
                Odd = (byte)Bits(58, 3),
                KdePredictor = (byte)Bits(61, 6),
                ShuffleIdx = (byte)Bits(68, 4),
            };
        return new NapsBlockInfo
        {
            IsRunBase = true,
            CoffsetEndMod256K = low,
            TweakIdxStart = Bits(20, 28),
            CoffsetStart256K = (uint)((v >> 50) & 0x3FFFFF),
        };
    }

    /// <summary>Index of the first block record of logical block <paramref name="ublock"/> (256K units).</summary>
    public uint BlockIndexOfUBlock(int ublock)
    {
        if (ublock < 0 || ublock >= UBlockCount) throw new InvalidDataException($"Logical block {ublock} is outside the layout.");
        (uint baseIndex, byte[] deltas) = U2c[ublock >> 3];
        return (ublock & 7) == 0 ? baseIndex : baseIndex + deltas[(ublock & 7) - 1];
    }

    private static ulong ReadU64(ReadOnlySpan<byte> s, int offset)
    {
        ulong v = 0;
        for (int i = 0; i < 8; i++) v |= (ulong)s[offset + i] << (8 * i);
        return v;
    }
}
