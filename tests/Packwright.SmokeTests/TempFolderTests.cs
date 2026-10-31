using Packwright.App.Services;
using Packwright.Containers;
using Packwright.Core.Services;
using Packwright.Core.Tasks;

namespace Packwright.SmokeTests;

/// <summary>
/// Where conversions and edits put their work files: the system temporary folder, a chosen folder, or next to the
/// output. Builds a small dump of its own, so no game is needed.
/// Usage: SmokeTests temp-folders &lt;work folder&gt;
/// </summary>
internal static class TempFolderTests
{
    private static int _failures;

    public static async Task<int> Run(string[] args)
    {
        string work = Path.GetFullPath(args[1]);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            string dump = MakeDump(Path.Combine(work, "dump"));
            string outputs = Path.Combine(work, "out");
            string exfat = Path.Combine(outputs, "game.exfat");
            TemporaryFolders.Configure(null, nextToOutput: false);
            await Ps5ImageConversionService.ConvertAsync(dump, exfat, Ps5ImageConversionTarget.Exfat);

            Console.WriteLine("== where a job folder is made");
            string chosen = Path.Combine(work, "chosen temp");
            string system = TemporaryFolders.Create("selftest", exfat);
            Check("empty uses the system temporary folder", system.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase));
            Directory.Delete(system);
            TemporaryFolders.Configure(chosen, nextToOutput: false);
            string inChosen = TemporaryFolders.Create("selftest", exfat);
            Check("a chosen folder is used", inChosen.StartsWith(chosen + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Directory.Delete(inChosen);
            TemporaryFolders.Configure(chosen, nextToOutput: true);
            string beside = TemporaryFolders.Create("selftest", exfat);
            Check("next to the output wins over the chosen folder",
                string.Equals(Path.GetDirectoryName(beside), outputs, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(beside).StartsWith(".Packwright-selftest-", StringComparison.Ordinal));
            Directory.Delete(beside);

            Console.WriteLine("\n== converting exFAT to ZArchive (unpacks the image first)");
            TemporaryFolders.Configure(chosen, nextToOutput: false);
            string zar = Path.Combine(outputs, "game.zar");
            await Ps5ImageConversionService.ConvertAsync(exfat, zar, Ps5ImageConversionTarget.ZArchive);
            Check("converts with a chosen folder", File.Exists(zar));
            Check("the chosen folder is kept", Directory.Exists(chosen));
            Check("the job's folder is removed", NoJobFolders(Path.Combine(chosen, "Packwright", "convert")));

            TemporaryFolders.Configure(chosen, nextToOutput: true);
            string zarBeside = Path.Combine(outputs, "game-beside.zar");
            await Ps5ImageConversionService.ConvertAsync(exfat, zarBeside, Ps5ImageConversionTarget.ZArchive);
            Check("converts with work files next to the output", File.Exists(zarBeside));
            Check("nothing is left next to the output",
                !Directory.EnumerateDirectories(outputs, ".Packwright-*").Any());

            Console.WriteLine("\n== a temporary folder that cannot be made");
            string blocker = Path.Combine(work, "a file");
            File.WriteAllText(blocker, "not a folder");
            TemporaryFolders.Configure(Path.Combine(blocker, "temp"), nextToOutput: false);
            string blocked = Path.Combine(outputs, "blocked.zar");
            string? message = await FailureOf(() => Ps5ImageConversionService.ConvertAsync(exfat, blocked, Ps5ImageConversionTarget.ZArchive));
            Check("the conversion fails instead of using the system drive", message?.Contains("temporary folder") == true);
            Check("no output is written", !File.Exists(blocked));
            message = await FailureOf(() => MetadataEditor.ApplyAsync(exfat, Path.Combine(outputs, "edited.exfat"), false,
                new MetadataEdit { Title = "Temp QA" }, "00000000000000000000000000000000", string.Empty,
                new Progress<PackageTaskProgress>(_ => { }), CancellationToken.None));
            Check("editing details fails the same way", message?.Contains("temporary folder") == true);

            Console.WriteLine("\n== a library scan while work files sit next to an output");
            string job = Path.Combine(outputs, ".Packwright-convert-0123456789abcdef");
            MakeDump(Path.Combine(job, "tree"));
            File.Copy(exfat, Path.Combine(job, "image.exfat"));
            var scan = await new Ps5LibraryScanner().ScanAsync([outputs], recursive: true);
            Check("the real outputs are found", scan.Games.Any(game => game.RootPath.EndsWith("game.exfat", StringComparison.OrdinalIgnoreCase)));
            Check("work folders are not listed as titles", !scan.Games.Any(game => game.RootPath.Contains(".Packwright-")));
        }
        finally
        {
            TemporaryFolders.Configure(null, nextToOutput: false);
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "\nALL PASSED" : $"\n{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    private static string MakeDump(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(root, "data"));
        File.WriteAllText(Path.Combine(root, "sce_sys", "param.json"),
            """{"titleId":"PPSA99999","contentId":"UP9999-PPSA99999_00-TEMPFOLDERTEST00","localizedParameters":{"defaultLanguage":"en-US","en-US":{"titleName":"Temp Folder Test"}}}""");
        var random = new Random(5);
        foreach (int size in new[] { 1 << 20, 3 << 20, 4096 })
        {
            byte[] data = new byte[size];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(root, "data", $"file{size}.bin"), data);
        }
        return root;
    }

    private static bool NoJobFolders(string folder) => !Directory.Exists(folder) || !Directory.EnumerateDirectories(folder).Any();

    private static async Task<string?> FailureOf(Func<Task> action)
    {
        try { await action(); return null; }
        catch (IOException ex) { return ex.Message; }
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }
}
