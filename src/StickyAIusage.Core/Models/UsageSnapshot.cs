using System.Text.Json.Serialization;

namespace StickyAIusage.Models;

/// <summary>How the account is billed, which decides what the widget shows.</summary>
public enum UsageMode
{
    /// <summary>No usable reading yet (first launch, signed out, endpoint down).</summary>
    Unknown,

    /// <summary>A fixed monthly spend cap in dollars (Enterprise and similar
    /// credit-based plans): "$96.75 · 48%".</summary>
    MonthlySpend,

    /// <summary>Rolling usage windows (Pro, Max): a 5-hour window and a
    /// weekly window as percentages, plus optional extra-usage credits.</summary>
    PlanLimits
}

/// <summary>
/// Normalized view of the account's usage, read from Anthropic's usage
/// endpoint (GET /api/oauth/usage). Field names here are ours; the raw JSON
/// shape is parsed and mapped in <see cref="Services.ClaudeOAuthUsageSource"/>
/// so that unrecognized / renamed upstream fields never break the app.
/// </summary>
public sealed record UsageSnapshot
{
    public UsageMode Mode { get; init; }

    /// <summary>True if the endpoint returned a usable monthly spend/cap pair
    /// (<see cref="UsageMode.MonthlySpend"/>).</summary>
    public bool HasMonthlySpend { get; init; }

    public decimal MonthlySpendUsedUsd { get; init; }
    public decimal MonthlySpendLimitUsd { get; init; }
    public double MonthlySpendPercent { get; init; }

    /// <summary>True when the source knows only how much of the spend limit is
    /// used, not the dollars (Claude Code's feed reports a gateway's
    /// <c>spend_limit</c> as a percentage): the layout then shows "63% of your
    /// limit", and the two dollar fields are zero and meaningless.</summary>
    public bool MonthlySpendPercentOnly { get; init; }

    /// <summary>When the spend limit starts over, if the source said.</summary>
    public DateTimeOffset? MonthlySpendResetsAt { get; init; }

    /// <summary>"normal" | "warning" | "critical" etc., as reported by the API's
    /// spend.severity field. Not currently used for color (we use our own
    /// thresholds) but kept for diagnostics / future use.</summary>
    public string? Severity { get; init; }

    // Rolling windows (PlanLimits). Null on monthly-cap (Enterprise) accounts.
    public double? FiveHourUtilizationPercent { get; init; }
    public DateTimeOffset? FiveHourResetsAt { get; init; }
    public double? WeeklyUtilizationPercent { get; init; }
    public DateTimeOffset? WeeklyResetsAt { get; init; }
    public double? WeeklyOpusUtilizationPercent { get; init; }
    public double? WeeklySonnetUtilizationPercent { get; init; }

    /// <summary>Windows of a kind this snapshot has no field for (a new
    /// window Claude Code starts reporting), kept so the flyout can list them
    /// by name instead of dropping them. Only the feed reports them, and the
    /// endpoint's disk cache never holds them (a list would also break record
    /// equality after a round trip).</summary>
    [JsonIgnore]
    public IReadOnlyList<UsageWindow> OtherWindows { get; init; } = [];

    /// <summary>Extra-usage credits on a PlanLimits account: pay-as-you-go
    /// spending allowed once the plan's windows are used up. Null when the
    /// endpoint didn't say; false when the account has it turned off.</summary>
    public bool? ExtraUsageEnabled { get; init; }
    public decimal? ExtraUsageLimit { get; init; }
    public decimal? ExtraUsageUsed { get; init; }

    /// <summary>ISO 4217 code for the extra-usage amounts; USD when absent.</summary>
    public string? Currency { get; init; }

    /// <summary>Prepaid usage-credit balance ("Current balance" on Claude's
    /// Settings → Usage), from spend.balance. Null when the endpoint doesn't
    /// report it - seen null on a Pro account with usage credits turned off,
    /// even though Settings showed a balance.</summary>
    public decimal? CreditBalance { get; init; }

    /// <summary>What's left to spend on usage credits: the prepaid balance
    /// when reported, otherwise the unused part of the monthly credit limit.</summary>
    public decimal? CreditsRemaining => CreditBalance ?? ExtraUsageRemaining;

    public decimal? ExtraUsageRemaining =>
        ExtraUsageEnabled == true && ExtraUsageLimit is { } limit && ExtraUsageUsed is { } used
            ? Math.Max(0, limit - used)
            : null;

    /// <summary>The plan named in the stored credentials ("pro", "max",
    /// "enterprise"...). Known before the first fetch, so the widget can pick
    /// its layout on launch; the endpoint's response shape decides after that.</summary>
    public string? SubscriptionType { get; init; }

    public string? AccountEmail { get; init; }

    /// <summary>True when the last fetch attempt failed with HTTP 401 and the
    /// token couldn't be refreshed. Opening Claude Desktop or the CLI signs in again.</summary>
    public bool RequiresReauth { get; init; }

    public DateTimeOffset FetchedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>True if this snapshot is a stale cached value being shown because
    /// the most recent fetch attempt failed.</summary>
    public bool IsStale { get; init; }

    /// <summary>The source that produced this reading ("claude-oauth", ...),
    /// set by <see cref="Services.ClaudeUsageProvider"/>. Null when no source
    /// answered at all.</summary>
    public string? SourceId { get; init; }

    public SourceTrust? Trust { get; init; }

    /// <summary>Why there is no reading, in words for the user ("Waiting for
    /// Claude Code: …"), when the source knows; shown by the Unknown layout.</summary>
    public string? StatusDetail { get; init; }

    /// <summary>True when the snapshot carries any number to show: a spend
    /// cap or at least one window. An empty one only says why there is none.</summary>
    [JsonIgnore]
    public bool HasReading =>
        HasMonthlySpend || FiveHourUtilizationPercent is not null || WeeklyUtilizationPercent is not null ||
        WeeklyOpusUtilizationPercent is not null || WeeklySonnetUtilizationPercent is not null || OtherWindows.Count > 0;

    private static readonly TimeSpan FiveHours = TimeSpan.FromHours(5);
    private static readonly TimeSpan SevenDays = TimeSpan.FromDays(7);

    /// <summary>The same reading in the provider-neutral shape: the 5-hour,
    /// weekly and per-model weekly windows that were reported, and the monthly
    /// spend cap (as a spend cap and as a "month_spend" window, so code that
    /// only looks at windows still sees how close the cap is).</summary>
    public ProviderUsage ToProviderUsage(string providerId = "claude", string displayName = "Claude")
    {
        var windows = new List<UsageWindow>();
        void Add(string kind, double? percent, DateTimeOffset? resetsAt, TimeSpan length)
        {
            if (percent is { } p)
                windows.Add(new UsageWindow(kind, p, resetsAt, length));
        }
        Add(UsageWindowKinds.FiveHour, FiveHourUtilizationPercent, FiveHourResetsAt, FiveHours);
        Add(UsageWindowKinds.SevenDay, WeeklyUtilizationPercent, WeeklyResetsAt, SevenDays);
        Add(UsageWindowKinds.SevenDayOpus, WeeklyOpusUtilizationPercent, null, SevenDays);
        Add(UsageWindowKinds.SevenDaySonnet, WeeklySonnetUtilizationPercent, null, SevenDays);
        windows.AddRange(OtherWindows);

        SpendCap? spend = null;
        if (HasMonthlySpend && MonthlySpendPercentOnly)
        {
            // A gateway's spend limit: its period isn't reported, so it is
            // not called a month.
            spend = new SpendCap(null, null, MonthlySpendPercent, MonthlySpendResetsAt, null);
            windows.Add(new UsageWindow(UsageWindowKinds.SpendLimit, MonthlySpendPercent, MonthlySpendResetsAt, null));
        }
        else if (HasMonthlySpend)
        {
            spend = new SpendCap(MonthlySpendUsedUsd, MonthlySpendLimitUsd, MonthlySpendPercent, MonthlySpendResetsAt, "month");
            windows.Add(new UsageWindow(UsageWindowKinds.MonthSpend, MonthlySpendPercent, MonthlySpendResetsAt, null));
        }

        var (health, detail) = RequiresReauth ? (UsageHealth.NeedsSignIn, "Sign in to Claude Code again (claude auth login).")
            : windows.Count == 0 ? (UsageHealth.Unavailable, StatusDetail)
            : IsStale ? (UsageHealth.Stale, null)
            : (UsageHealth.Ok, (string?)null);

        // No source answered: nothing vouches for the (empty) reading, so it
        // carries the lowest trust.
        return new ProviderUsage(providerId, displayName, SubscriptionType, windows, spend, FetchedAt,
            SourceId ?? "none", Trust ?? SourceTrust.Unofficial, health, detail);
    }
}
