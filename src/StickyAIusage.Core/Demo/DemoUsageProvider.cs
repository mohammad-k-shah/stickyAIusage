using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Demo;

/// <summary>Claude's reading in demo mode: the script's, never Claude Code's files or the network.</summary>
public sealed class DemoUsageProvider(DemoData data, Func<DateTimeOffset> clock) : IUsageProvider
{
    public string ProviderId => "claude";

    public string DisplayName => "Claude";

    public Task<UsageSnapshot> GetUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Usage(clock()));

    public Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Usage(clock()).ToProviderUsage());
}

/// <summary>Codex beside Claude in demo mode.</summary>
public sealed class DemoCodexProvider(DemoData data, Func<DateTimeOffset> clock) : IProviderUsageReader
{
    public string ProviderId => CodexUsageProvider.Id;

    public string DisplayName => "Codex";

    public bool IsPresent => true;

    public Task<ProviderUsage> GetProviderUsageAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(data.Codex(clock()));
}

/// <summary>The three made-up sessions behind the pill's cost and the flyout's cards.</summary>
public sealed class DemoSessionReader(DemoData data, Func<DateTimeOffset> clock) : ISessionReader
{
    public string Location => "the demo's sample sessions";

    public SessionInfo? GetMostRecentSession() => data.Sessions(clock())[0];

    public List<SessionInfo> GetSessionsForDate(DateOnly localDate) => GetSessionsInRange(localDate, localDate);

    public List<SessionInfo> GetSessionsInRange(DateOnly from, DateOnly to)
    {
        var today = DateOnly.FromDateTime(clock().LocalDateTime);
        return today >= from && today <= to ? data.Sessions(clock()).ToList() : [];
    }

    public DateTime? GetMostRecentActivityUtc() => clock().UtcDateTime;
}

/// <summary>The script's pace for Claude (the forecaster would need half an hour of readings);
/// none for another tool.</summary>
public sealed class DemoPace(DemoData data, Func<DateTimeOffset> clock) : IPaceForecaster
{
    public IReadOnlyList<PaceForecast> Update(ProviderUsage usage) =>
        usage.ProviderId == "claude" ? data.Forecasts(clock()) : [];
}

/// <summary>The cards' made-up repositories: no git or gh runs in demo mode.</summary>
public sealed class DemoGit(DemoData data) : IGitInfoSource
{
    public GitInfo? Get(string workingDirectory) => data.Git(workingDirectory);
}
