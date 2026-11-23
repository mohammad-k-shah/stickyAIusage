namespace StickyAIusage.ViewModels;

/// <summary>
/// One refresh at a time on a window's UI thread, without losing a change
/// that arrives during one: the feed folder can change while a refresh is
/// still waiting for the usage endpoint (seconds), after it already read the
/// folder. Such a change asks for one more refresh when the running one ends.
/// Not thread-safe: both apps call it on their UI thread only.
/// </summary>
public sealed class RefreshGate
{
    private bool _inFlight;
    private bool _again;

    /// <summary>Starts a refresh, or returns false when one is under way.</summary>
    public bool TryStart()
    {
        if (_inFlight)
            return false;
        _inFlight = true;
        return true;
    }

    /// <summary>New data may be waiting. True: refresh now. False: one is
    /// under way, and <see cref="Finish"/> will ask for another.</summary>
    public bool RequestRefresh()
    {
        if (!_inFlight)
            return true;
        _again = true;
        return false;
    }

    /// <summary>Ends the running refresh; true when a change arrived during
    /// it and the caller should refresh once more.</summary>
    public bool Finish()
    {
        _inFlight = false;
        var again = _again;
        _again = false;
        return again;
    }
}
