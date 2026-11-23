using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.Stats;
using Xunit;

namespace StickyAIusage.Tests;

/// <summary>The year card: when it is offered (injected time and zone) and what it says.</summary>
public class YearCardTests
{
    private static readonly TimeZoneInfo Kolkata = TimeZoneInfo.CreateCustomTimeZone("Test +05:30", TimeSpan.FromMinutes(330), "Test", "Test");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static YearSummary Year(long output = 48_200_000, decimal cost = 3412.5m, bool rough = false, string? model = "Opus 5.5") =>
        new(2026, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 15), 1_000, output, 900_000_000, 50_000_000, cost, rough,
            312, 148, model, new DateOnly(2026, 3, 14), 4_100_000);

    [Theory]
    [InlineData(2026, 11, 30, 23, 59, null)]
    [InlineData(2026, 12, 1, 0, 0, 2026)]
    [InlineData(2026, 12, 31, 23, 59, 2026)]
    [InlineData(2027, 1, 1, 0, 0, 2026)]
    [InlineData(2027, 1, 31, 23, 59, 2026)]
    [InlineData(2027, 2, 1, 0, 0, null)]
    [InlineData(2026, 6, 15, 12, 0, null)]
    public void The_card_is_offered_from_1_December_to_31_January_local_time(int year, int month, int day, int hour, int minute, int? offered)
    {
        var now = new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.FromMinutes(330));
        Assert.Equal(offered, YearCard.SeasonYear(now, Kolkata));
    }

    [Fact]
    public void The_season_starts_at_local_midnight_not_UTC_midnight()
    {
        // 1 December 00:10 in Kolkata is still 30 November 18:40 in UTC.
        var now = new DateTimeOffset(2026, 11, 30, 18, 40, 0, TimeSpan.Zero);
        Assert.Equal(2026, YearCard.SeasonYear(now, Kolkata));
        Assert.Null(YearCard.SeasonYear(now, Utc));
    }

    [Fact]
    public void The_card_shows_the_year_s_tokens_cost_sessions_days_model_and_busiest_day()
    {
        var card = ShareCards.ForYear(Year(), new AppSettings())!;

        Assert.Equal(ShareCardVariant.Year, card.Variant);
        Assert.Equal(2026, card.Year);
        Assert.Equal("My 2026 with Claude Code", card.Title);
        Assert.Equal("Year in review", card.Kicker);
        Assert.Equal("Jan 1 – Dec 15, 2026", card.DateText);
        Assert.Equal("998M", card.Headline); // 1,000 + 48.2M + 900M + 50M = 998,201,000
        Assert.Equal("tokens, cache included", card.HeadlineNote);
        Assert.Equal(
            [
                new ShareStat("Est. cost", "$3,412.50"),
                new ShareStat("Sessions", "312"),
                new ShareStat("Active days", "148"),
                new ShareStat("Favorite model", "Opus 5.5"),
                new ShareStat("Busiest day", "Sat, Mar 14")
            ],
            card.Stats);
        Assert.Equal(ShareCards.CostEstimateNote, card.EstimateNote);
        Assert.Empty(card.Gauges);
        Assert.Null(card.PaceLine);
    }

    [Fact]
    public void A_rough_cost_is_marked_and_a_zero_cost_or_missing_model_is_left_out()
    {
        Assert.Equal("≈$12,480", ShareCards.ForYear(Year(cost: 12_480.4m, rough: true), new AppSettings())!.Stats[0].Value);

        var plain = ShareCards.ForYear(Year(cost: 0, model: null), new AppSettings())!;
        Assert.DoesNotContain(plain.Stats, s => s.Label is "Est. cost" or "Favorite model");
        Assert.Null(plain.EstimateNote);
    }

    [Fact]
    public void A_year_without_any_tokens_has_no_card()
    {
        var empty = YearSummary.Sum(2026, new DateOnly(2026, 1, 1), new DateOnly(2026, 12, 15), new Dictionary<DateOnly, DayStats>());
        Assert.Null(ShareCards.ForYear(empty, new AppSettings()));
        Assert.Equal("No Claude Code sessions in 2026 to show yet.", YearCard.NothingYet(2026));
    }

    [Fact]
    public void The_year_card_never_names_a_project_even_with_names_turned_on()
    {
        var card = ShareCards.ForYear(Year(), new AppSettings { ShareIncludeProjectNames = true })!;
        Assert.Null(card.Subtitle);
        Assert.DoesNotContain(card.AllText(), t => t.Contains("acme", StringComparison.OrdinalIgnoreCase) ||
                                                   t.Contains(ShareCards.HiddenProject, StringComparison.Ordinal));
    }

    [Fact]
    public void The_shape_follows_Settings()
    {
        Assert.Equal(ShareCardShape.Portrait, ShareCards.ForYear(Year(), new AppSettings { ShareCardShape = "portrait" })!.Shape);
        Assert.Equal(ShareCardShape.Wide, ShareCards.ForYear(Year(), new AppSettings())!.Shape);
    }

    [Theory]
    [InlineData(0.5, "$0.50")]
    [InlineData(1234.567, "$1,234.57")]
    [InlineData(9999.99, "$9,999.99")]
    [InlineData(10000.4, "$10,000")]
    [InlineData(123456.5, "$123,457")]
    public void Year_costs_have_thousands_separators(double amount, string expected) =>
        Assert.Equal(expected, ShareCards.YearUsd((decimal)amount));

    [Fact]
    public void Posts_and_saved_files_name_the_year()
    {
        Assert.Equal("My 2026 with Claude Code — made with Sticky AI Usage https://github.com/mohammad-k-shah/stickyAIusage",
            ShareCards.PostText(ShareCardVariant.Year, 2026));
        Assert.StartsWith("https://x.com/intent/tweet?text=My%202026%20with%20Claude%20Code",
            ShareCards.PostUrl(ShareTarget.X, ShareCardVariant.Year, 2026));
        Assert.Equal("claude-code-2026-20261215-1030.png",
            ShareCards.FileName(new DateTimeOffset(2026, 12, 15, 10, 30, 0, TimeSpan.Zero), Utc, 2026));
        Assert.Equal("usage-20261215-1030.png", ShareCards.FileName(new DateTimeOffset(2026, 12, 15, 10, 30, 0, TimeSpan.Zero), Utc));
    }

    [Theory]
    [InlineData(true, true, ShareFlow.ShareTooltip)]
    [InlineData(true, false, ShareFlow.ShareTooltip)]
    [InlineData(false, true, ShareFlow.ShareYearTooltip)]
    [InlineData(false, false, ShareFlow.NoReadingTooltip)]
    public void The_header_s_Share_says_what_it_can_share(bool hasReading, bool inSeason, string expected) =>
        Assert.Equal(expected, ShareFlow.HeaderTooltip(hasReading, inSeason));

    [Fact]
    public void The_progress_line_counts_the_transcripts()
    {
        Assert.Equal("Reading 2026's transcripts…", YearCard.ProgressText(2026, new StatsProgress(0, 0)));
        Assert.Equal("Reading 2026's transcripts… 120 of 840", YearCard.ProgressText(2026, new StatsProgress(120, 840)));
    }

    [Fact]
    public async Task A_post_of_the_year_card_opens_the_year_s_text()
    {
        var output = new RecordingOutput();
        await ShareFlow.RunAsync(ShareAction.PostBluesky, [1, 2, 3], ShareCardVariant.Year, output,
            new DateTimeOffset(2026, 12, 15, 10, 30, 0, TimeSpan.Zero), Utc, 2026);
        Assert.Contains("My%202026%20with%20Claude%20Code", output.Opened);
    }

    private sealed class RecordingOutput : IShareOutput
    {
        public string? Opened { get; private set; }
        public Task<bool> CopyImageAsync(byte[] png) => Task.FromResult(true);
        public Task<string?> SaveImageAsync(byte[] png, string suggestedName) => Task.FromResult<string?>(suggestedName);
        public Task<string?> SaveToPicturesAsync(byte[] png, string fileName) => Task.FromResult<string?>(fileName);

        public Task<bool> OpenUrlAsync(string url)
        {
            Opened = url;
            return Task.FromResult(true);
        }
    }
}
