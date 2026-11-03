namespace PhotoScope.Services;

public enum OpenProblem { None, Empty, Invalid, NotFound, IsFolder, Unsupported }

/// <summary>Result of checking a path that Windows asked PhotoScope to open.</summary>
public sealed record OpenCheck(string? Path, OpenProblem Problem, string? Message)
{
    public bool Ok => Problem == OpenProblem.None;
}

/// <summary>
/// Reads the file path Windows passes when PhotoScope opens an image (e.g. as the default app or via
/// "Open with"). Handles quoting, spaces, unicode, relative paths, file:// URIs and long paths.
/// </summary>
public static class CommandLine
{
    /// <summary>Returns the first argument that names a file to open, made absolute; null if there is none.</summary>
    public static string? FindFileArgument(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (string.IsNullOrWhiteSpace(a)) continue;
            if (a == "--debug-import")
            {
                i++; // its folder argument
                if (i + 1 < args.Count && args[i + 1] == "copy") i++;
                continue;
            }
            if (a.StartsWith("--", StringComparison.Ordinal)) continue;
            return Normalize(a);
        }
        return null;
    }

    /// <summary>Trims stray quotes/whitespace, converts file:// URIs and makes the path absolute.</summary>
    public static string? Normalize(string raw, string? baseDirectory = null)
    {
        var s = raw.Trim().Trim('"').Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith("file:", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(s, UriKind.Absolute, out var uri) && uri.IsFile)
            s = uri.LocalPath + Uri.UnescapeDataString(uri.Fragment); // a '#' in a file name is parsed as a fragment
        // Long-path prefixes: keep the plain form so it matches library paths.
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) s = @"\\" + s[8..];
        else if (s.StartsWith(@"\\?\", StringComparison.Ordinal) && s.Length > 6 && s[5] == ':') s = s[4..];
        // Windows drops trailing dots and spaces when resolving, which could open a different file;
        // leave such paths as they are so Check reports them.
        if (s.EndsWith('.') || s.EndsWith(' ')) return s;
        try
        {
            return baseDirectory != null && !Path.IsPathRooted(s)
                ? Path.GetFullPath(s, baseDirectory)
                : Path.GetFullPath(s);
        }
        catch
        {
            return s; // reported as invalid by Check
        }
    }

    /// <summary>Checks whether a path can be shown in the viewer, with a friendly message when not.</summary>
    public static OpenCheck Check(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new(path, OpenProblem.Empty, "No file was given to open.");
        string full;
        try { full = Path.GetFullPath(path); }
        catch
        {
            return new(path, OpenProblem.Invalid, $"“{path}” isn't a valid file path.");
        }
        if (path.EndsWith('.') || path.EndsWith(' '))
            return new(path, OpenProblem.Invalid, $"“{path}” isn't a valid file path.");
        var name = Path.GetFileName(full);
        try
        {
            // Network paths are probed on another thread with a short timeout, so a dead share can't freeze the UI.
            var (isDir, isFile) = NetworkPaths.Run(full, () => (Directory.Exists(full), File.Exists(full)), NetworkPaths.ProbeTimeout);
            if (isDir)
                return new(full, OpenProblem.IsFolder, $"“{name}” is a folder. To add a folder to your library, use Import.");
            if (!ImageDecoder.IsSupported(full))
                return new(full, OpenProblem.Unsupported, $"PhotoScope can't open “{name}” because it doesn't support this type of file.");
            if (!isFile)
                return new(full, OpenProblem.NotFound, $"“{name}” can't be found. It may have been moved, renamed or deleted.");
        }
        catch (Exception ex)
        {
            return new(full, OpenProblem.Invalid, Friendly.Describe(ex));
        }
        return new(full, OpenProblem.None, null);
    }
}
