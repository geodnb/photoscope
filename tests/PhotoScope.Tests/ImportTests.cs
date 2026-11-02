using System.Windows.Media.Imaging;
using PhotoScope.Models;
using PhotoScope.Services;

namespace PhotoScope.Tests;

public sealed class ImportTests : IDisposable
{
    readonly TempDir _tmp = new();
    readonly LibraryDb _db;
    readonly ThumbnailCache _thumbs;

    public ImportTests()
    {
        AppPaths.UseRoot(_tmp.Sub("appdata"));
        AppPaths.EnsureCreated();
        _db = new LibraryDb(AppPaths.Database);
        _thumbs = new ThumbnailCache(AppPaths.Thumbs);
    }

    public void Dispose()
    {
        _db.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _tmp.Dispose();
    }

    sealed class SyncProgress : IProgress<ImportProgress>
    {
        public ImportProgress? Last;
        public void Report(ImportProgress value) => Last = value;
    }

    async Task<(ImportResult Result, List<Photo> Photos)> Import(ImportOptions o)
    {
        var photos = new List<Photo>();
        var result = await new ImportService(_db, _thumbs).RunAsync(o, new SyncProgress(), b => { lock (photos) photos.AddRange(b); }, CancellationToken.None);
        return (result, photos);
    }

    string MakeLibrary()
    {
        var src = _tmp.Sub("src");
        TestImages.Jpeg(Path.Combine(src, "a.jpg"), seed: 1, dateTaken: "2021:07:04 10:11:12", make: "Apple", model: "iPhone 13 Pro");
        TestImages.Jpeg(Path.Combine(src, "b.jpg"), seed: 2);
        TestImages.Jpeg(Path.Combine(src, "rotated.jpg"), w: 80, h: 40, seed: 3, orientation: 6);
        TestImages.Png(Path.Combine(src, "sub", "c.png"), seed: 4);
        TestImages.Encoded(new BmpBitmapEncoder(), Path.Combine(src, "sub", "d.bmp"), seed: 5);
        TestImages.Encoded(new GifBitmapEncoder(), Path.Combine(src, "sub", "e.gif"), seed: 6);
        TestImages.Encoded(new TiffBitmapEncoder(), Path.Combine(src, "sub", "deeper", "f.tif"), seed: 7);
        File.Copy(Path.Combine(src, "a.jpg"), Path.Combine(src, "sub", "a-copy.jpg"));
        File.WriteAllBytes(Path.Combine(src, "corrupt.jpg"), Enumerable.Range(0, 3000).Select(i => (byte)(i * 7)).ToArray());
        File.WriteAllBytes(Path.Combine(src, "empty.png"), []);
        File.WriteAllText(Path.Combine(src, "readme.txt"), "not an image");
        return src;
    }

    [Fact]
    public async Task Reference_import_reads_metadata_skips_duplicates_and_survives_bad_files()
    {
        var src = MakeLibrary();
        var before = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToDictionary(f => f, TestImages.Sha);

        var (r, photos) = await Import(new ImportOptions { Sources = [src], Recursive = true, SkipDuplicates = true });

        Assert.Equal(10, r.Found);                 // 10 image-extension files, .txt ignored
        Assert.Equal(1, r.Duplicates);             // a-copy.jpg
        Assert.Equal(1, r.Failed);                 // empty.png
        Assert.Equal(8, r.Imported);               // includes corrupt.jpg, flagged with a decode error
        Assert.Equal(8, photos.Count);

        var a = photos.Single(p => p.FileName == "a.jpg");
        Assert.Equal(new DateTime(2021, 7, 4, 10, 11, 12), a.DateTaken);
        Assert.Equal("Apple iPhone 13 Pro", a.CameraModel);
        Assert.True(_thumbs.Exists(a));

        var rotated = photos.Single(p => p.FileName == "rotated.jpg");
        Assert.Equal(6, rotated.Orientation);
        Assert.Equal(40, rotated.OrientedWidth);
        Assert.Equal(80, rotated.OrientedHeight);

        var corrupt = photos.Single(p => p.FileName == "corrupt.jpg");
        Assert.NotNull(corrupt.DecodeError);
        Assert.Null(photos.Single(p => p.FileName == "f.tif").DecodeError);

        // Originals untouched, nothing written next to them.
        var after = Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories).ToDictionary(f => f, TestImages.Sha);
        Assert.Equal(before, after);

        // Second import: everything is already there.
        var (r2, _) = await Import(new ImportOptions { Sources = [src], Recursive = true, SkipDuplicates = true });
        Assert.Equal(0, r2.Imported);
        Assert.Equal(8, r2.AlreadyInLibrary);
    }

    [Fact]
    public async Task Non_recursive_import_only_takes_top_level()
    {
        var src = MakeLibrary();
        var (r, _) = await Import(new ImportOptions { Sources = [src], Recursive = false });
        Assert.Equal(5, r.Found); // a, b, rotated, corrupt, empty
    }

    [Fact]
    public async Task Copy_import_organises_by_year_month_and_never_overwrites()
    {
        var src = MakeLibrary();
        var lib = _tmp.Sub("library");
        // A file already sitting where a.jpg would go must not be overwritten.
        Directory.CreateDirectory(Path.Combine(lib, "2021", "07"));
        File.WriteAllText(Path.Combine(lib, "2021", "07", "a.jpg"), "existing file");

        var (r, photos) = await Import(new ImportOptions
        {
            Sources = [src], Recursive = true, Mode = ImportMode.CopyToLibrary, LibraryFolder = lib, SkipDuplicates = true,
        });

        Assert.Equal(8, r.Imported);
        Assert.Equal("existing file", File.ReadAllText(Path.Combine(lib, "2021", "07", "a.jpg")));
        var a = photos.Single(p => p.FileName.StartsWith("a"));
        Assert.Equal(Path.Combine(lib, "2021", "07", "a (2).jpg"), a.Path);
        Assert.All(photos, p => Assert.StartsWith(lib, p.Path));
        Assert.True(File.Exists(Path.Combine(src, "a.jpg")));    // original still there
    }

    [Fact]
    public async Task Cancelled_import_keeps_what_was_done()
    {
        var src = _tmp.Sub("many");
        for (var i = 0; i < 60; i++) TestImages.Jpeg(Path.Combine(src, $"p{i}.jpg"), seed: 100 + i);
        using var cts = new CancellationTokenSource();
        var photos = new List<Photo>();
        var task = new ImportService(_db, _thumbs).RunAsync(new ImportOptions { Sources = [src] }, new SyncProgress(),
            b => { lock (photos) photos.AddRange(b); }, cts.Token);
        cts.CancelAfter(50);
        var r = await task;
        Assert.True(r.Cancelled || r.Imported == 60);
        Assert.Equal(r.Imported, _db.LoadPhotos().Count);
    }
}
