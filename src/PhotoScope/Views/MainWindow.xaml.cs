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
        s.SidebarWidth = SidebarColumn.ActualWidth;
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
