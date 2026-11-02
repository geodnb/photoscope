using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoScope.Controls;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

public partial class MainWindow
{
    const string PhotoFormat = "PhotoScope.Photos";
    const string PlaylistFormat = "PhotoScope.PlaylistItems";

    readonly MainViewModel _vm;
    bool _restoringSelection;
    InsertionMarker? _marker;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        RestoreLayout();
        App.AddSnapshotHook(this, RootGrid);

        Loaded += (_, _) =>
        {
            _vm.SetDpiScale(VisualTreeHelper.GetDpi(this).DpiScaleX);
            Gallery.Focus();
        };
        DpiChanged += (_, e) => _vm.SetDpiScale(e.NewDpi.DpiScaleX);
        Closing += OnClosing;
        PreviewKeyDown += OnWindowPreviewKeyDown;
        DragOver += OnWindowDragOver;
        Drop += OnWindowDrop;
        Gallery.PreviewMouseWheel += OnGalleryPreviewMouseWheel;

        // Drag photos from the gallery into a playlist; drag within the playlist to reorder.
        _ = new ListDragSource(Gallery, PhotoFormat);
        _ = new ListDragSource(PlaylistStrip, PlaylistFormat);
        PlaylistStrip.DragOver += OnStripDragOver;
        PlaylistStrip.DragLeave += (_, _) => RemoveMarker();
        PlaylistStrip.Drop += OnStripDrop;
        PlaylistStrip.KeyDown += OnStripKeyDown;
        PlaylistStrip.MouseDoubleClick += OnStripDoubleClick;
        PlaylistList.DragOver += OnPlaylistListDragOver;
        PlaylistList.Drop += OnPlaylistListDrop;
        PlaylistList.PreviewMouseRightButtonDown += (_, e) =>
        {
            if (ListDragSource.ItemAt(PlaylistList, e.OriginalSource as DependencyObject) is { } item) item.IsSelected = true;
        };
        PlaylistList.SelectionChanged += (_, _) =>
        {
            if (PlaylistList.IsKeyboardFocusWithin || PlaylistList.IsMouseOver) _vm.IsPlaylistVisible = true;
        };
        Gallery.ContextMenuOpening += OnGalleryContextMenuOpening;
        TagsBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { CommitTags(); e.Handled = true; }
            else if (e.Key == Key.Escape) { TagsBox.Text = _vm.SelectedPhoto?.Tags ?? ""; Gallery.Focus(); e.Handled = true; }
        };
        TagsBox.GotKeyboardFocus += (_, _) => _tagsFor = _vm.SelectedPhoto;
        TagsBox.LostKeyboardFocus += (_, _) => { if (_tagsFor != null) CommitTags(); };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedPhoto)) return;
            if (_tagsFor != null && _tagsFor != _vm.SelectedPhoto) CommitTags();
            TagsBox.Text = _vm.SelectedPhoto?.Tags ?? "";
            if (TagsBox.IsKeyboardFocusWithin) _tagsFor = _vm.SelectedPhoto;
        };
        Gallery.ContextMenu = new ContextMenu();

        _vm.ViewReset += OnViewReset;
        _vm.PropertyChanged += OnVmPropertyChanged;
        _vm.RevealRequested += photo =>
        {
            Gallery.SelectedItem = photo;
            Gallery.ScrollIntoView(photo);
            Gallery.Focus();
        };
    }

    // ------------------------------------------------------------------ layout persistence

    void RestoreLayout()
    {
        var s = _vm.Settings;
        Width = s.WindowWidth;
        Height = s.WindowHeight;
        if (s.WindowLeft is { } left && s.WindowTop is { } top && IsOnScreen(left, top, s.WindowWidth, s.WindowHeight))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        SidebarColumn.Width = new GridLength(s.SidebarWidth);
        _vm.IsPreviewVisible = s.PreviewVisible;
        _vm.IsPlaylistVisible = s.PlaylistVisible;
        ApplyPanelVisibility();
    }

    void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsPreviewVisible) or nameof(MainViewModel.IsPlaylistVisible))
        {
            SaveLayout();
            ApplyPanelVisibility();
        }
    }

    /// <summary>Shows or hides the docked panes, restoring their saved sizes.</summary>
    void ApplyPanelVisibility()
    {
        var s = _vm.Settings;
        var preview = _vm.IsPreviewVisible;
        PreviewPane.Visibility = preview ? Visibility.Visible : Visibility.Collapsed;
        PreviewSplitter.Visibility = PreviewPane.Visibility;
        PreviewColumn.MinWidth = preview ? 220 : 0;
        PreviewColumn.Width = preview ? new GridLength(s.PreviewWidth) : new GridLength(0);

        var playlist = _vm.IsPlaylistVisible && PlaylistPane.Child != null;
        PlaylistPane.Visibility = playlist ? Visibility.Visible : Visibility.Collapsed;
        PlaylistSplitter.Visibility = PlaylistPane.Visibility;
        PlaylistRow.MinHeight = playlist ? 120 : 0;
        PlaylistRow.Height = playlist ? new GridLength(s.PlaylistHeight) : new GridLength(0);
    }

    static bool IsOnScreen(double left, double top, double width, double height)
    {
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var title = new Rect(left + 40, top, Math.Max(width - 80, 40), 32);
        return screen.IntersectsWith(title);
    }

    void SaveLayout()
    {
        var s = _vm.Settings;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty && double.IsFinite(bounds.Width))
        {
            s.WindowLeft = bounds.Left;
            s.WindowTop = bounds.Top;
            s.WindowWidth = bounds.Width;
            s.WindowHeight = bounds.Height;
        }
        s.WindowMaximized = WindowState == WindowState.Maximized;
        if (SidebarColumn.ActualWidth > 0) s.SidebarWidth = SidebarColumn.ActualWidth;
        if (PreviewPane.Visibility == Visibility.Visible && PreviewColumn.ActualWidth > 0) s.PreviewWidth = PreviewColumn.ActualWidth;
        if (PlaylistPane.Visibility == Visibility.Visible && PlaylistRow.ActualHeight > 0) s.PlaylistHeight = PlaylistRow.ActualHeight;
    }

    void OnSplitterDragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        SaveLayout();
        SettingsService.Save();
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveLayout();
        SettingsService.Save();
    }

    // ------------------------------------------------------------------ gallery selection

    void OnGallerySelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_restoringSelection) return;
        var primary = e.AddedItems.Count > 0 ? e.AddedItems[^1] as Photo : null;
        if (primary == null && _vm.SelectedPhoto != null && Gallery.SelectedItems.Contains(_vm.SelectedPhoto))
            primary = _vm.SelectedPhoto;
        _vm.SetSelection(Gallery.SelectedItems, primary ?? Gallery.SelectedItem as Photo);
    }

    void OnViewReset(IReadOnlyList<Photo> keep)
    {
        _restoringSelection = true;
        try
        {
            Gallery.SelectItems(keep);
        }
        finally
        {
            _restoringSelection = false;
        }
        _vm.SetSelection(Gallery.SelectedItems, keep.Contains(_vm.SelectedPhoto!) ? _vm.SelectedPhoto : keep.FirstOrDefault());
        if (_vm.SelectedPhoto != null) Gallery.ScrollIntoView(_vm.SelectedPhoto);
    }

    // ------------------------------------------------------------------ gallery input

    void OnGalleryDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (ItemsControl.ContainerFromElement(Gallery, (DependencyObject)e.OriginalSource) is ListBoxItem { DataContext: Photo p })
        {
            e.Handled = true;
            _vm.OpenViewerCommand.Execute(p);
        }
    }

    void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _vm.SelectedPhoto is { } p)
        {
            e.Handled = true;
            _vm.OpenViewerCommand.Execute(p);
        }
    }

    void OnGalleryKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) || Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var handled = true;
        switch (e.Key)
        {
            case Key.Enter:
                _vm.OpenViewerCommand.Execute(_vm.SelectedPhoto);
                break;
            case Key.F:
                _vm.ToggleFavouriteSelectedCommand.Execute(null);
                break;
            case >= Key.D0 and <= Key.D5:
                _vm.SetRatingSelectedCommand.Execute(e.Key - Key.D0);
                break;
            case >= Key.NumPad0 and <= Key.NumPad5:
                _vm.SetRatingSelectedCommand.Execute(e.Key - Key.NumPad0);
                break;
            case Key.Delete:
                _vm.DeleteSelectedCommand.Execute(null);
                break;
            case Key.F2:
                _vm.RenameSelectedCommand.Execute(null);
                break;
            default:
                handled = false;
                break;
        }
        e.Handled = handled;
    }

    // ------------------------------------------------------------------ input

    void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        if (ctrl && e.Key == Key.F)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (ctrl && e.Key == Key.I)
        {
            _vm.ImportCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SearchBox.IsKeyboardFocusWithin)
        {
            if (_vm.SearchText.Length > 0) _vm.SearchText = "";
            else Gallery.Focus();
            e.Handled = true;
        }
    }

    void OnGalleryPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) return;
        _vm.ThumbnailSize = Math.Clamp(_vm.ThumbnailSize + (e.Delta > 0 ? 20 : -20), 80, 320);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ playlists: drag and drop

    List<Photo> PhotosFrom(IDataObject data, string format) =>
        data.GetData(format) is long[] ids ? ids.Select(_vm.FindPhoto).OfType<Photo>().ToList() : [];

    void OnStripDragOver(object sender, DragEventArgs e)
    {
        var internalMove = e.Data.GetDataPresent(PlaylistFormat);
        var fromGallery = e.Data.GetDataPresent(PhotoFormat);
        if (!internalMove && !fromGallery)
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }
        e.Effects = internalMove ? DragDropEffects.Move : DragDropEffects.Copy;
        _marker ??= InsertionMarker.Attach(PlaylistStrip);
        InsertionMarker.IndexAt(PlaylistStrip, e.GetPosition(PlaylistStrip), _marker);
        AutoScrollStrip(e.GetPosition(PlaylistStrip));
        e.Handled = true;
    }

    void AutoScrollStrip(Point p)
    {
        if (FindScrollViewer(PlaylistStrip) is not { } sv) return;
        if (p.X < 40) sv.ScrollToHorizontalOffset(sv.HorizontalOffset - 20);
        else if (p.X > PlaylistStrip.ActualWidth - 40) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + 20);
    }

    static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
        {
            var child = VisualTreeHelper.GetChild(d, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } found) return found;
        }
        return null;
    }

    void RemoveMarker()
    {
        _marker?.Detach();
        _marker = null;
    }

    void OnStripDrop(object sender, DragEventArgs e)
    {
        var index = InsertionMarker.IndexAt(PlaylistStrip, e.GetPosition(PlaylistStrip), null);
        RemoveMarker();
        e.Handled = true;
        if (e.Data.GetDataPresent(PlaylistFormat))
        {
            _vm.MovePlaylistItems(PhotosFrom(e.Data, PlaylistFormat), index);
            return;
        }
        var photos = PhotosFrom(e.Data, PhotoFormat);
        if (photos.Count == 0) return;
        Dispatcher.BeginInvoke(() =>
        {
            var pl = _vm.SelectedPlaylist ?? _vm.NewPlaylist();
            if (pl != null) _vm.AddToPlaylist(pl, photos, _vm.SelectedPlaylist == pl ? index : -1);
        });
    }

    void OnPlaylistListDragOver(object sender, DragEventArgs e)
    {
        var target = ListDragSource.ItemAt(PlaylistList, e.OriginalSource as DependencyObject);
        e.Effects = e.Data.GetDataPresent(PhotoFormat) && target != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnPlaylistListDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (ListDragSource.ItemAt(PlaylistList, e.OriginalSource as DependencyObject)?.DataContext is not Playlist pl) return;
        var photos = PhotosFrom(e.Data, PhotoFormat);
        if (photos.Count == 0) return;
        var added = _vm.AddToPlaylist(pl, photos);
        _vm.ShowBanner("playlist", BannerKind.Success,
            (added == 1 ? "Added 1 photo" : $"Added {added:N0} photos") + $" to \u201C{pl.Name}\u201D."
            + (photos.Count > added ? $" {photos.Count - added:N0} already in it." : ""));
    }

    void OnStripKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete && PlaylistStrip.SelectedItems.Count > 0)
        {
            _vm.RemoveFromPlaylistCommand.Execute(PlaylistStrip.SelectedItems.Cast<Photo>().ToList());
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && PlaylistStrip.SelectedItem is Photo p)
        {
            _vm.PlayPlaylistCommand.Execute(p);
            e.Handled = true;
        }
    }

    void OnStripDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ListDragSource.ItemAt(PlaylistStrip, e.OriginalSource as DependencyObject)?.DataContext is Photo p)
        {
            e.Handled = true;
            _vm.PlayPlaylistCommand.Execute(p);
        }
    }

    Photo? _tagsFor;

    void CommitTags()
    {
        // Tags apply to the photo they were typed for, even if the selection moved on.
        var p = _tagsFor ?? _vm.SelectedPhoto;
        _tagsFor = null;
        if (p != null && TagsBox.Text != p.Tags) _vm.SetTags(p, TagsBox.Text);
        if (_vm.SelectedPhoto != null) TagsBox.Text = _vm.SelectedPhoto.Tags;
    }

    // ------------------------------------------------------------------ gallery context menu

    void OnGalleryContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        // Right-clicking an unselected photo selects just that photo first.
        if (ListDragSource.ItemAt(Gallery, e.OriginalSource as DependencyObject) is { } item && !item.IsSelected)
        {
            Gallery.SelectedItems.Clear();
            item.IsSelected = true;
        }
        if (_vm.SelectedPhotos.Count == 0)
        {
            e.Handled = true;
            return;
        }
        Gallery.ContextMenu = GalleryMenu.Build(_vm);
    }

    // ------------------------------------------------------------------ drag and drop import

    static bool IsFileDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    void OnWindowDragOver(object sender, DragEventArgs e)
    {
        // Only files from outside can be dropped on the window itself (to import them).
        e.Effects = IsFileDrop(e) && !e.Data.GetDataPresent(PhotoFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || !IsFileDrop(e) || e.Data.GetDataPresent(PhotoFormat)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            e.Handled = true;
            // Let the drag operation finish before showing a modal dialog.
            Dispatcher.BeginInvoke(() => _vm.ShowImportDialog(paths));
        }
    }
}
