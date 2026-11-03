using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.Views;

namespace PhotoScope.ViewModels;

/// <summary>Support for viewing files opened from Windows (which may or may not be in the library).</summary>
public sealed partial class MainViewModel
{
    readonly TaskCompletionSource _loaded = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the library has been loaded from the database (successfully or not).</summary>
    public Task LibraryLoaded => _loaded.Task;

    /// <summary>The library entry for a file path, if there is one.</summary>
    public Photo? FindByPath(string path)
    {
        foreach (var p in _all)
            if (string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    /// <summary>
    /// Adds one file to the library in place (no copying), as chosen from the viewer's
    /// "Add to library" button. Returns the new library photo, or the existing one if it's already there.
    /// </summary>
    public async Task<Photo?> AddFileToLibraryAsync(string path)
    {
        await LibraryLoaded;
        if (FindByPath(path) is { } existing) return existing;

        var added = new List<Photo>();
        ImportResult result;
        try
        {
            result = await _importer.RunAsync(new ImportOptions
            {
                Sources = [path],
                Recursive = false,
                Mode = ImportMode.ReferenceInPlace,
                SkipDuplicates = false, // the user asked for this file explicitly
            }, new Progress<ImportProgress>(), batch => { lock (added) added.AddRange(batch); }, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Log.Error($"Add to library failed for {path}", ex);
            Dialogs.Error("Couldn't add the photo", Friendly.Describe(ex));
            return null;
        }

        List<Photo> photos;
        lock (added) photos = added.ToList();
        if (photos.Count > 0) AddImported(photos);
        var photo = FindByPath(path);
        if (photo == null)
            Dialogs.Error("Couldn't add the photo", result.Errors.FirstOrDefault() ?? "PhotoScope couldn't add this file to the library.");
        return photo;
    }

    /// <summary>
    /// For a file that isn't in the library: asks, then sends it to the Recycle Bin with the same
    /// safeguards as library deletes. Returns true only if the file was recycled.
    /// </summary>
    public async Task<bool> RecycleLooseFileAsync(string path)
    {
        if (!EnsureNotBusy()) return false;
        var name = Path.GetFileName(path);
        var choice = Dialogs.Show("Delete photo",
            $"Move “{name}” to the Recycle Bin? You can restore it from there. (It isn't in your PhotoScope library.)",
            new DialogButton("recycle", "Move to Recycle Bin"),
            new DialogButton("cancel", "Cancel", IsCancel: true, IsDefault: true));
        if (choice != "recycle") return false;

        IsBusy = true;
        try
        {
            var error = await RunOnStaThread(() =>
            {
                try
                {
                    FileOps.SendToRecycleBin(path);
                    return null;
                }
                catch (Exception ex)
                {
                    Log.Warn($"Not recycled: {path}", ex);
                    return ex is UserFacingException ? ex.Message : Friendly.Describe(ex);
                }
            });
            if (error != null)
            {
                Dialogs.Info("The file wasn't deleted", $"{name}: {error}");
                return false;
            }
            Log.Info($"Sent to Recycle Bin: {path}");
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }
}
