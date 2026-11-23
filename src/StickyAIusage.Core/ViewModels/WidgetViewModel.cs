using System.Globalization;
using StickyAIusage.Forecasting;
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

/// <summary>One line of the flyout's usage card: a label, a value, an
/// optional bar, and an optional second line under the bar (the pace:
/// "At this pace: limit at 4:12 PM, 38m before the reset").</summary>
public sealed record SummaryRow(string Label, string Value, double? Percent, UsageLevel Level, string? Detail2 = null)
{
    /// <summary>What a screen reader says for the row (UI Automation names a list item by it).</summary>
    public override string ToString() => Detail2 is null ? $"{Label}: {Value}" : $"{Label}: {Value}. {Detail2}";
}

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

    /// <summary>The note at the top of the flyout (sign in again, install the
    /// plugin, or the one-time plugin tip), or null. Both apps show it the
    /// same way: the message and a button that copies its command.</summary>
    public FlyoutNotice? Notice { get; init; }

    /// <summary>Why the reading isn't current and what to do, at the top of
    /// the flyout's usage card; null when it is current or the notice covers it.</summary>
    public HealthLine? Health { get; init; }

    public required bool ShowUsage { get; init; }

    /// <summary>"$96.75", "5h: 43%", "Sign in to Claude", "Waiting for Claude Code" or "Usage unavailable".</summary>
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

    /// <summary>The weekly text's color when the weekly limit is near (a
    /// forecast within two days, or reached); null: the usual color.</summary>
    public UsageLevel? WeeklyLevel { get; init; }

    /// <summary>The pace chip after the primary text, in its own color
    /// (<see cref="ForecastLevel"/>): "- limit ~4:12 PM", "- limit reached ·
    /// resets 4:30 PM", "- weekly limit ~Thu 3:10 PM", or on a monthly cap
    /// "→ $212". On the plan layout it takes the place of the reset
    /// countdown (<see cref="SecondaryText"/> is then null). Null: no chip.</summary>
    public string? ForecastChipText { get; init; }

    /// <summary>The chip in the compact pill: "→ 4:12", or the countdown in
    /// red once the limit is reached. Null: the compact pill shows
    /// <see cref="SecondaryCompactText"/> as usual.</summary>
    public string? ForecastChipCompactText { get; init; }

    public UsageLevel ForecastLevel { get; init; }

    /// <summary>What the forecast is based on, also part of <see cref="UsageTooltip"/>.</summary>
    public string? ForecastTooltip { get; init; }

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

    /// <summary>The other tools (Codex) under Claude's rows in the usage card; empty without them.</summary>
    public IReadOnlyList<ProviderSection> ProviderSections { get; init; } = [];

    /// <summary>"Codex 91%" after the pill's usage when another tool is closer to
    /// its limit than Claude and past the orange threshold; empty otherwise.</summary>
    public IReadOnlyList<ProviderChip> ExtraChips { get; init; } = [];

    /// <param name="forecasts">The pace of each window (<see cref="UsageFeed"/>);
    /// shown when Settings → Show the pace forecast is on and a forecast is at
    /// least as confident as <see cref="AppSettings.ForecastMinConfidence"/>.</param>
    /// <param name="culture">For times of day: the OS format by default.</param>
    /// <param name="others">The other tools' readings (Codex): a section each in the
    /// flyout, and a chip on the pill when one is closer to its limit.</param>
    public static WidgetViewModel From(UsageSnapshot usage, SessionInfo? session, AppSettings settings,
        DateTimeOffset now, TimeZoneInfo? zone = null, decimal? spentToday = null,
        IReadOnlyList<PaceForecast>? forecasts = null, CultureInfo? culture = null,
        IReadOnlyList<ProviderReading>? others = null)
    {
        others ??= [];
        var pace = new PaceText(settings.ShowForecast ? forecasts ?? [] : [], settings.ForecastFloor, now, zone, culture);
        string? chip = null, chipCompact = null;
        var chipLevel = UsageLevel.Green;
        UsageLevel? weeklyLevel = null;
        var mode = usage.HasMonthlySpend ? UsageMode.MonthlySpend
            : usage.FiveHourUtilizationPercent is not null || usage.WeeklyUtilizationPercent is not null ? UsageMode.PlanLimits
            : UsageMode.Unknown;
        var plan = Format.PlanName(usage.SubscriptionType);
        var age = Format.Ago(now - usage.FetchedAt);
        // The plugin's feed only moves when Claude Code answers: an older
        // reading is not failing, nothing is being retried - it is just as of then.
        var asOf = IsFeedReadingAsOf(usage, now);
        var updated = (asOf ? "As of " : "Updated ") + age;
        var freshness = usage.RequiresReauth
            ? $"Last updated {age}. Your Claude sign-in has expired - run claude auth login (or /login inside Claude Code); the widget resumes by itself."
            : asOf ? $"As of {age} - updates while Claude Code is active"
            : usage.IsStale ? $"Last updated {age} - retrying automatically" : $"Updated {age}";

        string primary;
        string? secondary = null, secondaryCompact = null, stale = null, weekly = null, credits = null, updatedText = null;
        double ring = 0;
        var state = UsageState.Normal;
        string tooltip;
        var rows = new List<SummaryRow>();

        switch (mode)
        {
            case UsageMode.MonthlySpend when usage.MonthlySpendPercentOnly:
            {
                // A spend limit known only as a percentage (Claude Code's feed
                // behind a gateway): no dollars to show, and no day target.
                var pct = usage.MonthlySpendPercent;
                primary = Format.WholePercent(pct);
                secondary = secondaryCompact = "of limit";
                stale = usage.RequiresReauth ? "· sign in · " + age : usage.IsStale ? "· " + age : asOf ? "· as of " + age : null;
                ring = pct;
                var resets = usage.MonthlySpendResetsAt is { } reset && reset > now ? " · " + Format.ResetsAt(reset, now, zone, culture) : "";
                tooltip = $"Spend limit: {Format.Percent(pct)} used{resets}\n{freshness}";
                rows.Add(new SummaryRow("Spend limit", $"{Format.WholePercent(pct)} of your limit{resets}",
                    Math.Min(100, pct), Thresholds.LevelFor(pct, settings)));
                if (pace.Projection(UsageWindowKinds.SpendLimit) is { ResetsAt: { } end } spend && spend.ProjectedAtReset is { } projected)
                {
                    rows.Add(new SummaryRow("At this pace", $"{Format.WholePercent(projected)} of your limit by {Format.LastDay(end, zone)}",
                        null, projected > 100 ? UsageLevel.Orange : UsageLevel.Green));
                    if (projected > 100 && pct < 100)
                    {
                        (chip, chipLevel) = ("→ " + Format.WholePercent(projected), UsageLevel.Orange);
                        pace.Say(UsageWindowKinds.SpendLimit, $"At this pace: {Format.WholePercent(projected)} of your limit by {Format.LastDay(end, zone)}.");
                    }
                }
                AddOtherWindows(rows, null, usage, now, zone, settings, pace);
                break;
            }
            case UsageMode.MonthlySpend:
            {
                var pct = usage.MonthlySpendPercent;
                var used = Format.Usd(usage.MonthlySpendUsedUsd);
                var limit = Format.Usd(usage.MonthlySpendLimitUsd);
                primary = used;
                secondary = secondaryCompact = Format.WholePercent(pct);
                stale = usage.RequiresReauth ? "· sign in · " + age : usage.IsStale ? "· " + age : asOf ? "· as of " + age : null;
                ring = pct;
                tooltip = $"This month: {used} of {limit} ({Format.Percent(pct)})\n{freshness}";
                rows.Add(new SummaryRow("This month", $"{used} of {limit} · {Format.WholePercent(pct)}",
                    Math.Min(100, pct), Thresholds.LevelFor(pct, settings), pace.EvenPace(UsageWindowKinds.MonthSpend)));
                // "On pace for $212 by Nov 30 (cap $200)"; the pill says "→ $212"
                // only when that passes the cap.
                if (pace.Projection(UsageWindowKinds.MonthSpend) is { ResetsAt: { } end } month && month.ProjectedAtReset is { } projected)
                {
                    var amount = Format.Usd(usage.MonthlySpendLimitUsd * (decimal)projected / 100);
                    var by = Format.LastDay(end, zone);
                    rows.Add(new SummaryRow("At this pace", $"{amount} by {by} (cap {limit})", null,
                        projected > 100 ? UsageLevel.Orange : UsageLevel.Green));
                    if (projected > 100 && pct < 100)
                    {
                        (chip, chipLevel) = ("→ " + amount, UsageLevel.Orange);
                        pace.Say(UsageWindowKinds.MonthSpend, $"At this pace: {amount} by {by}, over the {limit} cap.");
                    }
                }
                if (settings.ShowDayTarget && spentToday is { } today)
                {
                    var day = DayPercent(today, settings.DayTargetUsd);
                    rows.Add(new SummaryRow("Today", $"{Format.Usd(today)} of {Format.Usd(settings.DayTargetUsd)} · " +
                        Format.WholePercent(day), Math.Min(100, day), Thresholds.LevelFor(day, settings)));
                }
                AddOtherWindows(rows, null, usage, now, zone, settings, pace);
                break;
            }
            case UsageMode.PlanLimits:
            {
                var fiveHour = FiveHourWindow(usage, now);
                var weeklyWindow = WeeklyWindow(usage, now, zone, culture);
                var lines = new List<string> { plan is null ? "Plan usage limits" : $"Plan usage limits: {plan}" };

                if (fiveHour is { } f)
                {
                    primary = "5h: " + Format.WholePercent(f.Percent);
                    secondary = "- " + f.PillDetail;
                    secondaryCompact = "- " + f.CompactDetail;
                    rows.Add(new SummaryRow("5-hour window", $"{Format.WholePercent(f.Percent)} · {f.Detail}",
                        Math.Min(100, f.Percent), Thresholds.LevelFor(f.Percent, settings),
                        pace.PaceLine(UsageWindowKinds.FiveHour, withDay: false)));
                    lines.Add($"5-hour window: {Format.WholePercent(f.Percent)} · {f.Detail}");

                    // The chip takes the countdown's place: the limit reached
                    // (red), or when it comes at this pace - red within 30
                    // minutes, orange within an hour, yellow beyond.
                    if (pace.Enabled && usage.FiveHourResetsAt is { } reset && reset > now)
                    {
                        if (f.Percent >= 100)
                        {
                            (chip, chipCompact, chipLevel) = ("- limit reached · resets " + Format.ShortTime(reset, zone, culture),
                                "- " + f.CompactDetail, UsageLevel.Red);
                            (secondary, secondaryCompact) = (null, null);
                        }
                        else if (pace.LimitAt(UsageWindowKinds.FiveHour) is { } at)
                        {
                            (chip, chipCompact) = ("- limit ~" + Format.ShortTime(at, zone, culture),
                                "→ " + Format.ShortTimeNoDesignator(at, zone, culture));
                            chipLevel = Urgency(at - now, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60));
                            (secondary, secondaryCompact) = (null, null);
                            pace.Say(UsageWindowKinds.FiveHour, $"At this pace: 5-hour limit at about {Format.ShortTime(at, zone, culture)}, " +
                                           $"{Format.Duration(reset - at)} before the reset.");
                        }
                    }
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
                        Math.Min(100, w.Percent), Thresholds.LevelFor(w.Percent, settings),
                        PaceText.Join(pace.PaceLine(UsageWindowKinds.SevenDay, withDay: true), pace.EvenPace(UsageWindowKinds.SevenDay))));
                    lines.Add($"Weekly: {Format.WholePercent(w.Percent)} · {w.Detail}");

                    // The weekly limit only gets a say in the pill when it is
                    // reached or forecast within two days; the 5-hour chip wins.
                    if (pace.Enabled && usage.WeeklyResetsAt is { } weekReset && weekReset > now)
                    {
                        if (w.Percent >= 100)
                        {
                            weeklyLevel = UsageLevel.Red;
                            if (chip is null)
                                (chip, chipLevel, secondary) = ("- weekly limit reached", UsageLevel.Red, null);
                        }
                        else if (pace.LimitAt(UsageWindowKinds.SevenDay) is { } at && at - now <= WeeklyChipWithin)
                        {
                            weeklyLevel = Urgency(at - now, TimeSpan.FromHours(6), TimeSpan.FromHours(24));
                            if (chip is null)
                                (chip, chipLevel, secondary) = ("- weekly limit ~" + Format.DayTime(at, now, zone, culture), weeklyLevel.Value, null);
                            pace.Say(UsageWindowKinds.SevenDay, $"At this pace: weekly limit {Format.DayTime(at, now, zone, culture)}, " +
                                           $"{Format.Duration(weekReset - at)} before the reset.");
                        }
                    }
                }

                if (usage.CreditsRemaining is { } remaining)
                {
                    var money = Format.Money(remaining, usage.Currency);
                    credits = "Rem. Credits: " + money;
                    rows.Add(new SummaryRow("Remaining credits", money, null, UsageLevel.Green));
                    lines.Add("Remaining credits: " + money);
                }

                AddOtherWindows(rows, lines, usage, now, zone, settings, pace);

                ring = Math.Max(fiveHour?.Percent ?? 0, weeklyWindow?.Percent ?? 0);
                // Say so, rather than letting "Updated: 2d ago" creep up with no
                // hint that the numbers are frozen until the user signs in again.
                updatedText = usage.RequiresReauth ? "Sign in to Claude" : (asOf ? "As of: " : "Updated: ") + age;
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
                    // Claude Desktop doesn't renew the sign-in Claude Code saved: only the CLI does.
                    tooltip = "Your Claude sign-in has expired.\n" +
                              "Run claude auth login (or /login inside Claude Code) to sign in again; the widget resumes by itself.";
                }
                else
                {
                    state = UsageState.Unavailable;
                    // Nothing failed while the plugin's feed is still empty:
                    // say what the widget waits for (the flyout says how).
                    primary = usage.StatusDetail == ClaudeFeedUsageSource.WaitingDetail ? WaitingText : "Usage unavailable";
                    // The flyout says why, with the one thing to do about it (Health).
                    tooltip = usage.StatusDetail ?? "Couldn't read your usage. Click for the reason.";
                }
                break;
        }

        // The forecast lines go last in the pill's tooltip (Unknown has none).
        var forecastTooltip = pace.Tooltip();
        if (forecastTooltip is not null)
            tooltip += "\n" + forecastTooltip;

        var dayPercent = spentToday is { } spent ? DayPercent(spent, settings.DayTargetUsd) : 0;
        var showDay = mode == UsageMode.MonthlySpend && spentToday is not null && settings.ShowDayTarget &&
                      (settings.ShowDayTargetAmount || settings.ShowDayTargetPercent || settings.ShowDayTargetRing);

        var notice = NoticeFor(usage, mode, settings);
        return new WidgetViewModel
        {
            Mode = mode,
            State = state,
            PlanName = plan,
            Notice = notice,
            Health = HealthLine.For(usage, notice, now),
            ShowUsage = settings.ShowMonthlySpend,
            PrimaryText = primary,
            SecondaryText = secondary,
            SecondaryCompactText = secondaryCompact,
            StaleText = stale,
            WeeklyText = weekly,
            WeeklyLevel = weekly is null ? null : weeklyLevel,
            ForecastChipText = chip,
            ForecastChipCompactText = chipCompact,
            ForecastLevel = chipLevel,
            ForecastTooltip = forecastTooltip,
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
            SessionText = session is null ? "—" : Format.SessionCost(session),
            SessionTooltip = session is null
                ? "No Claude Code session found yet"
                : $"{(session.CostIsExact ? "Cost" : "Estimated cost")} of the most recent session ({Format.ModelName(session.Model)})\n" +
                  (session.CostIsExact ? "Claude Code's own figure\n"
                      : session.CostIsRough ? Format.UnknownModelPriceNote + "\n" : "") +
                  $"Last active {Format.Ago(now - session.LastActivityAt)}",
            // Without a reading the card holds only the health line: no plan to name.
            SummaryTitle = mode == UsageMode.Unknown ? "Usage" : plan is null ? "Plan Usage Limits" : $"Plan Usage Limits: {plan}",
            SummaryUpdatedText = mode == UsageMode.Unknown ? null : updated,
            SummaryRows = rows,
            ProviderSections = OtherProviders.From(others, settings, now, zone, culture),
            // Usage turned off hides the pill's usage: the other tools' chips go with it.
            ExtraChips = settings.ShowMonthlySpend
                ? OtherProviders.Chips(others, mode == UsageMode.Unknown ? 0 : ring, settings, now, zone, culture)
                : []
        };
    }

    /// <summary>The pill while the plugin's feed has no record yet.</summary>
    public const string WaitingText = "Waiting for Claude Code";

    /// <summary>At most one note, the most pressing first: a sign-in to
    /// renew (the numbers are frozen until then), then the plugin to install
    /// (there are no numbers at all), then the one-time tip for plan readings
    /// that come from the usage endpoint (Pro/Max: the plugin carries all of
    /// it; a monthly cap needs the endpoint, so no tip there).</summary>
    internal static FlyoutNotice? NoticeFor(UsageSnapshot usage, UsageMode mode, AppSettings settings)
    {
        if (usage.RequiresReauth)
            return FlyoutNotice.SignIn;
        if (!usage.HasReading && usage.StatusDetail == ClaudeFeedUsageSource.WaitingDetail)
            return FlyoutNotice.InstallPlugin;
        if (mode == UsageMode.PlanLimits && usage.SourceId == ClaudeOAuthUsageSource.Id && !settings.PluginTipDismissed)
            return FlyoutNotice.PluginTip;
        return null;
    }

    /// <summary>True for a reading from Claude Code's feed that is older than
    /// <see cref="ClaudeFeedUsageSource.LiveFor"/>: shown "as of", not "updated".</summary>
    internal static bool IsFeedReadingAsOf(UsageSnapshot usage, DateTimeOffset now) =>
        usage.SourceId == ClaudeFeedUsageSource.Id && usage.HasReading && !usage.RequiresReauth &&
        now - usage.FetchedAt >= ClaudeFeedUsageSource.LiveFor;

    /// <summary>Rows (and tooltip lines) for windows the layouts have no
    /// place of their own for, by a readable name.</summary>
    private static void AddOtherWindows(List<SummaryRow> rows, List<string>? lines, UsageSnapshot usage,
        DateTimeOffset now, TimeZoneInfo? zone, AppSettings settings, PaceText pace)
    {
        foreach (var window in usage.OtherWindows)
        {
            var (percent, detail) = window.ResetsAt switch
            {
                null => (window.PercentUsed, "Reset time unknown"),
                { } reset when reset <= now => (0, "Resetting now"),
                { } reset => (window.PercentUsed, Format.ResetsAt(reset, now, zone, pace.Culture))
            };
            var name = Format.WindowName(window.Kind);
            rows.Add(new SummaryRow(name, $"{Format.WholePercent(percent)} · {detail}",
                Math.Min(100, percent), Thresholds.LevelFor(percent, settings), pace.PaceLine(window.Kind, withDay: true)));
            lines?.Add($"{name}: {Format.WholePercent(percent)} · {detail}");
        }
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
    private static Window? WeeklyWindow(UsageSnapshot usage, DateTimeOffset now, TimeZoneInfo? zone, CultureInfo? culture)
    {
        if (usage.WeeklyUtilizationPercent is not { } pct)
            return null;
        if (usage.WeeklyResetsAt is not { } reset)
            return new Window(pct, "Reset time unknown", "", "");
        if (reset <= now)
            return new Window(0, "Resetting now", "", "");
        return new Window(pct, Format.ResetsAt(reset, now, zone, culture), "", "");
    }

    /// <summary>How far a weekly limit forecast may lie ahead to color the pill.</summary>
    private static readonly TimeSpan WeeklyChipWithin = TimeSpan.FromHours(48);

    /// <summary>Red, orange or yellow by how soon a limit comes.</summary>
    private static UsageLevel Urgency(TimeSpan left, TimeSpan red, TimeSpan orange) =>
        left <= red ? UsageLevel.Red : left <= orange ? UsageLevel.Orange : UsageLevel.Yellow;

    /// <summary>The forecasts of one refresh, and the words for them. Only
    /// the forecasts at or above the confidence floor speak; the even-pace
    /// comparison is plain arithmetic on the reading and needs no rate.</summary>
    internal sealed class PaceText(IReadOnlyList<PaceForecast> forecasts, ForecastConfidence floor,
        DateTimeOffset now, TimeZoneInfo? zone, CultureInfo? culture)
    {
        private readonly List<string> _lines = [];
        private readonly List<string> _bases = [];

        public CultureInfo? Culture => culture;

        /// <summary>Settings → Show the pace forecast is on (the list is empty when it's off).</summary>
        public bool Enabled => forecasts.Count > 0;

        private PaceForecast? Find(string kind) =>
            forecasts.FirstOrDefault(f => f.Kind == kind && f.ResetsAt is { } reset && reset > now);

        private PaceForecast? Usable(string kind) =>
            Find(kind) is { Confidence: not ForecastConfidence.None } f && f.Confidence >= floor ? f : null;

        /// <summary>When the limit comes at this pace, if it does before the reset.</summary>
        public DateTimeOffset? LimitAt(string kind) => Usable(kind)?.HitsLimitAt;

        /// <summary>A forecast with a projection to show.</summary>
        public PaceForecast? Projection(string kind) => Usable(kind) is { ProjectedAtReset: not null } f ? f : null;

        /// <summary>"At this pace: limit at 4:12 PM, 38m before the reset",
        /// "On pace to end near 61%", or null.</summary>
        public string? PaceLine(string kind, bool withDay)
        {
            // A used-up window has nothing left to forecast; its row says 100%.
            if (Usable(kind) is not { ResetsAt: { } reset, LimitReached: false } f)
                return null;
            if (f.HitsLimitAt is { } at)
                return "At this pace: limit " + (withDay ? Format.DayTime(at, now, zone, culture) : "at " + Format.ShortTime(at, zone, culture)) +
                       $", {Format.Duration(reset - at)} before the reset";
            return f.ProjectedAtReset switch
            {
                null => null,
                >= 100 => "On pace to use it all before the reset",
                { } p => "On pace to end near " + Format.WholePercent(p)
            };
        }

        /// <summary>"6 points ahead of an even pace", "under an even pace",
        /// "on an even pace": how the usage compares with a steady spread over
        /// the whole window.</summary>
        public string? EvenPace(string kind)
        {
            if (Find(kind)?.AheadOfEvenPace is not { } ahead)
                return null;
            return ahead >= 1.5 ? $"{Math.Round(ahead):0} points ahead of an even pace"
                : ahead <= -1.5 ? "under an even pace"
                : "on an even pace";
        }

        /// <summary>A line for the pill's tooltip, noting what it is based on.</summary>
        public void Say(string kind, string line)
        {
            _lines.Add(line);
            if (Find(kind)?.Basis is { } basis && !_bases.Contains(basis))
                _bases.Add(basis);
        }

        /// <summary>The tooltip lines with the basis: "Based on the last 30
        /// minutes of activity. An estimate." Null when nothing was said.</summary>
        public string? Tooltip() => _lines.Count == 0 ? null
            : string.Join("\n", _lines) + $"\nBased on {string.Join(" and ", _bases)} of activity. An estimate.";

        public static string? Join(params string?[] parts) =>
            parts.Where(p => !string.IsNullOrEmpty(p)).ToList() is { Count: > 0 } kept ? string.Join(" · ", kept) : null;
    }

    /// <summary>"3 sessions · $3.59 estimated" for the flyout header.</summary>
    public static string SessionsSummary(IReadOnlyCollection<SessionCardViewModel> cards, int days = 1) =>
        cards.Count == 0
            ? SessionRange.EmptySummary(days)
            : $"{cards.Count} session{(cards.Count == 1 ? "" : "s")} · {Format.Usd(cards.Sum(c => c.CostUsd))}" +
              (cards.All(c => c.CostIsExact) ? "" : " estimated");
}
