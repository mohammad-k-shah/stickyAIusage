using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StickyAIusage.Services;

/// <summary>User-configurable options. Persisted as
/// plain JSON under the app data folder (AppPaths) so settings survive restarts
/// without needing an installer or registry access.</summary>
public sealed class AppSettings
{
    /// <summary>The settings format this build writes. 1: v1 (no version
    /// key). 2: the version key itself and unknown keys kept on save. 3: a
    /// missing ClaudeSource means "feed" (new installs); a file of an older
    /// format without it keeps "auto", the behavior it was written under.
    /// 4: a missing Theme means "system" (new installs follow the computer); a
    /// file of an older format without it keeps "dark", its old default.
    /// 5: the first-run window (<see cref="OnboardingCompleted"/>); a file of an
    /// older format is an upgrade, which sees the one-screen "what's new" instead.
    /// 6: <see cref="DockMode"/> (Windows: in the taskbar by default); a file of an older
    /// format keeps a dragged pill where it was ("free") and moves a docked one into the taskbar.</summary>
    public const int CurrentSchemaVersion = 6;

    /// <summary>The format of the file this came from, after migration: the
    /// current version, or a newer one when a newer build wrote the file (kept
    /// so that build doesn't migrate its own file twice).</summary>
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    /// <summary>Keys this build doesn't know, written by a newer one. Kept
    /// and written back unchanged, so running an older copy (or a dev
    /// profile of an older commit) never deletes a newer copy's settings.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? UnknownKeys { get; set; }

    public bool ShowMonthlySpend { get; set; } = true;
    public bool ShowSessionCost { get; set; } = true;

    /// <summary>Day target (monthly spend cap accounts only): today's spend
    /// against a daily target in dollars. The widget can show any of the
    /// amount, the % of the target and a progress ring; with none of them on,
    /// or the whole thing off, the pill leaves it out.</summary>
    public bool ShowDayTarget { get; set; } = true;
    public decimal DayTargetUsd { get; set; } = 10m;
    public bool ShowDayTargetAmount { get; set; } = true;
    public bool ShowDayTargetPercent { get; set; } = true;
    public bool ShowDayTargetRing { get; set; } = true;
    /// <summary>Start at sign-in: the HKCU Run key on Windows, a LaunchAgent
    /// on macOS. Keeps its v1 name so existing settings files still load.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Windows: where the pill's bottom-right corner was left, in
    /// device-independent pixels (plus the window's 12 px margin). Read at
    /// start only when <see cref="WidgetDocked"/> is off.</summary>
    public double? WidgetRight { get; set; }
    public double? WidgetBottom { get; set; }

    /// <summary>Windows: the pill sits at the tray corner (the default, and
    /// what "Reset widget position" chooses). Kept as a flag rather than a
    /// position, so a docked pill follows the taskbar through resolution and
    /// monitor changes; dragging it or "Move widget to" turns it off.</summary>
    public bool WidgetDocked { get; set; } = true;

    /// <summary>Windows: "taskbar" (the default: inside the taskbar, left of the tray icons; above it
    /// when there is no room), "above" (the work-area corner above the tray) or "free" (where it was
    /// dragged: set by a drag, cleared by "Reset widget position"). <see cref="WidgetDocked"/> follows it.</summary>
    public string DockMode { get; set; } = ViewModels.DockModes.ToSetting(ViewModels.DockMode.Taskbar);

    /// <summary>Windows: the monitor whose taskbar the pill docks on: "primary" or a display's device
    /// name (as Windows reports it, such as DISPLAY2 with its \\.\ prefix). A monitor that isn't
    /// connected means the primary one.</summary>
    public string DockMonitor { get; set; } = PrimaryMonitor;

    public const string PrimaryMonitor = "primary";

    /// <summary>Windows: the pill steps aside while a full-screen app or a presentation is on its
    /// monitor, so a screen share or a film never shows it.</summary>
    public bool HideWhenFullscreen { get; set; } = true;

    /// <summary>macOS: last dragged position of the pill's top-right corner,
    /// in screen points. The Mac widget hangs down from the menu bar, so it
    /// anchors at the top instead of the bottom.</summary>
    public double? MacWidgetRight { get; set; }
    public double? MacWidgetTop { get; set; }

    /// <summary>Light or dark appearance: "system" (follow the OS, the
    /// default for new installs), "light" or "dark" (the default before
    /// format 4, which older files keep). The sun/moon button in the flyout
    /// sets light or dark; Settings can put it back on "system".</summary>
    public string Theme { get; set; } = NewInstallTheme;

    public const string NewInstallTheme = "system";

    /// <summary>What a file written before format 4 without a Theme ran with.</summary>
    public const string UpgradeTheme = "dark";

    /// <summary>Which fields the session cards show, and how they're grouped
    /// into colored rows: "model,effort|in,out,cached,total". Null means the
    /// shipped grouping (CardLayout.Default).</summary>
    public string? CardFields { get; set; }

    /// <summary>The laid-out blocks under a card's chips.</summary>
    public bool ShowContextBar { get; set; } = true;
    public bool ShowGitDetails { get; set; } = true;

    /// <summary>Whether live polling is currently running. Persisted so a
    /// user-paused widget stays paused across restarts.</summary>
    public bool IsRunning { get; set; } = true;

    // Color thresholds; Settings → Usage alerts changes them. Orange and red
    // also notify (AlertOnThreshold).
    public double YellowThresholdPercent { get; set; } = 50;
    public double OrangeThresholdPercent { get; set; } = 80;
    public double RedThresholdPercent { get; set; } = 95;

    /// <summary>Notifications (<see cref="Alerts.AlertPlanner"/>): all of them
    /// off with one switch, or each kind on its own.</summary>
    public bool AlertsEnabled { get; set; } = true;

    /// <summary>A window reached the orange or red threshold.</summary>
    public bool AlertOnThreshold { get; set; } = true;

    /// <summary>At this pace a limit comes soon (within
    /// <see cref="AlertForecastLeadMinutes"/>), the week runs out early, or a
    /// spend cap's projection passes it.</summary>
    public bool AlertOnForecast { get; set; } = true;

    /// <summary>A window reached 100%.</summary>
    public bool AlertOnLimit { get; set; } = true;

    /// <summary>A window that reached the orange threshold has reset.</summary>
    public bool AlertOnReset { get; set; } = true;

    /// <summary>How close a forecast limit must be to notify (settings.json only).</summary>
    public int AlertForecastLeadMinutes { get; set; } = 30;

    /// <summary>A Claude Code session has been waiting for you (a permission,
    /// a question) for <see cref="AttentionDelaySeconds"/>.</summary>
    public bool AlertOnNeedsYou { get; set; } = true;

    /// <summary>A Claude Code turn of a minute or more has finished (off by default).</summary>
    public bool AlertOnDone { get; set; }

    /// <summary>How long a session waits before "Claude needs you" notifies:
    /// most asks are answered at once (settings.json only, 0–3600).</summary>
    public int AttentionDelaySeconds { get; set; } = 20;

    /// <summary>The session lights: a dot per Claude Code session on the pill
    /// (working, needs you, done) and a status chip on each card.</summary>
    public bool ShowSessionLights { get; set; } = true;

    /// <summary>Quiet hours, local time, "HH:mm"; both null: none. May cross midnight.</summary>
    public string? AlertQuietFrom { get; set; }
    public string? AlertQuietTo { get; set; }

    /// <summary>The tray menu's "Mute alerts for an hour" runs until then.</summary>
    public DateTimeOffset? AlertsMutedUntil { get; set; }

    /// <summary>Where Claude's plan limits come from: "feed" (the default for
    /// new installs: the Claude Code plugin only, no network request, no
    /// sign-in read), "auto" (the plugin's feed, and the usage endpoint
    /// whenever the feed has nothing current: what an upgrade from an older
    /// version keeps), "legacy" (the usage endpoint only, as in v1) or "off".
    /// Settings → Usage source changes it; see <see cref="ClaudeSourceMode"/>.</summary>
    public string ClaudeSource { get; set; } = NewInstallClaudeSource;

    /// <summary>What a new install reads from: the official feed only. The
    /// usage endpoint is unofficial, so it is something to opt into.</summary>
    public const string NewInstallClaudeSource = "feed";

    /// <summary>What an install from before the source switch keeps: the
    /// feed when present, the usage endpoint otherwise - the numbers the user
    /// saw before the upgrade, without asking anything.</summary>
    public const string UpgradeClaudeSource = "auto";

    /// <summary>Other tools turned off in Settings → Providers ("codex"). A tool
    /// found on this machine shows unless its id is listed here, so a tool installed
    /// later shows without a visit to Settings.</summary>
    public List<string> DisabledProviders { get; set; } = [];

    /// <summary>The first-run window was finished or closed (new installs:
    /// false until then). Upgrades from a file of an older format start true
    /// with <see cref="OnboardingVersion"/> 0, so they see the short screen once.</summary>
    public bool OnboardingCompleted { get; set; }

    /// <summary>The first-run window's version last finished
    /// (<see cref="Health.Onboarding.CurrentVersion"/>): a higher one shows the
    /// "what's new" screen once.</summary>
    public int OnboardingVersion { get; set; }

    /// <summary>The flyout's one-time tip about the plugin (shown while the
    /// numbers come from the usage endpoint) was dismissed.</summary>
    public bool PluginTipDismissed { get; set; }

    /// <summary>Once a day, download the project's published price list
    /// (<see cref="ModelCatalogUpdater"/>). Off by default: while it is off the
    /// app makes no request of its own to GitHub.</summary>
    public bool CheckForUpdates { get; set; }

    /// <summary>Show where each window is heading at the current pace: the
    /// limit time in the pill when it comes before the reset, and the pace
    /// lines in the flyout.</summary>
    public bool ShowForecast { get; set; } = true;

    /// <summary>"low" (the default) shows every forecast; "high" only those
    /// with plenty of steady readings behind them.</summary>
    public string ForecastMinConfidence { get; set; } = "low";

    /// <summary>Share cards name the project (folder) and the session; off: they say "a project".</summary>
    public bool ShareIncludeProjectNames { get; set; }

    /// <summary>"wide" (1200×630, the link-preview shape) or "portrait" (1080×1350).</summary>
    public string ShareCardShape { get; set; } = "wide";

    /// <summary>The parsed <see cref="ForecastMinConfidence"/>; anything but "high" is low.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public Forecasting.ForecastConfidence ForecastFloor =>
        string.Equals(ForecastMinConfidence?.Trim(), "high", StringComparison.OrdinalIgnoreCase)
            ? Forecasting.ForecastConfidence.High
            : Forecasting.ForecastConfidence.Low;
}

/// <summary>The parsed <see cref="AppSettings.ClaudeSource"/>.</summary>
public enum ClaudeSourceMode
{
    Auto,
    Feed,
    Legacy,
    Off
}

public static class ClaudeSourceModes
{
    /// <summary>The mode a settings value names. An unknown or empty value is
    /// <see cref="ClaudeSourceMode.Feed"/> (with a log line), like a new
    /// install: a typo must never opt anyone into reading their sign-in.</summary>
    public static ClaudeSourceMode Parse(string? value)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "auto": return ClaudeSourceMode.Auto;
            case "feed": return ClaudeSourceMode.Feed;
            case "legacy": return ClaudeSourceMode.Legacy;
            case "off": return ClaudeSourceMode.Off;
            default:
                DiagnosticLog.WriteInfo($"Ignored ClaudeSource \"{value}\" in settings.json: use auto, feed, legacy or off. Using feed.");
                return ClaudeSourceMode.Feed;
        }
    }

    /// <summary>The settings value for a mode ("auto", "feed", "legacy", "off").</summary>
    public static string ToSetting(this ClaudeSourceMode mode) => mode switch
    {
        ClaudeSourceMode.Feed => "feed",
        ClaudeSourceMode.Legacy => "legacy",
        ClaudeSourceMode.Off => "off",
        _ => "auto"
    };

    /// <summary>True when the mode may ask the unofficial usage endpoint
    /// (and so reads the sign-in Claude Code saved).</summary>
    public static bool UsesUsageEndpoint(this ClaudeSourceMode mode) =>
        mode is ClaudeSourceMode.Auto or ClaudeSourceMode.Legacy;
}

public static class AppSettingsExtensions
{
    /// <summary>Settings → Providers shows the tool (it is not in <see cref="AppSettings.DisabledProviders"/>).</summary>
    public static bool IsProviderEnabled(this AppSettings settings, string providerId) =>
        !(settings.DisabledProviders ?? []).Contains(providerId, StringComparer.OrdinalIgnoreCase);

    /// <summary>Turns a tool on or off in Settings → Providers.</summary>
    public static void SetProviderEnabled(this AppSettings settings, string providerId, bool enabled)
    {
        var list = (settings.DisabledProviders ?? []).Where(id => !string.Equals(id, providerId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!enabled)
            list.Add(providerId);
        settings.DisabledProviders = list;
    }

    /// <summary>The session card layout this settings file describes.</summary>
    public static ViewModels.CardLayout CardLayout(this AppSettings settings) =>
        ViewModels.CardLayout.Parse(settings.CardFields, settings.ShowContextBar, settings.ShowGitDetails);

    /// <summary>Stores a layout (without saving the file).</summary>
    public static void SetCardLayout(this AppSettings settings, ViewModels.CardLayout layout)
    {
        settings.CardFields = layout.ToSetting();
        settings.ShowContextBar = layout.ShowContextBar;
        settings.ShowGitDetails = layout.ShowGitDetails;
    }
}

/// <summary>Loads and saves <c>settings.json</c>. Saving is atomic and keeps
/// the previous file as <c>settings.json.bak</c>; a file that fails to parse
/// falls back to that backup, then to the defaults. Neither ever throws:
/// settings must never block start-up or crash the app.</summary>
public static class SettingsService
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static AppSettings Load() => Load(AppPaths.SettingsPath);

    public static void Save(AppSettings settings) => Save(settings, AppPaths.SettingsPath);

    internal static string BackupPath(string path) => path + ".bak";

    internal static AppSettings Load(string path)
    {
        // A missing file means a first start (or a reset by hand): defaults,
        // not the backup.
        if (!File.Exists(path))
            return Defaults(path, firstStart: true);

        if (TryRead(path, out var settings, out var error))
            return settings;

        var backup = BackupPath(path);
        if (!File.Exists(backup))
        {
            DiagnosticLog.WriteInfo($"Settings file could not be read ({error}); using the defaults.");
            return Defaults(path);
        }
        if (TryRead(backup, out var fromBackup, out var backupError))
        {
            DiagnosticLog.WriteInfo($"Settings file could not be read ({error}); loaded the backup {Path.GetFileName(backup)}.");
            return fromBackup;
        }

        DiagnosticLog.WriteInfo($"Settings file could not be read ({error}) and neither could the backup ({backupError}); using the defaults.");
        return Defaults(path);
    }

    /// <summary>The usage endpoint's cache, relative to the settings file's
    /// folder: proof that an older version ran here and read the endpoint.</summary>
    internal static string LegacyCachePath(string settingsPath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsPath)) ?? ".", "cache", "claude-usage.json");

    /// <summary>The defaults, for a data folder without a (readable)
    /// settings file. A new install starts on the official feed only. But v1
    /// on macOS wrote the file only once something was changed, and a broken
    /// file was written by an earlier run too: a folder that already holds
    /// the usage endpoint's cache is an upgrade, and keeps reading as before.</summary>
    private static AppSettings Defaults(string path, bool firstStart = false)
    {
        var settings = new AppSettings();
        if (File.Exists(LegacyCachePath(path)))
        {
            settings.ClaudeSource = AppSettings.UpgradeClaudeSource;
            settings.Theme = AppSettings.UpgradeTheme;
            settings.OnboardingCompleted = true;
            DiagnosticLog.WriteInfo("No readable settings file, but an older version's usage cache is here: plan limits keep " +
                                    "coming from the plugin and Claude's usage service (ClaudeSource auto).");
        }
        else if (firstStart)
        {
            DiagnosticLog.WriteInfo("First start: plan limits come from the Claude Code plugin only (ClaudeSource feed); " +
                                    "Settings → Usage source can add Claude's usage service.");
        }
        return settings;
    }

    internal static void Save(AppSettings settings, string path)
    {
        try
        {
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(settings, WriteOptions), backupPath: BackupPath(path));
        }
        catch (Exception ex)
        {
            // Best-effort; failing to persist settings shouldn't crash the app.
            DiagnosticLog.WriteError("SettingsService.Save", ex);
        }
    }

    private static bool TryRead(string path, out AppSettings settings, out string? error)
    {
        try
        {
            var json = File.ReadAllText(path);
            var (version, keys) = ReadFormat(json);
            settings = JsonSerializer.Deserialize<AppSettings>(json)
                ?? throw new JsonException("the file holds null");
            Migrate(settings, version, keys);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            settings = new AppSettings();
            error = ex.Message;
            return false;
        }
    }

    /// <summary>The file's version (a file without the key is a v1 file) and
    /// the top-level keys it names.</summary>
    private static (int Version, IReadOnlySet<string> Keys) ReadFormat(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            return (1, new HashSet<string>());
        var version = root.TryGetProperty(nameof(AppSettings.SchemaVersion), out var value) && value.TryGetInt32(out var number)
            ? number
            : 1;
        return (version, root.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal));
    }

    /// <summary>Brings a file of an older format up to this build's. Each
    /// step changes only what its version changed; a file from a newer build
    /// is left as it is (its unknown keys are kept by UnknownKeys).</summary>
    /// <param name="keys">The top-level keys the file names.</param>
    internal static void Migrate(AppSettings settings, int fromVersion, IReadOnlySet<string> keys)
    {
        switch (fromVersion)
        {
            case < AppSettings.CurrentSchemaVersion:
                // 1 → 2: nothing to convert; version 2 only adds the version
                // key and keeps unknown keys.
                // 2 → 3: new installs default to the feed only; a file written
                // before that keeps reading the way it did (feed, then the
                // usage endpoint).
                if (fromVersion < 3 && !keys.Contains(nameof(AppSettings.ClaudeSource)))
                    settings.ClaudeSource = AppSettings.UpgradeClaudeSource;
                // 3 → 4: new installs follow the computer's theme; a file
                // without a Theme ran dark.
                if (!keys.Contains(nameof(AppSettings.Theme)))
                    settings.Theme = AppSettings.UpgradeTheme;
                // 4 → 5: a new install sees the three-step first-run window; a
                // file written before it is an upgrade, which sees the short one.
                if (!keys.Contains(nameof(AppSettings.OnboardingCompleted)))
                    settings.OnboardingCompleted = true;
                // 5 → 6: a docked pill now docks in the taskbar (the default place moved
                // there); one that was dragged stays where it was.
                if (!keys.Contains(nameof(AppSettings.DockMode)))
                    settings.DockMode = ViewModels.DockModes.ToSetting(settings.WidgetDocked ? ViewModels.DockMode.Taskbar : ViewModels.DockMode.Free);
                // Later steps go above this line.
                settings.SchemaVersion = AppSettings.CurrentSchemaVersion;
                break;
            case > AppSettings.CurrentSchemaVersion:
                DiagnosticLog.WriteInfo($"Settings were written by a newer version (format {fromVersion}); keeping the settings this version doesn't know.");
                settings.SchemaVersion = fromVersion;
                break;
            default:
                settings.SchemaVersion = fromVersion;
                break;
        }
    }
}
