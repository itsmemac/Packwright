using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Packwright.Core.Services;

/// <summary>An error reply from an FTP server. 4xx replies are temporary, 5xx are permanent.</summary>
public sealed class FtpException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
    public bool IsTemporary => Code is >= 400 and < 500;
}

/// <summary>
/// A small passive-mode FTP client for sending files to a console's FTP payload. It supports what such servers
/// usually offer (login, MKD, SIZE, REST, STOR) and copes when a server lacks SIZE or REST. It only ever connects to the
/// host it was given.
/// </summary>
public sealed class FtpClient : IAsyncDisposable
{
    private const int BufferSize = 256 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(60);

    private readonly TcpClient _control;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly string _host;
    private readonly HashSet<string> _knownDirectories = new(StringComparer.Ordinal);

    private FtpClient(TcpClient control, string host)
    {
        _control = control;
        _host = host;
        Stream stream = control.GetStream();
        _reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
        _writer = new StreamWriter(stream, new UTF8Encoding(false), 1024, leaveOpen: true) { NewLine = "\r\n", AutoFlush = true };
    }

    /// <summary>What the server said when we connected, for display.</summary>
    public string Greeting { get; private set; } = string.Empty;

    public static async Task<FtpClient> ConnectAsync(string host, int port, string user, string password, CancellationToken token)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(CommandTimeout);
            try { await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException($"Could not reach {host}:{port}. Is the console on, and is its FTP payload running?");
            }
            catch (SocketException ex)
            {
                throw new IOException($"Could not connect to {host}:{port}: {ex.Message}");
            }
            var client = new FtpClient(tcp, host);
            (int code, string text) = await client.ReadReplyAsync(token).ConfigureAwait(false);
            if (code != 220) throw new FtpException(code, "The server did not accept the connection: " + text);
            client.Greeting = text;
            (code, text) = await client.CommandAsync("USER " + Clean(user), token).ConfigureAwait(false);
            if (code == 331 || code == 332)
                (code, text) = await client.CommandAsync("PASS " + Clean(password), token).ConfigureAwait(false);
            if (code is not (230 or 202)) throw new FtpException(code, "Login was refused: " + text);
            // A binary transfer type is required for images and packages.
            (code, text) = await client.CommandAsync("TYPE I", token).ConfigureAwait(false);
            if (code != 200) throw new FtpException(code, "Binary mode was refused: " + text);
            return client;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    private static string Clean(string value)
    {
        if (value.AsSpan().IndexOfAny('\r', '\n') >= 0) throw new ArgumentException("Line breaks are not allowed in FTP commands.");
        return value;
    }

    private async Task<(int Code, string Text)> ReadReplyAsync(CancellationToken token)
    {
        try
        {
            return await ReadReplyCoreAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new IOException("The console did not answer in time.");
        }
    }

    private async Task<(int Code, string Text)> ReadReplyCoreAsync(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(CommandTimeout);
        string? line = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                       ?? throw new IOException("The console closed the connection.");
        if (line.Length < 3 || !int.TryParse(line.AsSpan(0, 3), out int code))
            throw new IOException("The server sent something that is not FTP: " + line);
        var text = new StringBuilder(line);
        if (line.Length > 3 && line[3] == '-')
        {
            string end = code.ToString() + " ";
            while (true)
            {
                string? next = await _reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)
                               ?? throw new IOException("The console closed the connection.");
                text.Append('\n').Append(next);
                if (next.StartsWith(end, StringComparison.Ordinal)) break;
            }
        }
        return (code, text.ToString());
    }

    public async Task<(int Code, string Text)> CommandAsync(string command, CancellationToken token)
    {
        await _writer.WriteLineAsync(command.AsMemory(), token).ConfigureAwait(false);
        return await ReadReplyAsync(token).ConfigureAwait(false);
    }

    /// <summary>The server's answer to SYST, or an empty string when it does not support it.</summary>
    public async Task<string> SystemAsync(CancellationToken token)
    {
        (int code, string text) = await CommandAsync("SYST", token).ConfigureAwait(false);
        return code == 215 ? text[4..] : string.Empty;
    }

    /// <summary>The size of a remote file, or null when it does not exist or the server cannot tell.</summary>
    public async Task<long?> SizeAsync(string remotePath, CancellationToken token)
    {
        (int code, string text) = await CommandAsync("SIZE " + Clean(remotePath), token).ConfigureAwait(false);
        if (code == 213 && long.TryParse(text.AsSpan(4).Trim(), out long size)) return size;
        return null;
    }

    /// <summary>Creates a folder and every missing parent. Folders that already exist are fine.</summary>
    public async Task EnsureDirectoryAsync(string remoteDirectory, CancellationToken token)
    {
        string path = Normalize(remoteDirectory);
        if (path == "/" || _knownDirectories.Contains(path)) return;
        string current = string.Empty;
        foreach (string part in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            current += "/" + part;
            if (!_knownDirectories.Add(current)) continue;
            (int code, string text) = await CommandAsync("MKD " + Clean(current), token).ConfigureAwait(false);
            // 257 created; a 5xx usually means it already exists. If it truly cannot be made, the upload reports it.
            if (code >= 400 && code < 500) throw new FtpException(code, $"Could not create {current}: {text}");
        }
    }

    public static string Normalize(string path)
    {
        string cleaned = path.Replace('\\', '/').Trim();
        while (cleaned.Contains("//", StringComparison.Ordinal)) cleaned = cleaned.Replace("//", "/", StringComparison.Ordinal);
        if (!cleaned.StartsWith('/')) cleaned = "/" + cleaned;
        return cleaned.Length > 1 ? cleaned.TrimEnd('/') : cleaned;
    }

    private async Task<IPEndPoint> PassiveAsync(CancellationToken token)
    {
        (int code, string text) = await CommandAsync("EPSV", token).ConfigureAwait(false);
        if (code == 229)
        {
            int open = text.IndexOf("(|||", StringComparison.Ordinal), close = text.IndexOf("|)", StringComparison.Ordinal);
            if (open >= 0 && close > open && int.TryParse(text.AsSpan(open + 4, close - open - 4), out int port))
                return new IPEndPoint(await ResolveAsync(token).ConfigureAwait(false), port);
        }
        (code, text) = await CommandAsync("PASV", token).ConfigureAwait(false);
        if (code != 227) throw new FtpException(code, "The server did not allow a data connection: " + text);
        int start = text.IndexOf('('), end = text.IndexOf(')');
        string[] parts = start >= 0 && end > start ? text[(start + 1)..end].Split(',') : [];
        if (parts.Length != 6 || !int.TryParse(parts[4], out int high) || !int.TryParse(parts[5], out int low))
            throw new IOException("The server's passive reply could not be read: " + text);
        // Always connect back to the address we used for the control connection: many consoles report a wrong one.
        return new IPEndPoint(await ResolveAsync(token).ConfigureAwait(false), high * 256 + low);
    }

    private async Task<IPAddress> ResolveAsync(CancellationToken token)
    {
        if (_control.Client.RemoteEndPoint is IPEndPoint remote) return remote.Address;
        return (await Dns.GetHostAddressesAsync(_host, token).ConfigureAwait(false))[0];
    }

    public enum UploadResult { Uploaded, Resumed, AlreadyThere }

    /// <summary>
    /// Sends one file. A partial copy already on the console is continued when the server supports it; a copy of the right
    /// size is left alone; the size is checked afterwards when the server can report it.
    /// </summary>
    public async Task<UploadResult> UploadAsync(string localFile, string remotePath, Action<long>? progress, CancellationToken token)
    {
        long length = new FileInfo(localFile).Length;
        long? existing = await SizeAsync(remotePath, token).ConfigureAwait(false);
        if (existing == length)
        {
            progress?.Invoke(length);
            return UploadResult.AlreadyThere;
        }
        long start = existing is > 0 && existing < length ? existing.Value : 0;
        bool resumed = false;
        try
        {
            await StoreAsync(localFile, remotePath, start, length, progress, token).ConfigureAwait(false);
            resumed = start > 0;
        }
        catch (FtpException) when (start > 0)
        {
            // The server would not continue a partial file: send the whole file again.
            await StoreAsync(localFile, remotePath, 0, length, progress, token).ConfigureAwait(false);
        }
        long? after = await SizeAsync(remotePath, token).ConfigureAwait(false);
        if (after is { } size && size != length)
            throw new IOException($"The copy on the console is {size:N0} bytes but the file is {length:N0} bytes.");
        return resumed ? UploadResult.Resumed : UploadResult.Uploaded;
    }

    private async Task StoreAsync(string localFile, string remotePath, long start, long length, Action<long>? progress, CancellationToken token)
    {
        IPEndPoint endpoint = await PassiveAsync(token).ConfigureAwait(false);
        if (start > 0)
        {
            (int restCode, string restText) = await CommandAsync("REST " + start, token).ConfigureAwait(false);
            if (restCode != 350) throw new FtpException(restCode, "The server cannot continue a partial file: " + restText);
        }
        using var data = new TcpClient { NoDelay = true, SendBufferSize = BufferSize };
        using (var connect = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            connect.CancelAfter(CommandTimeout);
            try { await data.ConnectAsync(endpoint.Address, endpoint.Port, connect.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("The console did not open a data connection in time.");
            }
        }
        (int code, string text) = await CommandAsync("STOR " + Clean(remotePath), token).ConfigureAwait(false);
        if (code is not (125 or 150))
        {
            if (code == 550 || code == 553) throw new FtpException(code, $"The console would not accept {remotePath}: {text}");
            throw new FtpException(code, "The upload was refused: " + text);
        }
        await using (NetworkStream output = data.GetStream())
        await using (var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan | FileOptions.Asynchronous))
        {
            input.Seek(start, SeekOrigin.Begin);
            byte[] buffer = new byte[BufferSize];
            long sent = start;
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(token);
            int read;
            try
            {
                while (true)
                {
                    stall.CancelAfter(StallTimeout);
                    read = await input.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await output.WriteAsync(buffer.AsMemory(0, read), stall.Token).ConfigureAwait(false);
                    sent += read;
                    progress?.Invoke(sent);
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                throw new IOException("The transfer stalled: the console stopped receiving data.");
            }
            await output.FlushAsync(token).ConfigureAwait(false);
        }
        // The data connection is closed, which tells the server the file is complete.
        (code, text) = await ReadReplyAsync(token).ConfigureAwait(false);
        if (code is not (226 or 250)) throw new FtpException(code, "The console reported a problem after the upload: " + text);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _writer.WriteLineAsync("QUIT".AsMemory(), quick.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
        _control.Dispose();
    }
}
