using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>The Claude snapshot in the provider-neutral shape, for both kinds
/// of plan and every health state.</summary>
public class ProviderUsageMappingTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 17, 9, 0, 0, TimeSpan.Zero);

    private static UsageSnapshot Map(string json, string? subscription = null)
    {
        using var doc = JsonDocument.Parse(json);
        return ClaudeOAuthUsageSource.MapToSnapshot(doc.RootElement, subscription, T);
    }

    [Fact]
    public void A_Pro_Max_reading_becomes_its_windows()
    {
        var snapshot = Map("""
            { "five_hour": { "utilization": 43, "resets_at": "2026-11-17T11:00:00Z" },
              "seven_day": { "utilization": 14, "resets_at": "2026-11-20T00:00:00Z" },
              "seven_day_opus": { "utilization": 5 },
              "seven_day_sonnet": { "utilization": 9 } }
            """, "max") with { SourceId = "claude-oauth", Trust = SourceTrust.Unofficial };

        var usage = snapshot.ToProviderUsage();

        Assert.Equal("claude", usage.ProviderId);
        Assert.Equal("max", usage.PlanName);
        Assert.Equal([UsageWindowKinds.FiveHour, UsageWindowKinds.SevenDay, UsageWindowKinds.SevenDayOpus, UsageWindowKinds.SevenDaySonnet],
            usage.Windows.Select(w => w.Kind));
        Assert.Equal(new UsageWindow(UsageWindowKinds.FiveHour, 43, new DateTimeOffset(2026, 11, 17, 11, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(5)),
            usage.Window(UsageWindowKinds.FiveHour));
        Assert.Equal(TimeSpan.FromDays(7), usage.Window(UsageWindowKinds.SevenDay)!.Length);
        Assert.Equal(9, usage.Window(UsageWindowKinds.SevenDaySonnet)!.PercentUsed);
        Assert.Null(usage.Spend);
        Assert.Equal(T, usage.ObservedAt);
        Assert.Equal("claude-oauth", usage.SourceId);
        Assert.Equal(SourceTrust.Unofficial, usage.Trust);
        Assert.Equal(UsageHealth.Ok, usage.Health);
    }

    [Fact]
    public void A_monthly_cap_becomes_a_spend_cap_and_a_month_window()
    {
        var snapshot = Map("""
            { "spend": { "used": { "amount_minor": 9675, "exponent": 2, "currency": "USD" },
                         "limit": { "amount_minor": 20000, "exponent": 2 }, "percent": 48.4 } }
            """, "enterprise");

        var usage = snapshot.ToProviderUsage();

        Assert.Equal(new SpendCap(96.75m, 200m, 48.4, null, "month"), usage.Spend);
        var month = Assert.Single(usage.Windows);
        Assert.Equal(UsageWindowKinds.MonthSpend, month.Kind);
        Assert.Equal(48.4, month.PercentUsed);
        Assert.Equal("enterprise", usage.PlanName);
    }

    [Fact]
    public void Windows_that_were_not_reported_are_left_out()
    {
        var usage = Map("""{ "five_hour": { "utilization": 10 }, "seven_day": null }""").ToProviderUsage();

        Assert.Equal(UsageWindowKinds.FiveHour, Assert.Single(usage.Windows).Kind);
        Assert.Null(usage.Window(UsageWindowKinds.SevenDay));
    }

    [Fact]
    public void Health_follows_the_snapshot()
    {
        var plan = Map("""{ "five_hour": { "utilization": 10 } }""");

        Assert.Equal(UsageHealth.Ok, plan.ToProviderUsage().Health);
        Assert.Equal(UsageHealth.Stale, (plan with { IsStale = true }).ToProviderUsage().Health);

        var signIn = (plan with { IsStale = true, RequiresReauth = true }).ToProviderUsage();
        Assert.Equal(UsageHealth.NeedsSignIn, signIn.Health);
        Assert.Contains("claude auth login", signIn.HealthDetail);

        var nothing = new UsageSnapshot { IsStale = true }.ToProviderUsage();
        Assert.Equal(UsageHealth.Unavailable, nothing.Health);
        Assert.Equal("none", nothing.SourceId);
        Assert.Empty(nothing.Windows);
    }

    [Fact]
    public void The_source_survives_the_disk_cache()
    {
        var snapshot = Map("""{ "five_hour": { "utilization": 10 } }""") with { SourceId = "claude-oauth", Trust = SourceTrust.Unofficial };

        var restored = JsonSerializer.Deserialize<UsageSnapshot>(JsonSerializer.Serialize(snapshot))!;

        Assert.Equal(snapshot, restored);
    }

    [Fact]
    public void A_cache_file_written_by_v1_still_loads()
    {
        var restored = JsonSerializer.Deserialize<UsageSnapshot>("""
            {"Mode":2,"HasMonthlySpend":false,"FiveHourUtilizationPercent":43,"IsStale":false,"FetchedAt":"2026-11-17T09:00:00+00:00"}
            """)!;

        Assert.Equal(UsageMode.PlanLimits, restored.Mode);
        Assert.Null(restored.SourceId);
        Assert.Null(restored.Trust);
    }
}

/// <summary>The feed the windows poll, built with fakes: proof that nothing
/// in it reaches for the network or the real transcript folder.</summary>
public class UsageFeedTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private sealed class FixedProvider(UsageSnapshot snapshot) : IUsageProvider
    {
        public string ProviderId => "claude";
        public string DisplayName => "Claude";
        public int Calls { get; private set; }

        public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(snapshot);
        }

        public Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(snapshot.ToProviderUsage());
    }

    [Fact]
    public async Task The_summary_comes_from_the_injected_provider_and_session_reader()
    {
        var now = DateTimeOffset.Now;
        var sessionId = Guid.NewGuid().ToString();
        Transcript.Write(_dir.Combine("projects", "acme", sessionId + ".jsonl"),
            Transcript.User(sessionId, now.AddMinutes(-5)),
            Transcript.Assistant(sessionId, "msg_1", now.AddMinutes(-4), input: 1000, output: 2000, model: "claude-sonnet-4-5"));
        var provider = new FixedProvider(new UsageSnapshot
        {
            Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendUsedUsd = 40m, MonthlySpendLimitUsd = 100m
        });
        var feed = new UsageFeed(provider, new ClaudeSessionLogSource(_dir.Combine("projects")),
            new DailySpendTracker(_dir.Combine("day-spend.json")));

        var (usage, session, spentToday) = await feed.GetSummaryAsync();

        Assert.Equal(1, provider.Calls);
        Assert.Equal(40m, usage.MonthlySpendUsedUsd);
        Assert.Equal(sessionId, session?.SessionId);
        Assert.NotNull(spentToday);
        // Two days: run just after midnight, the session from five minutes ago is yesterday's.
        var card = Assert.Single(await feed.GetCardsAsync(2));
        Assert.Equal(sessionId, card.SessionId);
    }
}
