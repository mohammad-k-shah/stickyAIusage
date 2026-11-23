using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Session costs end to end: recorded transcripts priced with the
/// catalog, the rough flag for unknown models, Claude Code's own figure from
/// the plugin's feed, and how the cards and the pill show each.</summary>
public class SessionCostTests
{
    /// <summary>
    /// Fixture transcripts with known token counts (one response written twice,
    /// a subagent turn, a zero-token "&lt;synthetic&gt;" message, fast mode, US-only
    /// inference, 5-minute and 1-hour cache writes). The expected totals are worked
    /// out by hand from the published price list:
    /// - Fable 5.2 ($8 in, $40 out, $0.20 cache read, $10 5m write, $16 1h write):
    ///   0.234096 + 0.100064 + 0.02064 × 2 (fast) = 0.37544.
    /// - Opus 5.5 ($4, $20, $0.20, $5, $8): 0.290012 + 0.170008 + 0.032004 × 1.1 (US-only) = 0.4952244.
    /// - Sonnet 5.5 ($2, $10, $0.10, $2.50): 0.09004 + 0.038008 + 0.011212 = 0.13926.
    /// - Haiku 5.5 ($0.10, $0.50, $0.01, $0.125; above 100,000 prompt tokens $0.50, $2.50,
    ///   $0.05, $0.625): 0.007951 + 0.0338775 (105,005-token prompt) + 0.00125027
    ///   (exactly 100,000: standard) = 0.04307877.
    /// </summary>
    [Theory]
    [InlineData("fable-5-2.jsonl", "5f2a7c10-1b3e-4d55-9a61-0c8e2f4b7a01", 0.37544, 1_000_000L)]
    [InlineData("opus-5-5.jsonl", "0b9d3e44-6a21-4f8c-8d17-5e3a9c2b1f02", 0.4952244, 1_000_000L)]
    [InlineData("sonnet-5-5.jsonl", "7c4e1a90-2d5b-4b3f-a8e6-91f0d3c5b203", 0.13926, 1_000_000L)]
    [InlineData("haiku-5-5.jsonl", "e3a8f5d2-9c17-4a06-b4d2-6f1e8a0c7d04", 0.04307877, 1_000_000L)]
    public void A_recorded_session_reconciles_with_the_price_list_within_one_percent(string fixture, string sessionId,
        double expected, long contextWindow)
    {
        using var dir = new TempDir();
        var path = dir.Combine("projects", "proj", sessionId + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "transcripts", fixture), path);

        var session = Readers.For(dir).ReadSession(path)!;

        Assert.InRange((double)session.EstimatedCostUsd, expected * 0.99, expected * 1.01);
        Assert.False(session.CostIsRough);
        Assert.Equal(contextWindow, session.ContextWindowSize);
    }

    [Fact]
    public void A_session_on_a_model_the_list_does_not_know_is_rough()
    {
        using var dir = new TempDir();
        var id = Guid.NewGuid().ToString();
        var path = dir.Combine("projects", "proj", id + ".jsonl");
        var t = DateTimeOffset.UtcNow;
        Transcript.Write(path,
            Transcript.Assistant(id, "m1", t, input: 1_000_000, model: "claude-sonnet-5-5"),
            Transcript.Assistant(id, "m2", t.AddSeconds(5), output: 1_000_000, model: "claude-nova-1"));

        var session = Readers.For(dir).ReadSession(path)!;

        Assert.True(session.CostIsRough);
        Assert.Equal(2m + 15m, session.EstimatedCostUsd); // Sonnet 5.5 input + the fallback's output price
        Assert.Equal("≈$17.00", Format.SessionCost(session));
    }

    [Fact]
    public void A_zero_token_synthetic_message_does_not_make_the_cost_rough()
    {
        using var dir = new TempDir();
        var id = Guid.NewGuid().ToString();
        var path = dir.Combine("projects", "proj", id + ".jsonl");
        var t = DateTimeOffset.UtcNow;
        Transcript.Write(path,
            Transcript.Assistant(id, "m1", t, output: 1_000_000, model: "claude-sonnet-5-5"),
            Transcript.Assistant(id, "m2", t.AddSeconds(5), model: "<synthetic>"));

        var session = Readers.For(dir).ReadSession(path)!;

        Assert.False(session.CostIsRough);
        Assert.Equal(10m, session.EstimatedCostUsd);
        Assert.Equal("claude-sonnet-5-5", session.Model);
    }

    // ------------------------------------------------ Claude Code's own figure

    private static SessionInfo Session(DateTimeOffset started, DateTimeOffset last, decimal estimate = 1.20m,
        string id = "aaaaaaaa-0000-0000-0000-000000000001") => new()
    {
        SessionId = id,
        TranscriptPath = "/tmp/" + id + ".jsonl",
        WorkingDirectory = Transcript.Cwd,
        Model = "claude-opus-5-5",
        StartedAt = started,
        LastActivityAt = last,
        EstimatedCostUsd = estimate
    };

    private static FeedSession Record(string id, decimal? cost, DateTimeOffset? startedAt, DateTimeOffset observedAt) =>
        new(id, "cli", Transcript.Cwd, "claude-opus-5-5", "2.1.325", "2.0.0", observedAt, startedAt, observedAt,
            null, null, null, null, cost, "idle", null, null);

    private static readonly DateTimeOffset T0 = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_feeds_cost_replaces_the_estimate_when_it_covers_the_whole_session()
    {
        var session = Session(T0, T0.AddMinutes(30));
        var feed = new[] { Record("other", 9m, T0, T0.AddMinutes(30)), Record(session.SessionId, 1.37m, T0.AddSeconds(-3), T0.AddMinutes(30)) };

        Assert.True(SessionCosts.ApplyExact(session, feed));

        Assert.Equal(1.37m, session.CostUsd);
        Assert.Equal(1.20m, session.EstimatedCostUsd); // kept for history and the day's spend
        Assert.Equal("$1.37", Format.SessionCost(session));
    }

    [Fact]
    public void A_resumed_session_keeps_its_estimate()
    {
        // The plugin started seeing the session an hour after its first message:
        // its figure would only cover the part since the resume.
        var session = Session(T0, T0.AddHours(2));
        var feed = new[] { Record(session.SessionId, 0.40m, T0.AddHours(1), T0.AddHours(2)) };

        Assert.False(SessionCosts.ApplyExact(session, feed));
        Assert.Equal(1.20m, session.CostUsd);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(-1.0)]
    public void A_record_without_a_usable_cost_keeps_the_estimate(double? cost)
    {
        var session = Session(T0, T0.AddMinutes(5));
        var feed = new[] { Record(session.SessionId, (decimal?)cost, T0, T0.AddMinutes(5)) };
        Assert.False(SessionCosts.ApplyExact(session, feed));
    }

    [Fact]
    public void A_record_behind_the_transcript_or_without_a_start_keeps_the_estimate()
    {
        var session = Session(T0, T0.AddMinutes(30));
        Assert.False(SessionCosts.ApplyExact(session, [Record(session.SessionId, 1m, T0, T0.AddMinutes(20))]));
        Assert.False(SessionCosts.ApplyExact(session, [Record(session.SessionId, 1m, null, T0.AddMinutes(30))]));
        Assert.False(SessionCosts.ApplyExact(session, []));
    }

    [Fact]
    public void A_cost_that_stops_matching_is_cleared_again()
    {
        var session = Session(T0, T0.AddMinutes(5));
        SessionCosts.ApplyExact(session, [Record(session.SessionId, 2m, T0, T0.AddMinutes(5))]);
        SessionCosts.ApplyExact(session, []);
        Assert.False(session.CostIsExact);
    }

    // ------------------------------------------------------------ display

    [Fact]
    public void A_card_says_where_its_figure_comes_from()
    {
        var now = T0.AddMinutes(10);
        var estimate = SessionCardViewModel.From(Session(T0, T0.AddMinutes(5)), now);
        Assert.Equal(("$1.20", "est. cost", false), (estimate.CostText, estimate.CostLabel, estimate.CostIsExact));
        Assert.Contains("list prices", estimate.CostToolTip);

        var roughSession = new SessionInfo
        {
            SessionId = "r", TranscriptPath = "/tmp/r.jsonl", WorkingDirectory = "/w", StartedAt = T0,
            LastActivityAt = T0, EstimatedCostUsd = 3m, CostIsRough = true
        };
        var rough = SessionCardViewModel.From(roughSession, now);
        Assert.Equal(("≈$3.00", "est. cost", "Unknown model: price estimated"), (rough.CostText, rough.CostLabel, rough.CostToolTip));

        var exactSession = Session(T0, T0.AddMinutes(5));
        exactSession.ExactCostUsd = 1.37m;
        var exact = SessionCardViewModel.From(exactSession, now);
        Assert.Equal(("$1.37", "cost", true, 1.37m), (exact.CostText, exact.CostLabel, exact.CostIsExact, exact.CostUsd));
        Assert.NotEqual(estimate.Signature, exact.Signature);
    }

    [Fact]
    public void An_exact_figure_is_never_shown_as_rough()
    {
        var session = new SessionInfo
        {
            SessionId = "r", TranscriptPath = "/tmp/r.jsonl", WorkingDirectory = "/w", StartedAt = T0,
            LastActivityAt = T0, EstimatedCostUsd = 3m, CostIsRough = true, ExactCostUsd = 2.5m
        };
        Assert.Equal("$2.50", Format.SessionCost(session));
        Assert.Equal("cost", Format.SessionCostNote(session).Label);
    }

    [Fact]
    public void The_cost_field_and_the_flyout_header_follow_the_same_rules()
    {
        var now = T0.AddMinutes(10);
        var rough = new SessionInfo
        {
            SessionId = "r", TranscriptPath = "/tmp/r.jsonl", WorkingDirectory = "/w", StartedAt = T0,
            LastActivityAt = T0, EstimatedCostUsd = 3m, CostIsRough = true
        };
        Assert.Equal("≈$3.00", SessionFields.Find("cost")!.Value(rough));

        var exact = Session(T0, T0.AddMinutes(5));
        exact.ExactCostUsd = 1m;
        var cards = new[] { SessionCardViewModel.From(exact, now) };
        Assert.Equal("1 session · $1.00", WidgetViewModel.SessionsSummary(cards));
        var mixed = new[] { cards[0], SessionCardViewModel.From(Session(T0, T0, 2m, "b"), now) };
        Assert.Equal("2 sessions · $3.00 estimated", WidgetViewModel.SessionsSummary(mixed));
    }
}

/// <summary>Tests that switch the price list in use for the whole process; they
/// run on their own so no other test sees the switched list.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PriceListInUse
{
    public const string Name = "Price list in use";
}

[Collection(PriceListInUse.Name)]
public class PriceListSwitchTests
{
    [Fact]
    public void A_new_price_list_prices_already_read_sessions_again()
    {
        using var dir = new TempDir();
        var id = Guid.NewGuid().ToString();
        var path = dir.Combine("projects", "proj", id + ".jsonl");
        Transcript.Write(path, Transcript.Assistant(id, "m1", DateTimeOffset.UtcNow, input: 1_000_000, model: "claude-sonnet-5"));
        var reader = Readers.For(dir);
        Assert.Equal(2m, reader.ReadSession(path)!.EstimatedCostUsd);

        try
        {
            ModelCatalog.Use(ModelCatalogTests.Parse(ModelCatalogTests.MinimalJson(updated: "2099-01-01", sonnet5Input: 7m)));
            Assert.Equal(7m, reader.ReadSession(path)!.EstimatedCostUsd);
        }
        finally
        {
            ModelCatalog.Reset();
        }
        Assert.Equal(2m, reader.ReadSession(path)!.EstimatedCostUsd);
    }

    [Fact]
    public void The_list_in_use_is_the_newer_of_the_built_in_and_the_downloaded_one()
    {
        var cache = Path.Combine(AppPaths.CacheDirectory, ModelCatalog.CacheFileName);
        Directory.CreateDirectory(AppPaths.CacheDirectory);
        try
        {
            File.WriteAllText(cache, ModelCatalogTests.MinimalJson(updated: ModelCatalogTests.DayAfterBuiltIn));
            ModelCatalog.Reset();
            Assert.Equal("downloaded", ModelCatalog.Current.Origin);

            File.WriteAllText(cache, ModelCatalogTests.MinimalJson(updated: "2020-01-01"));
            ModelCatalog.Reset();
            Assert.Same(ModelCatalog.BuiltIn, ModelCatalog.Current);
        }
        finally
        {
            File.Delete(cache);
            ModelCatalog.Reset();
        }
    }
}
