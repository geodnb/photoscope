using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace PhotoScope.Services;

/// <summary>An error whose message is already written for the user.</summary>
public sealed class UserFacingException(string message) : Exception(message);

/// <summary>
/// Every operation that touches photo files on disk. Rules:
/// never overwrite an existing file, and never delete permanently: deletion only ever goes to
/// the Recycle Bin through Microsoft.VisualBasic.FileIO.FileSystem.
/// </summary>
public static class FileOps
{
    static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM0", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "COM¹", "COM²", "COM³",
        "LPT0", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9", "LPT¹", "LPT²", "LPT³",
    };

    /// <summary>Returns a message describing why <paramref name="name"/> isn't a valid file name, or null if it is.</summary>
    public static string? ValidateFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Enter a name.";
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return "A file name can't contain any of these characters: \\ / : * ? \" < > |";
        if (name.EndsWith(' ') || name.EndsWith('.')) return "A file name can't end with a space or a full stop.";
        if (name.StartsWith(' ')) return "A file name can't start with a space.";
        if (name.Length > 240) return "That name is too long.";
        var stem = name.Split('.')[0].TrimEnd();
        if (ReservedNames.Contains(stem)) return $"\"{stem}\" is a name reserved by Windows.";
        if (name.Trim('.').Length == 0) return "Enter a name.";
        return null;
    }

    /// <summary>Renames a file in place. Fails (rather than overwriting) if the name is taken.</summary>
    public static string Rename(string path, string newFileName)
    {
        var error = ValidateFileName(newFileName);
        if (error != null) throw new UserFacingException(error);
        if (!File.Exists(path)) throw new UserFacingException("The file could not be found. It may have been moved or deleted.");

        var dir = Path.GetDirectoryName(path)!;
        var dest = Path.Combine(dir, newFileName);
        if (string.Equals(dest, path, StringComparison.Ordinal)) return path;

        var caseOnly = string.Equals(dest, path, StringComparison.OrdinalIgnoreCase);
        if (!caseOnly && (File.Exists(dest) || Directory.Exists(dest)))
            throw new UserFacingException($"There is already a file named \"{newFileName}\" in this folder.");

        File.Move(path, dest, overwrite: false);
        return dest;
    }

    /// <summary>Moves a file into <paramref name="folder"/>, adding " (2)" etc. if the name is taken. Never overwrites.</summary>
    /// <param name="isTaken">Extra check for names that must not be used (e.g. already in the library).</param>
    public static string MoveNoOverwrite(string path, string folder, Func<string, bool>? isTaken = null)
    {
        if (!File.Exists(path)) throw new UserFacingException("The file could not be found. It may have been moved or deleted.");
        var fullFolder = Path.GetFullPath(folder);
        if (SameFolder(Path.GetDirectoryName(Path.GetFullPath(path))!, fullFolder))
            return path; // already there (also when reached through a junction, subst or mapped drive)

        Directory.CreateDirectory(fullFolder);
        var name = Path.GetFileName(path);
        for (var n = 1; n < 10_000; n++)
        {
            var dest = Path.Combine(fullFolder, Candidate(name, n));
            if (File.Exists(dest) || Directory.Exists(dest) || isTaken?.Invoke(dest) == true) continue;
            try
            {
                File.Move(path, dest, overwrite: false);
                return dest;
            }
            catch (IOException) when (File.Exists(dest))
            {
                // Lost a race with another writer: try the next name.
            }
        }
        throw new UserFacingException("Couldn't find a free file name in the destination folder.");
    }

    /// <summary>Copies a file into <paramref name="folder"/>, adding " (2)" etc. if the name is taken. Never overwrites.</summary>
    public static string CopyNoOverwrite(string path, string folder, Func<string, bool>? isTaken = null)
    {
        var fullFolder = Path.GetFullPath(folder);
        Directory.CreateDirectory(fullFolder);
        var name = Path.GetFileName(path);
        for (var n = 1; n < 10_000; n++)
        {
            var dest = Path.Combine(fullFolder, Candidate(name, n));
            if (File.Exists(dest) || Directory.Exists(dest) || isTaken?.Invoke(dest) == true) continue;
            try
            {
                File.Copy(path, dest, overwrite: false);
                return dest;
            }
            catch (IOException) when (File.Exists(dest) && !string.Equals(dest, path, StringComparison.OrdinalIgnoreCase))
            {
                // Name taken between the check and the copy: try the next one.
            }
        }
        throw new UserFacingException("Couldn't find a free file name in the destination folder.");
    }

    static string Candidate(string fileName, int n)
    {
        if (n <= 1) return fileName;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return $"{stem} ({n}){ext}";
    }

    /// <summary>
    /// True only when Windows will really put this file in a Recycle Bin. The check resolves the
    /// file's final location (through junctions, symbolic links, mount points and subst drives) and
    /// refuses network shares, removable drives, volumes whose bin is set to "remove files
    /// immediately", the NoRecycleFiles policy, and files larger than the bin can hold. In all of
    /// those cases Windows would delete permanently, so PhotoScope leaves the file alone instead.
    /// </summary>
    public static bool CanRecycle(string path, out string reason)
    {
        reason = "";
        try
        {
            if (Path.GetFullPath(path).StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = "This file is on a network location, which has no Recycle Bin.";
                return false;
            }
            var final = ResolveFinalPath(path);
            if (final == null)
            {
                reason = "PhotoScope couldn't work out which drive this file is really stored on.";
                return false;
            }
            if (final.StartsWith(@"\\", StringComparison.Ordinal))
            {
                reason = "This file is on a network location, which has no Recycle Bin.";
                return false;
            }
            var root = Path.GetPathRoot(final);
            if (string.IsNullOrEmpty(root))
            {
                reason = "The file's drive couldn't be determined.";
                return false;
            }
            var drive = new DriveInfo(root);
            if (drive.DriveType != DriveType.Fixed)
            {
                reason = drive.DriveType switch
                {
                    DriveType.Network => "This file is on a network drive, which has no Recycle Bin.",
                    DriveType.Removable => "This file is on a removable drive, which has no Recycle Bin.",
                    _ => "This file's drive has no Recycle Bin.",
                };
                return false;
            }

            using (var policy = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer"))
            {
                if (policy?.GetValue("NoRecycleFiles") is int noRecycle && noRecycle != 0)
                {
                    reason = "A Windows policy on this PC turns the Recycle Bin off.";
                    return false;
                }
            }

            var size = new FileInfo(path).Length;
            var volume = VolumeGuid(root);
            long? capacityMb = null;
            if (volume != null)
            {
                using var key = Registry.CurrentUser.OpenSubKey($@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\{volume}");
                if (key?.GetValue("NukeOnDelete") is int nuke && nuke != 0)
                {
                    reason = "The Recycle Bin on this drive is set to remove files immediately.";
                    return false;
                }
                if (key?.GetValue("MaxCapacity") is int mb) capacityMb = mb;
            }
            // Without a recorded limit, assume Windows' smallest default (5% of the drive).
            var capacityBytes = capacityMb is { } cap ? cap * 1024L * 1024L : drive.TotalSize / 20;
            if (size >= capacityBytes)
            {
                reason = "This file is bigger than the Recycle Bin on its drive can hold.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            reason = Friendly.Describe(ex);
            return false;
        }
    }

    /// <summary>True when two folder paths are the same folder on disk (after resolving links and drive aliases).</summary>
    public static bool SameFolder(string a, string b)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase))
            return true;
        var ra = ResolveFinalPath(a);
        var rb = ResolveFinalPath(b);
        return ra != null && rb != null &&
               string.Equals(Path.TrimEndingDirectorySeparator(ra), Path.TrimEndingDirectorySeparator(rb), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The real location of a file or folder (GetFinalPathNameByHandle), e.g. C:\x for a subst drive
    /// or a junction target. Returns null if it can't be resolved to a drive-letter or UNC path.
    /// </summary>
    public static string? ResolveFinalPath(string path)
    {
        using var handle = CreateFileW(path, 0, FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, FileFlagBackupSemantics, IntPtr.Zero);
        if (handle.IsInvalid) return null;
        var buffer = new StringBuilder(1024);
        var len = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Capacity, 0);
        if (len == 0 || len >= buffer.Capacity) return null;
        var result = buffer.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + result[8..];
        if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
        return result.Length >= 2 && result[1] == ':' ? result : null;
    }

    static string? VolumeGuid(string root)
    {
        var sb = new StringBuilder(64);
        if (!GetVolumeNameForVolumeMountPointW(root, sb, (uint)sb.Capacity)) return null;
        var name = sb.ToString(); // \\?\Volume{guid}\
        var open = name.IndexOf('{');
        var close = name.IndexOf('}');
        return open >= 0 && close > open ? name[open..(close + 1)] : null;
    }

    const uint FileFlagBackupSemantics = 0x02000000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security, FileMode mode, uint flags, IntPtr template);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path, uint length, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint length);

    /// <summary>
    /// Sends one file to the Recycle Bin. This is the only delete path for photos in the app.
    /// Windows' own dialogs are left on so that, if Windows ever decides a file can't be recycled,
    /// it asks before doing anything permanent. Throws <see cref="OperationCanceledException"/> if
    /// the user cancels a Windows prompt.
    /// </summary>
    public static void SendToRecycleBin(string path)
    {
        if (!File.Exists(path)) throw new UserFacingException("The file could not be found. It may already have been moved or deleted.");
        if (!CanRecycle(path, out var reason))
            throw new UserFacingException(reason + " PhotoScope only deletes to the Recycle Bin, so the file was left alone.");

        FileSystem.DeleteFile(path, UIOption.AllDialogs, RecycleOption.SendToRecycleBin, UICancelOption.ThrowException);
    }

    public static void OpenFolder(string folder)
    {
        if (!Directory.Exists(folder)) throw new UserFacingException("That folder could not be found.");
        var psi = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
        psi.ArgumentList.Add(folder);
        Process.Start(psi)?.Dispose();
    }

    public static void ShowInExplorer(string path)
    {
        if (!File.Exists(path))
        {
            var folder = Path.GetDirectoryName(path);
            if (folder != null && Directory.Exists(folder)) { OpenFolder(folder); return; }
            throw new UserFacingException("The file could not be found.");
        }
        // Windows paths can't contain quotes, so this argument can't be broken out of.
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false })?.Dispose();
    }
}
