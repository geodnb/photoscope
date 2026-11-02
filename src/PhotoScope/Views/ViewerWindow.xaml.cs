using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

/// <summary>
/// Full-screen viewer and slideshow. Zoom/pan maths lives here because it is purely visual;
/// library changes (favourite, rating, rotation, delete) go through the main view model.
/// </summary>
public partial class ViewerWindow
{
    const double MaxZoom = 16;
    static readonly double[] Intervals = [2, 3, 5, 8, 10, 15, 20, 30, 60];

    readonly MainViewModel _vm;
    readonly List<Photo> _items;
    readonly Dictionary<long, Task<Decoded>> _cache = new();
    readonly SemaphoreSlim _decodeGate = new(2);
    readonly DispatcherTimer _slideTimer = new();
    readonly DispatcherTimer _idleTimer = new() { Interval = TimeSpan.FromSeconds(2.5) };
    readonly DispatcherTimer _loadingTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    readonly Random _random = new();

    int _index;
    int _showVersion;
    Photo? _shown;
    bool _hasImage;
    List<int> _order = new();
    int _orderPos;
    bool _slideshow;
    bool _playing;

    // Zoom state. Content is measured in DIPs such that scale 1 == one image pixel per screen pixel.
    double _contentW, _contentH, _scale = 1, _tx, _ty;
    bool _fit = true;
    double _dpi = 1;
    Point? _panStart;
    double _panTx, _panTy;
    int _wheelAccumulator;

    sealed record Decoded(BitmapSource? Bitmap, string? Error, bool Missing);

    public Photo? LastShown => _shown;

    public ViewerWindow(MainViewModel vm, IReadOnlyList<Photo> items, int startIndex, bool slideshow)
    {
        InitializeComponent();
        if (App.Offscreen) { App.SendToBack(this); WindowStartupLocation = WindowStartupLocation.Manual; Left = 0; Top = 0; App.AddSnapshotHook(this, Root); }
        _vm = vm;
        _items = items.ToList();
        _index = Math.Clamp(startIndex, 0, _items.Count - 1);

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

        Loaded += OnLoaded;
        Closing += OnClosing;
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

    void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (App.Offscreen)
        {
            // Automated UI checks: stay behind other windows and never maximise over the desktop.
            WindowState = WindowState.Normal;
            Width = 1600; Height = 900;
        }
        Log.Info($"Viewer opened: {_items.Count} photos, start {_index}, slideshow {_slideshow}");
        if (!App.Offscreen) WindowState = WindowState.Maximized;
        _dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Focus();
        Show(_index, crossfade: false);
        if (_slideshow) StartSlideshow();
        ShowChrome();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        Log.Info("Viewer closed");
        _slideTimer.Stop();
        _idleTimer.Stop();
        _loadingTimer.Stop();
        Mouse.OverrideCursor = null;
        SettingsService.Save();
    }

    // ------------------------------------------------------------------ loading

    Task<Decoded> GetDecoded(Photo p)
    {
        if (_cache.TryGetValue(p.Id, out var existing)) return existing;
        var task = Task.Run(async () =>
        {
            await _decodeGate.WaitAsync();
            try
            {
                if (!File.Exists(p.Path)) return new Decoded(null, "This file can't be found. It may have been moved, renamed or deleted.", true);
                var bmp = ImageDecoder.Decode(p.Path, 0, p.PixelWidth, p.PixelHeight, p.Orientation);
                return new Decoded(bmp, null, false);
            }
            catch (Exception ex)
            {
                Log.Warn($"Viewer could not decode {p.Path}", ex);
                return new Decoded(null, Friendly.DescribeImageError(ex, p.Path), false);
            }
            finally
            {
                _decodeGate.Release();
            }
        });
        _cache[p.Id] = task;
        return task;
    }

    /// <summary>Keeps the current photo and its neighbours decoded; drops everything else.</summary>
    void Preload()
    {
        var keep = new HashSet<long>();
        foreach (var offset in new[] { 0, 1, -1, 2 })
        {
            var i = NeighbourIndex(offset);
            if (i < 0) continue;
            keep.Add(_items[i].Id);
            GetDecoded(_items[i]);
        }
        foreach (var id in _cache.Keys.Where(id => !keep.Contains(id)).ToList())
            _cache.Remove(id);
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

        var task = GetDecoded(p);
        if (!task.IsCompleted)
        {
            // Show the cached thumbnail at once so navigation feels instant.
            var thumb = _vm.Thumbs.TryLoad(p, ThumbnailCache.MaxSide);
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

        if (crossfade && _hasImage) StartCrossfade();
        Display(p, result.Bitmap, result.Error, result.Missing, isPlaceholder: false);
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
        PositionText.Text = _slideshow ? $"{_orderPos + 1} of {_order.Count}" : $"{_index + 1} of {_items.Count}";
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
        Row("Dimensions", p.DimensionsText);
        Row("File size", p.SizeText);
        Row("Date taken", p.DateTakenText);
        Row("Camera", p.CameraModel);
        Row("Rating", p.Rating > 0 ? new string('\u2605', p.Rating) + new string('\u2606', 5 - p.Rating) : "Not rated");
        Row("Tags", p.Tags);
        Row("Path", p.Path);
    }

    bool _chromeVisible = true;

    void ShowChrome()
    {
        if (_slideshow) SlideBar.Visibility = Visibility.Visible;
        Mouse.OverrideCursor = null;
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
        if (IsActive) Mouse.OverrideCursor = Cursors.None;
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

    void ToggleFavourite()
    {
        if (_shown == null) return;
        _vm.ToggleFavourite([_shown]);
        UpdateChrome(_shown);
        ShowToast(_shown.IsFavourite ? "Added to favourites" : "Removed from favourites");
    }

    void SetRating(int rating)
    {
        if (_shown == null) return;
        _vm.SetRating([_shown], rating);
        UpdateChrome(_shown);
        ShowToast(rating == 0 ? "Rating cleared" : $"Rated {new string('\u2605', rating)}");
    }

    void RotateShown(int delta)
    {
        if (_shown == null) return;
        _vm.Rotate([_shown], delta);
        SetContentSize(_shown, MainImage.Source as BitmapSource);
        FitToScreen();
        UpdateInfo(_shown);
    }

    void DeleteShown()
    {
        if (_shown == null) return;
        var wasPlaying = _playing;
        if (wasPlaying) Pause();
        Mouse.OverrideCursor = null;
        var removed = _vm.DeleteInteractive([_shown]);
        if (removed.Count == 0)
        {
            if (wasPlaying) StartSlideshow();
            return;
        }

        var index = _index;
        foreach (var p in removed)
        {
            _cache.Remove(p.Id);
            _items.Remove(p);
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
            case Key.Escape: Close(); break;
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
