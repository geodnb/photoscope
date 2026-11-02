using System.Collections.Concurrent;

namespace PhotoScope.Services;

/// <summary>Raised when a network location doesn't answer in time (or is being skipped after not answering).</summary>
public sealed class NetworkTimeoutException(string message) : IOException(message);

/// <summary>
/// Helpers for photos on network shares and mapped drives. File calls against an unresponsive
/// share can block for a long time and can't be cancelled, so work on network paths runs on its
/// own threads with a timeout. A share that times out is skipped (backed off) for a while, and for
/// as long as any abandoned call to it is still stuck, so at most one stuck thread per share
/// accumulates no matter how many photos are on it.
/// </summary>
public static class NetworkPaths
{
    public static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(4);
    public static TimeSpan ReadTimeout { get; internal set; } = TimeSpan.FromSeconds(20); // settable by tests
    static readonly TimeSpan BackOff = TimeSpan.FromSeconds(60);

    static readonly ConcurrentDictionary<char, bool> NetworkDrives = new();
    static readonly ConcurrentDictionary<string, long> UnreachableUntil = new(StringComparer.OrdinalIgnoreCase);
    static readonly ConcurrentDictionary<string, int> StuckCalls = new(StringComparer.OrdinalIgnoreCase);

    static long Now => Environment.TickCount64; // monotonic: unaffected by clock changes

    /// <summary>True for UNC paths and mapped network drives.</summary>
    public static bool IsNetwork(string path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return !path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase);
        if (path.Length < 2 || path[1] != ':') return false;
        var letter = char.ToUpperInvariant(path[0]);
        if (NetworkDrives.TryGetValue(letter, out var cached)) return cached;
        DriveType type;
        try { type = new DriveInfo(letter + ":\\").DriveType; }
        catch { return false; }
        // Only remember definite answers: a letter that isn't mapped yet may become a network drive later.
        if (type is not (DriveType.NoRootDirectory or DriveType.Unknown)) NetworkDrives[letter] = type == DriveType.Network;
        return type == DriveType.Network;
    }

    /// <summary>The share or drive a path belongs to (\\server\share or X:\).</summary>
    public static string RootOf(string path)
    {
        try
        {
            if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
                path = @"\\" + path[8..];
            if (path.StartsWith(@"\\", StringComparison.Ordinal))
            {
                var parts = path.TrimStart('\\').Split('\\', 3);
                return parts.Length >= 2 ? $@"\\{parts[0]}\{parts[1]}" : path;
            }
            return Path.GetPathRoot(path) ?? path;
        }
        catch
        {
            return path;
        }
    }

    /// <summary>True while a share is being skipped because it recently didn't answer (or a call to it is still stuck).</summary>
    public static bool IsBackedOff(string path)
    {
        var root = RootOf(path);
        if (StuckCalls.TryGetValue(root, out var stuck) && stuck > 0) return true;
        return UnreachableUntil.TryGetValue(root, out var until) && until > Now;
    }

    static void Abandon(string path, Task task)
    {
        var root = RootOf(path);
        UnreachableUntil[root] = Now + (long)BackOff.TotalMilliseconds;
        StuckCalls.AddOrUpdate(root, 1, (_, n) => n + 1);
        Log.Warn($"Network location not responding; skipping it for at least {BackOff.TotalSeconds:0} s: {root}");
        // When the stuck call finally returns, stop counting it (and observe any exception).
        task.ContinueWith(t =>
        {
            _ = t.Exception;
            StuckCalls.AddOrUpdate(root, 0, (_, n) => Math.Max(0, n - 1));
        }, TaskScheduler.Default);
    }

    static NetworkTimeoutException NotResponding() => new("The network location isn't responding.");

    /// <summary>
    /// Runs <paramref name="work"/> for a network path on a dedicated thread and waits at most
    /// <paramref name="timeout"/>. On timeout the share is backed off and a
    /// <see cref="NetworkTimeoutException"/> is thrown; the stuck call finishes on its own thread
    /// and is ignored. Local paths just run <paramref name="work"/> directly.
    /// </summary>
    public static T Run<T>(string path, Func<T> work, TimeSpan timeout)
    {
        if (!IsNetwork(path)) return work();
        if (IsBackedOff(path)) throw NotResponding();

        var task = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        bool finished;
        try
        {
            finished = task.Wait(timeout);
        }
        catch (AggregateException ex)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerExceptions.Count == 1 ? ex.InnerException! : ex).Throw();
            throw; // unreachable
        }
        if (finished) return task.Result;
        Abandon(path, task);
        throw NotResponding();
    }

    /// <summary>Async variant of <see cref="Run{T}"/> that never blocks the calling thread.</summary>
    public static async Task<T> RunAsync<T>(string path, Func<T> work, TimeSpan timeout)
    {
        if (!IsNetwork(path)) return await Task.Run(work);
        if (IsBackedOff(path)) throw NotResponding();

        var task = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        if (await Task.WhenAny(task, Task.Delay(timeout)) == task) return await task;
        Abandon(path, task);
        throw NotResponding();
    }

    /// <summary>Async check that a file exists; null when a network share didn't answer.</summary>
    public static async Task<bool?> FileExistsAsync(string path)
    {
        try { return await RunAsync(path, () => File.Exists(path), ProbeTimeout); }
        catch (NetworkTimeoutException) { return null; }
    }
}
