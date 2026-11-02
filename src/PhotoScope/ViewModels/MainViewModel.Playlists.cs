using System.Collections;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.Views;

namespace PhotoScope.ViewModels;

/// <summary>Playlists: ordered collections of library photos, stored only in the database.</summary>
public sealed partial class MainViewModel
{
    public ObservableCollection<Playlist> Playlists { get; } = new();

    /// <summary>Photos of the selected playlist, in order.</summary>
    public Controls.BulkObservableCollection<Photo> PlaylistItems { get; } = new();

    [ObservableProperty] private Playlist? _selectedPlaylist;
    [ObservableProperty] private string _playlistSummary = "";

    void InitPlaylists()
    {
        PlaylistItems.CollectionChanged += (_, _) => UpdatePlaylistSummary();
    }

    void LoadPlaylists()
    {
        Playlists.Clear();
        foreach (var p in Db.LoadPlaylists()) Playlists.Add(p);
        SelectedPlaylist = Playlists.FirstOrDefault(p => p.Id == Settings.LastPlaylistId) ?? Playlists.FirstOrDefault();
    }

    partial void OnSelectedPlaylistChanged(Playlist? value)
    {
        Settings.LastPlaylistId = value?.Id;
        LoadPlaylistItems();
    }

    void LoadPlaylistItems()
    {
        var pl = SelectedPlaylist;
        if (pl == null)
        {
            PlaylistItems.ReplaceAll([]);
            return;
        }
        try
        {
            var items = Db.LoadPlaylistPhotoIds(pl.Id).Select(id => _byId.GetValueOrDefault(id)).OfType<Photo>().ToList();
            PlaylistItems.ReplaceAll(items);
            pl.Count = PlaylistItems.Count;
        }
        catch (Exception ex)
        {
            Log.Error("Could not load playlist", ex);
            ShowBanner("playlist", BannerKind.Error, "The playlist couldn't be loaded. Details were written to the log.");
        }
    }

    void UpdatePlaylistSummary()
    {
        var n = PlaylistItems.Count;
        PlaylistSummary = SelectedPlaylist == null ? "" : n == 1 ? "1 photo" : $"{n:N0} photos";
    }

    static string? ValidatePlaylistName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.Trim().Length > 100) return "That name is too long.";
        return null;
    }

    [RelayCommand]
    void CreatePlaylist() => NewPlaylist();

    public Playlist? NewPlaylist()
    {
        var name = Dialogs.Prompt("New playlist", "Give the playlist a name.", "New playlist", ValidatePlaylistName, "Create");
        if (name == null) return null;
        name = name.Trim();
        Playlist? created = null;
        if (!TryDb(() => created = new Playlist { Id = Db.CreatePlaylist(name), Name = name }, "the playlist")) return null;
        InsertSorted(created!);
        SelectedPlaylist = created;
        IsPlaylistVisible = true;
        return created;
    }

    void InsertSorted(Playlist p)
    {
        var i = 0;
        while (i < Playlists.Count && string.Compare(Playlists[i].Name, p.Name, StringComparison.CurrentCultureIgnoreCase) <= 0) i++;
        Playlists.Insert(i, p);
    }

    [RelayCommand]
    void RenamePlaylist(Playlist? p)
    {
        p ??= SelectedPlaylist;
        if (p == null) return;
        var name = Dialogs.Prompt("Rename playlist", "", p.Name, ValidatePlaylistName, "Rename");
        if (name == null) return;
        name = name.Trim();
        if (name == p.Name) return;
        if (!TryDb(() => Db.RenamePlaylist(p.Id, name), "the playlist name")) return;
        p.Name = name;
        var selected = SelectedPlaylist;
        Playlists.Remove(p);
        InsertSorted(p);
        SelectedPlaylist = selected;
    }

    [RelayCommand]
    void DeletePlaylist(Playlist? p)
    {
        p ??= SelectedPlaylist;
        if (p == null) return;
        if (!Dialogs.Confirm("Delete playlist?",
                $"“{p.Name}” will be deleted. The photos in it stay in your library and on disk.", "Delete playlist"))
            return;
        if (!TryDb(() => Db.DeletePlaylist(p.Id), "the playlist")) return;
        var wasSelected = SelectedPlaylist == p;
        Playlists.Remove(p);
        if (wasSelected) SelectedPlaylist = Playlists.FirstOrDefault();
    }

    /// <summary>Adds photos to a playlist (skipping ones already in it), at <paramref name="index"/> or the end.</summary>
    public int AddToPlaylist(Playlist pl, IReadOnlyList<Photo> photos, int index = -1)
    {
        if (photos.Count == 0) return 0;
        var isShown = pl == SelectedPlaylist;
        List<long> ids;
        try
        {
            ids = isShown ? PlaylistItems.Select(p => p.Id).ToList() : Db.LoadPlaylistPhotoIds(pl.Id);
        }
        catch (Exception ex)
        {
            Log.Error("Could not read playlist", ex);
            return 0;
        }
        var existing = new HashSet<long>(ids);
        var toAdd = photos.Where(p => existing.Add(p.Id)).ToList();
        if (toAdd.Count == 0) return 0;

        if (index < 0 || index > ids.Count) index = ids.Count;
        ids.InsertRange(index, toAdd.Select(p => p.Id));
        if (!TryDb(() => Db.SavePlaylistItems(pl.Id, ids), "the playlist")) return 0;

        if (isShown)
        {
            if (toAdd.Count <= 20)
                for (var i = 0; i < toAdd.Count; i++) PlaylistItems.Insert(index + i, toAdd[i]);
            else
                PlaylistItems.ReplaceAll(ids.Select(id => _byId.GetValueOrDefault(id)).OfType<Photo>());
        }
        pl.Count = ids.Count;
        return toAdd.Count;
    }

    /// <summary>Moves playlist entries so they start at <paramref name="targetIndex"/> (an index in the current order).</summary>
    public void MovePlaylistItems(IReadOnlyList<Photo> items, int targetIndex)
    {
        var pl = SelectedPlaylist;
        if (pl == null || items.Count == 0) return;
        var set = items.ToHashSet();
        var moving = PlaylistItems.Where(set.Contains).ToList(); // keep their relative order
        var before = PlaylistItems.Take(Math.Clamp(targetIndex, 0, PlaylistItems.Count)).Count(p => !set.Contains(p));
        var rest = PlaylistItems.Where(p => !set.Contains(p)).ToList();
        rest.InsertRange(before, moving);
        if (rest.SequenceEqual(PlaylistItems)) return;
        if (!TryDb(() => Db.SavePlaylistItems(pl.Id, rest.Select(p => p.Id).ToList()), "the playlist order")) return;
        if (moving.Count == 1)
            PlaylistItems.Move(PlaylistItems.IndexOf(moving[0]), rest.IndexOf(moving[0]));
        else
            PlaylistItems.ReplaceAll(rest);
    }

    [RelayCommand]
    void RemoveFromPlaylist(IList? items)
    {
        var pl = SelectedPlaylist;
        if (pl == null || items == null || items.Count == 0) return;
        var remove = items.Cast<Photo>().ToHashSet();
        var remaining = PlaylistItems.Where(p => !remove.Contains(p)).Select(p => p.Id).ToList();
        if (!TryDb(() => Db.SavePlaylistItems(pl.Id, remaining), "the playlist")) return;
        PlaylistItems.RemoveAll(remove.Contains);
        pl.Count = PlaylistItems.Count;
    }

    [RelayCommand]
    void RemoveOneFromPlaylist(Photo? photo)
    {
        if (photo != null) RemoveFromPlaylist(new List<Photo> { photo });
    }

    [RelayCommand]
    void PlayPlaylist(Photo? start)
    {
        if (PlaylistItems.Count == 0) return;
        ShowViewer(PlaylistItems.ToList(), start ?? PlaylistItems[0], slideshow: true);
    }

    [RelayCommand]
    void AddSelectedToPlaylist(Playlist? pl)
    {
        if (SelectedPhotos.Count == 0) return;
        pl ??= NewPlaylist();
        if (pl == null) return;
        var added = AddToPlaylist(pl, SelectedPhotos);
        var skipped = SelectedPhotos.Count - added;
        ShowBanner("playlist", BannerKind.Success,
            (added == 1 ? "Added 1 photo" : $"Added {added:N0} photos") + $" to “{pl.Name}”."
            + (skipped > 0 ? $" {skipped:N0} already in it." : ""),
            new BannerAction("Show", () => { SelectedPlaylist = pl; IsPlaylistVisible = true; }));
    }

    /// <summary>Keeps the playlist view in step after photos leave the library (the database cascades).</summary>
    void RemoveFromLoadedPlaylist(HashSet<Photo> removed)
    {
        PlaylistItems.RemoveAll(removed.Contains);
        try
        {
            var counts = Db.LoadPlaylists().ToDictionary(p => p.Id, p => p.Count);
            foreach (var p in Playlists) p.Count = counts.GetValueOrDefault(p.Id);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not refresh playlist counts", ex);
        }
    }
}
