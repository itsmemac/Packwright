// Packwright.Pkg - CRC-32C (Castagnoli), the checksum of the per-block table in the install metadata.
#nullable enable
using System.Runtime.Intrinsics.X86;

namespace Packwright.Pkg.Util;

public static class Crc32C
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? (c >> 1) ^ 0x82F63B78u : c >> 1;
            table[i] = c;
        }
        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        int i = 0;
        if (Sse42.X64.IsSupported)
        {
            ulong wide = crc;
            for (; i + 8 <= data.Length; i += 8)
                wide = Sse42.X64.Crc32(wide, BitConverter.ToUInt64(data.Slice(i, 8)));
            crc = (uint)wide;
        }
        for (; i < data.Length; i++) crc = (crc >> 8) ^ Table[(crc ^ data[i]) & 0xFF];
        return ~crc;
    }
}
