using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace PhotoScope.Services;

/// <summary>
/// Swaps the colour dictionary between Light and Dark, following the Windows app theme unless
/// the user overrides it. Only reads the registry; never writes to it.
/// </summary>
public static class ThemeService
{
    const string LightUri = "pack://application:,,,/PhotoScope;component/Themes/Light.xaml";
    const string DarkUri = "pack://application:,,,/PhotoScope;component/Themes/Dark.xaml";

    public static bool IsDark { get; private set; }
    public static event Action? Changed;

    public static void Initialize()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        Apply();
    }

    public static void Shutdown() => SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;

    static void OnUserPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.Color
            && SettingsService.Current.Theme == ThemeMode.System)
        {
            Application.Current?.Dispatcher.BeginInvoke(Apply);
        }
    }

    public static void Apply()
    {
        var app = Application.Current;
        if (app == null) return;

        var mode = SettingsService.Current.Theme;
        var dark = mode == ThemeMode.Dark || (mode == ThemeMode.System && SystemPrefersDark());
        var uri = new Uri(dark ? DarkUri : LightUri, UriKind.Absolute);

        var dicts = app.Resources.MergedDictionaries;
        var dict = new ResourceDictionary { Source = uri };
        var index = -1;
        for (var i = 0; i < dicts.Count; i++)
        {
            var src = dicts[i].Source?.OriginalString ?? "";
            if (src.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase) || src.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase))
            {
                index = i;
                break;
            }
        }
        if (index >= 0) dicts[index] = dict; else dicts.Insert(0, dict);

        IsDark = dark;
        foreach (Window w in app.Windows)
            ApplyTitleBar(w, w is Views.ViewerWindow ? true : null);
        Changed?.Invoke();
    }

    static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Makes the window's title bar match the theme (Windows 10 2004+ / Windows 11).</summary>
    /// <param name="forceDark">True for windows that are always dark (the viewer), whatever the theme.</param>
    public static void ApplyTitleBar(Window window, bool? forceDark = null)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;
        try
        {
            var value = (forceDark ?? IsDark) ? 1 : 0;
            DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref value, sizeof(int));
        }
        catch
        {
            // Older Windows: purely cosmetic, ignore.
        }
    }

    const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
