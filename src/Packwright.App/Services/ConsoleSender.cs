using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Packwright.Core.Services;
using Packwright.Core.Tasks;
using Packwright.Infrastructure;

namespace Packwright.App.Services;

/// <summary>
/// Sends files to a console on the local network: FTP upload, or "install by URL" where the console's package installer
/// downloads a .pkg from this PC. Only addresses on the local network are used, and only when the user sends something.
/// </summary>
public static class ConsoleSender
{
    private const int Attempts = 3;

    /// <summary>The remote folder a source will end up in: the chosen folder plus the file or dump folder name.</summary>
    public static string RemotePathFor(string remoteFolder, string source) =>
        FtpClient.Normalize(FtpClient.Normalize(remoteFolder) + "/" +
                            Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

    /// <summary>Connects and reports what the console's FTP server says, so a profile can be checked before sending.</summary>
    public static async Task<string> TestFtpAsync(ConsoleProfile profile, CancellationToken token)
    {
        IPAddress address = await ConsoleNetwork.ResolveLocalAsync(profile.Host, token).ConfigureAwait(false);
        await using FtpClient client = await ConnectAsync(address, profile, token).ConfigureAwait(false);
        string system = await client.SystemAsync(token).ConfigureAwait(false);
        (int code, string text) = await client.CommandAsync("PWD", token).ConfigureAwait(false);
        int open = text.IndexOf('"'), close = text.IndexOf('"', open + 1);
        string folder = code == 257 && open >= 0 && close > open ? text[(open + 1)..close] : "unknown";
        return $"Connected to {address}:{profile.FtpPort}. {(system.Length > 0 ? system + ". " : string.Empty)}Start folder: {folder}.";
    }

    private static Task<FtpClient> ConnectAsync(IPAddress address, ConsoleProfile profile, CancellationToken token) =>
        // Console FTP payloads accept anyone, so no account is used (and no password is stored).
        FtpClient.ConnectAsync(address.ToString(), profile.FtpPort, "anonymous", "packwright@localhost", token);

    // ------------------------------------------------------------------ FTP upload

    public static async Task UploadAsync(ConsoleProfile profile, string source, string remoteFolder,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        IPAddress address = await ConsoleNetwork.ResolveLocalAsync(profile.Host, token).ConfigureAwait(false);
        string remoteRoot = RemotePathFor(remoteFolder, source);
        List<(string Local, string Remote, long Size)> files = [];
        if (Directory.Exists(source))
        {
            string root = Path.GetFullPath(source);
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
                files.Add((file, remoteRoot + "/" + Path.GetRelativePath(root, file).Replace('\\', '/'), new FileInfo(file).Length));
        }
        else if (File.Exists(source))
            files.Add((source, remoteRoot, new FileInfo(source).Length));
        else
            throw new FileNotFoundException("The file to send was not found.", source);
        if (files.Count == 0) throw new InvalidDataException("There is nothing in that folder to send.");

        long total = files.Sum(file => file.Size), done = 0;
        var watch = Stopwatch.StartNew();
        long lastReport = 0;
        FtpClient? client = null;
        try
        {
            for (int index = 0; index < files.Count; index++)
            {
                (string local, string remote, long size) = files[index];
                for (int attempt = 1; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        client ??= await ConnectAsync(address, profile, token).ConfigureAwait(false);
                        await client.EnsureDirectoryAsync(remote[..remote.LastIndexOf('/')], token).ConfigureAwait(false);
                        long before = done;
                        await client.UploadAsync(local, remote, sent =>
                        {
                            long now = watch.ElapsedMilliseconds;
                            if (now - lastReport < 250 && sent < size) return;
                            lastReport = now;
                            progress.Report(new PackageTaskProgress("Sending to " + profile.Name, 1, 1, before + sent, total,
                                index + 1, files.Count, remote, StagePercent: total > 0 ? (before + sent) * 100.0 / total : -1));
                        }, token).ConfigureAwait(false);
                        done += size;
                        break;
                    }
                    catch (Exception ex) when (attempt < Attempts && IsRetryable(ex))
                    {
                        // The link dropped or the console was busy: reconnect and carry on (a partly sent file is continued).
                        Logger.Warn($"Send to {profile.Name}: attempt {attempt} failed ({ex.Message}); retrying.");
                        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                        client = null;
                        await Task.Delay(TimeSpan.FromSeconds(2), token).ConfigureAwait(false);
                    }
                }
            }
            progress.Report(new PackageTaskProgress("Sent to " + profile.Name, 1, 1, total, total, files.Count, files.Count, string.Empty, 100));
        }
        finally
        {
            if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
        }
        Logger.Info($"Sent {files.Count:N0} file(s), {total:N0} bytes, to {profile.Name} ({address}) at {remoteRoot}.");
    }

    /// <summary>Dropped links and busy servers are worth another try; refusals (5xx) are not.</summary>
    private static bool IsRetryable(Exception ex) => ex switch
    {
        FtpException ftp => ftp.IsTemporary,
        IOException or SocketException => true,
        _ => false
    };

    // ------------------------------------------------------------------ install by URL

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Fills the placeholders of the install request. {url} is escaped for JSON bodies.</summary>
    public static (string Url, string Body) BuildInstallRequest(ConsoleProfile profile, string fileUrl)
    {
        string url = profile.InstallUrl.Replace("{host}", profile.Host, StringComparison.Ordinal)
            .Replace("{url}", Uri.EscapeDataString(fileUrl), StringComparison.Ordinal);
        bool json = profile.InstallContentType.Contains("json", StringComparison.OrdinalIgnoreCase);
        string value = json ? JsonEncodedText.Encode(fileUrl).ToString() : fileUrl;
        return (url, profile.InstallBody.Replace("{url}", value, StringComparison.Ordinal));
    }

    public static async Task InstallByUrlAsync(ConsoleProfile profile, string package,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        if (!File.Exists(package) || !package.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Install by URL needs a .pkg file.");
        if (string.IsNullOrWhiteSpace(profile.InstallUrl))
            throw new InvalidOperationException("This console has no install address. Set it in the console's settings.");
        IPAddress console = await ConsoleNetwork.ResolveLocalAsync(profile.Host, token).ConfigureAwait(false);
        IPAddress local = ConsoleNetwork.LocalAddressFor(console);
        await using SingleFileServer server = SingleFileServer.Start(package, local);
        Logger.Info($"Serving {Path.GetFileName(package)} at {local}:{server.Port} for {profile.Name}.");

        (string requestUrl, string body) = BuildInstallRequest(profile, server.Url);
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? target) || target.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("The install address is not a valid http:// address: " + requestUrl);
        await ConsoleNetwork.ResolveLocalAsync(target.Host, token).ConfigureAwait(false);

        progress.Report(new PackageTaskProgress("Asking " + profile.Name + " to install", 1, 3, 0, server.Length, 0, 0, string.Empty, 0));
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(profile.InstallContentType);
        using HttpResponseMessage response = await Http.PostAsync(target, content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string text = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
            throw new IOException($"The console's installer answered {(int)response.StatusCode} {response.ReasonPhrase}. {(text.Length > 200 ? text[..200] : text)}");
        }

        // Keep the file available while the console downloads it: wait until it has taken the whole file and has been quiet.
        DateTime asked = DateTime.UtcNow;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(500, token).ConfigureAwait(false);
            double percent = server.Length > 0 ? Math.Min(100.0, server.BytesServed * 100.0 / server.Length) : 0;
            bool started = server.Requests > 0;
            progress.Report(new PackageTaskProgress(started ? profile.Name + " is downloading the package" : "Waiting for " + profile.Name + " to start the download",
                2, 3, Math.Min(server.BytesServed, server.Length), server.Length, 0, 0, string.Empty, percent));
            if (!started && DateTime.UtcNow - asked > TimeSpan.FromMinutes(5))
                throw new IOException("The console never asked for the file. Check the install address in the console's settings, and that the firewall allows Packwright on your network.");
            if (server.FullyServed && DateTime.UtcNow - server.LastRequestUtc > TimeSpan.FromSeconds(20)) break;
            if (started && DateTime.UtcNow - server.LastRequestUtc > TimeSpan.FromMinutes(5))
                throw new IOException("The console stopped downloading before it had the whole file.");
        }
        progress.Report(new PackageTaskProgress("The console has the package", 3, 3, server.Length, server.Length, 0, 0, string.Empty, 100));
        Logger.Info($"{profile.Name} downloaded {Path.GetFileName(package)}.");
    }
}
