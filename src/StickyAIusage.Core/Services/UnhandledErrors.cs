namespace StickyAIusage.Services;

/// <summary>
/// The last line of defence for both apps: an exception nothing else caught
/// is written to diagnostics.log, and one on the UI thread is marked handled
/// so the pill stays (each app wires its own dispatcher event to
/// <see cref="OnUiThread"/>). Background tasks nobody awaited are logged and
/// observed; an exception that ends the process is logged before it does.
///
/// A fault that repeats on every frame would fill the log, so after
/// <see cref="BurstLimit"/> lines in a minute the rest of that minute is
/// counted and summed up in one line.
/// </summary>
public sealed class UnhandledErrors
{
    public const int BurstLimit = 10;
    private static readonly TimeSpan Burst = TimeSpan.FromMinutes(1);

    private readonly Action<string> _log;
    private readonly Func<DateTimeOffset> _now;
    private readonly object _lock = new();
    private DateTimeOffset _burstStart = DateTimeOffset.MinValue;
    private int _inBurst;
    private int _skipped;

    /// <param name="log">Where lines go (the diagnostics log by default).</param>
    public UnhandledErrors(Action<string>? log = null, Func<DateTimeOffset>? now = null)
    {
        _log = log ?? DiagnosticLog.WriteInfo;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The app's instance, wired by <see cref="Install"/>.</summary>
    public static UnhandledErrors Shared { get; } = new();

    private static int _installed;

    /// <summary>Hooks the process-wide events (once): unhandled exceptions
    /// on any thread and unobserved task exceptions.</summary>
    public static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1)
            return;
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Shared.OnFatal(e.ExceptionObject as Exception, e.IsTerminating);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Shared.Write("Unobserved task exception (the app keeps running)", e.Exception);
            e.SetObserved();
        };
    }

    /// <summary>An exception from the UI thread's dispatcher: logged, and
    /// true = mark it handled so the app keeps running.</summary>
    public bool OnUiThread(Exception exception)
    {
        Write("Unhandled exception on the UI thread (the app keeps running)", exception);
        return true;
    }

    /// <summary>An exception from another thread: the runtime ends the
    /// process after this when <paramref name="terminating"/>.</summary>
    public void OnFatal(Exception? exception, bool terminating) =>
        Write(terminating ? "Unhandled exception; the app has to close" : "Unhandled exception", exception, always: terminating);

    /// <summary>Start-up threw before the widget was up.</summary>
    public void OnStartupFailed(Exception exception) =>
        Write("Start-up failed", exception, always: true);

    private void Write(string what, Exception? exception, bool always = false)
    {
        string? summary = null;
        bool write;
        lock (_lock)
        {
            var now = _now();
            if (now - _burstStart >= Burst)
            {
                // Said when the next one comes, so a quiet app writes nothing.
                if (_skipped > 0)
                    summary = $"{_skipped} more unhandled exception(s) in the minute from {_burstStart:HH:mm:ss}Z were not written out.";
                _burstStart = now;
                _inBurst = 0;
                _skipped = 0;
            }
            write = always || ++_inBurst <= BurstLimit;
            if (!write)
                _skipped++;
        }
        if (summary is not null)
            _log(summary);
        if (write)
            _log($"{what}: {exception?.ToString() ?? "(no exception object)"}");
    }
}
