// Packwright.Pkg - which files of a title's sce_sys folder live in the package's metadata container (as numbered
// entries) rather than in the inner image, and how each kind of entry is stored.
//
// The id/name table follows the one in LibProsperoPKG (GPL-3.0-or-later, SvenGDK); it is checked against the entry
// lists of real packages.
#nullable enable
using System.Text.RegularExpressions;

namespace Packwright.Pkg.Writer;

public static partial class PkgEntryTable
{
    public const uint Digests = 0x0001, EntryKeys = 0x0010, ImageKey = 0x0020, GeneralDigests = 0x0080, Metas = 0x0100, EntryNames = 0x0200;
    public const uint ImageDigests = 0x040A, PlayGoChunk = 0x1001, ParamJson = 0x2000, PlayGoHashTable = 0x2010, PlayGoFicm = 0x2011;
    public const uint Flags1Data = 0x08000000, Flags1Protected = 0x80000000;

    // names relative to sce_sys
    private static readonly Dictionary<string, uint> NameToId = new(StringComparer.Ordinal)
    {
        ["license.dat"] = 0x0400, ["license.info"] = 0x0401, ["nptitle.dat"] = 0x0402, ["npbind.dat"] = 0x0403,
        ["selfinfo.dat"] = 0x0404, ["imageinfo.dat"] = 0x0406, ["target-deltainfo.dat"] = 0x0407, ["origin-deltainfo.dat"] = 0x0408,
        ["psreserved.dat"] = 0x0409,
        ["param.sfo"] = 0x1000, ["playgo-chunk.dat"] = 0x1001, ["playgo-chunk.sha"] = 0x1002, ["playgo-manifest.xml"] = 0x1003,
        ["pronunciation.xml"] = 0x1004, ["pronunciation.sig"] = 0x1005, ["pic1.png"] = 0x1006, ["pubtoolinfo.dat"] = 0x1007,
        ["app/playgo-chunk.dat"] = 0x1008, ["app/playgo-chunk.sha"] = 0x1009, ["app/playgo-manifest.xml"] = 0x100A,
        ["shareparam.json"] = 0x100B, ["shareoverlayimage.png"] = 0x100C, ["save_data.png"] = 0x100D, ["shareprivacyguardimage.png"] = 0x100E,
        ["icon0.png"] = 0x1200, ["pic0.png"] = 0x1220, ["snd0.at9"] = 0x1240, ["changeinfo/changeinfo.xml"] = 0x1260,
        ["icon0.dds"] = 0x1280, ["pic0.dds"] = 0x12A0, ["pic1.dds"] = 0x12C0,
        ["trophy2/trophy00.ucp"] = 0x1480, ["uds/uds00.ucp"] = 0x14A0,
        ["param.json"] = 0x2000, ["playgo-hash-table.dat"] = 0x2010, ["playgo-ficm.dat"] = 0x2011,
        ["uds/npbind.dat"] = 0x2020, ["trophy2/npbind.dat"] = 0x2021, ["pic2.png"] = 0x2040, ["pic2.dds"] = 0x2060,
        ["playgo-scenario.json"] = 0x3000,
    };

    private static readonly Dictionary<uint, string> IdToName = BuildReverse();

    private static Dictionary<uint, string> BuildReverse()
    {
        var map = new Dictionary<uint, string>();
        foreach (var kv in NameToId) map[kv.Value] = kv.Key;
        return map;
    }

    [GeneratedRegex(@"^(icon0|pic1)_(\d\d)\.(png|dds)$")] private static partial Regex Numbered();
    [GeneratedRegex(@"^changeinfo/changeinfo_(\d\d)\.xml$")] private static partial Regex ChangeInfo();
    [GeneratedRegex(@"^entry_0x([0-9A-Fa-f]{4})\.bin$")] private static partial Regex Raw();

    /// <summary>The entry id of a sce_sys file (<paramref name="relative"/> is the path below sce_sys), or null when it belongs in the inner image.</summary>
    public static uint? IdOf(string relative)
    {
        if (relative == "imagedigs.dat") return null;           // regenerated
        if (NameToId.TryGetValue(relative, out uint id)) return id;
        Match m = Numbered().Match(relative);
        if (m.Success)
        {
            int n = int.Parse(m.Groups[2].Value);
            uint baseId = (m.Groups[1].Value, m.Groups[3].Value) switch
            {
                ("icon0", "png") => 0x1201, ("icon0", _) => 0x1281, ("pic1", "png") => 0x1241, _ => 0x12C1,
            };
            return n is >= 0 and <= 30 ? baseId + (uint)n : null;
        }
        m = ChangeInfo().Match(relative);
        if (m.Success) return 0x1261u + uint.Parse(m.Groups[1].Value);
        m = Raw().Match(relative);
        if (m.Success && uint.Parse(m.Groups[1].Value, System.Globalization.NumberStyles.HexNumber) is >= 0x0400 and <= 0x0409 and var raw) return raw;
        return null;
    }

    /// <summary>The name stored in the entry-name table (relative to sce_sys), or null for the entries that are stored without one.</summary>
    public static string? NameOf(uint id) => id is >= 0x0400 and <= 0x040A ? null : IdToName.TryGetValue(id, out string? n) ? n : NumberedName(id);

    private static string? NumberedName(uint id) => id switch
    {
        >= 0x1201 and <= 0x121F => $"icon0_{id - 0x1201:00}.png",
        >= 0x1241 and <= 0x125F => $"pic1_{id - 0x1241:00}.png",
        >= 0x1261 and <= 0x127F => $"changeinfo/changeinfo_{id - 0x1261:00}.xml",
        >= 0x1281 and <= 0x129F => $"icon0_{id - 0x1281:00}.dds",
        >= 0x12C1 and <= 0x12DF => $"pic1_{id - 0x12C1:00}.dds",
        _ => null,
    };

    /// <summary>The path below sce_sys that a metadata entry is exposed as when extracted.</summary>
    public static string? FileNameOf(uint id) => IdToName.TryGetValue(id, out string? n) ? n : NumberedName(id);

    /// <summary>True when the entry is stored encrypted (title data, licence and the bindings).</summary>
    public static bool IsProtected(uint id) => id is >= 0x0400 and <= 0x0409 and not 0x0405 || id is 0x2020 or 0x2021;

    /// <summary>The key index an entry is encrypted under.</summary>
    public static uint KeyIndex(uint id) => id == 0x0401 ? 4u : 3u;

    /// <summary>The first flags word of an entry.</summary>
    public static uint Flags1(uint id) => id switch
    {
        Digests or EntryNames => 0x40000000,
        EntryKeys or ImageKey or GeneralDigests or Metas => 0x60000000,
        ParamJson => 0,
        _ when IsProtected(id) => Flags1Protected,
        _ => Flags1Data,
    };

    public static uint Flags2(uint id) => IsProtected(id) ? KeyIndex(id) << 12 : 0;
}
