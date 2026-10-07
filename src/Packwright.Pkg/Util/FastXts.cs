// Packwright.Pkg - AES-XTS-128 over whole data units, in bulk.
//
// Same transform as XtsBlockTransform (the tweak is the data unit number, little-endian, encrypted with the tweak
// key, then multiplied by x in GF(2^128) for every following 16-byte block) but done with one ECB call over the
// whole unit instead of one call per block, which is several times faster on large images.
#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Packwright.Pkg.Util;

public sealed class FastXts : IDisposable
{
    private readonly Aes _data;
    private readonly Aes _tweak;
    private byte[] _mask = new byte[0];

    public FastXts(byte[] dataKey, byte[] tweakKey)
    {
        _data = Aes.Create();
        _data.Key = dataKey;
        _tweak = Aes.Create();
        _tweak.Key = tweakKey;
    }

    /// <summary>Encrypts or decrypts <paramref name="unit"/> (a multiple of 16 bytes) in place as data unit <paramref name="sector"/>.</summary>
    public void Crypt(Span<byte> unit, ulong sector, bool encrypt)
    {
        if (unit.Length % 16 != 0) throw new ArgumentException("An XTS data unit must be a multiple of 16 bytes.", nameof(unit));
        if (_mask.Length < unit.Length) _mask = new byte[unit.Length];
        Span<byte> mask = _mask.AsSpan(0, unit.Length);

        Span<byte> t = stackalloc byte[16];
        t.Clear();
        BinaryPrimitives.WriteUInt64LittleEndian(t, sector);
        _tweak.EncryptEcb(t, t, PaddingMode.None);

        ulong lo = BinaryPrimitives.ReadUInt64LittleEndian(t);
        ulong hi = BinaryPrimitives.ReadUInt64LittleEndian(t[8..]);
        for (int i = 0; i < mask.Length; i += 16)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(mask[i..], lo);
            BinaryPrimitives.WriteUInt64LittleEndian(mask[(i + 8)..], hi);
            ulong carry = hi >> 63;
            hi = (hi << 1) | (lo >> 63);
            lo <<= 1;
            if (carry != 0) lo ^= 0x87;
        }

        Xor(unit, mask);
        if (encrypt) _data.EncryptEcb(unit, unit, PaddingMode.None);
        else _data.DecryptEcb(unit, unit, PaddingMode.None);
        Xor(unit, mask);
    }

    private static void Xor(Span<byte> a, ReadOnlySpan<byte> b)
    {
        var wa = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(a);
        var wb = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ulong>(b);
        for (int i = 0; i < wa.Length; i++) wa[i] ^= wb[i];
    }

    public void Dispose()
    {
        _data.Dispose();
        _tweak.Dispose();
    }
}
