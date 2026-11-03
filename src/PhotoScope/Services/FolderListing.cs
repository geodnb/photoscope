using PhotoScope.ViewModels;

namespace PhotoScope.Services;

/// <summary>Lists the images in a folder the way File Explorer sorts them by name.</summary>
public static class FolderListing
{
    /// <summary>
    /// Supported images directly inside <paramref name="folder"/> (not subfolders), skipping hidden and
    /// system files, sorted by name with Explorer's natural ordering ("IMG_2" before "IMG_10").
    /// <paramref name="mustInclude"/> is always in the result even if it would otherwise be skipped.
    /// </summary>
    public static List<string> ListImages(string folder, string? mustInclude = null)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = false,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };
        var files = Directory.EnumerateFiles(folder, "*", options).Where(ImageDecoder.IsSupported).ToList();
        if (mustInclude != null && !files.Contains(mustInclude, StringComparer.OrdinalIgnoreCase))
            files.Add(mustInclude);
        files.Sort((a, b) =>
        {
            var c = NaturalSort.Compare(Path.GetFileName(a), Path.GetFileName(b));
            return c != 0 ? c : string.CompareOrdinal(a, b);
        });
        return files;
    }
}
