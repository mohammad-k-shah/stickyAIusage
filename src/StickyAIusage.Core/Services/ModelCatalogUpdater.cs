using System.Globalization;
using System.IO;
using System.Net.Http;

namespace StickyAIusage.Services;

/// <summary>
/// The optional daily price-list refresh (Settings → "Check for updates",
/// <see cref="AppSettings.CheckForUpdates"/>): at most once every 24 hours,
/// and not in the first minute after start, it downloads <c>data/models.json</c>
/// from the project's repository, validates it, and when its date is newer
/// than the list in use, saves it as <c>cache/models.json</c> and switches to it.
/// A failed, invalid or older download changes nothing. Only built when the
/// setting is on, so with it off no request is ever made.
/// </summary>
public sealed class ModelCatalogUpdater : IDisposable
{
    public static readonly Uri DefaultSource =
        new("https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/data/models.json");

    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);
    public static readonly TimeSpan StartDelay = TimeSpan.FromMinutes(1);
    public const string StampFileName = "models-check.txt";

    private readonly HttpClient _http;
    private readonly string _cacheDirectory;
    private readonly Func<DateTimeOffset> _clock;
    private readonly Uri _source;
    private readonly DateTimeOffset _startedAt;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private DateTimeOffset? _lastAttempt;
    private Task? _running;
    private bool _disposed;

    public ModelCatalogUpdater(HttpClient http, string cacheDirectory, Func<DateTimeOffset> clock, Uri? source = null)
    {
        _http = http;
        _cacheDirectory = cacheDirectory;
        _clock = clock;
        _source = source ?? DefaultSource;
        _startedAt = clock();
        _lastAttempt = ReadStamp();
    }

    /// <summary>Where notes go (the diagnostics log; a list in tests).</summary>
    internal Action<string> Log { get; init; } = DiagnosticLog.WriteInfo;

    /// <summary>Makes the downloaded list the one in use; <see cref="ModelCatalog.Use"/> in the app.</summary>
    internal Action<ModelCatalog> Apply { get; init; } = ModelCatalog.Use;

    /// <summary>The list a download must be newer than; <see cref="ModelCatalog.Current"/> in the app.</summary>
    internal Func<ModelCatalog> InUse { get; init; } = () => ModelCatalog.Current;

    /// <summary>Whether a check is due now.</summary>
    public bool IsDue()
    {
        var now = _clock();
        lock (_lock)
        {
            if (_running is { IsCompleted: false } || now - _startedAt < StartDelay)
                return false;
            // A stamp in the future means the clock was set back: check again.
            return _lastAttempt is not { } last || now - last >= Interval || last > now + TimeSpan.FromHours(1);
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
            // A tick that started before the Settings switch rebuilt the services
            // can still arrive after Dispose: it must not start anything.
            if (_disposed || _running is { IsCompleted: false })
                return;
            var token = _stop.Token;
            _running = Task.Run(() => RefreshAsync(token));
        }
    }

    /// <summary>One check. Never throws; the outcome is logged.</summary>
    internal async Task<RefreshOutcome> RefreshAsync(CancellationToken cancellationToken)
    {
        var now = _clock();
        lock (_lock)
            _lastAttempt = now;
        // Stamped before the request, so a crash or a hang can't turn into a retry loop.
        WriteStamp(now);

        try
        {
            using var response = await _http.GetAsync(_source, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return Note(RefreshOutcome.Failed, $"HTTP {(int)response.StatusCode}");
            if (response.Content.Headers.ContentLength > ModelCatalog.MaxBytes)
                return Note(RefreshOutcome.Invalid, "the file is too large");

            var text = await ReadLimitedAsync(response.Content, cancellationToken).ConfigureAwait(false);
            if (text is null)
                return Note(RefreshOutcome.Invalid, "the file is too large");
            if (!ModelCatalog.TryParse(text, out var downloaded, out var problem, origin: "downloaded"))
                return Note(RefreshOutcome.Invalid, problem);
            if (downloaded!.IsDatedInTheFuture(now))
                return Note(RefreshOutcome.Invalid, $"the file is dated {downloaded.Updated:yyyy-MM-dd}, in the future");

            var inUse = InUse();
            if (downloaded.Updated <= inUse.Updated)
                return Note(RefreshOutcome.NotNewer,
                    $"the published list ({downloaded.Updated:yyyy-MM-dd}) is not newer than the one in use ({inUse.Updated:yyyy-MM-dd})");

            Directory.CreateDirectory(_cacheDirectory);
            AtomicFile.WriteAllText(Path.Combine(_cacheDirectory, ModelCatalog.CacheFileName), text);
            Apply(downloaded);
            Log($"Prices: updated to the published price list of {downloaded.Updated:yyyy-MM-dd} " +
                $"({downloaded.Models.Count} models).");
            return RefreshOutcome.Updated;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return RefreshOutcome.Failed;
        }
        catch (Exception ex)
        {
            return Note(RefreshOutcome.Failed, ex.GetType().Name + ": " + ex.Message);
        }
    }

    private RefreshOutcome Note(RefreshOutcome outcome, string why)
    {
        Log($"Prices: price list check {(outcome == RefreshOutcome.NotNewer ? "found nothing new" : "failed")}: {why}. " +
            "Keeping the prices in use.");
        return outcome;
    }

    private static async Task<string?> ReadLimitedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > ModelCatalog.MaxBytes)
                return null;
            buffer.Write(chunk, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private string StampPath => Path.Combine(_cacheDirectory, StampFileName);

    private DateTimeOffset? ReadStamp()
    {
        try
        {
            return File.Exists(StampPath) &&
                   DateTimeOffset.TryParse(File.ReadAllText(StampPath).Trim(), CultureInfo.InvariantCulture,
                       DateTimeStyles.RoundtripKind, out var stamp)
                ? stamp
                : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ModelCatalogUpdater.ReadStamp", ex);
            return null;
        }
    }

    private void WriteStamp(DateTimeOffset at)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
            AtomicFile.WriteAllText(StampPath, at.ToString("O", CultureInfo.InvariantCulture));
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ModelCatalogUpdater.WriteStamp", ex);
        }
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

public enum RefreshOutcome { Updated, NotNewer, Invalid, Failed }
