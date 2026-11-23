using System.Globalization;
using System.Text;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Updates;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Health;

/// <summary>What the plugin wrote to <c>feed/plugin.json</c> when a Claude Code
/// session last started with it.</summary>
public sealed record PluginMarker(string? PluginVersion, string? ClaudeCodeVersion, string? Surface, DateTimeOffset? ObservedAt);

/// <summary>Everything the health checks look at, gathered by
/// <see cref="HealthProbe"/> (files) and the window (its latest reading).
/// <see cref="HealthCheck.Build"/> turns it into items without touching
/// anything, so every status is a unit test.</summary>
public sealed record HealthFacts
{
    public required DateTimeOffset Now { get; init; }
    public bool IsMac { get; init; }

    /// <summary>Claude Code's folder (<c>~/.claude</c> or <c>CLAUDE_CONFIG_DIR</c>).</summary>
    public required string ClaudeDirectory { get; init; }
    public bool ClaudeDirectoryExists { get; init; }
    public int TranscriptCount { get; init; }

    /// <summary>The Claude Code version that wrote the newest transcript.</summary>
    public string? ClaudeCodeVersion { get; init; }

    public PluginMarker? Marker { get; init; }

    /// <summary>When the plugin last wrote a session record.</summary>
    public DateTimeOffset? NewestRecordAt { get; init; }

    /// <summary>Claude Code lists the plugin as installed (<c>plugins/installed_plugins.json</c>).</summary>
    public bool PluginInstalled { get; init; }

    /// <summary>The window's latest reading; null before the first refresh.</summary>
    public UsageSnapshot? Usage { get; init; }
    public ClaudeSourceMode Mode { get; init; } = ClaudeSourceMode.Feed;

    /// <summary>Claude Code sessions with work today; null: not counted.</summary>
    public int? SessionsToday { get; init; }

    public bool AlertsEnabled { get; init; } = true;

    /// <summary>When this run last sent a test alert from Settings; null: not yet.</summary>
    public DateTimeOffset? LastTestAlertAt { get; init; }

    public bool StartAtLogin { get; init; }
    public bool CheckForUpdates { get; init; }

    /// <summary>This build's release version; null: not one (a build from source without a version).</summary>
    public string? RunningVersion { get; init; }

    /// <summary>When the app last asked GitHub for a newer version; null: not yet.</summary>
    public DateTimeOffset? UpdateCheckedAt { get; init; }

    /// <summary>A newer version, from the last answer; null: none known.</summary>
    public UpdateNotice? Update { get; init; }

    public bool CodexPresent { get; init; }
    public bool CodexEnabled { get; init; } = true;
    public string CodexSessionsFolder { get; init; } = "~/.codex/sessions";
}

/// <summary>
/// Settings → Health: the checks a new user or a bug report needs, each with
/// what to do when it isn't right. Pure: <see cref="Build"/> reads only its
/// facts. The texts are the same in both apps.
/// </summary>
public static class HealthCheck
{
    /// <summary>The oldest Claude Code whose terminal runs plugin hooks modules
    /// (the Desktop Code tab: 2.1.286). Checked on code.claude.com, 2026-11-20.</summary>
    public static readonly Version MinimumClaudeCode = new(2, 1, 287);

    /// <summary>The oldest plugin with the session lights.</summary>
    public static readonly Version MinimumPluginForLights = new(2, 2, 0);

    public const string SignInCommand = ClaudeCommands.SignIn;
    public const string UpdateClaudeCodeCommand = "claude update";
    public const string UpdatePluginCommand = "claude plugin update " + PluginInstaller.PluginId;
    public const string ClaudeCodeSetupUrl = "https://code.claude.com/docs/en/setup";
    public const string ReleasesUrl = "https://github.com/mohammad-k-shah/stickyAIusage/releases";

    public const string NotFoundDetail = "Claude Code was not found. Install it, or open the Code tab in Claude Desktop once.";

    public const string WslNote =
        " If you use Claude Code only inside WSL, the widget can't see it: plugins don't load in WSL sessions of Claude Desktop.";

    public const string NotConnectedDetail =
        "Not connected: the Claude Code plugin hasn't written anything here yet. Connect installs it with one command.";

    /// <summary>Not connected while Legacy or Off takes no limits from the plugin.</summary>
    public static string NotConnectedOptionalDetail(ClaudeSourceMode mode) =>
        (mode == ClaudeSourceMode.Off ? "Not connected. Plan limits are off;" : "Not connected. Your limits come from Claude's usage service;") +
        " the plugin adds the session lights and Claude Code's own session costs.";

    public const string InstalledNotConnectedDetail =
        "The plugin is installed, but Claude Code hasn't run it yet: start a new Claude Code session and send any message. " +
        "If nothing arrives, your organization may not allow user plugins; legacy mode is available (Settings → Usage source).";

    public const string NoLimitsDetail = "No limits yet: send any message in Claude Code.";

    /// <summary>The items in the order Settings lists them.</summary>
    public static IReadOnlyList<HealthItem> Build(HealthFacts facts) =>
    [
        ClaudeCode(facts),
        ClaudeVersion(facts),
        Feed(facts),
        Limits(facts),
        Legacy(facts),
        Sessions(facts),
        Notifications(facts),
        Startup(facts),
        Codex(facts),
        Update(facts)
    ];

    private static HealthItem ClaudeCode(HealthFacts f)
    {
        var folder = DiagnosticLog.HideHome(f.ClaudeDirectory);
        if (f.ClaudeDirectoryExists && f.TranscriptCount > 0)
            return new("claude-code", "Claude Code", HealthStatus.Ok,
                $"Found: {folder}, {Count(f.TranscriptCount, "session transcript")}.");
        var wsl = f.IsMac ? "" : WslNote;
        // Installed but never used: nothing to get, only to start.
        if (f.ClaudeDirectoryExists)
            return new("claude-code", "Claude Code", HealthStatus.Warn,
                $"{folder} has no session transcripts yet: start Claude Code (or the Code tab in Claude Desktop) and send a message.{wsl}");
        return new("claude-code", "Claude Code", HealthStatus.Fail, NotFoundDetail + wsl, HealthActionIds.GetClaudeCode, "Get Claude Code");
    }

    private static HealthItem ClaudeVersion(HealthFacts f)
    {
        var version = ParseVersion(f.ClaudeCodeVersion);
        if (version is null)
            return new("claude-version", "Claude Code version", HealthStatus.Info,
                "Not known yet: it is read from the newest session transcript.");
        if (version < MinimumClaudeCode)
            return new("claude-version", "Claude Code version", HealthStatus.Warn,
                $"Update Claude Code to use the official feed (yours: {f.ClaudeCodeVersion}; the plugin needs {MinimumClaudeCode} or later).",
                HealthActionIds.CopyUpdateClaudeCode, "Copy update command");
        return new("claude-version", "Claude Code version", HealthStatus.Ok, $"{f.ClaudeCodeVersion}, from the newest session.");
    }

    private static HealthItem Feed(HealthFacts f)
    {
        const string title = "Claude Code plugin";
        var lastWrite = Latest(f.NewestRecordAt, f.Marker?.ObservedAt);
        if (lastWrite is null)
        {
            // Legacy and Off take no limits from it: then it is an extra (lights, exact costs), not a fault.
            var optional = f.Mode is ClaudeSourceMode.Legacy or ClaudeSourceMode.Off;
            if (f.PluginInstalled)
                return new("feed", title, optional ? HealthStatus.Info : HealthStatus.Warn, InstalledNotConnectedDetail);
            return optional
                ? new("feed", title, HealthStatus.Info, NotConnectedOptionalDetail(f.Mode), HealthActionIds.Connect, "Connect")
                : new("feed", title, HealthStatus.Fail, NotConnectedDetail, HealthActionIds.Connect, "Connect");
        }

        var version = f.Marker?.PluginVersion;
        var named = version is null ? "Connected" : $"Connected (plugin {version})";
        var ago = Format.Ago(Clamp(f.Now - lastWrite.Value));
        if (ParseVersion(version) is { } plugin && plugin < MinimumPluginForLights)
            return new("feed", title, HealthStatus.Warn,
                $"{named}, last update {ago}. The session lights need plugin {MinimumPluginForLights.ToString(2)} or later: update it.",
                HealthActionIds.CopyUpdatePlugin, "Copy update command");
        return new("feed", title, HealthStatus.Ok, $"{named}, last update {ago}.");
    }

    private static HealthItem Limits(HealthFacts f)
    {
        const string title = "Plan limits";
        if (f.Mode == ClaudeSourceMode.Off)
            return new("limits", title, HealthStatus.Info, "Turned off (Settings → Usage source). The session cards still show.",
                HealthActionIds.OpenUsageSource, "Usage source");
        if (f.Usage is not { } usage)
            return new("limits", title, HealthStatus.Info, "Not read yet: the first refresh is still running.");

        var reading = usage.ToProviderUsage();
        var source = SourceName(usage);
        var ago = Format.Ago(Clamp(f.Now - usage.FetchedAt));
        switch (reading.Health)
        {
            case UsageHealth.NeedsSignIn:
                return new("limits", title, HealthStatus.Fail,
                    $"Your Claude sign-in has expired: run {SignInCommand} in a terminal, or /login inside Claude Code.",
                    HealthActionIds.CopySignIn, "Copy command");
            case UsageHealth.Unavailable:
                return usage.StatusDetail is ClaudeFeedUsageSource.WaitingDetail or ClaudeFeedUsageSource.NoLimitsDetail
                    ? new("limits", title, HealthStatus.Warn, NoLimitsDetail)
                    : new("limits", title, HealthStatus.Fail,
                        "Couldn't read your usage. The log has the reason: diagnostics.log in the log folder.",
                        HealthActionIds.OpenLogFolder, "Open log folder");
            case UsageHealth.Stale when usage.StatusDetail == ClaudeOAuthUsageSource.TokenExpiredDetail:
                return new("limits", title, HealthStatus.Warn,
                    $"{Numbers(reading)} from {source}, last updated {ago}. {usage.StatusDetail}",
                    HealthActionIds.OpenUsageSource, "Usage source");
            case UsageHealth.Stale:
                return new("limits", title, HealthStatus.Warn,
                    $"{Numbers(reading)} from {source}, last updated {ago}: the last try didn't get new numbers. The log says why.",
                    HealthActionIds.OpenLogFolder, "Open log folder");
            default:
                return new("limits", title, HealthStatus.Ok, $"{Numbers(reading)} from {source}, {ago}.");
        }
    }

    private static HealthItem Legacy(HealthFacts f) =>
        f.Mode.UsesUsageEndpoint()
            ? new("legacy", "Legacy mode", HealthStatus.Info,
                $"On ({UsageSourceOptions.For(f.Mode).Label}). {UsageSourceOptions.UnofficialWarning}",
                HealthActionIds.OpenUsageSource, "Usage source")
            : new("legacy", "Legacy mode", HealthStatus.Ok, "Off: the widget never reads your Claude sign-in.");

    private static HealthItem Sessions(HealthFacts f) => new("sessions", "Sessions today", HealthStatus.Info,
        f.SessionsToday switch
        {
            null => "Not counted yet.",
            0 => "No Claude Code sessions today yet.",
            var n => $"{Count(n.Value, "Claude Code session")} today."
        });

    private static HealthItem Notifications(HealthFacts f)
    {
        if (!f.AlertsEnabled)
            return new("notifications", "Notifications", HealthStatus.Info, "Turned off (Settings → Usage alerts).");
        var where = f.IsMac
            ? " On a Mac they come from Script Editor: allow it in System Settings → Notifications."
            : "";
        var detail = f.LastTestAlertAt is { } at
            ? $"On. A test alert was sent at {Format.ShortTime(at)}: if none appeared, check Focus / Do not disturb.{where}"
            : $"On. Send a test alert to see where they appear.{where}";
        return new("notifications", "Notifications", HealthStatus.Info, detail, HealthActionIds.SendTestAlert, "Send a test alert");
    }

    private static HealthItem Startup(HealthFacts f)
    {
        var title = f.IsMac ? "Launch at login" : "Start with Windows";
        return f.StartAtLogin
            ? new("startup", title, HealthStatus.Info, "On: the widget starts when you sign in.", HealthActionIds.ToggleStartup, "Turn off")
            : new("startup", title, HealthStatus.Info, "Off: start the widget yourself after you sign in.", HealthActionIds.ToggleStartup, "Turn on");
    }

    private static HealthItem Codex(HealthFacts f) => new("codex", "Codex", HealthStatus.Info,
        !f.CodexPresent ? $"Not found on this computer ({DiagnosticLog.HideHome(f.CodexSessionsFolder)})."
        : f.CodexEnabled ? "Found: its limits show beside Claude's."
        : "Found, turned off in Settings.");

    private static HealthItem Update(HealthFacts f)
    {
        if (!f.CheckForUpdates)
            return new("update", "Updates", HealthStatus.Info,
                "Off: the app makes no network request of its own. New versions are on the Releases page.",
                HealthActionIds.OpenReleases, "Releases");
        if (f.Update is { } update)
            return new("update", "Updates", HealthStatus.Warn, update.HealthDetail, HealthActionIds.OpenReleases, "What's new");
        var detail = f.RunningVersion is null
            ? "On, but this build has no release version, so it doesn't look for newer ones; the price list is checked once a day."
            : f.UpdateCheckedAt is { } at
                ? $"On: GitHub was asked {Format.Ago(Clamp(f.Now - at))}; {f.RunningVersion} is the newest version."
                : "On: once a day the app asks GitHub whether a newer version is out. Not asked yet (the first time a minute after start).";
        return new("update", "Updates", f.UpdateCheckedAt is null ? HealthStatus.Info : HealthStatus.Ok, detail,
            HealthActionIds.OpenReleases, "Releases");
    }

    /// <summary>The plain-text report for a bug report: which build, then every
    /// item. The home folder reads "~", as in the log.</summary>
    public static string Report(IReadOnlyList<HealthItem> items, string identity, DateTimeOffset now)
    {
        var text = new StringBuilder();
        text.AppendLine("Sticky AI Usage health report");
        text.AppendLine(identity);
        text.AppendLine($"{System.Runtime.InteropServices.RuntimeInformation.OSDescription} · .NET {Environment.Version}");
        text.AppendLine($"Checked {now.ToLocalTime().ToString("yyyy-MM-dd HH:mm zzz", CultureInfo.InvariantCulture)}");
        text.AppendLine();
        foreach (var item in items)
            text.AppendLine($"[{item.StatusLabel}] {item.Title}: {item.Detail}");
        return DiagnosticLog.HideHome(text.ToString());
    }

    /// <summary>"2.1.325" (or "2.1.325-beta", "2.1.325 (Claude Code)") as a version; null otherwise.</summary>
    public static Version? ParseVersion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;
        var digits = new string(text.Trim().TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray()).TrimEnd('.');
        return Version.TryParse(digits, out var version) && digits.Count(c => c == '.') >= 1 ? version : null;
    }

    private static string SourceName(UsageSnapshot usage) => usage.SourceId switch
    {
        ClaudeFeedUsageSource.Id => "the Claude Code plugin (official)",
        ClaudeOAuthUsageSource.Id => "Claude's usage service (unofficial)",
        _ => usage.Trust switch
        {
            SourceTrust.Official => "an official source",
            SourceTrust.Derived => "local files",
            _ => "an unofficial source"
        }
    };

    /// <summary>"5-hour 43% · weekly 12%", or the spend cap.</summary>
    private static string Numbers(ProviderUsage reading)
    {
        if (reading.Spend is { } spend)
            return spend is { UsedUsd: { } used, LimitUsd: { } limit }
                ? $"{Format.Usd(used)} of {Format.Usd(limit)} ({Format.WholePercent(spend.PercentUsed)})"
                : $"{Format.WholePercent(spend.PercentUsed)} of your spend limit";
        var parts = reading.Windows.Select(w => w.Kind switch
        {
            UsageWindowKinds.FiveHour => $"5-hour {Format.WholePercent(w.PercentUsed)}",
            UsageWindowKinds.SevenDay => $"weekly {Format.WholePercent(w.PercentUsed)}",
            _ => null
        }).OfType<string>().ToList();
        if (parts.Count == 0)
            parts = reading.Windows.Take(2).Select(w => $"{Format.WindowName(w.Kind)} {Format.WholePercent(w.PercentUsed)}").ToList();
        var text = string.Join(" · ", parts);
        return text.Length == 0 ? "Numbers" : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string Count(int n, string noun) =>
        n.ToString("N0", CultureInfo.InvariantCulture) + " " + noun + (n == 1 ? "" : "s");

    private static DateTimeOffset? Latest(DateTimeOffset? a, DateTimeOffset? b) =>
        a is null ? b : b is null ? a : a > b ? a : b;

    private static TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;
}
