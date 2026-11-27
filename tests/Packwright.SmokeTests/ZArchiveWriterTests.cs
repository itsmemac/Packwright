using System.Text;
using Packwright.Containers;

namespace Packwright.SmokeTests;

/// <summary>
/// The ZArchive writer compresses blocks on several threads. Writes the same files with one thread and with several,
/// checks that the archives are byte for byte the same, and reads them back with Packwright's own reader.
/// Usage: SmokeTests zar-writer &lt;work folder&gt;
/// </summary>
internal static class ZArchiveWriterTests
{
    private static int _failures;
    private static readonly int[] ThreadCounts = [0, 2, 3, 7, 16, 64];

    public static async Task<int> Run(string[] args)
    {
        string work = Path.GetFullPath(args[1]);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            const int block = ZArchiveWriter.BlockSize;
            var cases = new List<(string Name, List<(string Path, byte[] Data)> Files)>
            {
                ("no files", []),
                ("one empty file", [("empty.bin", [])]),
                ("tiny files", Enumerable.Range(1, 40).Select(i => ($"tiny/{i}.txt", Text(i, i))).ToList()),
                ("exact multiples of 64 KiB", [
                    ("one.bin", Text(block, 1)), ("two.bin", Text(2 * block, 2)),
                    ("record.bin", Text(16 * block, 3)), ("record-and-one.bin", Text(17 * block, 4))]),
                ("one byte either side of a block", [("under.bin", Text(block - 1, 5)), ("over.bin", Text(block + 1, 6))]),
                ("incompressible data", [("random.bin", Noise(3 * block + 123, 7)), ("zeros.bin", new byte[block])]),
                ("one batch for two threads", [("batch.bin", Text(16 * block, 8))]),
                ("many small files", ManySmallFiles(3000)),
                ("large mixed files", [
                    ("data/text.bin", Text(24 * 1024 * 1024 + 17, 9)), ("data/noise.bin", Noise(9 * 1024 * 1024, 10)),
                    ("data/empty.bin", []), ("data/sparse.bin", new byte[5 * 1024 * 1024 + 3])]),
            };

            foreach ((string name, List<(string Path, byte[] Data)> files) in cases)
            {
                Console.WriteLine($"\n== {name} ({files.Count} files, {files.Sum(file => (long)file.Data.Length):N0} bytes)");
                byte[] reference = Build(files, 1);
                bool same = true;
                foreach (int threads in ThreadCounts) same &= Build(files, threads).AsSpan().SequenceEqual(reference);
                Check($"the same bytes with {string.Join(", ", ThreadCounts)} threads as with one", same);
                Check("the same bytes with the old constructor", Build(files, null).AsSpan().SequenceEqual(reference));

                string path = Path.Combine(work, "archive.zar");
                await File.WriteAllBytesAsync(path, reference);
                string? problem = null;
                try { await ZArchiveImage.VerifyAsync(path); }
                catch (InvalidDataException ex) { problem = ex.Message; }
                Check("verifies" + (problem is null ? "" : ": " + problem), problem is null);
                using var volume = new ZArchiveVolume(path);
                bool match = volume.Entries.Count(entry => !entry.IsDirectory) == files.Count;
                foreach ((string file, byte[] data) in files)
                {
                    using Stream input = volume.OpenFile(file);
                    using var copy = new MemoryStream();
                    input.CopyTo(copy);
                    match &= copy.ToArray().AsSpan().SequenceEqual(data);
                }
                Check("reads back every file unchanged", match);
            }

            Console.WriteLine("\n== use and failure");
            byte[] payload = Text(40 * block, 11);
            using (var writer = new ZArchiveWriter(new MemoryStream(), leaveOpen: false, threads: 4))
            {
                writer.StartNewFile("a.bin");
                writer.Append(payload);
                writer.Finish();
                Check("a second Finish is refused", Throws<InvalidOperationException>(writer.Finish));
            }
            Check("a negative thread count is refused",
                Throws<ArgumentOutOfRangeException>(() => new ZArchiveWriter(new MemoryStream(), false, -1).Dispose()));
            using (var writer = new ZArchiveWriter(new MemoryStream(), leaveOpen: false, threads: 4))
            {
                writer.StartNewFile("a.bin");
                writer.Append(payload);
            }
            Check("disposing an unfinished archive returns", true);
            var failing = new FailingStream(5 * block);
            var broken = new ZArchiveWriter(failing, leaveOpen: true, threads: 4);
            broken.StartNewFile("a.bin");
            bool reported = Throws<IOException>(() =>
            {
                for (int i = 0; i < 20; i++) broken.Append(payload);
                broken.Finish();
            });
            Check("a failing output is reported", reported);
            Check("and keeps being reported", Throws<IOException>(() => broken.Append(payload)));
            broken.Dispose();
            Check("and disposing afterwards returns", true);
        }
        finally
        {
            Directory.Delete(work, true);
        }
        Console.WriteLine(_failures == 0 ? "\nALL PASSED" : $"\n{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static byte[] Build(List<(string Path, byte[] Data)> files, int? threads)
    {
        var output = new MemoryStream();
        using (ZArchiveWriter writer = threads is { } count
                   ? new ZArchiveWriter(output, true, count)
                   : new ZArchiveWriter(output, leaveOpen: true))
        {
            // Pieces of changing size, so blocks are filled across Append calls and across files.
            var random = new Random(threads ?? -1);
            foreach ((string path, byte[] data) in files)
            {
                int slash = path.LastIndexOf('/');
                if (slash > 0) writer.MakeDirectory(path[..slash]);
                writer.StartNewFile(path);
                for (int done = 0; done < data.Length;)
                {
                    int take = Math.Min(data.Length - done, random.Next(1, 3 * ZArchiveWriter.BlockSize));
                    writer.Append(data.AsSpan(done, take));
                    done += take;
                }
            }
            writer.Finish();
        }
        return output.ToArray();
    }

    private static List<(string Path, byte[] Data)> ManySmallFiles(int count)
    {
        var random = new Random(12);
        var files = new List<(string Path, byte[] Data)>();
        for (int i = 0; i < count; i++)
        {
            int size = random.Next(0, 4000);
            files.Add(($"small/{i % 37}/{i}.dat", i % 5 == 0 ? Noise(size, i) : Text(size, i)));
        }
        return files;
    }

    // Compresses well, like the text and tables in a game.
    private static byte[] Text(int length, int seed)
    {
        string[] words = ["texture", "mesh", "sound", "level", "shader", "script", "0x1F", "player", "\n", " ", "{", "}"];
        var random = new Random(seed);
        var text = new StringBuilder(length + 16);
        while (text.Length < length) text.Append(words[random.Next(words.Length)]);
        return Encoding.ASCII.GetBytes(text.ToString(0, length));
    }

    // Does not compress, like data that is already compressed.
    private static byte[] Noise(int length, int seed)
    {
        byte[] data = new byte[length];
        new Random(seed).NextBytes(data);
        return data;
    }

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return true; }
        catch (AggregateException ex) when (ex.InnerException is T) { return true; }
        return false;
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private sealed class FailingStream(long limit) : MemoryStream
    {
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Length + buffer.Length > limit) throw new IOException("The disk is full (test).");
            base.Write(buffer);
        }
    }
}
