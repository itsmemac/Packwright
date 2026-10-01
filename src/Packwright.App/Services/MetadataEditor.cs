using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Packwright.Core.Backends;
using Packwright.Core.Tasks;
using Packwright.Containers;
using Packwright.Infrastructure;
using UFS2Tool;

namespace Packwright.App.Services;

/// <summary>The values to write into a title's param.json and icon. Empty values are left as they are.</summary>
/// <summary>The feature flags a title can declare, and which bit of param.json each one is.</summary>
public static class DeclaredFeatureFlags
{
    public static readonly (string Name, string Key, int Bit)[] All =
    [
        ("HDR", "attribute", 29), ("120 Hz", "attribute3", 6), ("PS VR2 supported", "attribute3", 10),
        ("PS VR2 required", "attribute3", 11), ("VRR", "attribute3", 18), ("VRR 120 Hz", "attribute3", 19),
        ("VRR disabled", "attribute3", 20), ("PS5 Pro", "attribute3", 22), ("PS5 Pro 8K", "attribute3", 23),
        ("Power Saver", "attribute4", 0)
    ];

    public static IEnumerable<string> Names => All.Select(flag => flag.Name);
}

public sealed class MetadataEdit
{
    public string Title { get; set; } = string.Empty;
    public string TitleId { get; set; } = string.Empty;
    public string ContentId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;
    /// <summary>A PNG file that becomes sce_sys/icon0.png.</summary>
    public string IconPath { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string ConceptId { get; set; } = string.Empty;
    public string MasterVersion { get; set; } = string.Empty;
    public string Sdk { get; set; } = string.Empty;
    public string Drm { get; set; } = string.Empty;
    public string DefaultLanguage { get; set; } = string.Empty;
    public string Created { get; set; } = string.Empty;
    public string ToolVersion { get; set; } = string.Empty;
    /// <summary>The features to declare (see <see cref="DeclaredFeatureFlags"/>). Null leaves them as they are.</summary>
    public List<string>? Features { get; set; }

    public bool IsEmpty => Title.Length == 0 && TitleId.Length == 0 && ContentId.Length == 0 && Version.Length == 0 &&
                           Firmware.Length == 0 && IconPath.Length == 0 && Category.Length == 0 && ConceptId.Length == 0 &&
                           MasterVersion.Length == 0 && Sdk.Length == 0 && Drm.Length == 0 && DefaultLanguage.Length == 0 &&
                           Created.Length == 0 && ToolVersion.Length == 0 && Features is null;
}

/// <summary>
/// Writes edited details into the file itself: param.json and icon0.png inside a dump folder, an exFAT or
/// FFPKG image, an FFPFSC image, a ZArchive or a debug package. Nothing is left half-written: images are
/// rebuilt beside the original and swapped in only after they verify.
/// </summary>
public static class MetadataEditor
{
    private const string ParamPath = "sce_sys/param.json";
    private const string IconPath = "sce_sys/icon0.png";
    private const int MaximumParamBytes = 4 * 1024 * 1024;

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    // ---------------------------------------------------------------- param.json

    /// <summary>
    /// Returns <paramref name="original"/> with the edited fields changed and everything else kept, or a new
    /// minimal param.json when there is none.
    /// </summary>
    public static byte[] PatchParam(byte[]? original, MetadataEdit edit)
    {
        JsonObject root;
        if (original is null)
        {
            // The title has no param.json (a bare dump or archive): start a minimal one.
            root = new JsonObject { ["applicationCategoryType"] = 0 };
        }
        else
        {
            string text = Encoding.UTF8.GetString(original).TrimStart('\uFEFF').TrimEnd('\0');
            root = JsonNode.Parse(text, null, new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip
            }) as JsonObject ?? throw new InvalidDataException("param.json is not a JSON object.");
        }

        if (edit.TitleId.Length > 0) root["titleId"] = edit.TitleId;
        if (edit.ContentId.Length > 0) root["contentId"] = edit.ContentId;
        if (edit.Version.Length > 0)
        {
            if (root.ContainsKey("contentVersion") || !root.ContainsKey("masterVersion") || edit.MasterVersion.Length > 0)
                root["contentVersion"] = edit.Version;
            else
                root["masterVersion"] = edit.Version;
        }
        if (edit.Firmware.Length > 0) root["requiredSystemSoftwareVersion"] = FirmwareToParam(edit.Firmware, root["requiredSystemSoftwareVersion"]?.ToString());
        if (edit.Title.Length > 0) SetTitle(root, edit.Title);
        if (edit.ConceptId.Length > 0) root["conceptId"] = edit.ConceptId;
        if (edit.MasterVersion.Length > 0) root["masterVersion"] = edit.MasterVersion;
        if (edit.Sdk.Length > 0) root["sdkVersion"] = FirmwareToParam(edit.Sdk, root["sdkVersion"]?.ToString());
        if (edit.Drm.Length > 0) root["applicationDrmType"] = edit.Drm;
        if (edit.Category.Length > 0) root["applicationCategoryType"] = CategoryToParam(edit.Category);
        if (edit.Created.Length > 0) PubTools(root)["creationDate"] = edit.Created;
        if (edit.ToolVersion.Length > 0) PubTools(root)["toolVersion"] = edit.ToolVersion;
        if (edit.DefaultLanguage.Length > 0) SetDefaultLanguage(root, edit.DefaultLanguage, edit.Title);
        if (edit.Features is not null) ApplyFeatures(root, edit.Features);
        return Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions));
    }

    private static JsonObject PubTools(JsonObject root)
    {
        if (root["pubtools"] is JsonObject existing) return existing;
        var created = new JsonObject();
        root["pubtools"] = created;
        return created;
    }

    /// <summary>The number the app reads back as the category (the same mapping the library uses).</summary>
    private static int CategoryToParam(string category) => category switch { "DLC" => 1, "Patch" => 2, "App" => 3, _ => 0 };

    /// <summary>Makes <paramref name="language"/> the default language, adding an entry for it (with the title) when it has none.</summary>
    private static void SetDefaultLanguage(JsonObject root, string language, string title)
    {
        if (root["localizedParameters"] is not JsonObject localized)
        {
            localized = new JsonObject();
            root["localizedParameters"] = localized;
        }
        string previous = localized["defaultLanguage"]?.ToString() ?? string.Empty;
        localized["defaultLanguage"] = language;
        if (localized[language] is JsonObject) return;
        string name = title;
        if (name.Length == 0 && previous.Length > 0 && localized[previous] is JsonObject old) name = old["titleName"]?.ToString() ?? string.Empty;
        if (name.Length == 0)
            foreach ((string key, JsonNode? value) in localized)
                if (value is JsonObject other && other["titleName"]?.ToString() is { Length: > 0 } found) { name = found; break; }
        localized[language] = new JsonObject { ["titleName"] = name };
    }

    /// <summary>Sets or clears the known feature bits and leaves every other bit as it was.</summary>
    private static void ApplyFeatures(JsonObject root, IReadOnlyCollection<string> features)
    {
        foreach (IGrouping<string, (string Name, string Key, int Bit)> word in DeclaredFeatureFlags.All.GroupBy(flag => flag.Key))
        {
            long value = 0;
            if (root[word.Key] is JsonValue existing && existing.TryGetValue(out long number)) value = number;
            else if (root[word.Key]?.ToString() is { Length: > 0 } text && long.TryParse(text, out long parsed)) value = parsed;
            long updated = value;
            foreach ((string name, _, int bit) in word)
                updated = features.Contains(name, StringComparer.OrdinalIgnoreCase) ? updated | (1L << bit) : updated & ~(1L << bit);
            if (updated != value || root.ContainsKey(word.Key)) root[word.Key] = updated;
        }
    }

    private static void SetTitle(JsonObject root, string title)
    {
        if (root["localizedParameters"] is not JsonObject localized)
        {
            root["localizedParameters"] = new JsonObject
            {
                ["defaultLanguage"] = "en-US",
                ["en-US"] = new JsonObject { ["titleName"] = title }
            };
            return;
        }
        bool any = false;
        foreach ((string name, JsonNode? value) in localized.ToList())
        {
            if (name.Equals("defaultLanguage", StringComparison.OrdinalIgnoreCase) || value is not JsonObject language) continue;
            language["titleName"] = title;
            any = true;
        }
        if (!any)
        {
            string defaultLanguage = localized["defaultLanguage"]?.ToString() is { Length: > 0 } configured ? configured : "en-US";
            localized["defaultLanguage"] = defaultLanguage;
            localized[defaultLanguage] = new JsonObject { ["titleName"] = title };
        }
    }

    /// <summary>"4.50" becomes "0x0450000000000000". A value that already starts with 0x is used as written.</summary>
    internal static string FirmwareToParam(string text, string? existing)
    {
        text = text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return text;
        string[] parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int major))
            throw new InvalidDataException($"'{text}' is not a firmware version such as 4.50.");
        string minor = parts.Length > 1 ? parts[1] : "00";
        if (minor.Length == 1) minor += "0";
        if (minor.Length != 2 || !minor.All(char.IsAsciiHexDigit))
            throw new InvalidDataException($"'{text}' is not a firmware version such as 4.50.");
        string head = $"{major:D2}{minor}";
        int total = existing is { Length: > 2 } && existing.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? existing.Length - 2
            : 16;
        return "0x" + head.PadRight(Math.Max(total, 4), '0');
    }

    // ---------------------------------------------------------------- entry point

    /// <summary>
    /// Applies <paramref name="edit"/> to <paramref name="source"/>. When <paramref name="output"/> is empty or the
    /// same as the source the file is changed in place, otherwise a new file or folder is written.
    /// </summary>
    public static async Task ApplyAsync(string source, string output, bool fromPackage, MetadataEdit edit,
        string passcode, string backendId, IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        if (edit.IsEmpty) throw new InvalidOperationException("There is nothing to write.");
        bool inPlace = output.Length == 0 ||
                       string.Equals(Path.GetFullPath(output), Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase);
        string target = inPlace ? Path.GetFullPath(source) : Path.GetFullPath(output);
        if (!inPlace && (File.Exists(target) || Directory.Exists(target)))
            throw new IOException("The output already exists: " + target);

        string work = Path.Combine(Path.GetTempPath(), "Packwright-edit-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            byte[]? icon = edit.IconPath.Length > 0 ? await File.ReadAllBytesAsync(edit.IconPath, token).ConfigureAwait(false) : null;
            Logger.Info($"Writing edited details into {source}" + (inPlace ? string.Empty : " -> " + target));
            if (Directory.Exists(source))
            {
                await EditFolderAsync(source, target, inPlace, edit, icon, progress, token).ConfigureAwait(false);
            }
            else if (fromPackage)
            {
                await EditPackageAsync(source, target, edit, icon, passcode, backendId, work, progress, token).ConfigureAwait(false);
            }
            else
            {
                switch (Ps5ImageFormatProbe.Detect(source))
                {
                    case Ps5ImageFormat.Exfat:
                        await EditCopyOrInPlaceAsync(source, target, inPlace, progress,
                            path => EditExfatAsync(path, edit, icon, work, progress, token)).ConfigureAwait(false);
                        break;
                    case Ps5ImageFormat.Ufs2:
                        await EditCopyOrInPlaceAsync(source, target, inPlace, progress,
                            path => EditUfs2Async(path, edit, icon, work, progress, token)).ConfigureAwait(false);
                        break;
                    case Ps5ImageFormat.Pfs:
                        await EditPfsAsync(source, target, edit, icon, work, progress, token).ConfigureAwait(false);
                        break;
                    case Ps5ImageFormat.ZArchive:
                        await EditZArchiveAsync(source, target, edit, icon, progress, token).ConfigureAwait(false);
                        break;
                    default:
                        throw new InvalidDataException("This file type cannot be edited.");
                }
            }
            Logger.Info("Edited details written.");
        }
        finally
        {
            try { Directory.Delete(work, true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Stage(IProgress<PackageTaskProgress> progress, string text) =>
        progress.Report(new PackageTaskProgress(text, 0, 0, 0, 0, 0, 0, string.Empty));

    // ---------------------------------------------------------------- dump folder

    private static async Task EditFolderAsync(string source, string target, bool inPlace, MetadataEdit edit,
        byte[]? icon, IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        string root = source;
        if (!inPlace)
        {
            Stage(progress, "Copying folder");
            await Task.Run(() => CopyDirectory(source, target, token), token).ConfigureAwait(false);
            root = target;
        }
        try
        {
            Stage(progress, "Writing details");
            string param = Path.Combine(root, "sce_sys", "param.json");
            byte[]? existing = File.Exists(param) ? await File.ReadAllBytesAsync(param, token).ConfigureAwait(false) : null;
            byte[] patched = PatchParam(existing, edit);
            await WriteFileAsync(param, patched, token).ConfigureAwait(false);
            if (icon is not null)
                await WriteFileAsync(Path.Combine(root, "sce_sys", "icon0.png"), icon, token).ConfigureAwait(false);
        }
        catch when (!inPlace)
        {
            try { Directory.Delete(target, true); } catch (IOException) { }
            throw;
        }
    }

    private static async Task WriteFileAsync(string path, byte[] data, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".packwright-tmp";
        await File.WriteAllBytesAsync(temp, data, token).ConfigureAwait(false);
        File.Move(temp, path, true);
    }

    private static void CopyDirectory(string source, string target, CancellationToken token)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source))
        {
            token.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }
        foreach (string directory in Directory.EnumerateDirectories(source))
            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)), token);
    }

    // ---------------------------------------------------------------- exFAT and FFPKG (UFS2)

    private static async Task EditCopyOrInPlaceAsync(string source, string target, bool inPlace,
        IProgress<PackageTaskProgress> progress, Func<string, Task> edit)
    {
        if (!inPlace)
        {
            Stage(progress, "Copying image");
            await Task.Run(() => File.Copy(source, target)).ConfigureAwait(false);
        }
        try
        {
            await edit(target).ConfigureAwait(false);
        }
        catch when (!inPlace)
        {
            try { File.Delete(target); } catch (IOException) { }
            throw;
        }
    }

    private static async Task EditExfatAsync(string image, MetadataEdit edit, byte[]? icon, string work,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        byte[]? param;
        bool hasIcon, hasSys;
        using (var stream = File.OpenRead(image))
        using (var volume = new ExfatVolume(stream))
        {
            param = volume.Find(ParamPath) is null ? null : volume.ReadAllBytes(ParamPath, MaximumParamBytes);
            hasIcon = volume.Find(IconPath) is not null;
            hasSys = volume.Find("sce_sys") is not null;
        }
        string paramFile = Path.Combine(work, "param.json");
        await File.WriteAllBytesAsync(paramFile, PatchParam(param, edit), token).ConfigureAwait(false);
        var operations = new List<ExfatEditOperation>();
        if (!hasSys) operations.Add(ExfatEditOperation.AddDirectory("sce_sys"));
        operations.Add(param is null ? ExfatEditOperation.AddFile(ParamPath, paramFile) : ExfatEditOperation.Replace(ParamPath, paramFile));
        if (icon is not null)
        {
            string iconFile = Path.Combine(work, "icon0.png");
            await File.WriteAllBytesAsync(iconFile, icon, token).ConfigureAwait(false);
            operations.Add(hasIcon ? ExfatEditOperation.Replace(IconPath, iconFile) : ExfatEditOperation.AddFile(IconPath, iconFile));
        }
        await ExfatImageMaintenance.ApplyEditsAsync(image, operations, Jobs.Adapt(progress), token).ConfigureAwait(false);
    }

    private static async Task EditUfs2Async(string image, MetadataEdit edit, byte[]? icon, string work,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        byte[]? param;
        bool hasIcon, hasSys;
        using (var volume = new Ufs2Volume(image))
        {
            bool Has(string path, bool directory) => volume.Entries.Any(entry => entry.IsDirectory == directory &&
                entry.Path.Trim('/').Equals(path, StringComparison.OrdinalIgnoreCase));
            param = Has(ParamPath, false) ? volume.ReadAllBytes(ParamPath, MaximumParamBytes) : null;
            hasIcon = Has(IconPath, false);
            hasSys = Has("sce_sys", true);
        }
        string paramFile = Path.Combine(work, "param.json");
        await File.WriteAllBytesAsync(paramFile, PatchParam(param, edit), token).ConfigureAwait(false);
        var operations = new List<Ufs2EditOperation>();
        if (!hasSys) operations.Add(Ufs2EditOperation.AddDirectory("sce_sys"));
        operations.Add(param is null ? Ufs2EditOperation.AddFile(ParamPath, paramFile) : Ufs2EditOperation.Replace(ParamPath, paramFile));
        if (icon is not null)
        {
            string iconFile = Path.Combine(work, "icon0.png");
            await File.WriteAllBytesAsync(iconFile, icon, token).ConfigureAwait(false);
            operations.Add(hasIcon ? Ufs2EditOperation.Replace(IconPath, iconFile) : Ufs2EditOperation.AddFile(IconPath, iconFile));
        }
        await Ufs2Operations.ApplyEditsAsync(image, operations, Jobs.AdaptUfs2(progress), token).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- FFPFSC

    private static async Task EditPfsAsync(string source, string target, MetadataEdit edit, byte[]? icon, string work,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        string temporary = Path.Combine(work, "image.exfat");
        Stage(progress, "Unpacking FFPFSC");
        await Jobs.ConvertAsync(source, temporary, Ps5ImageConversionTarget.Exfat, false, false, progress, token,
            new ExfatBuildOptions { GenerateAmprIndex = true }).ConfigureAwait(false);
        await EditExfatAsync(temporary, edit, icon, work, progress, token).ConfigureAwait(false);

        string partial = target + ".packwright-tmp";
        try
        {
            Stage(progress, "Building FFPFSC");
            await Jobs.ConvertAsync(temporary, partial, Ps5ImageConversionTarget.Ffpfsc, true, false, progress, token,
                null, new FfpfscBuildOptions
                {
                    Compression = new PfscCompressionOptions { CompressionLevel = 7, MinimumGainPercent = 1 }
                }).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await Jobs.VerifyImageAsync(partial, Ps5ImageFormat.Pfs, token).ConfigureAwait(false);
            File.Move(partial, target, true);
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- ZArchive

    private static async Task EditZArchiveAsync(string source, string target, MetadataEdit edit, byte[]? icon,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        var replacements = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        using (var volume = new ZArchiveVolume(source))
        {
            ZArchiveEntry[] found = volume.Entries.Where(entry => !entry.IsDirectory &&
                (entry.Path.Equals(ParamPath, StringComparison.OrdinalIgnoreCase) ||
                 entry.Path.EndsWith("/" + ParamPath, StringComparison.OrdinalIgnoreCase))).ToArray();
            if (found.Length > 1)
                throw new InvalidDataException("The archive contains several game roots; it cannot be edited as one title.");
            string prefix;
            byte[]? existing = null;
            if (found.Length == 1)
            {
                prefix = found[0].Path[..^ParamPath.Length];
                existing = volume.ReadAllBytes(found[0].Path, MaximumParamBytes);
            }
            else
            {
                // No param.json yet: put one in the archive's sce_sys folder, or at the top.
                string? sys = volume.Entries.Where(entry => entry.IsDirectory &&
                        (entry.Path.Equals("sce_sys", StringComparison.OrdinalIgnoreCase) ||
                         entry.Path.EndsWith("/sce_sys", StringComparison.OrdinalIgnoreCase)))
                    .OrderBy(entry => entry.Path.Length).FirstOrDefault()?.Path;
                prefix = sys is null ? string.Empty : sys[..^"sce_sys".Length];
            }
            replacements[prefix + ParamPath] = PatchParam(existing, edit);
            if (icon is not null) replacements[prefix + IconPath] = icon;
        }

        string partial = target + ".packwright-tmp";
        try
        {
            await ZArchiveImage.RewriteWithReplacementsAsync(source, partial, replacements, Jobs.Adapt(progress), token)
                .ConfigureAwait(false);
            Stage(progress, "Verifying ZArchive");
            await ZArchiveImage.VerifyAsync(partial, null, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            File.Move(partial, target, true);
        }
        finally
        {
            try { File.Delete(partial); } catch (IOException) { }
        }
    }

    // ---------------------------------------------------------------- packages

    private static async Task EditPackageAsync(string source, string target, MetadataEdit edit, byte[]? icon,
        string passcode, string backendId, string work, IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        string temporary = Path.Combine(work, "package.exfat");
        Stage(progress, "Unpacking package");
        await Jobs.ConvertAsync(source, temporary, Ps5ImageConversionTarget.Exfat, false, true, progress, token,
            new ExfatBuildOptions { GenerateAmprIndex = true }).ConfigureAwait(false);
        await EditExfatAsync(temporary, edit, icon, work, progress, token).ConfigureAwait(false);

        string contentId = Jobs.ReadContentId(temporary, Ps5ImageFormat.Exfat);
        if (contentId.Length == 0) throw new InvalidDataException("The package has no content ID, so it cannot be rebuilt.");
        IPackageBackend backend = Jobs.ResolveBackend(backendId);
        string code = passcode.Length > 0 ? passcode : Packwright.Core.Builders.SonyDebugPackageCredentials.DefaultPasscode;
        Stage(progress, "Rebuilding package");
        await Jobs.BuildPackageAsync(temporary, target, contentId, code, true, new PackageBuildSettings(), backend,
            progress, token).ConfigureAwait(false);
    }
}
