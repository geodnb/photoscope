using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoScope.Services;

public sealed record ImageInfo(int PixelWidth, int PixelHeight, int Orientation, DateTime? DateTaken, string? CameraModel);

/// <summary>
/// All image decoding goes through here, using WPF's built-in WIC codecs. Every method can be
/// called from a background thread; returned bitmaps are frozen. Files are opened read-only
/// with sharing, so PhotoScope never locks or modifies an original.
/// </summary>
public static class ImageDecoder
{
    public static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".bmp", ".dib", ".gif", ".tif", ".tiff", ".ico",
        ".webp", ".heic", ".heif", ".avif", ".jxr", ".wdp",
    };

    static readonly HashSet<string> JpegExtensions = new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".jpeg", ".jpe", ".jfif" };
    static readonly HashSet<string> AlphaExtensions = new(StringComparer.OrdinalIgnoreCase) { ".png", ".gif", ".ico", ".webp", ".avif", ".tif", ".tiff" };

    /// <summary>Largest side used when decoding for the full viewer.</summary>
    public const int MaxViewerSide = 8192;

    /// <summary>Formats other than JPEG decode the whole image before scaling, so cap their size.</summary>
    const long MaxPixelsNonJpeg = 180_000_000;

    public static bool IsSupported(string path) => Extensions.Contains(Path.GetExtension(path));
    public static bool MayHaveAlpha(string path) => AlphaExtensions.Contains(Path.GetExtension(path));

    static FileStream OpenRead(string path) =>
        new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);

    // ------------------------------------------------------------------ info

    /// <summary>Reads size, orientation, date taken and camera without decoding pixels.</summary>
    public static ImageInfo ReadInfo(string path)
    {
        using var fs = OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = PickFrame(decoder);
        int w = frame.PixelWidth, h = frame.PixelHeight;
        if (w <= 0 || h <= 0) throw new FileFormatException("The image has no pixels.");

        BitmapMetadata? md = null;
        try { md = frame.Metadata as BitmapMetadata; } catch { /* format without metadata */ }

        var orientation = md != null ? ReadOrientation(md) : 1;
        var taken = md != null ? ReadDateTaken(md) : null;
        var camera = md != null ? ReadCamera(md) : null;
        return new ImageInfo(w, h, orientation, taken, camera);
    }

    static BitmapFrame PickFrame(BitmapDecoder decoder)
    {
        if (decoder.Frames.Count == 0) throw new FileFormatException("The file contains no image.");
        if (decoder is IconBitmapDecoder && decoder.Frames.Count > 1)
        {
            // Icons hold several sizes; show the biggest, best-quality one.
            return decoder.Frames
                .OrderByDescending(f => (long)f.PixelWidth * f.PixelHeight)
                .ThenByDescending(f => f.Format.BitsPerPixel)
                .First();
        }
        return decoder.Frames[0];
    }

    static object? TryQuery(BitmapMetadata md, string query)
    {
        try { return md.ContainsQuery(query) ? md.GetQuery(query) : null; }
        catch { return null; }
    }

    static int ReadOrientation(BitmapMetadata md)
    {
        var v = TryQuery(md, "System.Photo.Orientation")
             ?? TryQuery(md, "/app1/ifd/{ushort=274}")
             ?? TryQuery(md, "/ifd/{ushort=274}");
        var o = v switch
        {
            ushort u => u,
            short s => s,
            int i => i,
            uint ui => (int)ui,
            byte b => b,
            _ => 1,
        };
        return o is >= 1 and <= 8 ? o : 1;
    }

    static DateTime? ReadDateTaken(BitmapMetadata md)
    {
        string[] exifQueries =
        [
            "/app1/ifd/exif/{ushort=36867}", // DateTimeOriginal (JPEG)
            "/app1/ifd/exif/{ushort=36868}", // DateTimeDigitized (JPEG)
            "/ifd/exif/{ushort=36867}",      // TIFF
            "/ifd/exif/{ushort=36868}",
        ];
        foreach (var q in exifQueries)
            if (TryQuery(md, q) is string s && TryParseDate(s, out var d))
                return d;

        // Policy query: works for most WIC formats (PNG eXIf, HEIF, JPEG XR...).
        switch (TryQuery(md, "System.Photo.DateTaken"))
        {
            case DateTime dt when Plausible(dt):
                return DateTime.SpecifyKind(dt.Kind == DateTimeKind.Utc ? dt.ToLocalTime() : dt, DateTimeKind.Unspecified);
            case System.Runtime.InteropServices.ComTypes.FILETIME ft:
                var ticks = ((long)ft.dwHighDateTime << 32) | (uint)ft.dwLowDateTime;
                if (ticks > 0)
                {
                    var local = DateTime.FromFileTime(ticks);
                    if (Plausible(local)) return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                }
                break;
            case string s when TryParseDate(s, out var d2):
                return d2;
        }

        foreach (var q in new[] { "/xmp/exif:DateTimeOriginal", "/xmp/xmp:CreateDate", "/xmp/photoshop:DateCreated" })
            if (TryQuery(md, q) is string s && TryParseDate(s, out var d3))
                return d3;

        return null;
    }

    static readonly string[] DateFormats =
    [
        "yyyy:MM:dd HH:mm:ss", "yyyy:MM:dd HH:mm", "yyyy:MM:dd",
        "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF", "yyyy-MM-ddTHH:mm", "yyyy-MM-dd",
    ];

    public static bool TryParseDate(string raw, out DateTime result)
    {
        var s = raw.Trim('\0', ' ');
        result = default;
        if (s.Length < 8 || s.StartsWith("0000", StringComparison.Ordinal)) return false;

        if (DateTime.TryParseExact(s, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var d) && Plausible(d))
        {
            result = DateTime.SpecifyKind(d, DateTimeKind.Unspecified);
            return true;
        }
        // XMP dates can carry a time zone; keep the wall-clock time as written.
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var dto) && Plausible(dto.DateTime))
        {
            result = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }

    static bool Plausible(DateTime d) => d.Year >= 1826 && d <= DateTime.Now.AddYears(1);

    static string? ReadCamera(BitmapMetadata md)
    {
        string? model = null, make = null;
        try { model = md.CameraModel?.Trim('\0', ' '); } catch { }
        try { make = md.CameraManufacturer?.Trim('\0', ' '); } catch { }
        if (string.IsNullOrWhiteSpace(model)) return string.IsNullOrWhiteSpace(make) ? null : make;
        if (!string.IsNullOrWhiteSpace(make) && !model.StartsWith(make.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            return $"{make} {model}";
        return model;
    }

    // ------------------------------------------------------------------ pixels

    /// <summary>
    /// Decodes an image, scaled so its longest side is at most <paramref name="maxLongSide"/>
    /// (0 = full size, capped at <see cref="MaxViewerSide"/>), with the EXIF orientation applied.
    /// Pass the stored size when known to avoid an extra header read.
    /// </summary>
    public static BitmapSource Decode(string path, int maxLongSide, int knownWidth = 0, int knownHeight = 0, int orientation = 1)
    {
        try
        {
            return DecodeCore(path, maxLongSide, knownWidth, knownHeight, orientation, ignoreColorProfile: false);
        }
        catch (Exception ex) when (IsRetryable(ex))
        {
            // Broken or unusual colour profiles are a common cause of decode failures.
            return DecodeCore(path, maxLongSide, knownWidth, knownHeight, orientation, ignoreColorProfile: true);
        }
    }

    static bool IsRetryable(Exception ex) =>
        ex is ArgumentException or COMException or InvalidOperationException or FileFormatException
        && ex is not (FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException);

    static BitmapSource DecodeCore(string path, int maxLongSide, int w, int h, int orientation, bool ignoreColorProfile)
    {
        var ext = Path.GetExtension(path);
        if (ext.Equals(".ico", StringComparison.OrdinalIgnoreCase))
            return DecodeIcon(path, maxLongSide);

        if (w <= 0 || h <= 0)
        {
            var info = ReadInfo(path);
            (w, h) = (info.PixelWidth, info.PixelHeight);
            orientation = info.Orientation;
        }
        if (!JpegExtensions.Contains(ext) && (long)w * h > MaxPixelsNonJpeg)
            throw new ImageTooLargeException($"This image is too large to display ({w:N0} × {h:N0} pixels).");

        var target = maxLongSide > 0 ? Math.Min(maxLongSide, MaxViewerSide) : MaxViewerSide;

        using var fs = OpenRead(path);
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.CreateOptions = ignoreColorProfile ? BitmapCreateOptions.IgnoreColorProfile : BitmapCreateOptions.None;
        bi.StreamSource = fs;
        if (w >= h) { if (w > target) bi.DecodePixelWidth = target; }
        else if (h > target) bi.DecodePixelHeight = target;
        bi.EndInit();
        bi.Freeze();
        return Orient(bi, orientation);
    }

    static BitmapSource DecodeIcon(string path, int maxLongSide)
    {
        using var fs = OpenRead(path);
        var decoder = BitmapDecoder.Create(fs, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        BitmapSource frame = PickFrame(decoder);
        var longSide = Math.Max(frame.PixelWidth, frame.PixelHeight);
        if (maxLongSide > 0 && longSide > maxLongSide)
        {
            var s = (double)maxLongSide / longSide;
            frame = Materialize(new TransformedBitmap(frame, new ScaleTransform(s, s)));
        }
        else if (!frame.IsFrozen && frame.CanFreeze)
        {
            frame.Freeze();
        }
        return frame;
    }

    /// <summary>Applies an EXIF orientation (1-8) to a bitmap.</summary>
    public static BitmapSource Orient(BitmapSource src, int orientation)
    {
        Transform? t = orientation switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(270) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(-1, 1), new RotateTransform(90) } },
            8 => new RotateTransform(270),
            _ => null,
        };
        return t == null ? src : Materialize(new TransformedBitmap(src, t));
    }

    /// <summary>Scales a bitmap down so its longest side is at most <paramref name="maxLongSide"/>.</summary>
    public static BitmapSource ScaleDown(BitmapSource src, int maxLongSide)
    {
        var longSide = Math.Max(src.PixelWidth, src.PixelHeight);
        if (maxLongSide <= 0 || longSide <= maxLongSide) return src;
        var s = (double)maxLongSide / longSide;
        return Materialize(new TransformedBitmap(src, new ScaleTransform(s, s)));
    }

    /// <summary>Forces a lazy bitmap (e.g. TransformedBitmap) to compute its pixels now, on this thread.</summary>
    static BitmapSource Materialize(BitmapSource src)
    {
        var cached = new CachedBitmap(src, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        cached.Freeze();
        return cached;
    }

    /// <summary>Saves a bitmap as a JPEG (or PNG when it may contain transparency) atomically.</summary>
    public static void SaveThumbnail(BitmapSource bmp, string destination, bool png)
    {
        BitmapEncoder encoder;
        BitmapSource src;
        if (png)
        {
            encoder = new PngBitmapEncoder();
            src = bmp.Format == PixelFormats.Bgra32 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgra32, null, 0);
        }
        else
        {
            encoder = new JpegBitmapEncoder { QualityLevel = 85 };
            src = bmp.Format == PixelFormats.Bgr24 ? bmp : new FormatConvertedBitmap(bmp, PixelFormats.Bgr24, null, 0);
        }
        encoder.Frames.Add(BitmapFrame.Create(src));

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                encoder.Save(fs);
            File.Move(tmp, destination, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }
}
