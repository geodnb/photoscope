using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoScope.Models;

namespace PhotoScope.Services;

/// <summary>
/// Loads thumbnails on background threads with newest-first priority, so whatever is on screen
/// right now loads first while scrolling. Keeps a bounded in-memory LRU so memory stays flat
/// no matter how big the library is.
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

    readonly ThumbnailCache _disk;
    readonly Dispatcher _ui;
    readonly LinkedList<Request> _queue = new();
    readonly object _gate = new();
    readonly SemaphoreSlim _signal = new(0);
    readonly LruCache<(long Id, int Size, long Ticks), BitmapSource> _memory = new(700);

    /// <summary>Raised on the UI thread when a photo can't be decoded.</summary>
    public event Action<Photo, string>? DecodeFailed;

    public static void Initialize(ThumbnailCache disk, Dispatcher ui) => Instance = new ThumbnailLoader(disk, ui);

    ThumbnailLoader(ThumbnailCache disk, Dispatcher ui)
    {
        _disk = disk;
        _ui = ui;
        var workers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
        for (var i = 0; i < workers; i++)
        {
            new Thread(Worker)
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = $"Thumbnail worker {i + 1}",
            }.Start();
        }
    }

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
        lock (_gate) r.Node = _queue.AddLast(r);
        _signal.Release();
        return r;
    }

    public void Cancel(Request? r)
    {
        if (r == null) return;
        r.Cancelled = true;
        lock (_gate)
        {
            if (r.Node?.List != null) _queue.Remove(r.Node);
            r.Node = null;
        }
    }

    /// <summary>Drops in-memory thumbnails (e.g. after clearing the disk cache).</summary>
    public void ClearMemory() => _memory.Clear();

    void Worker()
    {
        while (true)
        {
            _signal.Wait();
            Request? r;
            lock (_gate)
            {
                r = _queue.Last?.Value;
                if (r == null) continue;
                _queue.RemoveLast();
                r.Node = null;
            }
            if (r.Cancelled) continue;

            BitmapSource? bmp = null;
            string? error = null;
            try
            {
                bmp = Produce(r.Photo, r.Size);
            }
            catch (Exception ex)
            {
                error = Friendly.DescribeImageError(ex, r.Photo.Path);
                Log.Warn($"Thumbnail failed for {r.Photo.Path}: {error}", ex);
            }

            if (bmp != null)
                _memory.Add((r.Photo.Id, r.Size, r.Photo.ModifiedUtc.Ticks), bmp);

            var request = r;
            _ui.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (error != null && request.Photo.DecodeError == null && !request.Photo.IsMissing)
                    DecodeFailed?.Invoke(request.Photo, error);
                if (!request.Cancelled) request.Done(bmp);
            });
        }
    }

    BitmapSource? Produce(Photo p, int size)
    {
        var fromDisk = _disk.TryLoad(p, size);
        if (fromDisk != null) return fromDisk;

        // No cached thumbnail: only try the original if it is there and decodable.
        if (p.IsMissing || p.DecodeError != null) return null;
        if (!File.Exists(p.Path)) return null;

        var generated = _disk.Generate(p);
        return ImageDecoder.ScaleDown(generated, size);
    }
}

/// <summary>Small thread-safe least-recently-used cache.</summary>
public sealed class LruCache<TKey, TValue> where TKey : notnull
{
    readonly int _capacity;
    readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _map = new();
    readonly LinkedList<(TKey Key, TValue Value)> _list = new();
    readonly object _gate = new();

    public LruCache(int capacity) => _capacity = Math.Max(1, capacity);

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
            }
            _map[key] = _list.AddFirst((key, value));
            while (_map.Count > _capacity && _list.Last is { } last)
            {
                _list.RemoveLast();
                _map.Remove(last.Value.Key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _map.Clear();
            _list.Clear();
        }
    }
}
