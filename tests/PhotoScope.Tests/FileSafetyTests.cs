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
        Assert.True(FileOps.CanRecycle(Path.Combine(_tmp.Path, "x.jpg"), out _));
    }

    /// <summary>Puts one tiny file in the real Recycle Bin, so it only runs when asked.</summary>
    [Fact]
    public void Recycle_sends_file_to_recycle_bin()
    {
        if (Environment.GetEnvironmentVariable("PHOTOSCOPE_TEST_RECYCLE") != "1") return;
        var f = Path.Combine(_tmp.Path, "photoscope-recycle-test.txt");
        File.WriteAllText(f, "safe to empty");
        FileOps.SendToRecycleBin(f);
        Assert.False(File.Exists(f));
    }

    [Fact]
    public void Thumbnail_cache_never_deletes_outside_its_folder()
    {
        var cacheDir = _tmp.Sub("cache");
        var outside = Path.Combine(_tmp.Path, "keep.jpg");
        File.WriteAllText(outside, "keep");
        var cache = new ThumbnailCache(cacheDir);
        File.WriteAllText(Path.Combine(cacheDir, "junk.jpg"), "x");
        cache.Clear();
        cache.Trim(0);
        Assert.True(File.Exists(outside));
        Assert.False(File.Exists(Path.Combine(cacheDir, "junk.jpg")));

        // A malformed hash can't escape the cache folder.
        var p = new Photo { Id = 5, Hash = @"..\..\evil", Path = outside };
        Assert.StartsWith(cache.Folder, Path.GetFullPath(cache.PathFor(p)));
    }
}
