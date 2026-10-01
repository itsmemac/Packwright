using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Packwright.Core.Models;
using Packwright.Core.Parsers;
using ProsperoPkgTool.Content;

namespace Packwright.Core.Services;

/// <summary>
/// What could be worked out, without any network, about a title whose details are missing. Only fields that
/// were missing are filled in, and <see cref="Sources"/> says where each value came from so the user can judge it.
/// </summary>
public sealed class MetadataSuggestion
{
    public string Title { get; set; } = string.Empty;
    public string TitleId { get; set; } = string.Empty;
    public string ContentId { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string Firmware { get; set; } = string.Empty;

    /// <summary>Artwork found inside the title that can become its icon (not yet cropped to a square).</summary>
    public Ps5ImageData? Icon { get; set; }
    public string IconSource { get; set; } = string.Empty;

    /// <summary>Field name ("Title", "Title ID", ...) to a short description of where the value came from.</summary>
    public Dictionary<string, string> Sources { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool HasAnything =>
        Title.Length > 0 || TitleId.Length > 0 || ContentId.Length > 0 || Version.Length > 0 || Firmware.Length > 0 ||
        Icon is not null;
}

/// <summary>
/// Works out missing title details from what is inside a title (nptitle.dat, trophy data, the executable header,
/// artwork) and from its file and folder names. Dumps that were stripped of their metadata may have almost nothing
/// to go on; the result says so by simply being empty.
/// </summary>
public static partial class Ps5MetadataSuggester
{
    private static readonly HashSet<string> GenericFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "ps5", "ps4", "games", "game", "dump", "dumps", "pkg", "pkgs", "backup", "backups", "downloads", "new folder",
        "ps5 games", "ps5games", "roms", "iso", "images", "temp", "tmp", "data"
    };

    private static readonly HashSet<string> GameExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".zar", ".pkg", ".exfat", ".ffpkg", ".ffpfsc" };

    /// <summary>True when the title lacks something this class can try to supply.</summary>
    public static bool IsMissingDetails(Ps5GameInfo game) =>
        game.ParamPath.Length == 0 || string.IsNullOrWhiteSpace(game.TitleId) ||
        string.IsNullOrWhiteSpace(game.ContentId) || string.IsNullOrWhiteSpace(game.DisplayVersion) ||
        string.IsNullOrWhiteSpace(game.RequiredSystemSoftware);

    public static MetadataSuggestion Suggest(Ps5GameInfo game, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(game);
        var result = new MetadataSuggestion();
        bool needTitle = game.ParamPath.Length == 0 || game.LocalizedTitles.Count == 0;
        bool needTitleId = string.IsNullOrWhiteSpace(game.TitleId);
        bool needContentId = string.IsNullOrWhiteSpace(game.ContentId);
        bool needVersion = string.IsNullOrWhiteSpace(game.DisplayVersion);
        bool needFirmware = string.IsNullOrWhiteSpace(game.RequiredSystemSoftware);

        string[] names = NameCandidates(game);
        IReadOnlyGameFileSystem? files = null;
        try
        {
            try { files = GameFileSystem.Open(game, cancellationToken); }
            catch (Exception ex) when (IsReadFailure(ex)) { files = null; }

            // ---- Title ID: nptitle.dat inside the title is the most reliable, then the names.
            if (needTitleId)
            {
                if (files is not null && TryTitleIdFromNpTitle(files, out string fromFile))
                {
                    result.TitleId = fromFile;
                    result.Sources["Title ID"] = "sce_sys/nptitle.dat";
                }
                else if (names.Select(name => TitleIdPattern().Match(name)).FirstOrDefault(match => match.Success) is { } hit)
                {
                    result.TitleId = hit.Value.ToUpperInvariant();
                    result.Sources["Title ID"] = "file or folder name";
                }
            }
            string titleId = needTitleId ? result.TitleId : game.TitleId;

            // ---- Title: trophy data names the game; failing that, the cleaned-up file or folder name.
            if (needTitle)
            {
                string? trophyTitle = null;
                if (files is not null)
                {
                    try { trophyTitle = new Ps5TrophyReader().Read(files, game.DefaultLanguage, cancellationToken)?.Title; }
                    catch (Exception ex) when (IsReadFailure(ex)) { }
                }
                if (!string.IsNullOrWhiteSpace(trophyTitle))
                {
                    result.Title = trophyTitle.Trim();
                    result.Sources["Title"] = "trophy data";
                }
                else if (names.Select(CleanName).FirstOrDefault(name => name.Length > 0) is { } cleaned)
                {
                    result.Title = cleaned;
                    result.Sources["Title"] = "file or folder name";
                }
            }

            // ---- Version: only if a name carries one (01.200.000).
            if (needVersion &&
                names.Select(name => VersionPattern().Match(name)).FirstOrDefault(match => match.Success) is { } version)
            {
                result.Version = version.Value;
                result.Sources["Version"] = "file or folder name";
            }

            // ---- Firmware: the executable header carries the minimum firmware when the dump kept it.
            if (needFirmware && files is not null && TryFirmwareFromExecutable(files, out string firmware))
            {
                result.Firmware = firmware;
                result.Sources["Firmware"] = "executable (eboot.bin) header";
            }

            // ---- Content ID: only the format is known offline, so it is built in the debug-package style.
            // The title only feeds the ID when it is a real one, not a file name that stood in for it.
            string effectiveTitle = result.Title.Length > 0 ? result.Title
                : game.LocalizedTitles.Count > 0 ? game.Title : string.Empty;
            if (needContentId && titleId.Length > 0)
            {
                result.ContentId = BuildContentId(titleId, effectiveTitle);
                result.Sources["Content ID"] = "generated from the Title ID (debug-package style)";
            }

            // ---- Icon: a title without icon0.png may still carry the same image as a texture, or other artwork.
            if (files is not null && !files.FileExists("sce_sys/icon0.png"))
                FindIcon(files, result, cancellationToken);
        }
        finally
        {
            files?.Dispose();
        }
        return result;
    }

    // ------------------------------------------------------------------ pieces

    private static bool IsReadFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or InvalidDataException or NotSupportedException or
            ArgumentException or InvalidOperationException or FormatException;

    private static string[] NameCandidates(Ps5GameInfo game)
    {
        var names = new List<string>();
        string path = game.RootPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isFile = File.Exists(path);
        names.Add(isFile ? Path.GetFileNameWithoutExtension(path) : Path.GetFileName(path));
        string? folder = Path.GetDirectoryName(path);
        for (int depth = 0; depth < 2 && !string.IsNullOrEmpty(folder); depth++)
        {
            // A folder only names the game when it is that game's own folder, not a shelf holding many titles.
            if (!HoldsFewEntries(folder)) break;
            names.Add(Path.GetFileName(folder));
            folder = Path.GetDirectoryName(folder);
        }
        return names.Where(name => !string.IsNullOrWhiteSpace(name)).ToArray();
    }

    private static bool HoldsFewEntries(string folder)
    {
        try
        {
            string[] entries = Directory.EnumerateFileSystemEntries(folder).Take(6).ToArray();
            int games = entries.Count(entry => GameExtensions.Contains(Path.GetExtension(entry)));
            return entries.Length <= 5 && games <= 1;
        }
        catch (Exception ex) when (IsReadFailure(ex)) { return false; }
    }

    private static bool TryTitleIdFromNpTitle(IReadOnlyGameFileSystem files, out string titleId)
    {
        titleId = string.Empty;
        foreach (string name in new[] { "sce_sys/nptitle.dat", "sce_sys/npbind.dat" })
        {
            if (!files.FileExists(name)) continue;
            try
            {
                using Stream stream = files.OpenRead(name);
                byte[] buffer = new byte[Math.Min(65536, (int)Math.Min(stream.Length, int.MaxValue))];
                int read = stream.Read(buffer, 0, buffer.Length);
                Match match = TitleIdPattern().Match(Encoding.ASCII.GetString(buffer, 0, read));
                if (match.Success)
                {
                    titleId = match.Value.ToUpperInvariant();
                    return true;
                }
            }
            catch (Exception ex) when (IsReadFailure(ex)) { }
        }
        return false;
    }

    private static bool TryFirmwareFromExecutable(IReadOnlyGameFileSystem files, out string firmware)
    {
        firmware = string.Empty;
        if (!files.FileExists("eboot.bin")) return false;
        try
        {
            using Stream stream = files.OpenRead("eboot.bin");
            byte[] head = new byte[(int)Math.Min(stream.Length, 1 << 20)];
            stream.ReadExactly(head);
            if (!ProsperoSelfReader.TryParse(head, out ProsperoSelfImage? image) || image is null) return false;
            ulong value = image.ExtInfo.FirmwareVersion;
            if (value == 0) return false;
            string formatted = Ps5ParamReader.FormatSystemVersion("0x" + value.ToString("X16", CultureInfo.InvariantCulture));
            if (formatted.Length == 0 || formatted.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) return false;
            firmware = formatted;
            return true;
        }
        catch (Exception ex) when (IsReadFailure(ex))
        {
            return false;
        }
    }

    private static void FindIcon(IReadOnlyGameFileSystem files, MetadataSuggestion result, CancellationToken cancellationToken)
    {
        // Same artwork, different file: the icon as a texture first, then the large backgrounds.
        foreach (string name in new[] { "icon0.dds", "pic0.png", "pic0.dds", "pic1.png", "pic1.dds", "pic2.png", "pic2.dds" })
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = "sce_sys/" + name;
            if (!files.FileExists(path)) continue;
            try
            {
                using Stream input = files.OpenRead(path);
                if (input.Length > 64 * 1024 * 1024) continue;
                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                {
                    var memory = new MemoryStream();
                    input.CopyTo(memory);
                    result.Icon = Ps5ImageData.FromPng(memory.ToArray());
                }
                else
                {
                    (byte[] rgba, int width, int height) = Ps5ImageCodec.DecodeDdsToRgba(input);
                    result.Icon = Ps5ImageData.FromRgbaForDisplay(rgba, width, height);
                }
                result.IconSource = name;
                return;
            }
            catch (Exception ex) when (IsReadFailure(ex)) { }
        }
    }

    /// <summary>Turns a file or folder name such as "PRAGMATA PS5 Dump [v01.200]" into a plausible title.</summary>
    public static string CleanName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || GenericFolders.Contains(name.Trim())) return string.Empty;
        string text = BracketedPattern().Replace(name, " ");
        text = TitleIdPattern().Replace(text, " ");
        text = VersionTokenPattern().Replace(text, " ");
        text = AppSuffixPattern().Replace(text, " ");
        text = DumpWordPattern().Replace(text, " ");
        text = text.Replace('_', ' ').Replace('.', ' ');
        text = SeparatorRun().Replace(text, " ").Trim(' ', '-', '+');
        if (text.Length < 2 || GenericFolders.Contains(text)) return string.Empty;
        return text;
    }

    /// <summary>The debug-package style content ID: IV0000-PPSAnnnnn_00 followed by 16 letters or digits.</summary>
    public static string BuildContentId(string titleId, string title)
    {
        var tail = new StringBuilder();
        foreach (char c in title.ToUpperInvariant())
            if (c is >= 'A' and <= 'Z' or >= '0' and <= '9' && tail.Length < 16) tail.Append(c);
        while (tail.Length < 16) tail.Append('0');
        return $"IV0000-{titleId.ToUpperInvariant()}_00-{tail}";
    }

    [GeneratedRegex(@"PPSA\d{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdPattern();

    [GeneratedRegex(@"(?<![\d.])\d{2}\.\d{3}\.\d{3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"(?<![\d.])[vV]?\d{2}\.\d{3}\.\d{3}(?![\d.])", RegexOptions.CultureInvariant)]
    private static partial Regex VersionTokenPattern();

    [GeneratedRegex(@"[\[\(\{][^\]\)\}]*[\]\)\}]", RegexOptions.CultureInvariant)]
    private static partial Regex BracketedPattern();

    [GeneratedRegex(@"[-_ ]app\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AppSuffixPattern();

    [GeneratedRegex(@"\b(ps5|ps4)?\s*(dump|dumped|fpkg|pkg|repack|decrypted|patched)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DumpWordPattern();

    [GeneratedRegex(@"[\s\-]{2,}|\s+", RegexOptions.CultureInvariant)]
    private static partial Regex SeparatorRun();
}
