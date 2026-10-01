using System.Text.Json;
using System.Text.RegularExpressions;

namespace Packwright.Core.Services;

/// <summary>One title found in the PlayStation Store.</summary>
public sealed record StoreResult(
    string Name, string ContentId, string TitleId, string Publisher, string ReleaseDate,
    string IconUrl, string BackgroundUrl, double Score)
{
    /// <summary>Region implied by the content ID prefix (UP = Americas, EP = Europe, ...).</summary>
    public string Region => ContentId.Length > 0
        ? char.ToUpperInvariant(ContentId[0]) switch
        {
            'U' => "Americas", 'E' => "Europe", 'J' => "Japan", 'K' => "Korea", 'H' => "Hong Kong", 'I' => "Asia",
            _ => "Other"
        }
        : string.Empty;
}

public sealed class OnlineLookupException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Looks a PS5 title up by name in the PlayStation Store's public search. Only the search text is sent; the
/// answer carries the title ID and content ID, the canonical name, the publisher and the artwork. Nothing is
/// stored or sent anywhere else, and every call is made on request, never in the background.
/// </summary>
public static partial class Ps5StoreLookup
{
    private const string SearchUrl = "https://store.playstation.com/store/api/chihiro/00_09_000/tumbler/{0}/{1}/999/{2}?suggested_size=12&mode=game";
    private const int MaximumImageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan MinimumGap = TimeSpan.FromMilliseconds(900);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static DateTime _lastRequest = DateTime.MinValue;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Packwright/1.0; +https://github.com/itsmemac/Packwright)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    /// <summary>Searches the store (US/English by default). Results are PS5 games only, best match first.</summary>
    public static async Task<IReadOnlyList<StoreResult>> SearchAsync(string query, string country = "US",
        string language = "en", CancellationToken cancellationToken = default)
    {
        query = CleanQuery(query);
        if (query.Length < 2) return [];
        string url = string.Format(SearchUrl, country, language, Uri.EscapeDataString(query));
        string json = await GetStringAsync(url, cancellationToken).ConfigureAwait(false);
        try
        {
            return Parse(json, query);
        }
        catch (JsonException ex)
        {
            throw new OnlineLookupException("The PlayStation Store answered in a format Packwright does not understand.", ex);
        }
    }

    /// <summary>
    /// Searches the US store and, unless the title ID is already matched, the UK store too, because the same game
    /// has a different title ID in each region. A result whose title ID equals <paramref name="knownTitleId"/>
    /// always ranks first.
    /// </summary>
    public static async Task<IReadOnlyList<StoreResult>> FindAsync(string name, string? knownTitleId = null,
        CancellationToken cancellationToken = default)
    {
        var all = new List<StoreResult>(await SearchAsync(name, "US", "en", cancellationToken).ConfigureAwait(false));
        bool Matches(StoreResult result) => !string.IsNullOrWhiteSpace(knownTitleId) &&
                                            result.TitleId.Equals(knownTitleId.Trim(), StringComparison.OrdinalIgnoreCase);
        if (!all.Any(Matches))
        {
            try { all.AddRange(await SearchAsync(name, "GB", "en", cancellationToken).ConfigureAwait(false)); }
            catch (OnlineLookupException) when (all.Count > 0) { }
        }
        return all.GroupBy(result => result.ContentId, StringComparer.OrdinalIgnoreCase).Select(group => group.First())
            .OrderByDescending(result => (Matches(result) ? 2.0 : 0.0) + result.Score)
            .ToList();
    }

    /// <summary>The store's search chokes on punctuation such as ' and :, so only letters, digits and hyphens are kept.</summary>
    internal static string CleanQuery(string query)
    {
        string text = query.Replace("'", string.Empty).Replace("’", string.Empty);
        text = QueryPunctuation().Replace(text, " ");
        return WhitespaceRun().Replace(text, " ").Trim();
    }

    /// <summary>
    /// The store's artwork for a title known by name and Title ID: the entry with that exact ID, or failing that
    /// the same-named entry of another region (the artwork is the same). Never used for IDs, only for the picture.
    /// Returns null when nothing close enough is found.
    /// </summary>
    public static async Task<StoreResult?> FindArtworkAsync(string name, string titleId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<StoreResult> results = await FindAsync(name, titleId, cancellationToken).ConfigureAwait(false);
        return results.FirstOrDefault(result => result.TitleId.Equals(titleId, StringComparison.OrdinalIgnoreCase) &&
                                                result.IconUrl.Length > 0)
               ?? results.FirstOrDefault(result => result.Score >= 0.9 && result.IconUrl.Length > 0);
    }

    /// <summary>Downloads store artwork. Only PlayStation image hosts are accepted.</summary>
    public static async Task<byte[]?> DownloadImageAsync(string url, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !(uri.Host.EndsWith(".playstation.com", StringComparison.OrdinalIgnoreCase) ||
              uri.Host.EndsWith(".playstation.net", StringComparison.OrdinalIgnoreCase)))
            return null;
        await WaitTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > MaximumImageBytes) return null;
            byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return data.Length is > 0 and <= MaximumImageBytes ? data : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested) throw;
            return null;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        await WaitTurnAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OnlineLookupException($"The PlayStation Store answered with an error ({(int)response.StatusCode}).");
            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OnlineLookupException("Could not reach the PlayStation Store. Check your internet connection.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnlineLookupException("The PlayStation Store did not answer in time.", ex);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>One request at a time, at least a moment apart, so a batch never hammers the store.</summary>
    private static async Task WaitTurnAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        TimeSpan wait = _lastRequest + MinimumGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero)
        {
            try { await Task.Delay(wait, cancellationToken).ConfigureAwait(false); }
            catch { Gate.Release(); throw; }
        }
        _lastRequest = DateTime.UtcNow;
    }

    internal static IReadOnlyList<StoreResult> Parse(string json, string query)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("links", out JsonElement links) || links.ValueKind != JsonValueKind.Array)
            return [];
        var results = new Dictionary<string, StoreResult>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement link in links.EnumerateArray())
        {
            string id = Text(link, "id");
            Match match = TitleIdPattern().Match(id);
            if (!match.Success || !IsPs5Game(link)) continue;
            string name = Text(link, "name");
            if (name.Length == 0) continue;
            string icon = string.Empty, background = string.Empty;
            if (link.TryGetProperty("images", out JsonElement images) && images.ValueKind == JsonValueKind.Array)
                foreach (JsonElement image in images.EnumerateArray())
                {
                    string imageUrl = Text(image, "url");
                    int type = image.TryGetProperty("type", out JsonElement t) && t.TryGetInt32(out int number) ? number : 0;
                    if (type == 10 && icon.Length == 0) icon = imageUrl;       // 1024x1024 square artwork
                    else if (type == 12 && background.Length == 0) background = imageUrl;   // 1920x1080 key art
                }
            double score = Score(name, id, query);
            results.TryAdd(id, new StoreResult(name, id, match.Value.ToUpperInvariant(), Text(link, "provider_name"),
                Text(link, "release_date"), icon, background, score));
        }
        return results.Values.OrderByDescending(result => result.Score).ThenBy(result => result.Name.Length).ToList();
    }

    private static bool IsPs5Game(JsonElement link)
    {
        if (!string.Equals(Text(link, "top_category"), "downloadable_game", StringComparison.OrdinalIgnoreCase)) return false;
        if (link.TryGetProperty("playable_platform", out JsonElement platforms) && platforms.ValueKind == JsonValueKind.Array)
            return platforms.EnumerateArray().Any(item => item.ValueKind == JsonValueKind.String &&
                                                          (item.GetString() ?? string.Empty).Contains("PS5", StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>How well a store name matches what was typed, 0 to 1. Special editions rank below the plain game.</summary>
    internal static double Score(string name, string contentId, string query)
    {
        string a = Normalize(name), b = Normalize(query);
        if (a.Length == 0 || b.Length == 0) return 0;
        double score;
        if (a == b) score = 1.0;
        else if (a.StartsWith(b, StringComparison.Ordinal) || b.StartsWith(a, StringComparison.Ordinal)) score = 0.9;
        else
        {
            HashSet<string> left = Tokens(name), right = Tokens(query);
            int shared = left.Intersect(right).Count();
            int total = left.Union(right).Count();
            score = total == 0 ? 0 : 0.85 * shared / total;
        }
        if (EditionWords().IsMatch(name) || contentId.Contains("DX", StringComparison.Ordinal)) score -= 0.12;
        return Math.Max(0, score);
    }

    private static string Normalize(string text) =>
        new string(text.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static HashSet<string> Tokens(string text) =>
        NonWord().Split(text.ToLowerInvariant()).Where(token => token.Length > 0).ToHashSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[^\p{L}\p{N}\- ]+|\s{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex QueryPunctuation();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"PPSA\d{5}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TitleIdPattern();

    [GeneratedRegex(@"\b(deluxe|digital deluxe|ultimate|premium|collector|bundle|edition|upgrade|pass|season|pack)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EditionWords();

    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex NonWord();
}
