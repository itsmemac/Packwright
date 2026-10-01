using System.Collections;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Packwright.App.Models;
using Packwright.Core.Models;
using Packwright.Core.Services;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

/// <summary>One planned rename: the source, where it would land and whether a numbered name was needed.</summary>
public sealed record RenamePlan(Ps5GameInfo Game, string Source, string Target, bool Conflict, string? Error)
{
    public bool Unchanged => Error is null && Target.Length == 0;
}

/// <summary>File operations behind the library context menu: rename, move, delete, export and duplicates.</summary>
public static class LibraryActions
{
    public static string DefaultRenameFormat(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.RenameFormat) ? AppSettingsNormalizer.DefaultRenameFormat : settings.RenameFormat.Trim();

    private static Ps5RenameTokens Tokens(Ps5GameInfo game) => new(
        Title: game.Title,
        TitleId: game.TitleId,
        ContentId: game.ContentId,
        ConceptId: game.ConceptId,
        Version: game.DisplayVersion,
        ContentVersion: game.ContentVersion,
        MasterVersion: game.MasterVersion,
        Category: LibraryItem.CategoryOf(game),
        Region: LibraryItem.RegionOf(game),
        Platform: game.Platform,
        SystemVersion: game.RequiredSystemSoftware,
        SdkVersion: game.SdkVersion,
        Source: game.SourceDescription,
        Size: game.SourceSize > 0 ? LibraryItem.FormatBytes(game.SourceSize) : string.Empty,
        Language: game.DefaultLanguage,
        Drm: game.DrmType,
        Date: game.CreationDate,
        Tool: game.ToolVersion);

    public static IReadOnlyList<RenamePlan> PlanRenames(IEnumerable<Ps5GameInfo> games, string format) =>
        games.Select(game => Resolve(game, Ps5RenameFormatter.Expand(format, Tokens(game), FallbackName(game)))).ToList();

    private static string FallbackName(Ps5GameInfo game)
    {
        string name = Path.GetFileNameWithoutExtension(game.RootPath);
        return string.IsNullOrWhiteSpace(name) ? "PS5_GAME" : name;
    }

    private static RenamePlan Resolve(Ps5GameInfo game, string baseName)
    {
        string source = game.RootPath;
        bool isDirectory = Directory.Exists(source);
        if (!isDirectory && !File.Exists(source))
            return new RenamePlan(game, source, string.Empty, false, "source path no longer exists");
        string safe = Ps5RenameFormatter.Sanitize(baseName);
        if (safe.Length == 0) return new RenamePlan(game, source, string.Empty, false, "empty name");
        string? parent = Path.GetDirectoryName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrEmpty(parent)) return new RenamePlan(game, source, string.Empty, false, "no parent folder");
        string extension = isDirectory ? string.Empty : Path.GetExtension(source);
        string raw = Path.Combine(parent, safe + extension);
        if (string.Equals(raw, source, StringComparison.OrdinalIgnoreCase))
            return new RenamePlan(game, source, string.Empty, false, null);
        bool conflict = File.Exists(raw) || Directory.Exists(raw);
        return new RenamePlan(game, source, conflict ? MakeUnique(raw, isDirectory) : raw, conflict, null);
    }

    /// <summary>
    /// Numbers packages per Title ID in install order (base game, then updates, then add-ons), so sorting
    /// the files by name installs them in the right order.
    /// </summary>
    public static IReadOnlyList<RenamePlan> PlanInstallOrder(IEnumerable<Ps5GameInfo> games, string format)
    {
        var plans = new List<RenamePlan>();
        foreach (IGrouping<string, Ps5GameInfo> group in games
                     .Where(game => game.SourceKind == Ps5SourceKind.SonyPackage && !string.IsNullOrWhiteSpace(game.TitleId))
                     .GroupBy(game => game.TitleId, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => group.Key, StringComparer.OrdinalIgnoreCase))
        {
            List<Ps5GameInfo> ordered = group
                .OrderBy(game => CategoryPriority(LibraryItem.CategoryOf(game)))
                .ThenBy(game => game.DisplayVersion, StringComparer.OrdinalIgnoreCase)
                .ThenBy(game => game.RootPath, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (int index = 0; index < ordered.Count; index++)
            {
                Ps5GameInfo game = ordered[index];
                string name = Ps5RenameFormatter.Expand(format, Tokens(game), FallbackName(game));
                plans.Add(Resolve(game, $"{index:D2} - {name}"));
            }
        }
        return plans;
    }

    private static int CategoryPriority(string category) => category switch
    {
        "Game" => 0,
        "Patch" => 1,
        "DLC" or "Add-on" => 2,
        "App" => 3,
        _ => 4
    };

    /// <summary>Lists updates and add-ons whose Title ID has no base game in the library.</summary>
    public static string MissingBaseReport(IEnumerable<LibraryItem> items)
    {
        List<LibraryItem> all = items.ToList();
        HashSet<string> bases = all.Where(item => item.Category == "Game" && item.TitleId.Length > 0)
            .Select(item => item.TitleId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var text = new StringBuilder();
        foreach (LibraryItem item in all
                     .Where(item => item.Category is "Patch" or "DLC" or "Add-on" && item.TitleId.Length > 0 &&
                                    !bases.Contains(item.TitleId))
                     .OrderBy(item => item.TitleId, StringComparer.OrdinalIgnoreCase))
            text.AppendLine($"{item.TitleId}  {item.Category,-6}  v{item.Version,-9}  {item.Title}\n      {item.Path}");
        return text.Length == 0 ? "Every update and add-on has a base game in the library." : text.ToString();
    }

    public static string MakeUnique(string target, bool isDirectory)
    {
        if (!File.Exists(target) && !Directory.Exists(target)) return target;
        string parent = Path.GetDirectoryName(target) ?? string.Empty;
        string name = isDirectory ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
        string extension = isDirectory ? string.Empty : Path.GetExtension(target);
        for (int index = 2; index < 10000; index++)
        {
            string candidate = Path.Combine(parent, $"{name} ({index}){extension}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
        return target;
    }

    public static string PreviewText(IReadOnlyList<RenamePlan> plans)
    {
        var text = new StringBuilder();
        foreach (RenamePlan plan in plans)
        {
            string name = Path.GetFileName(plan.Source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (plan.Error is not null) text.AppendLine($"x  {name}  ({plan.Error})");
            else if (plan.Unchanged) text.AppendLine($"=  {name}  (unchanged)");
            else
            {
                text.AppendLine($"   {name}");
                text.AppendLine($"-> {Path.GetFileName(plan.Target)}{(plan.Conflict ? "   [name already taken]" : string.Empty)}");
            }
        }
        return text.ToString();
    }

    /// <summary>Applies the plans and returns how many items were renamed.</summary>
    public static int ApplyRenames(IEnumerable<RenamePlan> plans)
    {
        int renamed = 0;
        foreach (RenamePlan plan in plans.Where(plan => plan.Error is null && plan.Target.Length > 0))
        {
            try
            {
                if (Directory.Exists(plan.Source)) Directory.Move(plan.Source, plan.Target);
                else File.Move(plan.Source, plan.Target);
                renamed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                Logger.Warn($"Rename skipped for '{plan.Source}': {ex.Message}");
            }
        }
        return renamed;
    }

    public static int MoveAll(IEnumerable<Ps5GameInfo> games, string destination, CancellationToken token)
    {
        Directory.CreateDirectory(destination);
        int moved = 0;
        foreach (Ps5GameInfo game in games)
        {
            string source = game.RootPath;
            bool isDirectory = Directory.Exists(source);
            if (!isDirectory && !File.Exists(source)) continue;
            string name = Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            string target = MakeUnique(Path.Combine(destination, name), isDirectory);
            try
            {
                LibraryFileMover.Move(source, target, token);
                moved++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Move skipped for '{source}': {ex.Message}");
            }
        }
        return moved;
    }

    public static int DeleteAll(IEnumerable<Ps5GameInfo> games)
    {
        int deleted = 0;
        foreach (Ps5GameInfo game in games)
        {
            try
            {
                if (Directory.Exists(game.RootPath)) Directory.Delete(game.RootPath, recursive: true);
                else if (File.Exists(game.RootPath)) File.Delete(game.RootPath);
                else continue;
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Logger.Warn($"Delete skipped for '{game.RootPath}': {ex.Message}");
            }
        }
        return deleted;
    }

    public static string ToCsv(IEnumerable<LibraryItem> items)
    {
        var text = new StringBuilder();
        text.AppendLine("Title,TitleId,ContentId,Category,Region,Format,SizeBytes,Version,RequiredFirmware,DRM,FileName,Path");
        foreach (LibraryItem item in items)
            text.AppendLine(string.Join(",", new[]
            {
                item.Title, item.TitleId, item.ContentId, item.Category, item.Region, item.Format,
                item.SizeBytes.ToString(), item.Version, item.Firmware, item.Game.DrmType, item.FileName, item.Path
            }.Select(Csv)));
        return text.ToString();
    }

    private static string Csv(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    /// <summary>Groups items that share the same param.json bytes (exact copies) or the same content ID.</summary>
    public static string DuplicatesReport(IEnumerable<LibraryItem> items)
    {
        var text = new StringBuilder();
        int groups = 0;
        foreach (IGrouping<string, LibraryItem> group in items
                     .Where(item => item.Game.RawParamJson.Length > 0)
                     .GroupBy(item => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.Game.RawParamJson))))
                     .Where(group => group.Count() > 1))
        {
            groups++;
            text.AppendLine($"Identical param.json ({group.Count()} copies): {group.First().Title}");
            foreach (LibraryItem item in group) text.AppendLine("   " + item.Path);
        }
        var identical = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (IGrouping<string, LibraryItem> group in items
                     .Where(item => item.ContentId.Length > 0)
                     .GroupBy(item => item.ContentId + "|" + item.Version, StringComparer.OrdinalIgnoreCase)
                     .Where(group => group.Count() > 1))
        {
            if (group.Select(item => item.Path).All(identical.Contains)) continue;
            groups++;
            text.AppendLine($"Same content ID and version ({group.Count()}): {group.First().Title}");
            foreach (LibraryItem item in group) text.AppendLine("   " + item.Path);
        }
        return groups == 0 ? "No duplicates found." : text.ToString();
    }

    /// <summary>Renders a parsed model as readable "name: value" lines (lists are summarised and truncated).</summary>
    public static string Describe(object? value, int maxListItems = 40)
    {
        if (value is null) return "Nothing to show.";
        var text = new StringBuilder();
        Write(text, value, 0, maxListItems);
        return text.ToString();
    }

    private static void Write(StringBuilder text, object value, int depth, int maxItems)
    {
        string indent = new(' ', depth * 2);
        foreach (PropertyInfo property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            object? item;
            try { item = property.GetValue(value); }
            catch (TargetInvocationException) { continue; }
            if (item is null || item is byte[]) continue;
            if (item is string or bool or IFormattable || item.GetType().IsEnum)
            {
                text.AppendLine($"{indent}{property.Name}: {item}");
            }
            else if (item is IEnumerable sequence)
            {
                List<object> list = sequence.Cast<object>().ToList();
                text.AppendLine($"{indent}{property.Name}: {list.Count} item(s)");
                if (depth >= 1) continue;
                foreach (object entry in list.Take(maxItems))
                {
                    if (entry is string or IFormattable) text.AppendLine($"{indent}  - {entry}");
                    else
                    {
                        text.AppendLine($"{indent}  -");
                        Write(text, entry, depth + 2, maxItems);
                    }
                }
                if (list.Count > maxItems) text.AppendLine($"{indent}  ... {list.Count - maxItems} more");
            }
            else if (depth < 1)
            {
                text.AppendLine($"{indent}{property.Name}:");
                Write(text, item, depth + 1, maxItems);
            }
        }
    }
}
