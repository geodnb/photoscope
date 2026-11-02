namespace PhotoScope.Services;

/// <summary>
/// Locations of everything PhotoScope writes. All of it lives under %LocalAppData%\PhotoScope
/// (or PHOTOSCOPE_HOME when set, which is used for testing). Nothing outside this folder is
/// ever created by the app apart from the optional library copy folder chosen by the user.
/// </summary>
public static class AppPaths
{
    public static string Root { get; private set; } = DefaultRoot();

    public static string Database => Path.Combine(Root, "library.db");
    public static string Thumbs => Path.Combine(Root, "thumbs");
    public static string Logs => Path.Combine(Root, "logs");
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    static string DefaultRoot()
    {
#if DEBUG
        // Debug builds only: lets automated checks use a throwaway data folder.
        var overrideRoot = Environment.GetEnvironmentVariable("PHOTOSCOPE_HOME");
        if (!string.IsNullOrWhiteSpace(overrideRoot))
            return Path.GetFullPath(overrideRoot);
#endif
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PhotoScope");
    }

    /// <summary>Points the app at a different data folder (used by tests).</summary>
    public static void UseRoot(string root) => Root = Path.GetFullPath(root);

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Thumbs);
        Directory.CreateDirectory(Logs);
    }

    /// <summary>True when <paramref name="path"/> is inside <paramref name="folder"/> (or is the folder).</summary>
    public static bool IsUnder(string path, string folder)
    {
        try
        {
            var p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var f = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            return p.Equals(f, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(f + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
}
