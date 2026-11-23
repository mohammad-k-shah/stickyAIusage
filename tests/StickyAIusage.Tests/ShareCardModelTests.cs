using System.Globalization;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

public class ShareCardModelTests
{
    // Wednesday 18 November 2026, 14:00 UTC; the cards are read in UTC with en-US times.
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");

    private const string SecretProject = "zebracorn-payments";
    private const string SecretSession = "Rotate the quokka keys";
    private const string SecretBranch = "feature/narwhal-merge";
    private const string SecretRepo = "okapi-org";
    private const string SecretEmail = "jane.doe@pangolin.example";

    private static ShareCardModel? Usage(UsageSnapshot usage, IReadOnlyList<PaceForecast>? forecasts = null,
        IReadOnlyList<SessionInfo>? today = null, decimal? spentToday = null, AppSettings? settings = null) =>
        ShareCards.ForUsage(usage, forecasts, today ?? [], spentToday, settings ?? new AppSettings(), Now, Utc, EnUs);

    private static UsageSnapshot Plan(double fiveHour = 43, double weekly = 14) => new()
    {
        Mode = UsageMode.PlanLimits,
        SubscriptionType = "pro",
        AccountEmail = SecretEmail,
        FiveHourUtilizationPercent = fiveHour,
        FiveHourResetsAt = Now.AddMinutes(150), // 4:30 PM
        WeeklyUtilizationPercent = weekly,
        WeeklyResetsAt = Now.AddDays(2).AddHours(1.5), // Fri 3:30 PM
        FetchedAt = Now
    };

    private static UsageSnapshot Monthly(decimal used = 96.75m, double percent = 48.4) => new()
    {
        Mode = UsageMode.MonthlySpend,
        HasMonthlySpend = true,
        SubscriptionType = "enterprise",
        AccountEmail = SecretEmail,
        MonthlySpendUsedUsd = used,
        MonthlySpendLimitUsd = 200m,
        MonthlySpendPercent = percent,
        FetchedAt = Now
    };

    private static SessionInfo Session(decimal cost = 3.05m, string model = "claude-opus-5-5", bool exact = false,
        bool rough = false, long input = 50, long output = 85_000, long cacheRead = 1_100_000, long cacheWrite = 50_000) =>
        new()
        {
            SessionId = "3c8e62c4-17a7-453e-8385-88147c5aaf07",
            SessionName = SecretSession,
            TranscriptPath = $@"C:\Users\me\.claude\projects\C--dev-{SecretProject}\3c8e62c4.jsonl",
            WorkingDirectory = $@"C:\dev\{SecretProject}",
            Model = model,
            StartedAt = Now.AddMinutes(-72),
            LastActivityAt = Now,
            InputTokens = input,
            OutputTokens = output,
            CacheReadTokens = cacheRead,
            CacheCreationTokens = cacheWrite,
            ContextWindowSize = 1_000_000,
            CurrentContextTokens = 640_000,
            EstimatedCostUsd = cost,
            ExactCostUsd = exact ? cost : null,
            CostIsRough = rough,
            Git = new GitInfo { Branch = SecretBranch, OriginOwner = SecretRepo, OriginRepo = SecretProject, RootDirectoryName = SecretProject }
        };

    private static PaceForecast FiveHourForecast(DateTimeOffset? limitAt, double? projected = 150,
        ForecastConfidence confidence = ForecastConfidence.High) =>
        new(UsageWindowKinds.FiveHour, 43, 20, limitAt, projected, 10, confidence, "the last 30 minutes")
            { ResetsAt = Now.AddMinutes(150), IsActive = true };

    private static void AssertNothingPrivate(ShareCardModel card)
    {
        foreach (var text in card.AllText())
            foreach (var secret in new[] { SecretProject, "zebracorn", SecretSession, "quokka", SecretBranch, "narwhal", SecretRepo, "okapi", SecretEmail, "pangolin", "3c8e62c4" })
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_plan_card_has_two_rings_the_plan_name_and_today()
    {
        var card = Usage(Plan(), today: [Session(), Session(cost: 0.54m, model: "claude-sonnet-5-5")])!;

        Assert.Equal(ShareCardVariant.Limits, card.Variant);
        Assert.Equal(ShareCardShape.Wide, card.Shape);
        Assert.Equal("My Claude usage", card.Kicker);
        Assert.Equal("Claude Pro", card.Title);
        Assert.Equal("Wed, Nov 18, 2026", card.DateText);
        Assert.Null(card.Headline);
        Assert.Collection(card.Gauges,
            g => Assert.Equal(("5-hour", 43.0, "43%", UsageLevel.Green, "resets 4:30 PM"), (g.Label, g.Percent, g.PercentText, g.Level, g.Detail)),
            g => Assert.Equal(("Week", "14%", "resets Fri 3:30 PM"), (g.Label, g.PercentText, g.Detail)));
        Assert.Equal(
            [new ShareStat("Sessions today", "2"), new ShareStat("Est. cost today", "$3.59"), new ShareStat("Top model", "Opus 5.5")],
            card.Stats);
        Assert.Equal(ShareCards.CostEstimateNote, card.EstimateNote);
        Assert.Equal("Sticky AI Usage", card.FooterName);
        Assert.Equal("github.com/mohammad-k-shah/stickyAIusage", card.FooterUrl);
    }

    [Fact]
    public void Nothing_private_is_on_any_default_card()
    {
        var sessions = new[] { Session() };
        AssertNothingPrivate(Usage(Plan(), [FiveHourForecast(Now.AddMinutes(40))], sessions)!);
        AssertNothingPrivate(Usage(Monthly(), today: sessions, spentToday: 4.20m)!);
        var session = ShareCards.ForSession(Session(), new AppSettings(), Now, Utc);
        AssertNothingPrivate(session);
        Assert.Equal("in a project", session.Subtitle);
    }

    [Fact]
    public void Opted_in_the_session_card_names_the_session_and_the_project_but_never_the_branch_or_account()
    {
        var card = ShareCards.ForSession(Session(), new AppSettings { ShareIncludeProjectNames = true }, Now, Utc);

        Assert.Equal($"in {SecretSession} · {SecretProject}", card.Subtitle);
        Assert.DoesNotContain(card.AllText(), t => t.Contains(SecretBranch) || t.Contains(SecretRepo));
    }

    [Fact]
    public void A_session_card_says_what_the_session_cost_and_how_it_ran()
    {
        var card = ShareCards.ForSession(Session(), new AppSettings(), Now, Utc);

        Assert.Equal(ShareCardVariant.Session, card.Variant);
        Assert.Equal("This session", card.Title);
        Assert.Equal("$3.05", card.Headline);
        Assert.Equal("estimated at API list prices", card.HeadlineNote);
        Assert.Equal(ShareCards.CostEstimateNote, card.EstimateNote);
        Assert.Empty(card.Gauges);
        // Prompt tokens are input + cache reads + cache writes: 50 + 1,100,000 + 50,000.
        Assert.Equal(
        [
            new ShareStat("Duration", "1h 12m"), new ShareStat("Model", "Opus 5.5"), new ShareStat("Tokens", "1.2M in · 85k out"),
            new ShareStat("Cache hits", "96%"), new ShareStat("Context at the end", "64% of 1M")
        ], card.Stats);
    }

    [Fact]
    public void Claude_Code_s_own_figure_is_not_called_an_estimate()
    {
        var card = ShareCards.ForSession(Session(exact: true), new AppSettings(), Now, Utc);
        Assert.Equal("cost, as Claude Code counts it", card.HeadlineNote);
        Assert.Null(card.EstimateNote);

        var plan = Usage(Plan(), today: [Session(exact: true)])!;
        Assert.Contains(new ShareStat("Cost today", "$3.05"), plan.Stats);
        Assert.Null(plan.EstimateNote);
    }

    [Fact]
    public void An_unknown_model_s_cost_is_marked_rough()
    {
        Assert.Equal("≈$3.05", ShareCards.ForSession(Session(rough: true, model: "claude-nova-1"), new AppSettings(), Now, Utc).Headline);
        Assert.Contains(new ShareStat("Est. cost today", "≈$3.05"), Usage(Plan(), today: [Session(rough: true)])!.Stats);
    }

    [Fact]
    public void A_cost_of_zero_is_left_out()
    {
        var session = ShareCards.ForSession(Session(cost: 0m), new AppSettings(), Now, Utc);
        Assert.Null(session.Headline);
        Assert.Null(session.HeadlineNote);
        Assert.Null(session.EstimateNote);

        var plan = Usage(Plan(), today: [Session(cost: 0m)])!;
        Assert.DoesNotContain(plan.Stats, s => s.Label.Contains("cost", StringComparison.OrdinalIgnoreCase));
        Assert.Null(plan.EstimateNote);
        var none = Usage(Plan())!;
        Assert.Equal([new ShareStat("Sessions today", "0")], none.Stats);
    }

    [Fact]
    public void A_session_without_tokens_or_a_context_size_has_no_rows_for_them()
    {
        var bare = new SessionInfo
        {
            SessionId = "6eba8773-0000-4000-8000-000000000000", TranscriptPath = "t.jsonl", WorkingDirectory = "/dev/x",
            StartedAt = Now.AddMinutes(-72), LastActivityAt = Now
        };
        var card = ShareCards.ForSession(bare, new AppSettings(), Now, Utc);
        Assert.Equal([new ShareStat("Duration", "1h 12m")], card.Stats);
    }

    [Fact]
    public void No_reading_gives_no_card()
    {
        Assert.Null(Usage(new UsageSnapshot { IsStale = true, FetchedAt = Now }));
        Assert.Null(Usage(new UsageSnapshot { RequiresReauth = true }));
    }

    [Fact]
    public void The_pace_line_says_when_the_5_hour_limit_comes()
    {
        var card = Usage(Plan(fiveHour: 80), [FiveHourForecast(Now.AddMinutes(25))])!;
        Assert.Equal("At this pace: 5-hour limit ~2:25 PM", card.PaceLine);
        Assert.Equal(UsageLevel.Red, card.PaceLevel);

        Assert.Equal(UsageLevel.Orange, Usage(Plan(), [FiveHourForecast(Now.AddMinutes(45))])!.PaceLevel);
        Assert.Equal(UsageLevel.Yellow, Usage(Plan(), [FiveHourForecast(Now.AddMinutes(90))])!.PaceLevel);
    }

    [Fact]
    public void Without_a_limit_before_the_reset_the_pace_line_says_where_the_window_ends()
    {
        var card = Usage(Plan(), [FiveHourForecast(null, projected: 61.4)])!;
        Assert.Equal("On pace for 61% of the 5-hour window", card.PaceLine);
        Assert.Equal(UsageLevel.Yellow, card.PaceLevel);
    }

    [Fact]
    public void The_week_speaks_when_the_5_hour_window_has_no_forecast()
    {
        var week = new PaceForecast(UsageWindowKinds.SevenDay, 60, 1, Now.AddHours(30), 140, 20, ForecastConfidence.Low, "the last 24 hours")
            { ResetsAt = Now.AddDays(2) };
        var card = Usage(Plan(), [week])!;
        Assert.Equal("At this pace: weekly limit ~Thu 8:00 PM", card.PaceLine);
        Assert.Equal(UsageLevel.Yellow, card.PaceLevel);

        var steady = week with { HitsLimitAt = null, ProjectedAtReset = 70 };
        Assert.Equal("On pace for 70% of the week", Usage(Plan(), [steady])!.PaceLine);
    }

    [Fact]
    public void A_reached_5_hour_limit_says_so_and_when_it_resets()
    {
        var card = Usage(Plan(fiveHour: 100), [FiveHourForecast(null, projected: 100)])!;
        Assert.Equal("5-hour limit reached · resets 4:30 PM", card.PaceLine);
        Assert.Equal(UsageLevel.Red, card.PaceLevel);
        Assert.Equal("100%", card.Gauges[0].PercentText);
        Assert.Equal(UsageLevel.Red, card.Gauges[0].Level);
    }

    [Fact]
    public void No_forecast_leaves_the_pace_line_out()
    {
        Assert.Null(Usage(Plan())!.PaceLine);
        // Too unsure, switched off, or below the confidence Settings asks for.
        Assert.Null(Usage(Plan(), [FiveHourForecast(Now.AddMinutes(25), confidence: ForecastConfidence.None)])!.PaceLine);
        Assert.Null(Usage(Plan(), [FiveHourForecast(Now.AddMinutes(25))], settings: new AppSettings { ShowForecast = false })!.PaceLine);
        Assert.Null(Usage(Plan(), [FiveHourForecast(Now.AddMinutes(25), confidence: ForecastConfidence.Low)],
            settings: new AppSettings { ForecastMinConfidence = "high" })!.PaceLine);
        // A forecast for a window that has reset since is old news.
        var old = FiveHourForecast(Now.AddMinutes(-10)) with { ResetsAt = Now.AddMinutes(-5) };
        Assert.Null(Usage(Plan(), [old])!.PaceLine);
    }

    [Fact]
    public void A_window_past_its_reset_reads_zero()
    {
        var card = Usage(Plan() with { FiveHourResetsAt = Now.AddMinutes(-1), WeeklyResetsAt = Now.AddMinutes(-1) })!;
        Assert.Equal(("0%", "starts with your next message"), (card.Gauges[0].PercentText, card.Gauges[0].Detail));
        Assert.Equal(("0%", "resetting now"), (card.Gauges[1].PercentText, card.Gauges[1].Detail));
    }

    [Fact]
    public void A_weekly_only_reading_has_one_ring()
    {
        var card = Usage(Plan() with { FiveHourUtilizationPercent = null, FiveHourResetsAt = null })!;
        Assert.Equal(["Week"], card.Gauges.Select(g => g.Label));
    }

    [Fact]
    public void A_monthly_cap_card_has_the_spend_its_bar_today_and_the_projection()
    {
        var month = new PaceForecast(UsageWindowKinds.MonthSpend, 88.1, 0.1, null, 106, 4, ForecastConfidence.High, "the last 7 days")
            { ResetsAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) };
        var card = Usage(Monthly(176.10m, 88.1), [month], [Session()], spentToday: 4.20m)!;

        Assert.Equal(ShareCardVariant.Spend, card.Variant);
        Assert.Equal("Claude Enterprise", card.Title);
        Assert.Equal("$176.10", card.Headline);
        Assert.Equal("of $200.00 this month", card.HeadlineNote);
        Assert.Equal(("This month", 88.1, "88%", UsageLevel.Orange), (card.Gauges[0].Label, card.Gauges[0].Percent, card.Gauges[0].PercentText, card.Gauges[0].Level));
        Assert.Equal("At this pace: $212.00 by Nov 30", card.PaceLine);
        Assert.Equal(UsageLevel.Orange, card.PaceLevel);
        Assert.Equal([new ShareStat("Spent today", "$4.20"), new ShareStat("Sessions today", "1"), new ShareStat("Top model", "Opus 5.5")],
            card.Stats);
        // The day's figure is approximate; the sessions' cost isn't shown, so it isn't called an estimate.
        Assert.Equal(ShareCards.DaySpendNote, card.EstimateNote);
    }

    [Fact]
    public void A_percent_only_spend_limit_shows_no_dollars()
    {
        var limit = new UsageSnapshot
        {
            Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendPercentOnly = true, MonthlySpendPercent = 63.4,
            MonthlySpendResetsAt = Now.AddDays(12), FetchedAt = Now
        };
        var forecast = new PaceForecast(UsageWindowKinds.SpendLimit, 63.4, 0.2, null, 120, 4, ForecastConfidence.High, "the last 7 days")
            { ResetsAt = new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero) };
        var card = Usage(limit, [forecast], spentToday: 4.20m)!;

        Assert.Equal("Claude spend", card.Title);
        Assert.Equal("63%", card.Headline);
        Assert.Equal("of your spend limit", card.HeadlineNote);
        Assert.Equal("Spend limit", card.Gauges[0].Label);
        Assert.Equal("At this pace: 120% of your limit by Nov 30", card.PaceLine);
        Assert.DoesNotContain(card.AllText(), t => t.Contains('$'));
    }

    [Fact]
    public void A_cap_passed_keeps_its_bar_full_and_its_text_true()
    {
        var card = Usage(Monthly(230m, 115))!;
        Assert.Equal((100.0, "115%", UsageLevel.Red), (card.Gauges[0].Percent, card.Gauges[0].PercentText, card.Gauges[0].Level));
    }

    [Theory]
    [InlineData(42.4, "42%")]
    [InlineData(42.5, "42%")] // banker's rounding, as everywhere else in the app
    [InlineData(42.6, "43%")]
    [InlineData(99.6, "100%")]
    public void Percentages_are_whole_numbers(double percent, string expected) =>
        Assert.Equal(expected, Usage(Plan(fiveHour: percent))!.Gauges[0].PercentText);

    [Fact]
    public void Long_plan_and_model_names_end_in_an_ellipsis()
    {
        var card = Usage(Plan() with { SubscriptionType = "max_ultra_mega_enterprise_preview_plan_2026" },
            today: [Session(model: "claude-supercalifragilisticexpialidociousmodel-9-9")])!;
        Assert.Equal(ShareCards.MaxNameLength, card.Title.Length);
        Assert.EndsWith("…", card.Title);
        var model = card.Stats.Single(s => s.Label == "Top model").Value;
        Assert.Equal(ShareCards.MaxNameLength, model.Length);
        Assert.EndsWith("…", model);
    }

    [Fact]
    public void The_top_model_is_the_one_behind_the_most_cost()
    {
        Assert.Equal("Sonnet 5.5", ShareCards.TopModel(
            [Session(cost: 1m), Session(cost: 1.5m, model: "claude-sonnet-5-5"), Session(cost: 1m, model: "claude-sonnet-5-5")]));
        // Equal cost: the one with more tokens.
        Assert.Equal("Haiku 5.5", ShareCards.TopModel([Session(cost: 1m, output: 10), Session(cost: 1m, model: "claude-haiku-5-5", output: 9_999_999)]));
        Assert.Null(ShareCards.TopModel([]));
    }

    [Fact]
    public void The_shape_comes_from_Settings()
    {
        Assert.Equal(ShareCardShape.Portrait, Usage(Plan(), settings: new AppSettings { ShareCardShape = "Portrait" })!.Shape);
        Assert.Equal(ShareCardShape.Portrait, ShareCards.ForSession(Session(), new AppSettings { ShareCardShape = "portrait" }, Now, Utc).Shape);
        Assert.Equal(ShareCardShape.Wide, ShareCardShapes.Parse("square"));
        Assert.Equal(ShareCardShape.Wide, ShareCardShapes.Parse(null));
        Assert.Equal("portrait", ShareCardShape.Portrait.ToSetting());
        Assert.Equal("wide", ShareCardShape.Wide.ToSetting());
        Assert.Equal((1200, 630), ShareCardShape.Wide.Size());
        Assert.Equal((1080, 1350), ShareCardShape.Portrait.Size());
    }

    [Fact]
    public void Flyout_cards_carry_their_session_for_Share_and_the_customizer_s_preview_has_none()
    {
        var session = Session();
        var card = SessionCardViewModel.From(session, Now);
        Assert.Same(session, card.Session);
        Assert.True(card.ShowShare);
        Assert.False(CardPreview.Card(CardLayout.Default, Now).ShowShare);
    }

    [Fact]
    public void New_settings_keep_names_off_and_the_card_wide()
    {
        var settings = new AppSettings();
        Assert.False(settings.ShareIncludeProjectNames);
        Assert.Equal("wide", settings.ShareCardShape);
    }

    [Fact]
    public void The_post_links_carry_only_the_static_text()
    {
        var x = ShareCards.PostUrl(ShareTarget.X, ShareCardVariant.Limits);
        Assert.StartsWith("https://x.com/intent/tweet?text=", x);
        Assert.Equal("My Claude usage today — made with Sticky AI Usage https://github.com/mohammad-k-shah/stickyAIusage",
            Uri.UnescapeDataString(x["https://x.com/intent/tweet?text=".Length..]));
        var bluesky = ShareCards.PostUrl(ShareTarget.Bluesky, ShareCardVariant.Session);
        Assert.Equal("https://bsky.app/intent/compose?text=" + Uri.EscapeDataString(ShareCards.PostText(ShareCardVariant.Session)), bluesky);
        Assert.StartsWith("This Claude Code session — made with", ShareCards.PostText(ShareCardVariant.Session));
        Assert.Equal("https://www.linkedin.com/sharing/share-offsite/?url=https%3A%2F%2Fgithub.com%2Fmohammad-k-shah%2FstickyAIusage",
            ShareCards.PostUrl(ShareTarget.LinkedIn, ShareCardVariant.Spend));
        Assert.Equal(ShareCards.PostUrl(ShareTarget.X, ShareCardVariant.Limits), ShareCards.PostUrl(ShareTarget.X, ShareCardVariant.Spend));
    }

    [Fact]
    public void Saved_cards_are_named_by_the_local_time()
    {
        var india = TimeZoneInfo.CreateCustomTimeZone("IST", TimeSpan.FromHours(5.5), "IST", "IST");
        Assert.Equal("usage-20261118-1930.png", ShareCards.FileName(Now, india));
        Assert.Equal("usage-20261118-1400.png", ShareCards.FileName(Now, Utc));
    }

    [Fact]
    public void The_date_is_the_local_day()
    {
        var late = new DateTimeOffset(2026, 11, 18, 22, 0, 0, TimeSpan.Zero);
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("JST", TimeSpan.FromHours(9), "JST", "JST");
        Assert.Equal("Thu, Nov 19, 2026", ShareCards.DateText(late, tokyo));
    }

    [Fact]
    public void The_footer_stays_readable_at_half_size_in_both_palettes()
    {
        foreach (var palette in new[] { ShareCardPalette.Dark, ShareCardPalette.Light })
        {
            // WCAG AA for normal text, against both ends of the background gradient.
            Assert.True(Contrast(palette.TextSecondary, palette.Background) >= 4.5, palette.ToString());
            Assert.True(Contrast(palette.TextSecondary, palette.BackgroundEnd) >= 4.5, palette.ToString());
            Assert.True(Contrast(palette.TextTertiary, palette.BackgroundEnd) >= 3, palette.ToString());
            Assert.True(Contrast(palette.TextPrimary, palette.BackgroundEnd) >= 7, palette.ToString());
            foreach (var level in Enum.GetValues<UsageLevel>())
                Assert.True(Contrast(palette.Level(level), palette.Background) >= 3, $"{level} in {palette}");
        }
        Assert.Same(ShareCardPalette.Dark, ShareCardPalette.For(dark: true));
        Assert.Equal(ShareCardPalette.Light.Red, ShareCardPalette.Light.Level(UsageLevel.Red));
    }

    private static double Contrast(string a, string b)
    {
        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static double Luminance(string argb)
    {
        var hex = argb.TrimStart('#')[^6..];
        double Channel(int i)
        {
            var c = Convert.ToInt32(hex.Substring(i, 2), 16) / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Channel(0) + 0.7152 * Channel(2) + 0.0722 * Channel(4);
    }
}
