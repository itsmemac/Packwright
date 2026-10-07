// Packwright.Pkg - turns the planned inner image into its stored form and builds the layout file for it.
//
// The expanded image is cut into spans of up to 256 KB (never across the end of a file). Each span is stored as it is
// or Kraken-compressed, whichever is smaller, and the layout records where every span went. Executable modules and the
// files in sce_sys are always stored as they are, starting on a 64 KB boundary; everything else is packed behind the
// previous span. Output goes straight to the outer image writer, so memory use does not grow with the title.
#nullable enable
using LibProsperoPkg.PFS.Compression.Oodle;

namespace Packwright.Pkg.Writer;

/// <summary>How the inner image is stored.</summary>
public enum PkgCompression
{
    /// <summary>Every span stored as it is.</summary>
    Stored,
    /// <summary>Spans compressed with Kraken where that makes them smaller.</summary>
    Kraken,
}

public sealed class StoredImageResult
{
    /// <summary>Size of the stored inner image (pfs_image.dat), a whole number of 64 KB blocks.</summary>
    public required long StoredSize { get; init; }
    /// <summary>The layout file (naps_pkg_layout.dat).</summary>
    public required byte[] Layout { get; init; }
    /// <summary>Spans stored compressed / as they are.</summary>
    public required int CompressedSpans { get; init; }
    public required int RawSpans { get; init; }
}

public static class StoredImageBuilder
{
    private const int Block = 0x10000;
    private const int Chunk128K = 0x20000;
    private const int MinCompressibleBlock = 256;

    /// <param name="openFile">Opens the source of the planned file with the given index (index into <c>inner.Files</c>).</param>
    /// <param name="sink">Receives the stored image, in order.</param>
    /// <param name="threads">Spans compressed at the same time; 0 picks the processor count.</param>
    /// <param name="progress">Reports expanded bytes processed.</param>
    public static StoredImageResult Build(InnerImagePlan inner, Func<int, Stream> openFile, OuterPfsWriter sink,
        PkgCompression compression, int threads, IProgress<long>? progress, CancellationToken cancellationToken)
    {
        bool compress = compression == PkgCompression.Kraken;
        int workers = threads > 0 ? threads : Math.Max(1, Environment.ProcessorCount);
        int batchSize = compress ? Math.Max(4, workers * 3) : 16;

        var spans = new List<LayoutSpan>();
        var fileStarts = new List<long>();
        int compressedSpans = 0, rawSpans = 0;
        long processed = 0;
        byte[] zeros = new byte[NapsLayoutBuilder.SpanSize];

        // A run of spans: read in a batch, compress the batch in parallel, write it in order.
        var batchData = new byte[batchSize][];
        for (int i = 0; i < batchSize; i++) batchData[i] = new byte[NapsLayoutBuilder.SpanSize];
        var results = new Packed[batchSize];

        void Segment(long logicalStart, long length, Func<long, int, byte[], int> read, bool allowCompression, bool alignRaw)
        {
            if (length <= 0) return;
            if (alignRaw) PadTo(Block);
            long done = 0;
            while (done < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = 0;
                long batchBytes = 0;
                while (count < batchSize && done + batchBytes < length)
                {
                    int take = (int)Math.Min(NapsLayoutBuilder.SpanSize, length - done - batchBytes);
                    read(done + batchBytes, take, batchData[count]);
                    results[count] = new Packed(take);
                    batchBytes += take;
                    count++;
                }

                if (compress && allowCompression && count > 1)
                    Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = workers, CancellationToken = cancellationToken },
                        i => results[i] = Pack(batchData[i], results[i].Length));
                else if (compress && allowCompression)
                    results[0] = Pack(batchData[0], results[0].Length);

                long position = logicalStart + done;
                for (int i = 0; i < count; i++)
                {
                    Packed p = results[i];
                    long storedAt = sink.ImageBytes;
                    if (p.Payload is null)
                    {
                        sink.WriteImage(batchData[i].AsSpan(0, p.Length));
                        spans.Add(LayoutSpan.Raw(position, p.Length, storedAt));
                        rawSpans++;
                    }
                    else
                    {
                        sink.WriteImage(p.Payload);
                        spans.Add(new LayoutSpan(position, p.Length, storedAt, p.Payload.Length, p.EvenLength, p.Even, p.Odd));
                        compressedSpans++;
                    }
                    position += p.Length;
                }
                done += batchBytes;
                processed += batchBytes;
                progress?.Report(processed);
            }
        }

        void PadTo(int alignment)
        {
            long at = sink.ImageBytes;
            long pad = (alignment - at % alignment) % alignment;
            while (pad > 0)
            {
                int take = (int)Math.Min(pad, zeros.Length);
                sink.WriteImage(zeros.AsSpan(0, take));
                pad -= take;
            }
        }

        // ---- the data region, file by file ------------------------------------------------------------------------
        long logical = 0;
        for (int i = 0; i < inner.Files.Count; i++)
        {
            PlannedFile f = inner.Files[i];
            if (f.LogicalOffset > logical)
            {
                fileStarts.Add(logical);
                Segment(logical, f.LogicalOffset - logical, (_, count, buffer) => { Array.Clear(buffer, 0, count); return count; }, true, false);
            }
            fileStarts.Add(f.LogicalOffset);
            bool raw = f.IsExecutable || f.Path.StartsWith("sce_sys/", StringComparison.Ordinal);
            if (f.Size > 0)
            {
                using Stream source = openFile(i);
                Segment(f.LogicalOffset, f.Size, (_, count, buffer) =>
                {
                    source.ReadExactly(buffer, 0, count);
                    return count;
                }, !raw, raw);
            }
            logical = f.LogicalOffset + f.Size;
        }

        // ---- the reserved region, then the filesystem metadata ---------------------------------------------------------
        Segment(inner.DataEnd, inner.MetadataOffset - inner.DataEnd, (_, count, buffer) => { Array.Clear(buffer, 0, count); return count; }, true, false);
        byte[] metadata = inner.Metadata;
        Segment(inner.MetadataOffset, inner.MountSize - inner.MetadataOffset, (offset, count, buffer) =>
        {
            Buffer.BlockCopy(metadata, checked((int)offset), buffer, 0, count);
            return count;
        }, true, false);

        long storedSize = sink.FinishImage();
        byte[] layout = NapsLayoutBuilder.Build(fileStarts, inner.DataEnd, inner.MetadataOffset, inner.MountSize, spans, storedSize);
        return new StoredImageResult { StoredSize = storedSize, Layout = layout, CompressedSpans = compressedSpans, RawSpans = rawSpans };
    }

    private readonly record struct Packed(int Length, byte[]? Payload = null, int EvenLength = 0, byte Even = 0, byte Odd = 0);

    // Compresses one span; null payload means "store it as it is".
    private static Packed Pack(byte[] data, int length)
    {
        if (length <= MinCompressibleBlock) return new Packed(length);
        EncodedBlock? encoded = OodleKrakenEncoder.EncodeBlock(data.AsSpan(0, length), useHuffmanArrays: true);
        if (encoded is not { } eb) return new Packed(length);

        int chunks = length > Chunk128K ? 2 : 1;
        long stream = (long)eb.Payload.Length + 5 + 3L * chunks;
        if (stream > ((long)length * 15) >> 4) return new Packed(length);

        // even chunk: bit 0 present, bit 2 coded as newLZ, bit 1 sub/delta literals; odd chunk: bit 0 stored, else the same bits
        byte even = (byte)(1 | (eb.Chunk0Form == KrakenSubChunkForm.Lz ? 4 : 0) | (eb.Chunk0Form == KrakenSubChunkForm.Lz && eb.Chunk0LitMode == 0 ? 2 : 0));
        byte odd = 0;
        if (eb.MultiChunk)
            odd = eb.Chunk1Form == KrakenSubChunkForm.Lz
                ? (byte)(4 | (eb.Chunk1LitMode == 0 ? 2 : 0))
                : (byte)1;
        int evenLength = eb.MultiChunk ? eb.FirstChunkCompSize : eb.Payload.Length;
        return new Packed(length, eb.Payload, evenLength, even, odd);
    }
}
