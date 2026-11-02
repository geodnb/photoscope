using System.Windows.Media.Imaging;
using PhotoScope.Models;

namespace PhotoScope.Services;

/// <summary>
/// On-disk thumbnail cache in %LocalAppData%\PhotoScope\thumbs. Files are named from the photo's
/// content hash plus its modified time, so a changed file gets a fresh thumbnail. This class only
/// ever deletes files inside its own cache folder.
/// </summary>
public sealed class ThumbnailCache
{
    /// <summary>Longest side of the thumbnails written to disk.</summary>
    public const int MaxSide = 480;

    public string Folder { get; }

    public ThumbnailCache(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
    }

    public string PathFor(Photo p)
    {
        // The hash comes from our own database, but sanitise it anyway so a bad value can never
        // produce a path outside the cache folder.
        var hash = IsHex(p.Hash) ? p.Hash.ToLowerInvariant() : "x" + (uint)p.Hash.GetHashCode() + "_" + p.Id;
        var ext = ImageDecoder.MayHaveAlpha(p.Path) ? ".png" : ".jpg";
        var name = $"{hash}_{p.ModifiedUtc.Ticks:x}{ext}";
        return Path.Combine(Folder, hash[..2], name);
    }

    static bool IsHex(string s) => s.Length >= 16 && s.All(char.IsAsciiHexDigit);

    /// <summary>Loads a cached thumbnail decoded to fit <paramref name="longSide"/>, or null if none is cached.</summary>
    public BitmapSource? TryLoad(Photo p, int longSide)
    {
        var path = PathFor(p);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bi = new BitmapImage();
            bi.BeginInit();
            bi.CacheOption = BitmapCacheOption.OnLoad;
            bi.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            bi.StreamSource = fs;
            // Cached thumbnails are already oriented, so compare oriented dimensions.
            if (p.OrientedWidth >= p.OrientedHeight) bi.DecodePixelWidth = Math.Min(longSide, MaxSide);
            else bi.DecodePixelHeight = Math.Min(longSide, MaxSide);
            bi.EndInit();
            bi.Freeze();

            // Record use for least-recently-used trimming (at most once a day per file).
            if (info.LastAccessTimeUtc < DateTime.UtcNow.AddDays(-1))
            {
                try { File.SetLastAccessTimeUtc(path, DateTime.UtcNow); } catch { }
            }
            return bi;
        }
        catch (Exception ex)
        {
            Log.Warn($"Cached thumbnail unreadable, regenerating: {path}", ex);
            TryDeleteCacheFile(path);
            return null;
        }
    }

    /// <summary>Decodes the original, writes the thumbnail to disk and returns it.</summary>
    public BitmapSource Generate(Photo p)
    {
        var bmp = ImageDecoder.Decode(p.Path, MaxSide, p.PixelWidth, p.PixelHeight, p.Orientation);
        try
        {
            ImageDecoder.SaveThumbnail(bmp, PathFor(p), ImageDecoder.MayHaveAlpha(p.Path));
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write thumbnail for {p.Path}", ex);
        }
        return bmp;
    }

    public bool Exists(Photo p) => File.Exists(PathFor(p));

    public long GetSizeBytes()
    {
        try
        {
            return new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        }
        catch (Exception ex)
        {
            Log.Warn("Could not measure thumbnail cache", ex);
            return 0;
        }
    }

    /// <summary>Deletes least-recently-used cache files until the cache is under 90% of <paramref name="maxBytes"/>.</summary>
    public void Trim(long maxBytes)
    {
        try
        {
            var files = new DirectoryInfo(Folder).EnumerateFiles("*", SearchOption.AllDirectories).ToList();
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            var target = (long)(maxBytes * 0.9);
            var removed = 0;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                var len = f.Length;
                if (TryDeleteCacheFile(f.FullName))
                {
                    total -= len;
                    removed++;
                }
            }
            Log.Info($"Thumbnail cache trimmed: removed {removed} files");
        }
        catch (Exception ex)
        {
            Log.Warn("Thumbnail cache trim failed", ex);
        }
    }

    /// <summary>Deletes every cached thumbnail (only files inside the cache folder).</summary>
    public void Clear()
    {
        foreach (var file in Directory.EnumerateFiles(Folder, "*", SearchOption.AllDirectories))
            TryDeleteCacheFile(file);
        foreach (var dir in Directory.EnumerateDirectories(Folder))
        {
            try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); } catch { }
        }
    }

    bool TryDeleteCacheFile(string path)
    {
        // Defence in depth: refuse to delete anything that isn't inside the cache folder.
        if (!AppPaths.IsUnder(path, Folder) || string.Equals(Path.GetFullPath(path), Folder, StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
