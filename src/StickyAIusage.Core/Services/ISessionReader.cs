using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>The sessions behind the pill's session cost and the flyout's
/// cards. An interface so <see cref="UsageFeed"/> can be built in a test, and
/// so another tool's local logs can supply sessions later.</summary>
public interface ISessionReader
{
    /// <summary>The session the collapsed pill shows (see
    /// <see cref="ClaudeSessionLogReader.GetMostRecentSession"/>).</summary>
    SessionInfo? GetMostRecentSession();

    List<SessionInfo> GetSessionsForDate(DateOnly localDate);

    List<SessionInfo> GetSessionsInRange(DateOnly from, DateOnly to);

    /// <summary>The newest transcript write, as a cheap "is the agent
    /// working right now" signal for polling.</summary>
    DateTime? GetMostRecentActivityUtc();

    /// <summary>Where the sessions are read from, for the log.</summary>
    string Location { get; }
}

/// <summary>Claude Code's transcripts under one projects folder: the
/// <see cref="TranscriptIndex"/> that knows the files and the reader that
/// parses them. Nothing starts until the first question; disposing it stops
/// the index's watcher and timer.</summary>
public sealed class ClaudeSessionLogSource : ISessionReader, IDisposable
{
    public ClaudeSessionLogSource(string projectsRoot) : this(new TranscriptIndex(projectsRoot))
    {
    }

    internal ClaudeSessionLogSource(TranscriptIndex index)
    {
        Index = index;
        Reader = new ClaudeSessionLogReader(index);
    }

    /// <summary>Claude Code's projects folder for this profile (AppPaths).</summary>
    public static ClaudeSessionLogSource CreateDefault() => new(ClaudeSessionLogReader.DefaultProjectsRoot);

    public TranscriptIndex Index { get; }

    public ClaudeSessionLogReader Reader { get; }

    public string Location => Index.Root;

    public SessionInfo? GetMostRecentSession() => Reader.GetMostRecentSession();

    public List<SessionInfo> GetSessionsForDate(DateOnly localDate) => Reader.GetSessionsForDate(localDate);

    public List<SessionInfo> GetSessionsInRange(DateOnly from, DateOnly to) => Reader.GetSessionsInRange(from, to);

    public DateTime? GetMostRecentActivityUtc() => Reader.GetMostRecentActivityUtc();

    public void Dispose() => Index.Dispose();
}
