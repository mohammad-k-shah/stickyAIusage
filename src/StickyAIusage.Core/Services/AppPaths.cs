using System.IO;

namespace StickyAIusage.Services;

/// <summary>Where the app keeps its own files, per OS. Windows paths are
/// unchanged from v1 (%LOCALAPPDATA%\StickyAIusage\...); macOS follows the
/// platform convention (~/Library/Application Support and ~/Library/Logs).</summary>
public static class AppPaths
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string DataDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", "StickyAIusage")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StickyAIusage");

    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");

    public static string LogDirectory => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Logs", "StickyAIusage")
        : Path.Combine(DataDirectory, "logs");

    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Claude Code's own folder (transcripts, and the credentials
    /// file on Windows/Linux).</summary>
    public static string ClaudeDirectory => Path.Combine(Home, ".claude");
}
