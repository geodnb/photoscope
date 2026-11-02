using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.ViewModels;

namespace PhotoScope.Views;

public partial class SettingsWindow
{
    static readonly int[] CacheLimits = [256, 512, 1024, 2048, 5120, 10240, 20480];

    readonly MainViewModel _vm;
    readonly AppSettings _s;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        _s = vm.Settings;

        ThemeSystem.IsChecked = _s.Theme == ThemeMode.System;
        ThemeLight.IsChecked = _s.Theme == ThemeMode.Light;
        ThemeDark.IsChecked = _s.Theme == ThemeMode.Dark;

        LibraryFolderBox.Text = _s.LibraryFolder;
        ModeReference.IsChecked = _s.DefaultImportMode == ImportMode.ReferenceInPlace;
        ModeCopy.IsChecked = _s.DefaultImportMode == ImportMode.CopyToLibrary;

        IntervalSlider.Value = Math.Clamp(Math.Round(_s.SlideshowIntervalSeconds), 1, 60);
        ShuffleBox.IsChecked = _s.SlideshowShuffle;
        LoopBox.IsChecked = _s.SlideshowLoop;

        foreach (var mb in CacheLimits)
            CacheLimitBox.Items.Add(new ComboBoxItem { Content = mb >= 1024 ? $"{mb / 1024} GB" : $"{mb} MB", Tag = mb });
        var nearest = CacheLimits.OrderBy(x => Math.Abs(x - _s.ThumbnailCacheLimitMB)).First();
        CacheLimitBox.SelectedIndex = Array.IndexOf(CacheLimits, nearest);

        AboutText.Text = $"PhotoScope {typeof(App).Assembly.GetName().Version?.ToString(3)}\n" +
                         $"Library and settings: {AppPaths.Root}\n" +
                         $"{_vm.LibraryCount:N0} photos in the library.";
        Loaded += async (_, _) => await MeasureCacheAsync();
    }

    async Task MeasureCacheAsync()
    {
        var bytes = await Task.Run(_vm.Thumbs.GetSizeBytes);
        CacheUsage.Text = $"Currently using {Photo.FormatBytes(bytes)}";
    }

    void OnIntervalChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalText != null) IntervalText.Text = $"{IntervalSlider.Value:0} sec";
    }

    void OnBrowseLibrary(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Choose the library folder" };
        if (Directory.Exists(LibraryFolderBox.Text)) dlg.InitialDirectory = LibraryFolderBox.Text;
        if (dlg.ShowDialog(this) == true) LibraryFolderBox.Text = dlg.FolderName;
    }

    async void OnClearCache(object sender, RoutedEventArgs e)
    {
        if (!Dialogs.Confirm("Clear the thumbnail cache?",
                "All cached thumbnails will be deleted and rebuilt as you browse. Your photos are not affected.", "Clear cache"))
            return;
        ClearCacheButton.IsEnabled = false;
        CacheUsage.Text = "Clearing…";
        try
        {
            await _vm.ClearThumbnailCacheAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("Clearing the cache failed", ex);
        }
        ClearCacheButton.IsEnabled = true;
        await MeasureCacheAsync();
    }

    void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try { FileOps.OpenFolder(AppPaths.Logs); }
        catch (Exception ex) { Dialogs.Error("Couldn't open the folder", Friendly.Describe(ex)); }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var error = AppSettings.ValidateLibraryFolder(LibraryFolderBox.Text);
        if (error != null)
        {
            LibraryFolderError.Text = error;
            LibraryFolderError.Visibility = Visibility.Visible;
            return;
        }

        _s.Theme = ThemeLight.IsChecked == true ? ThemeMode.Light : ThemeDark.IsChecked == true ? ThemeMode.Dark : ThemeMode.System;
        _s.LibraryFolder = Path.GetFullPath(LibraryFolderBox.Text.Trim());
        _s.DefaultImportMode = ModeCopy.IsChecked == true ? ImportMode.CopyToLibrary : ImportMode.ReferenceInPlace;
        _s.SlideshowIntervalSeconds = IntervalSlider.Value;
        _s.SlideshowShuffle = ShuffleBox.IsChecked == true;
        _s.SlideshowLoop = LoopBox.IsChecked == true;
        if (CacheLimitBox.SelectedItem is ComboBoxItem { Tag: int mb }) _s.ThumbnailCacheLimitMB = mb;
        _s.Normalize();
        DialogResult = true;
    }
}
