using Avalonia.Controls;
using Packwright.App.Services;
using Packwright.Core.Backends;
using Packwright.Core.Builders;
using Packwright.Infrastructure;

namespace Packwright.App.Views;

/// <summary>Presets: named sets of conversion and build options that can be applied in one click.</summary>
public partial class ToolsView
{
    private const string NoPreset = "Choose a preset...";

    internal static readonly ToolPreset[] BuiltInPresets =
    [
        new() { Name = "Smallest FFPFSC (level 9)", Target = nameof(Target.Ffpfsc), Level = 9, Gain = 1 },
        new() { Name = "Fast FFPFSC (level 3)", Target = nameof(Target.Ffpfsc), Level = 3, Gain = 5 },
        new() { Name = "exFAT image with AMPR index", Target = nameof(Target.Exfat), GenerateAmpr = true },
        new() { Name = "ZArchive (.zar)", Target = nameof(Target.ZArchive) },
        new() { Name = "Debug package, reproducible", Target = nameof(Target.Fpkg), Deterministic = true }
    ];

    private Target? _wantedTarget;
    private List<ToolPreset> _presetList = [];

    private void InitializePresets()
    {
        PresetBox.SelectionChanged += (_, _) => { if (!_suppress) OnPresetChosen(); };
        PresetSaveButton.Click += async (_, _) => await SavePresetAsync();
        PresetDeleteButton.Click += async (_, _) => await DeletePresetAsync();
        RebuildPresets(null);
    }

    private void RebuildPresets(string? select)
    {
        bool wasSuppressed = _suppress;
        _suppress = true;
        _presetList = [.. BuiltInPresets, .. _services.Settings.Presets];
        var names = new List<string> { NoPreset };
        names.AddRange(BuiltInPresets.Select(preset => preset.Name));
        names.AddRange(_services.Settings.Presets.Select(preset => preset.Name + "  (yours)"));
        PresetBox.ItemsSource = names;
        int index = select is null ? 0 : _presetList.FindIndex(preset => preset.Name == select) + 1;
        PresetBox.SelectedIndex = Math.Max(0, index);
        PresetDeleteButton.IsEnabled = PresetBox.SelectedIndex > BuiltInPresets.Length;
        _suppress = wasSuppressed;
    }

    private ToolPreset? ChosenPreset =>
        PresetBox.SelectedIndex > 0 && PresetBox.SelectedIndex <= _presetList.Count ? _presetList[PresetBox.SelectedIndex - 1] : null;

    private void OnPresetChosen()
    {
        PresetDeleteButton.IsEnabled = PresetBox.SelectedIndex > BuiltInPresets.Length;
        if (ChosenPreset is not { } preset) return;
        ApplyPreset(preset);
        ResultText.Text = $"Applied the preset '{preset.Name}'.";
    }

    private ToolPreset CapturePreset(string name)
    {
        int krakenIndex = Math.Max(0, KrakenLevelBox.SelectedIndex);
        return new ToolPreset
        {
            Name = name,
            Target = CurrentTarget?.ToString() ?? string.Empty,
            ClusterIndex = Math.Max(0, ClusterBox.SelectedIndex),
            GenerateAmpr = AmprBox.IsChecked == true,
            Level = (int)(LevelBox.Value ?? 7),
            Gain = (int)(GainBox.Value ?? 1),
            BlockIndex = Math.Max(0, BlockBox.SelectedIndex),
            FragmentIndex = Math.Max(0, FragmentBox.SelectedIndex),
            DensityIndex = Math.Max(0, DensityBox.SelectedIndex),
            MinFree = (int)(MinFreeBox.Value ?? 0),
            Backend = BackendRegistry.All.ElementAtOrDefault(Math.Max(0, BackendBox.SelectedIndex))?.Id ?? string.Empty,
            CompressionIndex = Math.Max(0, CompressionBox.SelectedIndex),
            KrakenLevel = KrakenLevels[krakenIndex].Value,
            KrakenThreads = (int)(KrakenThreadsBox.Value ?? 0),
            PlayGoChunks = (int)(PlayGoBox.Value ?? 1),
            Deterministic = DeterministicBox.IsChecked == true,
            FakeSign = FakeSignBox.IsChecked == true,
            RightSprx = RightSprxBox.IsChecked == true,
            DrmIndex = Math.Max(0, DrmBox.SelectedIndex),
            Sdk = SdkBox.SelectedIndex > 0 ? SdkBox.SelectedItem as string ?? string.Empty : string.Empty,
            TempDirectory = TempBox.Text?.Trim() ?? string.Empty
        };
    }

    private static void Select(ComboBox box, int index) =>
        box.SelectedIndex = box.Items.Count == 0 ? -1 : Math.Clamp(index, 0, box.Items.Count - 1);

    private void ApplyPreset(ToolPreset preset)
    {
        bool wasSuppressed = _suppress;
        _suppress = true;
        Select(ClusterBox, preset.ClusterIndex);
        AmprBox.IsChecked = preset.GenerateAmpr;
        LevelBox.Value = Math.Clamp(preset.Level, 1, 9);
        GainBox.Value = Math.Clamp(preset.Gain, 0, 100);
        Select(BlockBox, preset.BlockIndex);
        Select(FragmentBox, preset.FragmentIndex);
        Select(DensityBox, preset.DensityIndex);
        MinFreeBox.Value = Math.Clamp(preset.MinFree, 0, 50);
        int backend = BackendRegistry.All.ToList().FindIndex(item => item.Id == preset.Backend);
        if (backend >= 0) BackendBox.SelectedIndex = backend;
        Select(CompressionBox, preset.CompressionIndex);
        int kraken = Array.FindIndex(KrakenLevels, level => level.Value == preset.KrakenLevel);
        KrakenLevelBox.SelectedIndex = kraken >= 0 ? kraken : Array.FindIndex(KrakenLevels, level => level.Value == 7);
        KrakenThreadsBox.Value = Math.Clamp(preset.KrakenThreads, 0, 64);
        PlayGoBox.Value = Math.Clamp(preset.PlayGoChunks, 1, 64);
        DeterministicBox.IsChecked = preset.Deterministic;
        FakeSignBox.IsChecked = preset.FakeSign;
        RightSprxBox.IsChecked = preset.RightSprx;
        Select(DrmBox, preset.DrmIndex);
        int sdk = preset.Sdk.Length == 0 ? 0 : SdkBox.Items.OfType<string>().ToList().IndexOf(preset.Sdk);
        SdkBox.SelectedIndex = Math.Max(0, sdk);
        TempBox.Text = preset.TempDirectory.Length > 0 && Directory.Exists(preset.TempDirectory) ? preset.TempDirectory : string.Empty;
        _suppress = wasSuppressed;

        if (Enum.TryParse(preset.Target, out Target target))
        {
            _wantedTarget = target;
            if (ActionBox.ItemsSource is IEnumerable<string> actions && actions.Contains(ActionConvert))
            {
                if (CurrentAction != ActionConvert) ActionBox.SelectedItem = ActionConvert;
                else UpdateTargets();
            }
            else if (_source is not null)
                ResultText.Text = $"The options were applied, but this source cannot be converted to {TargetLabel(target)}.";
        }
        else
            UpdateOptions();
    }

    private async Task SavePresetAsync()
    {
        string? name = await Dialogs.PromptAsync(Dialogs.OwnerOf(this), "Save preset",
            "Name this preset (it saves the target and every option on this page, not the source or output):");
        if (name is null) return;
        if (BuiltInPresets.Any(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            await Dialogs.ShowMessageAsync(Dialogs.OwnerOf(this), "Save preset", "That name belongs to a built-in preset. Choose another.");
            return;
        }
        List<ToolPreset> mine = _services.Settings.Presets;
        if (mine.Any(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) &&
            !await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Replace preset?", $"A preset named '{name}' already exists. Replace it?", "Replace"))
            return;
        mine.RemoveAll(preset => preset.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        mine.Add(CapturePreset(name));
        _services.SaveSettings();
        RebuildPresets(name);
        ResultText.Text = $"Saved the preset '{name}'.";
    }

    private async Task DeletePresetAsync()
    {
        if (ChosenPreset is not { } preset || PresetBox.SelectedIndex <= BuiltInPresets.Length) return;
        if (!await Dialogs.ConfirmAsync(Dialogs.OwnerOf(this), "Delete preset?", $"Delete the preset '{preset.Name}'?", "Delete")) return;
        _services.Settings.Presets.RemoveAll(item => item.Name == preset.Name);
        _services.SaveSettings();
        RebuildPresets(null);
        ResultText.Text = $"Deleted the preset '{preset.Name}'.";
    }
}
