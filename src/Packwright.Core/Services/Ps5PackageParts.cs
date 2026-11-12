using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;
using Packwright.Pkg.Util;

namespace Packwright.Core.Services;

/// <summary>One file of a package that was downloaded in parts: a numbered slice, or the metadata container (<c>_sc</c>).</summary>
public sealed record Ps5PackagePart(string Path, int? Number, long Length)
{
    /// <summary>True for <c>&lt;name&gt;_sc.pkg</c>, the package's metadata container, which goes after the last numbered part.</summary>
    public bool IsContainer => Number is null;
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <summary>
/// A package split into parts the way PlayStation downloads are: <c>&lt;name&gt;_0.pkg</c>, <c>_1.pkg</c>, ... and often a
/// separate <c>&lt;name&gt;_sc.pkg</c>. The parts are consecutive slices of one package; <see cref="Parts"/> is in merge order.
/// </summary>
public sealed class Ps5PackagePartSet
{
    public required string Folder { get; init; }
    public required string BaseName { get; init; }
    /// <summary>Numbered parts in ascending order, then the container part when there is one.</summary>
    public required IReadOnlyList<Ps5PackagePart> Parts { get; init; }
    /// <summary>
    /// The work file of an in-place merge that was interrupted: part 0 with the first parts already appended. When set,
    /// it stands in for the parts it already holds.
    /// </summary>
    public Ps5PackagePart? Resume { get; init; }

    public IEnumerable<Ps5PackagePart> Numbered => Parts.Where(part => !part.IsContainer);
    public Ps5PackagePart? Container => Parts.FirstOrDefault(part => part.IsContainer);
    public long TotalBytes => (Resume?.Length ?? 0) + Parts.Sum(part => part.Length);
    public int Count => Parts.Count + (Resume is null ? 0 : 1);
    public string SuggestedOutput => System.IO.Path.Combine(Folder, BaseName + ".pkg");
    /// <summary>The file whose header describes the whole package: the resume file, or part 0.</summary>
    public string? HeaderPath => Resume?.Path ?? Parts.FirstOrDefault(part => part.Number == 0)?.Path;

    /// <summary>Numbers missing between the first and the last numbered part (and 0, unless a resume file holds it).</summary>
    public IReadOnlyList<int> MissingNumbers()
    {
        int[] numbers = Numbered.Select(part => part.Number!.Value).ToArray();
        if (numbers.Length == 0) return Resume is null ? [0] : [];
        int first = Resume is null ? 0 : numbers.Min();
        return Enumerable.Range(first, numbers.Max() - first + 1).Except(numbers).ToArray();
    }
}

/// <summary>What the parts were checked against before a merge (or what a merged file was checked against afterwards).</summary>
public sealed record Ps5PackagePartCheck(
    bool Ok, string Message, string ContentId, long ExpectedSize, long ContainerOffset, IReadOnlyList<string> Passed);

public enum Ps5PackageMergeMode
{
    /// <summary>Write a new file and keep the parts. Needs free space for the whole package.</summary>
    NewFile,
    /// <summary>
    /// Append every part onto part 0 and delete each part once it is in. Needs only a little free space, and an interrupted
    /// merge carries on where it stopped.
    /// </summary>
    InPlace
}

public sealed record Ps5PackageMergeProgress(string Stage, long Done, long Total, string Part);

/// <summary>
/// Finds, checks and merges packages that were downloaded in parts. The parts are joined end to end in number order with
/// the <c>_sc</c> container last, which is where the package header says its metadata container starts. Every check here
/// reads only headers and digests, so it needs no keys and works for retail packages; it proves that the parts fit
/// together and belong to the same package, not that the encrypted data inside each part is undamaged.
/// </summary>
public static partial class Ps5PackageParts
{
    private const int CntHeaderSize = 0x1000;
    private const int FihSize = 0x10000;
    private const string ResumeSuffix = ".merging";
    private static ReadOnlySpan<byte> FihMagic => [0x7F, (byte)'F', (byte)'I', (byte)'H'];
    private static ReadOnlySpan<byte> LihMagic => [0x7F, (byte)'L', (byte)'I', (byte)'H'];
    private static ReadOnlySpan<byte> CntMagic => [0x7F, (byte)'C', (byte)'N', (byte)'T'];

    [GeneratedRegex(@"^(?<base>.+)_(?<part>\d{1,4}|sc)\.pkg$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PartName();

    /// <summary>Splits <c>&lt;name&gt;_7.pkg</c> into its name and number (null for <c>_sc</c>).</summary>
    public static bool TryParseName(string fileName, out string baseName, out int? number)
    {
        Match match = PartName().Match(fileName);
        baseName = match.Success ? match.Groups["base"].Value : string.Empty;
        number = null;
        if (!match.Success) return false;
        string part = match.Groups["part"].Value;
        if (!part.Equals("sc", StringComparison.OrdinalIgnoreCase)) number = int.Parse(part, System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    /// Groups package files into part sets. A set needs at least two files, so an ordinary package that happens to end in
    /// <c>_1.pkg</c> is left alone. Files that are not in a set are not returned.
    /// </summary>
    public static IReadOnlyList<Ps5PackagePartSet> FindSets(IEnumerable<string> packagePaths)
    {
        var sets = new List<Ps5PackagePartSet>();
        foreach (var group in packagePaths
                     .Select(path => (Path: System.IO.Path.GetFullPath(path), Ok: TryParseName(System.IO.Path.GetFileName(path), out string name, out int? number), Name: name, Number: number))
                     .Where(file => file.Ok)
                     .GroupBy(file => (Folder: System.IO.Path.GetDirectoryName(file.Path) ?? string.Empty, file.Name),
                         new FolderNameComparer()))
        {
            string folder = group.Key.Folder, name = group.Key.Name;
            string resumePath = System.IO.Path.Combine(folder, name + ".pkg" + ResumeSuffix);
            Ps5PackagePart? resume = File.Exists(resumePath) ? new Ps5PackagePart(resumePath, 0, Length(resumePath)) : null;
            var parts = group
                .GroupBy(file => file.Number)                      // the same part twice (case only) counts once
                .Select(same => same.First())
                .OrderBy(file => file.Number is null ? 1 : 0).ThenBy(file => file.Number)
                .Select(file => new Ps5PackagePart(file.Path, file.Number, Length(file.Path)))
                .ToList();
            if (parts.Count + (resume is null ? 0 : 1) < 2) continue;
            sets.Add(new Ps5PackagePartSet { Folder = folder, BaseName = name, Parts = parts, Resume = resume });
        }
        return sets;
    }

    /// <summary>
    /// The part set a path belongs to: a part file (the set is found beside it), or a folder holding exactly one set.
    /// Null when the path is not part of a set; throws when a folder holds several, so the caller can ask for one part.
    /// </summary>
    public static Ps5PackagePartSet? FindSet(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(full))
        {
            IReadOnlyList<Ps5PackagePartSet> sets = FindSets(Directory.EnumerateFiles(full, "*.pkg"));
            if (sets.Count > 1)
                throw new InvalidDataException($"That folder holds {sets.Count} packages in parts ({string.Join(", ", sets.Select(set => set.BaseName))}). Choose one of their part files instead.");
            return sets.FirstOrDefault();
        }
        if (!File.Exists(full)) return null;
        string fileName = System.IO.Path.GetFileName(full);
        string name;
        if (fileName.EndsWith(".pkg" + ResumeSuffix, StringComparison.OrdinalIgnoreCase))
            name = fileName[..^(".pkg" + ResumeSuffix).Length];        // the work file of an interrupted in-place merge
        else if (!TryParseName(fileName, out name, out _)) return null;
        string folder = System.IO.Path.GetDirectoryName(full) ?? ".";
        return FindSets(Directory.EnumerateFiles(folder, "*.pkg"))
            .FirstOrDefault(set => set.BaseName.Equals(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>True when the path is a part file or the work file of an interrupted in-place merge.</summary>
    public static bool LooksLikePart(string path)
    {
        string fileName = System.IO.Path.GetFileName(path);
        return fileName.EndsWith(".pkg" + ResumeSuffix, StringComparison.OrdinalIgnoreCase) || TryParseName(fileName, out _, out _);
    }

    /// <summary>
    /// Checks the parts before merging, reading only headers and digests: no part is missing, part 0 starts the package,
    /// the parts end exactly where the metadata container starts (or hold it), and the container's own digests, the
    /// digest of part 0's header and the digest of the image superblock (near the end of the image) all match.
    /// </summary>
    public static Ps5PackagePartCheck Check(Ps5PackagePartSet set)
    {
        set = Recover(set);
        IReadOnlyList<int> missing = set.MissingNumbers();
        if (missing.Count > 0)
            return Fail($"{(missing.Count == 1 ? "Part" : "Parts")} {string.Join(", ", missing.Take(12))}{(missing.Count > 12 ? ", ..." : string.Empty)} " +
                        $"{(missing.Count == 1 ? "is" : "are")} missing. Download {(missing.Count == 1 ? "it" : "them")} into the same folder first.");
        using var reader = new SpanReader(set);
        return CheckLayout(reader, set.Container is not null, isMerged: false);
    }

    /// <summary>Checks a merged package the same way, from the single file.</summary>
    public static Ps5PackagePartCheck CheckMerged(string path)
    {
        using var reader = new SpanReader([new Ps5PackagePart(path, 0, Length(path))]);
        return CheckLayout(reader, hasContainerPart: false, isMerged: true);
    }

    private static Ps5PackagePartCheck CheckLayout(SpanReader reader, bool hasContainerPart, bool isMerged)
    {
        string what = isMerged ? "The package" : "Part 0";
        if (reader.Length < FihSize) return Fail($"{what} is too small to be a PS5 package.");
        byte[] head = reader.Read(0, FihSize);
        bool patch = head.AsSpan(0, 4).SequenceEqual(LihMagic);
        if (!patch && !head.AsSpan(0, 4).SequenceEqual(FihMagic))
            return Fail(isMerged ? "The file does not start with a PS5 package header."
                                 : "Part 0 does not start with a PS5 package header, so it is not the first part of a package.");
        long containerOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(patch ? 48 : 0x58));
        long numberedEnd = reader.Length - (hasContainerPart ? reader.LastLength : 0);
        if (hasContainerPart)
        {
            if (numberedEnd != containerOffset)
                return Fail(numberedEnd < containerOffset
                    ? $"The numbered parts end at byte {numberedEnd:N0}, but the package continues to byte {containerOffset:N0} " +
                      $"before its _sc part ({Ps5LibraryHealth.FormatBytes(containerOffset - numberedEnd)} short). A part is missing or did not finish downloading."
                    : $"The numbered parts are {Ps5LibraryHealth.FormatBytes(numberedEnd - containerOffset)} longer than the package header allows. " +
                      "A part from another download may be mixed in.");
        }
        if (containerOffset <= 0 || containerOffset + CntHeaderSize > reader.Length)
            return Fail(hasContainerPart || isMerged
                ? "The package's metadata container is beyond the end of the parts. A part is missing or did not finish downloading."
                : $"The package's metadata container belongs at byte {containerOffset:N0}, after the last part. Its _sc.pkg part is missing.");
        byte[] header = reader.Read(containerOffset, CntHeaderSize);
        if (!header.AsSpan(0, 4).SequenceEqual(CntMagic))
            return Fail(isMerged
                ? "There is no metadata container where the package header says it starts. The parts were joined in the wrong order or one is damaged."
                : "The _sc part (or the end of the parts) is not the package's metadata container. A part is damaged or from another download.");
        string contentId = Encoding.ASCII.GetString(header, 0x40, 36).TrimEnd('\0');
        ulong bodyOffset = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x20));
        ulong bodySize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(0x28));
        long containerEnd = containerOffset + (long)(bodyOffset + bodySize);
        if (bodyOffset < CntHeaderSize || containerEnd > reader.Length || containerEnd < containerOffset)
            return Fail("The metadata container is cut short. The _sc part (or the last part) did not finish downloading.", contentId);

        var passed = new List<string>();
        string? Digest(string name, bool ok, string failure)
        {
            if (ok) { passed.Add(name); return null; }
            return failure;
        }
        string? problem =
            Digest("container seal", Sha3.Hash(header.AsSpan(0, 0xFE0)).AsSpan().SequenceEqual(header.AsSpan(0xFE0, 32)),
                "The metadata container's seal does not match: the _sc part is damaged.")
            ?? Digest("container body", HashRange(reader, containerOffset + (long)bodyOffset, (long)bodySize).AsSpan().SequenceEqual(header.AsSpan(0x160, 32)),
                "The metadata container's contents do not match their digest: the _sc part is damaged.");
        if (problem is null && !patch)
        {
            long superblock = (long)BinaryPrimitives.ReadUInt64LittleEndian(head.AsSpan(0x20));
            problem =
                Digest("header", Sha3.Hash(head).AsSpan().SequenceEqual(header.AsSpan(0x460, 32)),
                    "Part 0 does not belong to this metadata container: the parts come from different downloads or versions.")
                ?? (superblock <= 0 || superblock + FihSize > containerOffset
                    ? "The image superblock is outside the image. The package header is damaged."
                    : Digest("image superblock", Sha3.Hash(reader.Read(superblock, FihSize)).AsSpan().SequenceEqual(header.AsSpan(0x440, 32)),
                        "The image superblock does not match the package: a part is damaged, from another download, or in the wrong place."));
        }
        if (problem is not null) return new Ps5PackagePartCheck(false, problem, contentId, reader.Length, containerOffset, passed);
        return new Ps5PackagePartCheck(true,
            $"{contentId}: {Ps5LibraryHealth.FormatBytes(reader.Length)}, the parts fit together and {string.Join(", ", passed)} digests match.",
            contentId, reader.Length, containerOffset, passed);
    }

    private static Ps5PackagePartCheck Fail(string message, string contentId = "") => new(false, message, contentId, 0, 0, []);

    private static byte[] HashRange(SpanReader reader, long offset, long length)
    {
        byte[] data = reader.Read(offset, checked((int)length));
        return Sha3.Hash(data);
    }

    /// <summary>
    /// Merges the parts into <paramref name="output"/> after <see cref="Check"/> passes, then checks the merged file. With
    /// <see cref="Ps5PackageMergeMode.NewFile"/> the parts stay and a temporary file is renamed into place at the end. With
    /// <see cref="Ps5PackageMergeMode.InPlace"/> part 0 becomes <c>&lt;output&gt;.merging</c>, every other part is appended and
    /// deleted once it is safely written, and the finished file is renamed to <paramref name="output"/>; run it again after
    /// an interruption to carry on.
    /// </summary>
    public static async Task<Ps5PackagePartCheck> MergeAsync(Ps5PackagePartSet set, string output, Ps5PackageMergeMode mode,
        bool overwrite, IProgress<Ps5PackageMergeProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(set);
        output = System.IO.Path.GetFullPath(output);
        if (set.Parts.Any(part => string.Equals(part.Path, output, StringComparison.OrdinalIgnoreCase)))
            throw new IOException("The merged file cannot replace one of its own parts. Choose another name.");
        if (File.Exists(output) || Directory.Exists(output))
        {
            if (!overwrite) throw new IOException("The output already exists: " + output);
            if (Directory.Exists(output)) throw new IOException("A folder with the output's name already exists: " + output);
        }

        if (set.Resume is not null && mode == Ps5PackageMergeMode.NewFile)
            throw new IOException($"An in-place merge of this package was interrupted ({System.IO.Path.GetFileName(set.Resume.Path)} " +
                                  "holds the first parts). Merge in place again to carry on.");
        set = Recover(set);
        progress?.Report(new Ps5PackageMergeProgress("Checking parts", 0, set.TotalBytes, string.Empty));
        Ps5PackagePartCheck before = Check(set);
        if (!before.Ok) throw new InvalidDataException(before.Message);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(output)!);

        if (mode == Ps5PackageMergeMode.InPlace) await MergeInPlaceAsync(set, output, overwrite, progress, cancellationToken).ConfigureAwait(false);
        else await MergeToNewFileAsync(set, output, overwrite, progress, cancellationToken).ConfigureAwait(false);

        progress?.Report(new Ps5PackageMergeProgress("Verifying", set.TotalBytes, set.TotalBytes, string.Empty));
        Ps5PackagePartCheck after = CheckMerged(output);
        if (!after.Ok) throw new InvalidDataException("The merged package did not pass its check: " + after.Message);
        return after;
    }

    private static async Task MergeToNewFileAsync(Ps5PackagePartSet set, string output, bool overwrite,
        IProgress<Ps5PackageMergeProgress>? progress, CancellationToken cancellationToken)
    {
        string folder = System.IO.Path.GetDirectoryName(output)!;
        long needed = set.TotalBytes - (overwrite && File.Exists(output) ? Length(output) : 0);
        if (FreeSpace(folder) is long free && free < needed)
            throw new IOException($"Not enough free space for the merged package: it needs {Ps5LibraryHealth.FormatBytes(needed)} and " +
                                  $"{Ps5LibraryHealth.FormatBytes(free)} is free. Choose another folder, or merge in place.");
        string temporary = output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 20,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                target.SetLength(set.TotalBytes);              // claims the space up front, so a full disk fails now
                long done = 0;
                foreach (Ps5PackagePart part in set.Parts)
                    done = await AppendAsync(part, target, done, set.TotalBytes, progress, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            File.Move(temporary, output, overwrite);
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    // ---- in-place merge ---------------------------------------------------------------------------------------------
    // Part 0 is renamed to <name>.pkg.merging and the other parts are appended to it one by one. Beside it,
    // <name>.pkg.merging.state records three lines: the length of the work file up to the last part that is completely
    // written, the name of that part, and the name of the part being appended. A part is deleted only after the state
    // says it is written, so an interruption at any point loses nothing: the next run cuts the work file back to the
    // recorded length, deletes a written part that was not deleted yet, and carries on.

    private sealed record MergeState(long Boundary, string Committed, string Pending);

    private static string StatePath(string work) => work + ".state";

    private static MergeState? ReadState(string work)
    {
        try
        {
            string[] lines = File.ReadAllLines(StatePath(work));
            return lines.Length >= 3 && long.TryParse(lines[0], out long boundary) ? new MergeState(boundary, lines[1], lines[2]) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private static void WriteState(string work, MergeState state)
    {
        string path = StatePath(work), temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write($"{state.Boundary}\n{state.Committed}\n{state.Pending}\n");
            writer.Flush();
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>
    /// The set as it stands for an interrupted in-place merge: the work file counts only up to its last completely
    /// written part, and a part that was written but not deleted yet is left out. Changes nothing on disk.
    /// </summary>
    private static Ps5PackagePartSet Recover(Ps5PackagePartSet set)
    {
        if (set.Resume is null || ReadState(set.Resume.Path) is not { } state) return set;
        long boundary = Math.Min(state.Boundary, set.Resume.Length);
        return new Ps5PackagePartSet
        {
            Folder = set.Folder, BaseName = set.BaseName,
            Resume = set.Resume with { Length = boundary },
            Parts = set.Parts.Where(part => !part.Name.Equals(state.Committed, StringComparison.OrdinalIgnoreCase)).ToList()
        };
    }

    private static async Task MergeInPlaceAsync(Ps5PackagePartSet set, string output, bool overwrite,
        IProgress<Ps5PackageMergeProgress>? progress, CancellationToken cancellationToken)
    {
        string work = System.IO.Path.Combine(set.Folder, set.BaseName + ".pkg" + ResumeSuffix);
        if (!string.Equals(System.IO.Path.GetPathRoot(output), System.IO.Path.GetPathRoot(set.Folder), StringComparison.OrdinalIgnoreCase))
            throw new IOException("Merging in place keeps the package on the drive of its parts. Choose an output on the same drive, or write a new file.");
        long largest = set.Parts.Where(part => part.Number != 0).Select(part => part.Length).DefaultIfEmpty(0).Max();
        if (FreeSpace(set.Folder) is long free && free < largest)
            throw new IOException($"Not enough free space to merge in place: it needs {Ps5LibraryHealth.FormatBytes(largest)} free " +
                                  $"(the largest part) and {Ps5LibraryHealth.FormatBytes(free)} is free.");

        set = Recover(set);
        if (set.Resume is null)
        {
            Ps5PackagePart first = set.Parts.First(part => part.Number == 0);
            File.Move(first.Path, work);
            WriteState(work, new MergeState(first.Length, first.Name, string.Empty));
            set = new Ps5PackagePartSet
            {
                Folder = set.Folder, BaseName = set.BaseName,
                Resume = new Ps5PackagePart(work, 0, first.Length),
                Parts = set.Parts.Where(part => part.Number != 0).ToList()
            };
        }
        else
        {
            // Carry on: forget a half-appended part and delete one that was written but not deleted.
            MergeState state = ReadState(work) ?? new MergeState(set.Resume.Length, string.Empty, string.Empty);
            string committed = System.IO.Path.Combine(set.Folder, state.Committed);
            if (state.Committed.Length > 0 && File.Exists(committed) && !committed.Equals(work, StringComparison.OrdinalIgnoreCase))
                File.Delete(committed);
        }

        long done = set.Resume!.Length;
        string committedName = ReadState(work)?.Committed ?? string.Empty;
        await using (var target = new FileStream(work, FileMode.Open, FileAccess.Write, FileShare.None, 1 << 20,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            target.SetLength(done);
            foreach (Ps5PackagePart part in set.Parts)
            {
                WriteState(work, new MergeState(done, committedName, part.Name));
                target.Position = done;
                long next = await AppendAsync(part, target, done, set.TotalBytes, progress, cancellationToken).ConfigureAwait(false);
                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);              // on disk before the state says so and the part goes
                WriteState(work, new MergeState(next, part.Name, string.Empty));
                File.Delete(part.Path);
                committedName = part.Name;
                done = next;
            }
        }
        File.Move(work, output, overwrite);
        TryDelete(StatePath(work));
    }

    private static async Task<long> AppendAsync(Ps5PackagePart part, FileStream target, long done, long total,
        IProgress<Ps5PackageMergeProgress>? progress, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[4 << 20];
        long copied = 0, reported = 0;
        await using var source = new FileStream(part.Path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (source.Length != part.Length)
            throw new IOException($"{part.Name} changed size since the check ({part.Length:N0} -> {source.Length:N0} bytes).");
        progress?.Report(new Ps5PackageMergeProgress("Merging", done, total, part.Name));
        int read;
        while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            copied += read;
            if (copied - reported >= 256L << 20)
            {
                reported = copied;
                progress?.Report(new Ps5PackageMergeProgress("Merging", done + copied, total, part.Name));
            }
        }
        if (copied != part.Length) throw new IOException($"{part.Name} ended early ({copied:N0} of {part.Length:N0} bytes).");
        return done + copied;
    }

    private static long Length(string path) => new FileInfo(path).Length;

    private static long? FreeSpace(string folder)
    {
        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));
            return string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal) ? null : new DriveInfo(root).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private sealed class FolderNameComparer : IEqualityComparer<(string Folder, string Name)>
    {
        public bool Equals((string Folder, string Name) x, (string Folder, string Name) y) =>
            string.Equals(x.Folder, y.Folder, StringComparison.OrdinalIgnoreCase) && string.Equals(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Folder, string Name) value) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(value.Folder), StringComparer.OrdinalIgnoreCase.GetHashCode(value.Name));
    }

    /// <summary>Reads byte ranges of the package the parts would make, as if they were already one file.</summary>
    private sealed class SpanReader : IDisposable
    {
        private readonly List<(long Start, long Length, string Path)> _spans = [];
        private readonly Dictionary<string, FileStream> _open = new(StringComparer.OrdinalIgnoreCase);

        public SpanReader(Ps5PackagePartSet set) : this(set.Resume is null ? set.Parts : set.Parts.Prepend(set.Resume)) { }

        public SpanReader(IEnumerable<Ps5PackagePart> parts)
        {
            long start = 0;
            foreach (Ps5PackagePart part in parts)
            {
                _spans.Add((start, part.Length, part.Path));
                start += part.Length;
            }
            Length = start;
        }

        public long Length { get; }
        public long LastLength => _spans.Count == 0 ? 0 : _spans[^1].Length;

        public byte[] Read(long offset, int count)
        {
            if (offset < 0 || offset + count > Length) throw new InvalidDataException("A read went past the end of the parts.");
            byte[] buffer = new byte[count];
            int filled = 0;
            foreach ((long start, long length, string path) in _spans)
            {
                if (filled == count) break;
                long position = offset + filled;
                if (position >= start + length || position < start) continue;
                int take = (int)Math.Min(count - filled, start + length - position);
                if (!_open.TryGetValue(path, out FileStream? stream))
                    _open[path] = stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
                stream.Position = position - start;
                stream.ReadExactly(buffer, filled, take);
                filled += take;
            }
            return buffer;
        }

        public void Dispose()
        {
            foreach (FileStream stream in _open.Values) stream.Dispose();
        }
    }
}
