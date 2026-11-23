namespace StickyAIusage.Models;

/// <summary>How far a number can be trusted, by where it came from. The
/// order matters: a lower value is more trustworthy.</summary>
public enum SourceTrust
{
    /// <summary>From a documented interface of the tool itself (Claude Code's plugin API).</summary>
    Official,

    /// <summary>Computed from the tool's local files, such as transcripts.</summary>
    Derived,

    /// <summary>From an undocumented endpoint; opt-in.</summary>
    Unofficial
}

/// <summary>Whether a reading can be shown as current.</summary>
public enum UsageHealth
{
    Ok,

    /// <summary>A real reading, but not a fresh one (the last fetch failed or was skipped).</summary>
    Stale,

    /// <summary>Only a new sign-in can bring the numbers back.</summary>
    NeedsSignIn,

    /// <summary>No reading at all yet.</summary>
    Unavailable
}

/// <summary>Names of usage windows. An open set: a provider may report a kind
/// not listed here, and the UI shows it by name.</summary>
public static class UsageWindowKinds
{
    public const string FiveHour = "five_hour";
    public const string SevenDay = "seven_day";
    public const string SevenDayOpus = "seven_day_opus";
    public const string SevenDaySonnet = "seven_day_sonnet";
    public const string SpendLimit = "spend_limit";
    public const string MonthSpend = "month_spend";
}

/// <summary>One limit window: how much of it is used and when it starts over.
/// <paramref name="Length"/> is the window's span when known (5 hours, 7 days).</summary>
public sealed record UsageWindow(string Kind, double PercentUsed, DateTimeOffset? ResetsAt, TimeSpan? Length);

/// <summary>A spending cap. Dollars are optional: some sources only know the
/// percentage. <paramref name="Period"/> is "month" and similar.</summary>
public sealed record SpendCap(decimal? UsedUsd, decimal? LimitUsd, double PercentUsed, DateTimeOffset? ResetsAt, string? Period);
