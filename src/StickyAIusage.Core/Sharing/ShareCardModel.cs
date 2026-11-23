using System.Globalization;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Stats;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Sharing;

/// <summary>Which card: today against the plan's limits, today against a
/// monthly spend cap, one session ("this refactor cost $3.05"), or the year
/// ("My 2026 with Claude Code", offered in December and January).</summary>
public enum ShareCardVariant { Limits, Spend, Session, Year }

/// <summary>Wide is the link-preview shape (1200×630); portrait suits a phone feed (1080×1350).</summary>
public enum ShareCardShape { Wide, Portrait }

public static class ShareCardShapes
{
    /// <summary>The settings value: "portrait" is portrait, anything else wide.</summary>
    public static ShareCardShape Parse(string? value) =>
        string.Equals(value?.Trim(), "portrait", StringComparison.OrdinalIgnoreCase) ? ShareCardShape.Portrait : ShareCardShape.Wide;

    public static string ToSetting(this ShareCardShape shape) => shape == ShareCardShape.Portrait ? "portrait" : "wide";

    /// <summary>The image size in pixels (rendered at 96 dpi, so DIPs = pixels).</summary>
    public static (int Width, int Height) Size(this ShareCardShape shape) =>
        shape == ShareCardShape.Portrait ? (1080, 1350) : (1200, 630);
}

/// <summary>A ring (plan windows) or a bar (a monthly cap): "5-hour", 43%, "resets 4:30 PM".</summary>
public sealed record ShareGauge(string Label, double Percent, string PercentText, UsageLevel Level, string? Detail);

/// <summary>One figure under the gauges: "Sessions today" / "3".</summary>
public sealed record ShareStat(string Label, string Value);

/// <summary>
/// What a share card shows, worked out once in Core so both apps draw the same
/// card from it and nothing private can slip in through a view: every string
/// the card can show is a property here (<see cref="AllText"/>). Project,
/// folder, branch, repository and session names never appear unless the user
/// turned <see cref="AppSettings.ShareIncludeProjectNames"/> on; the account
/// e-mail and anything beyond the plan's name are never read at all.
/// </summary>
public sealed record ShareCardModel
{
    public required ShareCardVariant Variant { get; init; }
    public required ShareCardShape Shape { get; init; }

    /// <summary>The small line above the title: "My Claude usage".</summary>
    public required string Kicker { get; init; }

    /// <summary>"Claude Pro", "Claude spend", "This session".</summary>
    public required string Title { get; init; }

    /// <summary>"in a project" (or the project's name when opted in) on a session card; null otherwise.</summary>
    public string? Subtitle { get; init; }

    /// <summary>"Wed, Nov 18, 2026".</summary>
    public required string DateText { get; init; }

    /// <summary>The big number of a spend or session card ("$96.75", "$3.05"); null on a limits card.</summary>
    public string? Headline { get; init; }

    /// <summary>What the big number is: "of $200.00 this month", "estimated at API list prices".</summary>
    public string? HeadlineNote { get; init; }

    /// <summary>Rings on a limits card (5-hour, week), one bar on a spend card, none on a session card.</summary>
    public IReadOnlyList<ShareGauge> Gauges { get; init; } = [];

    /// <summary>"At this pace: 5-hour limit ~4:12 PM", "On pace for 61% of the week"; null without a forecast.</summary>
    public string? PaceLine { get; init; }

    public UsageLevel PaceLevel { get; init; }

    public IReadOnlyList<ShareStat> Stats { get; init; } = [];

    /// <summary>"Costs are estimates at API list prices." when the card shows an estimate.</summary>
    public string? EstimateNote { get; init; }

    /// <summary>The year a year card covers; null on the other cards.</summary>
    public int? Year { get; init; }

    public string FooterName { get; init; } = ShareCards.ProductName;
    public string FooterUrl { get; init; } = ShareCards.ShortRepositoryUrl;

    /// <summary>Every piece of text the card shows (the privacy tests read this).</summary>
    public IEnumerable<string> AllText()
    {
        IEnumerable<string?> parts =
        [
            Kicker, Title, Subtitle, DateText, Headline, HeadlineNote, PaceLine, EstimateNote, FooterName, FooterUrl,
            .. Gauges.SelectMany(g => new[] { g.Label, g.PercentText, g.Detail }),
            .. Stats.SelectMany(s => new[] { s.Label, s.Value })
        ];
        return parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!);
    }
}

/// <summary>Builds the three cards from what the widget already has.</summary>
public static class ShareCards
{
    public const string ProductName = "Sticky AI Usage";
    public const string RepositoryUrl = "https://github.com/mohammad-k-shah/stickyAIusage";
    public const string ShortRepositoryUrl = "github.com/mohammad-k-shah/stickyAIusage";

    /// <summary>What stands in for a project, folder or session name on a default card.</summary>
    public const string HiddenProject = "a project";

    public const string CostEstimateNote = "Costs are estimates at API list prices.";
    public const string DaySpendNote = "Today's spend is approximate.";

    /// <summary>Longest plan or model name before it ends in "…" (the views trim too).</summary>
    internal const int MaxNameLength = 32;

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Today's card for the usage reading: limits for a plan account, spend
    /// for a monthly cap; null when there is no reading yet (Share is then off).</summary>
    /// <param name="today">Today's sessions, as the flyout lists them (exact costs applied).</param>
    public static ShareCardModel? ForUsage(UsageSnapshot usage, IReadOnlyList<PaceForecast>? forecasts,
        IReadOnlyList<SessionInfo> today, decimal? spentToday, AppSettings settings, DateTimeOffset now,
        TimeZoneInfo? zone = null, CultureInfo? culture = null)
    {
        var shape = ShareCardShapes.Parse(settings.ShareCardShape);
        var usable = settings.ShowForecast ? (forecasts ?? []) : [];
        if (usage.HasMonthlySpend)
            return Spend(usage, usable, today, spentToday, settings, shape, now, zone);
        if (usage.FiveHourUtilizationPercent is not null || usage.WeeklyUtilizationPercent is not null)
            return Limits(usage, usable, today, settings, shape, now, zone, culture);
        return null;
    }

    private static ShareCardModel Limits(UsageSnapshot usage, IReadOnlyList<PaceForecast> forecasts,
        IReadOnlyList<SessionInfo> today, AppSettings settings, ShareCardShape shape, DateTimeOffset now,
        TimeZoneInfo? zone, CultureInfo? culture)
    {
        var gauges = new List<ShareGauge>();
        if (usage.FiveHourUtilizationPercent is { } fiveHour)
        {
            var (percent, detail) = usage.FiveHourResetsAt is { } reset && reset > now
                ? (fiveHour, "resets " + Format.ShortTime(reset, zone, culture))
                : (0, "starts with your next message");
            gauges.Add(Gauge("5-hour", percent, detail, settings));
        }
        if (usage.WeeklyUtilizationPercent is { } weekly)
        {
            var (percent, detail) = usage.WeeklyResetsAt switch
            {
                null => (weekly, (string?)null),
                { } reset when reset <= now => (0, "resetting now"),
                { } reset => (weekly, "resets " + Format.DayTime(reset, now, zone, culture))
            };
            gauges.Add(Gauge("Week", percent, detail, settings));
        }

        var (pace, paceLevel) = LimitsPace(usage, forecasts, settings, now, zone, culture);
        var (stats, estimated) = TodayStats(today, withCost: true);
        var plan = Format.PlanName(usage.SubscriptionType);
        return new ShareCardModel
        {
            Variant = ShareCardVariant.Limits,
            Shape = shape,
            Kicker = "My Claude usage",
            Title = plan is null ? "Claude plan limits" : Trim("Claude " + plan),
            DateText = DateText(now, zone),
            Gauges = gauges,
            PaceLine = pace,
            PaceLevel = paceLevel,
            Stats = stats,
            EstimateNote = estimated ? CostEstimateNote : null
        };
    }

    /// <summary>The 5-hour window speaks first (reached, a limit time, or where it
    /// ends up), then the week; only forecasts as confident as Settings asks.</summary>
    private static (string? Line, UsageLevel Level) LimitsPace(UsageSnapshot usage, IReadOnlyList<PaceForecast> forecasts,
        AppSettings settings, DateTimeOffset now, TimeZoneInfo? zone, CultureInfo? culture)
    {
        if (forecasts.Count == 0)
            return (null, UsageLevel.Green);
        if (usage.FiveHourUtilizationPercent >= 100 && usage.FiveHourResetsAt is { } fiveReset && fiveReset > now)
            return ("5-hour limit reached · resets " + Format.ShortTime(fiveReset, zone, culture), UsageLevel.Red);
        if (Usable(forecasts, UsageWindowKinds.FiveHour, settings, now) is { } five)
        {
            if (five.HitsLimitAt is { } at)
                return ("At this pace: 5-hour limit ~" + Format.ShortTime(at, zone, culture),
                    Urgency(at - now, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)));
            if (five.ProjectedAtReset is { } projected && !five.LimitReached)
                return ($"On pace for {Format.WholePercent(Math.Min(100, projected))} of the 5-hour window",
                    Thresholds.LevelFor(projected, settings));
        }
        if (Usable(forecasts, UsageWindowKinds.SevenDay, settings, now) is { LimitReached: false } week)
        {
            if (week.HitsLimitAt is { } at)
                return ("At this pace: weekly limit ~" + Format.DayTime(at, now, zone, culture),
                    Urgency(at - now, TimeSpan.FromHours(6), TimeSpan.FromHours(24)));
            if (week.ProjectedAtReset is { } projected)
                return ($"On pace for {Format.WholePercent(Math.Min(100, projected))} of the week", Thresholds.LevelFor(projected, settings));
        }
        return (null, UsageLevel.Green);
    }

    private static ShareCardModel Spend(UsageSnapshot usage, IReadOnlyList<PaceForecast> forecasts,
        IReadOnlyList<SessionInfo> today, decimal? spentToday, AppSettings settings, ShareCardShape shape,
        DateTimeOffset now, TimeZoneInfo? zone)
    {
        var pct = usage.MonthlySpendPercent;
        var percentOnly = usage.MonthlySpendPercentOnly;
        var kind = percentOnly ? UsageWindowKinds.SpendLimit : UsageWindowKinds.MonthSpend;
        string? pace = null;
        var paceLevel = UsageLevel.Green;
        if (Usable(forecasts, kind, settings, now) is { ResetsAt: { } end, ProjectedAtReset: { } projected })
        {
            var by = Format.LastDay(end, zone);
            pace = percentOnly
                ? $"At this pace: {Format.WholePercent(projected)} of your limit by {by}"
                : $"At this pace: {Format.Usd(usage.MonthlySpendLimitUsd * (decimal)projected / 100)} by {by}";
            paceLevel = projected > 100 ? UsageLevel.Orange : UsageLevel.Green;
        }

        var stats = new List<ShareStat>();
        var approximateDay = false;
        // A percent-only limit has no dollars, so no day figure either.
        if (!percentOnly && spentToday is > 0m and { } day)
        {
            stats.Add(new ShareStat("Spent today", Format.Usd(day)));
            approximateDay = true;
        }
        var (todayStats, estimated) = TodayStats(today, withCost: false);
        stats.AddRange(todayStats);

        var plan = Format.PlanName(usage.SubscriptionType);
        var notes = string.Join(" ", new[] { estimated ? CostEstimateNote : null, approximateDay ? DaySpendNote : null }
            .Where(n => n is not null));
        return new ShareCardModel
        {
            Variant = ShareCardVariant.Spend,
            Shape = shape,
            Kicker = "My Claude usage",
            Title = plan is null ? "Claude spend" : Trim("Claude " + plan),
            DateText = DateText(now, zone),
            Headline = percentOnly ? Format.WholePercent(pct) : Format.Usd(usage.MonthlySpendUsedUsd),
            HeadlineNote = percentOnly ? "of your spend limit" : $"of {Format.Usd(usage.MonthlySpendLimitUsd)} this month",
            Gauges = [Gauge(percentOnly ? "Spend limit" : "This month", pct, null, settings)],
            PaceLine = pace,
            PaceLevel = paceLevel,
            Stats = stats,
            EstimateNote = notes.Length == 0 ? null : notes
        };
    }

    /// <summary>"Sessions today", the cost of them (left out when zero) and the
    /// top model; whether that cost is (partly) an estimate.</summary>
    private static (List<ShareStat> Stats, bool Estimated) TodayStats(IReadOnlyList<SessionInfo> today, bool withCost)
    {
        var stats = new List<ShareStat> { new("Sessions today", today.Count.ToString(Inv)) };
        var cost = today.Sum(s => s.CostUsd);
        var estimated = false;
        if (withCost && cost > 0)
        {
            var allExact = today.All(s => s.CostIsExact);
            var rough = today.Any(s => !s.CostIsExact && s.CostIsRough);
            stats.Add(new ShareStat(allExact ? "Cost today" : "Est. cost today", (rough ? "≈" : "") + Format.Usd(cost)));
            estimated = !allExact;
        }
        if (TopModel(today) is { } model)
            stats.Add(new ShareStat("Top model", model));
        return (stats, estimated);
    }

    /// <summary>The model behind the most cost today (tokens break a tie); null without one.</summary>
    internal static string? TopModel(IReadOnlyList<SessionInfo> sessions) =>
        sessions.Where(s => !string.IsNullOrWhiteSpace(s.Model))
            .GroupBy(s => Format.ModelName(s.Model))
            .OrderByDescending(g => g.Sum(s => s.CostUsd))
            .ThenByDescending(g => g.Sum(s => s.TotalTokens))
            .ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => Trim(g.Key))
            .FirstOrDefault();

    /// <summary>One session's card: its cost, how long it ran, the model, tokens, cache
    /// hits and context. Its project is "a project" unless the user opted in.</summary>
    public static ShareCardModel ForSession(SessionInfo session, AppSettings settings, DateTimeOffset now,
        TimeZoneInfo? zone = null)
    {
        var stats = new List<ShareStat> { new("Duration", Format.Duration(session.Elapsed)) };
        if (!string.IsNullOrWhiteSpace(session.Model))
            stats.Add(new ShareStat("Model", Trim(Format.ModelName(session.Model))));
        var prompt = session.InputTokens + session.CacheReadTokens + session.CacheCreationTokens;
        if (prompt + session.OutputTokens > 0)
            stats.Add(new ShareStat("Tokens", $"{Format.Compact(prompt)} in · {Format.Compact(session.OutputTokens)} out"));
        if (prompt > 0)
            stats.Add(new ShareStat("Cache hits", Format.WholePercent(100.0 * session.CacheReadTokens / prompt)));
        if (session.ContextWindowSize is > 0 and { } window)
            stats.Add(new ShareStat("Context at the end",
                $"{Format.WholePercent(Math.Min(100, 100.0 * session.CurrentContextTokens / window))} of {Format.Compact(window)}"));

        var hasCost = session.CostUsd > 0;
        return new ShareCardModel
        {
            Variant = ShareCardVariant.Session,
            Shape = ShareCardShapes.Parse(settings.ShareCardShape),
            Kicker = "Claude Code session",
            Title = "This session",
            Subtitle = "in " + (settings.ShareIncludeProjectNames ? ProjectLabel(session) : HiddenProject),
            DateText = DateText(session.LastActivityAt == default ? now : session.LastActivityAt, zone),
            Headline = hasCost ? Format.SessionCost(session) : null,
            HeadlineNote = !hasCost ? null : session.CostIsExact ? "cost, as Claude Code counts it" : "estimated at API list prices",
            Stats = stats,
            EstimateNote = hasCost && !session.CostIsExact ? CostEstimateNote : null
        };
    }

    /// <summary>The year card: total tokens, the estimated cost at list prices, sessions,
    /// active days, the favorite model and the busiest day. It never names a project,
    /// whatever <see cref="AppSettings.ShareIncludeProjectNames"/> says. Null when the
    /// year has no Claude Code work to show.</summary>
    public static ShareCardModel? ForYear(YearSummary year, AppSettings settings)
    {
        if (year.TotalTokens <= 0)
            return null;
        var stats = new List<ShareStat>();
        var hasCost = year.EstimatedCostUsd > 0;
        if (hasCost)
            stats.Add(new ShareStat("Est. cost", (year.CostIsRough ? "≈" : "") + YearUsd(year.EstimatedCostUsd)));
        stats.Add(new ShareStat("Sessions", year.Sessions.ToString("N0", Inv)));
        stats.Add(new ShareStat("Active days", year.ActiveDays.ToString("N0", Inv)));
        if (year.FavoriteModel is { } model)
            stats.Add(new ShareStat("Favorite model", Trim(model)));
        if (year.BusiestDay is { } busiest)
            stats.Add(new ShareStat("Busiest day", busiest.ToString("ddd, MMM d", Inv)));
        return new ShareCardModel
        {
            Variant = ShareCardVariant.Year,
            Shape = ShareCardShapes.Parse(settings.ShareCardShape),
            Year = year.Year,
            Kicker = "Year in review",
            Title = YearCard.Title(year.Year),
            DateText = year.From.ToString("MMM d", Inv) + " – " + year.To.ToString("MMM d, yyyy", Inv),
            Headline = Format.Compact(year.TotalTokens),
            HeadlineNote = "tokens, cache included",
            Stats = stats,
            EstimateNote = hasCost ? CostEstimateNote : null
        };
    }

    /// <summary>"$3,412.50" below $10,000, "$12,480" from there (a year's sum).</summary>
    internal static string YearUsd(decimal amount) =>
        amount < 10_000 ? "$" + amount.ToString("#,##0.00", Inv) : "$" + Math.Round(amount, MidpointRounding.AwayFromZero).ToString("#,##0", Inv);

    /// <summary>"Refactor payment retries · acme-webapp" (opted in only).</summary>
    private static string ProjectLabel(SessionInfo session)
    {
        var folder = session.WorkingDirectory.TrimEnd('/', '\\').Split('/', '\\').Last();
        var project = string.IsNullOrEmpty(folder) ? HiddenProject : Format.ProjectName(folder);
        var label = string.IsNullOrWhiteSpace(session.SessionName) ? project : $"{session.SessionName.Trim()} · {project}";
        return label.Length > 60 ? label[..59] + "…" : label;
    }

    /// <summary>The text an X or Bluesky post starts with; static, so the link carries
    /// nothing about the user: the image is what they paste.</summary>
    public static string PostText(ShareCardVariant variant, int? year = null) =>
        variant switch
        {
            ShareCardVariant.Session => "This Claude Code session",
            ShareCardVariant.Year when year is { } y => YearCard.Title(y),
            _ => "My Claude usage today"
        } + $" — made with {ProductName} {RepositoryUrl}";

    /// <summary>The share page of X, LinkedIn or Bluesky with that text (LinkedIn's
    /// share page takes only a link).</summary>
    public static string PostUrl(ShareTarget target, ShareCardVariant variant, int? year = null)
    {
        var text = Uri.EscapeDataString(PostText(variant, year));
        return target switch
        {
            ShareTarget.X => "https://x.com/intent/tweet?text=" + text,
            ShareTarget.Bluesky => "https://bsky.app/intent/compose?text=" + text,
            _ => "https://www.linkedin.com/sharing/share-offsite/?url=" + Uri.EscapeDataString(RepositoryUrl)
        };
    }

    /// <summary>"usage-20261118-2215.png", the local time the card was made; a year card
    /// "claude-code-2026-20261215-1030.png".</summary>
    public static string FileName(DateTimeOffset now, TimeZoneInfo? zone = null, int? year = null) =>
        (year is { } y ? $"claude-code-{y.ToString(Inv)}-" : "usage-") +
        TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local).ToString("yyyyMMdd-HHmm", Inv) + ".png";

    /// <summary>"Wed, Nov 18, 2026" in the local zone.</summary>
    internal static string DateText(DateTimeOffset at, TimeZoneInfo? zone) =>
        TimeZoneInfo.ConvertTime(at, zone ?? TimeZoneInfo.Local).ToString("ddd, MMM d, yyyy", Inv);

    private static ShareGauge Gauge(string label, double percent, string? detail, AppSettings settings) =>
        new(label, Math.Clamp(percent, 0, 100), Format.WholePercent(percent), Thresholds.LevelFor(percent, settings), detail);

    private static PaceForecast? Usable(IReadOnlyList<PaceForecast> forecasts, string kind, AppSettings settings, DateTimeOffset now) =>
        forecasts.FirstOrDefault(f => f.Kind == kind && f.ResetsAt is { } reset && reset > now
                                      && f.Confidence != ForecastConfidence.None && f.Confidence >= settings.ForecastFloor);

    private static UsageLevel Urgency(TimeSpan left, TimeSpan red, TimeSpan orange) =>
        left <= red ? UsageLevel.Red : left <= orange ? UsageLevel.Orange : UsageLevel.Yellow;

    internal static string Trim(string text) => text.Length > MaxNameLength ? text[..(MaxNameLength - 1)] + "…" : text;
}

public enum ShareTarget { X, LinkedIn, Bluesky }

/// <summary>
/// When the year card is offered and what its menu and progress say. It is
/// year-in-review season from 1 December to 31 January (local time): December
/// offers the year so far, January the whole year before.
/// </summary>
public static class YearCard
{
    /// <summary>The year to offer at <paramref name="now"/>, or null outside the season.</summary>
    public static int? SeasonYear(DateTimeOffset now, TimeZoneInfo? zone = null)
    {
        var local = TimeZoneInfo.ConvertTime(now, zone ?? TimeZoneInfo.Local);
        return local.Month switch
        {
            12 => local.Year,
            1 => local.Year - 1,
            _ => null
        };
    }

    /// <summary>"My 2026 with Claude Code": the card's title and its menu entry.</summary>
    public static string Title(int year) => $"My {year.ToString(CultureInfo.InvariantCulture)} with Claude Code";

    /// <summary>The line under the header while the year is read: "Reading 2026's transcripts… 120 of 840".</summary>
    public static string ProgressText(int year, StatsProgress progress) =>
        $"Reading {year.ToString(CultureInfo.InvariantCulture)}'s transcripts…" +
        (progress.Total > 0 ? $" {progress.Done.ToString(CultureInfo.InvariantCulture)} of {progress.Total.ToString(CultureInfo.InvariantCulture)}" : "");

    /// <summary>A made-up year (no one's data) for the self-tests and the screenshot tests.</summary>
    public static YearSummary Sample(int year) =>
        new(year, new DateOnly(year, 1, 1), new DateOnly(year, 12, 15), 2_400_000, 48_200_000, 1_210_000_000, 96_500_000, 3412.57m,
            false, 312, 148, "Opus 5.5", new DateOnly(year, 3, 14), 41_800_000);

    /// <summary>The line when the year has nothing to show.</summary>
    public static string NothingYet(int year) =>
        $"No Claude Code sessions in {year.ToString(CultureInfo.InvariantCulture)} to show yet.";
}

/// <summary>The card's colors, the same in both apps: the app's palette, with
/// the text colors chosen so the footer stays readable at half size.</summary>
public sealed record ShareCardPalette(string Background, string BackgroundEnd, string Track, string TextPrimary,
    string TextSecondary, string TextTertiary, string Accent, string Green, string Yellow, string Orange, string Red)
{
    public static readonly ShareCardPalette Dark = new("#FF1C1C21", "#FF26262E", "#FF3A3A44", "#FFF3F3F6", "#FFB4B4BF",
        "#FF8A8A96", "#FFD97757", "#FF4CC38A", "#FFE8C15A", "#FFF0954A", "#FFEE6B6B");

    public static readonly ShareCardPalette Light = new("#FFFFFFFF", "#FFF1F1F5", "#FFE2E2E9", "#FF17171C", "#FF55555F",
        "#FF6E6E79", "#FFC15F3C", "#FF15875A", "#FF9A6F0F", "#FFB85C18", "#FFC0392B");

    public static ShareCardPalette For(bool dark) => dark ? Dark : Light;

    public string Level(UsageLevel level) => level switch
    {
        UsageLevel.Red => Red,
        UsageLevel.Orange => Orange,
        UsageLevel.Yellow => Yellow,
        _ => Green
    };
}
