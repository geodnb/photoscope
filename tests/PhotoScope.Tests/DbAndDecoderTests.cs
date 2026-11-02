using System.Windows.Media.Imaging;
using PhotoScope.Models;
using PhotoScope.Services;

namespace PhotoScope.Tests;

public sealed class DbAndDecoderTests : IDisposable
{
    readonly TempDir _tmp = new();
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _tmp.Dispose();
    }

    static Photo P(string path, string hash) => new()
    {
        Path = path, Hash = hash, SizeBytes = 10, ModifiedUtc = DateTime.UtcNow, DateImportedUtc = DateTime.UtcNow,
    };

    [Fact]
    public void Playlists_keep_order_and_follow_photo_removal()
    {
        using var db = new LibraryDb(_tmp.Sub("lib.db"));
        var photos = new[] { P(@"C:\x\1.jpg", "aa"), P(@"C:\x\2.jpg", "bb"), P(@"C:\x\3.jpg", "cc") };
        db.InsertPhotos(photos);
        Assert.All(photos, p => Assert.True(p.Id > 0));

        var pl = db.CreatePlaylist("Trip");
        db.SavePlaylistItems(pl, [photos[2].Id, photos[0].Id, photos[1].Id]);
        Assert.Equal([photos[2].Id, photos[0].Id, photos[1].Id], db.LoadPlaylistPhotoIds(pl));

        db.DeletePhotos([photos[0].Id]);
        Assert.Equal([photos[2].Id, photos[1].Id], db.LoadPlaylistPhotoIds(pl));

        db.RenamePlaylist(pl, "Trip 2");
        Assert.Equal("Trip 2", db.LoadPlaylists().Single().Name);
        db.DeletePlaylist(pl);
        Assert.Empty(db.LoadPlaylists());
        Assert.Equal(2, db.LoadPhotos().Count);
    }

    [Fact]
    public void Duplicate_paths_are_ignored_case_insensitively()
    {
        using var db = new LibraryDb(_tmp.Sub("lib.db"));
        db.InsertPhotos([P(@"C:\x\1.jpg", "aa")]);
        var dup = P(@"c:\X\1.JPG", "aa");
        db.InsertPhotos([dup]);
        Assert.Equal(0, dup.Id);
        Assert.Single(db.LoadPhotos());
    }

    [Fact]
    public void Flags_round_trip()
    {
        using var db = new LibraryDb(_tmp.Sub("lib.db"));
        var p = P(@"C:\x\1.jpg", "aa");
        db.InsertPhotos([p]);
        db.SetFavourite([p.Id], true);
        db.SetRating([p.Id], 4);
        db.SetRotation(p.Id, -90);
        db.SetTags(p.Id, "beach, sunset");
        var back = db.LoadPhotos().Single();
        Assert.True(back.IsFavourite);
        Assert.Equal(4, back.Rating);
        Assert.Equal(270, back.Rotation);
        Assert.Equal("beach, sunset", back.Tags);
    }

    [Theory]
    [InlineData(1, 80, 40)]
    [InlineData(3, 80, 40)]
    [InlineData(6, 40, 80)]
    [InlineData(8, 40, 80)]
    [InlineData(5, 40, 80)]
    public void Orientation_is_applied_on_decode(int orientation, int w, int h)
    {
        var path = TestImages.Jpeg(_tmp.Sub($"o{orientation}.jpg"), w: 80, h: 40, orientation: orientation);
        var bmp = ImageDecoder.Decode(path, 0);
        Assert.Equal(w, bmp.PixelWidth);
        Assert.Equal(h, bmp.PixelHeight);
        Assert.True(bmp.IsFrozen);
    }

    [Fact]
    public void Decode_scales_to_requested_size()
    {
        var path = TestImages.Jpeg(_tmp.Sub("big.jpg"), w: 1200, h: 600);
        var bmp = ImageDecoder.Decode(path, 300, 1200, 600, 1);
        Assert.Equal(300, bmp.PixelWidth);
    }

    [Fact]
    public void Corrupt_file_gives_friendly_message()
    {
        var path = _tmp.Sub("bad.jpg");
        File.WriteAllBytes(path, [0xFF, 0xD8, 1, 2, 3, 4, 5]);
        var ex = Record.Exception(() => ImageDecoder.ReadInfo(path));
        Assert.NotNull(ex);
        var msg = Friendly.Describe(ex!);
        Assert.DoesNotContain("   at ", msg);
        Assert.False(string.IsNullOrWhiteSpace(msg));
    }

    [Theory]
    [InlineData("2021:07:04 10:11:12", 2021, 7, 4)]
    [InlineData("2021-07-04T10:11:12+02:00", 2021, 7, 4)]
    [InlineData("0000:00:00 00:00:00", 0, 0, 0)]
    [InlineData("garbage", 0, 0, 0)]
    public void Parses_exif_dates(string raw, int y, int m, int d)
    {
        var ok = ImageDecoder.TryParseDate(raw, out var dt);
        Assert.Equal(y != 0, ok);
        if (ok) Assert.Equal(new DateTime(y, m, d), dt.Date);
    }

    [Fact]
    public void Icon_uses_largest_frame()
    {
        // Build a two-size .ico (16 and 256 px PNG entries, as Windows itself writes them).
        var path = _tmp.Sub("app.ico");
        var frames = new[] { 16, 256 }.Select(s =>
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(TestImages.Make(s, s, s), System.Windows.Media.PixelFormats.Bgra32, null, 0)));
            using var ms = new MemoryStream();
            enc.Save(ms);
            return (Size: s, Data: ms.ToArray());
        }).ToList();
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)frames.Count);
            var offset = 6 + 16 * frames.Count;
            foreach (var f in frames)
            {
                w.Write((byte)(f.Size >= 256 ? 0 : f.Size)); w.Write((byte)(f.Size >= 256 ? 0 : f.Size)); w.Write((byte)0); w.Write((byte)0);
                w.Write((ushort)1); w.Write((ushort)32); w.Write((uint)f.Data.Length); w.Write((uint)offset);
                offset += f.Data.Length;
            }
            foreach (var f in frames) w.Write(f.Data);
        }
        Assert.Equal(256, ImageDecoder.ReadInfo(path).PixelWidth);
        Assert.Equal(256, ImageDecoder.Decode(path, 0).PixelWidth);
        Assert.Equal(64, ImageDecoder.Decode(path, 64).PixelWidth);
    }
}
