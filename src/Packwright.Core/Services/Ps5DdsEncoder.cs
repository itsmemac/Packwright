using System.Buffers.Binary;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Packwright.Core.Services;

/// <summary>
/// Encodes a picture as the BC7 texture (<c>.dds</c>) a package carries next to each of its PNG images. Every 4x4 block
/// is stored in BC7 mode 6 (one subset, 7-bit endpoints with a shared extra bit, 4-bit indices), which every BC7
/// decoder reads. The encoder is deliberately simple and fast; it is used when a title has the PNG but no texture.
/// </summary>
public static class Ps5DdsEncoder
{
    public const int HeaderSize = 148;
    private const uint DxgiFormatBc7Unorm = 98;

    private static readonly int[] Weights = [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    public static byte[] EncodePng(byte[] png)
    {
        ArgumentNullException.ThrowIfNull(png);
        using Image<Rgba32> image = Image.Load<Rgba32>(png);
        byte[] rgba = new byte[checked(image.Width * image.Height * 4)];
        image.CopyPixelDataTo(rgba);
        return EncodeRgba(rgba, image.Width, image.Height);
    }

    public static byte[] EncodeRgba(byte[] rgba, int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Invalid image size.");
        if (rgba.Length < checked(width * height * 4)) throw new ArgumentException("The pixel buffer is too small.", nameof(rgba));
        int blocksX = (width + 3) / 4, blocksY = (height + 3) / 4;
        int dataSize = checked(blocksX * blocksY * 16);
        byte[] dds = new byte[HeaderSize + dataSize];
        WriteHeader(dds, width, height, (uint)dataSize);

        Parallel.For(0, blocksY, by =>
        {
            Span<byte> block = stackalloc byte[64];
            for (int bx = 0; bx < blocksX; bx++)
            {
                Gather(rgba, width, height, bx * 4, by * 4, block);
                EncodeBlock(block, dds.AsSpan(HeaderSize + (by * blocksX + bx) * 16, 16));
            }
        });
        return dds;
    }

    private static void WriteHeader(byte[] d, int width, int height, uint linearSize)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(d, 0x20534444);            // "DDS "
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(4), 124);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(8), 0x000A1007);  // caps, height, width, pixel format, linear size
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(12), (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(16), (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(20), linearSize);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(28), 1);          // mip levels
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(76), 32);         // pixel format size
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(80), 4);          // four-cc present
        "DX10"u8.CopyTo(d.AsSpan(84));
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(108), 0x1000);    // texture
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(128), DxgiFormatBc7Unorm);
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(132), 3);         // 2D texture
        BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(140), 1);         // array size
    }

    // Copies a 4x4 block, repeating the last row and column at the edges of the picture.
    private static void Gather(byte[] rgba, int width, int height, int x0, int y0, Span<byte> block)
    {
        for (int y = 0; y < 4; y++)
        {
            int sy = Math.Min(y0 + y, height - 1);
            for (int x = 0; x < 4; x++)
            {
                int sx = Math.Min(x0 + x, width - 1);
                rgba.AsSpan((sy * width + sx) * 4, 4).CopyTo(block.Slice((y * 4 + x) * 4, 4));
            }
        }
    }

    private static void EncodeBlock(ReadOnlySpan<byte> block, Span<byte> destination)
    {
        Span<int> low = stackalloc int[4], high = stackalloc int[4];
        low.Fill(255);
        high.Fill(0);
        for (int i = 0; i < 16; i++)
            for (int c = 0; c < 4; c++)
            {
                int v = block[i * 4 + c];
                if (v < low[c]) low[c] = v;
                if (v > high[c]) high[c] = v;
            }

        Span<int> q0 = stackalloc int[4], q1 = stackalloc int[4], r0 = stackalloc int[4], r1 = stackalloc int[4];
        Quantize(low, q0, out int p0, r0);
        Quantize(high, q1, out int p1, r1);

        Span<byte> index = stackalloc byte[16];
        for (int i = 0; i < 16; i++) index[i] = BestIndex(r0, r1, block.Slice(i * 4, 4));

        // the first index is stored with three bits, so its top bit must be clear: swap the endpoints if it is not
        if (index[0] >= 8)
        {
            for (int c = 0; c < 4; c++) { (q0[c], q1[c]) = (q1[c], q0[c]); }
            (p0, p1) = (p1, p0);
            for (int i = 0; i < 16; i++) index[i] = (byte)(15 - index[i]);
        }

        ulong lo = 0, hi = 0;
        int position = 0;
        void Put(uint value, int bits)
        {
            for (int b = 0; b < bits; b++)
            {
                ulong bit = (value >> b) & 1;
                if (position < 64) lo |= bit << position; else hi |= bit << (position - 64);
                position++;
            }
        }
        Put(0x40, 7);                       // mode 6
        for (int c = 0; c < 4; c++) { Put((uint)q0[c], 7); Put((uint)q1[c], 7); }
        Put((uint)p0, 1);
        Put((uint)p1, 1);
        for (int i = 0; i < 16; i++) Put(index[i], i == 0 ? 3 : 4);
        BinaryPrimitives.WriteUInt64LittleEndian(destination, lo);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], hi);
    }

    // Seven bits per channel plus one shared low bit: pick the low bit that reproduces the endpoint best.
    private static void Quantize(ReadOnlySpan<int> endpoint, Span<int> quantized, out int pbit, Span<int> restored)
    {
        int bestError = int.MaxValue;
        pbit = 0;
        for (int p = 0; p < 2; p++)
        {
            int error = 0;
            for (int c = 0; c < 4; c++)
            {
                int v = Math.Clamp((endpoint[c] - p + 1) / 2, 0, 127);
                error += Math.Abs(((v << 1) | p) - endpoint[c]);
            }
            if (error < bestError) { bestError = error; pbit = p; }
        }
        for (int c = 0; c < 4; c++)
        {
            quantized[c] = Math.Clamp((endpoint[c] - pbit + 1) / 2, 0, 127);
            restored[c] = (quantized[c] << 1) | pbit;
        }
    }

    private static byte BestIndex(ReadOnlySpan<int> e0, ReadOnlySpan<int> e1, ReadOnlySpan<byte> pixel)
    {
        int best = 0, bestDistance = int.MaxValue;
        for (int i = 0; i < 16; i++)
        {
            int w = Weights[i], distance = 0;
            for (int c = 0; c < 4; c++)
            {
                int value = (e0[c] * (64 - w) + e1[c] * w + 32) >> 6;
                int d = value - pixel[c];
                distance += d * d;
            }
            if (distance < bestDistance) { bestDistance = distance; best = i; }
        }
        return (byte)best;
    }
}
