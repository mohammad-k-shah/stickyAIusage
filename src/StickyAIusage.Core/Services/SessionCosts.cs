using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Puts Claude Code's own session cost (the companion plugin's feed record)
/// on a session read from its transcript, when that figure can be trusted to
/// cover the same session: the plugin saw the session from its first message
/// (a resumed session's figure may only cover the part since the resume), and
/// its record is not behind the transcript. Otherwise the session keeps its
/// estimate from the price list.
/// </summary>
public static class SessionCosts
{
    /// <summary>How far the feed's start and last write may sit from the
    /// transcript's first and last message and still describe the same span.</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromMinutes(2);

    /// <summary>Sets <see cref="SessionInfo.ExactCostUsd"/> from the matching
    /// feed record, or clears it. Returns whether the session now has one.</summary>
    public static bool ApplyExact(SessionInfo session, IReadOnlyList<FeedSession> feed)
    {
        session.ExactCostUsd = FindExact(session, feed);
        return session.CostIsExact;
    }

    internal static decimal? FindExact(SessionInfo session, IReadOnlyList<FeedSession> feed)
    {
        foreach (var record in feed)
        {
            if (!string.Equals(record.SessionId, session.SessionId, StringComparison.OrdinalIgnoreCase))
                continue;
            if (record.CostUsd is not { } cost || cost < 0)
                return null;
            if (record.StartedAt is not { } startedAt || startedAt > session.StartedAt + Tolerance)
                return null;
            if (record.ObservedAt < session.LastActivityAt - Tolerance)
                return null;
            return cost;
        }
        return null;
    }
}
