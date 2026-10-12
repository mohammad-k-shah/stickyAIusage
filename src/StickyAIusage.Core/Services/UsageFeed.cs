using System.Collections.Concurrent;
using System.IO;
using StickyAIusage.Models;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Services;

/// <summary>
/// The data behind the widget, shared by the Windows and macOS apps: the
/// usage reading plus the most recent session for the pill, and today's
/// session cards for the flyout. Everything runs on the thread pool; callers
/// marshal the results back to their UI thread.
/// </summary>
public sealed class UsageFeed : IDisposable
{
    private static readonly TimeSpan GitCacheTtl = TimeSpan.FromSeconds(30);

    private readonly ClaudeUsageProvider _usageProvider = new();
    private readonly DailySpendTracker _dailySpend = new();
    private readonly ConcurrentDictionary<string, (DateTime At, GitInfo? Info)> _gitCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _logLock = new();
    private (string? path, long totalTokens, decimal cost)? _lastLoggedSession;

    /// <summary>The card layout the flyout renders with; set from settings
    /// whenever they change. Read on the thread pool, so it's a plain
    /// reference swap rather than a mutable object.</summary>
    public CardLayout Layout { get; set; } = CardLayout.Default;

    /// <summary>The usage reading, the most recent session, and - on a
    /// monthly-cap account - what was spent today (null otherwise).</summary>
    public Task<(UsageSnapshot Usage, SessionInfo? Session, decimal? SpentToday)> GetSummaryAsync() => Task.Run(async () =>
    {
        var usage = await _usageProvider.GetUsageAsync();
        var session = ClaudeSessionLogReader.GetMostRecentSession();
        LogSessionSelectionIfChanged(session);

        decimal? spentToday = null;
        if (usage.HasMonthlySpend)
        {
            var today = DateOnly.FromDateTime(DateTime.Now);
            spentToday = _dailySpend.Record(usage.MonthlySpendUsedUsd, today, () => CodeSpentSince(today));
        }
        return (usage, session, spentToday);
    });

    /// <summary>Estimated cost of the Claude Code sessions started today. A
    /// session carried over from yesterday is left out: its cost is for the
    /// whole session, not just today's part.</summary>
    private static decimal CodeSpentSince(DateOnly today) =>
        ClaudeSessionLogReader.GetSessionsForDate(today)
            .Where(s => DateOnly.FromDateTime(s.StartedAt.LocalDateTime) == today)
            .Sum(s => s.EstimatedCostUsd);

    /// <summary>Sessions active in the last <paramref name="days"/> days (1 =
    /// today only), most recently active first, with git info.</summary>
    public Task<List<SessionCardViewModel>> GetCardsAsync(int days) => Task.Run(() =>
    {
        var now = DateTimeOffset.Now;
        var layout = Layout;
        var today = DateOnly.FromDateTime(DateTime.Now);
        return ClaudeSessionLogReader.GetSessionsInRange(today.AddDays(1 - days), today)
            .Select(s =>
            {
                s.Git = GetGitInfo(s.WorkingDirectory);
                return SessionCardViewModel.From(s, now, layout);
            })
            .ToList();
    });

    /// <summary>Makes the next refresh log the selected session even if it's
    /// unchanged - used on resume, so "nothing updated for hours" and "the
    /// widget was paused" don't look identical in diagnostics.log.</summary>
    public void ForgetLoggedSession()
    {
        lock (_logLock) _lastLoggedSession = null;
    }

    /// <summary>Logs which transcript is read as "most recent" and its totals,
    /// only when something changed, so a "cost never updates" report can be
    /// diagnosed without spamming the log every 15s.</summary>
    private void LogSessionSelectionIfChanged(SessionInfo? session)
    {
        var current = session is null
            ? ((string? path, long totalTokens, decimal cost)?)null
            : (session.TranscriptPath, session.TotalTokens, session.EstimatedCostUsd);

        lock (_logLock)
        {
            if (current == _lastLoggedSession)
                return;
            _lastLoggedSession = current;
        }

        if (session is null)
        {
            DiagnosticLog.WriteInfo($"Session selection: no session found under {ClaudeSessionLogReader.DefaultProjectsRoot}.");
            return;
        }

        DiagnosticLog.WriteInfo(
            $"Session selection changed: path={session.TranscriptPath} " +
            $"lastWrite={File.GetLastWriteTime(session.TranscriptPath):yyyy-MM-dd HH:mm:ss} " +
            $"model={session.Model} in={session.InputTokens} out={session.OutputTokens} " +
            $"cacheRead={session.CacheReadTokens} cacheCreate={session.CacheCreationTokens} " +
            $"total={session.TotalTokens} cost={session.EstimatedCostUsd:0.0000}");
    }

    /// <summary>Git info per working directory, re-read at most every 30s -
    /// the panel refreshes every 15s while open, and each read is up to four
    /// git processes per session.</summary>
    private GitInfo? GetGitInfo(string workingDirectory)
    {
        if (_gitCache.TryGetValue(workingDirectory, out var cached) && DateTime.UtcNow - cached.At < GitCacheTtl)
            return cached.Info;

        GitInfo? info = null;
        try { info = GitInfoReader.TryRead(workingDirectory); }
        catch (Exception ex) { DiagnosticLog.WriteError("GitInfoReader.TryRead", ex); }

        _gitCache[workingDirectory] = (DateTime.UtcNow, info);
        return info;
    }

    public void Dispose() => _usageProvider.Dispose();
}
