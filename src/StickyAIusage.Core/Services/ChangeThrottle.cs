namespace StickyAIusage.Services;

/// <summary>
/// Turns a burst of signals into at most one call per <c>interval</c>: the first
/// signal calls at once, later ones within the interval are merged into one call at
/// its end. Claude Code appends to a transcript many times a turn; the window
/// refreshes once a second at most while it does, and not at all while it is quiet.
/// The call runs on a thread-pool thread and is never made after <see cref="Dispose"/>.
/// </summary>
public sealed class ChangeThrottle : IDisposable
{
    private readonly TimeSpan _interval;
    private readonly Action _call;
    private readonly Func<DateTimeOffset> _clock;
    private readonly object _lock = new();
    private readonly Timer _trailing;
    private DateTimeOffset _lastCall = DateTimeOffset.MinValue;
    private bool _pending;
    private bool _disposed;

    public ChangeThrottle(TimeSpan interval, Action call, Func<DateTimeOffset>? clock = null)
    {
        _interval = interval;
        _call = call;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _trailing = new Timer(_ => FireTrailing());
    }

    public void Signal()
    {
        lock (_lock)
        {
            if (_disposed || _pending)
                return;
            var wait = _lastCall + _interval - _clock();
            if (wait > TimeSpan.Zero)
            {
                _pending = true;
                // Never longer than one interval, whatever the clock did (set back an hour).
                _trailing.Change(wait > _interval ? _interval : wait, Timeout.InfiniteTimeSpan);
                return;
            }
            _lastCall = _clock();
        }
        Call();
    }

    private void FireTrailing()
    {
        lock (_lock)
        {
            if (_disposed || !_pending)
                return;
            _pending = false;
            _lastCall = _clock();
        }
        Call();
    }

    private void Call()
    {
        try
        {
            _call();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ChangeThrottle", ex);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _pending = false;
        }
        _trailing.Dispose();
    }
}
