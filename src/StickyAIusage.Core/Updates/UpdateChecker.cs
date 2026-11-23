using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using StickyAIusage.Services;

namespace StickyAIusage.Updates;

/// <summary>A published release: its tag as GitHub lists it ("v2.0.1") and its version.</summary>
/// <param name="PreRelease">Published as a pre-release (GitHub's flag or a hyphen in the tag).</param>
public sealed record ReleaseTag(string Tag, SemanticVersion Version, bool PreRelease)
{
    /// <summary>The tag when it is a version, null otherwise ("latest", "nightly").</summary>
    public static ReleaseTag? Parse(string? tag, bool flaggedPreRelease = false) =>
        SemanticVersion.Parse(tag) is { } version && tag!.IndexOf('+') < 0
            ? new ReleaseTag(tag.Trim(), version, flaggedPreRelease || version.IsPreRelease)
            : null;
}

/// <summary>A newer release than the running one: what the flyout, the tray menu and
/// Settings → Health say about it.</summary>
/// <param name="Command">The command that updates this copy (<see cref="InstallMethods.UpdateCommand"/>);
/// null: download it from the release page.</param>
/// <param name="CommandPlace">"PowerShell" or "Terminal"; null without a command.</param>
public sealed record UpdateNotice(SemanticVersion Version, SemanticVersion Running, string ReleaseUrl,
    InstallMethod Method, string? Command, string? CommandPlace)
{
    /// <summary>"Update available: v2.0.1 — what's new" (the flyout's link and the menu item).</summary>
    public string LinkText => $"Update available: v{Version} — what's new";

    /// <summary>The flyout's second line: how to update this copy.</summary>
    public string HowText => Command is null
        ? "Download it from the release page."
        : $"To update, run this in {CommandPlace}:";

    /// <summary>Settings → Health's line.</summary>
    public string HealthDetail => $"Version {Version} is out (this is {Running}). " +
        (Command is null ? "Download it from the release page." : $"To update, run in {CommandPlace}: {Command}");
}

/// <summary>What one check came to (the log line says the same in words).</summary>
public enum UpdateCheckOutcome { NewerFound, UpToDate, NotModified, RateLimited, Failed, Invalid }

/// <summary>
/// The daily "is there a newer version?" check (Settings → Check for updates,
/// <see cref="AppSettings.CheckForUpdates"/>; only built when it is on, so with it off
/// nothing is ever asked). At most once every 24 hours, never in the first minute after
/// start, it asks GitHub's REST API for the repository's newest releases - one GET, no
/// sign-in, nothing about the user or this computer - and remembers the answer in
/// <c>cache/update-check.json</c> with its ETag, so the next day's question is a
/// conditional one. The result is only shown, never installed. Pre-releases count only
/// while a pre-release is running. A failed, rate-limited or odd answer changes
/// nothing; it is logged in one line and tried again a day later.
/// </summary>
public sealed class UpdateChecker : IDisposable
{
    /// <summary>The newest ten releases (drafts are never listed for an anonymous request).
    /// The list, not <c>/releases/latest</c>: a pre-release build must hear of the next
    /// pre-release, which <c>latest</c> never names.</summary>
    public static readonly Uri DefaultSource =
        new("https://api.github.com/repos/mohammad-k-shah/stickyAIusage/releases?per_page=10");

    public const string ReleasePageBase = "https://github.com/mohammad-k-shah/stickyAIusage/releases/tag/";

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    public const string StateFileName = "update-check.json";

    /// <summary>An answer larger than this is refused unread (ten releases with long notes are ~100 KB).</summary>
    public const int MaxBytes = 1024 * 1024;

    private readonly HttpClient _http;
    private readonly string _statePath;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Uri _source;
    private readonly DateTimeOffset _startedAt;
    private readonly SemanticVersion? _running;
    private readonly InstallMethod _method;
    private readonly bool _isMac;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private volatile UpdateState _state;
    private Task? _check;
    private bool _disposed;

    /// <param name="running">This build's version; null (not known) never asks.</param>
    /// <param name="method">How this copy was installed: picks the update command.</param>
    public UpdateChecker(HttpClient http, string cacheDirectory, Func<DateTimeOffset> clock, SemanticVersion? running,
        InstallMethod method, bool isMac, Uri? source = null)
    {
        _http = http;
        _statePath = Path.Combine(cacheDirectory, StateFileName);
        _clock = clock;
        _source = source ?? DefaultSource;
        _startedAt = clock();
        _running = running;
        _method = method;
        _isMac = isMac;
        _state = UpdateState.Read(_statePath);
    }

    /// <summary>Where notes go (the diagnostics log; a list in tests).</summary>
    internal Action<string> Log { get; init; } = DiagnosticLog.WriteInfo;

    /// <summary>When the last check was tried; null: never.</summary>
    public DateTimeOffset? LastChecked => _state.CheckedAt;

    /// <summary>This build's version, when known.</summary>
    public SemanticVersion? Running => _running;

    /// <summary>A newer release than this build, from the last answer GitHub gave
    /// (kept across restarts); null: none known.</summary>
    public UpdateNotice? Notice
    {
        get
        {
            var state = _state;
            if (_running is null)
                return null;
            // A stable build hears of stable releases only; a pre-release build of both.
            var candidate = _running.IsPreRelease ? Newer(state.Newest, state.NewestStable) : state.NewestStable;
            if (candidate is null || !candidate.Version.IsNewerThan(_running))
                return null;
            return new UpdateNotice(candidate.Version, _running, ReleasePageBase + Uri.EscapeDataString(candidate.Tag), _method,
                InstallMethods.UpdateCommand(_method, _isMac, candidate.PreRelease),
                InstallMethods.CommandPlace(_method, _isMac));
        }
    }

    private static ReleaseTag? Newer(ReleaseTag? a, ReleaseTag? b) =>
        a is null ? b : b is null ? a : a.Version.CompareTo(b.Version) >= 0 ? a : b;

    /// <summary>Whether a check is due now.</summary>
    public bool IsDue()
    {
        if (_running is null)
            return false;
        var now = _clock();
        lock (_lock)
        {
            if (_check is { IsCompleted: false } || now - _startedAt < StartDelay)
                return false;
            // A check stamped in the future means the clock was set back: check again.
            return _state.CheckedAt is not { } last || now - last >= Interval || last > now + TimeSpan.FromHours(1);
        }
    }

    /// <summary>Starts a check in the background when one is due; returns at once.
    /// Called on every refresh tick, so it must stay cheap.</summary>
    public void RefreshIfDue()
    {
        if (!IsDue())
            return;
        lock (_lock)
        {
            // A tick from before a Settings rebuild can arrive after Dispose.
            if (_disposed || _check is { IsCompleted: false })
                return;
            var token = _stop.Token;
            _check = Task.Run(() => CheckAsync(token));
        }
    }

    /// <summary>One check. Never throws; the outcome is logged in one line.</summary>
    internal async Task<UpdateCheckOutcome> CheckAsync(CancellationToken cancellationToken)
    {
        var now = _clock();
        var before = _state;
        // Stamped before the request, so a crash or a hang can't turn into a retry loop.
        Save(before with { CheckedAt = now });

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _source);
            // GitHub asks every API client for a User-Agent; this one names the app, nothing else.
            request.Headers.UserAgent.Add(new ProductInfoHeaderValue("StickyAIusage", null));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
            if (before.ETag is { Length: > 0 } etag && EntityTagHeaderValue.TryParse(etag, out var tag))
                request.Headers.IfNoneMatch.Add(tag);

            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotModified)
                return Note(UpdateCheckOutcome.NotModified,
                    "no new release since the last check" + (Notice is { } known ? $"; version {known.Version} is out" : ""));
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                return Note(UpdateCheckOutcome.RateLimited,
                    $"GitHub declined (HTTP {(int)response.StatusCode}: its hourly limit for this address); trying again in a day");
            if (!response.IsSuccessStatusCode)
                return Note(UpdateCheckOutcome.Failed, $"check failed: HTTP {(int)response.StatusCode}; trying again in a day");
            if (response.Content.Headers.ContentLength > MaxBytes)
                return Note(UpdateCheckOutcome.Invalid, "check failed: the answer is too large");

            var text = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (text is null)
                return Note(UpdateCheckOutcome.Invalid, "check failed: the answer is too large");
            if (!TryReadReleases(text, out var newest, out var newestStable))
                return Note(UpdateCheckOutcome.Invalid, "check failed: the answer isn't a list of releases");

            Save(new UpdateState(now, response.Headers.ETag?.ToString(), newest, newestStable));
            return Notice is { } notice
                ? Note(UpdateCheckOutcome.NewerFound, $"version {notice.Version} is out (this is {_running})")
                : Note(UpdateCheckOutcome.UpToDate, $"this version ({_running}) is the newest");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return UpdateCheckOutcome.Failed;
        }
        catch (TaskCanceledException)
        {
            return Note(UpdateCheckOutcome.Failed, "check failed: GitHub didn't answer in time; trying again in a day");
        }
        catch (Exception ex)
        {
            return Note(UpdateCheckOutcome.Failed, $"check failed: {ex.GetType().Name}: {ex.Message}; trying again in a day");
        }
    }

    private UpdateCheckOutcome Note(UpdateCheckOutcome outcome, string what)
    {
        Log("Updates: " + what + ".");
        return outcome;
    }

    /// <summary>The newest release of any kind and the newest stable one in GitHub's
    /// release list. Drafts and tags that aren't versions are skipped.</summary>
    internal static bool TryReadReleases(string json, out ReleaseTag? newest, out ReleaseTag? newestStable)
    {
        newest = null;
        newestStable = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;
            foreach (var release in document.RootElement.EnumerateArray())
            {
                if (release.ValueKind != JsonValueKind.Object ||
                    (release.TryGetProperty("draft", out var draft) && draft.ValueKind == JsonValueKind.True) ||
                    !release.TryGetProperty("tag_name", out var tagName) || tagName.ValueKind != JsonValueKind.String)
                    continue;
                var flagged = release.TryGetProperty("prerelease", out var pre) && pre.ValueKind == JsonValueKind.True;
                if (ReleaseTag.Parse(tagName.GetString(), flagged) is not { } tag)
                    continue;
                newest = Newer(newest, tag);
                if (!tag.PreRelease)
                    newestStable = Newer(newestStable, tag);
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private void Save(UpdateState state)
    {
        _state = state;
        state.Write(_statePath);
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _stop.Cancel();
        _stop.Dispose();
    }
}

/// <summary><c>cache/update-check.json</c> (schema 1): when the last check was tried, the
/// ETag of GitHub's last full answer and the newest releases it named. A file of another
/// schema, or one that doesn't parse, counts as no check yet.</summary>
internal sealed record UpdateState(DateTimeOffset? CheckedAt, string? ETag, ReleaseTag? Newest, ReleaseTag? NewestStable)
{
    public const int Schema = 1;

    public static readonly UpdateState None = new(null, null, null, null);

    public static UpdateState Read(string path)
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 16 * 1024)
                return None;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("schema", out var schema) ||
                schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var number) || number != Schema)
                return None;
            DateTimeOffset? checkedAt = Text(root, "checkedAt") is { } at &&
                                        DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
            return new UpdateState(checkedAt, Text(root, "etag"),
                ReleaseTag.Parse(Text(root, "newest"), Flag(root, "newestIsPreRelease")),
                ReleaseTag.Parse(Text(root, "newestStable")) is { PreRelease: false } stable ? stable : null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            DiagnosticLog.WriteError("UpdateState.Read", ex);
            return None;
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Flag(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    public void Write(string path)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["schema"] = Schema,
                ["checkedAt"] = CheckedAt?.ToString("O", CultureInfo.InvariantCulture),
                ["etag"] = ETag,
                ["newest"] = Newest?.Tag,
                ["newestIsPreRelease"] = Newest?.PreRelease ?? false,
                ["newestStable"] = NewestStable?.Tag
            });
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("UpdateState.Write", ex);
        }
    }
}
