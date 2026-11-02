using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

public partial class MainWindow
{
    readonly MainViewModel _vm;
    bool _restoringSelection;

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
                _vm.DeleteInteractive(_vm.SelectedPhotos);
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

    // ------------------------------------------------------------------ drag and drop import

    static bool IsFileDrop(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    void OnWindowDragOver(object sender, DragEventArgs e)
    {
        if (IsFileDrop(e))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (e.Handled || !IsFileDrop(e)) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
        {
            e.Handled = true;
            // Let the drag operation finish before showing a modal dialog.
            Dispatcher.BeginInvoke(() => _vm.ShowImportDialog(paths));
        }
    }
}
