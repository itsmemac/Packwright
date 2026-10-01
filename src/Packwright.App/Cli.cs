using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Packwright.App.Models;
using Packwright.App.Services;
using Packwright.App.Views;
using Packwright.Containers;
using Packwright.Core.Backends;
using Packwright.Core.Builders;
using Packwright.Core.Models;
using Packwright.Core.Parsers;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using Packwright.Infrastructure;

namespace Packwright.App;

/// <summary>
/// Command-line mode: <c>Packwright info|scan|convert|extract|verify|health ...</c>. It runs the same jobs as the
/// window, without starting the interface, and never touches the network.
/// </summary>
internal static class Cli
{
    private const int Ok = 0, Failed = 1, Usage = 2, Cancelled = 130;

    private static readonly string[] Commands = ["info", "scan", "convert", "extract", "verify", "health", "send", "consoles", "version", "help"];

    // Options that take a value; everything else starting with '-' is a switch.
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "to", "o", "output", "preset", "passcode", "level", "gain", "cluster", "block", "fragment", "density", "min-free",
        "compression", "kraken-level", "threads", "playgo", "drm", "builder", "workspace", "format", "folder", "host", "port"
    };

    public static bool IsCommand(string[] args) =>
        args.Length > 0 && (Commands.Contains(args[0], StringComparer.OrdinalIgnoreCase) ||
                            args[0] is "--help" or "-h" or "/?" or "--version" or "-v" ||
                            // A bare word that is not a file or folder is a mistyped command, not something to open.
                            (!args[0].StartsWith('-') && args[0].IndexOfAny(['/', '\\', '.', ':']) < 0 &&
                             !File.Exists(args[0]) && !Directory.Exists(args[0])));

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);
    [DllImport("kernel32.dll")]
    private static extern nint GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    private static extern bool SetStdHandle(int handle, nint value);

    private sealed class Options
    {
        public List<string> Positional { get; } = [];
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> Switches { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Error { get; set; }

        public string? Get(params string[] names) => names.Select(name => Values.GetValueOrDefault(name)).FirstOrDefault(value => value is not null);
        public bool Has(string name) => Switches.Contains(name);
        public int? Int(string name) => Values.TryGetValue(name, out string? text) && int.TryParse(text, out int number) ? number : null;
    }

    private static Options Parse(string[] args)
    {
        var options = new Options();
        for (int index = 0; index < args.Length; index++)
        {
            string arg = args[index];
            if (arg == "--") { options.Positional.AddRange(args.Skip(index + 1)); break; }
            if (!arg.StartsWith('-') || arg.Length == 1) { options.Positional.Add(arg); continue; }
            string name = arg.TrimStart('-');
            string? inline = null;
            int equals = name.IndexOf('=');
            if (equals > 0) { inline = name[(equals + 1)..]; name = name[..equals]; }
            if (ValueOptions.Contains(name))
            {
                if (inline is null && index + 1 < args.Length) inline = args[++index];
                if (inline is null) options.Error = $"The option --{name} needs a value.";
                else options.Values[name] = inline;
            }
            else options.Switches.Add(name);
        }
        return options;
    }

    public static int Run(string[] args)
    {
        if (OperatingSystem.IsWindows())
        {
            // The window build has no console of its own: borrow the one it was started from.
            // AttachConsole swaps the standard handles for the console's, which would drop '>' and '|' redirection:
            // remember what was passed in and put it back.
            nint output = GetStdHandle(-11), error = GetStdHandle(-12);
            AttachConsole(-1);
            if (output != 0 && output != -1) SetStdHandle(-11, output);
            if (error != 0 && error != -1) SetStdHandle(-12, error);
            Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
            Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
        }
        string command = args[0].ToLowerInvariant();
        Options options = Parse(args.Skip(1).ToArray());
        if (options.Error is not null) return Fail(options.Error, Usage);
        if (options.Has("help") || options.Has("h")) return Help(command);
        using var cancel = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
        try
        {
            return command switch
            {
                "version" or "--version" or "-v" => Version(),
                "help" or "--help" or "-h" or "/?" => Help(options.Positional.FirstOrDefault()),
                "info" => Info(options),
                "scan" => ScanAsync(options, cancel.Token).GetAwaiter().GetResult(),
                "health" => HealthAsync(options, cancel.Token).GetAwaiter().GetResult(),
                "send" => SendAsync(options, cancel.Token).GetAwaiter().GetResult(),
                "consoles" => Consoles(),
                "convert" or "extract" or "verify" => JobAsync(command, options, cancel.Token).GetAwaiter().GetResult(),
                _ => Fail("Unknown command: " + command, Usage)
            };
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Cancelled.");
            return Cancelled;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException
                                       or ArgumentException or InvalidOperationException or JsonException)
        {
            Logger.Exception("CLI " + command, ex);
            return Fail(ex.Message, Failed);
        }
    }

    private static int Fail(string message, int code)
    {
        Console.Error.WriteLine("Error: " + message);
        if (code == Usage) Console.Error.WriteLine("Run 'Packwright help' for the commands.");
        return code;
    }

    private static int Version()
    {
        Console.WriteLine("Packwright " + UpdateService.CurrentVersion);
        return Ok;
    }

    private static int Help(string? topic)
    {
        Console.WriteLine($"""
            Packwright {UpdateService.CurrentVersion} command line

            Usage:  Packwright <command> [options]
            (On Windows use the packwright.cmd next to Packwright.exe so the prompt waits for the result.)

            Commands:
              info <path>                  Show the title, IDs, version and size of a dump folder, image or package.
              scan <folder>...             List every title found. --json | --csv | --no-recursive | -o <file>
              health <folder>...           Check a library for problems. --md | --json | -o <file>
              convert <source> --to <t>    Convert or build. <t> is exfat, ffpkg, ffpfsc, zar or pkg.
              extract <source>             Extract an image or package to a folder. -o <folder>
              verify <source>              Check an image or package for damage.
              send <source> --to <name>    Send to a console on your network (FTP). --folder <path> | --install (a .pkg: the
                                           console downloads it from this PC) | --host <ip> --port <n> instead of --to
              consoles                     List the consoles saved in Settings.
              version                      Print the version.

            convert options:
              -o, --output <file>   Where to write it (default: next to the source).  --overwrite replaces an existing file.
              --preset <name>       Use a saved or built-in preset from the Tools page (its target and options).
              --level 1-9  --gain 0-100            FFPFSC compression
              --cluster 32|64  --no-ampr           exFAT
              --block 32|64  --fragment 4|64  --density 256|512|1024  --min-free 0-50    FFPKG
              --builder <id>  --compression auto|kraken|stored  --kraken-level N  --threads N  --playgo N
              --deterministic  --no-fake-sign  --no-right-sprx  --drm upgradable|free|standard|keep  --workspace <folder>
            extract/verify on a package: --passcode <32 characters>   (default: the debug passcode)

            Exit codes: 0 done, 1 failed, 2 wrong usage, 130 cancelled.
            Only local files are used; the command line never connects to the internet.
            """);
        return Ok;
    }

    // ------------------------------------------------------------------ info / scan / health

    private static string Describe(Ps5GameInfo game, string path)
    {
        var item = new LibraryItem(game);
        var text = new StringBuilder();
        void Line(string label, string value) { if (value.Length > 0) text.AppendLine($"{label,-12}{value}"); }
        Line("Title", item.Title);
        Line("Title ID", item.TitleId);
        Line("Content ID", item.ContentId);
        Line("Category", item.Category);
        Line("Version", item.Version);
        Line("Firmware", item.Firmware);
        Line("Region", item.Region);
        Line("Format", item.Format);
        Line("Size", item.SizeText);
        Line("DRM", game.DrmType);
        Line("Path", path);
        if (item.HasMissing) text.AppendLine($"{"Missing",-12}{string.Join(", ", item.MissingFields)}");
        return text.ToString();
    }

    private static int Info(Options options)
    {
        if (options.Positional.Count != 1) return Fail("info needs one path.", Usage);
        string path = Path.GetFullPath(options.Positional[0]);
        Ps5GameInfo? game = SourceInfo.Read(path);
        if (game is null) return Fail("That is not a PS5 dump folder, image or package: " + path, Failed);
        Console.Write(Describe(game, path));
        return Ok;
    }

    private static async Task<List<LibraryItem>> ScanFoldersAsync(Options options, CancellationToken token)
    {
        if (options.Positional.Count == 0) throw new ArgumentException("Give at least one folder.");
        var folders = options.Positional.Select(Path.GetFullPath).ToList();
        foreach (string folder in folders.Where(folder => !Directory.Exists(folder)))
            throw new ArgumentException("Folder not found: " + folder);
        var progress = new Progress<Ps5ScanProgress>(value =>
        {
            if (!Console.IsErrorRedirected && value.Total > 0) Console.Error.Write($"\rScanning {value.Processed:N0} of {value.Total:N0}   ");
        });
        Ps5ScanResult result = await new Ps5LibraryScanner().ScanAsync(folders, !options.Has("no-recursive"), null, progress, token);
        if (!Console.IsErrorRedirected) Console.Error.Write("\r" + new string(' ', 40) + "\r");
        foreach (string error in result.Errors) Console.Error.WriteLine("Warning: " + error);
        return result.Games.Select(game => new LibraryItem(game)).OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static async Task<int> WriteOutputAsync(Options options, string text)
    {
        if (options.Get("o", "output") is { } file)
        {
            await File.WriteAllTextAsync(Path.GetFullPath(file), text, new UTF8Encoding(false));
            Console.Error.WriteLine("Wrote " + Path.GetFullPath(file));
        }
        else Console.Write(text);
        return Ok;
    }

    private static async Task<int> ScanAsync(Options options, CancellationToken token)
    {
        List<LibraryItem> items;
        try { items = await ScanFoldersAsync(options, token); }
        catch (ArgumentException ex) { return Fail(ex.Message, Usage); }
        if (options.Has("json")) return await WriteOutputAsync(options, LibraryExport.ToJson(items, includePaths: true) + Environment.NewLine);
        if (options.Has("csv")) return await WriteOutputAsync(options, LibraryActions.ToCsv(items));
        var text = new StringBuilder();
        int width = Math.Clamp(items.Count == 0 ? 10 : items.Max(item => item.Title.Length), 10, 48);
        text.AppendLine($"{"Title".PadRight(width)}  {"Title ID",-10}  {"Type",-6}  {"Version",-8}  {"Size",10}  Format");
        foreach (LibraryItem item in items)
            text.AppendLine($"{Trim(item.Title, width).PadRight(width)}  {item.TitleId,-10}  {item.Category,-6}  {item.Version,-8}  {item.SizeText,10}  {item.Format}");
        text.AppendLine().AppendLine($"{items.Count:N0} title(s), {Ps5LibraryHealth.FormatBytes(items.Sum(item => item.SizeBytes))}.");
        return await WriteOutputAsync(options, text.ToString());
    }

    private static string Trim(string text, int width) => text.Length <= width ? text : text[..(width - 1)] + "…";

    private static async Task<int> HealthAsync(Options options, CancellationToken token)
    {
        List<LibraryItem> items;
        try { items = await ScanFoldersAsync(options, token); }
        catch (ArgumentException ex) { return Fail(ex.Message, Usage); }
        HealthReport report = Ps5LibraryHealth.Analyze(items.Select(HealthView.ToHealthItem).ToList(), []);
        Ps5LibraryHealth.Sort(report);
        string text = options.Has("json")
            ? JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase }) + Environment.NewLine
            : Ps5LibraryHealth.ToMarkdown(report, issue => issue.ItemPath);
        await WriteOutputAsync(options, text);
        // A non-zero code when something is actually wrong makes this usable in scripts.
        return report.Count(HealthSeverity.Problem) > 0 ? Failed : Ok;
    }

    // ------------------------------------------------------------------ send to console

    private static int Consoles()
    {
        List<ConsoleProfile> consoles = new AppStateStore().LoadSettings().Consoles;
        if (consoles.Count == 0) { Console.WriteLine("No consoles are saved yet. Add one in Settings, or use --host."); return Ok; }
        foreach (ConsoleProfile console in consoles)
            Console.WriteLine($"{console.Name,-24}{console.Host}:{console.FtpPort}   folder {console.DefaultFolder}");
        return Ok;
    }

    private static async Task<int> SendAsync(Options options, CancellationToken token)
    {
        if (options.Positional.Count != 1) return Fail("send needs one file or folder.", Usage);
        string source = Path.GetFullPath(options.Positional[0]);
        if (!File.Exists(source) && !Directory.Exists(source)) return Fail("Not found: " + source, Failed);
        ConsoleProfile? profile;
        if (options.Get("to") is { } name)
        {
            profile = new AppStateStore().LoadSettings().Consoles.FirstOrDefault(console => console.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (profile is null) return Fail($"No console named '{name}'. Run 'Packwright consoles' to see the saved ones.", Usage);
        }
        else if (options.Get("host") is { } host)
            profile = new ConsoleProfile { Name = host, Host = host, FtpPort = options.Int("port") ?? 1337 };
        else
            return Fail("send needs --to <console name> or --host <address>.", Usage);
        var progress = new ConsoleProgress();
        Console.Error.WriteLine($"send: {source}");
        if (options.Has("install"))
        {
            Console.Error.WriteLine($"   -> {profile.Name}: install by URL");
            await ConsoleSender.InstallByUrlAsync(profile, source, progress, token);
        }
        else
        {
            string folder = options.Get("folder") ?? profile.DefaultFolder;
            Console.Error.WriteLine($"   -> {profile.Name}: {ConsoleSender.RemotePathFor(folder, source)}");
            await ConsoleSender.UploadAsync(profile, source, folder, progress, token);
        }
        progress.Finish();
        Console.Error.WriteLine("Done.");
        return Ok;
    }

    // ------------------------------------------------------------------ convert / extract / verify

    private static async Task<int> JobAsync(string command, Options options, CancellationToken token)
    {
        if (options.Positional.Count != 1) return Fail($"{command} needs exactly one source path.", Usage);
        string source = Path.GetFullPath(options.Positional[0]);
        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source)) return Fail("Not found: " + source, Failed);
        bool isPackage = !isDirectory && source.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase);
        Ps5ImageFormat format = isDirectory || isPackage ? Ps5ImageFormat.Unknown : Ps5ImageFormatProbe.Detect(source);
        if (!isDirectory && !isPackage && format == Ps5ImageFormat.Unknown)
            return Fail("That file is not a PS5 image or package: " + source, Failed);

        var spec = new JobSpec
        {
            Source = source,
            Format = format.ToString(),
            FromPackage = isPackage,
            Overwrite = options.Has("overwrite"),
            Passcode = options.Get("passcode") ?? string.Empty
        };
        if (spec.Passcode.Length > 0 && spec.Passcode.Length != 32) return Fail("The passcode must be 32 characters.", Usage);

        switch (command)
        {
            case "verify":
                spec.Kind = JobSpec.Verify;
                break;
            case "extract":
                spec.Kind = JobSpec.Extract;
                spec.Output = Path.GetFullPath(options.Get("o", "output") ?? SiblingName(source, isDirectory, "-files"));
                if (!spec.Overwrite) spec.Output = Jobs.FindAvailablePath(spec.Output);
                break;
            default:
                if (BuildConvertSpec(spec, options, isDirectory, isPackage, format) is { } problem) return Fail(problem, Usage);
                break;
        }

        Func<IProgress<PackageTaskProgress>, CancellationToken, Task>? run = spec.Executor();
        if (run is null) return Fail("That job cannot run with these arguments.", Usage);
        var printer = new ConsoleProgress();
        Console.Error.WriteLine($"{command}: {source}");
        if (spec.Output.Length > 0) Console.Error.WriteLine("   -> " + spec.Output);
        await run(printer, token);
        printer.Finish();
        Console.Error.WriteLine(command == "verify" ? "Verified: no problems found." : "Done.");
        return Ok;
    }

    private static string SiblingName(string source, bool isDirectory, string suffix)
    {
        string trimmed = source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = isDirectory ? Path.GetFileName(trimmed) : Path.GetFileNameWithoutExtension(trimmed);
        string parent = (isDirectory ? Path.GetDirectoryName(trimmed) : Path.GetDirectoryName(source)) ?? ".";
        return Path.Combine(parent, name + suffix);
    }

    /// <summary>Fills a convert or build job from the options; returns a message when they do not make sense.</summary>
    private static string? BuildConvertSpec(JobSpec spec, Options options, bool isDirectory, bool isPackage, Ps5ImageFormat format)
    {
        ToolPreset? preset = null;
        if (options.Get("preset") is { } presetName)
        {
            AppSettings settings = new AppStateStore().LoadSettings();
            preset = ToolsView.BuiltInPresets.Concat(settings.Presets)
                .FirstOrDefault(item => item.Name.Equals(presetName, StringComparison.OrdinalIgnoreCase));
            if (preset is null)
                return $"No preset named '{presetName}'. Built-in: {string.Join(", ", ToolsView.BuiltInPresets.Select(item => "\"" + item.Name + "\""))}.";
        }
        string? to = options.Get("to", "format") ?? preset?.Target;
        if (string.IsNullOrWhiteSpace(to)) return "convert needs --to exfat|ffpkg|ffpfsc|zar|pkg (or a preset that has a target).";
        string key = to.Trim().ToLowerInvariant();
        (string Name, string Extension)? target = key switch
        {
            "exfat" => ("Exfat", ".exfat"),
            "ffpkg" => ("Ffpkg", ".ffpkg"),
            "ffpfsc" => ("Ffpfsc", ".ffpfsc"),
            "zar" or "zarchive" => ("ZArchive", ".zar"),
            "pkg" or "fpkg" => ("Fpkg", ".pkg"),
            _ => null
        };
        if (target is null) return $"Unknown target '{to}'. Use exfat, ffpkg, ffpfsc, zar or pkg.";
        if (target.Value.Name != "Fpkg")
        {
            if (!isDirectory && !isPackage && !Enum.TryParse(target.Value.Name, out Ps5ImageConversionTarget parsed))
                return "Unsupported target.";
            if (!isDirectory && !isPackage && Enum.TryParse(target.Value.Name, out Ps5ImageConversionTarget check) &&
                !Ps5ImageConversionService.IsSupported(format, check))
                return $"A {format} image cannot be converted to {target.Value.Name}.";
            if (isPackage && target.Value.Name == "Fpkg") return "A package cannot be converted to a package.";
        }

        spec.Output = Path.GetFullPath(options.Get("o", "output") ?? SiblingName(spec.Source, isDirectory, target.Value.Extension));
        if (!spec.Overwrite && (File.Exists(spec.Output) || Directory.Exists(spec.Output)))
            return $"The output already exists: {spec.Output}. Use --overwrite or -o <other file>.";

        // Defaults come from the preset (if any); explicit options win.
        ToolPreset basis = preset ?? new ToolPreset();
        int Pick(string name, int fallback) => options.Int(name) ?? fallback;
        string? Choice(string name) => options.Get(name)?.Trim().ToLowerInvariant();

        spec.ClusterSize = Choice("cluster") switch { "32" => 32768, "64" => 65536, null => basis.ClusterIndex switch { 1 => 32768, 2 => 65536, _ => 0 }, _ => 0 };
        spec.GenerateAmpr = !options.Has("no-ampr") && basis.GenerateAmpr;
        spec.CompressionLevel = Math.Clamp(Pick("level", basis.Level), 1, 9);
        spec.MinimumGain = Math.Clamp(Pick("gain", basis.Gain), 0, 100);
        spec.BlockSize = (Choice("block") ?? (basis.BlockIndex == 1 ? "64" : "32")) == "64" ? 65536 : 32768;
        spec.FragmentSize = (Choice("fragment") ?? (basis.FragmentIndex == 1 ? "64" : "4")) == "64" ? 65536 : 4096;
        if (spec.FragmentSize > spec.BlockSize) spec.FragmentSize = spec.BlockSize;
        spec.BytesPerInode = (Choice("density") ?? basis.DensityIndex switch { 1 => "512", 2 => "1024", _ => "256" }) switch
        { "512" => 524288, "1024" => 1048576, _ => 262144 };
        spec.MinFreePercent = Math.Clamp(Pick("min-free", basis.MinFree), 0, 50);

        if (target.Value.Name == "Fpkg")
        {
            string contentId = Jobs.ReadContentId(spec.Source, format);
            if (contentId.Length == 0) return "The source has no content ID (sce_sys/param.json), so a package cannot be built from it.";
            spec.Kind = JobSpec.BuildPackage;
            spec.ContentId = contentId;
            spec.Backend = (options.Get("builder") ?? (basis.Backend.Length > 0 ? basis.Backend : AppSettingsDefaultBackend())).Trim();
            spec.Compression = (Choice("compression") switch
            {
                "kraken" => Ps5InnerCompression.Kraken,
                "stored" or "none" => Ps5InnerCompression.Stored,
                "auto" => Ps5InnerCompression.Auto,
                _ => basis.CompressionIndex switch { 1 => Ps5InnerCompression.Kraken, 2 => Ps5InnerCompression.Stored, _ => Ps5InnerCompression.Auto }
            }).ToString();
            spec.KrakenLevel = Math.Clamp(Pick("kraken-level", basis.KrakenLevel), -4, 9);
            spec.KrakenThreads = Math.Clamp(Pick("threads", basis.KrakenThreads), 0, 64);
            spec.PlayGoChunks = Math.Clamp(Pick("playgo", basis.PlayGoChunks), 1, 64);
            spec.Deterministic = options.Has("deterministic") || basis.Deterministic;
            spec.FakeSign = !options.Has("no-fake-sign") && basis.FakeSign;
            spec.RightSprx = !options.Has("no-right-sprx") && basis.RightSprx;
            spec.Drm = (Choice("drm") ?? basis.DrmIndex switch { 1 => "free", 2 => "standard", 3 => "keep", _ => "upgradable" }) switch
            { "free" => "free", "standard" => "standard", "keep" => null, _ => "upgradable" };
            spec.Sdk = basis.Sdk.Length > 0 ? SdkFromVersion(basis.Sdk) : null;
            spec.TempDirectory = options.Get("workspace") ?? (basis.TempDirectory.Length > 0 ? basis.TempDirectory : null);
            return null;
        }
        spec.Kind = JobSpec.Convert;
        spec.Target = target.Value.Name;
        return null;
    }

    private static string AppSettingsDefaultBackend() => new AppStateStore().LoadSettings().BuildBackend;

    private static ulong? SdkFromVersion(string version)
    {
        int index = Ps5SdkVersions.Releases.ToList().FindIndex(release => release.Version == version);
        return index >= 0 ? Ps5SdkVersions.ExecutableVersionAt(index) : null;
    }

    /// <summary>Prints the current step and a percentage to the error stream, a line at a time when it is redirected.</summary>
    private sealed class ConsoleProgress : IProgress<PackageTaskProgress>
    {
        private string _stage = string.Empty;
        private int _lastPercent = -1;
        private bool _lineOpen;

        public void Report(PackageTaskProgress value)
        {
            int percent = value.StagePercent >= 0 ? (int)value.StagePercent
                : value.TotalBytes > 0 ? (int)(value.CurrentBytes * 100 / value.TotalBytes) : -1;
            bool newStage = value.Stage.Length > 0 && value.Stage != _stage;
            if (newStage)
            {
                Finish();
                _stage = value.Stage;
                _lastPercent = -1;
                Console.Error.Write(_stage);
                _lineOpen = true;
                if (Console.IsErrorRedirected) Console.Error.WriteLine();
            }
            if (percent < 0 || percent == _lastPercent) return;
            if (Console.IsErrorRedirected ? percent % 10 != 0 : false) return;
            _lastPercent = percent;
            if (Console.IsErrorRedirected) Console.Error.WriteLine($"  {percent}%");
            else Console.Error.Write($"\r{_stage}  {percent}%   ");
        }

        public void Finish()
        {
            if (_lineOpen && !Console.IsErrorRedirected) Console.Error.WriteLine();
            _lineOpen = false;
        }
    }
}
