using System.Diagnostics;
using System.Security.Cryptography;
using Packwright.Pkg.Reader;

// Smoke tests for the Packwright package engine.
//   SmokeTests pkg-compare <package> <reference manifest>   hash every file in a package and compare it with
//                                                           a manifest of "path|size|sha256" lines
if (args.Length >= 2 && args[0] == "pkg-validate")
{
    using var p = PkgPackage.Open(args[1]);
    var sw0 = Stopwatch.StartNew();
    PkgValidationReport report = PkgValidator.Validate(p);
    foreach (PkgCheck c in report.Checks) Console.WriteLine($"  {c.Status,-7} {c.Name}: {c.Detail}");
    Console.WriteLine($"{(report.Passed ? "PASSED" : "FAILED")}{(report.HasWarnings ? " (with warnings)" : "")} in {sw0.Elapsed.TotalSeconds:0}s");
    return report.Passed ? 0 : 1;
}
if (args.Length == 4 && args[0] == "pkg-extract")
{
    // extract to a scratch folder, hash what was written, compare with the manifest, then delete the folder
    string dest = Path.Combine(args[2], "extract-" + Path.GetFileNameWithoutExtension(args[1]));
    if (Directory.Exists(dest)) Directory.Delete(dest, true);
    var sw1 = Stopwatch.StartNew();
    using (var p = PkgPackage.Open(args[1])) p.ExtractTo(dest);
    Console.WriteLine($"extracted in {sw1.Elapsed.TotalSeconds:0}s");
    var reference = File.ReadLines(args[3]).Select(l => l.Split('|')).Where(x => x.Length == 3).ToDictionary(x => x[0], x => (Size: long.Parse(x[1]), Hash: x[2]));
    int ok = 0, bad = 0;
    var onDisk = Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories).ToList();
    foreach (string f in onDisk)
    {
        string rel = Path.GetRelativePath(dest, f).Replace(Path.DirectorySeparatorChar, (char)47);
        using var s = File.OpenRead(f);
        if (reference.TryGetValue(rel, out var want) && want.Size == s.Length && want.Hash == Convert.ToHexString(SHA256.HashData(s))) ok++;
        else { bad++; Console.WriteLine($"  DIFF {rel}"); }
    }
    Console.WriteLine($"on disk: {onDisk.Count} files, reference: {reference.Count}; {ok} identical, {bad} different, {reference.Count - onDisk.Count} missing");
    Directory.Delete(dest, true);
    return bad == 0 && onDisk.Count == reference.Count ? 0 : 1;
}
if (args.Length == 2 && args[0] == "pkg-entries")
{
    using var p = PkgPackage.Open(args[1]);
    foreach (PkgEntry e in p.Entries)
    {
        string status;
        try
        {
            byte[] data = p.ReadEntry(e, "00000000000000000000000000000000");
            status = $"ok {data.Length} bytes, starts {Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)))}";
        }
        catch (Exception ex) { status = "FAILED: " + ex.Message; }
        Console.WriteLine($"  0x{e.Id:X4} {(e.IsEncrypted ? "protected" : "plain    ")} {status}");
    }
    return 0;
}
if (args.Length == 2 && args[0] == "pkg-entrydigests")
{
    using var p = PkgPackage.Open(args[1]);
    byte[] table = p.ReadEntry(p.Entries.First(x => x.Id == 0x0001));
    Console.WriteLine($"entry-digest table: {table.Length} bytes = {table.Length / 32.0} digests for {p.Entries.Count} entries");
    using var fs = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
    long cnt = p.CntOffset;
    for (int i = 0; i < p.Entries.Count; i++)
    {
        PkgEntry e = p.Entries[i];
        int plain = (int)e.Size, padded = (int)((e.Size + 15) & ~15u);
        string result = "";
        foreach (int len in new[] { plain, padded }.Distinct())
        {
            byte[] data = new byte[len]; fs.Position = cnt + e.Offset; fs.ReadExactly(data);
            byte[] h3 = LibProsperoPkg.Util.Crypto.Sha3_256(data), h2 = SHA256.HashData(data);
            for (int slot = 0; slot < table.Length / 32; slot++)
            {
                if (table.AsSpan(slot * 32, 32).SequenceEqual(h3)) result += $" SHA3(len={len})=slot{slot}";
                if (table.AsSpan(slot * 32, 32).SequenceEqual(h2)) result += $" SHA256(len={len})=slot{slot}";
            }
        }
        Console.WriteLine($"  [{i,2}] id=0x{e.Id:X4} enc={e.IsEncrypted,-5} size={e.Size,9}:{(result.Length == 0 ? " no match" : result)}");
    }
    return 0;
}
if (args.Length == 2 && args[0] == "fs-compare")
{
    var game = new Packwright.Core.Services.Ps5PkgGameReader().Read(args[1]);
    using var fsys = Packwright.Core.Services.GameFileSystem.Open(game, CancellationToken.None);
    using var pkg = PkgPackage.Open(args[1]);
    var viaFs = fsys.Files.ToDictionary(f => f.RelativePath, f => f, StringComparer.OrdinalIgnoreCase);
    var viaPkg = pkg.Files.ToDictionary(f => f.Path, f => f, StringComparer.OrdinalIgnoreCase);
    Console.WriteLine($"GameFileSystem: {viaFs.Count} files; reader: {viaPkg.Count} files");
    int missing = viaPkg.Keys.Count(k => !viaFs.ContainsKey(k));
    int extra = viaFs.Keys.Count(k => !viaPkg.ContainsKey(k));
    Console.WriteLine($"in reader but not in GameFileSystem: {missing}; in GameFileSystem but not in reader: {extra}");
    foreach (string k in viaPkg.Keys.Where(k => !viaFs.ContainsKey(k)).Take(8)) Console.WriteLine("  missing: " + k);
    foreach (string k in viaFs.Keys.Where(k => !viaPkg.ContainsKey(k)).Take(20)) Console.WriteLine("  extra:   " + k);
    int sizeDiff = viaFs.Where(p => viaPkg.TryGetValue(p.Key, out var f) && f.Size != p.Value.Size).Count();
    Console.WriteLine($"size differences: {sizeDiff}");
    return 0;
}
if (args.Length >= 2 && args[0] == "details")
{
    // what the app shows for a source: size, trophies, activities, executable, errors
    foreach (string source in args.Skip(1))
    {
        var scanner = new Packwright.Core.Services.Ps5LibraryScanner();
        var scan = await scanner.ScanAsync([source], recursive: true);
        foreach (var game in scan.Games)
        {
            var details = await new Packwright.Core.Services.Ps5DetailsLoader().LoadAsync(game);
            Console.WriteLine($"{game.Title} [{game.SourceKind}]: size {game.SourceSize:N0} bytes");
            Console.WriteLine($"  trophies: {(details.TrophySet is { } t ? $"{t.Trophies.Count} ({t.Title}, language {t.SelectedLanguage})" : "none")}");
            Console.WriteLine($"  activities: {(details.Uds is null ? "none" : "found")}; executable: {(details.Executable is null ? "none" : "found")}; files: {details.Files.TotalSize:N0} bytes");
            foreach (string error in details.Errors) Console.WriteLine("  error: " + error);
        }
    }
    return 0;
}
if (args.Length == 5 && args[0] == "edit-tests") return await Packwright.SmokeTests.EditTests.Run(args);
if (args.Length == 2 && args[0] == "ampr-refresh")
{
    var result = await Packwright.Containers.ExfatAmprPatcher.RefreshAsync(args[1]);
    Console.WriteLine("refreshed: " + result);
    return 0;
}
if (args.Length == 2 && args[0] == "ampr-with-marker")
{
    string marker = Path.Combine(Path.GetTempPath(), "ampr-marker.bin");
    File.WriteAllText(marker, "AMPR-MARKER-TEST");
    await Packwright.Containers.ExfatImageMaintenance.ApplyEditsAsync(args[1],
        [Packwright.Containers.ExfatEditOperation.AddFile("fakelib/libSceAmpr.sprx", marker)]);
    Console.WriteLine("marker added");
    var first = await Packwright.Containers.ExfatAmprPatcher.RefreshAsync(args[1]);
    Console.WriteLine("refresh 1: " + first);
    var second = await Packwright.Containers.ExfatAmprPatcher.RefreshAsync(args[1]);
    Console.WriteLine("refresh 2 (should change nothing): " + second);
    File.Delete(marker);
    return 0;
}
if (args.Length >= 2 && args[0] == "pkg-structure")
{
    using var p = PkgPackage.Open(args[1], args.Length > 2 ? args[2] : null);
    Console.Write(p.DescribeStructure());
    return 0;
}
if (args.Length >= 2 && args[0] == "pkg-inner")
{
    using var p = PkgPackage.Open(args[1], args.Length > 2 ? args[2] : null);
    Console.Write(p.DescribeInnerFilesystem());
    return 0;
}
if (args.Length >= 4 && args[0] == "pkg-range")
{
    using var p = PkgPackage.Open(args[1]);
    long off = Convert.ToInt64(args[2], 16); int len = int.Parse(args[3]);
    byte[] data = p.ReadInnerRange(off, len);
    for (int i = 0; i < data.Length; i += 32)
        Console.WriteLine($"{off + i:X8}: {Convert.ToHexString(data.AsSpan(i, Math.Min(32, data.Length - i)))}  {new string(data.Skip(i).Take(32).Select(b => b is >= 32 and < 127 ? (char)b : '.').ToArray())}");
    return 0;
}
if (args.Length == 2 && args[0] == "fpt-check")
{
    using var p = PkgPackage.Open(args[1]);
    var (mount, sb) = p.DebugMount();
    byte[] TableOf(int inode)
    {
        byte[] r = InnerFileTreeDiag.Raw(mount, sb, inode);
        long size = BitConverter.ToInt64(r, 8), where = (long)BitConverter.ToUInt64(r, 0x60);
        byte[] data = new byte[size]; mount.Position = where; mount.ReadExactly(data); return data;
    }
    var files = p.Files.Select(f => f.Path).ToList();
    var dirs = files.SelectMany(f => { var parts = f.Split('/'); return Enumerable.Range(1, parts.Length - 1).Select(n => string.Join('/', parts.Take(n))); }).Distinct().ToList();
    foreach (int inode in new[] { 1, 2 })
    {
        var table = Packwright.Pkg.Writer.FlatPathTable.Read(TableOf(inode));
        var hashes = table.Select(e => e.Hash).ToHashSet();
        int fileHits = files.Count(f => hashes.Contains(Packwright.Pkg.Writer.FlatPathTable.HashPath(f)));
        int dirHits = dirs.Count(d => hashes.Contains(Packwright.Pkg.Writer.FlatPathTable.HashPath(d)));
        var known = files.Concat(dirs).Select(Packwright.Pkg.Writer.FlatPathTable.HashPath).ToHashSet();
        int unexplained = table.Count(e => !known.Contains(e.Hash));
        Console.WriteLine($"inode {inode}: {table.Count} entries; files matched {fileHits}/{files.Count}; directories matched {dirHits}/{dirs.Count}; entries with no matching path {unexplained}");
        foreach (var e in table.Take(3)) Console.WriteLine($"   hash 0x{e.Hash:X16} packed 0x{e.Packed:X16}");
    }
    return 0;
}
if (args.Length == 2 && args[0] == "plan-check")
{
    using var p = PkgPackage.Open(args[1]);
    var (mount, sb) = p.DebugMount();
    var specs = new List<Packwright.Pkg.Writer.InnerFile>();
    long placed = 0;
    foreach (var f in p.Files.OrderBy(f => f.LogicalOffset))
    {
        if (f.LogicalOffset > placed) specs.Add(Packwright.Pkg.Writer.InnerFile.Hole(f.LogicalOffset - placed));
        byte[] raw = InnerFileTreeDiag.Raw(mount, sb, f.Inode);
        specs.Add(new Packwright.Pkg.Writer.InnerFile(f.Path, f.Size, (BitConverter.ToUInt32(raw, 4) & 0x40) != 0));
        placed = f.LogicalOffset + f.Size;
    }
    Console.WriteLine($"holes in the data region: {specs.Count(s => s.IsHole)}");
    byte[] sbHead = new byte[0x100]; mount.Position = sb; mount.ReadExactly(sbHead);
    long stamp = BitConverter.ToInt64(sbHead, 0x68); uint stampNs = BitConverter.ToUInt32(sbHead, 0x88);
    Console.WriteLine($"build time in the original: {stamp} s + {stampNs} ns");
    var plan = Packwright.Pkg.Writer.InnerImagePlanner.Plan(specs, p.Directories, stamp, stampNs);

    Console.WriteLine($"superblock at 0x{plan.MetadataOffset:X} (original 0x{sb:X}); mount size 0x{plan.MountSize:X} (original 0x{mount.Length:X}); data end 0x{plan.DataEnd:X}");
    Console.WriteLine("planned first files: " + string.Join(" | ", plan.Files.Take(6).Select(f => f.Path + " @0x" + f.LogicalOffset.ToString("X") + " (" + f.Size + ")")));
    Console.WriteLine("original first files: " + string.Join(" | ", p.Files.OrderBy(f => f.LogicalOffset).Take(6).Select(f => f.Path + " @0x" + f.LogicalOffset.ToString("X") + " (" + f.Size + ")")));
    Console.WriteLine("original last files: " + string.Join(" | ", p.Files.OrderBy(f => f.LogicalOffset).TakeLast(3).Select(f => f.Path + " @0x" + f.LogicalOffset.ToString("X") + " (" + f.Size + ")")));
    int offsetMismatch = 0;
    var actual = p.Files.ToDictionary(f => f.Path, f => f.LogicalOffset);
    foreach (var f in plan.Files) if (actual[f.Path] != f.LogicalOffset) offsetMismatch++;
    Console.WriteLine($"file offsets: {plan.Files.Count - offsetMismatch} of {plan.Files.Count} match the original");

    byte[] original = new byte[plan.Metadata.Length];
    mount.Position = plan.MetadataOffset;
    int got = mount.Read(original, 0, original.Length);
    Console.WriteLine($"metadata: planned {plan.Metadata.Length} bytes, read {got} from the original");
    int differing = 0, firstDiff = -1;
    for (int i = 0; i < Math.Min(got, plan.Metadata.Length); i++)
        if (original[i] != plan.Metadata[i]) { differing++; if (firstDiff < 0) firstDiff = i; }
    Console.WriteLine(differing == 0 && got == plan.Metadata.Length
        ? "METADATA IDENTICAL, byte for byte"
        : $"metadata differs in {differing} bytes, first at 0x{firstDiff:X} (block {firstDiff / 0x10000}, offset in block 0x{firstDiff % 0x10000:X})");
    if (firstDiff >= 0)
    {
        int s = Math.Max(0, firstDiff - 16) & ~15;
        Console.WriteLine($"  original: {Convert.ToHexString(original, s, 48)}");
        Console.WriteLine($"  planned:  {Convert.ToHexString(plan.Metadata, s, 48)}");
    }
    return differing == 0 ? 0 : 1;
}
if (args.Length == 2 && args[0] == "pkg-order")
{
    using var p = PkgPackage.Open(args[1]);
    var ordered = p.Files.OrderBy(f => f.LogicalOffset).ToList();
    string last = "";
    for (int i = 0; i < ordered.Count; i++)
    {
        string top = ordered[i].Path.Contains('/') ? ordered[i].Path[..ordered[i].Path.IndexOf('/')] : "(root file)";
        if (top != last || i < 3) { Console.WriteLine($"{i,5}: {ordered[i].Path}"); last = top; }
    }
    Console.WriteLine("total " + ordered.Count);
    return 0;
}
if (args.Length == 4 && args[0] == "pkg-order-range")
{
    using var p = PkgPackage.Open(args[1]);
    var ordered = p.Files.OrderBy(f => f.LogicalOffset).ToList();
    for (int i = int.Parse(args[2]); i < Math.Min(ordered.Count, int.Parse(args[3])); i++) Console.WriteLine($"{i,5}: {ordered[i].Path} ({ordered[i].Size})");
    return 0;
}
if (args.Length == 2 && args[0] == "pkg-gaps")
{
    using var p = PkgPackage.Open(args[1]);
    var ordered = p.Files.OrderBy(f => f.LogicalOffset).ToList();
    long cursor = 0; int shown = 0; long totalGap = 0;
    for (int i = 0; i < ordered.Count; i++)
    {
        long gap = ordered[i].LogicalOffset - cursor;
        if (gap != 0)
        {
            totalGap += gap;
            if (shown++ < 12) Console.WriteLine($"{i,5}: gap of {gap} (0x{gap:X}) before {ordered[i].Path} @0x{ordered[i].LogicalOffset:X}; previous file ended at 0x{cursor:X} ({(i > 0 ? ordered[i - 1].Path : "")})");
        }
        cursor = ordered[i].LogicalOffset + ordered[i].Size;
    }
    Console.WriteLine($"gaps: {shown} shown; total padding {totalGap:N0} bytes; data end 0x{cursor:X}");
    return 0;
}
if (args.Length == 2 && args[0] == "afid-check")
{
    using var p = PkgPackage.Open(args[1]);
    var (mount, sb) = p.DebugMount();
    byte[] r = InnerFileTreeDiag.Raw(mount, sb, 3);
    long size = BitConverter.ToInt64(r, 8), where = (long)BitConverter.ToUInt64(r, 0x60);
    byte[] data = new byte[size]; mount.Position = where; mount.ReadExactly(data);
    int n = (int)(size / 4);
    var values = Enumerable.Range(0, n).Select(i => BitConverter.ToInt32(data, i * 4)).ToArray();
    Console.WriteLine($"afid table: {n} entries; first value {values[0]}; files {p.Files.Count}; last 3: {string.Join(", ", values.TakeLast(3))}");
    var byAfid = values.Skip(1).Take(n - 3).ToList();
    var inodes = p.Files.Select(f => f.Inode).ToHashSet();
    for (int i = 0; i < byAfid.Count; i++)
        if (byAfid[i] < 0 || !inodes.Contains(byAfid[i])) Console.WriteLine($"  afid {i} -> {byAfid[i]} (not a file inode)");
    return 0;
}
if (args.Length == 4 && args[0] == "layout-dump")
{
    using var p = PkgPackage.Open(args[1]);
    var lay = p.ReadLayout();
    int from = int.Parse(args[2]), count = int.Parse(args[3]);
    Console.WriteLine($"header: files {lay.NumFiles} ublocks {lay.NumUBlocks} blocks {lay.Blocks.Count} u2c {lay.U2c.Count}");
    Console.WriteLine("fidx: " + string.Join(" ", lay.Files.Take(6).Select(f => $"0x{f.UncompressedOffset:X}/{f.Type:X2}")) + " ... " + string.Join(" ", lay.Files.TakeLast(4).Select(f => $"0x{f.UncompressedOffset:X}/{f.Type:X2}")));
    Console.WriteLine("u2c[0..3]: " + string.Join(" | ", lay.U2c.Take(4).Select(u => $"base {u.Base} deltas {string.Join(",", u.Deltas)}")));
    for (int i = from; i < Math.Min(lay.Blocks.Count, from + count); i++)
    {
        var b = lay.Blocks[i];
        Console.WriteLine(b.IsRunBase
            ? $"[{i,5}] RUN   coffEnd=0x{b.CoffsetEndMod256K:X5} tweak={b.TweakIdxStart} coffStart256K=0x{b.CoffsetStart256K:X}"
            : $"[{i,5}] block coffStart=0x{b.CoffsetStartMod256K:X5} uoff=0x{b.UoffsetStart:X5} clenEven-1={b.ClenEvenMinus1} even={b.Even} odd={b.Odd} kde={b.KdePredictor} shuffle={b.ShuffleIdx}{(b.IsTerminal ? " TERMINAL" : "")}");
    }
    return 0;
}
if (args.Length == 2 && args[0] == "outer-block-check")
{
    using var p = PkgPackage.Open(args[1]);
    var records = p.ReadOuterBlockRecords();
    Console.WriteLine($"{records.Length} outer-block records; first: {string.Join(" ", records.Take(4).Select(r => Convert.ToHexString(r)))}");
    int sha2 = 0, sha3 = 0, crcAny = 0, n = Math.Min(records.Length, 64);
    for (int i = 0; i < n; i++)
    {
        byte[] block = p.ReadStoredRange((long)i * 0x10000, 0x10000);
        byte[] h2 = SHA256.HashData(block), h3 = LibProsperoPkg.Util.Crypto.Sha3_256(block);
        if (h2.AsSpan(0, 8).SequenceEqual(records[i]) || h2.AsSpan(24, 8).SequenceEqual(records[i])) sha2++;
        if (h3.AsSpan(0, 8).SequenceEqual(records[i]) || h3.AsSpan(24, 8).SequenceEqual(records[i])) sha3++;
        if (BitConverter.ToUInt64(records[i]) == 0) crcAny++;
    }
    Console.WriteLine($"first {n} blocks: SHA-256 prefix/suffix matches {sha2}; SHA3-256 matches {sha3}; all-zero records {crcAny}");
    Console.WriteLine("distinct values among all records: " + records.Select(r => Convert.ToHexString(r)).Distinct().Count());
    return 0;
}
if (args.Length == 2 && args[0] == "layout-check")
{
    using var p = PkgPackage.Open(args[1]);
    var lay = p.ReadLayout();
    int groupStartsWithRun = 0, groups = lay.U2c.Count;
    for (int g = 0; g < groups; g++)
    {
        uint first = lay.U2c[g].Base;
        if (first > 0 && first <= lay.Blocks.Count && lay.Blocks[(int)first - 1].IsRunBase) groupStartsWithRun++;
    }
    var groupFirsts = lay.U2c.Select(u => (int)u.Base).ToHashSet();
    int runs = 0, runsAtGroupStart = 0, runsElsewhere = 0, consecutiveRuns = 0;
    for (int i = 0; i < lay.Blocks.Count; i++)
    {
        if (!lay.Blocks[i].IsRunBase) continue;
        runs++;
        if (groupFirsts.Contains(i + 1)) runsAtGroupStart++; else runsElsewhere++;
        if (i > 0 && lay.Blocks[i - 1].IsRunBase) consecutiveRuns++;
    }
    Console.WriteLine($"u2c groups {groups}; group first-record preceded by a run base: {groupStartsWithRun}");
    Console.WriteLine($"run bases {runs}: at a group start {runsAtGroupStart}, elsewhere {runsElsewhere}, back to back {consecutiveRuns}");
    // where are the 'elsewhere' runs? show a few with the previous normal record's stored end vs this run's start
    int shown = 0;
    for (int i = 1; i < lay.Blocks.Count && shown < 6; i++)
    {
        if (!lay.Blocks[i].IsRunBase || groupFirsts.Contains(i + 1)) continue;
        var r = lay.Blocks[i];
        Console.WriteLine($"  elsewhere: record {i}: tweak {r.TweakIdxStart}, coffStart256K 0x{r.CoffsetStart256K:X}, coffEnd 0x{r.CoffsetEndMod256K:X}");
        shown++;
    }
    return 0;
}
if (args.Length == 4 && args[0] == "stored-range")
{
    using var p = PkgPackage.Open(args[1]);
    long off = Convert.ToInt64(args[2], 16); int len = int.Parse(args[3]);
    byte[] data = p.ReadStoredRange(off, len);
    for (int i = 0; i < data.Length; i += 16)
        Console.WriteLine($"{off + i:X}: {Convert.ToHexString(data.AsSpan(i, Math.Min(16, data.Length - i)))}");
    return 0;
}
if (args.Length == 2 && args[0] == "pkg-outer") { using var outerPkg = PkgPackage.Open(args[1]); Console.WriteLine(outerPkg.DescribeOuter()); return 0; }
if (args.Length == 3 && args[0] == "make-keys") return Packwright.SmokeTests.KeyTestTools.MakeKeys(args[1], args[2]);
if (args.Length == 2 && args[0] == "pkg-sealcheck") { using var sealPkg = PkgPackage.Open(args[1]); int failed = 0; foreach (var (name, ok, detail) in sealPkg.CheckContainerDigests()) { Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {name} {detail}"); if (!ok) failed++; } Console.WriteLine(failed == 0 ? "ALL SEALS MATCH" : failed + " MISMATCHES"); return failed == 0 ? 0 : 1; }
if (args.Length == 1 && args[0] == "sha3-selftest") return Packwright.SmokeTests.Sha3Tests.Run();
if (args.Length == 2 && args[0] == "layout-hist") return Packwright.SmokeTests.LayoutHistogram.Run(args[1]);
if (args.Length >= 3 && args[0] == "repack-meta") return Packwright.SmokeTests.RepackMetaTests.Run(args[1], args[2], args.Length > 3 ? args[3] : null);
if (args.Length >= 3 && args[0] == "repack") return Packwright.SmokeTests.RepackTests.Repack(args[1], args[2], args.Length > 3 && args[3] != "-" ? args[3] : null, null, args.Length > 4 && args[4] == "kraken");
if (args.Length == 2 && args[0] == "pkg-digests")
{
    using var p = PkgPackage.Open(args[1]);
    PkgEntry e = p.Entries.First(x => x.Id == 0x040A);
    byte[] table = p.ReadEntry(e);
    long blocks = p.PfsSize / 0x10000;
    Console.WriteLine($"imagedigs: {table.Length} bytes = {table.Length / 32.0} digests, outer blocks = {blocks}");
    using var fs = new FileStream(args[1], FileMode.Open, FileAccess.Read, FileShare.Read);
    byte[] block = new byte[0x10000];
    int sha2 = 0, sha3 = 0, checkedBlocks = (int)Math.Min(blocks, 64);
    for (int i = 0; i < checkedBlocks; i++)
    {
        fs.Position = p.PfsOffset + (long)i * 0x10000;
        fs.ReadExactly(block);
        byte[] want = table.AsSpan(i * 32, 32).ToArray();
        if (SHA256.HashData(block).AsSpan().SequenceEqual(want)) sha2++;
        if (LibProsperoPkg.Util.Crypto.Sha3_256(block).AsSpan().SequenceEqual(want)) sha3++;
    }
    Console.WriteLine($"first {checkedBlocks} blocks: SHA-256 matches {sha2}, SHA3-256 matches {sha3}");
    return 0;
}
if (args.Length == 3 && args[0] == "pkg-inode")
{
    using var p = PkgPackage.Open(args[1]);
    var (mount, sb) = p.DebugMount();
    foreach (PkgFile f in p.Files.Where(f => f.Path.Contains(args[2], StringComparison.OrdinalIgnoreCase)).Take(3))
    {
        Console.WriteLine($"{f.Path} size={f.Size} offset=0x{f.LogicalOffset:X} inode={f.Inode}");
        Console.WriteLine("  " + Convert.ToHexString(InnerFileTreeDiag.Raw(mount, sb, f.Inode)));
    }
    return 0;
}
if (args.Length == 3 && args[0] == "pkg-compare")
    return PkgCompare(args[1], args[2]);

Console.WriteLine("usage: SmokeTests pkg-compare <package> <reference manifest>");
return 2;

static int PkgCompare(string package, string manifestPath)
{
    var sw = Stopwatch.StartNew();
    using var pkg = PkgPackage.Open(package);
    Console.WriteLine($"{Path.GetFileName(package)}: content id {pkg.ContentId}, {pkg.Entries.Count} metadata entries");

    var reference = new Dictionary<string, (long Size, string Hash)>(StringComparer.Ordinal);
    foreach (string line in File.ReadLines(manifestPath))
    {
        string[] part = line.Split('|');
        if (part.Length == 3) reference[part[0]] = (long.Parse(part[1]), part[2]);
    }

    IReadOnlyList<PkgFile> files = pkg.Files;
    Console.WriteLine($"files in package: {files.Count}, in reference: {reference.Count}");

    int ok = 0, bad = 0, extra = 0;
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (PkgFile file in files)
    {
        seen.Add(file.Path);
        using Stream stream = pkg.OpenFile(file);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        if (!reference.TryGetValue(file.Path, out var expected)) { extra++; Console.WriteLine($"  EXTRA   {file.Path}"); }
        else if (expected.Size != file.Size || expected.Hash != hash)
        {
            bad++;
            Console.WriteLine($"  DIFF    {file.Path} size {file.Size} vs {expected.Size}, hash {(expected.Hash == hash ? "same" : "different")}");
        }
        else ok++;
    }
    int missing = 0;
    foreach (string path in reference.Keys.Where(p => !seen.Contains(p)))
    {
        missing++;
        Console.WriteLine($"  MISSING {path}");
    }

    Console.WriteLine($"result: {ok} identical, {bad} different, {extra} extra, {missing} missing ({sw.Elapsed.TotalSeconds:0}s)");
    return bad + extra + missing == 0 ? 0 : 1;
}
