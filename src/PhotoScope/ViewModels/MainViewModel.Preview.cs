using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoScope.Models;
using PhotoScope.Services;
using PhotoScope.Views;

namespace PhotoScope.ViewModels;

/// <summary>Preview pane and full viewer entry points.</summary>
public sealed partial class MainViewModel
{
    const int PreviewMaxSide = 1600;
    int _previewVersion;

    [ObservableProperty] private ImageSource? _previewImage;
    [ObservableProperty] private bool _isPreviewLoading;
    [ObservableProperty] private string? _previewError;
    [ObservableProperty] private string _selectionSummary = "";
    [ObservableProperty] private bool _isPreviewVisible = true;
    [ObservableProperty] private bool _isPlaylistVisible = true;

    partial void OnIsPreviewVisibleChanged(bool value) => Settings.PreviewVisible = value;
    partial void OnIsPlaylistVisibleChanged(bool value) => Settings.PlaylistVisible = value;

    partial void OnSelectedPhotoChanged(Photo? value) => _ = LoadPreviewAsync(value);

    partial void OnSelectedCountChanged(int value)
    {
        if (value <= 1)
        {
            SelectionSummary = "";
            return;
        }
        var total = SelectedPhotos.Sum(p => p.SizeBytes);
        SelectionSummary = $"{value:N0} photos selected · {Photo.FormatBytes(total)}";
    }

    async Task LoadPreviewAsync(Photo? p)
    {
        var version = ++_previewVersion;
        PreviewError = null;
        if (p == null)
        {
            PreviewImage = null;
            IsPreviewLoading = false;
            return;
        }

        // Show the cached thumbnail straight away, then swap in a sharper image.
        PreviewImage = null;
        IsPreviewLoading = true;
        try
        {
            var thumb = await Task.Run(() => Thumbs.TryLoad(p, ThumbnailCache.MaxSide));
            if (version != _previewVersion) return;
            PreviewImage = thumb;

            await Task.Delay(90); // don't decode every photo while arrowing through the grid
            if (version != _previewVersion) return;

            if (p.IsMissing)
            {
                PreviewError = "This file can't be found. It may have been moved, renamed or deleted.";
                return;
            }
            if (p.DecodeError != null)
            {
                PreviewError = p.DecodeError;
                return;
            }

            var bmp = await Task.Run(() => ImageDecoder.Decode(p.Path, PreviewMaxSide, p.PixelWidth, p.PixelHeight, p.Orientation));
            if (version == _previewVersion) PreviewImage = bmp;
        }
        catch (Exception ex)
        {
            Log.Warn($"Preview failed for {p.Path}", ex);
            if (version == _previewVersion)
            {
                PreviewError = Friendly.DescribeImageError(ex, p.Path);
                if (!File.Exists(p.Path)) PreviewError = "This file can't be found. It may have been moved, renamed or deleted.";
            }
        }
        finally
        {
            if (version == _previewVersion) IsPreviewLoading = false;
        }
    }

    [RelayCommand]
    void TogglePreview() => IsPreviewVisible = !IsPreviewVisible;

    [RelayCommand]
    void TogglePlaylistPanel() => IsPlaylistVisible = !IsPlaylistVisible;

    // ------------------------------------------------------------------ full viewer

    [RelayCommand]
    void OpenViewer(Photo? photo)
    {
        photo ??= SelectedPhoto;
        if (photo == null || Photos.Count == 0) return;
        ShowViewer(Photos.ToList(), photo, slideshow: false);
    }

    /// <summary>Slideshow of the current gallery view, starting at the selected photo.</summary>
    [RelayCommand]
    void Slideshow()
    {
        if (Photos.Count == 0) return;
        var start = SelectedPhotos.Count == 1 ? SelectedPhoto : null;
        // With several photos selected, show just those.
        var list = SelectedPhotos.Count > 1 ? Photos.Where(SelectedPhotos.Contains).ToList() : Photos.ToList();
        ShowViewer(list, start ?? list[0], slideshow: true);
    }

    public void ShowViewer(IReadOnlyList<Photo> list, Photo start, bool slideshow)
    {
        if (list.Count == 0) return;
        var index = Math.Max(0, list.ToList().IndexOf(start));
        var viewer = new ViewerWindow(this, list, index, slideshow) { Owner = Application.Current.MainWindow };
        viewer.ShowDialog();
        if (viewer.LastShown is { } last && _byId.ContainsKey(last.Id) && Photos.Contains(last))
            RevealRequested?.Invoke(last);
    }
}
