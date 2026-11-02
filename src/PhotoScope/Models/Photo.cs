using CommunityToolkit.Mvvm.ComponentModel;

namespace PhotoScope.Models;

/// <summary>One photo in the library. Doubles as the row model and the bindable item.</summary>
public sealed partial class Photo : ObservableObject
{
    public long Id { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FileName), nameof(Folder))]
    private string _path = "";

    public long SizeBytes { get; set; }

    public DateTime ModifiedUtc
    {
        get => _modifiedUtc;
        set { _modifiedUtc = value; _effectiveDate = null; }
    }
    DateTime _modifiedUtc;
    public string Hash { get; set; } = "";

    /// <summary>Stored (un-oriented) pixel size as reported by the decoder.</summary>
    public int PixelWidth { get; set; }
    public int PixelHeight { get; set; }

    /// <summary>EXIF orientation 1-8 (1 = normal).</summary>
    public int Orientation { get; set; } = 1;

    /// <summary>EXIF date taken, as local wall-clock time. Null when the file has none.</summary>
    public DateTime? DateTaken
    {
        get => _dateTaken;
        set { _dateTaken = value; _effectiveDate = null; }
    }
    DateTime? _dateTaken;
    DateTime? _effectiveDate;
    public DateTime DateImportedUtc { get; set; }
    public string? CameraModel { get; set; }

    [ObservableProperty] private bool _isFavourite;
    [ObservableProperty] private int _rating;

    /// <summary>Viewing rotation in degrees (0, 90, 180, 270), stored only in the database.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Width), nameof(Height), nameof(DimensionsText))]
    private int _rotation;

    [ObservableProperty] private string _tags = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private bool _isMissing;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    private string? _decodeError;

    /// <summary>Bumped when the file behind the photo changes so thumbnails reload.</summary>
    [ObservableProperty] private int _thumbVersion;

    public string FileName => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";

    /// <summary>True when the EXIF orientation swaps width and height.</summary>
    public bool OrientationSwapsAxes => Orientation is >= 5 and <= 8;

    /// <summary>Oriented size (after EXIF orientation, before viewing rotation).</summary>
    public int OrientedWidth => OrientationSwapsAxes ? PixelHeight : PixelWidth;
    public int OrientedHeight => OrientationSwapsAxes ? PixelWidth : PixelHeight;

    /// <summary>Displayed size, including the viewing rotation.</summary>
    public int Width => Rotation % 180 == 0 ? OrientedWidth : OrientedHeight;
    public int Height => Rotation % 180 == 0 ? OrientedHeight : OrientedWidth;

    public DateTime FileDateLocal => ModifiedUtc.ToLocalTime();
    /// <summary>Date taken, or the file date when there is none. Cached because sorting reads it a lot.</summary>
    public DateTime EffectiveDate => _effectiveDate ??= DateTaken ?? FileDateLocal;
    public bool HasProblem => IsMissing || DecodeError != null;

    public string DateTakenText => DateTaken is { } d ? d.ToString("f") : $"{FileDateLocal:f} (file date)";
    public string ImportedText => DateImportedUtc.ToLocalTime().ToString("f");

    public string DimensionsText => Width > 0 && Height > 0 ? $"{Width:N0} × {Height:N0}" : "Unknown";
    public string SizeText => FormatBytes(SizeBytes);

    public IEnumerable<string> TagList => SplitTags(Tags);

    /// <summary>Raises change notifications for every property (after the file details change).</summary>
    public void NotifyAllChanged() => OnPropertyChanged(string.Empty);

    public static IEnumerable<string> SplitTags(string? tags) =>
        (tags ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Distinct(StringComparer.OrdinalIgnoreCase);

    public static string NormalizeTags(string? tags) => string.Join(", ", SplitTags(tags));

    public static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        double v = bytes;
        string[] units = ["KB", "MB", "GB", "TB"];
        var i = -1;
        do { v /= 1024; i++; } while (v >= 1024 && i < units.Length - 1);
        return v >= 100 ? $"{v:0} {units[i]}" : v >= 10 ? $"{v:0.#} {units[i]}" : $"{v:0.##} {units[i]}";
    }
}

public sealed partial class Playlist : ObservableObject
{
    public long Id { get; set; }
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private int _count;
}
