using StickyAIusage.Alerts;
using StickyAIusage.Demo;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;
using Xunit;

namespace StickyAIusage.Tests;

public class DemoDataTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset At(double seconds) => Start.AddSeconds(seconds);

    [Fact]
    public void The_same_start_and_moment_always_give_the_same_screens()
    {
        foreach (var variant in new[] { DemoVariant.Plan, DemoVariant.Cap })
        {
            var a = new DemoData(variant, Start);
            var b = new DemoData(variant, Start);
            foreach (var seconds in new[] { 0, 17.5, 60, 119, 120, 600 })
            {
                var now = At(seconds);
                Assert.Equal(a.Usage(now), b.Usage(now));
                Assert.Equal(a.Forecasts(now), b.Forecasts(now));
                Assert.Equal(a.Records(now), b.Records(now));
                Assert.Equal(a.Codex(now).Windows, b.Codex(now).Windows);
                Assert.Equal(a.Sessions(now).Select(Describe), b.Sessions(now).Select(Describe));
            }
        }

        static string Describe(SessionInfo s) =>
            $"{s.SessionId} {s.SessionName} {s.WorkingDirectory} {s.Model} {s.StartedAt:O} {s.LastActivityAt:O} {s.OutputTokens} " +
            $"{s.CurrentContextTokens} {s.EstimatedCostUsd}";
    }

    [Theory]
    [InlineData(-30, 62, 34)]
    [InlineData(0, 62, 34)]
    [InlineData(60, 77, 35)]
    [InlineData(120, 91, 36)]
    [InlineData(900, 91, 36)]
    public void The_five_hour_window_climbs_from_62_to_91_in_two_minutes_and_holds(double seconds, double fiveHour, double weekly)
    {
        var usage = new DemoData(DemoVariant.Plan, Start).Usage(At(seconds));
        Assert.Equal(UsageMode.PlanLimits, usage.Mode);
        Assert.Equal(fiveHour, usage.FiveHourUtilizationPercent);
        Assert.Equal(weekly, usage.WeeklyUtilizationPercent);
        Assert.Equal(Start + DemoData.FiveHourResetsIn, usage.FiveHourResetsAt);
        Assert.Equal(At(seconds), usage.FetchedAt);
        Assert.Equal(ClaudeFeedUsageSource.Id, usage.SourceId);
        Assert.Equal(SourceTrust.Official, usage.Trust);
        Assert.False(usage.IsStale);
    }

    [Fact]
    public void The_climb_never_goes_down_and_moves_in_whole_points()
    {
        var data = new DemoData(DemoVariant.Plan, Start);
        var last = 0.0;
        for (var second = 0; second <= 130; second++)
        {
            var percent = data.FiveHourPercent(At(second));
            Assert.True(percent >= last, $"{percent} after {last} at {second} s");
            Assert.Equal(Math.Round(percent), percent);
            last = percent;
        }
        Assert.Equal(DemoData.FiveHourTo, last);
    }

    [Fact]
    public void The_forecast_turns_yellow_then_orange_then_red_and_close_enough_to_alert()
    {
        var data = new DemoData(DemoVariant.Plan, Start);
        var stages = new List<string>();
        for (var second = 0; second <= 120; second++)
        {
            var now = At(second);
            var forecast = data.Forecasts(now)[0];
            Assert.Equal(UsageWindowKinds.FiveHour, forecast.Kind);
            Assert.True(forecast.IsActive);
            Assert.Equal(data.FiveHourResetsAt, forecast.ResetsAt);
            var left = forecast.HitsLimitAt!.Value - now;
            Assert.True(forecast.HitsLimitAt < data.FiveHourResetsAt, "the limit comes before the reset");
            Assert.True(forecast.ProjectedAtReset > 100);
            var stage = left > TimeSpan.FromMinutes(60) ? "yellow" : left > TimeSpan.FromMinutes(30) ? "orange" : "red";
            if (stages.Count == 0 || stages[^1] != stage)
                stages.Add(stage);
            // The rate is the one that reaches 100% at the time shown.
            Assert.InRange(forecast.PercentNow + forecast.RatePerHour!.Value * left.TotalHours, 99, 101);
        }
        Assert.Equal(["yellow", "orange", "red"], stages);
        // Red is within the "limit at" alert's 30 minutes.
        Assert.True(data.FiveHourLimitAt(At(120)) - At(120) <= AlertSettings.Default.ForecastLead);
    }

    [Fact]
    public void The_sessions_go_from_working_to_needs_you_and_done()
    {
        var data = new DemoData(DemoVariant.Plan, Start);
        Assert.All(data.Records(At(10)), r => Assert.Equal("working", r.State));

        var later = data.Records(At(45));
        Assert.Equal("done", later.Single(r => r.SessionId == DemoData.DocsSession).State);
        Assert.Equal("working", later.Single(r => r.SessionId == DemoData.CheckoutSession).State);

        var now = At(70);
        var activities = SessionActivities.From(data.Records(now), now);
        Assert.Equal(3, activities.Count);
        Assert.Equal(ActivityState.NeedsYou, activities[0].State);
        Assert.Equal(DemoData.CheckoutSession, activities[0].SessionId);
        Assert.Equal(Start + DemoData.NeedsYouAfter, activities[0].Since);
        Assert.Contains("waiting for permission to use Bash", activities[0].Line(now));
        Assert.Equal([ActivityState.NeedsYou, ActivityState.Working, ActivityState.Done], activities.Select(a => a.State));

        // Each record has a card with the same id.
        Assert.Equal(data.Records(now).Select(r => r.SessionId).Order(), data.Sessions(now).Select(s => s.SessionId).Order());
        Assert.All(data.Sessions(now), s => Assert.NotNull(data.Git(s.WorkingDirectory)));
    }

    [Fact]
    public void The_cap_variant_climbs_a_monthly_cap_instead()
    {
        var data = new DemoData(DemoVariant.Cap, Start);
        var first = data.Usage(At(0));
        Assert.Equal(UsageMode.MonthlySpend, first.Mode);
        Assert.True(first.HasMonthlySpend);
        Assert.Equal(DemoData.CapFromUsd, first.MonthlySpendUsedUsd);
        Assert.Equal(DemoData.CapLimitUsd, first.MonthlySpendLimitUsd);
        Assert.Equal(76.2, first.MonthlySpendPercent);
        var last = data.Usage(At(300));
        Assert.Equal(DemoData.CapToUsd, last.MonthlySpendUsedUsd);
        Assert.Equal(93.5, last.MonthlySpendPercent);
        var forecast = Assert.Single(data.Forecasts(At(60)));
        Assert.Equal(UsageWindowKinds.MonthSpend, forecast.Kind);
        Assert.True(forecast.ProjectedAtReset > 100);
    }

    [Fact]
    public void Codex_has_its_own_quieter_card()
    {
        var codex = new DemoData(DemoVariant.Plan, Start).Codex(At(120));
        Assert.Equal(CodexUsageProvider.Id, codex.ProviderId);
        Assert.Equal(UsageHealth.Ok, codex.Health);
        Assert.Equal([47.0, 18.0], codex.Windows.Select(w => w.PercentUsed));
    }

    [Fact]
    public void The_sample_data_contains_nothing_real()
    {
        var data = new DemoData(DemoVariant.Plan, Start);
        var texts = data.AllText(At(90)).Where(t => t.Length > 0).ToList();
        Assert.NotEmpty(texts);
        var real = new[] { Environment.UserName, Environment.MachineName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) }
            .Where(t => t.Length >= 3 && t != "you").ToList();
        foreach (var text in texts)
            foreach (var word in real)
                Assert.DoesNotContain(word, text, StringComparison.OrdinalIgnoreCase);
        Assert.All(data.Sessions(At(90)), s => Assert.StartsWith("/Users/you/dev/", s.WorkingDirectory));
        Assert.All(data.Records(At(90)), r => Assert.StartsWith("/Users/you/dev/", r.Cwd));
        var owners = data.Sessions(At(90)).Select(s => data.Git(s.WorkingDirectory)!).SelectMany(g => new[] { g.OriginOwner, g.UpstreamOwner })
            .OfType<string>().Distinct().Order();
        Assert.Equal(["acme", "acme-labs"], owners);
    }

    [Theory]
    [InlineData(new[] { "--demo" }, true, DemoVariant.Plan)]
    [InlineData(new[] { "--demo", "cap" }, true, DemoVariant.Cap)]
    [InlineData(new[] { "--DEMO", "CAP" }, true, DemoVariant.Cap)]
    [InlineData(new[] { "--demo", "plan" }, true, DemoVariant.Plan)]
    [InlineData(new[] { "--profile", "dev", "--demo" }, true, DemoVariant.Plan)]
    [InlineData(new[] { "--profile", "demo" }, false, DemoVariant.Plan)]
    [InlineData(new string[0], false, DemoVariant.Plan)]
    public void The_demo_is_asked_for_with_its_argument(string[] args, bool requested, DemoVariant variant)
    {
        Assert.Equal(requested, DemoMode.IsRequested(args, out var asked));
        Assert.Equal(variant, asked);
    }

}

public class DemoServicesTests
{
    [Fact]
    public async Task The_demo_graph_shows_the_script_and_raises_the_storyboard_s_alerts()
    {
        using var temp = new TempDir();
        var start = new DateTimeOffset(2026, 11, 20, 9, 30, 0, TimeSpan.Zero);
        var now = start;
        var data = new DemoData(DemoVariant.Plan, start);
        using var services = AppServices.CreateDemo(new AppSettings(), data, temp.Path, () => now);

        var told = new List<(int Second, AlertKind Kind, string Title, string Body)>();
        foreach (var second in new[] { 0, 30, 60, 75, 85, 100, 110, 120 })
        {
            now = start.AddSeconds(second);
            var summary = await services.Feed.GetSummaryAsync();
            Assert.Equal(data.FiveHourPercent(now), summary.Usage.FiveHourUtilizationPercent);
            Assert.Equal(data.Forecasts(now), summary.Forecasts);
            Assert.Equal(3, summary.Activities.Count);
            Assert.True(summary.HasPlugin);
            Assert.Equal("Codex", Assert.Single(summary.Others).Usage.DisplayName);
            Assert.Same(data.Sessions(now)[0].SessionId, summary.Session?.SessionId);
            told.AddRange(summary.Alerts.Select(a => (second, a.Kind, a.Title, a.Body)));
        }

        // The threshold (orange at 80%), the session that needs you (20 s after it asks) and the
        // "limit at" forecast once it is red, each once.
        Assert.Equal([AlertKind.Threshold, AlertKind.NeedsYou, AlertKind.ForecastHit], told.Select(t => t.Kind));
        Assert.All(told, t => Assert.True(t.Second >= 75, $"{t.Kind} at {t.Second} s"));
        Assert.Contains(told, t => t.Kind == AlertKind.NeedsYou && t.Body.Contains("acme-webapp", StringComparison.Ordinal));
        Assert.Contains(told, t => t.Kind == AlertKind.Threshold && t.Title == "Claude: 5-hour window at 80%");

        var cards = await services.Feed.GetCardsAsync(1);
        Assert.Equal(3, cards.Count);
        Assert.Equal(DemoData.CheckoutSession, cards[0].SessionId);
        // The cards carry the made-up repositories (git and gh never run on these folders).
        Assert.Equal(["feature/checkout-redesign", "test/refund-webhooks", "docs/onboarding"], cards.Select(c => c.GitBranch));
        Assert.Equal(3, (await services.Feed.GetTodaySessionsAsync()).Count);
    }

    [Fact]
    public async Task The_demo_graph_keeps_its_files_in_the_demo_s_folder()
    {
        using var temp = new TempDir();
        var now = DateTimeOffset.UtcNow;
        using (var services = AppServices.CreateDemo(new AppSettings(), DemoData.At(DemoVariant.Cap, now, TimeSpan.FromSeconds(90)),
                   temp.Path, () => now))
        {
            Assert.StartsWith(temp.Path, services.Transcripts.Root);
            var summary = await services.Feed.GetSummaryAsync();
            Assert.Equal(UsageMode.MonthlySpend, summary.Usage.Mode);
            Assert.NotNull(summary.SpentToday);
            await services.Feed.GetCardsAsync(2);
        }
        var written = Directory.EnumerateFiles(temp.Path, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(temp.Path, f).Replace('\\', '/')).Order().ToList();
        Assert.All(written, f => Assert.Matches("^demo-(cache|feed|projects)/", f));
        Assert.Contains("demo-cache/day-spend.json", written);
    }

    [Fact]
    public void The_demo_ticks_the_windows_while_it_climbs_and_then_stops()
    {
        using var temp = new TempDir();
        var start = DateTimeOffset.UtcNow;
        var now = start;
        using var services = AppServices.CreateDemo(new AppSettings(), new DemoData(DemoVariant.Plan, start), temp.Path, () => now);
        var ticks = 0;
        using var ticked = new SemaphoreSlim(0);
        services.UsageChanged += () =>
        {
            Interlocked.Increment(ref ticks);
            ticked.Release();
        };
        Assert.True(ticked.Wait(TimeSpan.FromSeconds(10)), "a tick while the demo climbs");
        now = start + DemoData.Climb + TimeSpan.FromMinutes(2);
        // The next tick sees the end and stops the timer: within a deadline (a busy test run can delay
        // a timer), two tick periods in a row pass without a tick.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        var quiet = false;
        while (!quiet && DateTime.UtcNow < deadline)
        {
            var before = Volatile.Read(ref ticks);
            Thread.Sleep(AppServices.DemoTick * 2.2);
            quiet = Volatile.Read(ref ticks) == before;
        }
        Assert.True(quiet, "the demo kept ticking after its climb");
    }
}

[Collection(SelfTestSandboxProcessTests.Name)]
public class DemoModeProcessTests
{
    [Fact]
    public async Task Start_moves_the_whole_app_into_a_temporary_profile_and_dispose_removes_it()
    {
        var real = AppPaths.Current;
        string root;
        DemoRun? started = null;
        try
        {
            using (var run = DemoMode.Start(DemoVariant.Cap, DateTimeOffset.UtcNow))
            {
                started = run;
                root = run.Sandbox.Root;
                Assert.Same(run, DemoMode.Active);
                Assert.StartsWith(DemoMode.FolderPrefix, Path.GetFileName(root));
                Assert.Equal(DemoVariant.Cap, run.Data.Variant);
                foreach (var folder in new[] { AppPaths.DataDirectory, AppPaths.FeedDirectory, AppPaths.ClaudeDirectory, AppPaths.CodexDirectory })
                {
                    Assert.True(run.Sandbox.Contains(folder), folder);
                    Assert.False(run.Sandbox.Contains(real.DataDirectory));
                }
                Assert.False(AppPaths.Current.IsDefaultInstance);
                var settings = SettingsService.Load();
                Assert.True(settings.OnboardingCompleted);
                Assert.Equal(Health.Onboarding.CurrentVersion, settings.OnboardingVersion);
                Assert.False(settings.StartWithWindows);
                Assert.Equal(Health.OnboardingScreen.None, Health.Onboarding.ScreenFor(settings, pluginConnected: false));

                // A window's services follow the demo: its cap, not an empty feed.
                using var host = new AppServicesHost(settings);
                Assert.True(run.Sandbox.Contains(host.Current.Transcripts.Root));
                var usage = (await host.Current.Feed.GetSummaryAsync()).Usage;
                Assert.Equal(UsageMode.MonthlySpend, usage.Mode);
                Assert.True(usage.MonthlySpendUsedUsd >= DemoData.CapFromUsd);
            }

            Assert.Null(DemoMode.Active);
            Assert.False(Directory.Exists(root));

            // The exit paths overlap (Main's using, the app's exit, the process's exit): a second call does nothing.
            started!.Dispose();
            Assert.Null(DemoMode.Active);
        }
        finally
        {
            AppPaths.Configure([]);
        }
    }
}
