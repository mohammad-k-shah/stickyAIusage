using System.Globalization;
using StickyAIusage.Models;

namespace StickyAIusage.ViewModels;

/// <summary>What a Claude Code session is doing, as the session lights show it.</summary>
public enum ActivityState { NeedsYou, Working, RateLimited, NoSignal, Done, Idle }

/// <summary>
/// One session's activity, from the companion plugin's record: working,
/// waiting for you (and for what), done, rate limited, or "no signal" when a
/// busy session's record stopped being rewritten (it crashed or was killed
/// without saying it ended).
/// </summary>
public sealed record SessionActivity(
    string SessionId,
    string Project,
    ActivityState State,
    DateTimeOffset Since,
    DateTimeOffset LastHeardAt,
    FeedAttention? Attention,
    TimeSpan? LastTurn,
    DateTimeOffset? ResetsAt)
{
    /// <summary>The card's status chip: "Needs you: permission", "Working",
    /// "Done 3m ago", "Rate limited · resets 4:30 PM", "No signal".</summary>
    public string Chip(DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null) => State switch
    {
        ActivityState.NeedsYou => "Needs you: " + WaitShort,
        ActivityState.Working => "Working",
        ActivityState.RateLimited => ResetsAt is { } reset && reset > now
            ? "Rate limited · resets " + Format.ShortTime(reset, zone, culture)
            : "Rate limited",
        ActivityState.NoSignal => "No signal",
        ActivityState.Done => "Done " + Format.Ago(now - Since),
        _ => "Idle " + Format.Ago(now - Since)
    };

    /// <summary>One line of the pill's tooltip: "acme-webapp — waiting for
    /// permission to use Bash (2m)".</summary>
    public string Line(DateTimeOffset now, TimeZoneInfo? zone = null, CultureInfo? culture = null) => Project + " — " + State switch
    {
        ActivityState.NeedsYou => $"{WaitLong} ({Format.Duration(now - Since)})",
        ActivityState.Working => $"working ({Format.Duration(now - Since)})",
        ActivityState.RateLimited => ResetsAt is { } reset && reset > now
            ? "rate limited, resets " + Format.ShortTime(reset, zone, culture)
            : "rate limited",
        ActivityState.NoSignal => $"no signal for {Format.Duration(now - LastHeardAt)}",
        ActivityState.Done => "done " + Format.Ago(now - Since),
        _ => "idle " + Format.Ago(now - Since)
    };

    /// <summary>"permission", "a question", "input".</summary>
    public string WaitShort => Attention?.Type switch
    {
        SessionActivities.Permission => "permission",
        SessionActivities.Question => "a question",
        SessionActivities.Input => "input",
        _ => "input"
    };

    /// <summary>"waiting for permission to use Bash", "waiting for your answer to a question".</summary>
    public string WaitLong => Attention?.Type switch
    {
        SessionActivities.Permission when Attention.Tool is { Length: > 0 } tool => "waiting for permission to use " + ToolLabel(tool),
        SessionActivities.Permission => "waiting for permission",
        SessionActivities.Question => "waiting for your answer to a question",
        _ => "waiting for your input"
    };

    /// <summary>"mcp__github__create_issue" → "github create_issue": an MCP tool's raw name reads badly.</summary>
    private static string ToolLabel(string tool) =>
        tool.StartsWith("mcp__", StringComparison.Ordinal) && tool.Split("__", 3) is [_, var server, var name]
            ? $"{server} {name}"
            : tool;
}

/// <summary>Builds the <see cref="SessionActivity"/> list from the plugin's records.</summary>
public static class SessionActivities
{
    public const string Permission = "permission";
    public const string Question = "question";
    public const string Input = "input";

    /// <summary>A busy session's record is rewritten at least every three
    /// minutes (a one-minute heartbeat, a rewrite after two quiet minutes); one
    /// older than this has lost its session.</summary>
    public static readonly TimeSpan NoSignalAfter = TimeSpan.FromMinutes(4);

    /// <summary>How long a finished, idle or silent session keeps its dot.</summary>
    public static readonly TimeSpan RecentFor = TimeSpan.FromMinutes(10);

    /// <summary>Every session the plugin describes that hasn't ended, the ones
    /// that need you first (longest wait first), then working, rate limited,
    /// no signal, then done and idle (most recent first).</summary>
    public static IReadOnlyList<SessionActivity> From(IReadOnlyList<FeedSession> sessions, DateTimeOffset now)
    {
        var list = new List<SessionActivity>();
        foreach (var session in sessions)
        {
            if (session.HasEnded || string.IsNullOrEmpty(session.SessionId))
                continue;
            var heard = session.ObservedAt > now ? now : session.ObservedAt;
            var since = session.StateSince is { } s ? (s > now ? now : s) : heard;
            var state = StateOf(session.State);
            // A record that stopped being rewritten while the session was busy.
            if ((state is ActivityState.Working or ActivityState.NeedsYou or ActivityState.RateLimited) &&
                now - heard > NoSignalAfter)
                state = ActivityState.NoSignal;
            var reset = session.RateLimits?.FirstOrDefault(w => w.Kind == UsageWindowKinds.FiveHour)?.ResetsAt;
            list.Add(new SessionActivity(session.SessionId, ProjectOf(session.Cwd), state, since, heard,
                state == ActivityState.NeedsYou ? session.Attention ?? new FeedAttention(Input, null) : null,
                session.LastTurn, reset));
        }
        return list
            .OrderBy(a => Rank(a.State))
            .ThenBy(a => a.State is ActivityState.NeedsYou or ActivityState.Working ? a.Since.UtcTicks : -a.Since.UtcTicks)
            .ThenBy(a => a.SessionId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The ones the pill shows: busy ones, and the others for ten minutes.</summary>
    public static bool IsRecent(SessionActivity activity, DateTimeOffset now) => activity.State switch
    {
        ActivityState.NeedsYou or ActivityState.Working or ActivityState.RateLimited => true,
        ActivityState.NoSignal => now - activity.LastHeardAt <= RecentFor,
        _ => now - activity.Since <= RecentFor
    };

    /// <summary>The plugin's state word; an unknown one is taken as busy (a
    /// later plugin's state is more likely work than nothing).</summary>
    internal static ActivityState StateOf(string? state) => state?.ToLowerInvariant() switch
    {
        "needs_you" => ActivityState.NeedsYou,
        "done" => ActivityState.Done,
        "idle" or "started" or null or "" => ActivityState.Idle,
        "rate_limited" => ActivityState.RateLimited,
        _ => ActivityState.Working
    };

    private static int Rank(ActivityState state) => state switch
    {
        ActivityState.NeedsYou => 0,
        ActivityState.Working => 1,
        ActivityState.RateLimited => 2,
        ActivityState.NoSignal => 3,
        _ => 4
    };

    /// <summary>The folder's name as the cards show it; "Claude Code" without one.</summary>
    internal static string ProjectOf(string? cwd)
    {
        if (string.IsNullOrWhiteSpace(cwd))
            return "Claude Code";
        var folder = cwd.TrimEnd('/', '\\').Split('/', '\\').Last();
        return string.IsNullOrEmpty(folder) ? cwd : Format.ProjectName(folder);
    }
}

/// <summary>One dot on the pill.</summary>
public enum LightKind { NeedsYou, Working, Done, RateLimited, NoSignal }

/// <summary>
/// The pill's session lights: up to four dots (needs you first), "+N" beyond
/// them, one dot in the compact pill (amber if any session needs you, green if
/// any is working, none otherwise) and a tooltip line per session. Null when
/// switched off or when no session is recent.
/// </summary>
public sealed record SessionLights(
    IReadOnlyList<LightKind> Dots,
    string? MoreText,
    LightKind? CompactDot,
    string Tooltip,
    int NeedsYou,
    int Working,
    int Done,
    int RateLimited = 0,
    int NoSignal = 0)
{
    public const int MaxDots = 4;

    /// <summary>The flyout's line when no plugin record exists.</summary>
    public const string ConnectHint =
        "Connect to Claude Code to see which session needs you: install the Sticky AI Usage plugin.";

    /// <summary>The link under <see cref="ConnectHint"/>: opens the Connect step.</summary>
    public const string ConnectAction = "Connect to Claude Code…";

    public static SessionLights? From(IReadOnlyList<SessionActivity> activities, DateTimeOffset now, bool enabled,
        TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        if (!enabled)
            return null;
        var shown = activities.Where(a => SessionActivities.IsRecent(a, now)).ToList();
        if (shown.Count == 0)
            return null;
        var dots = shown.Take(MaxDots).Select(a => Kind(a.State)).ToList();
        var needsYou = shown.Count(a => a.State == ActivityState.NeedsYou);
        var working = shown.Count(a => a.State == ActivityState.Working);
        LightKind? compact = needsYou > 0 ? LightKind.NeedsYou : working > 0 ? LightKind.Working : null;
        return new SessionLights(
            dots,
            shown.Count > MaxDots ? $"+{shown.Count - MaxDots}" : null,
            compact,
            string.Join("\n", shown.Select(a => a.Line(now, zone, culture))),
            needsYou,
            working,
            shown.Count(a => a.State is ActivityState.Done or ActivityState.Idle),
            shown.Count(a => a.State == ActivityState.RateLimited),
            shown.Count(a => a.State == ActivityState.NoSignal));
    }

    public static LightKind Kind(ActivityState state) => state switch
    {
        ActivityState.NeedsYou => LightKind.NeedsYou,
        ActivityState.Working => LightKind.Working,
        ActivityState.RateLimited => LightKind.RateLimited,
        ActivityState.NoSignal => LightKind.NoSignal,
        _ => LightKind.Done
    };

    /// <summary>What a screen reader says for the dots: "1 needs you, 2 working".</summary>
    public string Summary => string.Join(", ", new[]
    {
        NeedsYou > 0 ? $"{NeedsYou} needs you" : null,
        Working > 0 ? $"{Working} working" : null,
        RateLimited > 0 ? $"{RateLimited} rate limited" : null,
        NoSignal > 0 ? $"{NoSignal} no signal" : null,
        Done > 0 ? $"{Done} done" : null
    }.Where(p => p is not null));
}
