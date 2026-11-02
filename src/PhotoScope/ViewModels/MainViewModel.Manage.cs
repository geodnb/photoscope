using System.Collections.Concurrent;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.Views;

namespace PhotoScope.ViewModels;

/// <summary>
/// Actions that touch files on disk: rename, move, copy, Recycle Bin, relocate. Every one of them
/// asks first (or is explicitly requested through a dialog), never overwrites, and never deletes
/// permanently.
/// </summary>
public sealed partial class MainViewModel
{
    [ObservableProperty] private bool _isBusy;

    bool EnsureNotBusy()
    {
        if (!IsBusy) return true;
        Dialogs.Info("Please wait", "PhotoScope is still finishing the previous file operation.");
        return false;
    }

    static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { tcs.SetResult(work()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        { IsBackground = true, Name = "Recycle Bin worker" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------ delete / remove

    [RelayCommand]
    async Task DeleteSelected() => await DeleteInteractiveAsync(SelectedPhotos.ToList());

    /// <summary>
    /// Asks whether to remove photos from the library or send the files to the Recycle Bin.
    /// Returns the photos that left the library.
    /// </summary>
    public async Task<IReadOnlyCollection<Photo>> DeleteInteractiveAsync(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0 || !EnsureNotBusy()) return [];
        var n = photos.Count;
        var what = n == 1 ? $"“{photos[0].FileName}”" : $"these {n:N0} photos";
        var allMissing = photos.All(p => p.IsMissing);

        var buttons = new List<DialogButton> { new("library", "Remove from library") };
        if (!allMissing) buttons.Add(new DialogButton("recycle", "Move to Recycle Bin"));
        buttons.Add(new DialogButton("cancel", "Cancel", IsCancel: true, IsDefault: true));

        var message = allMissing
            ? $"Remove {what} from your PhotoScope library? The files can't be found, so there is nothing to delete on disk."
            : $"What would you like to do with {what}?\n\n" +
              "• Remove from library: PhotoScope forgets them. The files stay exactly where they are.\n" +
              "• Move to Recycle Bin: the files are moved to the Windows Recycle Bin, where you can restore them.";
        var choice = Dialogs.Show(n == 1 ? "Delete photo" : $"Delete {n:N0} photos", message, buttons.ToArray());

        if (choice == "library") return RemoveFromLibraryCore(photos);
        if (choice == "recycle") return await RecycleAsync(photos);
        return [];
    }

    async Task<IReadOnlyCollection<Photo>> RecycleAsync(IReadOnlyList<Photo> photos)
    {
        IsBusy = true;
        ShowBanner("busy", BannerKind.Info, photos.Count == 1 ? "Moving 1 file to the Recycle Bin…" : $"Moving {photos.Count:N0} files to the Recycle Bin…");
        var items = photos.Select(p => (Photo: p, p.Path)).ToList();
        try
        {
            var (done, failed, cancelled) = await RunOnStaThread(() =>
            {
                var ok = new List<Photo>();
                var errors = new List<string>();
                foreach (var (photo, path) in items)
                {
                    if (photo.IsMissing || !File.Exists(path))
                    {
                        errors.Add($"{Path.GetFileName(path)}: the file can't be found.");
                        continue;
                    }
                    try
                    {
                        FileOps.SendToRecycleBin(path);
                        if (File.Exists(path)) errors.Add($"{Path.GetFileName(path)}: Windows didn't move it.");
                        else ok.Add(photo);
                    }
                    catch (OperationCanceledException)
                    {
                        return (ok, errors, true);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"Recycle failed for {path}", ex);
                        errors.Add($"{Path.GetFileName(path)}: {(ex is UserFacingException ? ex.Message : Friendly.Describe(ex))}");
                    }
                }
                return (ok, errors, false);
            });

            foreach (var p in done) Log.Info($"Sent to Recycle Bin: {p.Path}");
            if (done.Count > 0 && TryDb(() => Db.DeletePhotos(done.Select(p => p.Id)), "the library"))
                ForgetPhotos(done);

            CloseBanner("busy");
            if (failed.Count > 0)
            {
                Dialogs.Info("Some files weren't deleted",
                    string.Join("\n", failed.Take(15)) + (failed.Count > 15 ? $"\n…and {failed.Count - 15} more." : "") +
                    "\n\nThose files were left untouched.");
            }
            if (done.Count > 0)
            {
                ShowBanner("delete", BannerKind.Success,
                    (done.Count == 1 ? "Moved 1 file" : $"Moved {done.Count:N0} files") + " to the Recycle Bin." + (cancelled ? " Stopped when Windows' prompt was cancelled." : ""));
            }
            return done;
        }
        finally
        {
            CloseBanner("busy");
            IsBusy = false;
        }
    }

    [RelayCommand]
    void RemoveSelectedFromLibrary() => RemoveFromLibrary(SelectedPhotos.ToList());

    void RemoveFromLibrary(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0) return;
        var n = photos.Count;
        if (!Dialogs.Confirm(n == 1 ? "Remove from library?" : $"Remove {n:N0} photos from the library?",
                (n == 1 ? $"“{photos[0].FileName}” will be removed" : "They will be removed") +
                " from PhotoScope, including any playlists, ratings and tags. The files themselves are not touched.",
                "Remove from library"))
            return;
        RemoveFromLibraryCore(photos);
    }

    IReadOnlyCollection<Photo> RemoveFromLibraryCore(IReadOnlyList<Photo> photos)
    {
        if (!TryDb(() => Db.DeletePhotos(photos.Select(p => p.Id)), "the library")) return [];
        Log.Info($"Removed {photos.Count} photos from the library (files untouched)");
        ForgetPhotos(photos.ToList());
        return photos.ToList();
    }

    // ------------------------------------------------------------------ rename / move / copy

    [RelayCommand]
    void RenameSelected()
    {
        if (SelectedPhotos.Count != 1 || !EnsureNotBusy()) return;
        var p = SelectedPhotos[0];
        if (p.IsMissing || !File.Exists(p.Path))
        {
            Dialogs.Info("Can't rename", "The file can't be found. Relocate it first.");
            return;
        }
        var ext = Path.GetExtension(p.FileName);
        var folder = p.Folder;
        string? Validate(string name)
        {
            name = name.Trim();
            if (FileOps.ValidateFileName(name) is { } error) return error;
            if (!string.Equals(Path.GetExtension(name), ext, StringComparison.OrdinalIgnoreCase))
                return $"Keep the {ext} extension so Windows still recognises the file.";
            var dest = Path.Combine(folder, name);
            var caseOnly = string.Equals(dest, p.Path, StringComparison.OrdinalIgnoreCase);
            if (!caseOnly && (File.Exists(dest) || Directory.Exists(dest))) return "A file with that name already exists in this folder.";
            if (!caseOnly && Db.PathExists(dest)) return "Another library entry already uses that name.";
            return null;
        }

        var newName = Dialogs.Prompt("Rename file", $"Renames the file on disk in {folder}.", p.FileName, Validate, "Rename",
            Path.GetFileNameWithoutExtension(p.FileName).Length);
        if (newName == null || newName.Trim() == p.FileName) return;

        try
        {
            var oldPath = p.Path;
            var dest = FileOps.Rename(oldPath, newName.Trim());
            if (!TryDb(() => Db.SetPath(p.Id, dest), "the new name"))
            {
                // Keep disk and library consistent: undo the rename.
                try { File.Move(dest, oldPath, overwrite: false); } catch (Exception ex) { Log.Error("Could not undo rename", ex); }
                return;
            }
            Log.Info($"Renamed {oldPath} -> {dest}");
            p.Path = dest;
            Reposition(p);
        }
        catch (Exception ex)
        {
            Log.Warn($"Rename failed for {p.Path}", ex);
            Dialogs.Error("Couldn't rename the file", ex is UserFacingException ? ex.Message : Friendly.Describe(ex));
        }
    }

    [RelayCommand]
    async Task MoveSelected() => await MoveOrCopyAsync(SelectedPhotos.ToList(), move: true);

    [RelayCommand]
    async Task CopySelected() => await MoveOrCopyAsync(SelectedPhotos.ToList(), move: false);

    async Task MoveOrCopyAsync(IReadOnlyList<Photo> photos, bool move)
    {
        if (photos.Count == 0 || !EnsureNotBusy()) return;
        var candidates = photos.Where(p => !p.IsMissing).Select(p => (Photo: p, p.Path)).ToList();
        var present = (await Task.Run(() => candidates.Where(c => File.Exists(c.Path)).ToList())).Select(c => c.Photo).ToList();
        if (present.Count == 0)
        {
            Dialogs.Info("Nothing to do", "The selected files can't be found.");
            return;
        }

        var dlg = new OpenFolderDialog { Title = move ? "Move photos to…" : "Copy photos to…" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var target = dlg.FolderName;

        var n = present.Count;
        if (move && !Dialogs.Confirm(n == 1 ? "Move file?" : $"Move {n:N0} files?",
                $"Move {(n == 1 ? $"“{present[0].FileName}”" : $"{n:N0} files")} to {target}? " +
                "Files with the same name already there are kept; the moved file gets a name like “photo (2).jpg”.",
                "Move"))
            return;

        IsBusy = true;
        ShowBanner("busy", BannerKind.Info, $"{(move ? "Moving" : "Copying")} {n:N0} file{(n == 1 ? "" : "s")}…");
        try
        {
            var items = present.Select(p => (Photo: p, p.Path)).ToList();
            var results = await Task.Run(() =>
            {
                var done = new List<(Photo Photo, string Old, string New)>();
                var errors = new List<string>();
                foreach (var (photo, path) in items)
                {
                    try
                    {
                        if (!move)
                        {
                            done.Add((photo, path, FileOps.CopyNoOverwrite(path, target)));
                            continue;
                        }
                        // Never pick a name another library entry (e.g. a missing photo) still owns.
                        var dest = FileOps.MoveNoOverwrite(path, target, Db.PathExists);
                        if (string.Equals(dest, path, StringComparison.OrdinalIgnoreCase)) continue;
                        try
                        {
                            Db.SetPath(photo.Id, dest); // update the library straight away, file by file
                            done.Add((photo, path, dest));
                        }
                        catch (Exception dbEx)
                        {
                            Log.Error($"Library update failed after moving {path}; moving it back", dbEx);
                            try { File.Move(dest, path, overwrite: false); }
                            catch (Exception undoEx) { Log.Error($"Could not move {dest} back", undoEx); }
                            errors.Add($"{Path.GetFileName(path)}: the library couldn't be updated, so the file was left where it was.");
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"{(move ? "Move" : "Copy")} failed for {path}", ex);
                        errors.Add($"{Path.GetFileName(path)}: {(ex is UserFacingException ? ex.Message : Friendly.Describe(ex))}");
                    }
                }
                return (done, errors);
            });

            if (move)
            {
                foreach (var (photo, oldPath, newPath) in results.done)
                {
                    photo.Path = newPath;
                    Log.Info($"Moved {oldPath} -> {newPath}");
                }
                if (SortBy == SortField.Name) RefreshView();
            }

            CloseBanner("busy");
            var count = results.done.Count;
            ShowBanner("move", results.errors.Count > 0 ? BannerKind.Warning : BannerKind.Success,
                $"{(move ? "Moved" : "Copied")} {count:N0} file{(count == 1 ? "" : "s")} to {target}." +
                (results.errors.Count > 0 ? $" {results.errors.Count:N0} couldn't be {(move ? "moved" : "copied")}." : "") +
                (!move ? " Copies aren't added to the library." : ""),
                results.errors.Count > 0
                    ? [new BannerAction("Details", () => Dialogs.Info("Problems", string.Join("\n", results.errors.Take(20))))]
                    : [new BannerAction("Open folder", () => SafeRun(() => FileOps.OpenFolder(target)))]);
        }
        finally
        {
            CloseBanner("busy");
            IsBusy = false;
        }
    }

    // ------------------------------------------------------------------ explorer

    static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex)
        {
            Log.Warn("Explorer action failed", ex);
            Dialogs.Error("Couldn't open Explorer", ex is UserFacingException ? ex.Message : Friendly.Describe(ex));
        }
    }

    [RelayCommand]
    void OpenContainingFolder()
    {
        if (SelectedPhoto is { } p) SafeRun(() => FileOps.OpenFolder(p.Folder));
    }

    [RelayCommand]
    void ShowInExplorer()
    {
        if (SelectedPhoto is { } p) SafeRun(() => FileOps.ShowInExplorer(p.Path));
    }

    // ------------------------------------------------------------------ tags

    [RelayCommand]
    void EditTagsSelected()
    {
        var photos = SelectedPhotos.ToList();
        if (photos.Count == 0) return;
        if (photos.Count == 1)
        {
            var tags = Dialogs.Prompt("Edit tags", "Separate tags with commas.", photos[0].Tags, _ => null, "Save");
            if (tags != null) SetTags(photos[0], tags);
            return;
        }
        var add = Dialogs.Prompt($"Add tags to {photos.Count:N0} photos", "Separate tags with commas. Existing tags are kept.", "", _ => null, "Add");
        if (string.IsNullOrWhiteSpace(add)) return;
        AddTags(photos, add);
    }

    // ------------------------------------------------------------------ relocate missing files

    [RelayCommand]
    void RelocateSelected() => RelocateMissing(SelectedPhotos.Where(p => p.IsMissing).ToList());

    void RelocateMissing(IReadOnlyList<Photo> photos)
    {
        if (photos.Count == 0 || !EnsureNotBusy()) return;
        if (photos.Count == 1) _ = RelocateOneAsync(photos[0]);
        else _ = RelocateManyAsync(photos);
    }

    async Task RelocateOneAsync(Photo p)
    {
        var dlg = new OpenFileDialog
        {
            Title = $"Find {p.FileName}",
            FileName = p.FileName,
            Filter = $"{p.FileName}|{p.FileName}|Images|{string.Join(";", ImageDecoder.Extensions.Select(x => "*" + x))}",
        };
        var folder = p.Folder;
        while (!string.IsNullOrEmpty(folder) && !Directory.Exists(folder)) folder = Path.GetDirectoryName(folder);
        if (!string.IsNullOrEmpty(folder)) dlg.InitialDirectory = folder;
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;

        var path = dlg.FileName;
        if (!string.Equals(path, p.Path, StringComparison.OrdinalIgnoreCase) && Db.PathExists(path))
        {
            Dialogs.Info("Already in the library", "That file is already in your library as a separate photo.");
            return;
        }
        Relocation found;
        IsBusy = true;
        try { found = await Task.Run(() => ReadRelocation(path, null)); }
        catch (Exception ex)
        {
            Dialogs.Error("Couldn't read the file", Friendly.Describe(ex));
            return;
        }
        finally { IsBusy = false; }
        if (!string.Equals(found.Hash, p.Hash, StringComparison.OrdinalIgnoreCase) &&
            !Dialogs.Confirm("Different file?", "The file you chose doesn't have the same contents as the original. Use it for this photo anyway?", "Use this file"))
            return;
        ApplyRelocations([(p, found)]);
    }

    sealed record Relocation(string Path, string Hash, long Size, DateTime ModifiedUtc, ImageInfo? Info);

    static Relocation ReadRelocation(string path, string? knownHash)
    {
        var fi = new FileInfo(path);
        ImageInfo? info = null;
        try { info = ImageDecoder.ReadInfo(path); } catch { }
        return new Relocation(path, knownHash ?? ImportService.HashFileForCompare(path), fi.Length, fi.LastWriteTimeUtc, info);
    }

    async Task RelocateManyAsync(IReadOnlyList<Photo> photos)
    {
        var dlg = new OpenFolderDialog { Title = $"Choose a folder to search for {photos.Count:N0} missing photos" };
        if (dlg.ShowDialog(Application.Current.MainWindow) != true) return;
        var root = dlg.FolderName;

        IsBusy = true;
        ShowBanner("busy", BannerKind.Info, $"Searching {root} for {photos.Count:N0} missing photos…");
        try
        {
            var wanted = photos.Select(p => (Photo: p, Name: p.FileName, Size: p.SizeBytes, p.Hash)).ToList();
            var matches = await Task.Run(() =>
            {
                var byName = wanted.GroupBy(w => w.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                var found = new ConcurrentDictionary<Photo, string>();
                var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
                foreach (var file in Directory.EnumerateFiles(root, "*", options).Where(f => !AppPaths.IsUnder(f, AppPaths.Root)))
                {
                    if (!byName.TryGetValue(Path.GetFileName(file), out var candidates)) continue;
                    long size;
                    try { size = new FileInfo(file).Length; } catch { continue; }
                    foreach (var c in candidates.Where(c => c.Size == size && !found.ContainsKey(c.Photo)))
                    {
                        try
                        {
                            if (string.Equals(ImportService.HashFileForCompare(file), c.Hash, StringComparison.OrdinalIgnoreCase))
                            {
                                found[c.Photo] = file;
                                break;
                            }
                        }
                        catch (Exception ex) { Log.Warn($"Relocate: could not hash {file}", ex); }
                    }
                }
                return found.Where(kv => !Db.PathExists(kv.Value))
                            .Select(kv => (kv.Key, ReadRelocation(kv.Value, kv.Key.Hash))).ToList();
            });

            var relocated = ApplyRelocations(matches);
            CloseBanner("busy");
            Dialogs.Info("Relocate", relocated == photos.Count
                ? $"Found all {relocated:N0} photos."
                : $"Found {relocated:N0} of {photos.Count:N0} photos. The rest are still marked as missing.");
        }
        catch (Exception ex)
        {
            Log.Error("Relocate failed", ex);
            Dialogs.Error("Couldn't search that folder", Friendly.Describe(ex));
        }
        finally
        {
            CloseBanner("busy");
            IsBusy = false;
            UpdateCounts();
            UpdateMissingBanner();
            if (SelectedSection?.Section == LibrarySection.Missing) RefreshView();
        }
    }

    /// <summary>Saves relocations to the database first (one transaction), then updates the photos and refreshes once.</summary>
    int ApplyRelocations(IReadOnlyList<(Photo Photo, Relocation Found)> items)
    {
        if (items.Count == 0) return 0;
        var updated = items.Select(x => (x.Photo, Copy: WithRelocation(x.Photo, x.Found))).ToList();
        if (!TryDb(() => Db.UpdateFileDetailsMany(updated.Select(u => u.Copy)), "the new locations")) return 0;

        foreach (var (p, c) in updated)
        {
            p.Path = c.Path;
            p.Hash = c.Hash;
            p.SizeBytes = c.SizeBytes;
            p.ModifiedUtc = c.ModifiedUtc;
            p.PixelWidth = c.PixelWidth;
            p.PixelHeight = c.PixelHeight;
            p.Orientation = c.Orientation;
            p.DateTaken = c.DateTaken;
            p.CameraModel = c.CameraModel;
            p.DecodeError = c.DecodeError;
            p.IsMissing = false;
            p.NotifyAllChanged();
            p.ThumbVersion++;
            Log.Info($"Relocated photo {p.Id} to {p.Path}");
        }
        UpdateCounts();
        UpdateMissingBanner();
        if (SelectedSection?.Section == LibrarySection.Missing) RefreshView();
        if (SelectedPhoto is { } sel && updated.Any(u => u.Photo == sel)) _ = LoadPreviewAsync(sel);
        return updated.Count;
    }

    static Photo WithRelocation(Photo p, Relocation r) => new()
    {
        Id = p.Id,
        Path = r.Path,
        Hash = r.Hash,
        SizeBytes = r.Size,
        ModifiedUtc = r.ModifiedUtc,
        PixelWidth = r.Info?.PixelWidth ?? p.PixelWidth,
        PixelHeight = r.Info?.PixelHeight ?? p.PixelHeight,
        Orientation = r.Info?.Orientation ?? p.Orientation,
        DateTaken = r.Info?.DateTaken ?? p.DateTaken,
        CameraModel = r.Info?.CameraModel ?? p.CameraModel,
        DecodeError = r.Info != null ? null : p.DecodeError,
        IsMissing = false,
    };

    // ------------------------------------------------------------------ settings

    [RelayCommand]
    void OpenSettings()
    {
        var oldLimit = Settings.ThumbnailCacheLimitMB;
        var dlg = new SettingsWindow(this) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() != true) return;
        SettingsService.Save();
        ThemeService.Apply();
        if (Settings.ThumbnailCacheLimitMB < oldLimit)
        {
            var limit = (long)Settings.ThumbnailCacheLimitMB * 1024 * 1024;
            _ = Task.Run(() => Thumbs.Trim(limit));
        }
    }

    /// <summary>Deletes every cached thumbnail (only PhotoScope's own cache files) and reloads the visible ones.</summary>
    public async Task ClearThumbnailCacheAsync()
    {
        ThumbnailLoader.Instance.ClearMemory();
        await Task.Run(Thumbs.Clear);
        ThumbnailLoader.Instance.ClearMemory();
        foreach (var p in _all) p.ThumbVersion++;
        Log.Info("Thumbnail cache cleared");
    }
}
