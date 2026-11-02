using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoScope.Models;

namespace PhotoScope.Services;

/// <summary>
/// Loads thumbnails on background threads with newest-first priority, so whatever is on screen
/// right now loads first while scrolling. Keeps a bounded in-memory LRU so memory stays flat
/// no matter how big the library is.
/// Two queues: local workers serve the disk cache and local files; photos on network shares that
/// still need a thumbnail are handed to separate network workers with timeouts, so a share that
/// stops responding can never hold up thumbnails for local photos.
/// </summary>
public sealed class ThumbnailLoader
{
    public sealed class Request
    {
        internal Request(Photo photo, int size, Action<BitmapSource?> done)
        {
            Photo = photo;
            Size = size;
            Done = done;
        }

        public Photo Photo { get; }
        public int Size { get; }
        internal Action<BitmapSource?> Done { get; }
        internal volatile bool Cancelled;
        internal LinkedListNode<Request>? Node;
    }

    public static ThumbnailLoader Instance { get; private set; } = null!;

    const int NetworkWorkers = 2;

    /// <summary>Test hook run inside network work (e.g. to simulate a share that hangs).</summary>
    internal static Action<Photo>? TestNetworkHook { get; set; }

    readonly ThumbnailCache _disk;
    readonly Dispatcher _ui;
    readonly LinkedList<Request> _local = new();
    readonly LinkedList<Request> _network = new();
    readonly object _gate = new();
    readonly SemaphoreSlim _localSignal = new(0);
    readonly SemaphoreSlim _networkSignal = new(0);
    // Bounded by bytes, not count, so big thumbnails on high-DPI screens can't balloon memory.
    readonly LruCache<(long Id, int Size, long Ticks), BitmapSource> _memory =
        new(160L * 1024 * 1024, b => (long)b.PixelWidth * b.PixelHeight * 4);

    /// <summary>Raised on the UI thread when a photo can't be decoded.</summary>
    public event Action<Photo, string, bool>? DecodeFailed;

    public static void Initialize(ThumbnailCache disk, Dispatcher ui) => Instance = new ThumbnailLoader(disk, ui);

    ThumbnailLoader(ThumbnailCache disk, Dispatcher ui)
    {
        _disk = disk;
        _ui = ui;
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (var i = 0; i < workers; i++)
            StartWorker(() => Worker(network: false), $"Thumbnail worker {i + 1}");
        for (var i = 0; i < NetworkWorkers; i++)
            StartWorker(() => Worker(network: true), $"Network thumbnail worker {i + 1}");
    }

    static void StartWorker(ThreadStart body, string name) =>
        new Thread(body) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = name }.Start();

    public ThumbnailCache Disk => _disk;

    /// <summary>
    /// Requests a thumbnail. If it is in memory, <paramref name="done"/> runs immediately and null is
    /// returned; otherwise it runs later on the UI thread unless the request is cancelled first.
    /// </summary>
    public Request? Load(Photo photo, int size, Action<BitmapSource?> done)
    {
        if (_memory.TryGet((photo.Id, size, photo.ModifiedUtc.Ticks), out var cached))
        {
            done(cached);
            return null;
        }
        var r = new Request(photo, size, done);
        Enqueue(r, network: false);
        return r;
    }

    void Enqueue(Request r, bool network)
    {
        lock (_gate)
        {
            if (r.Cancelled) return;
            r.Node = (network ? _network : _local).AddLast(r);
        }
        (network ? _networkSignal : _localSignal).Release();
    }

    public void Cancel(Request? r)
    {
        if (r == null) return;
        r.Cancelled = true;
        lock (_gate)
        {
            r.Node?.List?.Remove(r.Node);
            r.Node = null;
        }
    }

    /// <summary>Drops in-memory thumbnails (e.g. after clearing the disk cache).</summary>
    public void ClearMemory() => _memory.Clear();

    void Worker(bool network)
    {
        var queue = network ? _network : _local;
        var signal = network ? _networkSignal : _localSignal;
        while (true)
        {
            signal.Wait();
            Request? r;
            lock (_gate)
            {
                r = queue.Last?.Value;
                if (r == null) continue;
                queue.RemoveLast();
                r.Node = null;
            }
            if (r.Cancelled) continue;

            BitmapSource? bmp = null;
            string? error = null;
            var permanent = false;
            try
            {
                if (network)
                {
                    bmp = ProduceFromNetwork(r.Photo, r.Size);
                }
                else
                {
                    bmp = _disk.TryLoad(r.Photo, r.Size);
                    if (bmp == null && NeedsOriginal(r.Photo))
                    {
                        if (NetworkPaths.IsNetwork(r.Photo.Path))
                        {
                            Enqueue(r, network: true); // hand off; this worker stays free for local photos
                            continue;
                        }
                        bmp = ProduceFromOriginal(r.Photo, r.Size);
                    }
                }
            }
            catch (NetworkTimeoutException)
            {
                // Share not answering: show nothing for now, try again on a later request.
            }
            catch (Exception ex)
            {
                error = Friendly.DescribeImageError(ex, r.Photo.Path);
                permanent = Friendly.IsPermanentImageError(ex);
                Log.Warn($"Thumbnail failed for {r.Photo.Path}: {error}", ex);
            }

            if (bmp != null)
                _memory.Add((r.Photo.Id, r.Size, r.Photo.ModifiedUtc.Ticks), bmp);

            var request = r;
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (error != null && request.Photo.DecodeError == null && !request.Photo.IsMissing)
                    DecodeFailed?.Invoke(request.Photo, error, permanent);
                if (!request.Cancelled) request.Done(bmp);
            });
        }
    }

    /// <summary>Only try the original if it isn't known to be missing or undecodable.</summary>
    static bool NeedsOriginal(Photo p) => !p.IsMissing && p.DecodeError == null;

    BitmapSource? ProduceFromOriginal(Photo p, int size)
    {
        if (!File.Exists(p.Path)) return null;
        var generated = _disk.Generate(p);
        return ImageDecoder.ScaleDown(generated, size);
    }

    BitmapSource? ProduceFromNetwork(Photo p, int size)
    {
        if (NetworkPaths.IsBackedOff(p.Path)) return null;
        // The whole read + decode runs with a timeout; a hung share costs at most this worker's wait.
        return NetworkPaths.Run(p.Path, () =>
        {
            TestNetworkHook?.Invoke(p);
            return ProduceFromOriginal(p, size);
        }, NetworkPaths.ReadTimeout);
    }
}

/// <summary>Small thread-safe least-recently-used cache with a total weight budget.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    readonly long _budget;
    readonly Func<TValue, long> _weigh;
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    readonly LinkedList<(TKey Key, TValue Value)> _list = new();
    readonly object _gate = new();
    long _total;

    public LruCache(long budget, Func<TValue, long>? weigh = null)
    {
        _budget = Math.Max(1, budget);
        _weigh = weigh ?? (_ => 1);
    }

    public bool TryGet(TKey key, out TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var node))
            {
                _list.Remove(node);
                _list.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
        }
        value = default!;
        return false;
    }

    public void Add(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_map.TryGetValue(key, out var existing))
            {
                _list.Remove(existing);
                _map.Remove(key);
                _total -= _weigh(existing.Value.Value);
            }
            _map[key] = _list.AddFirst((key, value));
            _total += _weigh(value);
            while (_total > _budget && _map.Count > 1 && _list.Last is { } last)
            {
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
                _total -= _weigh(last.Value.Value);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _list.Clear();
            _total = 0;
        }
    }
}
