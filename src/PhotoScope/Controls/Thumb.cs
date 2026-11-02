using System.Windows;
using System.Windows.Controls;
using PhotoScope.Models;
using PhotoScope.Services;

namespace PhotoScope.Controls;

/// <summary>
/// Attached behaviour for an Image inside a virtualised item: when the item's photo, the
/// requested size or the photo's thumbnail version changes, the previous request is cancelled
/// and a new one queued. Recycled containers therefore never show a stale thumbnail.
/// </summary>
public static class Thumb
{
    public static readonly DependencyProperty PhotoProperty = DependencyProperty.RegisterAttached(
        "Photo", typeof(Photo), typeof(Thumb), new PropertyMetadata(null, OnInputChanged));
    public static Photo? GetPhoto(DependencyObject d) => (Photo?)d.GetValue(PhotoProperty);
    public static void SetPhoto(DependencyObject d, Photo? v) => d.SetValue(PhotoProperty, v);

    public static readonly DependencyProperty DecodeSizeProperty = DependencyProperty.RegisterAttached(
        "DecodeSize", typeof(int), typeof(Thumb), new PropertyMetadata(200, OnInputChanged));
    public static int GetDecodeSize(DependencyObject d) => (int)d.GetValue(DecodeSizeProperty);
    public static void SetDecodeSize(DependencyObject d, int v) => d.SetValue(DecodeSizeProperty, v);

    public static readonly DependencyProperty VersionProperty = DependencyProperty.RegisterAttached(
        "Version", typeof(int), typeof(Thumb), new PropertyMetadata(0, OnInputChanged));
    public static int GetVersion(DependencyObject d) => (int)d.GetValue(VersionProperty);
    public static void SetVersion(DependencyObject d, int v) => d.SetValue(VersionProperty, v);

    /// <summary>True while the image has no thumbnail to show (drives the loading/placeholder look).</summary>
    public static readonly DependencyProperty IsEmptyProperty = DependencyProperty.RegisterAttached(
        "IsEmpty", typeof(bool), typeof(Thumb), new PropertyMetadata(true));
    public static bool GetIsEmpty(DependencyObject d) => (bool)d.GetValue(IsEmptyProperty);
    static void SetIsEmpty(DependencyObject d, bool v) => d.SetValue(IsEmptyProperty, v);

    static readonly DependencyProperty RequestProperty = DependencyProperty.RegisterAttached(
        "Request", typeof(ThumbnailLoader.Request), typeof(Thumb), new PropertyMetadata(null));

    static readonly DependencyProperty ShownPhotoProperty = DependencyProperty.RegisterAttached(
        "ShownPhoto", typeof(Photo), typeof(Thumb), new PropertyMetadata(null));

    static void OnInputChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image) return;
        if (e.Property == PhotoProperty && e.OldValue == null)
        {
            image.Unloaded -= OnUnloaded;
            image.Unloaded += OnUnloaded;
        }
        Reload(image, keepCurrent: e.Property != PhotoProperty && e.Property != VersionProperty);
    }

    static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            ThumbnailLoader.Instance?.Cancel((ThumbnailLoader.Request?)image.GetValue(RequestProperty));
            image.ClearValue(RequestProperty);
            // If the container comes back later, Loaded doesn't re-trigger a load, so do it here.
            image.Loaded -= OnLoaded;
            image.Loaded += OnLoaded;
        }
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Image image)
        {
            image.Loaded -= OnLoaded;
            if (image.Source == null) Reload(image, keepCurrent: false);
        }
    }

    static void Reload(Image image, bool keepCurrent)
    {
        var loader = ThumbnailLoader.Instance;
        if (loader == null) return;

        loader.Cancel((ThumbnailLoader.Request?)image.GetValue(RequestProperty));
        image.ClearValue(RequestProperty);

        var photo = GetPhoto(image);
        if (photo == null)
        {
            image.Source = null;
            SetIsEmpty(image, true);
            return;
        }

        // Keep the old picture while a bigger one loads for the same photo (no flicker on resize).
        if (!keepCurrent || image.GetValue(ShownPhotoProperty) != photo)
        {
            image.Source = null;
            SetIsEmpty(image, true);
        }

        var size = Math.Clamp(GetDecodeSize(image), 32, ThumbnailCache.MaxSide);
        var request = loader.Load(photo, size, bmp =>
        {
            if (GetPhoto(image) != photo) return;
            image.ClearValue(RequestProperty);
            if (bmp == null && image.Source != null && image.GetValue(ShownPhotoProperty) == photo) return;
            image.Source = bmp;
            image.SetValue(ShownPhotoProperty, photo);
            SetIsEmpty(image, bmp == null);
        });
        if (request != null) image.SetValue(RequestProperty, request);
    }
}
