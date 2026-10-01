using System.IO.Pipes;
using System.Text;

namespace Packwright.Infrastructure;

/// <summary>
/// Keeps one Packwright running per user. A second launch hands its arguments to the first copy, which brings
/// its window to the front, and then exits, so two copies never fight over the same settings and library cache.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();

    private SingleInstance(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    // A test copy started with its own PACKWRIGHT_DATA folder is a separate "installation" and may run alongside.
    private static string Suffix =>
        new string(Environment.UserName.Where(char.IsLetterOrDigit).ToArray()) +
        (Environment.GetEnvironmentVariable("PACKWRIGHT_DATA") is { Length: > 0 } custom
            ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(Path.GetFullPath(custom))))[..12]
            : string.Empty);

    /// <summary>Returns the lock when this is the first copy, or null after passing <paramref name="args"/> on.</summary>
    public static SingleInstance? TryAcquire(string[] args)
    {
        string suffix = Suffix;
        string pipe = "Packwright.Activate." + suffix;
        Mutex mutex;
        try
        {
            mutex = new Mutex(true, "Packwright.SingleInstance." + suffix, out bool created);
            if (created) return new SingleInstance(mutex, pipe);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // If the lock cannot be used at all, start normally rather than refuse to run.
            return new SingleInstance(new Mutex(), pipe);
        }

        mutex.Dispose();
        try
        {
            using var client = new NamedPipeClientStream(".", pipe, PipeDirection.Out);
            client.Connect(4000);
            byte[] message = Encoding.UTF8.GetBytes(string.Join('\n', args.Where(arg => arg.Length > 0)));
            client.Write(message, 0, message.Length);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
        {
            Logger.Warn("Packwright is already running but could not be reached: " + ex.Message);
        }
        return null;
    }

    /// <summary>Calls <paramref name="onActivate"/> (with any paths given) each time another launch is attempted.</summary>
    public void Listen(Action<string[]> onActivate)
    {
        _ = Task.Run(async () =>
        {
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                    await server.WaitForConnectionAsync(_stop.Token).ConfigureAwait(false);
                    using var reader = new StreamReader(server, Encoding.UTF8);
                    string text = await reader.ReadToEndAsync(_stop.Token).ConfigureAwait(false);
                    onActivate(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    await Task.Delay(500).ConfigureAwait(false);
                }
            }
        });
    }

    public void Dispose()
    {
        _stop.Cancel();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
