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
/// shape is parsed and mapped in <see cref="Services.ClaudeUsageProvider"/>
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
}
