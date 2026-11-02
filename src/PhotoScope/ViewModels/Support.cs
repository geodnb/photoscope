using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PhotoScope.Services;

namespace PhotoScope.ViewModels;

public sealed partial class NavEntry(LibrarySection section, string title, string glyph) : ObservableObject
{
    public LibrarySection Section { get; } = section;
    public string Title { get; } = title;
    public string Glyph { get; } = glyph;
    [ObservableProperty] private int _count;
    [ObservableProperty] private bool _isVisible = true;
}

public sealed record SortOption(SortField Field, string Label);

public enum BannerKind { Info, Success, Warning, Error }

public sealed record BannerAction(string Label, Action Run);

/// <summary>A dismissable message shown above the gallery.</summary>
public sealed partial class Banner : ObservableObject
{
    public Banner(string key, BannerKind kind, string message, IEnumerable<BannerAction>? actions, Action<Banner> close)
    {
        Key = key;
        Kind = kind;
        _message = message;
        Actions = actions?.ToList() ?? [];
        CloseCommand = new RelayCommand(() => close(this));
        RunCommand = new RelayCommand<BannerAction>(a => a?.Run());
    }

    public string Key { get; }
    public BannerKind Kind { get; }
    [ObservableProperty] private string _message;
    public IReadOnlyList<BannerAction> Actions { get; }
    public IRelayCommand CloseCommand { get; }
    public IRelayCommand<BannerAction> RunCommand { get; }

    public string Glyph => Kind switch
    {
        BannerKind.Success => "",
        BannerKind.Warning => "",
        BannerKind.Error => "",
        _ => "",
    };
}

public static class NaturalSort
{
    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern int StrCmpLogicalW(string x, string y);

    /// <summary>Explorer-style comparison ("IMG_2" before "IMG_10").</summary>
    public static int Compare(string a, string b)
    {
        try { return StrCmpLogicalW(a, b); }
        catch { return string.Compare(a, b, StringComparison.CurrentCultureIgnoreCase); }
    }
}
