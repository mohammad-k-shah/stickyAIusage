using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Polls Anthropic's undocumented usage endpoint (GET /api/oauth/usage), the
/// one Claude Code itself reads. Read-only: fetching
/// usage never spends against the account's cap.
///
/// On monthly-cap accounts the top-level `spend` block is the most direct
/// source for monthly $ used/limit/percent; `extra_usage.*` mirrors it and is
/// kept as a fallback. `five_hour` / `seven_day*` buckets are null on those
/// accounts and are mapped through as null rather than assumed absent, so an
/// account that does get rate-limit windows picks them up automatically.
///
/// 1. Polling is adaptive (ComputeAdaptiveInterval), not a fixed schedule -
///    fast (30s) while Claude Code is actively being used, slow (10min)
///    "heartbeat" otherwise, so the app doesn't keep calling this endpoint
///    around the clock for a number that only changes when the user is
///    actually spending. The heartbeat is intentionally never zero, since
///    a monthly cap also covers Chat/Cowork/Claude-in-Chrome, which don't
///    write to the local session logs this app can otherwise see.
/// 2. The access token is refreshed proactively before it expires
///    (ClaudeTokenRefresher), because Claude Desktop doesn't reliably keep
///    the stored token fresh. The 401 handling remains as a fallback if a
///    refresh attempt itself fails.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider, IDisposable
{
    private const string UsageApiUrl = "https://api.anthropic.com/api/oauth/usage";
    private const string AnthropicBetaHeader = "oauth-2025-04-20";

    // Adaptive polling: polling on a fixed schedule while Claude Code is idle
    // wastes calls against whatever quota this undocumented endpoint
    // enforces, for a number that only changes when the user is actually
    // spending. Poll fast while Claude Code is active, fall back to a slow
    // "heartbeat" while it's not. The heartbeat never fully stops because a
    // monthly cap also covers Chat/Cowork/Claude-in-Chrome, none of which
    // write to the local session logs this app can see - a purely "stop
    // polling when the local log is idle" design would go stale exactly
    // while spend happens elsewhere.
    private static readonly TimeSpan ActiveInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan IdleInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan QuietThreshold = TimeSpan.FromMinutes(2);

    // After a window's reset time, wait this long before asking for the new
    // reading, so the server has rolled over.
    private static readonly TimeSpan ResetGrace = TimeSpan.FromSeconds(20);

    // Fallback backoff on a 429 with no (or an unparsable) Retry-After header.
    // Without a real backoff, a failing fetch retries on every tick and keeps
    // the endpoint rate-limiting the app for hours.
    private static readonly TimeSpan DefaultRateLimitBackoff = TimeSpan.FromMinutes(2);

    // A flat 2-minute retry keeps re-triggering the limit and leaves the
    // reading hours stale: each further 429 in a row
    // doubles the wait, up to this cap, and any success starts over. The same
    // cap bounds a server-sent Retry-After and a persisted cooldown, so a bad
    // value can never lock the widget out for days.
    private static readonly TimeSpan MaxRateLimitBackoff = TimeSpan.FromMinutes(30);
    private int _consecutiveRateLimits;

    /// <summary>
    /// The smallest gap between polls that hasn't been rate limited, learned at
    /// runtime: the endpoint is undocumented and its allowance differs per
    /// account, so ActiveInterval alone is a guess. Without this, backing off
    /// and then returning straight to ActiveInterval means the very next poll
    /// is rate limited again - observed as success, 429 thirty seconds later,
    /// repeating all day while the reading sat minutes stale.
    ///
    /// Doubles on each 429 and halves on each success, so polling settles just
    /// under whatever the endpoint currently allows and speeds back up once it
    /// relents. Persisted with the cooldown so a restart doesn't relearn it.
    /// </summary>
    private TimeSpan _learnedFloor;

    internal static TimeSpan RaiseFloor(TimeSpan floor) =>
        Min(MaxRateLimitBackoff, Max(ActiveInterval + ActiveInterval, floor + floor));

    internal static TimeSpan LowerFloor(TimeSpan floor) =>
        floor <= ActiveInterval ? TimeSpan.Zero : TimeSpan.FromTicks(floor.Ticks / 2);

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;
    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;

    // The credentials file's write time when a 401 last left us needing a new
    // sign-in (a dead refresh token can't be fixed here). When the file changes -
    // the user signed in again - the next poll fetches at once instead of
    // waiting out the backoff. Not available on macOS (Keychain): plain backoff.
    private DateTime? _reauthStamp;

    private static DateTime? CredentialsStamp()
    {
        try
        {
            return File.Exists(ClaudeCredentialsReader.CredentialsPath) ? File.GetLastWriteTimeUtc(ClaudeCredentialsReader.CredentialsPath) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static TimeSpan RateLimitBackoff(TimeSpan? retryAfter, int consecutive)
    {
        var doubled = TimeSpan.FromTicks(DefaultRateLimitBackoff.Ticks << Math.Min(Math.Max(consecutive - 1, 0), 4));
        var wait = retryAfter is { } ra && ra > doubled ? ra : doubled;
        return wait > MaxRateLimitBackoff ? MaxRateLimitBackoff : wait;
    }

    private readonly HttpClient _httpClient;
    private readonly string _cacheFilePath;
    private readonly string _throttleStatePath;
    private readonly SemaphoreSlim _fetchLock = new(1, 1);

    private UsageSnapshot? _lastSnapshot;

    // Adaptive-interval state - not persisted across restarts;
    // an app restart is rare enough that re-detecting "active vs idle" fresh
    // is fine, and it's independent of the forced backoff below.
    private DateTime? _lastSeenFileActivityUtc;
    private DateTimeOffset _lastActivityChangeAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastFetchAttempt = DateTimeOffset.MinValue;

    /// <summary>
    /// A hard floor on the next allowed fetch, set *only* by a 429 or 401
    /// response - it always wins over the adaptive interval above, since a
    /// server-mandated cooldown must be respected regardless of how actively
    /// Claude Code is being used. Set unconditionally on every such failure
    /// (never left at "no floor") so a failing fetch still backs off, even
    /// before the first successful fetch. Persisted to disk (see
    /// _throttleStatePath) so restarting the app during an active
    /// server-imposed cooldown doesn't throw it away and immediately
    /// re-attempt.
    /// </summary>
    private DateTimeOffset _nextAllowedFetchAt = DateTimeOffset.MinValue;

    public string ProviderId => "claude";
    public string DisplayName => "Claude";

    public ClaudeUsageProvider(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };

        var cacheDir = AppPaths.CacheDirectory;
        Directory.CreateDirectory(cacheDir);
        _cacheFilePath = Path.Combine(cacheDir, "claude-usage.json");
        _throttleStatePath = Path.Combine(cacheDir, "claude-usage-throttle.json");

        _nextAllowedFetchAt = TryLoadThrottleState() ?? DateTimeOffset.MinValue;
    }

    public async Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default)
    {
        // Serve from memory/disk cache if we polled recently - this is what
        // guarantees the poll-interval contract even if the UI's timer and a
        // manual refresh race each other.
        _lastSnapshot ??= TryLoadDiskCache();

        // Always recomputed, regardless of whether we're about to fetch, so
        // "Claude Code just became active" is noticed within one tick (~15s)
        // even in the middle of a long idle wait - not only when the next
        // scheduled fetch finally comes due.
        var adaptiveInterval = ComputeAdaptiveInterval();
        var now = DateTimeOffset.UtcNow;

        if (!IsFetchDue(now, adaptiveInterval))
            return _lastSnapshot ?? UnavailableSnapshot();

        await _fetchLock.WaitAsync(cancellationToken);
        try
        {
            // Re-check after acquiring the lock in case another call just fetched.
            now = DateTimeOffset.UtcNow;
            if (!IsFetchDue(now, adaptiveInterval))
                return _lastSnapshot ?? UnavailableSnapshot();

            _lastFetchAttempt = now;

            var store = ClaudeCredentialsReader.ForCurrentPlatform();
            var token = await ClaudeTokenRefresher.GetValidAccessTokenAsync(_httpClient, store, cancellationToken);
            if (token is null)
            {
                DiagnosticLog.WriteError("ClaudeUsageProvider.GetUsageAsync",
                    new InvalidOperationException(
                        $"No access token found in the {store.Description} (missing, unreadable, or no claudeAiOauth.accessToken)."));
                return _lastSnapshot ?? UnavailableSnapshot();
            }

            try
            {
                var snapshot = await FetchFromApiAsync(token, ClaudeTokenRefresher.ReadSubscriptionType(store), cancellationToken);
                if (snapshot.Mode != _lastSnapshot?.Mode)
                    DiagnosticLog.WriteInfo($"Usage mode: {snapshot.Mode} (subscription: {snapshot.SubscriptionType ?? "unknown"}).");
                _lastSnapshot = snapshot;
                _consecutiveRateLimits = 0;
                _reauthStamp = null;
                SetLearnedFloor(LowerFloor(_learnedFloor));
                TrySaveDiskCache(snapshot);
                return snapshot;
            }
            catch (UsageApiRateLimitedException rlEx)
            {
                // Back off for (at least) whatever the server told us via
                // Retry-After - this is a hard floor that overrides the
                // adaptive interval above regardless of activity state, so a
                // failing fetch never hammers the endpoint into sustained 429s.
                var backoff = RateLimitBackoff(rlEx.RetryAfter, ++_consecutiveRateLimits);
                SetLearnedFloor(RaiseFloor(_learnedFloor));
                SetNextAllowedFetchAt(DateTimeOffset.UtcNow + backoff);
                DiagnosticLog.WriteError("ClaudeUsageProvider.GetUsageAsync",
                    new Exception($"Rate limited; backing off {backoff.TotalSeconds:0}s, "
                        + $"polling no faster than every {_learnedFloor.TotalSeconds:0}s.", rlEx));
                // Remember this result, not just return it - otherwise every
                // subsequent call during the backoff window (there's no new
                // fetch attempt until it elapses) falls through to a bare
                // UnavailableSnapshot() below instead of keeping this state,
                // which is exactly what made a distinct failure message
                // (e.g. RequiresReauth) disappear after its first showing -
                // see the matching fix in the 401 branch below.
                _lastSnapshot = _lastSnapshot is not null ? _lastSnapshot with { IsStale = true } : UnavailableSnapshot();
                TrySaveDiskCache(_lastSnapshot);
                return _lastSnapshot;
            }
            catch (UsageApiUnauthorizedException authEx)
            {
                // ClaudeTokenRefresher already tries to proactively refresh
                // before the token expires - reaching a 401 here means that
                // either failed (e.g. the refresh token itself is also
                // expired) or the response's stated expiry didn't match
                // reality. The stored token can go stale even during active
                // Claude Desktop use, so this is a real fallback path. Back off
                // well past a single failed refresh attempt before retrying.
                SetNextAllowedFetchAt(DateTimeOffset.UtcNow + TimeSpan.FromMinutes(10));
                _reauthStamp = CredentialsStamp();
                DiagnosticLog.WriteError("ClaudeUsageProvider.GetUsageAsync", authEx);
                _lastSnapshot = (_lastSnapshot ?? UnavailableSnapshot()) with { IsStale = true, RequiresReauth = true };
                TrySaveDiskCache(_lastSnapshot);
                return _lastSnapshot;
            }
            catch (Exception ex)
            {
                // Network error, or the endpoint's shape changed under us -
                // keep showing the last good value rather
                // than blank/crash. Logged so a silent fetch
                // failure (e.g. corporate proxy/firewall blocking the call)
                // can actually be diagnosed instead of just showing nothing.
                DiagnosticLog.WriteError("ClaudeUsageProvider.GetUsageAsync", ex);
                _lastSnapshot = _lastSnapshot is not null ? _lastSnapshot with { IsStale = true } : UnavailableSnapshot();
                return _lastSnapshot;
            }
        }
        finally
        {
            _fetchLock.Release();
        }
    }

    /// <summary>
    /// A fetch is due when the adaptive interval has passed, or when a usage
    /// window's reset time (5-hour or weekly) has just passed since the last
    /// fetch - the reset times come with each reading and the countdown runs
    /// locally in between, so the endpoint is only asked again once there's
    /// something new to learn. A server-imposed cooldown (429/401) always wins.
    /// </summary>
    private bool IsFetchDue(DateTimeOffset now, TimeSpan adaptiveInterval)
    {
        // Signed in again since the 401 (Claude rewrote the credentials file): try now.
        if (_reauthStamp is { } stamp && CredentialsStamp() != stamp)
        {
            _reauthStamp = null;
            _nextAllowedFetchAt = DateTimeOffset.MinValue;
            return true;
        }
        if (now < _nextAllowedFetchAt)
            return false;
        if (now - _lastFetchAttempt >= adaptiveInterval)
            return true;
        return ResetPassedSinceLastFetch(_lastSnapshot, _lastFetchAttempt, now);
    }

    /// <summary>True when a window reset (plus a short grace period for the
    /// server to roll over) falls between the last fetch attempt and now.</summary>
    internal static bool ResetPassedSinceLastFetch(UsageSnapshot? snapshot, DateTimeOffset lastFetchAttempt, DateTimeOffset now)
    {
        if (snapshot is null)
            return false;
        foreach (var reset in new[] { snapshot.FiveHourResetsAt, snapshot.WeeklyResetsAt })
        {
            if (reset is { } r && r + ResetGrace <= now && lastFetchAttempt < r + ResetGrace)
                return true;
        }
        return false;
    }

    /// <summary>Decides how often to poll based on whether any Claude Code
    /// session transcript has changed recently (a cheap, no-content-parsing
    /// check - see ClaudeSessionLogReader.GetMostRecentActivityUtc). Called
    /// on every GetUsageAsync invocation, not just when a fetch is imminent,
    /// so a transition from idle back to active is picked up within one poll
    /// tick rather than only once a long idle wait finally elapses.</summary>
    private TimeSpan ComputeAdaptiveInterval()
    {
        var currentActivity = ClaudeSessionLogReader.GetMostRecentActivityUtc();
        if (currentActivity != _lastSeenFileActivityUtc)
        {
            _lastSeenFileActivityUtc = currentActivity;
            _lastActivityChangeAt = DateTimeOffset.UtcNow;
        }

        var quietFor = DateTimeOffset.UtcNow - _lastActivityChangeAt;
        // Never poll faster than the endpoint has shown it will tolerate.
        return Max(quietFor < QuietThreshold ? ActiveInterval : IdleInterval, _learnedFloor);
    }

    private static UsageSnapshot UnavailableSnapshot() => new()
    {
        HasMonthlySpend = false,
        IsStale = true,
        SubscriptionType = ClaudeTokenRefresher.ReadSubscriptionType(ClaudeCredentialsReader.ForCurrentPlatform())
    };

    private void SetNextAllowedFetchAt(DateTimeOffset value)
    {
        _nextAllowedFetchAt = value;
        SaveThrottleState();
    }

    private void SetLearnedFloor(TimeSpan value)
    {
        if (value == _learnedFloor) return;
        _learnedFloor = value;
        SaveThrottleState();
    }

    /// <summary>The cooldown on the first line and the learned floor on the
    /// second, so a file written by an older build (one line) still loads.</summary>
    private void SaveThrottleState()
    {
        try
        {
            File.WriteAllText(_throttleStatePath,
                $"{_nextAllowedFetchAt.ToUnixTimeSeconds()}{Environment.NewLine}{(long)_learnedFloor.TotalSeconds}");
        }
        catch
        {
            // Best-effort; worst case a restart re-attempts one call early.
        }
    }

    private DateTimeOffset? TryLoadThrottleState()
    {
        try
        {
            if (!File.Exists(_throttleStatePath)) return null;
            var lines = File.ReadAllLines(_throttleStatePath);
            if (lines.Length == 0 || !long.TryParse(lines[0].Trim(), out var seconds))
                return null;

            if (lines.Length > 1 && long.TryParse(lines[1].Trim(), out var floorSeconds) && floorSeconds > 0)
                _learnedFloor = Min(MaxRateLimitBackoff, TimeSpan.FromSeconds(floorSeconds));

            var until = DateTimeOffset.FromUnixTimeSeconds(seconds);
            var latest = DateTimeOffset.UtcNow + MaxRateLimitBackoff;
            return until > latest ? latest : until;
        }
        catch
        {
            return null;
        }
    }

    private async Task<UsageSnapshot> FetchFromApiAsync(string token, string? subscriptionType, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, UsageApiUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Add("anthropic-beta", AnthropicBetaHeader);

        using var response = await _httpClient.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
        {
            TimeSpan? retryAfter = response.Headers.RetryAfter?.Delta
                ?? (response.Headers.RetryAfter?.Date is { } retryDate
                    ? retryDate - DateTimeOffset.UtcNow
                    : null);
            throw new UsageApiRateLimitedException(retryAfter);
        }

        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
        {
            var bodyPreview = json.Length > 500 ? json[..500] : json;
            throw new UsageApiUnauthorizedException(bodyPreview);
        }

        if (!response.IsSuccessStatusCode)
        {
            // Include status + a truncated body so a proxy/auth failure on a
            // corporate network is diagnosable from the log file, instead of
            // just "HttpRequestException" with no detail.
            var bodyPreview = json.Length > 500 ? json[..500] : json;
            throw new HttpRequestException(
                $"Usage API returned {(int)response.StatusCode} {response.StatusCode}: {bodyPreview}");
        }
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        return MapToSnapshot(root, subscriptionType);
    }

    /// <summary>
    /// Maps the raw API shape to our normalized model and decides the display
    /// mode from the response's shape:
    /// - A top-level <c>spend</c> block with a limit is a monthly spend cap
    ///   (Enterprise and similar plans).
    /// - Otherwise, rolling windows - flat <c>five_hour</c>/<c>seven_day</c>
    ///   buckets or, on newer accounts, <c>limits[]</c> entries of kind
    ///   <c>session</c>/<c>weekly_all</c> - mean a Pro/Max
    ///   style plan. Its <c>extra_usage</c> block is the optional pay-as-you-go
    ///   credit pool, not a monthly cap.
    /// - Only when neither is present is <c>extra_usage</c> read as the
    ///   monthly cap (on Enterprise it mirrors <c>spend</c>).
    /// Every lookup is defensive because this endpoint is undocumented.
    /// </summary>
    internal static UsageSnapshot MapToSnapshot(JsonElement root, string? subscriptionType = null)
    {
        decimal? spendUsed = null, spendLimit = null, creditBalance = null;
        double? spendPercent = null;
        string? severity = null, currency = null;

        if (root.TryGetProperty("spend", out var spend) && spend.ValueKind == JsonValueKind.Object)
        {
            spendUsed = ReadMinorAmountAsUsd(spend, "used");
            spendLimit = ReadMinorAmountAsUsd(spend, "limit");
            spendPercent = ReadNullableDouble(spend, "percent");
            severity = ReadNullableString(spend, "severity");
            creditBalance = ReadMinorAmountAsUsd(spend, "balance");
            currency ??= spend.TryGetProperty("used", out var used) && used.ValueKind == JsonValueKind.Object
                ? ReadNullableString(used, "currency")
                : null;
        }

        var limits = root.TryGetProperty("limits", out var limitsEl) && limitsEl.ValueKind == JsonValueKind.Array
            ? limitsEl.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.Object).ToList()
            : [];
        var sessionLimit = limits.FirstOrDefault(l => ReadNullableString(l, "kind") == "session");
        var weeklyLimit = limits.FirstOrDefault(l => ReadNullableString(l, "kind") == "weekly_all");

        // Flat buckets first, limits[] as a per-field fallback.
        var fiveHour = ReadBucketUtilization(root, "five_hour") ?? ReadLimitPercent(sessionLimit);
        var fiveHourResets = ReadBucketResetsAt(root, "five_hour") ?? ReadResetsAt(sessionLimit);
        var weekly = ReadBucketUtilization(root, "seven_day") ?? ReadLimitPercent(weeklyLimit);
        var weeklyResets = ReadBucketResetsAt(root, "seven_day") ?? ReadResetsAt(weeklyLimit);
        var hasWindows = fiveHour is not null || weekly is not null;

        bool? extraEnabled = null;
        decimal? extraLimit = null, extraUsed = null;
        if (root.TryGetProperty("extra_usage", out var extra) && extra.ValueKind == JsonValueKind.Object)
        {
            extraEnabled = extra.TryGetProperty("is_enabled", out var en) && en.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? en.GetBoolean()
                : null;
            // Both figures come back in cents.
            extraLimit = ReadNullableDouble(extra, "monthly_limit") is { } l ? (decimal)(l / 100.0) : null;
            extraUsed = ReadNullableDouble(extra, "used_credits") is { } u ? (decimal)(u / 100.0) : null;
            currency = ReadNullableString(extra, "currency") ?? currency;

            if (spendUsed is null && !hasWindows)
            {
                spendUsed = extraUsed;
                spendLimit = extraLimit;
                spendPercent ??= ReadNullableDouble(extra, "utilization");
            }
        }

        var hasMonthlySpend = spendUsed is not null && spendLimit is not null && spendLimit > 0;
        var mode = hasMonthlySpend ? UsageMode.MonthlySpend
            : hasWindows ? UsageMode.PlanLimits
            : UsageMode.Unknown;

        return new UsageSnapshot
        {
            Mode = mode,
            HasMonthlySpend = hasMonthlySpend,
            MonthlySpendUsedUsd = hasMonthlySpend ? spendUsed!.Value : 0m,
            MonthlySpendLimitUsd = hasMonthlySpend ? spendLimit!.Value : 0m,
            MonthlySpendPercent = !hasMonthlySpend ? 0
                : spendPercent ?? (double)(spendUsed!.Value / spendLimit!.Value * 100m),
            Severity = severity,
            FiveHourUtilizationPercent = fiveHour,
            FiveHourResetsAt = fiveHourResets,
            WeeklyUtilizationPercent = weekly,
            WeeklyResetsAt = weeklyResets,
            WeeklyOpusUtilizationPercent = ReadBucketUtilization(root, "seven_day_opus"),
            WeeklySonnetUtilizationPercent = ReadBucketUtilization(root, "seven_day_sonnet"),
            ExtraUsageEnabled = extraEnabled,
            ExtraUsageLimit = extraLimit,
            ExtraUsageUsed = extraUsed,
            CreditBalance = creditBalance,
            Currency = currency,
            SubscriptionType = subscriptionType,
            FetchedAt = DateTimeOffset.UtcNow,
            IsStale = false
        };
    }

    private static double? ReadLimitPercent(JsonElement limit) =>
        limit.ValueKind == JsonValueKind.Object ? ReadNullableDouble(limit, "percent") : null;

    /// <summary>resets_at has been seen both as an ISO 8601 string and as
    /// epoch seconds; accept either.</summary>
    private static DateTimeOffset? ReadResetsAt(JsonElement parent)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty("resets_at", out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Number)
            return DateTimeOffset.FromUnixTimeSeconds((long)el.GetDouble());
        if (el.ValueKind == JsonValueKind.String)
        {
            var text = el.GetString();
            if (DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
                return parsed;
            if (double.TryParse(text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        }
        return null;
    }

    private static decimal? ReadMinorAmountAsUsd(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return null;
        var minor = ReadNullableDouble(obj, "amount_minor");
        if (minor is null) return null;
        var exponent = ReadNullableDouble(obj, "exponent") ?? 2;
        return (decimal)(minor.Value / Math.Pow(10, exponent));
    }

    private static double? ReadBucketUtilization(JsonElement root, string bucketName)
    {
        if (!root.TryGetProperty(bucketName, out var bucket) || bucket.ValueKind != JsonValueKind.Object)
            return null;
        return ReadNullableDouble(bucket, "utilization");
    }

    private static DateTimeOffset? ReadBucketResetsAt(JsonElement root, string bucketName) =>
        root.TryGetProperty(bucketName, out var bucket) ? ReadResetsAt(bucket) : null;

    private static double? ReadNullableDouble(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el)) return null;
        return el.ValueKind switch
        {
            JsonValueKind.Number => el.GetDouble(),
            JsonValueKind.String when double.TryParse(el.GetString(), System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var d) => d,
            _ => null
        };
    }

    private static string? ReadNullableString(JsonElement parent, string propertyName)
    {
        if (!parent.TryGetProperty(propertyName, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private UsageSnapshot? TryLoadDiskCache()
    {
        try
        {
            if (!File.Exists(_cacheFilePath)) return null;
            var json = File.ReadAllText(_cacheFilePath);
            var cached = JsonSerializer.Deserialize<UsageSnapshot>(json);
            return cached is null ? null : cached with { IsStale = true };
        }
        catch
        {
            return null;
        }
    }

    private void TrySaveDiskCache(UsageSnapshot snapshot)
    {
        try
        {
            File.WriteAllText(_cacheFilePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // Best-effort only; the in-memory cache is what actually matters
            // for the 30s rate limit within a running process.
        }
    }

    public void Dispose() => _fetchLock.Dispose();
}

/// <summary>Thrown internally when the usage API returns HTTP 429, carrying
/// the server's Retry-After hint (if any) so the caller can back off properly
/// instead of just retrying at the normal poll interval.</summary>
internal sealed class UsageApiRateLimitedException(TimeSpan? retryAfter) : Exception("Usage API rate limited (429).")
{
    public TimeSpan? RetryAfter { get; } = retryAfter;
}

/// <summary>Thrown when the usage API returns HTTP 401 - in practice, an
/// expired OAuth access token ("OAuth access token has expired.
/// Re-authenticate to continue."). Reached only when ClaudeTokenRefresher
/// couldn't refresh it (e.g. the refresh token has expired too); the fix is
/// signing in again through Claude Desktop or the CLI.</summary>
internal sealed class UsageApiUnauthorizedException(string body) : Exception($"Usage API unauthorized (401): {body}");
