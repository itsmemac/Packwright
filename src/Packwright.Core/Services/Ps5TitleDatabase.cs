using System.Text;

namespace Packwright.Core.Services;

/// <summary>One PS5 title in the public title list: the same game has one entry per region, each with its own IDs.</summary>
public sealed record DbTitle(string TitleId, string ConceptId, string Name, string ContentId, string RegionCode, string PublisherId)
{
    public string Region => RegionCode.ToUpperInvariant() switch
    {
        "UP" => "Americas", "EP" => "Europe", "JP" => "Japan", "KP" => "Korea", "HP" => "Hong Kong", "IP" => "Asia",
        _ => RegionCode
    };
}

/// <summary>
/// Looks a title up by its Title ID in the community title list kept by andshrew
/// (https://github.com/andshrew/PlayStation-Titles, MIT License): every PS5 title with its name, content ID and region.
/// The list is a plain file of about 2 MB. It is downloaded once, only when a lookup is asked for, kept in the app's
/// data folder and refreshed when it is older than a month. Nothing about the user is sent: it is an ordinary file download.
/// </summary>
public static class Ps5TitleDatabase
{
    private const string Url = "https://raw.githubusercontent.com/andshrew/PlayStation-Titles/main/PS5_Titles.tsv";
    private const string FileName = "ps5-titles.tsv";
    private const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromDays(30);
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static Dictionary<string, List<DbTitle>>? _index;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Packwright/1.0; +https://github.com/itsmemac/Packwright)");
        return client;
    }

    /// <summary>The entries for a Title ID such as PPSA01880 (or PPSA01880_00). Empty when the title is not in the list.</summary>
    public static async Task<IReadOnlyList<DbTitle>> LookupAsync(string titleId, string cacheDirectory,
        CancellationToken cancellationToken = default)
    {
        string key = Normalize(titleId);
        if (key.Length == 0) return [];
        Dictionary<string, List<DbTitle>> index = await LoadAsync(cacheDirectory, cancellationToken).ConfigureAwait(false);
        return index.TryGetValue(key, out List<DbTitle>? titles) ? titles : [];
    }

    /// <summary>True when the list is already on disk (so a lookup needs no download).</summary>
    public static bool IsCached(string cacheDirectory) => File.Exists(Path.Combine(cacheDirectory, FileName));

    private static string Normalize(string titleId)
    {
        string text = titleId.Trim().ToUpperInvariant();
        int underscore = text.IndexOf('_');
        return underscore > 0 ? text[..underscore] : text;
    }

    private static async Task<Dictionary<string, List<DbTitle>>> LoadAsync(string cacheDirectory, CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string path = Path.Combine(cacheDirectory, FileName);
            bool exists = File.Exists(path);
            bool stale = !exists || DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > MaximumAge;
            if (_index is not null && !stale) return _index;

            if (stale)
            {
                try
                {
                    await DownloadAsync(path, cancellationToken).ConfigureAwait(false);
                }
                catch (OnlineLookupException) when (exists)
                {
                    // Could not refresh: the older copy on disk is still good.
                }
            }
            if (_index is null || stale)
            {
                try
                {
                    _index = Parse(await File.ReadAllLinesAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false));
                }
                catch (IOException ex)
                {
                    throw new OnlineLookupException("The title list could not be read: " + ex.Message, ex);
                }
            }
            return _index;
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task DownloadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OnlineLookupException($"The title list could not be downloaded ({(int)response.StatusCode}).");
            if (response.Content.Headers.ContentLength > MaximumBytes)
                throw new OnlineLookupException("The title list is larger than expected, so it was not downloaded.");
            byte[] data = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (data.Length is 0 or > MaximumBytes || !Encoding.UTF8.GetString(data, 0, Math.Min(data.Length, 32)).StartsWith("titleId", StringComparison.Ordinal))
                throw new OnlineLookupException("The downloaded title list is not in the expected format.");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temp = path + ".download";
            await File.WriteAllBytesAsync(temp, data, cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, true);
        }
        catch (HttpRequestException ex)
        {
            throw new OnlineLookupException("Could not reach GitHub to download the title list. Check your internet connection.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnlineLookupException("Downloading the title list took too long.", ex);
        }
        catch (IOException ex)
        {
            throw new OnlineLookupException("The title list could not be saved: " + ex.Message, ex);
        }
    }

    internal static Dictionary<string, List<DbTitle>> Parse(IEnumerable<string> lines)
    {
        var index = new Dictionary<string, List<DbTitle>>(StringComparer.OrdinalIgnoreCase);
        bool first = true;
        foreach (string line in lines)
        {
            if (first) { first = false; continue; }   // header
            string[] columns = line.Split('\t');
            if (columns.Length < 6 || columns[0].Length < 9) continue;
            string key = Normalize(columns[0]);
            if (!key.StartsWith("PPSA", StringComparison.Ordinal)) continue;
            var title = new DbTitle(key, columns[1].Trim(), CleanName(columns[2]), columns[3].Trim(), columns[4].Trim(), columns[5].Trim());
            if (!index.TryGetValue(key, out List<DbTitle>? list)) index[key] = list = [];
            if (!list.Any(existing => existing.ContentId.Equals(title.ContentId, StringComparison.OrdinalIgnoreCase)))
                list.Add(title);
        }
        return index;
    }

    /// <summary>Store names carry trademark symbols that do not belong in a title.</summary>
    internal static string CleanName(string name) =>
        name.Replace("™", string.Empty).Replace("®", string.Empty).Replace("©", string.Empty).Trim();
}
