using System.Diagnostics;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoScope.Models;
using PhotoScope.Services;

namespace PhotoScope.Tests;

[Collection("ThumbnailLoader")]
public sealed class NetworkTests : IDisposable
{
    readonly TempDir _tmp = new();

    public void Dispose()
    {
        ThumbnailLoader.TestNetworkHook = null;
        NetworkPaths.ReadTimeout = TimeSpan.FromSeconds(20);
        _tmp.Dispose();
    }

    [Theory]
    [InlineData(@"\\server\share\a.jpg", true)]
    [InlineData(@"\\?\UNC\server\share\a.jpg", true)]
    [InlineData(@"\\?\C:\a.jpg", false)]
    [InlineData("relative.jpg", false)]
    public void Detects_unc_paths(string path, bool network) => Assert.Equal(network, NetworkPaths.IsNetwork(path));

    [Fact]
    public void Root_of_unc_path_is_the_share() =>
        Assert.Equal(@"\\server\share", NetworkPaths.RootOf(@"\\server\share\folder\a.jpg"));

    [Fact]
    public void Hung_share_times_out_then_is_skipped_quickly()
    {
        var path = $@"\\photoscope-hang-{Guid.NewGuid():N}\share\a.jpg";
        var sw = Stopwatch.StartNew();
        Assert.Throws<NetworkTimeoutException>(() => NetworkPaths.Run(path, () => { Thread.Sleep(3000); return 1; }, TimeSpan.FromMilliseconds(200)));
        Assert.True(sw.ElapsedMilliseconds < 2000, $"took {sw.ElapsedMilliseconds} ms");
        Assert.True(NetworkPaths.IsBackedOff(path));

        // While backed off, calls fail immediately without touching the share.
        sw.Restart();
        var touched = false;
        Assert.Throws<NetworkTimeoutException>(() => NetworkPaths.Run(path, () => { touched = true; return 1; }, TimeSpan.FromSeconds(5)));
        Assert.False(touched);
        Assert.True(sw.ElapsedMilliseconds < 500);
    }

    [Fact]
    public void Stuck_share_gets_at_most_one_abandoned_call()
    {
        var path = $@"\\photoscope-stuck-{Guid.NewGuid():N}\share\a.jpg";
        var started = 0;
        var release = new ManualResetEventSlim();
        for (var i = 0; i < 20; i++)
        {
            Assert.Throws<NetworkTimeoutException>(() => NetworkPaths.Run(path, () =>
            {
                Interlocked.Increment(ref started);
                release.Wait(TimeSpan.FromSeconds(30));
                return 1;
            }, TimeSpan.FromMilliseconds(100)));
        }
        Assert.Equal(1, Volatile.Read(ref started));   // only the first call reached the share
        release.Set();
    }

    [Theory]
    [InlineData(@"\\?\UNC\srv\share\a.jpg", @"\\srv\share")]
    [InlineData(@"\\srv\share\deep\a.jpg", @"\\srv\share")]
    public void Long_path_unc_roots_are_per_share(string path, string root) => Assert.Equal(root, NetworkPaths.RootOf(path));

    [Fact]
    public void Local_paths_run_directly() =>
        Assert.Equal(42, NetworkPaths.Run(_tmp.Sub("x.jpg"), () => 42, TimeSpan.FromMilliseconds(1)));

    /// <summary>
    /// Several thumbnails for a share that hangs are queued first, then one local photo. The local
    /// thumbnail must arrive promptly even though every network worker is stuck.
    /// </summary>
    [Fact]
    public void Hung_network_share_does_not_delay_local_thumbnails()
    {
        // A dispatcher thread to receive the loader's callbacks.
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() =>
        {
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait();

        try
        {
            NetworkPaths.ReadTimeout = TimeSpan.FromSeconds(3);
            ThumbnailLoader.TestNetworkHook = _ => Thread.Sleep(10_000); // a share that never answers
            ThumbnailLoader.Initialize(new ThumbnailCache(_tmp.Sub("thumbs")), dispatcher!);
            var loader = ThumbnailLoader.Instance;

            var server = $"photoscope-hang-{Guid.NewGuid():N}";
            var networkDone = 0;
            for (var i = 0; i < 6; i++)
            {
                var np = new Photo { Id = 1000 + i, Path = $@"\\{server}\share\p{i}.jpg", Hash = $"{i:x16}", PixelWidth = 64, PixelHeight = 48 };
                loader.Load(np, 120, _ => Interlocked.Increment(ref networkDone));
            }
            Thread.Sleep(300); // let the network workers pick up (and hang on) their requests

            var localPath = TestImages.Jpeg(_tmp.Sub("local.jpg"), w: 64, h: 48);
            var local = new Photo { Id = 1, Path = localPath, Hash = "abcdefabcdef0123", PixelWidth = 64, PixelHeight = 48, ModifiedUtc = File.GetLastWriteTimeUtc(localPath) };
            var got = new TaskCompletionSource<BitmapSource?>();
            var sw = Stopwatch.StartNew();
            loader.Load(local, 120, b => got.TrySetResult(b));

            // Well inside the 3 s network timeout: the local photo didn't wait for the hung share.
            Assert.True(got.Task.Wait(TimeSpan.FromSeconds(2.5)), "local thumbnail was held up by the hung share");
            Assert.NotNull(got.Task.Result);

            // The hung requests time out (no thumbnail) and the rest are skipped while the share backs off.
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (Volatile.Read(ref networkDone) < 6 && DateTime.UtcNow < deadline) Thread.Sleep(100);
            Assert.Equal(6, Volatile.Read(ref networkDone));
        }
        finally
        {
            dispatcher?.InvokeShutdown();
        }
    }

    /// <summary>A request handed to the network queue is answered exactly once; one cancelled meanwhile isn't answered.</summary>
    [Fact]
    public void Network_handoff_answers_once_and_respects_cancel()
    {
        Dispatcher? dispatcher = null;
        var ready = new ManualResetEventSlim();
        var ui = new Thread(() => { dispatcher = Dispatcher.CurrentDispatcher; ready.Set(); Dispatcher.Run(); });
        ui.SetApartmentState(ApartmentState.STA);
        ui.IsBackground = true;
        ui.Start();
        ready.Wait();
        try
        {
            // Network work that takes a moment and then fails fast, without touching a real server.
            ThumbnailLoader.TestNetworkHook = _ => { Thread.Sleep(300); throw new FileNotFoundException("simulated"); };
            ThumbnailLoader.Initialize(new ThumbnailCache(_tmp.Sub("thumbs2")), dispatcher!);
            var loader = ThumbnailLoader.Instance;
            var server = $"photoscope-handoff-{Guid.NewGuid():N}";

            var answered = 0;
            var p1 = new Photo { Id = 2001, Path = $@"\\{server}\share\a.jpg", Hash = "1111111111111111", PixelWidth = 10, PixelHeight = 10 };
            loader.Load(p1, 120, _ => Interlocked.Increment(ref answered));

            var cancelledAnswered = 0;
            var p2 = new Photo { Id = 2002, Path = $@"\\{server}\share\b.jpg", Hash = "2222222222222222", PixelWidth = 10, PixelHeight = 10 };
            var r2 = loader.Load(p2, 120, _ => Interlocked.Increment(ref cancelledAnswered));
            Thread.Sleep(50); // likely mid hand-off or waiting on the network queue
            loader.Cancel(r2);

            Thread.Sleep(2000);
            Assert.Equal(1, Volatile.Read(ref answered));
            Assert.Equal(0, Volatile.Read(ref cancelledAnswered));
        }
        finally
        {
            dispatcher?.InvokeShutdown();
        }
    }
}
