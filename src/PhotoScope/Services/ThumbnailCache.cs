using System.Text.RegularExpressions;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoScope.Models;

namespace PhotoScope.Services;

/// <summary>
/// On-disk thumbnail cache in %LocalAppData%\PhotoScope\thumbs. Files are named from the photo's
/// content hash plus its modified time, so a changed file gets a fresh thumbnail.
/// Deletion safety: the cache walks its own folder without following junctions or symbolic
/// links, and only ever deletes regular files whose names match its own naming pattern.
/// </summary>
public sealed partial class ThumbnailCache
{
    /// <summary>Longest side of the thumbnails written to disk.</summary>
    public const int MaxSide = 480;

    [GeneratedRegex(@"^(?:[0-9a-f]{16,64}|x\d+_\d+)_[0-9a-f]{1,16}\.(?:jpg|png)(?:\.[0-9a-f]{8}\.tmp)?$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheFileName();

    [GeneratedRegex(@"^(?:[0-9a-f]{2}|x\d)$", RegexOptions.CultureInvariant)]
    private static partial Regex CacheDirName();

    public string Folder { get; }

    public ThumbnailCache(string folder)
    {
        Folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(Folder);
    }

    string Stem(Photo p)
    {
        // The hash comes from our own database, but sanitise it anyway so a bad value can never
        // produce a path outside the cache folder.
        var hash = IsHex(p.Hash) ? p.Hash.ToLowerInvariant() : "x" + (uint)p.Hash.GetHashCode() + "_" + p.Id;
        return Path.Combine(Folder, hash[..2], $"{hash}_{p.ModifiedUtc.Ticks:x}");
    }

    /// <summary>Path of the cached thumbnail. PNG is used only when the image has transparency.</summary>
    public string PathFor(Photo p, bool png) => Stem(p) + (png ? ".png" : ".jpg");

    /// <summary>The existing cached thumbnail for a photo, or null.</summary>
    public string? FindExisting(Photo p)
    {
        var stem = Stem(p);
        if (File.Exists(stem + ".jpg")) return stem + ".jpg";
        if (File.Exists(stem + ".png")) return stem + ".png";
        return null;
    }

    static bool IsHex(string s) => s.Length >= 16 && s.All(char.IsAsciiHexDigit);

    /// <summary>Loads a cached thumbnail decoded to fit <paramref name="longSide"/>, or null if none is cached.</summary>
    public BitmapSource? TryLoad(Photo p, int longSide)
    {
        var path = FindExisting(p);
        if (path == null) return null;
        try
        {
            var info = new FileInfo(path);
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
            TryDeleteCacheFile(new FileInfo(path));
            return null;
        }
    }

    static bool HasAlpha(PixelFormat f) =>
        f == PixelFormats.Bgra32 || f == PixelFormats.Pbgra32 || f == PixelFormats.Rgba64 || f == PixelFormats.Prgba64 ||
        f == PixelFormats.Rgba128Float || f == PixelFormats.Prgba128Float ||
        f == PixelFormats.Indexed1 || f == PixelFormats.Indexed2 || f == PixelFormats.Indexed4 || f == PixelFormats.Indexed8;

    /// <summary>Decodes the original, writes the thumbnail to disk and returns it.</summary>
    public BitmapSource Generate(Photo p)
    {
        var bmp = ImageDecoder.Decode(p.Path, MaxSide, p.PixelWidth, p.PixelHeight, p.Orientation);
        try
        {
            var png = ImageDecoder.MayHaveAlpha(p.Path) && HasAlpha(bmp.Format);
            ImageDecoder.SaveThumbnail(bmp, PathFor(p, png), png);
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not write thumbnail for {p.Path}", ex);
        }
        return bmp;
    }

    public bool Exists(Photo p) => FindExisting(p) != null;

    /// <summary>
    /// Lists the cache's own files: walks at most one level of hash sub-folders, never follows
    /// reparse points (junctions/symlinks), and only returns names the cache itself creates.
    /// </summary>
    List<FileInfo> CacheFiles()
    {
        var result = new List<FileInfo>();
        var root = new DirectoryInfo(Folder);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var dir in root.EnumerateDirectories("*", options))
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !CacheDirName().IsMatch(dir.Name)) continue;
            foreach (var f in dir.EnumerateFiles("*", options))
                if (IsCacheFile(f)) result.Add(f);
        }
        return result;
    }

    bool IsCacheFile(FileInfo f) =>
        !f.Attributes.HasFlag(FileAttributes.ReparsePoint) && CacheFileName().IsMatch(f.Name);

    public long GetSizeBytes()
    {
        try { return CacheFiles().Sum(f => f.Length); }
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
            var files = CacheFiles();
            var total = files.Sum(f => f.Length);
            if (total <= maxBytes) return;

            var target = (long)(maxBytes * 0.9);
            var removed = 0;
            foreach (var f in files.OrderBy(f => f.LastAccessTimeUtc > f.LastWriteTimeUtc ? f.LastAccessTimeUtc : f.LastWriteTimeUtc))
            {
                if (total <= target) break;
                var len = f.Length;
                if (TryDeleteCacheFile(f))
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

    /// <summary>Deletes every cached thumbnail (only the cache's own files).</summary>
    public void Clear()
    {
        foreach (var f in CacheFiles()) TryDeleteCacheFile(f);
        var options = new EnumerationOptions { RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0 };
        foreach (var dir in new DirectoryInfo(Folder).EnumerateDirectories("*", options))
        {
            if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !CacheDirName().IsMatch(dir.Name)) continue;
            try { if (!dir.EnumerateFileSystemInfos().Any()) dir.Delete(); } catch { }
        }
    }

    bool TryDeleteCacheFile(FileInfo f)
    {
        try
        {
            f.Refresh();
            // Re-check right before deleting: our naming pattern, a regular file, directly inside a
            // real (non-link) hash folder of the cache.
            if (!f.Exists || !IsCacheFile(f)) return false;
            var dir = f.Directory;
            if (dir == null || dir.Attributes.HasFlag(FileAttributes.ReparsePoint) || !CacheDirName().IsMatch(dir.Name)) return false;
            if (!string.Equals(Path.TrimEndingDirectorySeparator(dir.Parent?.FullName ?? ""), Folder, StringComparison.OrdinalIgnoreCase)) return false;
            f.Delete();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
