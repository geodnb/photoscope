using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoScope.Controls;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

/// <summary>
/// Photo viewer and slideshow, in a normal resizable window with an optional full-screen mode.
/// Two ways in:
/// <list type="bullet">
/// <item>library mode: a list of library photos (from the gallery or a playlist);</item>
/// <item>file mode: a file opened from Windows. It is shown at once; the other images in its folder
/// are listed in the background, and photos already in the library pick up their rating, favourite
/// and tags once the library has loaded. Files that aren't in the library can be added with a button.</item>
/// </list>
/// Zoom/pan maths lives here because it is purely visual; library changes go through the view model.
/// </summary>
public partial class ViewerWindow
{
    const double MaxZoom = 16;
    static readonly double[] Intervals = [2, 3, 5, 8, 10, 15, 20, 30, 60];
    static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    readonly MainViewModel _vm;
    List<Photo> _items;
    readonly bool _fileMode;
    readonly Dictionary<string, Task<Decoded>> _cache = new(PathComparer);
    readonly SemaphoreSlim _decodeGate = new(2);
    readonly DispatcherTimer _slideTimer = new();
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Random _random = new();

    int _index;
    int _showVersion;
    int _listingVersion;
    Photo? _shown;
    bool _hasImage;
    List<int> _order = new();
    int _orderPos;
    bool _slideshow;
    bool _playing;

    // Full screen: the window state to go back to.
    bool _fullScreen;
    WindowState _restoreState;
    bool _slideshowStartedOnce;

    // Zoom state. Content is measured in DIPs such that scale 1 == one image pixel per screen pixel.
    double _contentW, _contentH, _scale = 1, _tx, _ty;
    bool _fit = true;
    double _dpi = 1;
    Point? _panStart;
    double _panTx, _panTy;
    int _wheelAccumulator;

    /// <param name="Reduced">True when decoded at screen size rather than full resolution.</param>
    /// <param name="Info">Header details, read when the photo's size wasn't known (files opened from Windows).</param>
    sealed record Decoded(BitmapSource? Bitmap, string? Error, bool Missing, bool Reduced = false, bool Skipped = false,
        ImageInfo? Info = null, long Size = 0, DateTime ModifiedUtc = default);

    // Paths still worth decoding (current + neighbours). Read by decode tasks, so guarded by a lock.
    readonly HashSet<string> _wanted = new(PathComparer);
    Task<Decoded>? _fullTask;
    string? _fullFor;
    bool _shownReduced;

    /// <summary>
    /// Longest side, in physical pixels, worth decoding for "fit" display: the largest connected
    /// monitor, so images stay sharp when the window is full screen on a big secondary display.
    /// </summary>
    readonly int ScreenLongSide = Math.Clamp(WindowPlacement.LargestMonitorLongSide(), 1280, ImageDecoder.MaxViewerSide);

    public Photo? LastShown => _shown;

    /// <summary>Library mode: browse <paramref name="items"/> (gallery view or playlist).</summary>
    public ViewerWindow(MainViewModel vm, IReadOnlyList<Photo> items, int startIndex, bool slideshow)
        : this(vm, items.ToList(), startIndex, slideshow, fileMode: false)
    {
    }

    /// <summary>File mode: show a file opened from Windows, then let the arrow keys browse its folder.</summary>
    public static ViewerWindow ForFile(MainViewModel vm, string path) =>
        new(vm, [LoosePhoto(path)], 0, slideshow: false, fileMode: true);

    ViewerWindow(MainViewModel vm, List<Photo> items, int startIndex, bool slideshow, bool fileMode)
    {
        InitializeComponent();
        Icon = ThemedWindow.AppIcon;
        _vm = vm;
        _fileMode = fileMode;
        _items = items;
        _index = Math.Clamp(startIndex, 0, Math.Max(0, _items.Count - 1));
        // Opened from Explorer there's no library window behind the viewer, so offer a way to it.
        LibraryButton.Visibility = fileMode ? Visibility.Visible : Visibility.Collapsed;
        RestorePlacement();
        if (App.Offscreen) { App.SendToBack(this); App.AddSnapshotHook(this, Root); }

        for (var i = 0; i < 5; i++)
        {
            var star = new Button
            {
                Style = (Style)FindResource("ViewerButton"),
                Width = 28,
                FontSize = 15,
                Focusable = false,
                Tag = i + 1,
                Content = "\uE734",
                ToolTip = $"{i + 1} star{(i == 0 ? "" : "s")} ({i + 1})",
            };
            star.Click += (s, _) =>
            {
                var r = (int)((Button)s!).Tag;
                SetRating(_shown?.Rating == r ? 0 : r);
            };
            Stars.Children.Add(star);
        }

        foreach (var s in Intervals) IntervalBox.Items.Add(new ComboBoxItem { Content = s < 60 ? $"{s:0} sec" : "1 min", Tag = s });
        SelectInterval(_vm.Settings.SlideshowIntervalSeconds);
        ShuffleButton.IsChecked = _vm.Settings.SlideshowShuffle;
        LoopButton.IsChecked = _vm.Settings.SlideshowLoop;
        _slideTimer.Interval = TimeSpan.FromSeconds(_vm.Settings.SlideshowIntervalSeconds);
        _slideTimer.Tick += (_, _) => Advance(+1, fromTimer: true);
        _idleTimer.Tick += (_, _) => HideChrome();
        _loadingTimer.Tick += (_, _) =>
        {
            _loadingTimer.Stop();
            Loading.Visibility = Visibility.Visible;
        };

        BuildOrder(keepCurrent: true);
        _slideshow = slideshow;

        SourceInitialized += (_, _) => ThemeService.ApplyTitleBar(this, forceDark: true);
        Loaded += OnLoaded;
        Closing += OnClosing;
        Deactivated += (_, _) => ShowCursor();
        KeyDown += OnKeyDown;
        MouseMove += (_, _) => ShowChrome();
        Viewport.SizeChanged += (_, _) => { if (_fit) FitToScreen(); else ApplyTransform(); };
        Viewport.MouseWheel += OnWheel;
        Viewport.MouseLeftButtonDown += OnViewportMouseDown;
        Viewport.MouseMove += OnViewportMouseMove;
        Viewport.MouseLeftButtonUp += OnViewportMouseUp;
        Viewport.ManipulationStarting += (_, e) => { e.ManipulationContainer = Viewport; e.Mode = ManipulationModes.Scale | ManipulationModes.Translate; };
        Viewport.ManipulationDelta += OnManipulationDelta;
        DpiChanged += (_, e) => { _dpi = e.NewDpi.DpiScaleX; if (_shown != null) SetContentSize(_shown, MainImage.Source as BitmapSource); };
    }

    /// <summary>A photo for a file that isn't (yet known to be) in the library.</summary>
    static Photo LoosePhoto(string path) => new() { Id = 0, Path = path };

    static bool InLibrary(Photo p) => p.Id > 0;

    static string CacheKey(Photo p) => p.Path;

    // ------------------------------------------------------------------ window placement

    void RestorePlacement()
    {
        // Default: a comfortable size, centred over the library window (or on the primary monitor).
        var work = SystemParameters.WorkArea;
        Width = Math.Min(1280, work.Width);
        Height = Math.Min(820, work.Height);
        if (App.Offscreen)
        {
            Left = 0;
            Top = 0;
            return;
        }
        var owner = Dialogs.Owner();
        if (owner != null && owner != this && owner.WindowState == WindowState.Normal)
        {
            Left = owner.Left + Math.Max(0, (owner.ActualWidth - Width) / 2);
            Top = owner.Top + Math.Max(0, (owner.ActualHeight - Height) / 2);
        }
        else
        {
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }
        // The saved spot wins if it's still on a connected monitor (checked in physical pixels).
        SourceInitialized += (_, _) =>
        {
            WindowPlacement.Apply(this, _vm.Settings.ViewerPlacement);
            if (_slideshow) EnterFullScreen(); // before the first paint: no windowed flash
        };
    }

    void SavePlacement()
    {
        if (App.Offscreen) return;
        var placement = WindowPlacement.Capture(this, _fullScreen ? _restoreState == WindowState.Maximized : null);
        if (placement != null) _vm.Settings.ViewerPlacement = placement;
    }

    // ------------------------------------------------------------------ full screen

    void ToggleFullScreen()
    {
        if (_fullScreen) ExitFullScreen();
        else EnterFullScreen();
    }

    void EnterFullScreen()
    {
        if (_fullScreen || App.Offscreen) return;
        _restoreState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
        _fullScreen = true;
        // Style changes only take effect on a restored window; then maximise without a frame,
        // which covers the taskbar on the window's current monitor.
        if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        FullScreenButton.Content = "\uE73F";
        FullScreenButton.ToolTip = "Exit full screen (F11 or Esc)";
        AutomationPropertiesHelper.SetName(FullScreenButton, "Exit full screen");
    }

    void ExitFullScreen()
    {
        if (!_fullScreen) return;
        _fullScreen = false;
        // Windows keeps the window's normal bounds while it is maximised, so restoring the state
        // brings back the previous size and position.
        WindowState = WindowState.Normal;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        if (!WindowPlacement.NormalBoundsVisible(this))
        {
            // Its old monitor was unplugged while in full screen: centre on the primary monitor.
            var work = SystemParameters.WorkArea;
            Width = Math.Min(Width, work.Width);
            Height = Math.Min(Height, work.Height);
            Left = work.Left + (work.Width - Width) / 2;
            Top = work.Top + (work.Height - Height) / 2;
        }
        if (_restoreState == WindowState.Maximized) WindowState = WindowState.Maximized;
        FullScreenButton.Content = "\uE740";
        FullScreenButton.ToolTip = "Full screen (F11)";
        AutomationPropertiesHelper.SetName(FullScreenButton, "Full screen");
        ShowCursor();
        ShowChrome();
    }

    static class AutomationPropertiesHelper
    {
        public static void SetName(DependencyObject d, string name) => System.Windows.Automation.AutomationProperties.SetName(d, name);
    }

    // ------------------------------------------------------------------ lifecycle

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        Log.Info($"Viewer opened: {_items.Count} photos, start {_index}, slideshow {_slideshow}, file mode {_fileMode}");
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Activate();
        Focus();
        Show(_index, crossfade: false);
        if (_slideshow) StartSlideshow();
        ShowChrome();
        if (_fileMode) _ = LoadFolderAndLibraryAsync();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        Log.Info("Viewer closed");
        _slideTimer.Stop();
        _idleTimer.Stop();
        _loadingTimer.Stop();
        ShowCursor();
        SavePlacement();
        SettingsService.Save();
    }

    /// <summary>File mode: shows another file in this window (e.g. a second "Open" from Explorer).</summary>
    public void OpenFile(string path)
    {
        if (!_fileMode) return;
        var photo = _vm.LibraryLoaded.IsCompleted ? _vm.FindByPath(path) ?? LoosePhoto(path) : LoosePhoto(path);
        _items = [photo];
        _index = 0;
        ++_listingVersion;
        // Forget earlier decodes: the file may have been edited since it was last shown.
        _cache.Clear();
        _fullTask = null;
        _fullFor = null;
        if (!IsLoaded)
        {
            // Not shown yet (several files opened at once): OnLoaded shows the latest one.
            BuildOrder(keepCurrent: true);
            return;
        }
        if (WindowState == WindowState.Minimized) SystemCommands.RestoreWindow(this); // back to normal or maximised
        Activate();
        if (_playing) Pause();
        _slideshow = false;
        SlideBar.Visibility = Visibility.Collapsed;
        BuildOrder(keepCurrent: true);
        _shown = null;
        _hasImage = false;
        Show(0, crossfade: false);
        _ = LoadFolderAndLibraryAsync();
    }

    /// <summary>
    /// File mode, in the background after the first image is up: list the folder (sorted like
    /// Explorer) and attach library details to any photos that are in the library.
    /// </summary>
    async Task LoadFolderAndLibraryAsync()
    {
        var version = ++_listingVersion;
        var current = _items[_index].Path;
        var folder = Path.GetDirectoryName(current);
        List<string>? paths = null;
        if (folder != null)
        {
            try
            {
                paths = await NetworkPaths.RunAsync(folder, () => FolderListing.ListImages(folder, current), NetworkPaths.ReadTimeout);
            }
            catch (Exception ex)
            {
                Log.Warn($"Couldn't list {folder}", ex);
            }
        }
        if (version != _listingVersion || !IsLoaded) return;
        if (paths != null && _items.Count > 0) ApplyListing(paths);

        try { await _vm.LibraryLoaded; } catch { }
        if (version != _listingVersion || !IsLoaded) return;
        AttachLibrary();
    }

    void ApplyListing(List<string> paths)
    {
        var currentPath = _items[_index].Path;
        var existing = _items.ToDictionary(CacheKey, PathComparer);
        _items = paths.Select(p => existing.GetValueOrDefault(p) ?? LoosePhoto(p)).ToList();
        _index = Math.Max(0, _items.FindIndex(p => PathComparer.Equals(p.Path, currentPath)));
        BuildOrder(keepCurrent: true);
        UpdateChrome(_items[_index]);
        Preload();
    }

    /// <summary>Swaps in library photos (with rating, favourite, tags) for files that are in the library.</summary>
    void AttachLibrary()
    {
        var byPath = new Dictionary<string, Photo>(PathComparer);
        foreach (var p in _vm.AllPhotos) byPath[p.Path] = p;
        for (var i = 0; i < _items.Count; i++)
        {
            if (InLibrary(_items[i]) || !byPath.TryGetValue(_items[i].Path, out var lib)) continue;
            if (ReferenceEquals(_shown, _items[i])) _shown = lib;
            _items[i] = lib;
        }
        if (_shown != null)
        {
            UpdateChrome(_shown);
            SetContentSize(_shown, MainImage.Source as BitmapSource);
            if (_fit) FitToScreen(); else ApplyTransform();
        }
    }

    // ------------------------------------------------------------------ loading

    Task<Decoded> GetDecoded(Photo p)
    {
        if (_cache.TryGetValue(CacheKey(p), out var existing)) return existing;
        lock (_wanted) _wanted.Add(CacheKey(p));
        var task = DecodeAsync(p, ScreenLongSide, checkWanted: true);
        _cache[CacheKey(p)] = task;
        return task;
    }

    Task<Decoded> DecodeAsync(Photo p, int maxSide, bool checkWanted)
    {
        var (path, w, h, orientation) = (p.Path, p.PixelWidth, p.PixelHeight, p.Orientation);
        var key = CacheKey(p);
        // Network files don't take a local decode slot (their own thread + timeout instead), so a
        // dead share can never delay decoding a local photo.
        var network = NetworkPaths.IsNetwork(path);
        return Task.Run(async () =>
        {
            if (!network) await _decodeGate.WaitAsync();
            try
            {
                // The user may have moved on while this waited in the queue: don't decode for nothing.
                if (checkWanted)
                    lock (_wanted)
                        if (!_wanted.Contains(key)) return new Decoded(null, null, false, Skipped: true);
                return await NetworkPaths.RunAsync(path, () =>
                {
                    var fi = new FileInfo(path);
                    if (!fi.Exists) return new Decoded(null, "This file can't be found. It may have been moved, renamed or deleted.", true);
                    // Files opened from Windows have no stored size yet: read the header first so
                    // 1:1 zoom and the info overlay are right.
                    ImageInfo? info = null;
                    if (w <= 0 || h <= 0)
                    {
                        info = ImageDecoder.ReadInfo(path);
                        (w, h, orientation) = (info.PixelWidth, info.PixelHeight, info.Orientation);
                    }
                    var bmp = ImageDecoder.Decode(path, maxSide, w, h, orientation);
                    var originalLong = Math.Max(w, h);
                    var reduced = originalLong > 0 && Math.Max(bmp.PixelWidth, bmp.PixelHeight) < Math.Min(originalLong, ImageDecoder.MaxViewerSide);
                    return new Decoded(bmp, null, false, reduced, Info: info, Size: fi.Length, ModifiedUtc: fi.LastWriteTimeUtc);
                }, NetworkPaths.ReadTimeout);
            }
            catch (Exception ex)
            {
                Log.Warn($"Viewer could not decode {path}", ex);
                return new Decoded(null, Friendly.DescribeImageError(ex, path), false);
            }
            finally
            {
                if (!network) _decodeGate.Release();
            }
        });
    }

    /// <summary>Keeps the current photo and its neighbours decoded; drops everything else.</summary>
    void Preload()
    {
        PruneCache();
        foreach (var offset in new[] { 1, -1 })
        {
            var i = NeighbourIndex(offset);
            if (i >= 0) GetDecoded(_items[i]);
        }
    }

    /// <summary>Forgets every decode except the current photo and its immediate neighbours.</summary>
    void PruneCache()
    {
        var keep = new HashSet<string>(PathComparer);
        foreach (var offset in new[] { 0, 1, -1 })
        {
            var i = NeighbourIndex(offset);
            if (i >= 0) keep.Add(CacheKey(_items[i]));
        }
        if (_items.Count > 0) keep.Add(CacheKey(_items[_index]));
        lock (_wanted)
        {
            _wanted.Clear();
            _wanted.UnionWith(keep);
        }
        foreach (var k in _cache.Keys.Where(k => !keep.Contains(k)).ToList())
            _cache.Remove(k);
        if (_fullFor != null && !keep.Contains(_fullFor))
        {
            _fullTask = null;
            _fullFor = null;
        }
    }

    /// <summary>Loads the full-resolution image for the current photo once the user zooms in.</summary>
    async void EnsureFullResolution()
    {
        if (!_shownReduced || _shown is not { } p || _fit) return;
        if (!PathComparer.Equals(_fullFor, CacheKey(p)) || _fullTask == null)
        {
            _fullFor = CacheKey(p);
            _fullTask = DecodeAsync(p, 0, checkWanted: false);
        }
        var task = _fullTask;
        Decoded result;
        try { result = await task; }
        catch { return; }
        if (!IsLoaded || !PathComparer.Equals(_shown?.Path, p.Path) || result.Bitmap == null || _fullTask != task) return;
        MainImage.Source = result.Bitmap; // same on-screen size, more pixels
        _shownReduced = false;
    }

    int NeighbourIndex(int offset)
    {
        if (_items.Count == 0) return -1;
        if (_slideshow)
        {
            var pos = _orderPos + offset;
            if (pos < 0 || pos >= _order.Count)
            {
                if (LoopButton.IsChecked != true) return -1;
                pos = ((pos % _order.Count) + _order.Count) % _order.Count;
            }
            return _order[pos];
        }
        var i = _index + offset;
        return i >= 0 && i < _items.Count ? i : -1;
    }

    async void Show(int index, bool crossfade)
    {
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        _index = Math.Clamp(index, 0, _items.Count - 1);
        var p = _items[_index];
        var version = ++_showVersion;
        UpdateChrome(p);
        PruneCache(); // bounded memory even when keys are held down or decodes are slow

        var task = GetDecoded(p);
        if (!task.IsCompleted)
        {
            // Show the cached thumbnail at once so navigation feels instant.
            var thumb = InLibrary(p) ? _vm.Thumbs.TryLoad(p, ThumbnailCache.MaxSide) : null;
            if (thumb != null && !crossfade) Display(p, thumb, null, false, isPlaceholder: true);
            _loadingTimer.Start();
        }

        Decoded result;
        try
        {
            result = await task;
        }
        catch (Exception ex)
        {
            result = new Decoded(null, Friendly.Describe(ex), false);
        }
        if (version != _showVersion || !IsLoaded) return;
        _loadingTimer.Stop();
        Loading.Visibility = Visibility.Collapsed;
        // The item may have been swapped for its library entry while decoding.
        p = _items[_index];

        if (result.Skipped)
        {
            // Shouldn't happen for the current photo, but if it does, decode it now.
            _cache.Remove(CacheKey(p));
            Show(_index, crossfade);
            return;
        }
        if (!InLibrary(p))
        {
            // Fill in details for files opened from Windows (not saved anywhere).
            if (result.Info is { } info && p.PixelWidth <= 0)
            {
                p.PixelWidth = info.PixelWidth;
                p.PixelHeight = info.PixelHeight;
                p.Orientation = info.Orientation;
                p.DateTaken = info.DateTaken;
                p.CameraModel = info.CameraModel;
            }
            if (result.Size > 0)
            {
                p.SizeBytes = result.Size;
                p.ModifiedUtc = result.ModifiedUtc;
            }
            UpdateInfo(p);
        }
        if (crossfade && _hasImage) StartCrossfade();
        Display(p, result.Bitmap, result.Error, result.Missing, isPlaceholder: false);
        _shownReduced = result.Reduced;
        if (!_fit) EnsureFullResolution();
        Preload();
    }

    void Display(Photo p, BitmapSource? bmp, string? error, bool missing, bool isPlaceholder)
    {
        var changedPhoto = !ReferenceEquals(_shown, p);
        _shown = p;
        if (bmp == null)
        {
            MainImage.Source = null;
            _hasImage = false;
            Problem.Visibility = Visibility.Visible;
            ProblemGlyph.Text = missing ? "\uE7BA" : "\uE91B";
            ProblemTitle.Text = missing ? "File not found" : "Can't display this photo";
            ProblemText.Text = (error ?? "") + Environment.NewLine + p.Path;
            UpdateZoomText();
            return;
        }

        Problem.Visibility = Visibility.Collapsed;
        MainImage.Source = bmp;
        _hasImage = true;
        SetContentSize(p, bmp);
        // Placeholder → full image for the same photo keeps the current zoom.
        if (changedPhoto || isPlaceholder) FitToScreen();
        else ApplyTransform();
    }

    /// <summary>Sizes the image so that scale 1 means one image pixel per physical screen pixel.</summary>
    void SetContentSize(Photo p, BitmapSource? bmp)
    {
        double w = p.OrientedWidth, h = p.OrientedHeight;
        if ((w <= 0 || h <= 0) && bmp != null)
        {
            w = bmp.PixelWidth;
            h = bmp.PixelHeight;
        }
        if (w <= 0 || h <= 0) return;
        MainImage.Width = w / _dpi;
        MainImage.Height = h / _dpi;
        MainImage.LayoutTransform = new RotateTransform(p.Rotation);
        var swap = p.Rotation % 180 != 0;
        _contentW = (swap ? h : w) / _dpi;
        _contentH = (swap ? w : h) / _dpi;
    }

    void StartCrossfade()
    {
        FadeImage.Source = MainImage.Source;
        FadeImage.Width = MainImage.Width;
        FadeImage.Height = MainImage.Height;
        FadeImage.LayoutTransform = MainImage.LayoutTransform;
        FadeHost.RenderTransform = ImageHost.RenderTransform;
        var anim = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(650)) { EasingFunction = new QuadraticEase() };
        anim.Completed += (_, _) => FadeImage.Source = null;
        FadeHost.BeginAnimation(OpacityProperty, anim);
        ImageHost.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(650)));
    }

    // ------------------------------------------------------------------ zoom and pan

    double FitScale
    {
        get
        {
            var vw = Viewport.ActualWidth;
            var vh = Viewport.ActualHeight;
            if (_contentW <= 0 || _contentH <= 0 || vw <= 0 || vh <= 0) return 1;
            return Math.Min(1, Math.Min(vw / _contentW, vh / _contentH));
        }
    }

    void FitToScreen()
    {
        _fit = true;
        _scale = FitScale;
        ApplyTransform();
    }

    void ZoomAt(Point p, double newScale)
    {
        if (!_hasImage) return;
        var min = FitScale;
        newScale = Math.Clamp(newScale, min, MaxZoom);
        _tx = p.X - (p.X - _tx) * newScale / _scale;
        _ty = p.Y - (p.Y - _ty) * newScale / _scale;
        _scale = newScale;
        _fit = Math.Abs(newScale - min) < 0.0001;
        ApplyTransform();
        if (!_fit) EnsureFullResolution();
    }

    void ApplyTransform()
    {
        var vw = Viewport.ActualWidth;
        var vh = Viewport.ActualHeight;
        var cw = _contentW * _scale;
        var ch = _contentH * _scale;
        _tx = cw <= vw ? (vw - cw) / 2 : Math.Clamp(_tx, vw - cw, 0);
        _ty = ch <= vh ? (vh - ch) / 2 : Math.Clamp(_ty, vh - ch, 0);
        ImageHost.RenderTransform = new MatrixTransform(_scale, 0, 0, _scale, _tx, _ty);
        Viewport.Cursor = CanPan ? (_panStart != null ? Cursors.SizeAll : Cursors.Hand) : null;
        UpdateZoomText();
    }

    bool CanPan => _hasImage && (_contentW * _scale > Viewport.ActualWidth + 0.5 || _contentH * _scale > Viewport.ActualHeight + 0.5);

    void UpdateZoomText() => ZoomText.Text = _hasImage ? $"{_scale * 100:0}%" + (_fit ? " (fit)" : "") : "";

    Point ViewportCentre => new(Viewport.ActualWidth / 2, Viewport.ActualHeight / 2);

    void OnWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            var factor = Math.Pow(1.0015, e.Delta);
            ZoomAt(e.GetPosition(Viewport), _scale * factor);
            return;
        }
        // Accumulate so high-resolution touchpads don't skip several photos per flick.
        _wheelAccumulator += e.Delta;
        if (Math.Abs(_wheelAccumulator) < 120) return;
        var step = _wheelAccumulator > 0 ? -1 : +1;
        _wheelAccumulator = 0;
        Advance(step, fromTimer: false);
    }

    void OnViewportMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleZoom(e.GetPosition(Viewport));
            e.Handled = true;
            return;
        }
        if (!CanPan) return;
        _panStart = e.GetPosition(Viewport);
        _panTx = _tx;
        _panTy = _ty;
        Viewport.CaptureMouse();
        Viewport.Cursor = Cursors.SizeAll;
        e.Handled = true;
    }

    void OnViewportMouseMove(object sender, MouseEventArgs e)
    {
        if (_panStart is not { } start || e.LeftButton != MouseButtonState.Pressed) return;
        var p = e.GetPosition(Viewport);
        _tx = _panTx + (p.X - start.X);
        _ty = _panTy + (p.Y - start.Y);
        ApplyTransform();
    }

    void OnViewportMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_panStart == null) return;
        _panStart = null;
        Viewport.ReleaseMouseCapture();
        ApplyTransform();
    }

    void OnManipulationDelta(object? sender, ManipulationDeltaEventArgs e)
    {
        var d = e.DeltaManipulation;
        if (Math.Abs(d.Scale.X - 1) > 0.001)
            ZoomAt(e.ManipulationOrigin, _scale * d.Scale.X);
        if (CanPan)
        {
            _tx += d.Translation.X;
            _ty += d.Translation.Y;
            ApplyTransform();
        }
        e.Handled = true;
    }

    void ToggleZoom(Point at)
    {
        if (!_hasImage) return;
        if (_fit)
        {
            var target = FitScale < 1 ? 1 : Math.Min(MaxZoom, _scale * 2);
            ZoomAt(at, target);
        }
        else
        {
            FitToScreen();
        }
    }

    void ActualSize()
    {
        if (!_hasImage) return;
        if (Math.Abs(_scale - 1) < 0.0001 && !_fit) FitToScreen();
        else ZoomAt(ViewportCentre, 1);
    }

    // ------------------------------------------------------------------ navigation and slideshow

    void BuildOrder(bool keepCurrent)
    {
        _order = Enumerable.Range(0, _items.Count).ToList();
        if (ShuffleButton.IsChecked == true)
        {
            for (var i = _order.Count - 1; i > 0; i--)
            {
                var j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }
            if (keepCurrent)
            {
                // Current photo goes first so shuffling doesn't jump away from it.
                var at = _order.IndexOf(_index);
                if (at > 0) (_order[0], _order[at]) = (_order[at], _order[0]);
            }
        }
        _orderPos = Math.Max(0, _order.IndexOf(_index));
    }

    void Advance(int step, bool fromTimer)
    {
        if (_items.Count == 0) return;
        if (_slideshow)
        {
            var pos = _orderPos + step;
            if (pos >= _order.Count || pos < 0)
            {
                if (LoopButton.IsChecked != true)
                {
                    if (fromTimer)
                    {
                        Pause();
                        ShowToast("End of slideshow");
                    }
                    return;
                }
                if (pos >= _order.Count && ShuffleButton.IsChecked == true)
                {
                    BuildOrder(keepCurrent: false);
                    pos = 0;
                }
                else
                {
                    pos = (pos + _order.Count) % _order.Count;
                }
            }
            _orderPos = pos;
            if (_playing) RestartTimer();
            Show(_order[pos], crossfade: true);
            return;
        }

        var next = _index + step;
        if (next < 0 || next >= _items.Count) return;
        Show(next, crossfade: false);
    }

    void GoTo(int index)
    {
        if (_items.Count == 0) return;
        if (_slideshow)
        {
            _orderPos = Math.Max(0, _order.IndexOf(index));
            if (_playing) RestartTimer();
        }
        Show(index, crossfade: false);
    }

    void StartSlideshow()
    {
        if (!_slideshow)
        {
            _slideshow = true;
            BuildOrder(keepCurrent: true);
        }
        SlideBar.Visibility = Visibility.Visible;
        _playing = true;
        RestartTimer();
        UpdatePlayButtons();
        // Slideshows start full screen; resuming one the user has taken out of full screen doesn't force it back.
        if (!_slideshowStartedOnce) EnterFullScreen();
        _slideshowStartedOnce = true;
        Preload();
    }

    void Pause()
    {
        _playing = false;
        _slideTimer.Stop();
        UpdatePlayButtons();
    }

    void TogglePlay()
    {
        if (!_slideshow || !_playing)
        {
            StartSlideshow();
            ShowToast("Playing");
        }
        else
        {
            Pause();
            ShowToast("Paused");
        }
    }

    void RestartTimer()
    {
        _slideTimer.Stop();
        _slideTimer.Interval = TimeSpan.FromSeconds(_vm.Settings.SlideshowIntervalSeconds);
        _slideTimer.Start();
    }

    void UpdatePlayButtons()
    {
        var glyph = _playing ? "\uE769" : "\uE768";
        PlayButton.Content = glyph;
        SlidePlay.Content = glyph;
    }

    void SelectInterval(double seconds)
    {
        var best = Intervals.OrderBy(i => Math.Abs(i - seconds)).First();
        IntervalBox.SelectedIndex = Array.IndexOf(Intervals, best);
    }

    // ------------------------------------------------------------------ chrome

    void UpdateChrome(Photo p)
    {
        FileNameText.Text = p.FileName;
        Title = $"{p.FileName} – PhotoScope";
        PositionText.Text = _slideshow ? $"{_orderPos + 1} of {_order.Count}" : $"{_index + 1} of {_items.Count}";
        var inLibrary = InLibrary(p);
        // Until the library has loaded we can't tell whether a file is in it.
        AddToLibraryButton.Visibility = inLibrary || !_vm.LibraryLoaded.IsCompleted ? Visibility.Collapsed : Visibility.Visible;
        FavButton.IsEnabled = inLibrary;
        Stars.IsEnabled = inLibrary;
        Stars.Opacity = inLibrary ? 1 : 0.4;
        FavButton.ToolTip = inLibrary ? "Favourite (F)" : "Add the photo to your library to favourite it";
        Stars.ToolTip = inLibrary ? "Rating (0–5)" : "Add the photo to your library to rate it";
        FavButton.IsChecked = p.IsFavourite;
        FavButton.Content = p.IsFavourite ? "\uEB52" : "\uEB51";
        for (var i = 0; i < Stars.Children.Count; i++)
        {
            var b = (Button)Stars.Children[i];
            var filled = i < p.Rating;
            b.Content = filled ? "\uE735" : "\uE734";
            b.Foreground = filled ? new SolidColorBrush(Color.FromRgb(0xFF, 0xC8, 0x3D)) : Brushes.White;
        }
        PrevEdge.Visibility = NeighbourIndex(-1) >= 0 ? Visibility.Visible : Visibility.Hidden;
        NextEdge.Visibility = NeighbourIndex(+1) >= 0 ? Visibility.Visible : Visibility.Hidden;
        UpdateInfo(p);
    }

    void UpdateInfo(Photo p)
    {
        InfoName.Text = p.FileName;
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            var row = InfoGrid.RowDefinitions.Count;
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = new SolidColorBrush(Color.FromRgb(0xA8, 0xA8, 0xA8)), Margin = new Thickness(0, 2, 16, 2) };
            var v = new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2) };
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            InfoGrid.Children.Add(l);
            InfoGrid.Children.Add(v);
        }
        Row("Dimensions", p.PixelWidth > 0 ? p.DimensionsText : null);
        Row("File size", p.SizeBytes > 0 ? p.SizeText : null);
        Row("Date taken", p.ModifiedUtc != default || p.DateTaken != null ? p.DateTakenText : null);
        Row("Camera", p.CameraModel);
        if (InLibrary(p))
        {
            Row("Rating", p.Rating > 0 ? new string('★', p.Rating) + new string('☆', 5 - p.Rating) : "Not rated");
            Row("Tags", p.Tags);
        }
        else
        {
            Row("Library", "Not in your library");
        }
        Row("Path", p.Path);
    }

    bool _chromeVisible = true;

    void ShowCursor()
    {
        ForceCursor = false;
        ClearValue(CursorProperty);
    }

    void ShowChrome()
    {
        if (_slideshow) SlideBar.Visibility = Visibility.Visible;
        ShowCursor();
        if (!_chromeVisible)
        {
            _chromeVisible = true;
            foreach (var el in ChromeElements) Fade(el, 1);
        }
        _idleTimer.Stop();
        _idleTimer.Start();
    }

    IEnumerable<UIElement> ChromeElements => [TopBar, SlideBar, PrevEdge, NextEdge];

    static void Fade(UIElement el, double to)
    {
        el.IsHitTestVisible = to > 0;
        el.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 120 : 350)));
    }

    void HideChrome()
    {
        _idleTimer.Stop();
        // Keep the controls up while the mouse is over them.
        if (TopBar.IsMouseOver || SlideBar.IsMouseOver || IntervalBox.IsDropDownOpen) { _idleTimer.Start(); return; }
        _chromeVisible = false;
        foreach (var el in ChromeElements) Fade(el, 0);
        // Only hide the pointer in full screen; in a window it would vanish over the desktop's edge.
        if (IsActive && _fullScreen && IsMouseOver)
        {
            // Hides the pointer over this window only (not over other PhotoScope windows).
            Cursor = Cursors.None;
            ForceCursor = true;
        }
    }

    void ShowToast(string text)
    {
        ToastText.Text = text;
        var anim = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1300) };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(120))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(900))));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(1300))));
        Toast.BeginAnimation(OpacityProperty, anim);
    }

    // ------------------------------------------------------------------ actions

    bool RequireLibrary(string what)
    {
        if (_shown == null) return false;
        if (InLibrary(_shown)) return true;
        if (!_vm.LibraryLoaded.IsCompleted)
        {
            ShowToast("Your library is still loading\u2026");
            return false;
        }
        ShowToast($"Add this photo to your library to {what} it");
        return false;
    }

    void ToggleFavourite()
    {
        if (!RequireLibrary("favourite")) return;
        _vm.ToggleFavourite([_shown!]);
        UpdateChrome(_shown!);
        ShowToast(_shown!.IsFavourite ? "Added to favourites" : "Removed from favourites");
    }

    void SetRating(int rating)
    {
        if (!RequireLibrary("rate")) return;
        _vm.SetRating([_shown!], rating);
        UpdateChrome(_shown!);
        ShowToast(rating == 0 ? "Rating cleared" : $"Rated {new string('★', rating)}");
    }

    void RotateShown(int delta)
    {
        if (_shown == null) return;
        if (InLibrary(_shown)) _vm.Rotate([_shown], delta);
        else _shown.Rotation = ((_shown.Rotation + delta) % 360 + 360) % 360; // just for this viewing
        SetContentSize(_shown, MainImage.Source as BitmapSource);
        FitToScreen();
        UpdateInfo(_shown);
    }

    async void AddShownToLibrary()
    {
        if (_shown is not { } p || InLibrary(p)) return;
        AddToLibraryButton.IsEnabled = false;
        try
        {
            var lib = await _vm.AddFileToLibraryAsync(p.Path);
            if (!IsLoaded || lib == null) return;
            if (p.Rotation != 0 && lib.Rotation == 0) _vm.Rotate([lib], p.Rotation);
            var i = _items.IndexOf(p);
            if (i >= 0) _items[i] = lib;
            if (ReferenceEquals(_shown, p)) _shown = lib;
            UpdateChrome(_items[_index]);
            ShowToast("Added to library");
        }
        catch (Exception ex)
        {
            Log.Error("Add to library from viewer failed", ex);
        }
        finally
        {
            AddToLibraryButton.IsEnabled = true;
        }
    }

    async void DeleteShown()
    {
        if (_shown is not { } shown) return;
        var wasPlaying = _playing;
        if (wasPlaying) Pause();
        ShowCursor();
        IReadOnlyCollection<Photo> removed;
        try
        {
            if (InLibrary(shown)) removed = await _vm.DeleteInteractiveAsync([shown]);
            else removed = await _vm.RecycleLooseFileAsync(shown.Path) ? [shown] : [];
        }
        catch (Exception ex)
        {
            Log.Error("Delete from viewer failed", ex);
            removed = [];
        }
        if (!IsLoaded) return;
        if (removed.Count == 0)
        {
            if (wasPlaying) StartSlideshow();
            return;
        }

        var index = _index;
        foreach (var p in removed)
        {
            _cache.Remove(CacheKey(p));
            // Match by path: the list may hold a different object for the same file by now.
            var at = _items.FindIndex(x => PathComparer.Equals(x.Path, p.Path));
            if (at < 0) continue;
            if (_fileMode && File.Exists(p.Path)) _items[at] = LoosePhoto(p.Path); // removed from library only: still in the folder
            else _items.RemoveAt(at);
        }
        _shown = null;
        _hasImage = false;
        if (_items.Count == 0)
        {
            Close();
            return;
        }
        index = Math.Min(index, _items.Count - 1);
        _index = index;
        BuildOrder(keepCurrent: true);
        Show(index, crossfade: false);
        if (wasPlaying) StartSlideshow();
    }

    // ------------------------------------------------------------------ input

    void OnKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var handled = true;
        switch (key)
        {
            case Key.Escape:
                // Esc leaves full screen first; it closes the viewer only when already windowed.
                if (_fullScreen) ExitFullScreen();
                else Close();
                break;
            case Key.F11: ToggleFullScreen(); break;
            case Key.L when ctrl: OpenLibrary(); break;
            case Key.Left or Key.PageUp or Key.Up: Advance(-1, false); break;
            case Key.Right or Key.PageDown or Key.Down: Advance(+1, false); break;
            case Key.Home: GoTo(_slideshow ? _order[0] : 0); break;
            case Key.End: GoTo(_slideshow ? _order[^1] : _items.Count - 1); break;
            case Key.Space: TogglePlay(); break;
            case Key.I: ToggleInfo(); break;
            case Key.F when !ctrl: ToggleFavourite(); break;
            case Key.R: RotateShown(shift ? -90 : 90); break;
            case Key.Delete: DeleteShown(); break;
            case Key.D0 or Key.NumPad0 when ctrl: FitToScreen(); break;
            case Key.D1 or Key.NumPad1 when ctrl: ActualSize(); break;
            case Key.OemPlus or Key.Add: ZoomAt(ViewportCentre, _scale * 1.25); break;
            case Key.OemMinus or Key.Subtract: ZoomAt(ViewportCentre, _scale / 1.25); break;
            case >= Key.D0 and <= Key.D5 when !ctrl: SetRating(key - Key.D0); break;
            case >= Key.NumPad0 and <= Key.NumPad5 when !ctrl: SetRating(key - Key.NumPad0); break;
            default: handled = false; break;
        }
        if (handled)
        {
            e.Handled = true;
            if (key != Key.Space) ShowChrome();
        }
    }

    void ToggleInfo()
    {
        var show = InfoPanel.Visibility != Visibility.Visible;
        InfoPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        InfoButton.IsChecked = show;
    }

    void OnPrev(object sender, RoutedEventArgs e) => Advance(-1, false);
    void OnNext(object sender, RoutedEventArgs e) => Advance(+1, false);
    void OnFavourite(object sender, RoutedEventArgs e) => ToggleFavourite();
    void OnRotateLeft(object sender, RoutedEventArgs e) => RotateShown(-90);
    void OnRotateRight(object sender, RoutedEventArgs e) => RotateShown(90);
    void OnFit(object sender, RoutedEventArgs e) => FitToScreen();
    void OnActualSize(object sender, RoutedEventArgs e) => ActualSize();
    void OnInfo(object sender, RoutedEventArgs e) => ToggleInfo();
    void OnPlayPause(object sender, RoutedEventArgs e) => TogglePlay();
    void OnFullScreen(object sender, RoutedEventArgs e) => ToggleFullScreen();
    void OnOpenLibrary(object sender, RoutedEventArgs e) => OpenLibrary();

    /// <summary>Shows the library window (selecting this photo if it's in the library).</summary>
    void OpenLibrary()
    {
        if (_fullScreen) ExitFullScreen(); // otherwise the library would open hidden behind a full-screen viewer
        ShowCursor();
        (Application.Current as App)?.ShowLibrary(_shown);
    }
    void OnAddToLibrary(object sender, RoutedEventArgs e) => AddShownToLibrary();
    void OnClose(object sender, RoutedEventArgs e) => Close();

    void OnIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IntervalBox.SelectedItem is ComboBoxItem { Tag: double seconds })
        {
            _vm.Settings.SlideshowIntervalSeconds = seconds;
            if (_playing) RestartTimer();
        }
        Focus();
    }

    void OnShuffle(object sender, RoutedEventArgs e)
    {
        _vm.Settings.SlideshowShuffle = ShuffleButton.IsChecked == true;
        BuildOrder(keepCurrent: true);
        UpdateChrome(_items[_index]);
        Preload();
    }

    void OnLoop(object sender, RoutedEventArgs e)
    {
        _vm.Settings.SlideshowLoop = LoopButton.IsChecked == true;
        UpdateChrome(_items[_index]);
        Preload();
    }
}
