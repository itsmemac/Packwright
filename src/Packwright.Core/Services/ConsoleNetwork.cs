using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Packwright.Core.Services;

/// <summary>Rules for talking to a console: only machines on the local network, never the internet.</summary>
public static class ConsoleNetwork
{
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) ||
                   (b[0] == 169 && b[1] == 254);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte first = address.GetAddressBytes()[0];
            return address.IsIPv6LinkLocal || (first & 0xFE) == 0xFC;   // fe80::/10 and fc00::/7
        }
        return false;
    }

    /// <summary>Resolves the host and checks that every address is on the local network. Returns the address to use.</summary>
    public static async Task<IPAddress> ResolveLocalAsync(string host, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("Enter the console's address.");
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(host.Trim(), out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(host.Trim(), token).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            throw new IOException($"The address '{host}' could not be found.");
        }
        if (addresses.Length == 0) throw new IOException($"The address '{host}' could not be found.");
        IPAddress chosen = addresses.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1).First();
        if (addresses.Any(address => !IsLocal(address)))
            throw new IOException($"{host} is not on your local network. Packwright only sends to devices on your own network.");
        return chosen;
    }

    /// <summary>The address of this PC as seen from the console's network (the interface that routes to it).</summary>
    public static IPAddress LocalAddressFor(IPAddress remote)
    {
        using var probe = new Socket(remote.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        probe.Connect(remote, 9);   // no packet is sent; this only asks the OS which interface it would use
        return ((IPEndPoint)probe.LocalEndPoint!).Address;
    }
}

/// <summary>
/// Serves exactly one file over HTTP (GET, HEAD and ranges) at an unguessable address, bound to one local interface, so a
/// console's package installer can download it. It stops when disposed.
/// </summary>
public sealed class SingleFileServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly string _path;
    private readonly long _length;
    private readonly string _name;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;
    private long _bytesServed;
    private long _lastRequestTicks;

    private SingleFileServer(string file, IPAddress bind, int port)
    {
        _path = file;
        _length = new FileInfo(file).Length;
        _name = Path.GetFileName(file);
        Token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        _listener = new TcpListener(bind, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        Address = bind;
        _loop = Task.Run(AcceptLoopAsync);
    }

    public IPAddress Address { get; }
    public int Port { get; }
    public string Token { get; }
    public long Length => _length;
    public long BytesServed => Interlocked.Read(ref _bytesServed);
    public int Requests { get; private set; }
    /// <summary>True once a complete copy has been sent to a client in one request (or a range that reached the end).</summary>
    public bool FullyServed { get; private set; }
    public DateTime LastRequestUtc => new(Interlocked.Read(ref _lastRequestTicks), DateTimeKind.Utc);

    public string Url => $"http://{(Address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{Address}]" : Address.ToString())}:{Port}/{Token}/{Uri.EscapeDataString(_name)}";

    public static SingleFileServer Start(string file, IPAddress bind, int port = 0) => new(file, bind, port);

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                _ = Task.Run(() => HandleAsync(client));
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                await using NetworkStream stream = client.GetStream();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 4096, leaveOpen: true);
                string? requestLine = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false);
                if (requestLine is null) return;
                long? rangeStart = null, rangeEnd = null;
                string? line;
                while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token).ConfigureAwait(false)))
                    if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) ParseRange(line[6..].Trim(), out rangeStart, out rangeEnd);
                string[] parts = requestLine.Split(' ');
                Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
                Requests++;
                bool head = parts.Length > 1 && parts[0] == "HEAD";
                bool valid = parts.Length >= 2 && (parts[0] == "GET" || head) &&
                             Uri.UnescapeDataString(parts[1].Split('?')[0]) == $"/{Token}/{_name}";
                if (!valid)
                {
                    await WriteHeaderAsync(stream, "404 Not Found", 0, null, timeout.Token).ConfigureAwait(false);
                    return;
                }
                long start = 0, end = _length - 1;
                bool partial = rangeStart is not null;
                if (partial)
                {
                    start = rangeStart!.Value;
                    end = rangeEnd is { } e ? Math.Min(e, _length - 1) : _length - 1;
                    if (rangeStart < 0 || start > end || start >= _length)
                    {
                        await WriteHeaderAsync(stream, "416 Range Not Satisfiable", 0, $"Content-Range: bytes */{_length}", timeout.Token).ConfigureAwait(false);
                        return;
                    }
                }
                long count = end - start + 1;
                await WriteHeaderAsync(stream, partial ? "206 Partial Content" : "200 OK", count,
                    partial ? $"Content-Range: bytes {start}-{end}/{_length}" : null, timeout.Token).ConfigureAwait(false);
                if (head) return;
                await using var file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read, 256 * 1024, FileOptions.SequentialScan | FileOptions.Asynchronous);
                file.Seek(start, SeekOrigin.Begin);
                byte[] buffer = new byte[256 * 1024];
                long remaining = count;
                while (remaining > 0)
                {
                    timeout.CancelAfter(TimeSpan.FromSeconds(60));
                    int read = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), timeout.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    await stream.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    remaining -= read;
                    Interlocked.Add(ref _bytesServed, read);
                    Interlocked.Exchange(ref _lastRequestTicks, DateTime.UtcNow.Ticks);
                }
                if (remaining == 0 && end == _length - 1) FullyServed = true;
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
    }

    private static void ParseRange(string value, out long? start, out long? end)
    {
        start = end = null;
        if (!value.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return;
        string[] bounds = value[6..].Split(',')[0].Split('-');
        if (bounds.Length != 2) return;
        if (long.TryParse(bounds[0], out long first)) start = first;
        if (long.TryParse(bounds[1], out long last)) end = last;
    }

    private async Task WriteHeaderAsync(NetworkStream stream, string status, long length, string? extra, CancellationToken token)
    {
        string header = $"HTTP/1.1 {status}\r\nContent-Type: application/octet-stream\r\nAccept-Ranges: bytes\r\nContent-Length: {length}\r\n" +
                        (extra is null ? string.Empty : extra + "\r\n") + "Connection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), token).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop.ConfigureAwait(false); } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException) { }
        _stop.Dispose();
    }
}
