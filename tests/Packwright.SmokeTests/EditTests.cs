using System.Diagnostics;
using System.Security.Cryptography;
using Packwright.App.Services;
using Packwright.Containers;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using UFS2Tool;

namespace Packwright.SmokeTests;

/// <summary>
/// Edit operations on every format: editing files inside exFAT and FFPKG images, writing edited details into
/// every format (folder, four images, package), and the repair / refresh / rebuild maintenance jobs.
/// Usage: SmokeTests edit-tests &lt;package&gt; &lt;reference manifest&gt; &lt;packwright.cmd&gt; &lt;work folder&gt;
/// </summary>
internal static class EditTests
{
    private static string _cli = string.Empty;
    private static Dictionary<string, (long Size, string Hash)> _ref = new();
    private static int _failures;

    public static async Task<int> Run(string[] args)
    {
        string pkg = args[1], manifest = args[2];
        _cli = args[3];
        string work = Path.GetFullPath(args[4]);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);

        foreach (string line in File.ReadLines(manifest))
        {
            string[] p = line.Split('|');
            if (p.Length == 3) _ref[p[0]] = (long.Parse(p[1]), p[2]);
        }
        string[] candidates = _ref.Where(f => !f.Key.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f.Value.Size).Select(f => f.Key).ToArray();
        string fileA = candidates[0], fileB = candidates[1];
        Console.WriteLine($"reference: {_ref.Count} files; edit targets: replace '{fileA}', delete '{fileB}'");

        Console.WriteLine("\n== 1. editing files inside images");
        foreach (string fmt in new[] { "exfat", "ffpkg" })
            await FileEditsAsync(pkg, work, fmt, fileA, fileB);

        Console.WriteLine("\n== 2. writing edited details into every format");
        foreach (string fmt in new[] { "folder", "exfat", "ffpkg", "ffpfsc", "zar", "pkg" })
            await DetailsAsync(pkg, work, fmt);

        Console.WriteLine("\n== 3. maintenance jobs");
        await MaintenanceAsync(pkg, work);

        Console.WriteLine($"\nEDIT TESTS DONE, {_failures} failure(s)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task FileEditsAsync(string pkg, string work, string fmt, string fileA, string fileB)
    {
        string label = $"edit files in {fmt}";
        try
        {
            string image = Path.Combine(work, "fe." + fmt);
            Cli($"convert \"{pkg}\" --to {fmt} -o \"{image}\"");
            string replacement = Path.Combine(work, "replacement.bin");
            File.WriteAllText(replacement, "REPLACED-CONTENT-" + fmt);
            string added = Path.Combine(work, "added.txt");
            File.WriteAllText(added, "ADDED-FILE-" + fmt);
            string tree = Path.Combine(work, "tree");
            Directory.CreateDirectory(Path.Combine(tree, "sub"));
            File.WriteAllText(Path.Combine(tree, "one.txt"), "tree-one");
            File.WriteAllText(Path.Combine(tree, "sub", "two.txt"), "tree-two");

            if (fmt == "exfat")
                await ExfatImageMaintenance.ApplyEditsAsync(image, new[]
                {
                    ExfatEditOperation.Replace(fileA, replacement),
                    ExfatEditOperation.AddFile("added/new.txt", added),
                    ExfatEditOperation.AddDirectoryTree("added/tree", tree),
                    ExfatEditOperation.AddDirectory("added/emptydir"),
                    ExfatEditOperation.Delete(fileB),
                });
            else
                await Ufs2Operations.ApplyEditsAsync(image, new[]
                {
                    Ufs2EditOperation.Replace(fileA, replacement),
                    Ufs2EditOperation.AddFile("added/new.txt", added),
                    Ufs2EditOperation.AddDirectoryTree("added/tree", tree),
                    Ufs2EditOperation.AddDirectory("added/emptydir"),
                    Ufs2EditOperation.Delete(fileB),
                });

            if (Cli($"verify \"{image}\"") != 0) { Fail(label, "verify failed after the edits"); return; }
            string outDir = Path.Combine(work, "fe-out");
            if (Cli($"extract \"{image}\" -o \"{outDir}\"") != 0) { Fail(label, "extract failed"); return; }

            var problems = new List<string>();
            if (Read(outDir, fileA) != "REPLACED-CONTENT-" + fmt) problems.Add("replaced file has the wrong content");
            if (Read(outDir, "added/new.txt") != "ADDED-FILE-" + fmt) problems.Add("added file is wrong or missing");
            if (Read(outDir, "added/tree/one.txt") != "tree-one" || Read(outDir, "added/tree/sub/two.txt") != "tree-two")
                problems.Add("added folder tree is wrong or missing");
            if (!Directory.Exists(Path.Combine(outDir, "added", "emptydir"))) problems.Add("new empty folder is missing");
            if (File.Exists(Path.Combine(outDir, fileB.Replace('/', Path.DirectorySeparatorChar)))) problems.Add("deleted file is still there");
            (int ok, int bad, int missing) = CompareTree(outDir, skip: [fileA, fileB]);
            if (bad > 0 || missing > 0) problems.Add($"{bad} other files changed, {missing} went missing");

            Report(label, problems, $"{ok} untouched files identical");
            Directory.Delete(outDir, true);
            File.Delete(image);
            Directory.Delete(tree, true);
        }
        catch (Exception ex) { Fail(label, ex.GetType().Name + ": " + ex.Message); }
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task DetailsAsync(string pkg, string work, string fmt)
    {
        string label = $"edit details of {fmt}";
        string source = Path.Combine(work, "ed." + fmt);
        try
        {
            string output = string.Empty;
            switch (fmt)
            {
                case "pkg":
                    source = Path.Combine(work, "ed-source.pkg");
                    File.Copy(pkg, source, true);
                    output = Path.Combine(work, "ed-edited.pkg");
                    break;
                case "folder":
                    string baseImage = Path.Combine(work, "ed-base.exfat");
                    Cli($"convert \"{pkg}\" --to exfat -o \"{baseImage}\"");
                    Cli($"extract \"{baseImage}\" -o \"{source}\"");
                    File.Delete(baseImage);
                    break;
                default:
                    Cli($"convert \"{pkg}\" --to {fmt} -o \"{source}\"");
                    break;
            }

            var edit = new MetadataEdit { Title = "Edited Title QA", Version = "01.77.000" };
            var progress = new Progress<PackageTaskProgress>(_ => { });
            await MetadataEditor.ApplyAsync(source, output, fromPackage: fmt == "pkg", edit,
                "00000000000000000000000000000000", "ppt", progress, CancellationToken.None);

            string edited = fmt == "pkg" ? output : source;
            var problems = new List<string>();
            var scan = await new Ps5LibraryScanner().ScanAsync([fmt == "folder" ? work : edited], recursive: true);
            var game = scan.Games.FirstOrDefault(g => g.RootPath.StartsWith(fmt == "folder" ? source : edited, StringComparison.OrdinalIgnoreCase));
            if (game is null) problems.Add("the edited file is not found by the scanner");
            else
            {
                if (game.Title != "Edited Title QA") problems.Add($"title is '{game.Title}'");
                if (!game.DisplayVersion.Contains("77")) problems.Add($"version is '{game.DisplayVersion}'");
            }
            if (fmt != "folder" && Cli($"verify \"{edited}\"") != 0) problems.Add("verify failed after the edit");

            string detail = "title and version changed";
            if (fmt != "pkg" || problems.Count == 0)
            {
                string outDir = Path.Combine(work, "ed-out");
                if (fmt == "folder") { Report(label, problems, detail); Directory.Delete(source, true); return; }
                if (Cli($"extract \"{edited}\" -o \"{outDir}\"") != 0) problems.Add("extract failed");
                else
                {
                    // everything except the rewritten param.json must be untouched
                    (int ok, int bad, int missing) = CompareTree(outDir, skip: ["sce_sys/param.json"]);
                    if (bad > 0) problems.Add($"{bad} other files changed");
                    detail += $", {ok} other files identical";
                    Directory.Delete(outDir, true);
                }
            }
            Report(label, problems, detail);
        }
        catch (Exception ex) { Fail(label, ex.GetType().Name + ": " + ex.Message); }
        finally
        {
            foreach (string path in new[] { source, Path.Combine(work, "ed-edited.pkg") })
            {
                try { if (File.Exists(path)) File.Delete(path); else if (Directory.Exists(path)) Directory.Delete(path, true); }
                catch (IOException) { }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------

    private static async Task MaintenanceAsync(string pkg, string work)
    {
        string exfat = Path.Combine(work, "mt.exfat"), ffpkg = Path.Combine(work, "mt.ffpkg");
        Cli($"convert \"{pkg}\" --to exfat -o \"{exfat}\"");
        Cli($"convert \"{pkg}\" --to ffpkg -o \"{ffpkg}\"");
        await Maintenance("repair exFAT", exfat, () => ExfatImageMaintenance.RepairAsync(exfat));
        await Maintenance("refresh exFAT AMPR index", exfat, () => ExfatAmprPatcher.RefreshAsync(exfat));
        await Maintenance("rebuild FFPKG metadata", ffpkg, () => Ufs2Operations.RebuildWithEditsAsync(ffpkg, static _ => { }));
        File.Delete(exfat);
        File.Delete(ffpkg);
    }

    private static async Task Maintenance(string label, string image, Func<Task> action)
    {
        try
        {
            await action();
            var problems = new List<string>();
            if (Cli($"verify \"{image}\"") != 0) problems.Add("verify failed afterwards");
            string outDir = Path.Combine(Path.GetDirectoryName(image)!, "mt-out");
            if (Cli($"extract \"{image}\" -o \"{outDir}\"") != 0) problems.Add("extract failed afterwards");
            else
            {
                (int ok, int bad, int missing) = CompareTree(outDir, skip: []);
                if (bad > 0 || missing > 0) problems.Add($"{bad} files changed, {missing} missing");
                Report(label, problems, $"{ok} files identical");
                Directory.Delete(outDir, true);
                return;
            }
            Report(label, problems, string.Empty);
        }
        catch (Exception ex) { Fail(label, ex.GetType().Name + ": " + ex.Message); }
    }

    // ---------------------------------------------------------------------------------------------

    private static (int Ok, int Bad, int Missing) CompareTree(string dir, string[] skip)
    {
        int ok = 0, bad = 0, missing = 0;
        foreach ((string key, (long size, string hash)) in _ref)
        {
            if (skip.Contains(key)) continue;
            string path = Path.Combine(dir, key.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path)) { missing++; continue; }
            using FileStream s = File.OpenRead(path);
            if (s.Length == size && Convert.ToHexString(SHA256.HashData(s)) == hash) ok++; else bad++;
        }
        return (ok, bad, missing);
    }

    private static string? Read(string dir, string relative)
    {
        string path = Path.Combine(dir, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    private static int Cli(string arguments)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c \"\"{_cli}\" {arguments}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        using Process p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    }

    private static void Report(string label, List<string> problems, string detail)
    {
        if (problems.Count == 0) Console.WriteLine($"  PASS  {label}: {detail}");
        else Fail(label, string.Join("; ", problems));
    }

    private static void Fail(string label, string why)
    {
        _failures++;
        Console.WriteLine($"  FAIL  {label}: {why}");
    }
}
