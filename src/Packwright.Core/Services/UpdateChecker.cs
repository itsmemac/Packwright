using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace Packwright.Core.Services;

public sealed record ReleaseAsset(string Name, string Url, long Size);

public sealed record ReleaseInfo(
    string Version, string Tag, string Name, string Notes, string PageUrl, DateTime? PublishedUtc,
    IReadOnlyList<ReleaseAsset> Assets);

public sealed record DownloadResult(string Path, string Sha256, bool? Verified, string VerifyMessage);

/// <summary>
/// Looks for a newer Packwright on GitHub (the releases of the project's own repository) and can download the right
/// file for this system and check it against the release's SHA256SUMS.txt. Nothing is installed automatically.
/// Network is used only when a check or download is asked for.
/// </summary>
public static class UpdateChecker
{
    public const string Repository = "itsmemac/Packwright";
    private const long MaximumDownloadBytes = 700L * 1024 * 1024;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Packwright-update-check (+https://github.com/itsmemac/Packwright)");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    /// <summary>The releases endpoint. PACKWRIGHT_UPDATE_URL points it elsewhere, for testing against a local server.</summary>
    public static string LatestUrl =>
        Environment.GetEnvironmentVariable("PACKWRIGHT_UPDATE_URL") is { Length: > 0 } custom
            ? custom
            : $"https://api.github.com/repos/{Repository}/releases/latest";

    /// <summary>The newest published release, or null when the project has not published one yet.</summary>
    public static async Task<ReleaseInfo?> GetLatestAsync(CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(LatestUrl, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && response.Headers.Contains("X-RateLimit-Remaining"))
                throw new OnlineLookupException("GitHub is limiting requests from this connection right now. Try again in a while.");
            if (!response.IsSuccessStatusCode)
                throw new OnlineLookupException($"GitHub answered with an error ({(int)response.StatusCode}).");
            json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new OnlineLookupException("Could not reach GitHub. Check your internet connection.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new OnlineLookupException("GitHub did not answer in time.", ex);
        }

        try
        {
            return Parse(json);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new OnlineLookupException("GitHub's answer was not in the expected format.", ex);
        }
    }

    internal static ReleaseInfo? Parse(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        string tag = Text(root, "tag_name");
        if (tag.Length == 0) return null;
        var assets = new List<ReleaseAsset>();
        if (root.TryGetProperty("assets", out JsonElement list) && list.ValueKind == JsonValueKind.Array)
            foreach (JsonElement asset in list.EnumerateArray())
            {
                string name = Text(asset, "name"), url = Text(asset, "browser_download_url");
                long size = asset.TryGetProperty("size", out JsonElement sizeElement) && sizeElement.TryGetInt64(out long parsed) ? parsed : 0;
                if (name.Length > 0 && url.Length > 0) assets.Add(new ReleaseAsset(name, url, size));
            }
        DateTime? published = DateTime.TryParse(Text(root, "published_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out DateTime when) ? when : null;
        return new ReleaseInfo(tag.TrimStart('v', 'V'), tag, Text(root, "name"), Text(root, "body"),
            Text(root, "html_url"), published, assets);
    }

    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>True when <paramref name="latest"/> is a higher version than <paramref name="current"/> (1.2.0 vs 1.10.0 compares numerically).</summary>
    public static bool IsNewer(string latest, string current) => Compare(latest, current) > 0;

    internal static int Compare(string left, string right)
    {
        (int[] a, string pa) = Split(left);
        (int[] b, string pb) = Split(right);
        for (int i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            int x = i < a.Length ? a[i] : 0, y = i < b.Length ? b[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        // 1.2.0 is newer than 1.2.0-beta.
        if (pa.Length == 0 && pb.Length > 0) return 1;
        if (pa.Length > 0 && pb.Length == 0) return -1;
        return string.CompareOrdinal(pa, pb);
    }

    private static (int[] Parts, string Pre) Split(string version)
    {
        string text = version.Trim().TrimStart('v', 'V');
        int dash = text.IndexOf('-');
        string pre = dash >= 0 ? text[(dash + 1)..] : string.Empty;
        if (dash >= 0) text = text[..dash];
        int plus = text.IndexOf('+');
        if (plus >= 0) text = text[..plus];
        int[] parts = text.Split('.', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out int number) ? number : 0)
            .ToArray();
        return (parts, pre);
    }

    /// <summary>The platform part of the release file names: windows-x64, linux-x64, macos-arm64 or macos-x64.</summary>
    public static string PlatformTag()
    {
        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
        string arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            _ => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
        };
        return $"{os}-{arch}";
    }

    /// <summary>True for the Windows installer (Packwright-Setup-vX-windows-x64.exe).</summary>
    public static bool IsInstaller(ReleaseAsset asset) =>
        asset.Name.Contains("Setup", StringComparison.OrdinalIgnoreCase) &&
        asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The kind of Linux package this distribution installs: ".deb", ".rpm", or empty when it is neither.</summary>
    public static string LinuxPackageExtension(string? osRelease = null)
    {
        try
        {
            osRelease ??= File.Exists("/etc/os-release") ? File.ReadAllText("/etc/os-release") : string.Empty;
        }
        catch (IOException) { osRelease = string.Empty; }
        catch (UnauthorizedAccessException) { osRelease = string.Empty; }
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in osRelease.Split((char)10))
        {
            if (!line.StartsWith("ID=", StringComparison.Ordinal) && !line.StartsWith("ID_LIKE=", StringComparison.Ordinal)) continue;
            foreach (string word in line[(line.IndexOf('=') + 1)..].Trim().Trim('"').Split(' ', StringSplitOptions.RemoveEmptyEntries))
                words.Add(word);
        }
        if (words.Overlaps(["debian", "ubuntu"])) return ".deb";
        if (words.Overlaps(["fedora", "rhel", "centos", "suse", "opensuse", "opensuse-leap", "opensuse-tumbleweed"])) return ".rpm";
        return string.Empty;
    }

    /// <summary>
    /// The installer for this system: the Windows setup program, the macOS disk image, or the .deb / .rpm that matches the
    /// Linux distribution. Null when the release has none for this system (the window then points to the release page).
    /// </summary>
    public static ReleaseAsset? PickAsset(ReleaseInfo release, string? platform = null, string? linuxPackage = null)
    {
        platform ??= PlatformTag();
        string extension = platform.StartsWith("windows", StringComparison.OrdinalIgnoreCase) ? ".exe"
            : platform.StartsWith("macos", StringComparison.OrdinalIgnoreCase) ? ".dmg"
            : linuxPackage ?? LinuxPackageExtension();
        if (extension.Length == 0) return null;
        return release.Assets.FirstOrDefault(asset =>
            asset.Name.Contains("-" + platform + ".", StringComparison.OrdinalIgnoreCase) &&
            asset.Name.EndsWith(extension, StringComparison.OrdinalIgnoreCase) &&
            (extension != ".exe" || IsInstaller(asset)));
    }

    private static bool AllowedHost(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.IsLoopback && Environment.GetEnvironmentVariable("PACKWRIGHT_UPDATE_URL") is { Length: > 0 }))
            return false;
        string host = uri.Host;
        if (uri.IsLoopback) return Environment.GetEnvironmentVariable("PACKWRIGHT_UPDATE_URL") is { Length: > 0 };
        return host.Equals("github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".github.com", StringComparison.OrdinalIgnoreCase) ||
               host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Downloads <paramref name="asset"/> into <paramref name="folder"/> and compares its SHA-256 with the release's
    /// SHA256SUMS.txt. The file is kept either way; <see cref="DownloadResult.Verified"/> says whether it matched
    /// (null when the release has no checksum file).
    /// </summary>
    public static async Task<DownloadResult> DownloadAsync(ReleaseInfo release, ReleaseAsset asset, string folder,
        IProgress<(long Done, long Total)>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(asset.Url, UriKind.Absolute, out Uri? uri) || !AllowedHost(uri))
            throw new OnlineLookupException("The download address is not a GitHub address, so it was not used.");
        Directory.CreateDirectory(folder);
        string target = UniquePath(Path.Combine(folder, Path.GetFileName(asset.Name)));
        string partial = target + ".part";
        string hash;
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new OnlineLookupException($"The download failed ({(int)response.StatusCode}).");
            if (response.RequestMessage?.RequestUri is { } final && !AllowedHost(final))
                throw new OnlineLookupException("The download was redirected somewhere that is not GitHub, so it was stopped.");
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            if (total > MaximumDownloadBytes) throw new OnlineLookupException("The file is larger than expected, so it was not downloaded.");

            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (Stream input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous))
            {
                byte[] buffer = new byte[1 << 20];
                long done = 0;
                while (true)
                {
                    int read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0) break;
                    done += read;
                    if (done > MaximumDownloadBytes) throw new OnlineLookupException("The file is larger than expected, so the download was stopped.");
                    sha.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    progress?.Report((done, total));
                }
            }
            hash = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            File.Move(partial, target);
        }
        catch (HttpRequestException ex)
        {
            TryDelete(partial);
            throw new OnlineLookupException("The download could not be completed. Check your internet connection.", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            TryDelete(partial);
            throw new OnlineLookupException("The download took too long.", ex);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }

        (bool? verified, string message) = await VerifyAsync(release, asset, hash, cancellationToken).ConfigureAwait(false);
        return new DownloadResult(target, hash, verified, message);
    }

    private static async Task<(bool? Verified, string Message)> VerifyAsync(ReleaseInfo release, ReleaseAsset asset, string hash,
        CancellationToken cancellationToken)
    {
        ReleaseAsset? sums = release.Assets.FirstOrDefault(candidate => candidate.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (sums is null) return (null, "This release has no checksum file, so the download could not be checked.");
        try
        {
            if (!Uri.TryCreate(sums.Url, UriKind.Absolute, out Uri? uri) || !AllowedHost(uri))
                return (null, "The checksum file is not at a GitHub address, so it was not used.");
            string text = await Client.GetStringAsync(uri, cancellationToken).ConfigureAwait(false);
            foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                string[] parts = line.Trim().Split([' ', '\t', '*'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[^1].Equals(asset.Name, StringComparison.OrdinalIgnoreCase))
                    return parts[0].Equals(hash, StringComparison.OrdinalIgnoreCase)
                        ? (true, "Checked against SHA256SUMS.txt: the download is intact.")
                        : (false, "The download does NOT match SHA256SUMS.txt. Do not use it; download it again.");
            }
            return (null, "The checksum file does not list this download, so it could not be checked.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return (null, "The checksum file could not be fetched, so the download was not checked.");
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string stem = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, Path.GetFileNameWithoutExtension(path));
        string extension = Path.GetExtension(path);
        for (int i = 2; i < 1000; i++)
            if (!File.Exists($"{stem} ({i}){extension}")) return $"{stem} ({i}){extension}";
        return path;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
