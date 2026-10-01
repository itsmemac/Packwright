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
/// Errors are turned into plain-language messages; the technical details go to the log.
/// </summary>
public static class ConsoleSender
{
    /// <summary>How many times in a row a connection may drop without any new data getting through before giving up.</summary>
    private const int MaximumFailuresWithoutProgress = 6;
    private static readonly int[] BackoffSeconds = [2, 4, 8, 15, 30, 30];

    /// <summary>The remote folder a source will end up in: the chosen folder plus the file or dump folder name.</summary>
    public static string RemotePathFor(string remoteFolder, string source) =>
        FtpClient.Normalize(FtpClient.Normalize(remoteFolder) + "/" +
                            Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));

    /// <summary>A queued task does not keep the password; it is looked up from the saved console when the task runs.</summary>
    public static ConsoleProfile WithSavedLogin(ConsoleProfile profile)
    {
        if (profile.FtpPassword.Length > 0 || profile.FtpUser.Length == 0) return profile;
        ConsoleProfile? saved = AppServices.Instance.Settings.Consoles
            .FirstOrDefault(console => console.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase));
        return saved is null || !saved.FtpUser.Equals(profile.FtpUser, StringComparison.Ordinal)
            ? profile
            : new ConsoleProfile
            {
                Name = profile.Name, Host = profile.Host, FtpPort = profile.FtpPort, DefaultFolder = profile.DefaultFolder,
                FtpUser = profile.FtpUser, FtpPassword = saved.FtpPassword, InstallUrl = profile.InstallUrl,
                InstallBody = profile.InstallBody, InstallContentType = profile.InstallContentType
            };
    }

    // ------------------------------------------------------------------ test

    /// <summary>Connects and reports what the console's FTP server says, so a profile can be checked before sending.</summary>
    public static async Task<string> TestFtpAsync(ConsoleProfile profile, CancellationToken token)
    {
        try
        {
            IPAddress address = await ConsoleNetwork.ResolveLocalAsync(profile.Host, token).ConfigureAwait(false);
            await using FtpClient client = await ConnectAsync(address, profile, token).ConfigureAwait(false);
            string system = await client.SystemAsync(token).ConfigureAwait(false);
            (int code, string text) = await client.CommandAsync("PWD", token).ConfigureAwait(false);
            int open = text.IndexOf('"'), close = text.IndexOf('"', open + 1);
            string folder = code == 257 && open >= 0 && close > open ? text[(open + 1)..close] : "unknown";
            return $"Connected to {address}:{profile.FtpPort}" +
                   (profile.FtpUser.Length > 0 ? $" as {profile.FtpUser}" : " (no login needed)") +
                   $". {(system.Length > 0 ? system + ". " : string.Empty)}Start folder: {folder}.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Exception($"Test connection to {profile.Name} failed", ex);
            throw Friendly(profile, ex, string.Empty);
        }
    }

    private static Task<FtpClient> ConnectAsync(IPAddress address, ConsoleProfile profile, CancellationToken token)
    {
        // Many console FTP payloads let anyone in; phones and PCs running an FTP app usually want the login shown in that app.
        bool login = profile.FtpUser.Length > 0;
        return FtpClient.ConnectAsync(address.ToString(), profile.FtpPort, login ? profile.FtpUser : "anonymous",
            login ? profile.FtpPassword : "packwright@localhost", token);
    }

    // ------------------------------------------------------------------ FTP upload

    public static async Task UploadAsync(ConsoleProfile profile, string source, string remoteFolder,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        profile = WithSavedLogin(profile);
        try
        {
            await UploadCoreAsync(profile, source, remoteFolder, progress, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.Exception($"Send to {profile.Name} ({profile.Host}:{profile.FtpPort}) failed", ex);
            throw Friendly(profile, ex, remoteFolder);
        }
    }

    private static async Task UploadCoreAsync(ConsoleProfile profile, string source, string remoteFolder,
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
        string stage = "Sending to " + profile.Name;
        var watch = Stopwatch.StartNew();
        long lastReport = 0;
        void Report(string text, long bytes, int item, string file) =>
            progress.Report(new PackageTaskProgress(text, 0, 0, bytes, total, item, files.Count, file));

        FtpClient? client = null;
        try
        {
            Report(stage, 0, 0, string.Empty);
            for (int index = 0; index < files.Count; index++)
            {
                (string local, string remote, long size) = files[index];
                int failures = 0;          // drops in a row without any new data getting through
                long furthest = 0;         // the most bytes of this file the console has had so far
                long furthestAtLastDrop = -1;
                for (int attempt = 1; ; attempt++)
                {
                    token.ThrowIfCancellationRequested();
                    long before = done;
                    try
                    {
                        client ??= await ConnectAsync(address, profile, token).ConfigureAwait(false);
                        await client.EnsureDirectoryAsync(remote[..remote.LastIndexOf('/')], token).ConfigureAwait(false);
                        await client.UploadAsync(local, remote, sent =>
                        {
                            if (sent > furthest) furthest = sent;
                            long now = watch.ElapsedMilliseconds;
                            if (now - lastReport < 250 && sent < size) return;
                            lastReport = now;
                            Report(stage, before + sent, index + 1, remote);
                        }, token).ConfigureAwait(false);
                        done += size;
                        break;
                    }
                    catch (Exception ex) when (IsRetryable(ex))
                    {
                        // The link dropped or the console was busy: reconnect and carry on (a partly sent file is continued
                        // when the console supports it). Data getting through again resets the count, so a long transfer
                        // over a flaky link can survive many drops, while a console that is simply gone gives up soon.
                        if (client is not null) await client.DisposeAsync().ConfigureAwait(false);
                        client = null;
                        if (furthest > furthestAtLastDrop && furthestAtLastDrop >= 0) failures = 0;   // data got through since the last drop
                        furthestAtLastDrop = furthest;
                        failures++;
                        Logger.Warn($"Send to {profile.Name}: the connection dropped ({ex.Message}); try {failures} of {MaximumFailuresWithoutProgress}.");
                        if (failures >= MaximumFailuresWithoutProgress) throw;
                        int wait = BackoffSeconds[Math.Min(failures - 1, BackoffSeconds.Length - 1)];
                        for (int left = wait; left > 0; left--)
                        {
                            Report($"Connection lost. Trying again in {left} s ({failures} of {MaximumFailuresWithoutProgress})", before + Math.Min(furthest, size), index + 1, remote);
                            await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                        }
                        Report($"Reconnecting to {profile.Name}...", before + Math.Min(furthest, size), index + 1, remote);
                    }
                }
            }
            Report("Sent to " + profile.Name, total, files.Count, string.Empty);
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
        UserMessageException => false,
        IOException or SocketException => true,
        _ => false
    };

    // ------------------------------------------------------------------ plain-language errors

    /// <summary>Turns what the system reported into words for the person using the app. The details stay in the log.</summary>
    internal static UserMessageException Friendly(ConsoleProfile profile, Exception ex, string remoteFolder)
    {
        if (ex is UserMessageException already) return already;
        string who = profile.Name.Length > 0 ? profile.Name : profile.Host;
        string folder = remoteFolder.Length > 0 ? remoteFolder : profile.DefaultFolder;
        Exception root = ex;
        while (root.InnerException is not null) root = root.InnerException;
        SocketError? socket = (root as SocketException)?.SocketErrorCode ?? (ex as SocketException)?.SocketErrorCode;
        string text = ex.Message;
        string message;

        if (ex is FtpException ftp)
            message = ftp.Code switch
            {
                530 or 331 or 332 or 532 => $"{who} did not accept the login. Open Settings, Manage consoles and enter the user name and password " +
                                            "shown in its FTP app (leave them empty only if it lets anyone in).",
                452 or 552 => $"{who} is out of space. Free some space on it and try again.",
                421 => $"{who} ended the session (it may be busy or shutting down). Wait a moment and try again.",
                550 or 553 or 450 or 451 => $"{who} would not let Packwright save to {folder}. Check that the folder exists and that its FTP app " +
                                                  "is allowed to write there (on a phone, give the app storage access).",
                _ => $"{who} did not accept the transfer."
            };
        else if (text.Contains("not on your local network", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("could not be found", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("Enter the console", StringComparison.OrdinalIgnoreCase))
            message = text;   // already written for people
        else if (socket is SocketError.ConnectionRefused || text.StartsWith("Could not connect", StringComparison.OrdinalIgnoreCase) ||
                 text.StartsWith("Could not reach", StringComparison.OrdinalIgnoreCase))
            message = $"Could not connect to {who} ({profile.Host}, port {profile.FtpPort}). Check that it is switched on and on the same " +
                      "Wi-Fi or network as this PC, and that its FTP server is running with that port number.";
        else if (socket is SocketError.ConnectionReset or SocketError.ConnectionAborted or SocketError.Shutdown ||
                 text.Contains("forcibly closed", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("closed the connection", StringComparison.OrdinalIgnoreCase))
            message = $"{who} closed the connection while the file was being sent, even after several tries. This usually means the Wi-Fi " +
                      "dropped, it went to sleep, its FTP app stopped or was moved to the background, or it ran out of space or " +
                      "cannot store a file this large. Keep it awake with the FTP app open, make sure it has free space, then press Retry.";
        else if (text.Contains("did not answer in time", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("stalled", StringComparison.OrdinalIgnoreCase) ||
                 text.Contains("data connection in time", StringComparison.OrdinalIgnoreCase) ||
                 socket is SocketError.TimedOut)
            message = $"{who} stopped answering. Check that it is still on, awake and on the same network, then press Retry.";
        else if (text.Contains("bytes but the file is", StringComparison.OrdinalIgnoreCase))
            message = $"{who} did not receive the whole file (the size on it does not match). Press Retry; if it happens again, check its free space.";
        else if (ex is FileNotFoundException or DirectoryNotFoundException)
            message = "Packwright could not find the file to send. It may have been moved or deleted.";
        else if (ex is UnauthorizedAccessException)
            message = "Packwright is not allowed to read the file to send.";
        else
            message = $"Sending to {who} did not work. The technical details are in the log (open the Log page).";
        return new UserMessageException(message, ex);
    }

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
        try
        {
            await InstallByUrlCoreAsync(profile, package, progress, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not UserMessageException)
        {
            Logger.Exception($"Install on {profile.Name} ({profile.Host}) failed", ex);
            string who = profile.Name.Length > 0 ? profile.Name : profile.Host;
            if (ex is HttpRequestException or TaskCanceledException)
                throw new UserMessageException($"Packwright could not reach the installer on {who}. Check that the installer app is running on it " +
                                               "and that the request address in Settings, Manage consoles is right.", ex);
            if (ex is InvalidDataException or InvalidOperationException or ArgumentException)
                throw new UserMessageException(ex.Message, ex);
            throw Friendly(profile, ex, string.Empty);
        }
    }

    private static async Task InstallByUrlCoreAsync(ConsoleProfile profile, string package,
        IProgress<PackageTaskProgress> progress, CancellationToken token)
    {
        if (!File.Exists(package) || !package.EndsWith(".pkg", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Install by URL only works with a .pkg file.");
        if (string.IsNullOrWhiteSpace(profile.InstallUrl))
            throw new InvalidOperationException("This console has no install address yet. Add one in Settings, Manage consoles.");
        IPAddress console = await ConsoleNetwork.ResolveLocalAsync(profile.Host, token).ConfigureAwait(false);
        IPAddress local = ConsoleNetwork.LocalAddressFor(console);
        await using SingleFileServer server = SingleFileServer.Start(package, local);
        Logger.Info($"Serving {Path.GetFileName(package)} at {local}:{server.Port} for {profile.Name}.");

        (string requestUrl, string body) = BuildInstallRequest(profile, server.Url);
        if (!Uri.TryCreate(requestUrl, UriKind.Absolute, out Uri? target) || target.Scheme != Uri.UriSchemeHttp)
            throw new InvalidOperationException("The install address in Settings, Manage consoles is not a valid http:// address.");
        await ConsoleNetwork.ResolveLocalAsync(target.Host, token).ConfigureAwait(false);

        progress.Report(new PackageTaskProgress("Asking " + profile.Name + " to install", 0, 0, 0, server.Length));
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(profile.InstallContentType);
        using HttpResponseMessage response = await Http.PostAsync(target, content, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string text = (await response.Content.ReadAsStringAsync(token).ConfigureAwait(false)).Trim();
            Logger.Warn($"The installer on {profile.Name} answered {(int)response.StatusCode} {response.ReasonPhrase}: {(text.Length > 300 ? text[..300] : text)}");
            throw new UserMessageException($"The installer on {profile.Name} did not accept the request. Check the request address and body in " +
                                           "Settings, Manage consoles against the installer's instructions. Details are in the log.");
        }

        // Keep the file available while the console downloads it: wait until it has taken the whole file and has been quiet.
        DateTime asked = DateTime.UtcNow;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            await Task.Delay(500, token).ConfigureAwait(false);
            bool started = server.Requests > 0;
            progress.Report(new PackageTaskProgress(started ? profile.Name + " is downloading the package" : "Waiting for " + profile.Name + " to start the download",
                0, 0, Math.Min(server.BytesServed, server.Length), server.Length));
            if (!started && DateTime.UtcNow - asked > TimeSpan.FromMinutes(5))
                throw new UserMessageException($"{profile.Name} never asked for the file. Check the request address in Settings, Manage consoles, " +
                                               "and that your firewall lets Packwright share files on your network.");
            if (server.FullyServed && DateTime.UtcNow - server.LastRequestUtc > TimeSpan.FromSeconds(20)) break;
            if (started && DateTime.UtcNow - server.LastRequestUtc > TimeSpan.FromMinutes(5))
                throw new UserMessageException($"{profile.Name} stopped downloading before it had the whole file. Try again.");
        }
        progress.Report(new PackageTaskProgress("The console has the package", 0, 0, server.Length, server.Length));
        Logger.Info($"{profile.Name} downloaded {Path.GetFileName(package)}.");
    }
}
