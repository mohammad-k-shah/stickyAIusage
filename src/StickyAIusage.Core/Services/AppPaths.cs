using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace StickyAIusage.Services;

/// <summary>Where the app keeps its own files, per OS, and where it finds
/// Claude Code's. Windows paths are unchanged from v1
/// (%LOCALAPPDATA%\StickyAIusage\...); macOS follows the platform convention
/// (~/Library/Application Support and ~/Library/Logs).
///
/// A profile (<c>--profile dev</c> or <c>STICKYAIUSAGE_PROFILE</c>) gives a
/// copy its own data folder and single-instance lock, so a test build runs
/// beside the installed widget without touching its files.
/// <c>STICKYAIUSAGE_DATA_DIR</c> points the data folder anywhere (tests,
/// portable use). Both apps call <see cref="Configure"/> first thing at start.</summary>
public static partial class AppPaths
{
    public const string ProfileArgument = "--profile";
    public const string ProfileVariable = "STICKYAIUSAGE_PROFILE";
    public const string DataDirectoryVariable = "STICKYAIUSAGE_DATA_DIR";
    public const string FeedDirectoryVariable = "STICKYAIUSAGE_FEED_DIR";
    public const string ClaudeConfigVariable = "CLAUDE_CONFIG_DIR";
    public const string CodexHomeVariable = "CODEX_HOME";
    private const string AppFolderName = "StickyAIusage";
    private const int MaxProfileLength = 32;

    private static AppLocations? _current;

    /// <summary>Everything resolved at start. Read on first use when
    /// <see cref="Configure"/> was never called (tests, designer).</summary>
    public static AppLocations Current => _current ??= Resolve(Environment.GetEnvironmentVariable, [], OperatingSystem.IsMacOS());

    /// <summary>Resolves the paths from the command line and the environment
    /// and logs what was chosen (and any value that was ignored). Call before
    /// anything reads a path: the settings file and the log live under them.</summary>
    public static AppLocations Configure(string[] args)
    {
        _current = Resolve(Environment.GetEnvironmentVariable, args, OperatingSystem.IsMacOS());
        foreach (var warning in _current.Warnings)
            DiagnosticLog.WriteInfo(warning);
        if (!_current.IsDefaultInstance)
            DiagnosticLog.WriteInfo($"Running as {_current.InstanceLabel}; data folder {_current.DataDirectory}.");
        return _current;
    }

    public static string DataDirectory => Current.DataDirectory;
    public static string CacheDirectory => Path.Combine(DataDirectory, "cache");
    public static string LogDirectory => Current.LogDirectory;
    public static string SettingsPath => Path.Combine(DataDirectory, "settings.json");

    /// <summary>Claude Code's own folder (transcripts, and the credentials
    /// file on Windows/Linux): <c>CLAUDE_CONFIG_DIR</c> when it names an
    /// existing folder, else ~/.claude.</summary>
    public static string ClaudeDirectory => Current.ClaudeDirectory;

    /// <summary>Codex CLI's folder: <c>CODEX_HOME</c>, else ~/.codex.</summary>
    public static string CodexDirectory => Current.CodexDirectory;

    /// <summary>Where the companion Claude Code plugin writes its usage
    /// records. One fixed place for every profile, because the plugin cannot
    /// know which copy of the widget is reading it.</summary>
    public static string FeedDirectory => Current.FeedDirectory;

    /// <summary>The single-instance names for this profile, so two profiles
    /// can run at once but two copies of one profile cannot.</summary>
    public static string InstanceName(string prefix) =>
        Current.InstanceKey is { } key ? $"{prefix}.{key}" : prefix;

    [GeneratedRegex("^[A-Za-z0-9_-]+$")]
    private static partial Regex ProfileNamePattern();

    internal static bool IsValidProfileName(string? name) =>
        name is { Length: > 0 and <= MaxProfileLength } && ProfileNamePattern().IsMatch(name);

    /// <summary>Pure resolution, so every rule is testable: data folder from
    /// STICKYAIUSAGE_DATA_DIR (a full path), else the profile (argument before
    /// variable), else the default.</summary>
    internal static AppLocations Resolve(Func<string, string?> environment, IReadOnlyList<string> args, bool isMac,
        string? homeOverride = null, string? localAppDataOverride = null)
    {
        var warnings = new List<string>();
        var home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var platformRoot = isMac
            ? Path.Combine(home, "Library", "Application Support")
            : localAppDataOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var defaultData = Path.Combine(platformRoot, AppFolderName);

        string? profile = null;
        var requested = ProfileFromArguments(args, warnings) ?? NullIfEmpty(environment(ProfileVariable));
        if (requested is not null)
        {
            if (IsValidProfileName(requested))
                profile = requested;
            else
                warnings.Add($"Ignored profile name \"{requested}\": use 1 to {MaxProfileLength} letters, digits, '-' or '_'.");
        }

        string dataDirectory, logDirectory;
        string? instanceKey;
        var dataOverride = NullIfEmpty(environment(DataDirectoryVariable));
        if (dataOverride is not null && !Path.IsPathFullyQualified(dataOverride))
        {
            warnings.Add($"Ignored {DataDirectoryVariable}: \"{dataOverride}\" is not a full path.");
            dataOverride = null;
        }

        if (dataOverride is not null)
        {
            dataDirectory = Path.GetFullPath(dataOverride);
            logDirectory = Path.Combine(dataDirectory, "logs");
            // Keyed by the folder, not the profile name: two different folders
            // must never share a lock, and one folder never gets two.
            instanceKey = "dir-" + ShortHash(dataDirectory);
        }
        else
        {
            var folder = profile is null ? AppFolderName : $"{AppFolderName}-{profile}";
            dataDirectory = Path.Combine(platformRoot, folder);
            logDirectory = isMac ? Path.Combine(home, "Library", "Logs", folder) : Path.Combine(dataDirectory, "logs");
            instanceKey = profile;
        }

        var feedOverride = NullIfEmpty(environment(FeedDirectoryVariable));
        if (feedOverride is not null && !Path.IsPathFullyQualified(feedOverride))
        {
            warnings.Add($"Ignored {FeedDirectoryVariable}: \"{feedOverride}\" is not a full path.");
            feedOverride = null;
        }

        var defaultClaude = Path.Combine(home, ".claude");
        var claudeDirectory = defaultClaude;
        if (NullIfEmpty(environment(ClaudeConfigVariable)) is { } claudeOverride)
        {
            if (Path.IsPathFullyQualified(claudeOverride) && Directory.Exists(claudeOverride))
                claudeDirectory = Path.GetFullPath(claudeOverride);
            else
                warnings.Add($"Ignored {ClaudeConfigVariable}: \"{claudeOverride}\" is not an existing folder given as a full path; reading {defaultClaude}.");
        }

        var codexDirectory = Path.Combine(home, ".codex");
        if (NullIfEmpty(environment(CodexHomeVariable)) is { } codexOverride)
        {
            if (Path.IsPathFullyQualified(codexOverride))
                codexDirectory = Path.GetFullPath(codexOverride);
            else
                warnings.Add($"Ignored {CodexHomeVariable}: \"{codexOverride}\" is not a full path.");
        }

        return new AppLocations(
            DataDirectoryOverridden: dataOverride is not null,
            DataDirectory: dataDirectory,
            LogDirectory: logDirectory,
            FeedDirectory: feedOverride is not null ? Path.GetFullPath(feedOverride) : Path.Combine(defaultData, "feed"),
            ClaudeDirectory: claudeDirectory,
            CodexDirectory: codexDirectory,
            Profile: profile,
            InstanceKey: instanceKey,
            Warnings: warnings);
    }

    /// <summary>Reads <c>--profile name</c> or <c>--profile=name</c>. A flag
    /// without a value is reported and ignored rather than taking the next
    /// argument by surprise.</summary>
    private static string? ProfileFromArguments(IReadOnlyList<string> args, List<string> warnings)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(ProfileArgument + "=", StringComparison.OrdinalIgnoreCase))
                return arg[(ProfileArgument.Length + 1)..];
            if (!string.Equals(arg, ProfileArgument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                return args[i + 1];
            warnings.Add($"Ignored {ProfileArgument}: no profile name after it.");
            return null;
        }
        return null;
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string ShortHash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToUpperInvariant())))[..8].ToLowerInvariant();
}

/// <summary>The resolved folders of one run. <see cref="InstanceKey"/> is
/// null for the default copy.</summary>
public sealed record AppLocations(
    bool DataDirectoryOverridden,
    string DataDirectory,
    string LogDirectory,
    string FeedDirectory,
    string ClaudeDirectory,
    string CodexDirectory,
    string? Profile,
    string? InstanceKey,
    IReadOnlyList<string> Warnings)
{
    public bool IsDefaultInstance => InstanceKey is null;

    /// <summary>"profile dev", or "a custom data folder", for the log and the tray tooltip.</summary>
    public string InstanceLabel => DataDirectoryOverridden ? "a custom data folder"
        : Profile is not null ? $"profile {Profile}"
        : "the default profile";

    /// <summary>The arguments that start this same copy again (launch at
    /// login). Null when that can't be expressed as arguments: a data folder
    /// set through the environment.</summary>
    public IReadOnlyList<string>? RelaunchArguments => DataDirectoryOverridden ? null
        : Profile is not null ? [AppPaths.ProfileArgument, Profile]
        : [];

    /// <summary>Suffix for per-copy names outside the data folder (the
    /// launch-at-login entry), empty for the default copy.</summary>
    public string NameSuffix => InstanceKey is null ? "" : "-" + InstanceKey;
}
