using System.Collections.Concurrent;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Git info per working folder for the session cards. The flyout refreshes
/// every 15 s while it is open, and one read is up to four git processes per
/// session, so the local state is read again at most every
/// <see cref="LocalFor"/>. The pull request comes from `gh`, which asks
/// GitHub over the network: it is kept for <see cref="PullRequestFor"/>, and
/// asked again sooner only when the branch or the origin changes.
/// </summary>
public sealed class GitInfoCache : IGitInfoSource
{
    public static readonly TimeSpan LocalFor = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan PullRequestFor = TimeSpan.FromMinutes(5);

    private readonly GitInfoReader _reader;
    private readonly Func<DateTimeOffset> _now;
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, GitInfo? Info)> _local = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset At, string? Branch, string? Host, PullRequestInfo? Pr)> _pullRequests =
        new(StringComparer.OrdinalIgnoreCase);

    public GitInfoCache(GitInfoReader? reader = null, Func<DateTimeOffset>? now = null)
    {
        _reader = reader ?? GitInfoReader.Default;
        _now = now ?? (() => DateTimeOffset.UtcNow);
    }

    /// <summary>The folder's git state with its pull request, or null when it
    /// isn't a repository. Never throws.</summary>
    public GitInfo? Get(string workingDirectory)
    {
        var now = _now();
        if (!(_local.TryGetValue(workingDirectory, out var cached) && now - cached.At < LocalFor))
        {
            GitInfo? read = null;
            try { read = _reader.ReadLocal(workingDirectory); }
            catch (Exception ex) { DiagnosticLog.WriteError("GitInfoReader.ReadLocal", ex); }
            cached = (now, read);
            _local[workingDirectory] = cached;
        }
        return cached.Info is { } info ? GitInfoReader.WithPullRequest(info, PullRequest(workingDirectory, info, now)) : null;
    }

    private PullRequestInfo? PullRequest(string workingDirectory, GitInfo info, DateTimeOffset now)
    {
        if (_pullRequests.TryGetValue(workingDirectory, out var cached) && now - cached.At < PullRequestFor &&
            cached.Branch == info.Branch && cached.Host == info.OriginHost)
            return cached.Pr;

        PullRequestInfo? pr = null;
        try { pr = _reader.ReadPullRequest(workingDirectory, info); }
        catch (Exception ex) { DiagnosticLog.WriteError("GitInfoReader.ReadPullRequest", ex); }
        _pullRequests[workingDirectory] = (now, info.Branch, info.OriginHost, pr);
        return pr;
    }
}
