using System.Security.Cryptography;
using Packwright.App.Services;
using Packwright.Core.Builders;
using Packwright.Core.Services;
using Packwright.Core.Tasks;

namespace Packwright.SmokeTests;

/// <summary>
/// Merging a package that was downloaded in parts. Builds a small package from a dump of its own, cuts it the way
/// PlayStation downloads are cut (numbered slices, and the metadata container as a separate _sc part), and merges it back.
/// Usage: SmokeTests merge-parts &lt;work folder&gt;
/// </summary>
internal static class MergePartsTests
{
    private static int _failures;
    private const string Name = "UP9999-PPSA99996_00-MERGEPARTSTEST00";

    public static async Task<int> Run(string[] args)
    {
        string work = Path.GetFullPath(args[1]);
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        try
        {
            string package = await BuildAsync(work, "a", 5);
            string other = await BuildAsync(work, "b", 6);
            byte[] original = File.ReadAllBytes(package);
            string originalHash = Hash(original);
            long container = BitConverter.ToInt64(original, 0x58);
            Console.WriteLine($"package {original.Length:N0} bytes, container at {container:N0}");

            Console.WriteLine("\n== finding and checking parts");
            string parts = Cut(original, Path.Combine(work, "parts"), 1 << 20, separateContainer: true);
            Ps5PackagePartSet set = Ps5PackageParts.FindSet(Path.Combine(parts, Name + "_3.pkg"))!;
            Check("a part file finds its set", set is not null && set.Parts.Count == CountParts(parts));
            Check("the _sc part is last", set!.Parts[^1].IsContainer && set.Parts[0].Number == 0);
            Check("the folder finds the same set", Ps5PackageParts.FindSet(parts)?.Count == set.Count);
            Ps5PackagePartCheck check = Ps5PackageParts.Check(set);
            Check($"the parts check out ({string.Join(", ", check.Passed)})", check.Ok && check.Passed.Count == 4);

            Console.WriteLine("\n== merge to a new file");
            string merged = Path.Combine(work, "merged.pkg");
            Ps5PackagePartCheck after = await Ps5PackageParts.MergeAsync(set, merged, Ps5PackageMergeMode.NewFile, false);
            Check("the merged file is identical to the original", after.Ok && Hash(File.ReadAllBytes(merged)) == originalHash);
            Check("the parts are kept", CountParts(parts) == set.Parts.Count);
            Check("no temporary file is left", !Directory.EnumerateFiles(work, "*.tmp").Any());
            Check("an existing output is not replaced without overwrite",
                await FailureOf(() => Ps5PackageParts.MergeAsync(set, merged, Ps5PackageMergeMode.NewFile, false)) is { } exists && exists.Contains("already exists"));
            Check("merged package verifies (job)", await RunVerify(merged) is null);

            Console.WriteLine("\n== no _sc part (the container is inside the last part)");
            string whole = Cut(original, Path.Combine(work, "whole"), 1 << 20, separateContainer: false);
            Ps5PackagePartSet wholeSet = Ps5PackageParts.FindSet(whole)!;
            string mergedWhole = Path.Combine(work, "whole.pkg");
            await Ps5PackageParts.MergeAsync(wholeSet, mergedWhole, Ps5PackageMergeMode.NewFile, false);
            Check("merges to the original", Hash(File.ReadAllBytes(mergedWhole)) == originalHash);

            Console.WriteLine("\n== problems are found before anything is written");
            string missing = Cut(original, Path.Combine(work, "missing"), 1 << 20, true);
            File.Delete(Path.Combine(missing, Name + "_2.pkg"));
            Check("a missing part", Ps5PackageParts.Check(Ps5PackageParts.FindSet(missing)!).Message.Contains("Part 2 is missing"));
            string noSc = Cut(original, Path.Combine(work, "nosc"), 1 << 20, true);
            File.Delete(Path.Combine(noSc, Name + "_sc.pkg"));
            Check("a missing _sc part", Ps5PackageParts.Check(Ps5PackageParts.FindSet(noSc)!).Message.Contains("_sc.pkg part is missing"));
            string shortPart = Cut(original, Path.Combine(work, "short"), 1 << 20, true);
            using (var stream = new FileStream(Path.Combine(shortPart, Name + "_1.pkg"), FileMode.Open)) stream.SetLength(stream.Length - 4096);
            Check("a part that did not finish downloading", Ps5PackageParts.Check(Ps5PackageParts.FindSet(shortPart)!).Message.Contains("short"));
            string mixed = Cut(original, Path.Combine(work, "mixed"), 1 << 20, true);
            byte[] otherBytes = File.ReadAllBytes(other);
            File.WriteAllBytes(Path.Combine(mixed, Name + "_0.pkg"), otherBytes.AsSpan(0, 1 << 20).ToArray());
            Check("part 0 from another build", !Ps5PackageParts.Check(Ps5PackageParts.FindSet(mixed)!).Ok);
            Check("a failed check writes nothing",
                await FailureOf(() => Ps5PackageParts.MergeAsync(Ps5PackageParts.FindSet(missing)!, Path.Combine(work, "never.pkg"), Ps5PackageMergeMode.NewFile, false)) is not null &&
                !File.Exists(Path.Combine(work, "never.pkg")));

            Console.WriteLine("\n== a package joined in the wrong order (the _sc part right after part 0)");
            string wrong = Path.Combine(work, "wrong.pkg");
            using (var output = File.Create(wrong))
                foreach (Ps5PackagePart part in set.Parts.Take(1).Concat(set.Parts.Where(p => p.IsContainer)).Concat(set.Parts.Skip(1).Where(p => !p.IsContainer)))
                    output.Write(File.ReadAllBytes(part.Path));
            Ps5PackagePartCheck wrongCheck = Ps5PackageParts.CheckMerged(wrong);
            Check("is found by the check", !wrongCheck.Ok && wrongCheck.Message.Contains("wrong order"));
            Check("and by Verify (job)", await RunVerify(wrong) is { } verifyMessage && verifyMessage.Contains("did not pass"));

            Console.WriteLine("\n== merge in place");
            string inPlace = Cut(original, Path.Combine(work, "inplace"), 1 << 20, true);
            Ps5PackagePartSet inPlaceSet = Ps5PackageParts.FindSet(inPlace)!;
            string inPlaceOutput = Path.Combine(inPlace, Name + ".pkg");
            await Ps5PackageParts.MergeAsync(inPlaceSet, inPlaceOutput, Ps5PackageMergeMode.InPlace, false);
            Check("the merged file is identical to the original", Hash(File.ReadAllBytes(inPlaceOutput)) == originalHash);
            Check("only the merged package is left", Directory.GetFiles(inPlace).Length == 1);

            Console.WriteLine("\n== an interrupted merge in place carries on");
            string resume = Cut(original, Path.Combine(work, "resume"), 1 << 20, true);
            string backup = Cut(original, Path.Combine(work, "resume-backup"), 1 << 20, true);
            Ps5PackagePartSet resumeSet = Ps5PackageParts.FindSet(resume)!;
            string resumeOutput = Path.Combine(resume, Name + ".pkg");
            using (var stop = new CancellationTokenSource())
            {
                var progress = new SyncProgress<Ps5PackageMergeProgress>(value => { if (value.Part == Name + "_3.pkg") stop.Cancel(); });
                string? stopped = await FailureOf(() => Ps5PackageParts.MergeAsync(resumeSet, resumeOutput, Ps5PackageMergeMode.InPlace, false, progress, stop.Token));
                Check("the first run stops", stopped is not null && !File.Exists(resumeOutput));
            }
            string workFile = Path.Combine(resume, Name + ".pkg.merging");
            Check("parts 1 and 2 are merged and deleted", File.Exists(workFile) && !File.Exists(Path.Combine(resume, Name + "_2.pkg")) && File.Exists(Path.Combine(resume, Name + "_3.pkg")));
            // A half-written part and a written part that was not deleted yet, as a power cut could leave them.
            File.AppendAllText(workFile, new string('x', 12345));
            File.Copy(Path.Combine(backup, Name + "_2.pkg"), Path.Combine(resume, Name + "_2.pkg"));
            Ps5PackagePartSet again = Ps5PackageParts.FindSet(Path.Combine(resume, Name + "_5.pkg"))!;
            Check("the interrupted merge is recognised", again.Resume is not null && Ps5PackageParts.Check(again).Ok);
            Check("a new-file merge is refused while it is unfinished",
                await FailureOf(() => Ps5PackageParts.MergeAsync(again, Path.Combine(work, "elsewhere.pkg"), Ps5PackageMergeMode.NewFile, false)) is { } refused && refused.Contains("interrupted"));
            await Ps5PackageParts.MergeAsync(again, resumeOutput, Ps5PackageMergeMode.InPlace, false);
            Check("the second run finishes it identically to the original", Hash(File.ReadAllBytes(resumeOutput)) == originalHash);
            Check("only the merged package is left", Directory.GetFiles(resume).Length == 1);

            Console.WriteLine("\n== the library and other files");
            string library = Cut(original, Path.Combine(work, "library"), 1 << 20, true);
            File.Copy(other, Path.Combine(library, "Some Game_1.pkg"));
            var scan = await new Ps5LibraryScanner().ScanAsync([library], recursive: true);
            Check("the parts are one title", scan.Games.Count(game => game.PackageParts > 0) == 1 &&
                                              scan.Games.Single(game => game.PackageParts > 0).SourceSize == original.Length);
            Check("a lone package ending in _1 is a package, not a part", scan.Games.Any(game => game.PackageParts == 0 && game.RootPath.EndsWith("Some Game_1.pkg")));
            Cut(otherBytes, library, 1 << 20, true, "UP9999-PPSA99995_00-SECONDPACKAGE000");
            Check("a folder with two split packages asks for a part", FailureOfSync(() => Ps5PackageParts.FindSet(library)) is { } two && two.Contains("2 packages"));
        }
        finally
        {
            try { Directory.Delete(work, true); } catch (IOException) { }
        }
        Console.WriteLine(_failures == 0 ? "\nALL PASSED" : $"\n{_failures} FAILED");
        return _failures == 0 ? 0 : 1;
    }

    /// <summary>A debug package built from a small dump, with enough data for several 1 MiB parts.</summary>
    private static async Task<string> BuildAsync(string work, string label, int seed)
    {
        string dump = Path.Combine(work, "dump-" + label);
        Directory.CreateDirectory(Path.Combine(dump, "sce_sys"));
        Directory.CreateDirectory(Path.Combine(dump, "data"));
        File.WriteAllText(Path.Combine(dump, "sce_sys", "param.json"),
            "{\"titleId\":\"PPSA99996\",\"contentId\":\"" + Name + "\",\"contentVersion\":\"01.000.000\",\"localizedParameters\":{\"defaultLanguage\":\"en-US\",\"en-US\":{\"titleName\":\"Merge Test\"}}}");
        var random = new Random(seed);
        foreach ((string file, int size) in new[] { ("eboot.bin", 70000), ("data/a.bin", 3 << 20), ("data/b.bin", 2 << 20) })
        {
            byte[] data = new byte[size];
            random.NextBytes(data);
            File.WriteAllBytes(Path.Combine(dump, file), data);
        }
        string output = Path.Combine(work, label + ".pkg");
        await Jobs.BuildPackageAsync(dump, output, Name, Ps5DebugPackageCredentials.DefaultPasscode, false,
            new PackageBuildSettings(Ps5InnerCompression.Stored), Jobs.ResolveBackend(null), new SyncProgress<PackageTaskProgress>(_ => { }), CancellationToken.None);
        return output;
    }

    /// <summary>Cuts a package into numbered parts of <paramref name="size"/> bytes, with the container as _sc when asked.</summary>
    private static string Cut(byte[] package, string folder, int size, bool separateContainer, string name = Name)
    {
        Directory.CreateDirectory(folder);
        long container = BitConverter.ToInt64(package, 0x58);
        long numberedEnd = separateContainer ? container : package.Length;
        int number = 0;
        for (long offset = 0; offset < numberedEnd; offset += size, number++)
            File.WriteAllBytes(Path.Combine(folder, $"{name}_{number}.pkg"), package.AsSpan((int)offset, (int)Math.Min(size, numberedEnd - offset)).ToArray());
        if (separateContainer) File.WriteAllBytes(Path.Combine(folder, $"{name}_sc.pkg"), package.AsSpan((int)container).ToArray());
        return folder;
    }

    private static int CountParts(string folder) => Directory.GetFiles(folder, Name + "_*.pkg").Length;

    private static async Task<string?> RunVerify(string path)
    {
        var spec = new JobSpec { Kind = JobSpec.Verify, Source = path, FromPackage = true };
        bool retail = Jobs.IsRetailOrPatchPackage(path);
        // The test package is a debug build: check it the way a retail one is checked, which is what merging relies on.
        if (!retail) return Ps5PackageParts.CheckMerged(path) is { Ok: false } bad ? "did not pass: " + bad.Message : null;
        return await FailureOf(() => spec.Executor()!(new SyncProgress<PackageTaskProgress>(_ => { }), CancellationToken.None));
    }

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data));

    private static async Task<string?> FailureOf(Func<Task> action)
    {
        try { await action(); return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { return ex.Message; }
    }

    private static string? FailureOfSync(Action action)
    {
        try { action(); return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException) { return ex.Message; }
    }

    private static void Check(string what, bool ok)
    {
        Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
        if (!ok) _failures++;
    }

    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
