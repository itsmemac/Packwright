// Packwright.Pkg - SHA3-256 (FIPS 202).
//
// The platform's SHA3 is used when it has one; Windows builds without it (older Windows 10 and Windows 11 before
// 22H2) fall back to the managed Keccak implementation below. Both give the same digest.
#nullable enable
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Packwright.Pkg.Util;

public static class Sha3
{
    public static bool UsesPlatform { get; } = SHA3_256.IsSupported;

    public static byte[] Hash(ReadOnlySpan<byte> data)
    {
        byte[] result = new byte[32];
        Hash(data, result);
        return result;
    }

    public static void Hash(ReadOnlySpan<byte> data, Span<byte> destination)
    {
        if (UsesPlatform) { SHA3_256.HashData(data, destination); return; }
        var k = new Keccak();
        k.Append(data);
        k.Finish(destination);
    }

    public static byte[] Hash(Stream stream)
    {
        if (UsesPlatform) return SHA3_256.HashData(stream);
        var k = new Keccak();
        byte[] buffer = new byte[1 << 20];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0) k.Append(buffer.AsSpan(0, n));
        byte[] result = new byte[32];
        k.Finish(result);
        return result;
    }

    /// <summary>Incremental SHA3-256.</summary>
    public sealed class Keccak
    {
        private const int Rate = 136;
        private readonly ulong[] _state = new ulong[25];
        private readonly byte[] _buffer = new byte[Rate];
        private int _filled;

        public void Append(ReadOnlySpan<byte> data)
        {
            while (data.Length > 0)
            {
                int take = Math.Min(Rate - _filled, data.Length);
                data[..take].CopyTo(_buffer.AsSpan(_filled));
                _filled += take;
                data = data[take..];
                if (_filled == Rate) { Absorb(); _filled = 0; }
            }
        }

        public void Finish(Span<byte> destination)
        {
            Array.Clear(_buffer, _filled, Rate - _filled);
            _buffer[_filled] ^= 0x06;
            _buffer[Rate - 1] ^= 0x80;
            Absorb();
            for (int i = 0; i < 4; i++) BinaryPrimitives.WriteUInt64LittleEndian(destination[(i * 8)..], _state[i]);
        }

        private void Absorb()
        {
            for (int i = 0; i < Rate / 8; i++) _state[i] ^= BinaryPrimitives.ReadUInt64LittleEndian(_buffer.AsSpan(i * 8));
            Permute(_state);
        }

        private static readonly ulong[] RoundConstants =
        [
            0x0000000000000001, 0x0000000000008082, 0x800000000000808A, 0x8000000080008000, 0x000000000000808B, 0x0000000080000001,
            0x8000000080008081, 0x8000000000008009, 0x000000000000008A, 0x0000000000000088, 0x0000000080008009, 0x000000008000000A,
            0x000000008000808B, 0x800000000000008B, 0x8000000000008089, 0x8000000000008003, 0x8000000000008002, 0x8000000000000080,
            0x000000000000800A, 0x800000008000000A, 0x8000000080008081, 0x8000000000008080, 0x0000000080000001, 0x8000000080008008,
        ];

        private static readonly int[] Rotations =
            [1, 3, 6, 10, 15, 21, 28, 36, 45, 55, 2, 14, 27, 41, 56, 8, 25, 43, 62, 18, 39, 61, 20, 44];
        private static readonly int[] Lanes =
            [10, 7, 11, 17, 18, 3, 5, 16, 8, 21, 24, 4, 15, 23, 19, 13, 12, 2, 20, 14, 22, 9, 6, 1];

        private static void Permute(ulong[] a)
        {
            Span<ulong> c = stackalloc ulong[5];
            for (int round = 0; round < 24; round++)
            {
                for (int x = 0; x < 5; x++) c[x] = a[x] ^ a[x + 5] ^ a[x + 10] ^ a[x + 15] ^ a[x + 20];
                for (int x = 0; x < 5; x++)
                {
                    ulong d = c[(x + 4) % 5] ^ ulong.RotateLeft(c[(x + 1) % 5], 1);
                    for (int y = 0; y < 25; y += 5) a[y + x] ^= d;
                }
                ulong t = a[1];
                for (int i = 0; i < 24; i++)
                {
                    int j = Lanes[i];
                    ulong next = a[j];
                    a[j] = ulong.RotateLeft(t, Rotations[i]);
                    t = next;
                }
                for (int y = 0; y < 25; y += 5)
                {
                    for (int x = 0; x < 5; x++) c[x] = a[y + x];
                    for (int x = 0; x < 5; x++) a[y + x] = c[x] ^ (~c[(x + 1) % 5] & c[(x + 2) % 5]);
                }
                a[0] ^= RoundConstants[round];
            }
        }
    }
}
