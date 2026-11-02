using CommunityToolkit.Mvvm.Input;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.Views;

namespace PhotoScope.ViewModels;

/// <summary>Library-only actions: favourite, rating, viewing rotation and tags. None of these touch the image files.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Runs a database write and reports failure in the UI instead of crashing.</summary>
    bool TryDb(Action action, string what)
    {
        try
        {
            action();
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Database update failed: {what}", ex);
            Dialogs.Error("Couldn't save the change", $"PhotoScope couldn't save {what}. Details were written to the log.");
            return false;
        }
    }

    public void ToggleFavourite(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0) return;
        var value = !photos.All(p => p.IsFavourite);
        if (!TryDb(() => Db.SetFavourite(photos.Select(p => p.Id), value), "the favourite")) return;
        foreach (var p in photos) p.IsFavourite = value;
        if (SelectedSection?.Section == LibrarySection.Favourites)
            foreach (var p in photos) Reposition(p);
        UpdateCounts();
    }

    public void SetRating(IReadOnlyList<Photo> photos, int rating)
    {
        if (photos.Count == 0) return;
        rating = Math.Clamp(rating, 0, 5);
        if (!TryDb(() => Db.SetRating(photos.Select(p => p.Id), rating), "the rating")) return;
        foreach (var p in photos) p.Rating = rating;
    }

    /// <summary>Changes the viewing rotation stored in the database. The image file is not modified.</summary>
    public void Rotate(IReadOnlyList<Photo> photos, int delta)
    {
        foreach (var p in photos)
        {
            var value = ((p.Rotation + delta) % 360 + 360) % 360;
            if (TryDb(() => Db.SetRotation(p.Id, value), "the rotation")) p.Rotation = value;
        }
    }

    public void SetTags(Photo p, string tags)
    {
        var normalized = Photo.NormalizeTags(tags);
        if (normalized == p.Tags) return;
        if (TryDb(() => Db.SetTags(p.Id, normalized), "the tags")) p.Tags = normalized;
    }

    [RelayCommand]
    void ToggleFavouriteSelected() => ToggleFavourite(SelectedPhotos);

    [RelayCommand]
    void SetRatingSelected(object? rating)
    {
        if (rating != null && int.TryParse(rating.ToString(), out var r)) SetRating(SelectedPhotos, r);
    }

    [RelayCommand]
    void RotateLeftSelected() => Rotate(SelectedPhotos, -90);

    [RelayCommand]
    void RotateRightSelected() => Rotate(SelectedPhotos, 90);
}
