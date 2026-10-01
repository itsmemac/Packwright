using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Packwright.App.Models;
using Packwright.Core.Services;

namespace Packwright.App.Services;

/// <summary>Writes the library as a JSON file or as one self-contained web page. Nothing here uses the network.</summary>
public static class LibraryExport
{
    private const int IconWidth = 96;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record Row(string Title, string TitleId, string ContentId, string Category, string Role, string Region,
        string Format, long SizeBytes, string Version, string Firmware, string Drm, string Sdk, string FileName,
        bool Edited, string[] Missing, string? Path);

    private static Row ToRow(LibraryItem item, bool includePaths) => new(
        item.Title, item.TitleId, item.ContentId, item.Category, item.Role, item.Region, item.Format, item.SizeBytes,
        item.Version, item.Firmware, item.Game.DrmType, item.Game.SdkVersion, item.FileName, item.IsEdited,
        [.. item.MissingFields], includePaths ? item.Path : null);

    public static string ToJson(IReadOnlyCollection<LibraryItem> items, bool includePaths)
    {
        var document = new
        {
            generator = "Packwright " + UpdateService.CurrentVersion,
            exportedUtc = DateTime.UtcNow,
            count = items.Count,
            totalBytes = items.Sum(item => item.SizeBytes),
            items = items.Select(item => ToRow(item, includePaths)).ToList()
        };
        return JsonSerializer.Serialize(document, JsonOptions);
    }

    /// <summary>A small PNG of the title's artwork for the web page, or null when it has none.</summary>
    public static byte[]? ReadSmallIcon(LibraryItem item)
    {
        try
        {
            if (ThumbnailService.ReadIcon(item) is not { Length: > 0 } bytes) return null;
            using var input = new MemoryStream(bytes);
            using Bitmap bitmap = Bitmap.DecodeToWidth(input, IconWidth, BitmapInterpolationMode.MediumQuality);
            using var output = new MemoryStream();
            bitmap.Save(output);
            return output.ToArray();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException
                                       or NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    public static string ToHtml(IReadOnlyCollection<LibraryItem> items, bool includePaths,
        IReadOnlyDictionary<string, byte[]>? icons)
    {
        static string H(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);
        long total = items.Sum(item => item.SizeBytes);
        var html = new StringBuilder(64 * 1024);
        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src data:; style-src 'unsafe-inline'; script-src 'unsafe-inline'">
            <title>PS5 library</title>
            <style>
            :root{--bg:#f4f5f8;--card:#fff;--text:#15171c;--muted:#666d7a;--line:#e1e4ea;--accent:#2f6fed;--chip:#eef1f7}
            @media (prefers-color-scheme:dark){:root{--bg:#14161a;--card:#1d2026;--text:#e8eaee;--muted:#9aa1ad;--line:#2b3038;--accent:#6b9bff;--chip:#272b33}}
            *{box-sizing:border-box}body{margin:0;background:var(--bg);color:var(--text);font:14px/1.45 system-ui,Segoe UI,Roboto,sans-serif}
            main{max-width:1280px;margin:0 auto;padding:28px 18px 48px}h1{margin:0 0 4px;font-size:26px}
            .muted{color:var(--muted)}.stats{display:flex;flex-wrap:wrap;gap:10px;margin:18px 0}
            .stat{background:var(--card);border:1px solid var(--line);border-radius:12px;padding:10px 16px}
            .stat b{display:block;font-size:20px}.bar{display:flex;gap:10px;margin:0 0 12px}
            input{flex:1;padding:9px 12px;border-radius:10px;border:1px solid var(--line);background:var(--card);color:var(--text);font:inherit}
            .wrap{background:var(--card);border:1px solid var(--line);border-radius:12px;overflow:auto}
            table{border-collapse:collapse;width:100%}th,td{padding:8px 12px;text-align:left;border-bottom:1px solid var(--line);vertical-align:middle}
            th{position:sticky;top:0;background:var(--card);cursor:pointer;user-select:none;white-space:nowrap;font-size:12px;text-transform:uppercase;letter-spacing:.04em;color:var(--muted)}
            th.asc::after{content:" \25B2"}th.desc::after{content:" \25BC"}tr:last-child td{border-bottom:0}
            td.icon{width:56px;padding-right:0}td.icon img{width:44px;height:44px;border-radius:9px;display:block}
            td.icon .ph{width:44px;height:44px;border-radius:9px;background:var(--chip)}
            .t{font-weight:600}.sub{color:var(--muted);font-size:12px}.num{text-align:right;white-space:nowrap}
            .pill{display:inline-block;padding:1px 8px;border-radius:99px;background:var(--chip);font-size:12px}
            .warn{color:#c47a00}footer{margin-top:18px;font-size:12px;color:var(--muted)}
            </style></head><body><main>
            """);
        html.Append("<h1>PS5 library</h1><div class=\"muted\">")
            .Append(H($"Exported {DateTime.Now:d MMMM yyyy, HH:mm} with Packwright {UpdateService.CurrentVersion}"))
            .Append("</div><div class=\"stats\">");
        Stat(html, items.Count.ToString("N0"), "titles");
        Stat(html, Ps5LibraryHealth.FormatBytes(total), "total size");
        foreach (IGrouping<string, LibraryItem> group in items.GroupBy(item => item.Category.Length > 0 ? item.Category : "Other")
                     .OrderByDescending(group => group.Count()))
            Stat(html, group.Count().ToString("N0"), group.Key);
        int missing = items.Count(item => item.HasMissing);
        if (missing > 0) Stat(html, missing.ToString("N0"), "with missing details");
        html.Append("</div><div class=\"bar\"><input id=\"q\" type=\"search\" placeholder=\"Search title, ID, version...\" autofocus></div>");
        html.Append("<div class=\"wrap\"><table id=\"t\"><thead><tr><th data-n=\"0\" style=\"cursor:default\"></th>")
            .Append("<th>Title</th><th>Category</th><th>Region</th><th>Format</th><th>Version</th><th>Firmware</th><th class=\"num\" data-num=\"1\">Size</th>");
        if (includePaths) html.Append("<th>Path</th>");
        html.Append("</tr></thead><tbody>");
        foreach (LibraryItem item in items.OrderBy(item => item.Title, StringComparer.CurrentCultureIgnoreCase))
        {
            html.Append("<tr><td class=\"icon\">");
            if (icons is not null && icons.TryGetValue(item.Path, out byte[]? png))
                html.Append("<img alt=\"\" src=\"data:image/png;base64,").Append(Convert.ToBase64String(png)).Append("\">");
            else html.Append("<div class=\"ph\"></div>");
            html.Append("</td><td><div class=\"t\">").Append(H(item.Title)).Append("</div><div class=\"sub\">")
                .Append(H(item.TitleId.Length > 0 ? item.TitleId : item.FileName));
            if (item.ContentId.Length > 0) html.Append("  ·  ").Append(H(item.ContentId));
            html.Append("</div>");
            if (item.HasMissing) html.Append("<div class=\"sub warn\">").Append(H(item.MissingText)).Append("</div>");
            html.Append("</td><td><span class=\"pill\">").Append(H(item.Category)).Append("</span></td><td>").Append(H(item.Region))
                .Append("</td><td>").Append(H(item.Format)).Append("</td><td>").Append(H(item.Version)).Append("</td><td>")
                .Append(H(item.Firmware)).Append("</td><td class=\"num\" data-v=\"").Append(item.SizeBytes).Append("\">")
                .Append(H(item.SizeText)).Append("</td>");
            if (includePaths) html.Append("<td class=\"sub\">").Append(H(item.Path)).Append("</td>");
            html.Append("</tr>");
        }
        html.Append("""
            </tbody></table></div>
            <footer>Made with Packwright. This page is a single file; it loads nothing from the internet.</footer>
            </main><script>
            (function(){var t=document.getElementById('t'),b=t.tBodies[0],q=document.getElementById('q');
            q.addEventListener('input',function(){var s=q.value.toLowerCase();
              for(var i=0;i<b.rows.length;i++)b.rows[i].style.display=b.rows[i].textContent.toLowerCase().indexOf(s)<0?'none':'';});
            var heads=t.tHead.rows[0].cells;
            for(let c=1;c<heads.length;c++)heads[c].addEventListener('click',function(){
              var asc=!heads[c].classList.contains('asc');
              for(var k=0;k<heads.length;k++)heads[k].classList.remove('asc','desc');
              heads[c].classList.add(asc?'asc':'desc');
              var rows=[].slice.call(b.rows),num=heads[c].hasAttribute('data-num');
              function key(r){var x=r.cells[c];return num?+x.getAttribute('data-v'):x.textContent.trim().toLowerCase();}
              rows.sort(function(a,z){var p=key(a),r=key(z);return(p<r?-1:p>r?1:0)*(asc?1:-1);});
              rows.forEach(function(r){b.appendChild(r);});});})();
            </script></body></html>
            """);
        return html.ToString();
    }

    private static void Stat(StringBuilder html, string value, string label) =>
        html.Append("<div class=\"stat\"><b>").Append(WebUtility.HtmlEncode(value)).Append("</b><span class=\"muted\">")
            .Append(WebUtility.HtmlEncode(label)).Append("</span></div>");
}
