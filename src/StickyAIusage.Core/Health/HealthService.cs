using System.IO;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Health;

/// <summary>A health check's items and the text of its report.</summary>
public sealed record HealthResult(IReadOnlyList<HealthItem> Items, string Report);

/// <summary>
/// Runs Settings → Health for a window: its services (the transcript index,
/// the feed, today's sessions), its latest reading and the settings, through
/// <see cref="HealthProbe"/> and <see cref="HealthCheck"/>. Off the UI thread;
/// never throws (a failed probe is logged and its facts stay unknown).
/// </summary>
public static class HealthService
{
    /// <param name="usage">The window's latest reading; null before the first refresh.</param>
    /// <param name="lastTestAlertAt">When this run last sent a test alert.</param>
    /// <param name="identity">The build line (<see cref="AppIdentity.Describe"/>) for the report.</param>
    public static Task<HealthResult> CheckAsync(AppServices services, AppSettings settings, UsageSnapshot? usage,
        DateTimeOffset? lastTestAlertAt, string identity, IHealthFiles? files = null) => Task.Run(async () =>
    {
        files ??= DiskHealthFiles.Instance;
        var now = DateTimeOffset.UtcNow;
        var facts = new HealthFacts
        {
            Now = now,
            IsMac = OperatingSystem.IsMacOS(),
            ClaudeDirectory = AppPaths.ClaudeDirectory,
            Usage = usage,
            Mode = ClaudeSourceModes.Parse(settings.ClaudeSource),
            AlertsEnabled = settings.AlertsEnabled,
            LastTestAlertAt = lastTestAlertAt,
            StartAtLogin = settings.StartWithWindows,
            CheckForUpdates = settings.CheckForUpdates,
            // The check's own version, or (switched on, services not rebuilt yet) the build's.
            RunningVersion = (services.AppUpdates?.Running ?? AppIdentity.ReleaseVersion())?.ToString(),
            UpdateCheckedAt = services.AppUpdates?.LastChecked,
            Update = services.AppUpdates?.Notice,
            CodexEnabled = settings.IsProviderEnabled(CodexUsageProvider.Id),
            CodexSessionsFolder = Path.Combine(AppPaths.CodexDirectory, "sessions")
        };
        try
        {
            var index = services.Transcripts;
            index.WaitUntilReady(TimeSpan.FromSeconds(15));
            var transcripts = index.Files().Where(f => ClaudeSessionLogReader.IsSessionTranscript(f.Path)).ToList();
            var newest = transcripts.Count == 0 ? null : transcripts.MaxBy(f => f.LastWriteUtc).Path;
            var (exists, version, marker, installed) = HealthProbe.Read(files, AppPaths.ClaudeDirectory, AppPaths.FeedDirectory, newest);
            var records = services.ClaudeFeed.Sessions;
            facts = facts with
            {
                ClaudeDirectoryExists = exists,
                TranscriptCount = transcripts.Count,
                ClaudeCodeVersion = version,
                Marker = marker,
                PluginInstalled = installed,
                NewestRecordAt = records.Count == 0 ? null : records.Max(r => r.ObservedAt),
                CodexPresent = files.DirectoryExists(facts.CodexSessionsFolder),
                SessionsToday = (await services.Feed.GetTodaySessionsAsync()).Count
            };
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("HealthService.CheckAsync", ex);
        }
        var items = HealthCheck.Build(facts);
        return new HealthResult(items, HealthCheck.Report(items, identity, now));
    });
}
