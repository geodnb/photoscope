using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PhotoScope.Controls;

/// <summary>
/// Saves and restores window positions with Windows' own GetWindowPlacement/SetWindowPlacement,
/// which work in physical pixels. That keeps positions right across monitors with different
/// scaling, and lets us check against the monitors that are connected now (a window saved on a
/// monitor that has since been unplugged is centred on the primary monitor instead).
/// </summary>
public static class WindowPlacement
{
    /// <summary>Serialised placement: "left,top,right,bottom,maximized" of the restored (normal) bounds.</summary>
    public static string? Capture(Window window, bool? maximizedOverride = null)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        if (!GetWindowPlacement(hwnd, ref wp)) return null;
        var maximized = maximizedOverride ?? (wp.showCmd == SwShowMaximized || (wp.showCmd == SwShowMinimized && (wp.flags & WpfRestoreToMaximized) != 0));
        var r = wp.rcNormalPosition;
        if (r.Right - r.Left < 100 || r.Bottom - r.Top < 100) return null;
        return string.Join(",", r.Left, r.Top, r.Right, r.Bottom, maximized ? 1 : 0);
    }

    /// <summary>
    /// Applies a saved placement. Call from SourceInitialized (the handle exists, the window isn't
    /// shown yet). Returns false, changing nothing, when there's no usable saved placement or its
    /// title bar wouldn't be on any connected monitor.
    /// </summary>
    public static bool Apply(Window window, string? saved)
    {
        if (!TryParse(saved, out var rect, out var maximized)) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!TitleBarVisible(rect)) return false;
        var wp = new WINDOWPLACEMENT
        {
            length = Marshal.SizeOf<WINDOWPLACEMENT>(),
            showCmd = maximized ? SwShowMaximized : SwShowNormal,
            rcNormalPosition = rect,
        };
        // SetWindowPlacement would show the window; WPF shows it moments later anyway, and we
        // only call this before the first Show.
        return SetWindowPlacement(hwnd, ref wp);
    }

    /// <summary>True when the window's restored bounds have a grabbable title bar on a connected monitor.</summary>
    public static bool NormalBoundsVisible(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return true;
        var wp = new WINDOWPLACEMENT { length = Marshal.SizeOf<WINDOWPLACEMENT>() };
        return !GetWindowPlacement(hwnd, ref wp) || TitleBarVisible(wp.rcNormalPosition);
    }

    static bool TitleBarVisible(RECT r)
    {
        // A strip across the top of the window, inset from the edges: it must be on a monitor.
        var strip = new RECT { Left = r.Left + 60, Top = r.Top, Right = Math.Max(r.Left + 120, r.Right - 60), Bottom = r.Top + 30 };
        return MonitorFromRect(ref strip, MonitorDefaultToNull) != IntPtr.Zero;
    }

    internal static bool TryParse(string? saved, out RECT rect, out bool maximized)
    {
        rect = default;
        maximized = false;
        if (string.IsNullOrWhiteSpace(saved)) return false;
        var parts = saved.Split(',');
        if (parts.Length != 5) return false;
        var n = new int[5];
        for (var i = 0; i < 5; i++)
            if (!int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out n[i])) return false;
        rect = new RECT { Left = n[0], Top = n[1], Right = n[2], Bottom = n[3] };
        maximized = n[4] == 1;
        var w = rect.Right - rect.Left;
        var h = rect.Bottom - rect.Top;
        return w >= 100 && h >= 100 && w <= 40000 && h <= 40000;
    }

    /// <summary>Longest side, in physical pixels, of any connected monitor.</summary>
    public static int LargestMonitorLongSide()
    {
        var largest = 0;
        try
        {
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    var m = info.rcMonitor;
                    largest = Math.Max(largest, Math.Max(m.Right - m.Left, m.Bottom - m.Top));
                }
                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // fall through to the default
        }
        return largest > 0 ? largest : 1920;
    }

    // ------------------------------------------------------------------ interop

    const int SwShowNormal = 1;
    const int SwShowMinimized = 2;
    const int SwShowMaximized = 3;
    const int WpfRestoreToMaximized = 2;
    const uint MonitorDefaultToNull = 0;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct WINDOWPLACEMENT
    {
        public int length;
        public int flags;
        public int showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")] static extern bool GetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern bool SetWindowPlacement(IntPtr hwnd, ref WINDOWPLACEMENT wp);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);
    [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
