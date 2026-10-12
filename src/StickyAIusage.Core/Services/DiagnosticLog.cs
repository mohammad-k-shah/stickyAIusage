using System.IO;

namespace StickyAIusage.Services;

/// <summary>
/// Minimal best-effort file logger for failures that would otherwise be
/// silently swallowed (by design - a usage-fetch or git-shellout failure must
/// never crash the widget). Without this, a
/// "why is the monthly spend just missing" report has no way to be diagnosed
/// remotely - this gives the user a file to paste back.
/// </summary>
public static class DiagnosticLog
{
    private static readonly object WriteLock = new();

    /// <summary>Lets tests redirect logging away from the real log file.</summary>
    internal static string? PathOverride { get; set; }

    public static string LogPath => PathOverride ?? Path.Combine(AppPaths.LogDirectory, "diagnostics.log");

    /// <summary>Past this size the log is moved to diagnostics.old.log
    /// (replacing the previous one), so the two files stay around 2 MB in total.</summary>
    internal const long MaxBytes = 1_000_000;

    // People paste this file into public issues, and their home folder path
    // carries their account name.
    private static readonly string HomePrefix =
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + Path.DirectorySeparatorChar;

    public static void WriteError(string context, Exception ex) =>
        WriteLine($"{context}: {ex}");

    public static void WriteInfo(string message) =>
        WriteLine(message);

    private static void WriteLine(string message)
    {
        try
        {
            lock (WriteLock)
            {
                var dir = Path.GetDirectoryName(LogPath)!;
                Directory.CreateDirectory(dir);
                RotateIfLarge(LogPath, MaxBytes);
                File.AppendAllText(LogPath,
                    $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {HideHome(message)}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never itself throw or block the caller.
        }
    }

    internal static void RotateIfLarge(string path, long maxBytes)
    {
        var file = new FileInfo(path);
        if (file.Exists && file.Length > maxBytes)
            File.Move(path, Path.ChangeExtension(path, ".old.log"), overwrite: true);
    }

    internal static string HideHome(string message) =>
        HomePrefix.Length > 1
            ? message.Replace(HomePrefix, "~" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            : message;
}
