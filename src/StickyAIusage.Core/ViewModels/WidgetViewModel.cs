using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.ViewModels;

public enum UsageState
{
    /// <summary>A reading is shown (possibly stale).</summary>
    Normal,

    /// <summary>The sign-in expired and couldn't be refreshed.</summary>
    SignIn,

    /// <summary>No reading at all yet.</summary>
    Unavailable
}

/// <summary>One line of the flyout's usage card: a label, a value, and an
/// optional bar.</summary>
public sealed record SummaryRow(string Label, string Value, double? Percent, UsageLevel Level);

/// <summary>
/// Everything the collapsed pill and the flyout's usage card display, worked
/// out once from the usage reading, the most recent session and the settings,
/// so the Windows and macOS apps show exactly the same thing and only map it
/// to their own controls. Two layouts, chosen by <see cref="UsageMode"/>:
/// - Monthly spend cap (Enterprise): "$96.75  48% │ Today: $4.20 42% │
///   Session: $3.05", ring = % of the cap; the day target has its own ring
///   (% of the daily target set in Settings).
/// - Plan limits (Pro, Max): "5h: 43% - Resets: 2h 6m │ 1w: 14% │ Rem. Credits:
///   $9.32 │ Updated: just now", ring = the fuller window. In the compact
///   form (docked in the Windows taskbar or the macOS menu bar):
///   "5h: 43% - 2h 6m │ 1w: 14%". No session cost here
///   (it isn't what a plan user watches); it stays on the session cards.
/// Reset countdowns are computed from the reading's reset timestamps and the
/// current time, so they tick down locally between fetches.
/// </summary>
public sealed class WidgetViewModel
{
    public required UsageMode Mode { get; init; }
    public required UsageState State { get; init; }
    public string? PlanName { get; init; }

    /// <summary>The login expired: the flyout offers a Sign in button.</summary>
    public bool NeedsSignIn { get; init; }

    public required bool ShowUsage { get; init; }

    /// <summary>"$96.75", "5h: 43%", "Sign in to Claude" or "Usage unavailable".</summary>
    public required string PrimaryText { get; init; }

    /// <summary>"48%" or "- Resets: 2h 6m"; null when there's nothing to add.</summary>
    public string? SecondaryText { get; init; }

    /// <summary>Shorter secondary text for the compact pill (Windows taskbar,
    /// macOS menu bar): "48%" or "- 2h 6m".</summary>
    public string? SecondaryCompactText { get; init; }

    /// <summary>"· 12m ago" when a monthly reading is stale, otherwise null.</summary>
    public string? StaleText { get; init; }

    /// <summary>"1w: 14%" for plan accounts.</summary>
    public string? WeeklyText { get; init; }

    /// <summary>"Rem. Credits: $9.32" for plan accounts that report credits.</summary>
    public string? CreditsText { get; init; }

    /// <summary>"Updated: just now" / "Updated: 12m ago" for plan accounts.</summary>
    public string? UpdatedText { get; init; }

    public required bool ShowRing { get; init; }
    public required double RingPercent { get; init; }
    public required UsageLevel Level { get; init; }
    public required string UsageTooltip { get; init; }

    /// <summary>The day target group ("Today: $4.20 42%" and its ring):
    /// monthly-cap accounts only, when it's on in Settings and at least one
    /// of its parts is.</summary>
    public bool ShowDayTarget { get; init; }

    /// <summary>"$4.20", or null when Settings hides the amount.</summary>
    public string? DayAmountText { get; init; }

    /// <summary>"42%" of the daily target, or null when Settings hides it.</summary>
    public string? DayPercentText { get; init; }

    public bool ShowDayRing { get; init; }
    public double DayRingPercent { get; init; }
    public UsageLevel DayLevel { get; init; }
    public string DayTooltip { get; init; } = "";

    public required bool ShowSession { get; init; }
    public required string SessionText { get; init; }
    public required string SessionTooltip { get; init; }

    /// <summary>"Plan Usage Limits: Pro" - the flyout usage card's heading.</summary>
    public required string SummaryTitle { get; init; }

    /// <summary>"Updated just now", right-aligned in the usage card's heading.</summary>
    public string? SummaryUpdatedText { get; init; }

    /// <summary>Rows for the flyout's usage card; empty when there's no reading.</summary>
    public required IReadOnlyList<SummaryRow> SummaryRows { get; init; }

    public static WidgetViewModel From(UsageSnapshot usage, SessionInfo? session, AppSettings settings,
        DateTimeOffset now, TimeZoneInfo? zone = null, decimal? spentToday = null)
    {
        var mode = usage.HasMonthlySpend ? UsageMode.MonthlySpend
            : usage.FiveHourUtilizationPercent is not null || usage.WeeklyUtilizationPercent is not null ? UsageMode.PlanLimits
            : UsageMode.Unknown;
        var plan = Format.PlanName(usage.SubscriptionType);
        var age = Format.Ago(now - usage.FetchedAt);
        var updated = "Updated " + age;
        var freshness = usage.RequiresReauth
            ? $"Last updated {age}. Your Claude login has expired - run `claude` and use /login to sign in again; the widget resumes by itself."
            : usage.IsStale ? $"Last updated {age} - retrying automatically" : $"Updated {age}";

        string primary;
        string? secondary = null, secondaryCompact = null, stale = null, weekly = null, credits = null, updatedText = null;
        double ring = 0;
        var state = UsageState.Normal;
        string tooltip;
        var rows = new List<SummaryRow>();

        switch (mode)
        {
            case UsageMode.MonthlySpend:
            {
                var pct = usage.MonthlySpendPercent;
                var used = Format.Usd(usage.MonthlySpendUsedUsd);
                var limit = Format.Usd(usage.MonthlySpendLimitUsd);
                primary = used;
                secondary = secondaryCompact = Format.WholePercent(pct);
                stale = usage.RequiresReauth ? "· sign in · " + age : usage.IsStale ? "· " + age : null;
                ring = pct;
                tooltip = $"This month: {used} of {limit} ({Format.Percent(pct)})\n{freshness}";
                rows.Add(new SummaryRow("This month", $"{used} of {limit} · {Format.WholePercent(pct)}",
                    Math.Min(100, pct), Thresholds.LevelFor(pct, settings)));
                if (settings.ShowDayTarget && spentToday is { } today)
                {
                    var day = DayPercent(today, settings.DayTargetUsd);
                    rows.Add(new SummaryRow("Today", $"{Format.Usd(today)} of {Format.Usd(settings.DayTargetUsd)} · " +
                        Format.WholePercent(day), Math.Min(100, day), Thresholds.LevelFor(day, settings)));
                }
                break;
            }
            case UsageMode.PlanLimits:
            {
                var fiveHour = FiveHourWindow(usage, now);
                var weeklyWindow = WeeklyWindow(usage, now, zone);
                var lines = new List<string> { plan is null ? "Plan usage limits" : $"Plan usage limits: {plan}" };

                if (fiveHour is { } f)
                {
                    primary = "5h: " + Format.WholePercent(f.Percent);
                    secondary = "- " + f.PillDetail;
                    secondaryCompact = "- " + f.CompactDetail;
                    rows.Add(new SummaryRow("5-hour window", $"{Format.WholePercent(f.Percent)} · {f.Detail}",
                        Math.Min(100, f.Percent), Thresholds.LevelFor(f.Percent, settings)));
                    lines.Add($"5-hour window: {Format.WholePercent(f.Percent)} · {f.Detail}");
                }
                else
                {
                    primary = "1w: " + Format.WholePercent(weeklyWindow!.Value.Percent);
                }

                if (weeklyWindow is { } w)
                {
                    if (fiveHour is not null)
                        weekly = "1w: " + Format.WholePercent(w.Percent);
                    rows.Add(new SummaryRow("Weekly", $"{Format.WholePercent(w.Percent)} · {w.Detail}",
                        Math.Min(100, w.Percent), Thresholds.LevelFor(w.Percent, settings)));
                    lines.Add($"Weekly: {Format.WholePercent(w.Percent)} · {w.Detail}");
                }

                if (usage.CreditsRemaining is { } remaining)
                {
                    var money = Format.Money(remaining, usage.Currency);
                    credits = "Rem. Credits: " + money;
                    rows.Add(new SummaryRow("Remaining credits", money, null, UsageLevel.Green));
                    lines.Add("Remaining credits: " + money);
                }

                ring = Math.Max(fiveHour?.Percent ?? 0, weeklyWindow?.Percent ?? 0);
                // Say so, rather than letting "Updated: 2d ago" creep up with no
                // hint that the numbers are frozen until the user signs in again.
                updatedText = usage.RequiresReauth ? "Sign in to Claude" : "Updated: " + age;
                lines.Add(freshness);
                tooltip = string.Join("\n", lines);
                break;
            }
            default:
                if (usage.RequiresReauth)
                {
                    // Distinct from a generic failure because the fix is different.
                    state = UsageState.SignIn;
                    primary = "Sign in to Claude";
                    tooltip = "Your Claude login has expired and couldn't be refreshed automatically.\n" +
                              "Open Claude Desktop or run `claude` once to sign in again.";
                }
                else
                {
                    state = UsageState.Unavailable;
                    primary = "Usage unavailable";
                    tooltip = "Couldn't read your usage from Anthropic.\n" +
                              "Open the log folder from the menu; diagnostics.log has the reason.";
                }
                break;
        }

        var dayPercent = spentToday is { } spent ? DayPercent(spent, settings.DayTargetUsd) : 0;
        var showDay = mode == UsageMode.MonthlySpend && spentToday is not null && settings.ShowDayTarget &&
                      (settings.ShowDayTargetAmount || settings.ShowDayTargetPercent || settings.ShowDayTargetRing);

        return new WidgetViewModel
        {
            Mode = mode,
            State = state,
            PlanName = plan,
            NeedsSignIn = usage.RequiresReauth,
            ShowUsage = settings.ShowMonthlySpend,
            PrimaryText = primary,
            SecondaryText = secondary,
            SecondaryCompactText = secondaryCompact,
            StaleText = stale,
            WeeklyText = weekly,
            CreditsText = credits,
            UpdatedText = updatedText,
            ShowRing = mode != UsageMode.Unknown,
            RingPercent = Math.Min(100, ring),
            Level = Thresholds.LevelFor(ring, settings),
            UsageTooltip = tooltip,
            ShowDayTarget = showDay,
            DayAmountText = showDay && settings.ShowDayTargetAmount ? Format.Usd(spentToday!.Value) : null,
            DayPercentText = showDay && settings.ShowDayTargetPercent ? Format.WholePercent(dayPercent) : null,
            ShowDayRing = showDay && settings.ShowDayTargetRing,
            DayRingPercent = Math.Min(100, dayPercent),
            DayLevel = Thresholds.LevelFor(dayPercent, settings),
            DayTooltip = spentToday is { } s2
                ? $"Today: {Format.Usd(s2)} of your {Format.Usd(settings.DayTargetUsd)} day target ({Format.WholePercent(dayPercent)})\n" +
                  "Approximate: from your monthly spend since the day began (all Claude apps)"
                : "",
            // Plan users watch their windows, not per-session dollars; the
            // cost stays on the session cards in the flyout.
            ShowSession = settings.ShowSessionCost && mode != UsageMode.PlanLimits,
            SessionText = session is null ? "—" : Format.Usd(session.EstimatedCostUsd),
            SessionTooltip = session is null
                ? "No Claude Code session found yet"
                : $"Estimated cost of the most recent session ({Format.ModelName(session.Model)})\n" +
                  $"Last active {Format.Ago(now - session.LastActivityAt)}",
            SummaryTitle = plan is null ? "Plan Usage Limits" : $"Plan Usage Limits: {plan}",
            SummaryUpdatedText = mode == UsageMode.Unknown ? null : updated,
            SummaryRows = rows
        };
    }

    /// <summary>Today's spend as a % of the daily target (can pass 100).</summary>
    private static double DayPercent(decimal spent, decimal target) =>
        target <= 0 ? 0 : (double)(spent / target * 100);

    /// <param name="Detail">For the flyout: "Resets in 2h 6m".</param>
    /// <param name="PillDetail">For the pill: "Resets: 2h 6m".</param>
    /// <param name="CompactDetail">For the compact pill: "2h 6m".</param>
    private readonly record struct Window(double Percent, string Detail, string PillDetail, string CompactDetail);

    /// <summary>
    /// The 5-hour window. A new window only starts with the next message after
    /// the previous one ends, so once its reset time has passed (or the
    /// endpoint reports no reset time, as it does before a window starts) it
    /// shows 0% and "Starts with your next message" - without waiting for the
    /// next fetch.
    /// </summary>
    private const string NotStarted = "Starts with your next message";

    private static Window? FiveHourWindow(UsageSnapshot usage, DateTimeOffset now)
    {
        if (usage.FiveHourUtilizationPercent is not { } pct)
            return null;
        if (usage.FiveHourResetsAt is not { } reset)
            return new Window(pct, NotStarted, NotStarted, "not started");
        if (reset <= now)
            return new Window(0, NotStarted, NotStarted, "not started");
        return new Window(pct, Format.ResetsIn(reset - now), "Resets: " + Format.Duration(reset - now),
            Format.Duration(reset - now));
    }

    /// <summary>The weekly window, with its reset shown as a day and time
    /// ("Resets Fri 3:30 PM"), like Claude's Settings → Usage.</summary>
    private static Window? WeeklyWindow(UsageSnapshot usage, DateTimeOffset now, TimeZoneInfo? zone)
    {
        if (usage.WeeklyUtilizationPercent is not { } pct)
            return null;
        if (usage.WeeklyResetsAt is not { } reset)
            return new Window(pct, "Reset time unknown", "", "");
        if (reset <= now)
            return new Window(0, "Resetting now", "", "");
        return new Window(pct, Format.ResetsAt(reset, now, zone), "", "");
    }

    /// <summary>"3 sessions · $3.59 estimated" for the flyout header.</summary>
    public static string SessionsSummary(IReadOnlyCollection<SessionCardViewModel> cards, int days = 1) =>
        cards.Count == 0
            ? SessionRange.EmptySummary(days)
            : $"{cards.Count} session{(cards.Count == 1 ? "" : "s")} · {Format.Usd(cards.Sum(c => c.CostUsd))} estimated";
}
