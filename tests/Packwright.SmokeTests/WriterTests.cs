using System.Diagnostics;
using System.Security.Cryptography;
using Packwright.Pkg.Reader;
using Packwright.Pkg.Writer;

namespace Packwright.SmokeTests;

internal static class RepackTests
{
    /// <summary>Rebuilds a package from the files and metadata entries of another one, 1:1.</summary>
    public static int Repack(string source, string output, string? keyFile, string? passcode, bool kraken = false)
    {
        var sw = Stopwatch.StartNew();
        using var p = PkgPackage.Open(source);
        var files = new List<PkgSourceFile>();
        foreach (PkgFile f in p.Files)
        {
            PkgFile file = f;
            files.Add(new PkgSourceFile(f.Path, f.Size, () => p.OpenFile(file)));
        }
        var skip = new HashSet<uint> { 0x1, 0x10, 0x20, 0x80, 0x100, 0x200, 0x40A };
        foreach (PkgEntry e in p.Entries)
        {
            if (skip.Contains(e.Id)) continue;
            string name = PkgEntryTable.FileNameOf(e.Id) ?? $"entry_0x{e.Id:X4}.bin";
            byte[] data = e.IsEncrypted ? p.ReadEntry(e, PkgBuildOptions.DefaultPasscode) : p.ReadEntry(e);
            files.Add(new PkgSourceFile("sce_sys/" + name, data.Length, () => new MemoryStream(data)));
        }
        PkgKeySet keys = keyFile is null ? PkgKeySet.None : PkgKeySet.Load(keyFile);
        Console.WriteLine($"source: {files.Count} files/entries; keys: {keys.Describe()}");
        long last = -1;
        var progress = new Progress<PkgBuildProgress>(pr =>
        {
            long step = pr.Total > 0 ? pr.Done * 10 / pr.Total : 0;
            if (step != last) { last = step; Console.WriteLine($"  {pr.Stage} {(pr.Total > 0 ? pr.Done * 100 / pr.Total + "%" : "")}"); }
        });
        PkgBuildResult r = PkgBuilder.Build(files, p.Directories, output, new PkgBuildOptions
        {
            ContentId = p.ContentId, Passcode = passcode ?? PkgBuildOptions.DefaultPasscode, Keys = keys, Seed = new byte[16],
            Log = Console.WriteLine, Progress = progress,
            Compression = kraken ? PkgCompression.Kraken : PkgCompression.Stored,
        });
        Console.WriteLine($"built {r.OutputPath}: {r.PackageSize:N0} bytes, {r.InnerFiles} files, {r.MetadataEntries} entries in {sw.Elapsed.TotalSeconds:0.0}s");
        foreach (string w in r.Warnings) Console.WriteLine("  warning: " + w);
        return 0;
    }
}

internal static class KeyTestTools
{
    /// <summary>Builds a key file in Packwright's import format from the public parts of a library's data (test use only) and prints the fingerprints.</summary>
    public static int MakeKeys(string dir, string output)
    {
        byte[] pass = File.ReadAllBytes(Path.Combine(dir, "LibProsperoPkg.Keys.Data.passcode.bin"));
        byte[] mount = File.ReadAllBytes(Path.Combine(dir, "LibProsperoPkg.Keys.Data.mount_image.bin"));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(File.ReadAllText(Path.Combine(dir, "LibProsperoPkg.Keys.Data.pkg_meta_rsa_key.pem")));
        byte[] meta = rsa.ExportParameters(false).Modulus!;
        var lines = new List<string> { "# test key file" };
        for (int i = 0; i < 7; i++) lines.Add($"passcode{i}={Convert.ToHexString(pass.AsSpan(i * 384, 384))}");
        lines.Add($"mount-image={Convert.ToHexString(mount)}");
        lines.Add($"metadata={Convert.ToHexString(meta)}");
        File.WriteAllLines(output, lines);
        Console.WriteLine($"metadata length {meta.Length} fingerprint {Convert.ToHexString(SHA256.HashData(meta)).ToLowerInvariant()}");
        return 0;
    }
}

internal static class Sha3Tests
{
    public static int Run()
    {
        var rng = new Random(7);
        int bad = 0;
        foreach (int length in Enumerable.Range(0, 600).Concat([1000, 4096, 65536, 100000]))
        {
            byte[] data = new byte[length];
            rng.NextBytes(data);
            var k = new Packwright.Pkg.Util.Sha3.Keccak();
            int cut = length == 0 ? 0 : rng.Next(length);
            k.Append(data.AsSpan(0, cut)); k.Append(data.AsSpan(cut));
            byte[] managed = new byte[32];
            k.Finish(managed);
            if (!managed.AsSpan().SequenceEqual(SHA3_256.HashData(data))) { bad++; Console.WriteLine("mismatch at length " + length); }
        }
        Console.WriteLine(bad == 0 ? "SHA3 MANAGED OK" : bad + " mismatches");
        return bad == 0 ? 0 : 1;
    }
}

internal static class LayoutHistogram
{
    public static int Run(string package)
    {
        using var p = PkgPackage.Open(package);
        var layout = p.ReadLayout();
        var groups = layout.Blocks.Where(b => !b.IsRunBase && !b.IsTerminal)
            .GroupBy(b => $"even={b.Even} odd={b.Odd} kde={b.KdePredictor} shuffle={b.ShuffleIdx}")
            .OrderByDescending(g => g.Count());
        foreach (var g in groups) Console.WriteLine($"{g.Count(),8}  {g.Key}");
        return 0;
    }
}

internal static class RepackMetaTests
{
    public static int Run(string source, string output, string? keyFile)
    {
        var sw = Stopwatch.StartNew();
        using var p = PkgPackage.Open(source);
        PkgEntry param = p.Entries.First(e => e.Id == 0x2000);
        byte[] edited = Packwright.Core.Services.Ps5ParamJson.ApplyDrmType(p.ReadEntry(param), "upgradable");
        PkgKeySet keys = keyFile is null ? PkgKeySet.None : PkgKeySet.Load(keyFile);
        PkgBuildResult r = PkgRepackager.ReplaceEntries(p, output, new Dictionary<uint, byte[]> { [0x2000] = edited }, keys,
            PkgBuildOptions.DefaultPasscode, Console.WriteLine);
        Console.WriteLine($"rewrote {r.OutputPath}: {r.PackageSize:N0} bytes, {r.MetadataEntries} entries in {sw.Elapsed.TotalSeconds:0.0}s");
        return 0;
    }
}
