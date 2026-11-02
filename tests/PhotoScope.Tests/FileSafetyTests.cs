using PhotoScope.Models;
using PhotoScope.Services;

namespace PhotoScope.Tests;

public sealed class FileSafetyTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose() => _tmp.Dispose();

    [Theory]
    [InlineData("holiday.jpg", true)]
    [InlineData("a b.jpg", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("bad:name.jpg", false)]
    [InlineData("bad?.jpg", false)]
    [InlineData("trailing.", false)]
    [InlineData("trailing ", false)]
    [InlineData("CON.jpg", false)]
    [InlineData("lpt1", false)]
    [InlineData("con-not-reserved.jpg", true)]
    [InlineData("..", false)]
    [InlineData("sub\\dir.jpg", false)]
    public void Validates_file_names(string name, bool ok) => Assert.Equal(ok, FileOps.ValidateFileName(name) == null);

    [Fact]
    public void Rename_refuses_to_overwrite()
    {
        var a = Path.Combine(_tmp.Path, "a.jpg");
        var b = Path.Combine(_tmp.Path, "b.jpg");
        File.WriteAllText(a, "A");
        File.WriteAllText(b, "B");
        Assert.Throws<UserFacingException>(() => FileOps.Rename(a, "b.jpg"));
        Assert.Throws<UserFacingException>(() => FileOps.Rename(a, "B.JPG"));
        Assert.Equal("A", File.ReadAllText(a));
        Assert.Equal("B", File.ReadAllText(b));
    }

    [Fact]
    public void Rename_allows_case_only_change()
    {
        var a = Path.Combine(_tmp.Path, "photo.jpg");
        File.WriteAllText(a, "A");
        var result = FileOps.Rename(a, "Photo.jpg");
        Assert.Equal("Photo.jpg", Path.GetFileName(Directory.GetFiles(_tmp.Path).Single()));
        Assert.Equal(Path.Combine(_tmp.Path, "Photo.jpg"), result);
    }

    [Fact]
    public void Move_and_copy_pick_a_free_name()
    {
        var src = Path.Combine(_tmp.Path, "src", "x.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.WriteAllText(src, "new");
        var dest = _tmp.Sub("dest");
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, "x.jpg"), "old");

        var copied = FileOps.CopyNoOverwrite(src, dest);
        Assert.Equal(Path.Combine(dest, "x (2).jpg"), copied);
        var moved = FileOps.MoveNoOverwrite(src, dest);
        Assert.Equal(Path.Combine(dest, "x (3).jpg"), moved);
        Assert.Equal("old", File.ReadAllText(Path.Combine(dest, "x.jpg")));
        Assert.False(File.Exists(src));
    }

    [Fact]
    public void Recycle_is_refused_where_there_is_no_recycle_bin()
    {
        Assert.False(FileOps.CanRecycle(@"\\server\share\photo.jpg", out var reason));
        Assert.Contains("network", reason);
        var local = Path.Combine(_tmp.Path, "x.jpg");
        File.WriteAllText(local, "x");
        // Depends on this PC's bin settings: either allowed, or refused with a clear reason.
        if (!FileOps.CanRecycle(local, out var why)) Assert.Contains("Recycle Bin", why);
        Assert.False(FileOps.CanRecycle(Path.Combine(_tmp.Path, "missing.jpg"), out _));
    }

    static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() => { try { action(); } catch (Exception ex) { error = ex; } });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception("STA test failed", error);
    }

    /// <summary>
    /// Forces the progress sink down its abort path (as if Windows said "can't recycle"): the
    /// operation must be refused and the file left byte-for-byte untouched. Runs on every machine.
    /// </summary>
    [Fact]
    public void Unrecyclable_file_is_never_deleted_simulated() => RunSta(() =>
    {
        var f = Path.Combine(_tmp.Path, "cannot-recycle.jpg");
        File.WriteAllText(f, "original contents");
        var result = RecycleBin.Recycle(f, simulateNotRecyclable: true);
        Assert.Equal(RecycleOutcome.Refused, result.Outcome);
        Assert.True(File.Exists(f));
        Assert.Equal("original contents", File.ReadAllText(f));
    });

    /// <summary>
    /// The real thing, with Windows deciding: ask IFileOperation directly (skipping PhotoScope's own
    /// pre-checks) to recycle a file in a place Windows can't recycle from. Windows would delete it
    /// permanently, so the progress sink must abort and the file must survive untouched.
    /// Uses PHOTOSCOPE_TEST_NOBIN_DIR if set (e.g. a folder on a subst drive), otherwise the temp
    /// folder when its drive's bin is set to "remove files immediately"; otherwise it's skipped.
    /// </summary>
    [SkippableFact]
    public void Unrecyclable_file_is_never_deleted_by_windows()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOSCOPE_TEST_NOBIN_DIR");
        if (string.IsNullOrEmpty(dir))
        {
            var probe = Path.Combine(_tmp.Path, "probe.txt");
            File.WriteAllText(probe, "x");
            var noBinHere = !FileOps.CanRecycle(probe, out var why) && why.Contains("remove files immediately");
            Skip.IfNot(noBinHere, "Set PHOTOSCOPE_TEST_NOBIN_DIR to a folder Windows can't recycle from (e.g. on a subst drive).");
            dir = _tmp.Path;
        }
        RunSta(() => NeverDeletedIn(dir!));
    }

    void NeverDeletedIn(string dir)
    {
        var f = Path.Combine(dir, $"photoscope-no-bin-test-{Guid.NewGuid():N}.jpg");
        File.WriteAllText(f, "original contents");
        try
        {
            var result = RecycleBin.Recycle(f);
            Assert.Equal(RecycleOutcome.Refused, result.Outcome);
            Assert.True(File.Exists(f));
            Assert.Equal("original contents", File.ReadAllText(f));

            // Through the app's entry point: refused with a clear reason, file still untouched.
            var ex = Assert.Throws<UserFacingException>(() => FileOps.SendToRecycleBin(f));
            Assert.Contains("left alone", ex.Message);
            Assert.True(File.Exists(f));
            Assert.Equal("original contents", File.ReadAllText(f));
        }
        finally
        {
            if (File.Exists(f)) File.Delete(f); // test's own scratch file
        }
    }

    /// <summary>Opt-in: PHOTOSCOPE_TEST_RECYCLE_DIR=a folder on a drive with a working Recycle Bin.</summary>
    [SkippableFact]
    public void Recyclable_file_goes_to_the_recycle_bin()
    {
        var dir = Environment.GetEnvironmentVariable("PHOTOSCOPE_TEST_RECYCLE_DIR");
        Skip.If(string.IsNullOrEmpty(dir), "Set PHOTOSCOPE_TEST_RECYCLE_DIR to a folder with a working Recycle Bin (puts a tiny file in the bin).");
        RunSta(() => RecyclesIn(dir!));
    }

    static void RecyclesIn(string dir)
    {
        var f = Path.Combine(dir, $"photoscope-recycle-test-{Guid.NewGuid():N}.txt");
        File.WriteAllText(f, "safe to empty from the Recycle Bin");
        Assert.True(FileOps.CanRecycle(f, out var why), why);
        Assert.Equal(RecycleOutcome.Recycled, RecycleBin.Recycle(f).Outcome);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Thumbnail_cache_never_deletes_outside_its_folder_even_through_a_junction()
    {
        var cacheDir = _tmp.Sub("cache");
        var victimDir = _tmp.Sub("victim");
        Directory.CreateDirectory(victimDir);
        // A file outside the cache whose name even matches the cache's naming pattern.
        var victim = Path.Combine(victimDir, "aaaaaaaaaaaaaaaa_1.jpg");
        File.WriteAllText(victim, "precious");
        var keep = Path.Combine(_tmp.Path, "keep.jpg");
        File.WriteAllText(keep, "keep");

        var cache = new ThumbnailCache(cacheDir);
        var own = Path.Combine(cacheDir, "ab", "abababababababab_1.jpg");
        Directory.CreateDirectory(Path.GetDirectoryName(own)!);
        File.WriteAllText(own, "thumb");
        var foreign = Path.Combine(cacheDir, "ab", "notes.txt");
        File.WriteAllText(foreign, "not ours");

        // Junction inside the cache pointing at the victim folder (junctions need no admin rights).
        var junction = Path.Combine(cacheDir, "aa");
        var psi = new System.Diagnostics.ProcessStartInfo("cmd.exe") { CreateNoWindow = true, UseShellExecute = false };
        psi.ArgumentList.Add("/c");
        psi.ArgumentList.Add("mklink");
        psi.ArgumentList.Add("/J");
        psi.ArgumentList.Add(junction);
        psi.ArgumentList.Add(victimDir);
        System.Diagnostics.Process.Start(psi)!.WaitForExit();
        Assert.True(Directory.Exists(junction), "test setup: junction not created");

        cache.Trim(0);
        cache.Clear();

        Assert.True(File.Exists(victim));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(foreign));
        Assert.False(File.Exists(own));

        // A malformed hash can't escape the cache folder.
        var p = new Photo { Id = 5, Hash = @"..\..\evil", Path = keep };
        Assert.StartsWith(cache.Folder, Path.GetFullPath(cache.PathFor(p, png: false)));
        Directory.Delete(junction); // removes the link only, not its target
    }

    [Theory]
    [InlineData(@"D:\Photos\Library", true)]
    [InlineData(@"relative\folder", false)]
    [InlineData(@"C:\", false)]
    [InlineData(@"\\?\C:\x", false)]
    [InlineData("", false)]
    public void Library_folder_validation(string path, bool ok) =>
        Assert.Equal(ok, AppSettings.ValidateLibraryFolder(path) == null);

    [Fact]
    public void Library_folder_cannot_be_inside_app_data() =>
        Assert.NotNull(AppSettings.ValidateLibraryFolder(Path.Combine(AppPaths.Root, "thumbs", "lib")));

    [Fact]
    public void Lru_cache_respects_byte_budget()
    {
        var lru = new LruCache<int, byte[]>(100, b => b.Length);
        for (var i = 0; i < 10; i++) lru.Add(i, new byte[30]);
        Assert.False(lru.TryGet(0, out _));
        Assert.True(lru.TryGet(9, out _));
        Assert.True(lru.TryGet(7, out _));
        Assert.False(lru.TryGet(6, out _));
    }
}
