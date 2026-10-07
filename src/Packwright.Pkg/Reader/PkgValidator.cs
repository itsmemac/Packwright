// Packwright.Pkg - integrity checks for PS5 packages.
//
// Everything here works without any key: structure, the package's own table of per-entry digests, the layout
// of the inner image, and a full decode of every file. Packages whose contents are encrypted with a key we do
// not have report a warning for the checks that cannot run instead of a failure.
#nullable enable
using LibProsperoPkg.Util;

namespace Packwright.Pkg.Reader;

public enum PkgCheckStatus { Pass, Warning, Fail }

public sealed record PkgCheck(string Name, PkgCheckStatus Status, string Detail);

public sealed class PkgValidationReport
{
    public required IReadOnlyList<PkgCheck> Checks { get; init; }

    /// <summary>True when no check failed (warnings are allowed).</summary>
    public bool Passed => Checks.All(c => c.Status != PkgCheckStatus.Fail);
    public bool HasWarnings => Checks.Any(c => c.Status == PkgCheckStatus.Warning);
    public IEnumerable<PkgCheck> Failures => Checks.Where(c => c.Status == PkgCheckStatus.Fail);
}

public static class PkgValidator
{
    private const uint EntryDigestsId = 0x0001;
    private const int OuterBlockSize = 0x10000;

    /// <summary>
    /// Checks a package. With <paramref name="decodeContent"/> every file is decoded in full, which is the
    /// slow part (it reads the whole package); progress is reported in bytes of expanded content.
    /// </summary>
    public static PkgValidationReport Validate(PkgPackage pkg, bool decodeContent = true,
        IProgress<PkgExtractProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        var checks = new List<PkgCheck>();
        void Add(string name, PkgCheckStatus status, string detail) => checks.Add(new PkgCheck(name, status, detail));

        CheckHeader(pkg, Add);
        CheckEntries(pkg, Add);
        CheckEntryDigests(pkg, Add, cancellationToken);
        if (!CheckInner(pkg, Add, decodeContent, progress, cancellationToken))
            return new PkgValidationReport { Checks = checks };
        return new PkgValidationReport { Checks = checks };
    }

    private static void CheckHeader(PkgPackage pkg, Action<string, PkgCheckStatus, string> add)
    {
        var problems = new List<string>();
        if (pkg.ContentId.Length != 36) problems.Add($"the content id '{pkg.ContentId}' is not 36 characters");
        if (pkg.PfsSize <= 0 || pkg.PfsSize % OuterBlockSize != 0) problems.Add("the outer image size is not a multiple of 64 KB");
        if (pkg.PfsOffset + pkg.PfsSize > pkg.FileSize) problems.Add("the outer image extends past the end of the file");
        if (pkg.CntOffset < pkg.PfsOffset + pkg.PfsSize) problems.Add("the metadata container overlaps the outer image");
        if (pkg.SuperblockOffset < pkg.PfsOffset || pkg.SuperblockOffset >= pkg.PfsOffset + pkg.PfsSize)
            problems.Add("the outer superblock lies outside the outer image");
        add("Header", problems.Count == 0 ? PkgCheckStatus.Pass : PkgCheckStatus.Fail,
            problems.Count == 0 ? $"{pkg.ContentId}, {(pkg.IsRetail ? "retail" : "debug")} package" : string.Join("; ", problems));
    }

    private static void CheckEntries(PkgPackage pkg, Action<string, PkgCheckStatus, string> add)
    {
        var bad = pkg.Entries.Where(e => pkg.CntOffset + e.Offset + e.Size > pkg.FileSize).Select(e => $"0x{e.Id:X4}").ToList();
        add("Metadata entries", bad.Count == 0 ? PkgCheckStatus.Pass : PkgCheckStatus.Fail,
            bad.Count == 0 ? $"{pkg.Entries.Count} entries, all inside the file" : "entries extend past the end of the file: " + string.Join(", ", bad));
    }

    // The container carries one SHA3-256 per entry (table entry 0x0001, slot = entry position; slot 0 is the
    // table itself). Protected entries are hashed as stored, padded to a 16-byte boundary.
    private static void CheckEntryDigests(PkgPackage pkg, Action<string, PkgCheckStatus, string> add, CancellationToken ct)
    {
        int tableIndex = -1;
        for (int i = 0; i < pkg.Entries.Count; i++)
            if (pkg.Entries[i].Id == EntryDigestsId) { tableIndex = i; break; }
        if (tableIndex < 0) { add("Metadata digests", PkgCheckStatus.Warning, "the package has no digest table to check"); return; }

        byte[] table;
        try { table = pkg.ReadEntry(pkg.Entries[tableIndex]); }
        catch (Exception ex) when (ex is NotSupportedException or InvalidDataException or IOException)
        { add("Metadata digests", PkgCheckStatus.Warning, "the digest table cannot be read: " + ex.Message); return; }
        if (table.Length < 32L * pkg.Entries.Count)
        { add("Metadata digests", PkgCheckStatus.Fail, "the digest table is shorter than the entry table"); return; }

        using var file = new FileStream(pkg.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1024 * 1024, FileOptions.SequentialScan);
        var mismatches = new List<string>();
        int checkedCount = 0;
        for (int i = 0; i < pkg.Entries.Count; i++)
        {
            if (i == tableIndex) continue;
            ct.ThrowIfCancellationRequested();
            PkgEntry e = pkg.Entries[i];
            long length = e.IsEncrypted ? (e.Size + 15) & ~15L : e.Size;
            if (pkg.CntOffset + e.Offset + length > pkg.FileSize) continue; // reported by the entry check
            byte[] hash = Crypto.Sha3_256(file, pkg.CntOffset + e.Offset, length);
            checkedCount++;
            if (!table.AsSpan(i * 32, 32).SequenceEqual(hash)) mismatches.Add($"0x{e.Id:X4}");
        }
        add("Metadata digests", mismatches.Count == 0 ? PkgCheckStatus.Pass : PkgCheckStatus.Fail,
            mismatches.Count == 0 ? $"{checkedCount} entries match their digests" : "entries do not match their digests: " + string.Join(", ", mismatches));
    }

    private static bool CheckInner(PkgPackage pkg, Action<string, PkgCheckStatus, string> add, bool decodeContent,
        IProgress<PkgExtractProgress>? progress, CancellationToken ct)
    {
        IReadOnlyList<PkgFile> files;
        try { files = pkg.Files; }
        catch (PkgNotSupportedException ex)
        {
            add("Contents", PkgCheckStatus.Warning, "the contents cannot be checked: " + ex.Message);
            return false;
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or EndOfStreamException or ArgumentException or InvalidOperationException)
        {
            add("Contents", PkgCheckStatus.Fail, "the inner image cannot be read: " + ex.Message);
            return false;
        }

        long total = files.Sum(f => f.Size);
        long mount = pkg.InnerImageSize;
        var outside = files.Where(f => f.LogicalOffset < 0 || f.Size < 0 || f.LogicalOffset + f.Size > mount).Select(f => f.Path).ToList();
        var unsafePaths = files.Where(f => f.Path.StartsWith('/') || f.Path.Split('/').Contains("..")).Select(f => f.Path).ToList();
        if (outside.Count > 0 || unsafePaths.Count > 0)
            add("File table", PkgCheckStatus.Fail,
                (outside.Count > 0 ? $"{outside.Count} files lie outside the image (first: {outside[0]}). " : "") +
                (unsafePaths.Count > 0 ? $"{unsafePaths.Count} paths are unsafe (first: {unsafePaths[0]})." : ""));
        else
            add("File table", PkgCheckStatus.Pass, $"{files.Count:N0} files, {total:N0} bytes");

        if (!decodeContent) return true;

        long done = 0;
        var failures = new List<string>();
        byte[] buffer = new byte[1024 * 1024];
        foreach (PkgFile file in files)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using Stream input = pkg.OpenFile(file);
                long read = 0;
                int n;
                while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    read += n;
                    done += n;
                    progress?.Report(new PkgExtractProgress(done, total, file.Path));
                }
                if (read != file.Size) failures.Add($"{file.Path} (ended after {read:N0} of {file.Size:N0} bytes)");
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or EndOfStreamException)
            {
                failures.Add($"{file.Path} ({ex.Message})");
                if (failures.Count >= 20) break;
            }
        }
        add("Content", failures.Count == 0 ? PkgCheckStatus.Pass : PkgCheckStatus.Fail,
            failures.Count == 0 ? $"every file decodes ({done:N0} bytes)" : $"{failures.Count} files cannot be decoded; first: {failures[0]}");
        return true;
    }
}
