using System.Collections;
using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoScope.Controls;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.Views;

namespace PhotoScope.ViewModels;

/// <summary>State and behaviour of the main window: library, gallery view, import and selection.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    const int RecentDays = 30;

    readonly ImportService _importer;
    readonly List<Photo> _all = new();
    readonly Dictionary<long, Photo> _byId = new();
    readonly DispatcherTimer _searchTimer;
    readonly Dispatcher _ui;
    CancellationTokenSource? _importCts;
    double _dpiScale = 1;
    bool _suppressRefresh;

    public MainViewModel(LibraryDb db, ThumbnailCache thumbs, AppSettings settings)
    {
        Db = db;
        Thumbs = thumbs;
        Settings = settings;
        _ui = Dispatcher.CurrentDispatcher;
        _importer = new ImportService(db, thumbs);

        Sections =
        [
            new NavEntry(LibrarySection.All, "All Photos", "\uE91B"),
            new NavEntry(LibrarySection.Favourites, "Favourites", "\uEB51"),
            new NavEntry(LibrarySection.Recent, "Recently Imported", "\uE823"),
            new NavEntry(LibrarySection.Missing, "Missing Files", "\uE7BA") { IsVisible = false },
        ];

        _suppressRefresh = true;
        _sortBy = settings.SortBy;
        _sortDescending = settings.SortDescending;
        _thumbnailSize = settings.ThumbnailSize;
        _selectedSection = Sections.FirstOrDefault(s => s.Section == settings.LastSection && s.Section != LibrarySection.Missing) ?? Sections[0];
        _suppressRefresh = false;
        UpdateTileSize();

        _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            RefreshView();
        };

        ThumbnailLoader.Instance.DecodeFailed += OnDecodeFailed;
        InitPlaylists();
    }

    public LibraryDb Db { get; }
    public ThumbnailCache Thumbs { get; }
    public AppSettings Settings { get; }

    /// <summary>The photos currently shown in the gallery, filtered and sorted.</summary>
    public BulkObservableCollection<Photo> Photos { get; } = new();
    public ObservableCollection<NavEntry> Sections { get; }
    public ObservableCollection<Banner> Banners { get; } = new();

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(SortField.DateTaken, "Date taken"),
        new(SortField.DateImported, "Date imported"),
        new(SortField.Name, "Name"),
        new(SortField.Size, "Size"),
    ];

    /// <summary>Raised after the gallery contents are replaced, with the photos that should stay selected.</summary>
    public event Action<IReadOnlyList<Photo>>? ViewReset;

    /// <summary>Raised when the view should scroll a photo into view and select it.</summary>
    public event Action<Photo>? RevealRequested;

    [ObservableProperty] private NavEntry? _selectedSection;
    [ObservableProperty] private SortField _sortBy;
    [ObservableProperty] private bool _sortDescending;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private double _thumbnailSize;
    [ObservableProperty] private int _thumbDecodeSize = 200;
    [ObservableProperty] private Size _tileSize;
    [ObservableProperty] private string _viewTitle = "All Photos";
    [ObservableProperty] private string _viewSubtitle = "";
    [ObservableProperty] private bool _isLoading = true;
    [ObservableProperty] private bool _isLibraryEmpty;
    [ObservableProperty] private bool _isViewEmpty;

    [ObservableProperty] private bool _isImporting;
    [ObservableProperty] private double _importPercent;
    [ObservableProperty] private string _importStatus = "";

    [ObservableProperty] private Photo? _selectedPhoto;
    [ObservableProperty] private int _selectedCount;
    public IReadOnlyList<Photo> SelectedPhotos { get; private set; } = [];

    public int LibraryCount => _all.Count;
    public IReadOnlyList<Photo> AllPhotos => _all;
    public Photo? FindPhoto(long id) => _byId.GetValueOrDefault(id);

    // ------------------------------------------------------------------ startup

    public async Task InitializeAsync()
    {
        try
        {
            var photos = await Task.Run(Db.LoadPhotos);
            foreach (var p in photos)
            {
                if (_byId.ContainsKey(p.Id)) continue;
                _all.Add(p);
                _byId[p.Id] = p;
            }
            // Errors that may be fixed by installing a codec (WebP/HEIC/AVIF) are retried on every launch.
            var retry = _all.Where(p => p.DecodeError != null && Friendly.IsCodecMessage(p.DecodeError)).ToList();
            if (retry.Count > 0)
            {
                foreach (var p in retry) p.DecodeError = null;
                try { Db.ClearDecodeErrors(retry.Select(p => p.Id)); } catch (Exception ex) { Log.Warn("Could not reset decode errors", ex); }
            }
            LoadPlaylists();
            RefreshView();
            UpdateCounts();
            Log.Info($"Library loaded: {photos.Count} photos");
        }
        catch (Exception ex)
        {
            Log.Error("Library could not be loaded", ex);
            ShowBanner("load", BannerKind.Error, "Your library couldn't be loaded. Details were written to the log.");
        }
        finally
        {
            IsLoading = false;
        }

        _ = CheckMissingFilesAsync();
        var limit = (long)Settings.ThumbnailCacheLimitMB * 1024 * 1024;
        _ = Task.Run(() => Thumbs.Trim(limit));
    }

    // ------------------------------------------------------------------ view

    partial void OnSelectedSectionChanged(NavEntry? value)
    {
        if (value == null) return;
        if (value.Section != LibrarySection.Missing) Settings.LastSection = value.Section;
        RefreshView();
    }

    partial void OnSortByChanged(SortField value)
    {
        Settings.SortBy = value;
        RefreshView();
    }

    partial void OnSortDescendingChanged(bool value)
    {
        Settings.SortDescending = value;
        RefreshView();
    }

    partial void OnSearchTextChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    partial void OnThumbnailSizeChanged(double value)
    {
        Settings.ThumbnailSize = value;
        UpdateTileSize();
    }

    public void SetDpiScale(double scale)
    {
        _dpiScale = scale > 0 ? scale : 1;
        UpdateTileSize();
    }

    void UpdateTileSize()
    {
        // Tile = image + padding/border/margin from the TileItem style.
        TileSize = new Size(ThumbnailSize + 16, ThumbnailSize + 16);
        var px = ThumbnailSize * _dpiScale;
        ThumbDecodeSize = (int)Math.Clamp(Math.Ceiling(px / 40.0) * 40, 80, ThumbnailCache.MaxSide);
    }

    [RelayCommand]
    void ToggleSortDirection() => SortDescending = !SortDescending;

    [RelayCommand]
    void ClearSearch() => SearchText = "";

    public void RefreshView()
    {
        if (_suppressRefresh) return;
        var section = SelectedSection?.Section ?? LibrarySection.All;
        var terms = SearchTerms();
        var list = new List<Photo>(_all.Count);
        foreach (var p in _all)
            if (MatchesSection(p, section) && MatchesSearch(p, terms))
                list.Add(p);
        list.Sort(CurrentComparison());

        var keep = SelectedPhotos;
        Photos.ReplaceAll(list);
        UpdateViewText();

        if (keep.Count > 0)
        {
            var visible = new HashSet<Photo>(list);
            ViewReset?.Invoke(keep.Where(visible.Contains).ToList());
        }
        else
        {
            ViewReset?.Invoke([]);
        }
    }

    string[] SearchTerms() =>
        (SearchText ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    static bool MatchesSection(Photo p, LibrarySection section) => section switch
    {
        LibrarySection.Favourites => p.IsFavourite,
        LibrarySection.Recent => p.DateImportedUtc >= DateTime.UtcNow.AddDays(-RecentDays),
        LibrarySection.Missing => p.IsMissing,
        _ => true,
    };

    static bool MatchesSearch(Photo p, string[] terms)
    {
        foreach (var t in terms)
        {
            if (p.FileName.Contains(t, StringComparison.OrdinalIgnoreCase)) continue;
            if (p.Tags.Length > 0 && p.Tags.Contains(t, StringComparison.OrdinalIgnoreCase)) continue;
            return false;
        }
        return true;
    }

    bool Matches(Photo p) => MatchesSection(p, SelectedSection?.Section ?? LibrarySection.All) && MatchesSearch(p, SearchTerms());

    Comparison<Photo> CurrentComparison()
    {
        Comparison<Photo> primary = SortBy switch
        {
            SortField.DateImported => (a, b) => a.DateImportedUtc.CompareTo(b.DateImportedUtc),
            SortField.Name => (a, b) => NaturalSort.Compare(a.FileName, b.FileName),
            SortField.Size => (a, b) => a.SizeBytes.CompareTo(b.SizeBytes),
            _ => (a, b) => a.EffectiveDate.CompareTo(b.EffectiveDate),
        };
        var desc = SortDescending;
        return (a, b) =>
        {
            var r = primary(a, b);
            if (r == 0) r = a.Id.CompareTo(b.Id);
            return desc ? -r : r;
        };
    }

    /// <summary>Puts a photo back in the right place (or removes it) after something that affects filtering changed.</summary>
    public void Reposition(Photo p)
    {
        var index = Photos.IndexOf(p);
        var matches = Matches(p);
        if (index >= 0 && !matches)
        {
            Photos.RemoveAt(index);
        }
        else if (index < 0 && matches)
        {
            Photos.Insert(FindInsertIndex(p, CurrentComparison()), p);
        }
        UpdateViewText();
    }

    int FindInsertIndex(Photo p, Comparison<Photo> cmp)
    {
        int lo = 0, hi = Photos.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (cmp(Photos[mid], p) <= 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    void UpdateViewText()
    {
        var section = SelectedSection;
        ViewTitle = section?.Title ?? "All Photos";
        var n = Photos.Count;
        ViewSubtitle = n == 1 ? "1 photo" : $"{n:N0} photos";
        if (!string.IsNullOrWhiteSpace(SearchText) && n != CountInSection())
            ViewSubtitle += " match your search";
        IsLibraryEmpty = _all.Count == 0;
        IsViewEmpty = n == 0 && _all.Count > 0;
    }

    int CountInSection()
    {
        var section = SelectedSection?.Section ?? LibrarySection.All;
        return section == LibrarySection.All ? _all.Count : _all.Count(p => MatchesSection(p, section));
    }

    public void UpdateCounts()
    {
        int fav = 0, recent = 0, missing = 0;
        var cutoff = DateTime.UtcNow.AddDays(-RecentDays);
        foreach (var p in _all)
        {
            if (p.IsFavourite) fav++;
            if (p.DateImportedUtc >= cutoff) recent++;
            if (p.IsMissing) missing++;
        }
        Sections[0].Count = _all.Count;
        Sections[1].Count = fav;
        Sections[2].Count = recent;
        Sections[3].Count = missing;
        Sections[3].IsVisible = missing > 0 || SelectedSection?.Section == LibrarySection.Missing;
        OnPropertyChanged(nameof(LibraryCount));
        UpdateViewText();
    }

    // ------------------------------------------------------------------ selection

    /// <summary>Called by the view whenever the gallery selection changes.</summary>
    public void SetSelection(IList selectedItems, Photo? primary)
    {
        SelectedPhotos = selectedItems.Cast<Photo>().ToList();
        SelectedCount = SelectedPhotos.Count;
        SelectedPhoto = primary ?? SelectedPhotos.FirstOrDefault();
    }

    public void Reveal(Photo p)
    {
        if (!Photos.Contains(p))
        {
            SearchText = "";
            _searchTimer.Stop();
            SelectedSection = Sections[0];
            RefreshView();
        }
        RevealRequested?.Invoke(p);
    }

    // ------------------------------------------------------------------ banners

    public void ShowBanner(string key, BannerKind kind, string message, params BannerAction[] actions)
    {
        var existing = Banners.FirstOrDefault(b => b.Key == key);
        if (existing != null) Banners.Remove(existing);
        Banners.Insert(0, new Banner(key, kind, message, actions, b => Banners.Remove(b)));
        while (Banners.Count > 3) Banners.RemoveAt(Banners.Count - 1);
    }

    public void CloseBanner(string key)
    {
        var existing = Banners.FirstOrDefault(b => b.Key == key);
        if (existing != null) Banners.Remove(existing);
    }

    // ------------------------------------------------------------------ import

    [RelayCommand]
    void Import() => ShowImportDialog(null);

    public void ShowImportDialog(IReadOnlyList<string>? initialPaths)
    {
        if (IsImporting)
        {
            Dialogs.Info("Import in progress", "Wait for the current import to finish, or cancel it first.");
            return;
        }
        if (IsLoading)
        {
            Dialogs.Info("Still loading", "Your library is still loading. Try again in a moment.");
            return;
        }
        var dlg = new ImportDialog(Settings, initialPaths) { Owner = Application.Current.MainWindow };
        if (dlg.ShowDialog() == true && dlg.Result is { } options)
            _ = RunImportAsync(options);
    }

    public Task ImportAsync(ImportOptions options) => RunImportAsync(options);

    async Task RunImportAsync(ImportOptions options)
    {
        _importCts = new CancellationTokenSource();
        IsImporting = true;
        ImportPercent = 0;
        ImportStatus = "Looking for photos…";
        CloseBanner("import");

        var progress = new Progress<ImportProgress>(p =>
        {
            if (p.Scanning)
            {
                ImportPercent = 0;
                ImportStatus = $"Looking for photos… {p.Found:N0} found";
                return;
            }
            ImportPercent = p.Found == 0 ? 100 : 100.0 * p.Processed / p.Found;
            var extra = new List<string>();
            if (p.Duplicates > 0) extra.Add($"{p.Duplicates:N0} duplicates skipped");
            if (p.AlreadyInLibrary > 0) extra.Add($"{p.AlreadyInLibrary:N0} already in library");
            if (p.Failed > 0) extra.Add($"{p.Failed:N0} failed");
            ImportStatus = $"Importing {p.Processed:N0} of {p.Found:N0}" + (extra.Count > 0 ? " · " + string.Join(" · ", extra) : "");
        });

        try
        {
            var result = await _importer.RunAsync(options, progress,
                batch => _ui.BeginInvoke(() => AddImported(batch)), _importCts.Token);
            // Let any batches queued just before completion land first.
            await _ui.InvokeAsync(() => { }, DispatcherPriority.Background);
            ShowImportSummary(result);
        }
        catch (Exception ex)
        {
            Log.Error("Import failed", ex);
            ShowBanner("import", BannerKind.Error, "The import stopped because of an error. " + Friendly.Describe(ex));
        }
        finally
        {
            IsImporting = false;
            _importCts.Dispose();
            _importCts = null;
        }

        var limit = (long)Settings.ThumbnailCacheLimitMB * 1024 * 1024;
        _ = Task.Run(() => Thumbs.Trim(limit));
    }

    [RelayCommand]
    void CancelImport()
    {
        _importCts?.Cancel();
        ImportStatus = "Cancelling…";
    }

    void AddImported(IReadOnlyList<Photo> batch)
    {
        var cmp = CurrentComparison();
        var section = SelectedSection?.Section ?? LibrarySection.All;
        var terms = SearchTerms();
        foreach (var p in batch)
        {
            if (_byId.ContainsKey(p.Id)) continue;
            _all.Add(p);
            _byId[p.Id] = p;
            if (MatchesSection(p, section) && MatchesSearch(p, terms))
                Photos.Insert(FindInsertIndex(p, cmp), p);
        }
        UpdateCounts();
    }

    void ShowImportSummary(ImportResult r)
    {
        var parts = new List<string>
        {
            r.Imported == 1 ? "Imported 1 photo." : $"Imported {r.Imported:N0} photos.",
        };
        if (r.Duplicates > 0) parts.Add(r.Duplicates == 1 ? "Skipped 1 duplicate." : $"Skipped {r.Duplicates:N0} duplicates.");
        if (r.AlreadyInLibrary > 0) parts.Add($"{r.AlreadyInLibrary:N0} already in the library.");
        if (r.Failed > 0) parts.Add(r.Failed == 1 ? "1 file couldn't be imported." : $"{r.Failed:N0} files couldn't be imported.");
        if (r.Found == 0 && !r.Cancelled) parts = ["No supported photos were found."];
        if (r.Cancelled) parts.Insert(0, "Import cancelled.");

        var kind = r.Failed > 0 || r.Errors.Count > 0 ? BannerKind.Warning : r.Cancelled ? BannerKind.Info : BannerKind.Success;
        var actions = new List<BannerAction>();
        if (r.Errors.Count > 0)
        {
            var errors = r.Errors.ToList();
            actions.Add(new BannerAction("Details", () => Dialogs.Info("Import problems",
                string.Join(Environment.NewLine, errors.Take(25)) + (errors.Count > 25 ? $"{Environment.NewLine}…and {errors.Count - 25} more (see the log)." : ""))));
        }
        if (r.Imported > 0 && SelectedSection?.Section != LibrarySection.Recent)
            actions.Add(new BannerAction("Show", () => SelectedSection = Sections[2]));
        ShowBanner("import", kind, string.Join(" ", parts), actions.ToArray());
    }

    // ------------------------------------------------------------------ missing files

    public async Task CheckMissingFilesAsync()
    {
        var snapshot = _all.Select(p => (Photo: p, p.Path, p.SizeBytes, p.ModifiedUtc)).ToList();
        if (snapshot.Count == 0) return;

        var (missing, present, changed) = await Task.Run(() =>
        {
            // One probe per folder (Lazy: concurrent callers share it). null = the share didn't answer.
            var folderState = new ConcurrentDictionary<string, Lazy<bool?>>(StringComparer.OrdinalIgnoreCase);
            var missingBag = new ConcurrentBag<Photo>();
            var presentBag = new ConcurrentBag<Photo>();
            var changedBag = new ConcurrentBag<(Photo Photo, long Size, DateTime Modified, ImageInfo? Info, string? Hash)>();
            Parallel.ForEach(snapshot, new ParallelOptions { MaxDegreeOfParallelism = 8 }, item =>
            {
                try
                {
                    var dir = System.IO.Path.GetDirectoryName(item.Path) ?? "";
                    // Network folders are probed with a timeout. If a share doesn't answer, its photos'
                    // status is left as it was: "not answering" is not the same as "missing".
                    var folder = folderState.GetOrAdd(dir, d => new Lazy<bool?>(() =>
                    {
                        try { return NetworkPaths.Run(d, () => Directory.Exists(d), NetworkPaths.ProbeTimeout); }
                        catch (NetworkTimeoutException) { return null; }
                    })).Value;
                    if (folder == null) return;
                    FileInfo? fi = null;
                    if (folder == true)
                    {
                        try
                        {
                            fi = NetworkPaths.Run(item.Path, () => { var f = new FileInfo(item.Path); _ = f.Exists; return f; }, NetworkPaths.ProbeTimeout);
                        }
                        catch (NetworkTimeoutException) { return; } // unknown: leave as it was
                    }
                    if (fi is not { Exists: true })
                    {
                        missingBag.Add(item.Photo);
                        return;
                    }
                    presentBag.Add(item.Photo);
                    if (fi.Length != item.SizeBytes || fi.LastWriteTimeUtc != item.ModifiedUtc)
                    {
                        ImageInfo? info = null;
                        string? hash = null;
                        try
                        {
                            try { info = NetworkPaths.Run(item.Path, () => ImageDecoder.ReadInfo(item.Path), NetworkPaths.ReadTimeout); }
                            catch (NetworkTimeoutException) { throw; }
                            catch { /* undecodable: details stay as they were */ }
                            hash = NetworkPaths.Run(item.Path, () => ImportService.HashFileForCompare(item.Path), TimeSpan.FromMinutes(2));
                        }
                        catch (Exception ex) when (ex is NetworkTimeoutException or IOException or UnauthorizedAccessException)
                        {
                            // Couldn't read it fully now: don't save half-updated details; the next scan retries.
                            return;
                        }
                        changedBag.Add((item.Photo, fi.Length, fi.LastWriteTimeUtc, info, hash));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn($"Missing-file check failed for {item.Path}", ex);
                }
            });
            return (missingBag.ToList(), presentBag.ToList(), changedBag.ToList());
        });

        // Ignore results for photos that were renamed, moved or removed while the scan ran.
        var checkedPath = snapshot.ToDictionary(s => s.Photo, s => s.Path);
        bool Unchanged(Photo p) => _byId.ContainsKey(p.Id) && checkedPath.TryGetValue(p, out var path) && path == p.Path;
        var nowMissing = missing.Where(p => !p.IsMissing && Unchanged(p)).ToList();
        var nowFound = present.Where(p => p.IsMissing && Unchanged(p)).ToList();
        var updates = changed.Where(c => Unchanged(c.Photo)).Select(c => (c.Photo, Copy: new Photo
        {
            Id = c.Photo.Id, Path = c.Photo.Path, Hash = c.Hash ?? c.Photo.Hash, SizeBytes = c.Size, ModifiedUtc = c.Modified,
            PixelWidth = c.Info?.PixelWidth ?? c.Photo.PixelWidth, PixelHeight = c.Info?.PixelHeight ?? c.Photo.PixelHeight,
            Orientation = c.Info?.Orientation ?? c.Photo.Orientation, DateTaken = c.Info?.DateTaken ?? c.Photo.DateTaken,
            CameraModel = c.Info?.CameraModel ?? c.Photo.CameraModel, DecodeError = c.Info != null ? null : c.Photo.DecodeError,
            IsMissing = false,
        })).ToList();
        try
        {
            await Task.Run(() =>
            {
                if (nowMissing.Count > 0) Db.SetMissing(nowMissing.Select(p => p.Id), true);
                if (nowFound.Count > 0) Db.SetMissing(nowFound.Select(p => p.Id), false);
                if (updates.Count > 0) Db.UpdateFileDetailsMany(updates.Select(u => u.Copy));
            });
            foreach (var p in nowMissing) p.IsMissing = true;
            foreach (var p in nowFound) p.IsMissing = false;
            foreach (var (p, c) in updates)
            {
                if (!Unchanged(p)) continue;
                p.Hash = c.Hash;
                p.SizeBytes = c.SizeBytes;
                p.ModifiedUtc = c.ModifiedUtc;
                p.PixelWidth = c.PixelWidth;
                p.PixelHeight = c.PixelHeight;
                p.Orientation = c.Orientation;
                p.DateTaken = c.DateTaken;
                p.CameraModel = c.CameraModel;
                p.DecodeError = c.DecodeError;
                p.NotifyAllChanged();
                p.ThumbVersion++;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Could not save missing-file status", ex);
        }

        if (changed.Count > 0) Log.Info($"{changed.Count} photos changed on disk since they were imported");
        UpdateCounts();
        UpdateMissingBanner();
        if (SelectedSection?.Section == LibrarySection.Missing) RefreshView();
    }

    void UpdateMissingBanner()
    {
        var count = _all.Count(p => p.IsMissing);
        if (count == 0)
        {
            CloseBanner("missing");
            return;
        }
        var msg = count == 1
            ? "1 photo can't be found. It may have been moved, renamed or deleted outside PhotoScope, or its drive isn't connected."
            : $"{count:N0} photos can't be found. They may have been moved, renamed or deleted outside PhotoScope, or their drive isn't connected.";
        ShowBanner("missing", BannerKind.Warning, msg,
            new BannerAction("Review", () => SelectedSection = Sections[3]),
            new BannerAction("Relocate…", () => RelocateMissing(_all.Where(p => p.IsMissing).ToList())),
            new BannerAction("Remove from library", () => RemoveFromLibrary(_all.Where(p => p.IsMissing).ToList())));
    }

    void OnDecodeFailed(Photo p, string error, bool permanent)
    {
        if (!_byId.ContainsKey(p.Id)) return;
        // Temporary problems (file in use, drive offline, low memory) aren't remembered; the
        // thumbnail is simply tried again later.
        if (!permanent) return;
        p.DecodeError = error;
        try { Db.SetDecodeError(p.Id, error); }
        catch (Exception ex) { Log.Warn("Could not save decode error", ex); }
    }

    /// <summary>Removes photos from the in-memory library and the view (callers handle the database).</summary>
    void ForgetPhotos(IReadOnlyCollection<Photo> photos)
    {
        var set = new HashSet<Photo>(photos);
        _all.RemoveAll(set.Contains);
        foreach (var p in photos) _byId.Remove(p.Id);
        Photos.RemoveAll(set.Contains);
        RemoveFromLoadedPlaylist(set);
        UpdateCounts();
        UpdateMissingBanner();
    }
}
