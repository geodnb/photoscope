using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace PhotoScope.Services;

/// <summary>
/// Optional, per-user registration so PhotoScope appears in Explorer's "Open with" list and in
/// Settings > Apps > Default apps for its image types. Only runs when the user asks (Settings).
/// Everything is written under HKEY_CURRENT_USER, so no admin rights are needed and nothing
/// system-wide changes. It never changes which app is the default; Windows lets only the user do that.
///
/// Written (and removed again by <see cref="Unregister"/>):
///   HKCU\Software\Classes\PhotoScope.Image                       file type + open command
///   HKCU\Software\Classes\Applications\PhotoScope.exe            app entry + SupportedTypes
///   HKCU\Software\Classes\.ext\OpenWithProgids  value "PhotoScope.Image"   (one value per type)
///   HKCU\Software\PhotoScope\Capabilities                        name + FileAssociations
///   HKCU\Software\RegisteredApplications  value "PhotoScope"
/// </summary>
public static class FileAssociations
{
    public const string ProgId = "PhotoScope.Image";
    const string AppKey = @"Software\Classes\Applications\PhotoScope.exe";
    const string ProgIdKey = @"Software\Classes\" + ProgId;
    const string CapabilitiesKey = @"Software\PhotoScope\Capabilities";
    const string RegisteredAppsKey = @"Software\RegisteredApplications";
    const string RegisteredAppName = "PhotoScope";

    public sealed record Entry(string Key, string ValueName, string Value);

    /// <summary>The exact values that registration writes, for an exe at <paramref name="exePath"/>.</summary>
    public static IReadOnlyList<Entry> Plan(string exePath)
    {
        var command = $"\"{exePath}\" \"%1\"";
        var list = new List<Entry>
        {
            new(ProgIdKey, "", "PhotoScope image"),
            new(ProgIdKey + @"\DefaultIcon", "", $"\"{exePath}\",0"),
            new(ProgIdKey + @"\shell\open\command", "", command),
            new(AppKey, "FriendlyAppName", "PhotoScope"),
            new(AppKey + @"\shell\open\command", "", command),
            new(CapabilitiesKey, "ApplicationName", "PhotoScope"),
            new(CapabilitiesKey, "ApplicationDescription", "A fast photo viewer and manager."),
            new(RegisteredAppsKey, RegisteredAppName, CapabilitiesKey),
        };
        foreach (var ext in ImageDecoder.Extensions.Order(StringComparer.OrdinalIgnoreCase))
        {
            list.Add(new(AppKey + @"\SupportedTypes", ext, ""));
            list.Add(new($@"Software\Classes\{ext}\OpenWithProgids", ProgId, ""));
            list.Add(new(CapabilitiesKey + @"\FileAssociations", ext, ProgId));
        }
        return list;
    }

    /// <summary>The running PhotoScope.exe, or null when started some other way (e.g. "dotnet PhotoScope.dll").</summary>
    public static string? CurrentExe()
    {
        var path = Environment.ProcessPath;
        return path != null && Path.GetFileName(path).Equals("PhotoScope.exe", StringComparison.OrdinalIgnoreCase) ? path : null;
    }

    /// <summary>True when the registration exists and points at this exe.</summary>
    public static bool IsRegistered(string? exePath = null)
    {
        exePath ??= CurrentExe();
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(ProgIdKey + @"\shell\open\command");
            var value = key?.GetValue("") as string;
            return value != null && (exePath == null || value.Contains(exePath, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    public static void Register(string exePath)
    {
        foreach (var e in Plan(exePath))
        {
            using var key = Registry.CurrentUser.CreateSubKey(e.Key, writable: true);
            key.SetValue(e.ValueName, e.Value, RegistryValueKind.String);
        }
        NotifyShell();
        Log.Info($"Registered PhotoScope in 'Open with' for the current user ({exePath})");
    }

    /// <summary>Removes exactly what <see cref="Register"/> added and nothing else.</summary>
    public static void Unregister()
    {
        var cu = Registry.CurrentUser;
        cu.DeleteSubKeyTree(ProgIdKey, throwOnMissingSubKey: false);
        // Applications\PhotoScope.exe is keyed by file name, so only remove it if it is ours.
        using (var app = cu.OpenSubKey(AppKey))
        {
            var friendly = app?.GetValue("FriendlyAppName") as string;
            using var cmd = cu.OpenSubKey(AppKey + @"\shell\open\command");
            var command = cmd?.GetValue("") as string ?? "";
            var ours = friendly == "PhotoScope" && command.Contains("PhotoScope.exe", StringComparison.OrdinalIgnoreCase);
            if (app != null && ours) { app.Dispose(); cu.DeleteSubKeyTree(AppKey, throwOnMissingSubKey: false); }
        }
        cu.DeleteSubKeyTree(CapabilitiesKey, throwOnMissingSubKey: false);
        using (var parent = cu.OpenSubKey(@"Software\PhotoScope", writable: true))
        {
            if (parent != null && parent.SubKeyCount == 0 && parent.ValueCount == 0)
                cu.DeleteSubKey(@"Software\PhotoScope", throwOnMissingSubKey: false);
        }
        using (var apps = cu.OpenSubKey(RegisteredAppsKey, writable: true))
            apps?.DeleteValue(RegisteredAppName, throwOnMissingValue: false);
        foreach (var ext in ImageDecoder.Extensions)
        {
            var path = $@"Software\Classes\{ext}\OpenWithProgids";
            bool empty;
            using (var key = cu.OpenSubKey(path, writable: true))
            {
                if (key == null) continue;
                key.DeleteValue(ProgId, throwOnMissingValue: false); // only our value
                empty = key.ValueCount == 0 && key.SubKeyCount == 0;
            }
            // Tidy up an OpenWithProgids key left empty; the extension key itself always stays.
            if (empty) cu.DeleteSubKey(path, throwOnMissingSubKey: false);
        }
        NotifyShell();
        Log.Info("Removed PhotoScope's 'Open with' registration for the current user");
    }

    static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); } catch { }
    }

    [DllImport("shell32.dll")]
    static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
