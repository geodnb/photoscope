using System.Windows.Threading;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.ViewModels;

namespace PhotoScope.Tests;

/// <summary>Exercises the main view model's library/playlist logic without any windows.</summary>
[Collection("ThumbnailLoader")]
public sealed class ViewModelTests : IDisposable
{
    readonly TempDir _tmp = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _tmp.Dispose();
    }

    static void RunSta(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        t.Join();
        if (error != null) throw new Exception("STA test failed", error);
    }

    (MainViewModel vm, LibraryDb db, List<Photo> photos) Build(int count)
    {
        var db = new LibraryDb(_tmp.Sub("lib.db"));
        var photos = Enumerable.Range(0, count).Select(i => new Photo
        {
            Path = _tmp.Sub($"p{i}.jpg"), Hash = $"{i:x16}", SizeBytes = 100 + i,
            ModifiedUtc = DateTime.UtcNow, DateImportedUtc = DateTime.UtcNow, DateTaken = new DateTime(2020, 1, 1).AddDays(i),
        }).ToList();
        db.InsertPhotos(photos);
        var thumbs = new ThumbnailCache(_tmp.Sub("thumbs"));
        ThumbnailLoader.Initialize(thumbs, Dispatcher.CurrentDispatcher);
        var vm = new MainViewModel(db, thumbs, new AppSettings());
        // Load synchronously for the test.
        vm.InitializeAsync().GetAwaiter().GetResult();
        return (vm, db, vm.AllPhotos.OrderBy(p => p.Id).ToList());
    }

    [Fact]
    public void Playlist_add_dedupes_reorders_and_removes() => RunSta(() =>
    {
        var (vm, db, photos) = Build(6);
        using var _ = db;
        var pl = new Playlist { Id = db.CreatePlaylist("Trip"), Name = "Trip" };
        vm.Playlists.Add(pl);
        vm.SelectedPlaylist = pl;

        Assert.Equal(3, vm.AddToPlaylist(pl, [photos[0], photos[1], photos[2]]));
        Assert.Equal(1, vm.AddToPlaylist(pl, [photos[1], photos[3]]));        // photo 1 already there
        Assert.Equal(1, vm.AddToPlaylist(pl, [photos[4]], index: 0));          // insert at the front
        Assert.Equal([4, 0, 1, 2, 3], vm.PlaylistItems.Select(p => photos.IndexOf(p)));

        vm.MovePlaylistItems([photos[4]], 5);                                  // to the end
        Assert.Equal([0, 1, 2, 3, 4], vm.PlaylistItems.Select(p => photos.IndexOf(p)));
        vm.MovePlaylistItems([photos[2], photos[3]], 0);                       // block to the front, order kept
        Assert.Equal([2, 3, 0, 1, 4], vm.PlaylistItems.Select(p => photos.IndexOf(p)));

        vm.RemoveFromPlaylistCommand.Execute(new List<Photo> { photos[3] });
        Assert.Equal([2, 0, 1, 4], vm.PlaylistItems.Select(p => photos.IndexOf(p)));
        Assert.Equal(vm.PlaylistItems.Select(p => p.Id), db.LoadPlaylistPhotoIds(pl.Id));
        Assert.Equal(4, pl.Count);
    });

    [Fact]
    public void Sort_search_and_favourites_filter() => RunSta(() =>
    {
        var (vm, db, photos) = Build(5);
        using var _ = db;
        vm.SortBy = SortField.DateTaken;
        vm.SortDescending = false;
        Assert.Equal(photos.Select(p => p.Id), vm.Photos.Select(p => p.Id));
        vm.SortDescending = true;
        Assert.Equal(photos.Select(p => p.Id).Reverse(), vm.Photos.Select(p => p.Id));

        vm.SetTags(photos[2], "beach,  Sunset ,beach");
        Assert.Equal("beach, Sunset", photos[2].Tags);
        vm.SearchText = "sunset";
        vm.RefreshView();
        Assert.Single(vm.Photos);

        vm.SearchText = "";
        vm.ToggleFavourite([photos[0], photos[1]]);
        vm.SelectedSection = vm.Sections.First(s => s.Section == LibrarySection.Favourites);
        Assert.Equal(2, vm.Photos.Count);
        vm.ToggleFavourite([photos[0]]);
        Assert.Single(vm.Photos);                                             // un-favourited photo leaves the view
        Assert.False(db.LoadPhotos().Single(p => p.Id == photos[0].Id).IsFavourite);
    });
}
