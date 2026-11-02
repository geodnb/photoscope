using System.Text.Json;
using System.Text.Json.Serialization;

namespace PhotoScope.Services;

public enum ImportMode { ReferenceInPlace, CopyToLibrary }
public enum ThemeMode { System, Light, Dark }
public enum SortField { DateTaken, DateImported, Name, Size }
public enum LibrarySection { All, Favourites, Recent, Missing }

public sealed class AppSettings
{
    // Window and layout
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double WindowWidth { get; set; } = 1400;
    public double WindowHeight { get; set; } = 880;
    public bool WindowMaximized { get; set; }
    public double SidebarWidth { get; set; } = 232;
    public double PreviewWidth { get; set; } = 340;
    public double PlaylistHeight { get; set; } = 190;
    public bool PreviewVisible { get; set; } = true;
    public bool PlaylistVisible { get; set; } = true;

    // Last view
    public LibrarySection LastSection { get; set; } = LibrarySection.All;
    public long? LastPlaylistId { get; set; }
    public SortField SortBy { get; set; } = SortField.DateTaken;
    public bool SortDescending { get; set; } = true;
    public double ThumbnailSize { get; set; } = 160;

    // Import
    public string LibraryFolder { get; set; } = DefaultLibraryFolder();
    public ImportMode DefaultImportMode { get; set; } = ImportMode.ReferenceInPlace;
    public bool ImportRecursive { get; set; } = true;
    public bool SkipDuplicates { get; set; } = true;

    // Slideshow
    public double SlideshowIntervalSeconds { get; set; } = 5;
    public bool SlideshowShuffle { get; set; }
    public bool SlideshowLoop { get; set; } = true;

    // Cache and appearance
    public int ThumbnailCacheLimitMB { get; set; } = 4096;
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    /// <summary>Returns why a library copy folder is unsafe or invalid, or null if it's fine.</summary>
    public static string? ValidateLibraryFolder(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "Choose a library folder.";
        text = text.Trim();
        if (text.StartsWith(@"\\?\", StringComparison.Ordinal) || text.StartsWith(@"\\.\", StringComparison.Ordinal))
            return "Use a normal folder path, such as D:\\Photos\\Library.";
        if (!Path.IsPathFullyQualified(text)) return "Use a full path, such as D:\\Photos\\Library.";
        string full;
        try { full = Path.GetFullPath(text); }
        catch { return "That isn't a valid folder path."; }
        if (AppPaths.IsUnder(full, AppPaths.Root) || AppPaths.IsUnder(AppPaths.Root, full))
            return "The library folder can't contain, or be inside, PhotoScope's own data folder.";
        var root = Path.GetPathRoot(full);
        if (string.Equals(Path.TrimEndingDirectorySeparator(full), Path.TrimEndingDirectorySeparator(root ?? ""), StringComparison.OrdinalIgnoreCase))
            return "Choose a folder rather than the root of a drive.";
        foreach (var special in new[] { Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86 })
        {
            var dir = Environment.GetFolderPath(special);
            if (!string.IsNullOrEmpty(dir) && AppPaths.IsUnder(full, dir)) return "The library folder can't be inside a Windows system folder.";
        }
        return null;
    }

    public static string DefaultLibraryFolder() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "PhotoScope Library");

    public void Normalize()
    {
        static double Clamp(double v, double min, double max, double fallback) =>
            double.IsFinite(v) ? Math.Clamp(v, min, max) : fallback;

        WindowWidth = Clamp(WindowWidth, 640, 20000, 1400);
        WindowHeight = Clamp(WindowHeight, 420, 20000, 880);
        SidebarWidth = Clamp(SidebarWidth, 160, 480, 232);
        PreviewWidth = Clamp(PreviewWidth, 220, 900, 340);
        PlaylistHeight = Clamp(PlaylistHeight, 120, 600, 190);
        ThumbnailSize = Clamp(ThumbnailSize, 80, 320, 160);
        SlideshowIntervalSeconds = Clamp(SlideshowIntervalSeconds, 1, 120, 5);
        ThumbnailCacheLimitMB = Math.Clamp(ThumbnailCacheLimitMB, 100, 100_000);
        if (ValidateLibraryFolder(LibraryFolder) != null) LibraryFolder = DefaultLibraryFolder();
        else LibraryFolder = Path.GetFullPath(LibraryFolder.Trim());
        if (WindowLeft is { } l && !double.IsFinite(l)) WindowLeft = null;
        if (WindowTop is { } t && !double.IsFinite(t)) WindowTop = null;
        if (!Enum.IsDefined(LastSection)) LastSection = LibrarySection.All;
        if (!Enum.IsDefined(SortBy)) SortBy = SortField.DateTaken;
        if (!Enum.IsDefined(Theme)) Theme = ThemeMode.System;
        if (!Enum.IsDefined(DefaultImportMode)) DefaultImportMode = ImportMode.ReferenceInPlace;
    }
}

/// <summary>Loads and saves settings.json (atomically, so a crash mid-save can't corrupt it).</summary>
public static class SettingsService
{
    static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AppSettings Current { get; private set; } = new();

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Settings file could not be read; using defaults", ex);
            Current = new AppSettings();
        }
        Current.Normalize();
        return Current;
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Root);
            var tmp = AppPaths.SettingsFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Settings could not be saved", ex);
        }
    }
}
