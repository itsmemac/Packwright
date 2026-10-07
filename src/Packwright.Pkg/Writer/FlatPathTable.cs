// Packwright.Pkg - the flat path tables of a package's inner filesystem (inode_flat_path_table and
// apr_flat_path_table): a sorted list of path hashes that lets the console find a file without walking
// directories.
//
// The path hash is a three-lane sponge over the upper-cased path. The algorithm and its constants come from
// LibProsperoPKG by SvenGDK (GPL-3.0-or-later); the two seeds also appear in the header of real tables, which is
// how the implementation is checked.
#nullable enable
using System.Buffers.Binary;
using System.Text;

namespace Packwright.Pkg.Writer;

public static class FlatPathTable
{
    private const ulong Seed0 = 0x92ca8aab26a24f51UL;
    private const ulong Seed1 = 0x09bbb761a41bc44dUL;
    private const ulong RoundConstant = 0x8000000080008081UL;

    /// <summary>Size of the table header; entries start right after it.</summary>
    public const int HeaderSize = 0x40;

    /// <summary>The two seeds as they are stored in the header at offset 0x30.</summary>
    public static ReadOnlySpan<byte> HeaderSeed =>
    [
        0x51, 0x4f, 0xa2, 0x26, 0xab, 0x8a, 0xca, 0x92,
        0x4d, 0xc4, 0x1b, 0xa4, 0x61, 0xb7, 0xbb, 0x09,
    ];

    private static ulong Rotl(ulong x, int n) => (x << n) | (x >> (64 - n));
    private static ulong Rotr(ulong x, int n) => (x >> n) | (x << (64 - n));

    /// <summary>The hash of a path: upper-cased, without a leading slash (for example <c>SCE_SYS/KEYSTONE</c>).</summary>
    public static ulong HashPath(string path)
    {
        if (path.Length > 0 && path[0] == '/') path = path[1..];
        return Hash(Encoding.ASCII.GetBytes(path.ToUpperInvariant()));
    }

    public static ulong Hash(ReadOnlySpan<byte> text)
    {
        int length = text.Length;
        ulong s0 = Seed0, s1 = Rotl(Seed0, 11), s2 = Rotl(Seed0, 23);
        ulong tail = 0;
        if (length != 0)
        {
            int words = (length - 1) >> 3;
            ulong a0 = s0, a1 = s1, a2 = s2;
            int offset = 0;
            for (int i = 0; i < words; i++)
            {
                ulong w = BinaryPrimitives.ReadUInt64LittleEndian(text.Slice(offset, 8));
                offset += 8;
                a0 ^= w;
                ulong t18 = Rotr(Rotl(a2 ^ a1, 5) ^ a0, 11);
                ulong t12 = Rotl(Rotl(a2 ^ a0, 17) ^ a1, 11);
                a2 = Rotr(Rotl(a1 ^ a0, 1) ^ a2, 5);
                a0 = (~t12 & a2) ^ t18 ^ RoundConstant;
                a1 = (~a2 & t18) ^ t12;
                a2 = (~t18 & t12) ^ a2;
            }
            s0 = a0; s1 = a1; s2 = a2;
            int tailLength = ((length - 1) & 7) + 1;
            for (int j = 0; j < tailLength; j++) tail |= (ulong)text[offset + j] << (8 * j);
        }
        ulong u16 = s1, u11 = s2, u6 = tail ^ s0 ^ Seed1;
        ulong u17 = Rotl(u11 ^ u16, 5) ^ u6;
        ulong u18 = Rotl(u11 ^ u6, 17) ^ u16;
        u11 = Rotl(u16 ^ u6, 1) ^ u11;
        return (~Rotl(u18, 11) & Rotr(u11, 5)) ^ Rotr(u17, 11) ^ RoundConstant;
    }

    /// <summary>Reads a table back as (hash, packed) pairs, for checking and comparing.</summary>
    public static IReadOnlyList<(ulong Hash, ulong Packed)> Read(ReadOnlySpan<byte> table)
    {
        if (table.Length < HeaderSize || table[0x20] != 0x7F || table[0x21] != (byte)'F')
            throw new InvalidDataException("This is not a flat path table.");
        int count = (int)BinaryPrimitives.ReadUInt32LittleEndian(table[0x2C..]);
        var entries = new List<(ulong, ulong)>(count);
        for (int i = 0; i < count; i++)
        {
            ReadOnlySpan<byte> e = table.Slice(HeaderSize + i * 16, 16);
            entries.Add((BinaryPrimitives.ReadUInt64LittleEndian(e), BinaryPrimitives.ReadUInt64LittleEndian(e[8..])));
        }
        return entries;
    }
}
