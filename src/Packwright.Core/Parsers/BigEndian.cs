using System.Buffers.Binary;

namespace Packwright.Core.Parsers;

internal static class BigEndian
{
    public static uint UInt32(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt32BigEndian(value);
    public static ulong UInt64(ReadOnlySpan<byte> value) => BinaryPrimitives.ReadUInt64BigEndian(value);
}
