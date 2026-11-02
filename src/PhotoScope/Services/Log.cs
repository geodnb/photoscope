namespace PhotoScope.Services;

/// <summary>Minimal thread-safe file logger. Never throws.</summary>
public static class Log
{
    static readonly object Gate = new();

    public static string CurrentFile => Path.Combine(AppPaths.Logs, $"photoscope-{DateTime.Now:yyyy-MM-dd}.log");

    public static void Info(string message) => Write("INFO ", message, null);
    public static void Warn(string message, Exception? ex = null) => Write("WARN ", message, ex);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    static void Write(string level, string message, Exception? ex)
    {
        try
        {
            var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [T{Environment.CurrentManagedThreadId}] {message}";
            if (ex != null)
                line += Environment.NewLine + "    " + ex.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ");
            lock (Gate)
            {
                Directory.CreateDirectory(AppPaths.Logs);
                File.AppendAllText(CurrentFile, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }

    /// <summary>Removes PhotoScope's own log files older than <paramref name="days"/> days.</summary>
    public static void PruneOld(int days = 14)
    {
        try
        {
            if (!Directory.Exists(AppPaths.Logs)) return;
            var cutoff = DateTime.UtcNow.AddDays(-days);
            foreach (var file in Directory.EnumerateFiles(AppPaths.Logs, "photoscope-*.log"))
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                    File.Delete(file);
        }
        catch (Exception ex)
        {
            Warn("Could not prune old logs", ex);
        }
    }
}
