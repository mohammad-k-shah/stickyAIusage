using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>How the provider chooses between its sources: freshness, trust,
/// and never asking a less trusted source while a better one is fresh.</summary>
public class ClaudeUsageProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private DateTime? _lastTranscriptWrite;

    private sealed class FakeSource(string id, SourceTrust trust, Func<UsageSnapshot?> reading) : IClaudeUsageSource
    {
        public string SourceId => id;
        public SourceTrust Trust => trust;
        public int Calls { get; private set; }

        public Task<UsageSnapshot?> TryGetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(reading());
        }
    }

    private static UsageSnapshot Reading(double fiveHour, TimeSpan age, bool stale = false) => new()
    {
        Mode = UsageMode.PlanLimits,
        FiveHourUtilizationPercent = fiveHour,
        FetchedAt = Now - age,
        IsStale = stale
    };

    private ClaudeUsageProvider Provider(params IClaudeUsageSource[] sources) =>
        new(sources, () => Now, () => _lastTranscriptWrite);

    private void ClaudeCodeIsWorking() => _lastTranscriptWrite = Now.UtcDateTime.AddSeconds(-30);

    [Fact]
    public async Task With_only_the_endpoint_source_its_reading_is_returned_as_is()
    {
        var reading = Reading(43, TimeSpan.FromHours(3), stale: true) with { RequiresReauth = true, SubscriptionType = "max" };
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => reading);

        var snapshot = await Provider(oauth).GetUsageAsync();

        Assert.Equal(reading with { SourceId = "claude-oauth", Trust = SourceTrust.Unofficial }, snapshot);
        Assert.Equal(1, oauth.Calls);
    }

    [Fact]
    public async Task With_one_source_the_transcript_folder_is_not_scanned()
    {
        var probes = 0;
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(43, TimeSpan.Zero));
        var provider = new ClaudeUsageProvider([oauth], () => Now, () => { probes++; return null; });

        await provider.GetUsageAsync();

        Assert.Equal(0, probes);
    }

    [Fact]
    public async Task A_less_trusted_source_is_not_asked_while_a_more_trusted_one_is_fresh()
    {
        var feed = new FakeSource("claude-feed", SourceTrust.Official, () => Reading(40, TimeSpan.FromMinutes(9)));
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(55, TimeSpan.Zero));

        // Registered in the "wrong" order: trust decides who is asked first.
        var snapshot = await Provider(oauth, feed).GetUsageAsync();

        Assert.Equal("claude-feed", snapshot.SourceId);
        Assert.Equal(40, snapshot.FiveHourUtilizationPercent);
        Assert.Equal(0, oauth.Calls);
    }

    [Fact]
    public async Task While_Claude_Code_works_a_reading_older_than_a_minute_is_not_fresh()
    {
        ClaudeCodeIsWorking();
        var feed = new FakeSource("claude-feed", SourceTrust.Official, () => Reading(40, TimeSpan.FromSeconds(90)));
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(41, TimeSpan.FromSeconds(30)));

        var snapshot = await Provider(feed, oauth).GetUsageAsync();

        // Asked, but within two minutes of each other the more trusted wins.
        Assert.Equal(1, oauth.Calls);
        Assert.Equal("claude-feed", snapshot.SourceId);
    }

    [Fact]
    public async Task A_much_newer_reading_beats_a_more_trusted_old_one()
    {
        var feed = new FakeSource("claude-feed", SourceTrust.Official, () => Reading(40, TimeSpan.FromMinutes(25)));
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(52, TimeSpan.FromMinutes(1)));

        var snapshot = await Provider(feed, oauth).GetUsageAsync();

        Assert.Equal("claude-oauth", snapshot.SourceId);
        Assert.Equal(SourceTrust.Unofficial, snapshot.Trust);
        Assert.Equal(52, snapshot.FiveHourUtilizationPercent);
    }

    [Fact]
    public async Task A_stale_reading_never_beats_a_current_one()
    {
        var feed = new FakeSource("claude-feed", SourceTrust.Official, () => Reading(40, TimeSpan.Zero, stale: true));
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(52, TimeSpan.FromMinutes(8)));

        var snapshot = await Provider(feed, oauth).GetUsageAsync();

        Assert.Equal(1, oauth.Calls);
        Assert.Equal("claude-oauth", snapshot.SourceId);
    }

    [Fact]
    public async Task With_every_reading_stale_the_newest_is_shown()
    {
        var feed = new FakeSource("claude-feed", SourceTrust.Official, () => Reading(40, TimeSpan.FromHours(1), stale: true));
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(52, TimeSpan.FromMinutes(5), stale: true));

        var snapshot = await Provider(feed, oauth).GetUsageAsync();

        Assert.Equal("claude-oauth", snapshot.SourceId);
        Assert.True(snapshot.IsStale);
    }

    [Fact]
    public async Task A_source_with_nothing_or_one_that_throws_is_passed_over()
    {
        var empty = new FakeSource("claude-feed", SourceTrust.Official, () => null);
        var broken = new FakeSource("broken", SourceTrust.Derived, () => throw new InvalidOperationException("boom"));
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(52, TimeSpan.Zero));

        var snapshot = await Provider(empty, broken, oauth).GetUsageAsync();

        Assert.Equal("claude-oauth", snapshot.SourceId);
        Assert.Equal(1, broken.Calls);
    }

    [Fact]
    public async Task No_reading_at_all_is_an_empty_stale_snapshot()
    {
        var empty = new FakeSource("claude-feed", SourceTrust.Official, () => null);

        var snapshot = await Provider(empty).GetUsageAsync();

        Assert.True(snapshot.IsStale);
        Assert.Equal(UsageMode.Unknown, snapshot.Mode);
        Assert.Null(snapshot.SourceId);
        Assert.Equal(UsageHealth.Unavailable, snapshot.ToProviderUsage().Health);
    }

    [Fact]
    public async Task The_provider_neutral_reading_names_its_source()
    {
        var oauth = new FakeSource("claude-oauth", SourceTrust.Unofficial, () => Reading(43, TimeSpan.Zero));

        var usage = await Provider(oauth).GetProviderUsageAsync();

        Assert.Equal("claude", usage.ProviderId);
        Assert.Equal("Claude", usage.DisplayName);
        Assert.Equal("claude-oauth", usage.SourceId);
        Assert.Equal(SourceTrust.Unofficial, usage.Trust);
        Assert.Equal(43, usage.Window(UsageWindowKinds.FiveHour)!.PercentUsed);
    }

    [Theory]
    [InlineData(30, 60)]       // a transcript changed 30 s ago: Claude Code is working
    [InlineData(119, 60)]
    [InlineData(121, 600)]     // quiet for over two minutes
    public void Freshness_follows_transcript_activity(int secondsSinceWrite, int expectedSeconds)
    {
        var limit = ClaudeUsageProvider.FreshnessLimit(Now, Now.UtcDateTime.AddSeconds(-secondsSinceWrite));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), limit);
    }

    [Fact]
    public void No_transcripts_at_all_means_quiet()
    {
        Assert.Equal(ClaudeUsageProvider.IdleFreshness, ClaudeUsageProvider.FreshnessLimit(Now, null));
    }
}
