using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>
/// The polling contract of the usage-endpoint source: when a request may go
/// out, and what a 429, a 401 or an expired sign-in does to that. Time is a
/// fake clock and the endpoint a fake handler, so every case is exact.
/// </summary>
public class ClaudeOAuthUsageSourceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeUsageEndpoint _endpoint = new();
    private DateTimeOffset _now = DateTimeOffset.UtcNow;
    private DateTime? _activity;
    private int _activityTicks;

    private string CredentialsPath => _dir.Combine(".credentials.json");
    private string CacheDirectory => _dir.Combine("cache");

    public ClaudeOAuthUsageSourceTests() => WriteCredentials();

    public void Dispose() => _dir.Dispose();

    private ClaudeOAuthUsageSource NewSource() => new(
        new HttpClient(_endpoint), () => new FileCredentialStore(CredentialsPath), CredentialsPath,
        CacheDirectory, () => _now, () => _activity);

    /// <summary>A transcript was written just now: the source polls at its
    /// active pace. Each call is a new write, as when Claude Code is working.</summary>
    private void TranscriptWritten() => _activity = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(++_activityTicks);

    private void Advance(TimeSpan by) => _now += by;

    private void WriteCredentials(DateTimeOffset? refreshTokenExpiresAt = null, string accessToken = "access-1",
        DateTimeOffset? expiresAt = null)
    {
        // By default the access token stays valid for a day of real time.
        var oauth = new Dictionary<string, object>
        {
            ["accessToken"] = accessToken,
            ["refreshToken"] = "refresh-1",
            ["expiresAt"] = (expiresAt ?? DateTimeOffset.UtcNow.AddDays(1)).ToUnixTimeMilliseconds(),
            ["subscriptionType"] = "max"
        };
        if (refreshTokenExpiresAt is { } rt)
            oauth["refreshTokenExpiresAt"] = rt.ToUnixTimeMilliseconds();
        File.WriteAllText(CredentialsPath, JsonSerializer.Serialize(new { claudeAiOauth = oauth }));
    }

    private string PlanBody(DateTimeOffset? fiveHourResetsAt = null) => JsonSerializer.Serialize(new
    {
        five_hour = new { utilization = 43.0, resets_at = (fiveHourResetsAt ?? _now.AddHours(3)).ToString("O") },
        seven_day = new { utilization = 14.0, resets_at = _now.AddDays(4).ToString("O") }
    });

    [Fact]
    public async Task Success_returns_the_reading_from_one_request_with_the_stored_token()
    {
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        var source = NewSource();

        var snapshot = await source.GetUsageAsync();

        Assert.Equal(UsageMode.PlanLimits, snapshot.Mode);
        Assert.Equal(43.0, snapshot.FiveHourUtilizationPercent);
        Assert.Equal(14.0, snapshot.WeeklyUtilizationPercent);
        Assert.Equal("max", snapshot.SubscriptionType);
        Assert.Equal(_now, snapshot.FetchedAt);
        Assert.False(snapshot.IsStale);
        var request = Assert.Single(_endpoint.Requests);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.Uri);
        Assert.Equal("Bearer access-1", request.Authorization);
        Assert.Equal("oauth-2025-04-20", request.Beta);

        // Asked again at once: served from memory, no second request.
        Assert.Equal(43.0, (await source.GetUsageAsync()).FiveHourUtilizationPercent);
        Assert.Single(_endpoint.Requests);
    }

    [Fact]
    public async Task Polls_every_30_seconds_while_Claude_Code_works_and_every_10_minutes_when_quiet()
    {
        var source = NewSource();
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        Advance(TimeSpan.FromSeconds(29));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        Advance(TimeSpan.FromSeconds(1));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);

        // No transcript writes from here on: after two quiet minutes the
        // source waits ten minutes between requests.
        Advance(TimeSpan.FromMinutes(3));
        await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
        Advance(TimeSpan.FromMinutes(6.9));
        await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
        Advance(TimeSpan.FromMinutes(0.1));
        await source.GetUsageAsync();
        Assert.Equal(3, _endpoint.Requests.Count);
    }

    [Fact]
    public async Task Rate_limit_without_Retry_After_waits_two_minutes_then_doubles_and_halves_the_floor()
    {
        _endpoint.Enqueue(HttpStatusCode.TooManyRequests);
        var source = NewSource();
        TranscriptWritten();

        var limited = await source.GetUsageAsync();
        Assert.True(limited.IsStale);
        Assert.Equal(TimeSpan.FromSeconds(60), source.LearnedFloor);
        Assert.Equal(_now + TimeSpan.FromMinutes(2), source.NextAllowedFetchAt);

        Advance(TimeSpan.FromSeconds(119));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        Advance(TimeSpan.FromSeconds(1));
        TranscriptWritten();
        var snapshot = await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
        Assert.False(snapshot.IsStale);
        Assert.Equal(TimeSpan.FromSeconds(30), source.LearnedFloor);
    }

    [Fact]
    public async Task Consecutive_rate_limits_double_the_wait_and_the_floor()
    {
        _endpoint.Enqueue(HttpStatusCode.TooManyRequests);
        _endpoint.Enqueue(HttpStatusCode.TooManyRequests);
        var source = NewSource();
        TranscriptWritten();
        await source.GetUsageAsync();

        Advance(TimeSpan.FromMinutes(2));
        TranscriptWritten();
        await source.GetUsageAsync();

        Assert.Equal(2, _endpoint.Requests.Count);
        Assert.Equal(TimeSpan.FromSeconds(120), source.LearnedFloor);
        Assert.Equal(_now + TimeSpan.FromMinutes(4), source.NextAllowedFetchAt);
    }

    [Fact]
    public async Task Retry_After_from_the_server_is_respected()
    {
        _endpoint.Enqueue(HttpStatusCode.TooManyRequests, retryAfter: TimeSpan.FromMinutes(10));
        var source = NewSource();
        TranscriptWritten();
        await source.GetUsageAsync();

        Advance(TimeSpan.FromMinutes(9.9));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        Advance(TimeSpan.FromMinutes(0.1));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    [Fact]
    public async Task Unauthorized_asks_for_sign_in_and_retries_once_an_hour()
    {
        _endpoint.Enqueue(HttpStatusCode.Unauthorized, """{"error":"expired"}""");
        var source = NewSource();
        TranscriptWritten();

        var snapshot = await source.GetUsageAsync();
        Assert.True(snapshot.RequiresReauth);
        Assert.True(snapshot.IsStale);

        Advance(TimeSpan.FromMinutes(59));
        TranscriptWritten();
        Assert.True((await source.GetUsageAsync()).RequiresReauth);
        Assert.Single(_endpoint.Requests);

        Advance(TimeSpan.FromMinutes(1));
        TranscriptWritten();
        Assert.False((await source.GetUsageAsync()).RequiresReauth);
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    [Fact]
    public async Task A_new_sign_in_after_a_401_is_tried_at_once()
    {
        _endpoint.Enqueue(HttpStatusCode.Unauthorized);
        var source = NewSource();
        await source.GetUsageAsync();

        WriteCredentials();
        File.SetLastWriteTimeUtc(CredentialsPath, DateTime.UtcNow.AddMinutes(5));
        Advance(TimeSpan.FromSeconds(1));

        Assert.False((await source.GetUsageAsync()).RequiresReauth);
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    [Fact]
    public async Task Expired_refresh_token_makes_no_requests_at_all()
    {
        WriteCredentials(refreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(-1));
        var source = NewSource();

        Assert.True((await source.GetUsageAsync()).RequiresReauth);
        Advance(TimeSpan.FromHours(2));
        TranscriptWritten();
        Assert.True((await source.GetUsageAsync()).RequiresReauth);

        Assert.Empty(_endpoint.Requests);
    }

    // --- The sign-in is only read: an expired token waits for Claude Code, nothing is renewed or written ---

    [Fact]
    public async Task An_expired_token_is_not_sent_and_the_last_numbers_wait_for_Claude_Code()
    {
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        var source = NewSource();
        TranscriptWritten();
        Assert.Equal(43.0, (await source.GetUsageAsync()).FiveHourUtilizationPercent);

        // The token expires; Claude Code hasn't run since.
        WriteCredentials(expiresAt: _now.AddSeconds(30));
        var stamp = File.GetLastWriteTimeUtc(CredentialsPath);
        Advance(TimeSpan.FromMinutes(1));
        TranscriptWritten();
        var waiting = await source.GetUsageAsync();

        Assert.Single(_endpoint.Requests);
        Assert.True(waiting.IsStale);
        Assert.Equal(43.0, waiting.FiveHourUtilizationPercent);
        Assert.Equal(ClaudeOAuthUsageSource.TokenExpiredDetail, waiting.StatusDetail);
        Assert.False(waiting.RequiresReauth);

        // Looked at again after five minutes (a local read): still expired, still nothing asked.
        Advance(ClaudeOAuthUsageSource.TokenWaitRecheck);
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        // Claude Code ran and wrote a new token: used at once, the wait is over.
        WriteCredentials(accessToken: "access-2");
        File.SetLastWriteTimeUtc(CredentialsPath, stamp.AddMinutes(10));
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        Advance(TimeSpan.FromSeconds(1));
        var fresh = await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
        Assert.Equal("Bearer access-2", _endpoint.Requests[^1].Authorization);
        Assert.False(fresh.IsStale);
        Assert.Null(fresh.StatusDetail);
    }

    [Fact]
    public async Task A_refused_token_whose_sign_in_lasts_is_not_sent_again_until_it_changes()
    {
        WriteCredentials(refreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(30));
        _endpoint.Enqueue(HttpStatusCode.Unauthorized, """{"error":"expired"}""");
        var source = NewSource();
        TranscriptWritten();

        var waiting = await source.GetUsageAsync();
        Assert.False(waiting.RequiresReauth);
        Assert.Equal(ClaudeOAuthUsageSource.TokenExpiredDetail, waiting.StatusDetail);

        Advance(TimeSpan.FromHours(3));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        WriteCredentials(refreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(30), accessToken: "access-2");
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        Advance(ClaudeOAuthUsageSource.TokenWaitRecheck);
        TranscriptWritten();
        Assert.Equal(43.0, (await source.GetUsageAsync()).FiveHourUtilizationPercent);
        Assert.Equal("Bearer access-2", _endpoint.Requests[^1].Authorization);
    }

    [Fact]
    public async Task The_credentials_are_never_written_and_only_the_usage_service_is_asked()
    {
        WriteCredentials(refreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(30), expiresAt: DateTimeOffset.UtcNow.AddMinutes(30));
        var before = File.ReadAllBytes(CredentialsPath);
        var written = File.GetLastWriteTimeUtc(CredentialsPath);
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        _endpoint.Enqueue(HttpStatusCode.Unauthorized);
        var source = NewSource();

        // A good fetch, then one past the token's expiry, then a refusal of a "good" one.
        TranscriptWritten();
        await source.GetUsageAsync();
        Advance(TimeSpan.FromHours(1));
        TranscriptWritten();
        await source.GetUsageAsync();
        WriteCredentials(refreshTokenExpiresAt: DateTimeOffset.UtcNow.AddDays(30), expiresAt: _now.AddHours(5));
        before = File.ReadAllBytes(CredentialsPath);
        written = File.GetLastWriteTimeUtc(CredentialsPath);
        Advance(ClaudeOAuthUsageSource.TokenWaitRecheck);
        TranscriptWritten();
        await source.GetUsageAsync();

        Assert.Equal(before, File.ReadAllBytes(CredentialsPath));
        Assert.Equal(written, File.GetLastWriteTimeUtc(CredentialsPath));
        Assert.Empty(Directory.GetFiles(_dir.Path, "*.tmp"));
        Assert.All(_endpoint.Requests, r => Assert.Equal("https://api.anthropic.com/api/oauth/usage", r.Uri));
    }

    [Fact]
    public async Task The_sign_in_is_read_once_per_fetch()
    {
        var reads = 0;
        var store = new CountingStore(() => File.ReadAllText(CredentialsPath), () => reads++);
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody());
        var source = new ClaudeOAuthUsageSource(new HttpClient(_endpoint), () => store, CredentialsPath, CacheDirectory,
            () => _now, () => _activity);

        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Equal(1, reads);
        Advance(TimeSpan.FromSeconds(30));
        TranscriptWritten();
        await source.GetUsageAsync();
        Assert.Equal(2, reads);
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    private sealed class CountingStore(Func<string?> read, Action counted) : ICredentialStore
    {
        public string Description => "test store";

        public string? Read()
        {
            counted();
            return read();
        }
    }

    [Fact]
    public async Task Cooldown_and_learned_floor_survive_a_restart()
    {
        _endpoint.Enqueue(HttpStatusCode.TooManyRequests);
        TranscriptWritten();
        await NewSource().GetUsageAsync();

        Advance(TimeSpan.FromMinutes(1));
        var restarted = NewSource();
        TranscriptWritten();
        var snapshot = await restarted.GetUsageAsync();

        Assert.Single(_endpoint.Requests);
        Assert.True(snapshot.IsStale);
        Assert.Equal(TimeSpan.FromSeconds(60), restarted.LearnedFloor);

        Advance(TimeSpan.FromMinutes(1));
        TranscriptWritten();
        await restarted.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    [Fact]
    public void A_cooldown_on_disk_is_capped_at_30_minutes()
    {
        Directory.CreateDirectory(CacheDirectory);
        File.WriteAllText(Path.Combine(CacheDirectory, "claude-usage-throttle.json"),
            $"{_now.AddDays(3).ToUnixTimeSeconds()}\n99999");

        var source = NewSource();

        Assert.Equal(_now + TimeSpan.FromMinutes(30), source.NextAllowedFetchAt);
        Assert.Equal(TimeSpan.FromMinutes(30), source.LearnedFloor);
    }

    [Fact]
    public async Task A_passed_reset_time_triggers_a_fetch_before_the_idle_interval()
    {
        var resetsAt = _now.AddMinutes(5);
        _endpoint.Enqueue(HttpStatusCode.OK, PlanBody(fiveHourResetsAt: resetsAt));
        var source = NewSource();
        await source.GetUsageAsync();

        // Quiet the whole time, so only the reset can make a request due.
        Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(19));
        await source.GetUsageAsync();
        Assert.Single(_endpoint.Requests);

        Advance(TimeSpan.FromSeconds(1));
        await source.GetUsageAsync();
        Assert.Equal(2, _endpoint.Requests.Count);
    }

    [Fact]
    public async Task A_network_error_keeps_the_last_reading_marked_stale()
    {
        var source = NewSource();
        TranscriptWritten();
        await source.GetUsageAsync();

        _endpoint.FailNextWith(new HttpRequestException("offline"));
        Advance(TimeSpan.FromSeconds(30));
        TranscriptWritten();
        var snapshot = await source.GetUsageAsync();

        Assert.Equal(2, _endpoint.Requests.Count);
        Assert.True(snapshot.IsStale);
        Assert.Equal(43.0, snapshot.FiveHourUtilizationPercent);
    }

    [Fact]
    public async Task The_last_reading_is_kept_on_disk_for_the_next_start()
    {
        var first = NewSource();
        await first.GetUsageAsync();

        _endpoint.Enqueue(HttpStatusCode.TooManyRequests);
        var restarted = NewSource();
        var snapshot = await restarted.GetUsageAsync();

        Assert.True(snapshot.IsStale);
        Assert.Equal(43.0, snapshot.FiveHourUtilizationPercent);
    }

    /// <summary>Answers like the usage endpoint; any other host (such as the
    /// token refresh) fails the test.</summary>
    internal sealed class FakeUsageEndpoint : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, TimeSpan? RetryAfter)> _responses = new();
        private Exception? _failNext;

        public List<(string Uri, string? Authorization, string? Beta)> Requests { get; } = new();

        public string DefaultBody { get; set; } =
            """{"five_hour":{"utilization":43.0,"resets_at":null},"seven_day":{"utilization":14.0,"resets_at":null}}""";

        public void Enqueue(HttpStatusCode status, string body = "{}", TimeSpan? retryAfter = null) =>
            _responses.Enqueue((status, body, retryAfter));

        public void FailNextWith(Exception ex) => _failNext = ex;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal("api.anthropic.com", request.RequestUri!.Host);
            Requests.Add((request.RequestUri.ToString(), request.Headers.Authorization?.ToString(),
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? beta.Single() : null));

            if (_failNext is { } ex)
            {
                _failNext = null;
                throw ex;
            }

            var (status, body, retryAfter) = _responses.Count > 0 ? _responses.Dequeue() : (HttpStatusCode.OK, DefaultBody, null);
            var response = new HttpResponseMessage(status) { Content = new StringContent(body) };
            if (retryAfter is { } wait)
                response.Headers.RetryAfter = new RetryConditionHeaderValue(wait);
            return Task.FromResult(response);
        }
    }
}
