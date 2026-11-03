using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PhotoScope.Services;

/// <summary>A request handed from a second launch to the running PhotoScope.</summary>
public sealed record InstanceMessage(string Command, string? Path = null)
{
    public const string Open = "open";
    public const string Activate = "activate";
}

/// <summary>
/// Keeps PhotoScope to one running copy per user (and data folder). The first copy owns a mutex and
/// listens on a named pipe; later launches send their file path down the pipe and exit. The pipe
/// only accepts connections from the same Windows user (PipeOptions.CurrentUserOnly), messages are
/// small JSON documents with a size cap, and anything malformed is ignored.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    const int MaxMessageBytes = 256 * 1024;

    // Paths are written as plain UTF-8 (not \uXXXX escapes) so long unicode paths stay small.
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    readonly Mutex? _mutex;
    readonly string _pipeName;
    CancellationTokenSource? _serverCts;
    Task? _serverTask;

    public bool IsPrimary { get; }
    public string PipeName => _pipeName;

    SingleInstance(Mutex? mutex, bool primary, string pipeName)
    {
        _mutex = mutex;
        IsPrimary = primary;
        _pipeName = pipeName;
    }

    /// <summary>An identifier unique to this Windows user and PhotoScope data folder.</summary>
    public static string IdFor(string dataRoot)
    {
        string user;
        try { user = WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName; }
        catch { user = Environment.UserName; }
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(user + "|" + Path.GetFullPath(dataRoot).ToUpperInvariant()));
        return "PhotoScope-" + Convert.ToHexString(bytes)[..20];
    }

    /// <summary>Claims the single-instance mutex. Check <see cref="IsPrimary"/>.</summary>
    public static SingleInstance Acquire(string id)
    {
        Mutex mutex;
        try
        {
            mutex = new Mutex(false, @"Local\" + id);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            // E.g. the running copy is elevated and we aren't: run on our own rather than fail.
            Log.Warn("Single-instance mutex unavailable; running as a separate copy", ex);
            return new SingleInstance(null, primary: true, id) { Standalone = true };
        }
        bool owned;
        try
        {
            owned = mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            owned = true; // the previous copy crashed; we take over
        }
        return new SingleInstance(mutex, owned, id);
    }

    /// <summary>True when single-instance support couldn't be set up and this copy runs on its own.</summary>
    public bool Standalone { get; private init; }

    /// <summary>Primary only: listens for messages from later launches and passes them to <paramref name="onMessage"/>.</summary>
    /// <param name="onMessage">
    /// Returns true once the message has been accepted (e.g. queued on the UI thread). Only then is
    /// the sender told it was delivered; false or no answer within 10 s means "not handled".
    /// </param>
    public void StartServer(Func<InstanceMessage, Task<bool>> onMessage)
    {
        if (!IsPrimary || Standalone || _serverTask != null) return;
        _serverCts = new CancellationTokenSource();
        var ct = _serverCts.Token;
        _serverTask = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    await server.WaitForConnectionAsync(ct);
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(5));
                    var message = await ReadMessageAsync(server, timeout.Token);
                    if (message != null)
                    {
                        var accepted = false;
                        try { accepted = await onMessage(message).WaitAsync(TimeSpan.FromSeconds(10), ct); }
                        catch (TimeoutException) { Log.Warn("Single-instance pipe: the UI didn't accept a message in time"); }
                        catch (Exception ex) when (ex is not OperationCanceledException) { Log.Error("Handling a message from another PhotoScope launch failed", ex); }
                        using var ackTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        ackTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                        await server.WriteAsync(new byte[] { accepted ? (byte)1 : (byte)0 }, ackTimeout.Token);
                        await server.FlushAsync(ackTimeout.Token);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // A bad client must never stop the server: log and keep listening.
                    Log.Warn("Single-instance pipe: connection failed", ex);
                    try { await Task.Delay(200, ct); } catch (OperationCanceledException) { break; }
                }
            }
        }, CancellationToken.None);
    }

    static async Task<InstanceMessage?> ReadMessageAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var length = BitConverter.ToInt32(header);
        if (length <= 0 || length > MaxMessageBytes)
        {
            Log.Warn($"Single-instance pipe: ignored a message of {length} bytes");
            return null;
        }
        var body = new byte[length];
        await stream.ReadExactlyAsync(body, ct);
        try
        {
            var msg = JsonSerializer.Deserialize<InstanceMessage>(body, Json);
            if (msg is not { Command: InstanceMessage.Open or InstanceMessage.Activate }) return null;
            if (msg.Command == InstanceMessage.Open && string.IsNullOrWhiteSpace(msg.Path)) return null;
            return msg;
        }
        catch (JsonException)
        {
            Log.Warn("Single-instance pipe: ignored a malformed message");
            return null;
        }
    }

    /// <summary>
    /// Later launches: sends a message to the running copy, retrying while it starts up.
    /// Returns true once the running copy has acknowledged it.
    /// </summary>
    public static bool TrySend(string id, InstanceMessage message, TimeSpan timeout)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        if (body.Length > MaxMessageBytes) return false;
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", id, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                var remaining = (int)Math.Max(100, (deadline - DateTime.UtcNow).TotalMilliseconds);
                client.Connect(Math.Min(remaining, 1000));

                // Let the running copy bring its window to the front (we were the foreground app).
                if (GetNamedPipeServerProcessId(client.SafePipeHandle, out var serverPid)) AllowSetForegroundWindow(serverPid);

                // Allow for the running copy's UI to accept the message (it answers within ~10 s).
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                client.WriteAsync(BitConverter.GetBytes(body.Length), cts.Token).AsTask().GetAwaiter().GetResult();
                client.WriteAsync(body, cts.Token).AsTask().GetAwaiter().GetResult();
                client.Flush();
                var ack = new byte[1];
                var read = client.ReadAsync(ack, cts.Token).AsTask().GetAwaiter().GetResult();
                if (read == 1 && ack[0] == 1) return true;
                if (read == 1) return false; // reached it, but it couldn't take the message (shutting down or hung)
            }
            catch (Exception ex) when (ex is TimeoutException or IOException or OperationCanceledException or UnauthorizedAccessException)
            {
                // Not listening yet (still starting) or busy with another launch: retry shortly.
            }
            Thread.Sleep(150);
        }
        return false;
    }

    public void Dispose()
    {
        _serverCts?.Cancel();
        try { _serverTask?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        if (IsPrimary && _mutex != null)
        {
            try { _mutex.ReleaseMutex(); } catch { }
        }
        _mutex?.Dispose();
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetNamedPipeServerProcessId(Microsoft.Win32.SafeHandles.SafePipeHandle pipe, out uint serverProcessId);

    [DllImport("user32.dll")]
    static extern bool AllowSetForegroundWindow(uint processId);
}
