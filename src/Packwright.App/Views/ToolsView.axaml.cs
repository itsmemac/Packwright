using Avalonia.Controls;
using Packwright.App.Services;
using Packwright.Core.Backends;
using Packwright.Core.Builders;
using Packwright.Core.Models;
using Packwright.Core.Parsers;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using Packwright.Containers;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

public partial class ToolsView : UserControl
{
    private enum Target { Exfat, Ffpkg, Ffpfsc, ZArchive, Fpkg }

    private const string ActionConvert = "Convert / Build";
    private const string ActionExtract = "Extract";
    private const string ActionVerify = "Verify";
    private const string ActionRepair = "Repair exFAT";
    private const string ActionAmpr = "Refresh AMPR index";
    private const string ActionEdit = "Edit files...";
    private const string ActionRebuild = "Rebuild FFPKG metadata";

    private static readonly (int Value, string Name)[] KrakenLevels =
    [
        (-4, "-4 HyperFast4"), (-3, "-3 HyperFast3"), (-2, "-2 HyperFast2"), (-1, "-1 HyperFast1"),
        (0, "0 None"), (1, "1 SuperFast"), (2, "2 VeryFast"), (3, "3 Fast"), (4, "4 Normal"),
        (5, "5 Optimal1"), (6, "6 Optimal2"), (7, "7 Optimal3"), (8, "8 Optimal4"), (9, "9 Optimal5")
    ];

    private readonly AppServices _services = AppServices.Instance;
    private readonly List<Target> _targets = [];
    private bool _suppress;
    private string? _source;
    private Ps5ImageFormat _format;
    private bool _isPackage;
    private bool _isDirectory;
    private SonyPkgKind? _packageKind;

    public ToolsView()
    {
        InitializeComponent();
        BackendBox.ItemsSource = BackendRegistry.All.Select(backend =>
            backend.Id == BackendRegistry.LppId && !Jobs.PlatformSupportsLpp
                ? backend.DisplayName + " (Windows only)"
                : backend.DisplayName).ToList();
        BackendBox.SelectedIndex = Math.Max(0, BackendRegistry.All.ToList()
            .FindIndex(backend => backend.Id == Jobs.ResolveBackend(_services.Settings.BuildBackend).Id));
        CompressionBox.ItemsSource = new[] { "Auto (Kraken where it helps)", "Kraken (force)", "Uncompressed" };
        CompressionBox.SelectedIndex = 0;
        DrmBox.ItemsSource = new[] { "Upgradable", "Free", "Standard", "Keep source" };
        DrmBox.SelectedIndex = 0;
        SdkBox.ItemsSource = new[] { "Auto (from source)" }.Concat(Ps5SdkVersions.Releases.Select(release => release.Version)).ToList();
        SdkBox.SelectedIndex = 0;
        KrakenLevelBox.ItemsSource = KrakenLevels.Select(level => level.Name).ToList();
        KrakenLevelBox.SelectedIndex = Array.FindIndex(KrakenLevels, level => level.Value == 7);
        BrowseTempButton.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, "Workspace folder") is { } folder) TempBox.Text = folder;
        };
        ClusterBox.ItemsSource = new[] { "Auto", "32 KB", "64 KB" };
        ClusterBox.SelectedIndex = 0;
        BlockBox.ItemsSource = new[] { "32 KB", "64 KB" };
        BlockBox.SelectedIndex = 0;
        FragmentBox.ItemsSource = new[] { "4 KB", "64 KB" };
        FragmentBox.SelectedIndex = 0;
        DensityBox.ItemsSource = new[] { "256 KiB", "512 KiB", "1 MiB" };
        DensityBox.SelectedIndex = 0;

        SourceBox.LostFocus += (_, _) => SetSource(SourceBox.Text?.Trim());
        SourceBox.KeyDown += (_, e) => { if (e.Key == Avalonia.Input.Key.Enter) SetSource(SourceBox.Text?.Trim()); };
        BrowseFileButton.Click += async (_, _) =>
        {
            if (await Dialogs.PickFileAsync(this, "Select an image or package") is { } file) SetSource(file);
        };
        BrowseFolderButton.Click += async (_, _) =>
        {
            if (await Dialogs.PickFolderAsync(this, "Select a dump folder") is { } folder) SetSource(folder);
        };
        ActionBox.SelectionChanged += (_, _) => { if (!_suppress) UpdateTargets(); };
        TargetBox.SelectionChanged += (_, _) => { if (!_suppress) UpdateOptions(); };
        BrowseOutputButton.Click += async (_, _) => await BrowseOutputAsync();
        RunButton.Click += async (_, _) => await RunAsync();
        InitializePresets();
        UpdateActions();
    }

    public event Action<string>? StatusChanged;

    /// <summary>Picks "convert", "extract" or "verify" in the Action list, when the current source allows it.</summary>
    public void ChooseAction(string name)
    {
        string wanted = name switch { "extract" => ActionExtract, "verify" => ActionVerify, _ => ActionConvert };
        if (ActionBox.ItemsSource is IEnumerable<string> actions && actions.Contains(wanted)) ActionBox.SelectedItem = wanted;
    }

    private SourceSummary _summary = SourceSummary.Empty;

    private async Task LoadSummaryAsync(string source)
    {
        SourceSummary summary = await SourceInfo.DescribeAsync(source);
        if (!string.Equals(source, _source, StringComparison.OrdinalIgnoreCase)) return;
        _summary = summary;
        SourceTitle.Text = summary.Title;
        SourceTitle.IsVisible = summary.Title.Length > 0;
        if (summary.Icon is { Length: > 0 } icon)
        {
            SourceIcon.Source = new Avalonia.Media.Imaging.Bitmap(new MemoryStream(icon));
            SourceIconHost.IsVisible = true;
        }
    }

    private string? CurrentAction => ActionBox.SelectedItem as string;
    private Target? CurrentTarget =>
        CurrentAction == ActionConvert && TargetBox.SelectedIndex >= 0 && TargetBox.SelectedIndex < _targets.Count
            ? _targets[TargetBox.SelectedIndex]
            : null;

    public void SetSource(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && _source is not null &&
            (File.Exists(path) || Directory.Exists(path)) &&
            string.Equals(Path.GetFullPath(path), _source, StringComparison.OrdinalIgnoreCase))
        {
            SourceBox.Text = _source;
            return;
        }
        if (string.IsNullOrWhiteSpace(path) || (!File.Exists(path) && !Directory.Exists(path)))
        {
            _source = null;
            if (!string.IsNullOrWhiteSpace(path)) ResultText.Text = "That path does not exist.";
        }
        else
        {
            _source = Path.GetFullPath(path);
            ResultText.Text = string.Empty;
        }
        SourceBox.Text = _source ?? path;
        _isDirectory = _source is not null && Directory.Exists(_source);
        _isPackage = _source is not null && !_isDirectory &&
                     Path.GetExtension(_source).Equals(".pkg", StringComparison.OrdinalIgnoreCase);
        _format = _source is null || _isDirectory || _isPackage
            ? Ps5ImageFormat.Unknown
            : Ps5ImageFormatProbe.Detect(_source);
        _packageKind = null;
        if (_isPackage)
        {
            try { _packageKind = new SonyPkgReader().Read(_source!).Kind; }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                           or ArgumentException or NotSupportedException)
            {
                Logger.Warn("Could not read package " + _source + ": " + ex.Message);
            }
        }
        DetectedText.Text = _source is null ? "Nothing selected" : FormatLabel();
        _summary = SourceSummary.Empty;
        SourceTitle.IsVisible = SourceIconHost.IsVisible = false;
        SourceIcon.Source = null;
        if (_source is not null) _ = LoadSummaryAsync(_source);
        if (_isPackage && _packageKind is not null and not SonyPkgKind.FinalizedDebug)
            ResultText.Text = _packageKind == SonyPkgKind.FinalizedPatch
                ? "Patch package: Convert, Extract and Verify need a finalized debug (FPKG) package."
                : "Retail package: Convert, Extract and Verify need a debug (FPKG) package.";
        UpdateActions();
    }

    private string FormatLabel()
    {
        if (_isDirectory) return "dump folder";
        if (_isPackage)
            return _packageKind switch
            {
                SonyPkgKind.FinalizedDebug => "debug package (FPKG)",
                SonyPkgKind.FinalizedPatch => "patch package (LIH)",
                SonyPkgKind.FinalizedRetail => "retail package",
                SonyPkgKind.MetadataContainer => "CNT metadata",
                _ => "package"
            };
        return _format switch
        {
            Ps5ImageFormat.Exfat => "exFAT image",
            Ps5ImageFormat.Ufs2 => "FFPKG image",
            Ps5ImageFormat.Pfs => "FFPFSC image",
            Ps5ImageFormat.ZArchive => "ZArchive",
            _ => "unknown (not a PS5 image)"
        };
    }

    private void UpdateActions()
    {
        var actions = new List<string>();
        if (_source is not null)
        {
            if (_isDirectory) actions.Add(ActionConvert);
            else if (_isPackage)
            {
                if (_packageKind == SonyPkgKind.FinalizedDebug) actions.AddRange([ActionConvert, ActionExtract, ActionVerify]);
            }
            else if (_format is Ps5ImageFormat.Exfat or Ps5ImageFormat.Ufs2 or Ps5ImageFormat.Pfs or Ps5ImageFormat.ZArchive)
            {
                actions.AddRange([ActionConvert, ActionExtract, ActionVerify]);
                if (_format == Ps5ImageFormat.Exfat) actions.AddRange([ActionEdit, ActionRepair, ActionAmpr]);
                else if (_format == Ps5ImageFormat.Ufs2) actions.AddRange([ActionEdit, ActionRebuild]);
            }
        }
        _suppress = true;
        string? previous = CurrentAction;
        ActionBox.ItemsSource = actions;
        ActionBox.SelectedItem = previous is not null && actions.Contains(previous) ? previous : actions.FirstOrDefault();
        _suppress = false;
        UpdateTargets();
    }

    private void UpdateTargets()
    {
        Target? previousTarget = _wantedTarget ?? CurrentTarget;
        _targets.Clear();
        if (_source is not null && CurrentAction == ActionConvert)
        {
            if (_isPackage)
                _targets.AddRange([Target.Exfat, Target.Ffpkg, Target.Ffpfsc, Target.ZArchive]);
            else if (_isDirectory)
                _targets.AddRange([Target.Exfat, Target.Ffpkg, Target.Ffpfsc, Target.ZArchive, Target.Fpkg]);
            else
            {
                foreach (Ps5ImageConversionTarget target in Enum.GetValues<Ps5ImageConversionTarget>())
                    if (Ps5ImageConversionService.IsSupported(_format, target))
                        _targets.Add(target switch
                        {
                            Ps5ImageConversionTarget.Exfat => Target.Exfat,
                            Ps5ImageConversionTarget.Ffpkg => Target.Ffpkg,
                            Ps5ImageConversionTarget.ZArchive => Target.ZArchive,
                            _ => Target.Ffpfsc
                        });
                _targets.Add(Target.Fpkg);
            }
        }
        _suppress = true;
        TargetBox.ItemsSource = _targets.Select(TargetLabel).ToList();
        int keep = previousTarget is { } wanted ? _targets.IndexOf(wanted) : -1;
        TargetBox.SelectedIndex = _targets.Count == 0 ? -1 : Math.Max(0, keep);
        _suppress = false;
        if (_targets.Count > 0) _wantedTarget = null;
        UpdateOptions();
    }

    private static string TargetLabel(Target target) => target switch
    {
        Target.Exfat => "exFAT image (.exfat)",
        Target.Ffpkg => "FFPKG image (.ffpkg)",
        Target.Ffpfsc => "FFPFSC image (.ffpfsc)",
        Target.ZArchive => "ZArchive (.zar)",
        _ => "Debug package (.pkg)"
    };

    private static string Extension(Target target) => target switch
    {
        Target.Exfat => ".exfat",
        Target.Ffpkg => ".ffpkg",
        Target.Ffpfsc => ".ffpfsc",
        Target.ZArchive => ".zar",
        _ => ".pkg"
    };

    private static string ShortLabel(Target target) => target switch
    {
        Target.Exfat => "exFAT",
        Target.Ffpkg => "FFPKG",
        Target.Ffpfsc => "FFPFSC",
        Target.ZArchive => "ZArchive",
        _ => "FPKG"
    };

    private void UpdateOptions()
    {
        string? action = CurrentAction;
        bool convert = action == ActionConvert;
        bool extract = action == ActionExtract;
        TargetPanel.IsVisible = convert;
        // Presets hold conversion settings, so they only make sense for Convert / Build (or before a source is chosen).
        PresetPanel.IsVisible = convert || action is null;
        OutputPanel.IsVisible = convert || extract;
        OutputLabel.Text = extract ? "Output folder" : "Output file";
        PasscodePanel.IsVisible = _isPackage && (extract || action == ActionVerify);
        BuildOptions.IsVisible = convert && CurrentTarget == Target.Fpkg;
        Target? chosen = convert ? CurrentTarget : null;
        ExfatOptions.IsVisible = chosen is Target.Exfat or Target.Ffpfsc;
        FfpfscOptions.IsVisible = chosen == Target.Ffpfsc;
        FfpkgOptions.IsVisible = chosen == Target.Ffpkg;
        ImageOptions.IsVisible = chosen is Target.Exfat or Target.Ffpfsc or Target.Ffpkg;
        RunButton.Content = action == ActionEdit ? "Open editor" : "Run";
        RunButton.IsEnabled = action is not null;
        if (convert && CurrentTarget is { } target) SuggestOutput(Extension(target), replaceExtension: true);
        else if (extract) SuggestOutput("-files", replaceExtension: false);
    }

    private string _lastSuggestion = string.Empty;

    private void SuggestOutput(string suffix, bool replaceExtension)
    {
        if (_source is null) { OutputBox.Text = string.Empty; _lastSuggestion = string.Empty; return; }
        string current = OutputBox.Text?.Trim() ?? string.Empty;
        if (current.Length > 0 && current != _lastSuggestion)
        {
            // The user typed or picked this path; keep it and only follow the target's file extension.
            if (replaceExtension && !current.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                string next = Path.ChangeExtension(current, suffix);
                OutputBox.Text = next;
                _lastSuggestion = next;
            }
            return;
        }
        string name = _isDirectory
            ? new DirectoryInfo(_source).Name
            : Path.GetFileNameWithoutExtension(_source);
        string? parent = !string.IsNullOrWhiteSpace(_services.Settings.OutputDirectory) &&
                         Directory.Exists(_services.Settings.OutputDirectory)
            ? _services.Settings.OutputDirectory
            : _isDirectory ? Directory.GetParent(_source)?.FullName : Path.GetDirectoryName(_source);
        _lastSuggestion = Path.Combine(parent ?? _source, name + suffix);
        OutputBox.Text = _lastSuggestion;
    }

    private async Task BrowseOutputAsync()
    {
        if (CurrentAction == ActionExtract)
        {
            if (await Dialogs.PickFolderAsync(this, "Select the output folder") is { } folder) OutputBox.Text = folder;
            return;
        }
        Target target = CurrentTarget ?? Target.Ffpfsc;
        string? file = await Dialogs.SaveFileAsync(this, "Output file",
            Path.GetFileName(OutputBox.Text ?? "output" + Extension(target)), Extension(target));
        if (file is not null) OutputBox.Text = file;
    }

    private string Passcode() =>
        !string.IsNullOrEmpty(PasscodeBox.Text) ? PasscodeBox.Text!
        : !string.IsNullOrEmpty(_services.Settings.DebugPasscode) ? _services.Settings.DebugPasscode
        : SonyDebugPackageCredentials.DefaultPasscode;

    private async Task RunAsync()
    {
        if (_source is null || CurrentAction is not { } action) return;
        string source = _source;
        string fileName = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        string sourceLabel = FormatLabel();
        ResultText.Text = string.Empty;
        try
        {
            if (_summary.Title.Length == 0) _summary = await SourceInfo.DescribeAsync(source);
            var spec = new JobSpec
            {
                Title = _summary.Title,
                Icon = _summary.Icon is { Length: > 0 } iconBytes ? Convert.ToBase64String(iconBytes) : null,
                Source = source,
                Format = _format.ToString(),
                FromPackage = _isPackage,
                Passcode = Passcode(),
                Overwrite = OverwriteBox.IsChecked == true
            };
            switch (action)
            {
                case ActionConvert:
                    await QueueConvertAsync(spec, fileName, sourceLabel);
                    break;
                case ActionExtract:
                    spec.Kind = JobSpec.Extract;
                    if (string.IsNullOrWhiteSpace(OutputBox.Text)) return;
                    spec.Output = Jobs.FindAvailablePath(OutputBox.Text.Trim());
                    Enqueue(spec, $"Extract {fileName}", _isPackage ? "Extract package" : "Extract", sourceLabel, "folder");
                    break;
                case ActionVerify:
                    spec.Kind = JobSpec.Verify;
                    Enqueue(spec, $"Verify {fileName}", _isPackage ? "Verify package" : "Verify", sourceLabel, "");
                    break;
                case ActionEdit:
                    await new ImageEditorWindow(source, _format).ShowDialog(Dialogs.OwnerOf(this)!);
                    break;
                case ActionRebuild:
                    if (!await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Rebuild FFPKG image?",
                            "This extracts every readable file, rebuilds all FFPKG metadata beside the original, verifies the " +
                            "replacement and only then swaps it into place. It needs substantial free disk space.\n\n" + source,
                            "Rebuild"))
                        break;
                    spec.Kind = JobSpec.RebuildFfpkg;
                    Enqueue(spec, $"Rebuild {fileName}", "Rebuild", sourceLabel, "FFPKG");
                    break;
                case ActionRepair:
                    spec.Kind = JobSpec.Repair;
                    Enqueue(spec, $"Repair {fileName}", "Repair", sourceLabel, "");
                    break;
                case ActionAmpr:
                    spec.Kind = JobSpec.RefreshAmpr;
                    Enqueue(spec, $"Refresh AMPR {fileName}", "Refresh AMPR", sourceLabel, "");
                    break;
            }
        }
        catch (Exception ex)
        {
            Logger.Exception("Tools", ex);
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Image tools", ex.Message);
        }
    }

    private void Enqueue(JobSpec spec, string name, string operation, string sourceFormat, string targetFormat,
        string qualifier = "")
    {
        _services.EnqueueJob(spec, name, operation, sourceFormat, targetFormat, qualifier);
        ResultText.Text = "Queued. See the Tasks page.";
        StatusChanged?.Invoke($"Queued: {name}");
    }

    private async Task QueueConvertAsync(JobSpec spec, string fileName, string sourceLabel)
    {
        string output = OutputBox.Text?.Trim() ?? string.Empty;
        if (output.Length == 0 || CurrentTarget is not { } target)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Image tools", "Choose an output file.");
            return;
        }
        spec.Output = output;
        if (!spec.Overwrite && (File.Exists(output) || Directory.Exists(output)))
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Image tools",
                "The output already exists:\n" + output + "\n\nTick \"Overwrite the output\" or choose another name.");
            return;
        }
        if (target == Target.Fpkg)
        {
            await QueueBuildPackageAsync(spec, fileName, sourceLabel);
            return;
        }
        spec.Kind = JobSpec.Convert;
        spec.Target = (target switch
        {
            Target.Exfat => Ps5ImageConversionTarget.Exfat,
            Target.Ffpkg => Ps5ImageConversionTarget.Ffpkg,
            Target.ZArchive => Ps5ImageConversionTarget.ZArchive,
            _ => Ps5ImageConversionTarget.Ffpfsc
        }).ToString();
        spec.ClusterSize = ClusterBox.SelectedIndex switch { 1 => 32768, 2 => 65536, _ => 0 };
        spec.GenerateAmpr = AmprBox.IsChecked == true;
        spec.CompressionLevel = (int)(LevelBox.Value ?? 7);
        spec.MinimumGain = (int)(GainBox.Value ?? 1);
        spec.BlockSize = BlockBox.SelectedIndex == 1 ? 65536 : 32768;
        spec.FragmentSize = FragmentBox.SelectedIndex == 1 ? 65536 : 4096;
        if (spec.FragmentSize > spec.BlockSize) spec.FragmentSize = spec.BlockSize;
        spec.BytesPerInode = DensityBox.SelectedIndex switch { 1 => 524288, 2 => 1048576, _ => 262144 };
        spec.MinFreePercent = (int)(MinFreeBox.Value ?? 0);
        Enqueue(spec, $"Convert {fileName}", "Convert", sourceLabel, ShortLabel(target));
    }

    private async Task QueueBuildPackageAsync(JobSpec spec, string fileName, string sourceLabel)
    {
        string contentId = Jobs.ReadContentId(spec.Source, _format);
        if (contentId.Length == 0)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Build package",
                "The source does not contain a content ID (sce_sys/param.json), so a package cannot be built from it.");
            return;
        }
        IPackageBackend backend = BackendRegistry.All[Math.Max(0, BackendBox.SelectedIndex)];
        if (backend.Id == BackendRegistry.LppId && !Jobs.PlatformSupportsLpp)
        {
            backend = BackendRegistry.Get(BackendRegistry.PptId);
            StatusChanged?.Invoke("LibProsperoPkg is Windows only; using ProsperoPkgTool.");
        }
        spec.Kind = JobSpec.BuildPackage;
        spec.ContentId = contentId;
        spec.Backend = backend.Id;
        spec.Compression = (CompressionBox.SelectedIndex switch
        {
            1 => Ps5InnerCompression.Kraken,
            2 => Ps5InnerCompression.Stored,
            _ => Ps5InnerCompression.Auto
        }).ToString();
        spec.KrakenLevel = KrakenLevels[Math.Max(0, KrakenLevelBox.SelectedIndex)].Value;
        spec.KrakenThreads = (int)(KrakenThreadsBox.Value ?? 0);
        spec.PlayGoChunks = (int)(PlayGoBox.Value ?? 1);
        spec.Deterministic = DeterministicBox.IsChecked == true;
        spec.FakeSign = FakeSignBox.IsChecked == true;
        spec.RightSprx = RightSprxBox.IsChecked == true;
        spec.Drm = DrmBox.SelectedIndex switch { 0 => "upgradable", 1 => "free", 2 => "standard", _ => null };
        spec.Sdk = SdkBox.SelectedIndex > 0 ? Ps5SdkVersions.ExecutableVersionAt(SdkBox.SelectedIndex - 1) : null;
        spec.TempDirectory = string.IsNullOrWhiteSpace(TempBox.Text) ? null : TempBox.Text!.Trim();

        // Free-space preflight before a long build; the engine re-checks exactly while it runs.
        Ps5DiskSpaceCheck space = Ps5DiskSpace.Check(EstimatePayload(spec.Source), spec.Output, spec.TempDirectory);
        if (space.Status == Ps5DiskSpaceStatus.Insufficient)
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Build package",
                "Not enough free disk space to build this package.\n\n" + space.Message +
                "\n\nFree space, or choose a different workspace or output volume.");
            return;
        }
        if (space.Status == Ps5DiskSpaceStatus.NearLimit && !await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this),
                "Low disk space", "Low free disk space for this build.\n\n" + space.Message, "Build anyway"))
            return;
        Enqueue(spec, $"Build package from {fileName}", "Build package", sourceLabel, "FPKG", backend.DisplayName);
    }

    private static long EstimatePayload(string source)
    {
        try
        {
            if (Directory.Exists(source))
                return Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
            if (File.Exists(source)) return new FileInfo(source).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return 0;
    }
}
