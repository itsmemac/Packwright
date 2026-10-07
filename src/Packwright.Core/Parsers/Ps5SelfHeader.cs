using System.Buffers.Binary;

namespace Packwright.Core.Parsers;

/// <summary>Reads the few fields of an executable's SELF container header the app shows.</summary>
internal static class Ps5SelfHeader
{
    private const uint SelfMagic = 0x1D3D154F;
    private const uint SignedModuleMagic = 0xEEF51454;

    /// <summary>
    /// Reads the firmware version recorded in the container's extended info block, which sits after the embedded ELF
    /// header and its program headers.
    /// </summary>
    public static bool TryReadFirmwareVersion(ReadOnlySpan<byte> data, out ulong firmware)
    {
        firmware = 0;
        if (data.Length < 32) return false;
        uint magic = BinaryPrimitives.ReadUInt32LittleEndian(data);
        if (magic != SelfMagic && magic != SignedModuleMagic) return false;

        int segments = BinaryPrimitives.ReadUInt16LittleEndian(data[24..]);
        long elf = 32 + segments * 32L;
        if (elf + 64 > data.Length) return false;
        if (data[(int)elf] != 0x7F || data[(int)elf + 1] != (byte)'E' || data[(int)elf + 2] != (byte)'L' || data[(int)elf + 3] != (byte)'F') return false;

        int programHeaders = BinaryPrimitives.ReadUInt16LittleEndian(data[((int)elf + 56)..]);
        long headerEnd = elf + 64 + programHeaders * 56L;
        if (headerEnd > data.Length) return false;
        long info = (headerEnd + 15) & ~15L;
        if (info + 64 > data.Length) return false;
        firmware = BinaryPrimitives.ReadUInt64LittleEndian(data[((int)info + 24)..]);
        return true;
    }
}
