using System.IO;
using System.Text.Json;
using StickyAIusage.Alerts;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>The session lights end to end in Core: records in a feed folder →
/// a refresh's activities, the plugin flag and the "needs you" alert.</summary>
public class SessionLightsWiringTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NoEndpoint(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("Feed mode never builds the endpoint.");

    private AppServices Services(AppSettings settings) =>
        AppServices.Create(settings, new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false,
            NoEndpoint, cacheDirectory: _dir.Combine("cache"));

    private void WriteRecord(string id, string state, TimeSpan waited, object? attention = null)
    {
        var now = DateTimeOffset.UtcNow;
        var ms = now.ToUnixTimeMilliseconds();
        Directory.CreateDirectory(_dir.Combine("feed"));
        File.WriteAllText(_dir.Combine("feed", $"claude-{id}.json"), JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schema"] = 1, ["source"] = "claude-code-mod", ["sessionId"] = id, ["cwd"] = "C:/work/acme-webapp",
            ["observedAt"] = ms, ["startedAt"] = ms - 600_000, ["rateLimitsAt"] = null, ["rateLimits"] = Array.Empty<object>(),
            ["state"] = state, ["stateSince"] = ms - (long)waited.TotalMilliseconds, ["attention"] = attention
        }));
    }

    [Fact]
    public async Task A_waiting_session_reaches_the_summary_and_notifies_once()
    {
        WriteRecord("s1", "needs_you", TimeSpan.FromSeconds(30), new { type = "permission", tool = "Bash" });
        WriteRecord("s2", "working", TimeSpan.FromMinutes(2));
        using var services = Services(new AppSettings { ClaudeSource = "feed" });

        var summary = await services.Feed.GetSummaryAsync();
        Assert.Equal(["s1", "s2"], summary.Activities.Select(a => a.SessionId));
        Assert.True(summary.HasPlugin);
        var alert = Assert.Single(summary.Alerts);
        Assert.Equal(AlertKind.NeedsYou, alert.Kind);
        Assert.StartsWith("acme-webapp: waiting for permission to use Bash", alert.Body);
        Assert.Null(summary.AttentionDueAt);

        Assert.Empty((await services.Feed.GetSummaryAsync()).Alerts);
    }

    [Fact]
    public async Task A_fresh_wait_says_when_to_look_again()
    {
        WriteRecord("s1", "needs_you", TimeSpan.FromSeconds(5), new { type = "question" });
        using var services = Services(new AppSettings { ClaudeSource = "feed" });

        var before = DateTimeOffset.UtcNow;
        var summary = await services.Feed.GetSummaryAsync();
        Assert.Empty(summary.Alerts);
        Assert.NotNull(summary.AttentionDueAt);
        Assert.InRange(summary.AttentionDueAt!.Value, before.AddSeconds(13), before.AddSeconds(16));
    }

    [Fact]
    public async Task Without_any_record_or_marker_the_plugin_is_missing()
    {
        using var services = Services(new AppSettings { ClaudeSource = "feed" });
        var summary = await services.Feed.GetSummaryAsync();
        Assert.False(summary.HasPlugin);
        Assert.Empty(summary.Activities);

        Directory.CreateDirectory(_dir.Combine("feed"));
        File.WriteAllText(_dir.Combine("feed", ClaudeFeedUsageSource.MarkerFileName), "{}");
        Assert.True((await services.Feed.GetSummaryAsync()).HasPlugin);
    }

    /// <summary>The usage endpoint, faked: never asked for more than a reading.</summary>
    private sealed class FakeEndpoint : IClaudeUsageSource
    {
        public string SourceId => "claude-oauth";
        public SourceTrust Trust => SourceTrust.Unofficial;
        public Task<Models.UsageSnapshot?> TryGetAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Models.UsageSnapshot?>(new Models.UsageSnapshot { FetchedAt = DateTimeOffset.UtcNow });
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("off")]
    public async Task The_lights_work_whatever_the_plan_limits_come_from(string source)
    {
        WriteRecord("s1", "needs_you", TimeSpan.FromSeconds(30), new { type = "question" });
        using var services = AppServices.Create(new AppSettings { ClaudeSource = source },
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false,
            _ => (new FakeEndpoint(), []), cacheDirectory: _dir.Combine("cache"));

        var summary = await services.Feed.GetSummaryAsync();
        Assert.Equal("s1", Assert.Single(summary.Activities).SessionId);
        Assert.True(summary.HasPlugin);
        Assert.Equal(AlertKind.NeedsYou, Assert.Single(summary.Alerts).Kind);
        // The plan limits still come only from the chosen source.
        Assert.NotEqual(ClaudeFeedUsageSource.Id, summary.Usage.SourceId);
    }

    [Fact]
    public async Task The_cards_carry_the_status_and_the_waiting_one_comes_first_unless_the_lights_are_off()
    {
        var now = DateTimeOffset.Now;
        var busy = Guid.NewGuid().ToString();
        var waits = Guid.NewGuid().ToString();
        foreach (var (id, minutesAgo) in new[] { (busy, 1), (waits, 6) })
            Transcript.Write(_dir.Combine("projects", "acme", id + ".jsonl"),
                Transcript.User(id, now.AddMinutes(-minutesAgo - 1)),
                Transcript.Assistant(id, "msg_" + id, now.AddMinutes(-minutesAgo), input: 1000, output: 2000, model: "claude-sonnet-4-5"));
        WriteRecord(waits, "needs_you", TimeSpan.FromSeconds(30), new { type = "question" });
        WriteRecord(busy, "working", TimeSpan.FromMinutes(1));
        using var services = Services(new AppSettings { ClaudeSource = "feed" });
        await services.Feed.GetSummaryAsync();

        // Two days: just after midnight the sessions are yesterday's.
        var cards = await services.Feed.GetCardsAsync(2);
        Assert.Equal([waits, busy], cards.Select(c => c.SessionId));
        Assert.Equal("Needs you: a question", cards[0].StatusText);
        Assert.Equal("Working", cards[1].StatusText);

        using var off = Services(new AppSettings { ClaudeSource = "feed", ShowSessionLights = false });
        await off.Feed.GetSummaryAsync();
        var plain = await off.Feed.GetCardsAsync(2);
        Assert.Equal([busy, waits], plain.Select(c => c.SessionId)); // most recently active first, as before
        Assert.All(plain, c => Assert.Null(c.StatusText));
    }
}
