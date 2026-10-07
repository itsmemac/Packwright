using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Packwright.Core.Services;

/// <summary>
/// Small, targeted edits of a title's <c>sce_sys/param.json</c>. The file is rewritten the way the SDK tools write
/// it: keys in ordinal order, two-space indent, CRLF line endings and a trailing newline.
/// </summary>
public static class Ps5ParamJson
{
    // requiredSystemSoftwareVersion is never raised above this (firmware 9.00)
    private const ulong RequiredSystemSoftwareVersionCap = 0x0900000000000000UL;

    public static byte[] ApplyDrmType(ReadOnlySpan<byte> source, string drmType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(drmType);
        JsonObject root = Parse(source);
        root["applicationDrmType"] = drmType;
        return Serialize(root);
    }

    /// <summary>Stamps a full 64-bit executable SDK id: sdkVersion becomes its package version and the required firmware is raised to match.</summary>
    public static byte[] ApplySdkOverride(ReadOnlySpan<byte> source, ulong executableVersion)
    {
        JsonObject root = Parse(source);
        ulong package = executableVersion & 0xFFFF000000000000UL;
        root["sdkVersion"] = VersionText(package);
        ulong required = Math.Max(Math.Min(HexVersion(root["requiredSystemSoftwareVersion"]), RequiredSystemSoftwareVersionCap), package);
        root["requiredSystemSoftwareVersion"] = VersionText(required);
        return Serialize(root);
    }

    /// <summary>The value of a top-level string property, or null.</summary>
    public static string? ReadString(ReadOnlySpan<byte> source, string name)
    {
        try { return Parse(source)[name]?.GetValue<string>(); }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or FormatException) { return null; }
    }

    private static JsonObject Parse(ReadOnlySpan<byte> source)
    {
        try
        {
            return JsonNode.Parse(source) as JsonObject ?? throw new InvalidDataException("sce_sys/param.json must contain a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("sce_sys/param.json is not valid JSON.", ex);
        }
    }

    private static byte[] Serialize(JsonObject root)
    {
        string text = Sorted(root).ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        text = text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace("\n", "\r\n", StringComparison.Ordinal) + "\r\n";
        return new UTF8Encoding(false).GetBytes(text);
    }

    private static JsonNode Sorted(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var sorted = new JsonObject();
            foreach (KeyValuePair<string, JsonNode?> item in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                sorted[item.Key] = Sorted(item.Value);
            return sorted;
        }
        if (node is JsonArray array)
        {
            var copy = new JsonArray();
            foreach (JsonNode? item in array) copy.Add(Sorted(item));
            return copy;
        }
        return node?.DeepClone() ?? JsonValue.Create((string?)null)!;
    }

    private static ulong HexVersion(JsonNode? node)
    {
        string? text = node?.GetValue<string>();
        return text is not null && text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            && ulong.TryParse(text.AsSpan(2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out ulong value) ? value : 0;
    }

    private static string VersionText(ulong value) => $"0x{value:x16}";
}
