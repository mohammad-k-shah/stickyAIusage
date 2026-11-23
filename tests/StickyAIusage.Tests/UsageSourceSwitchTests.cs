using System.Text.Json;
using System.Text.Json.Nodes;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Which source a new install, an upgrade and a broken settings file
/// start with, and that a new install never builds the usage endpoint.</summary>
public class UsageSourceDefaultsTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string SettingsPath => _dir.Combine("settings.json");

    /// <summary>What an older version leaves behind after reading the endpoint.</summary>
    private void WriteLegacyCache()
    {
        Directory.CreateDirectory(_dir.Combine("cache"));
        File.WriteAllText(_dir.Combine("cache", "claude-usage.json"), "{}");
    }

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NeverBuilt(Func<DateTimeOffset> _) =>
        throw new InvalidOperationException("The usage endpoint source must not be built for a new install.");

    [Fact]
    public void A_new_install_reads_the_plugin_only()
    {
        var settings = SettingsService.Load(SettingsPath);

        Assert.Equal("feed", settings.ClaudeSource);
        Assert.Equal(ClaudeSourceMode.Feed, ClaudeSourceModes.Parse(settings.ClaudeSource));
        Assert.Equal(AppSettings.NewInstallClaudeSource, new AppSettings().ClaudeSource);
    }

    [Fact]
    public async Task A_new_install_never_builds_the_usage_endpoint_or_reads_the_sign_in()
    {
        var settings = SettingsService.Load(SettingsPath);
        using var services = AppServices.Create(settings, new ClaudeSessionLogSource(_dir.Combine("projects")),
            _dir.Combine("feed"), watchFeed: false, NeverBuilt);

        var snapshot = await services.Claude.GetUsageAsync();

        Assert.Equal([ClaudeFeedUsageSource.Id], services.Claude.Sources.Select(s => s.SourceId));
        Assert.Equal(ClaudeFeedUsageSource.WaitingDetail, snapshot.StatusDetail);
    }

    [Fact]
    public void A_v1_file_keeps_reading_the_way_it_did()
    {
        File.WriteAllText(SettingsPath, """{ "Theme": "light", "IsRunning": true }""");

        var settings = SettingsService.Load(SettingsPath);

        Assert.Equal(AppSettings.UpgradeClaudeSource, settings.ClaudeSource);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void An_older_file_that_chose_a_source_keeps_it(int version)
    {
        File.WriteAllText(SettingsPath, $$"""{ "SchemaVersion": {{version}}, "ClaudeSource": "feed" }""");

        Assert.Equal("feed", SettingsService.Load(SettingsPath).ClaudeSource);
    }

    [Fact]
    public void A_current_file_without_a_source_is_a_new_install()
    {
        File.WriteAllText(SettingsPath, $$"""{ "SchemaVersion": {{AppSettings.CurrentSchemaVersion}}, "Theme": "light" }""");

        Assert.Equal(AppSettings.NewInstallClaudeSource, SettingsService.Load(SettingsPath).ClaudeSource);
    }

    [Fact]
    public void A_newer_file_without_a_source_is_left_to_its_own_defaults()
    {
        File.WriteAllText(SettingsPath, """{ "SchemaVersion": 9 }""");

        Assert.Equal(AppSettings.NewInstallClaudeSource, SettingsService.Load(SettingsPath).ClaudeSource);
    }

    [Fact]
    public void No_settings_file_beside_an_older_versions_usage_cache_is_an_upgrade()
    {
        // v1 on macOS wrote settings.json only once something changed.
        WriteLegacyCache();

        Assert.Equal(AppSettings.UpgradeClaudeSource, SettingsService.Load(SettingsPath).ClaudeSource);
    }

    [Fact]
    public void A_broken_file_without_a_backup_falls_back_by_the_same_rule()
    {
        File.WriteAllText(SettingsPath, "{ not json");
        Assert.Equal(AppSettings.NewInstallClaudeSource, SettingsService.Load(SettingsPath).ClaudeSource);

        WriteLegacyCache();
        Assert.Equal(AppSettings.UpgradeClaudeSource, SettingsService.Load(SettingsPath).ClaudeSource);
    }

    [Fact]
    public void An_upgrade_writes_its_source_down_on_the_next_save()
    {
        File.WriteAllText(SettingsPath, """{ "Theme": "light" }""");

        SettingsService.Save(SettingsService.Load(SettingsPath), SettingsPath);

        var saved = JsonNode.Parse(File.ReadAllText(SettingsPath))!;
        Assert.Equal("auto", saved["ClaudeSource"]!.GetValue<string>());
        Assert.Equal(AppSettings.CurrentSchemaVersion, saved["SchemaVersion"]!.GetValue<int>());
        // And it stays "auto" now that the file is the current format.
        Assert.Equal("auto", SettingsService.Load(SettingsPath).ClaudeSource);
    }

    [Theory]
    [InlineData(ClaudeSourceMode.Auto, "auto", true)]
    [InlineData(ClaudeSourceMode.Feed, "feed", false)]
    [InlineData(ClaudeSourceMode.Legacy, "legacy", true)]
    [InlineData(ClaudeSourceMode.Off, "off", false)]
    public void Modes_round_trip_and_say_whether_they_ask_the_usage_endpoint(ClaudeSourceMode mode, string setting, bool endpoint)
    {
        Assert.Equal(setting, mode.ToSetting());
        Assert.Equal(mode, ClaudeSourceModes.Parse(mode.ToSetting()));
        Assert.Equal(endpoint, mode.UsesUsageEndpoint());
    }

    [Fact]
    public void The_plugin_tip_stays_dismissed_across_a_restart()
    {
        var settings = SettingsService.Load(SettingsPath);
        Assert.False(settings.PluginTipDismissed);
        settings.PluginTipDismissed = true;
        SettingsService.Save(settings, SettingsPath);

        Assert.True(SettingsService.Load(SettingsPath).PluginTipDismissed);
    }
}

/// <summary>Changing the source in Settings rebuilds the sources between
/// refreshes, without a restart.</summary>
public class AppServicesHostTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly List<CountingEndpoint> _endpoints = [];

    public void Dispose() => _dir.Dispose();

    private sealed class CountingEndpoint : IClaudeUsageSource, IDisposable
    {
        public string SourceId => ClaudeOAuthUsageSource.Id;
        public SourceTrust Trust => SourceTrust.Unofficial;
        public int Calls { get; private set; }
        public bool Disposed { get; private set; }

        public Task<UsageSnapshot?> TryGetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<UsageSnapshot?>(new UsageSnapshot
            {
                Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 40, FetchedAt = DateTimeOffset.UtcNow
            });
        }

        public void Dispose() => Disposed = true;
    }

    private AppServicesHost Host(AppSettings settings, bool watchFeed = false) =>
        new(settings, s => AppServices.Create(s, new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed,
            _ =>
            {
                var endpoint = new CountingEndpoint();
                _endpoints.Add(endpoint);
                return (endpoint, [endpoint]);
            }));

    [Fact]
    public void Saving_without_changing_the_source_keeps_the_same_sources()
    {
        var settings = new AppSettings { ClaudeSource = "legacy" };
        using var host = Host(settings);
        var first = host.Current;

        settings.Theme = "light";
        Assert.False(host.SettingsChanged());
        Assert.False(host.ApplyPendingChange());

        Assert.Same(first, host.Current);
        Assert.Single(_endpoints);
    }

    [Fact]
    public async Task Turning_legacy_on_builds_the_endpoint_at_the_next_refresh()
    {
        var settings = new AppSettings { ClaudeSource = "feed" };
        using var host = Host(settings);
        Assert.Empty(_endpoints);

        settings.ClaudeSource = "legacy";
        Assert.True(host.SettingsChanged());
        // Nothing changes until the window's next refresh starts.
        Assert.Equal(ClaudeSourceMode.Feed, host.Current.Mode);
        Assert.True(host.ApplyPendingChange());

        Assert.Equal(ClaudeSourceMode.Legacy, host.Current.Mode);
        var snapshot = await host.Current.Claude.GetUsageAsync();
        Assert.Equal(40, snapshot.FiveHourUtilizationPercent);
        Assert.Equal(1, Assert.Single(_endpoints).Calls);
    }

    [Fact]
    public async Task Turning_legacy_off_disposes_the_endpoint_and_never_asks_it_again()
    {
        var settings = new AppSettings { ClaudeSource = "auto" };
        using var host = Host(settings);
        var endpoint = Assert.Single(_endpoints);

        settings.ClaudeSource = "feed";
        host.SettingsChanged();
        host.ApplyPendingChange();
        await host.Current.Claude.GetUsageAsync();

        Assert.True(endpoint.Disposed);
        Assert.Equal(0, endpoint.Calls);
        Assert.Single(_endpoints);
        Assert.Equal([ClaudeFeedUsageSource.Id], host.Current.Claude.Sources.Select(s => s.SourceId));
    }

    [Fact]
    public void A_change_and_its_undo_before_the_next_refresh_rebuild_nothing()
    {
        var settings = new AppSettings { ClaudeSource = "feed" };
        using var host = Host(settings);
        var first = host.Current;

        settings.ClaudeSource = "off";
        host.SettingsChanged();
        settings.ClaudeSource = "feed";
        Assert.False(host.SettingsChanged());

        Assert.False(host.ApplyPendingChange());
        Assert.Same(first, host.Current);
    }

    [Fact]
    public void The_card_layout_survives_a_rebuild()
    {
        var settings = new AppSettings { ClaudeSource = "feed" };
        using var host = Host(settings);
        var layout = CardLayout.Parse("model|cost", showContextBar: false, showGitDetails: false);
        host.Current.Feed.Layout = layout;

        settings.ClaudeSource = "off";
        host.SettingsChanged();
        host.ApplyPendingChange();

        Assert.Same(layout, host.Current.Feed.Layout);
    }

    [Fact]
    public void A_source_that_fails_to_start_leaves_the_old_one_running_and_retries_on_the_next_save()
    {
        var settings = new AppSettings { ClaudeSource = "legacy" };
        var builds = 0;
        using var host = new AppServicesHost(settings, s =>
        {
            if (++builds == 2)
                throw new IOException("feed folder not writable");
            return AppServices.Create(s, new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false,
                _ => (new CountingEndpoint(), []));
        });
        var first = host.Current;

        settings.ClaudeSource = "feed";
        host.SettingsChanged();
        Assert.False(host.ApplyPendingChange());
        Assert.Same(first, host.Current);
        Assert.Equal(ClaudeSourceMode.Legacy, host.Current.Mode);

        Assert.True(host.SettingsChanged());
        Assert.True(host.ApplyPendingChange());
        Assert.Equal(ClaudeSourceMode.Feed, host.Current.Mode);
    }

    [Fact]
    public void Disposing_the_host_disposes_the_current_sources()
    {
        var host = Host(new AppSettings { ClaudeSource = "legacy" });

        host.Dispose();

        Assert.True(Assert.Single(_endpoints).Disposed);
    }

    [Fact]
    public async Task Feed_changes_reach_the_window_from_the_current_sources_only()
    {
        var settings = new AppSettings { ClaudeSource = "feed" };
        using var host = Host(settings, watchFeed: true);
        var changes = 0;
        host.UsageChanged += () => Interlocked.Increment(ref changes);

        // Off still watches the folder (the session lights), but only the new
        // graph's watcher: one write, one change, not one from each.
        settings.ClaudeSource = "off";
        host.SettingsChanged();
        host.ApplyPendingChange();
        WriteRecord("a");
        var first = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref changes) == 0 && DateTime.UtcNow < first)
            await Task.Delay(50);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(1, Volatile.Read(ref changes)); // the old watcher is gone
        Interlocked.Exchange(ref changes, 0);

        settings.ClaudeSource = "feed";
        host.SettingsChanged();
        host.ApplyPendingChange();
        WriteRecord("b");
        var until = DateTime.UtcNow.AddSeconds(10);
        while (Volatile.Read(ref changes) == 0 && DateTime.UtcNow < until)
            await Task.Delay(50);
        Assert.True(Volatile.Read(ref changes) > 0);
    }

    private void WriteRecord(string session)
    {
        Directory.CreateDirectory(_dir.Combine("feed"));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(_dir.Combine("feed", $"claude-{session}.json"), JsonSerializer.Serialize(new
        {
            schema = 1, sessionId = session, observedAt = now, rateLimitsAt = now,
            rateLimits = new[] { new { kind = "five_hour", percentUsed = 10.0 } }
        }));
    }
}

/// <summary>The note at the top of the flyout, in every layout.</summary>
public class FlyoutNoticeTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 17, 12, 0, 0, TimeSpan.Zero);

    private static WidgetViewModel View(UsageSnapshot usage, AppSettings? settings = null) =>
        WidgetViewModel.From(usage, null, settings ?? new AppSettings(), Now);

    private static UsageSnapshot Plan(string source) => new()
    {
        Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 40, WeeklyUtilizationPercent = 10,
        FetchedAt = Now, SourceId = source
    };

    private static UsageSnapshot Monthly(string source) => new()
    {
        Mode = UsageMode.MonthlySpend, HasMonthlySpend = true, MonthlySpendUsedUsd = 40, MonthlySpendLimitUsd = 100,
        MonthlySpendPercent = 40, FetchedAt = Now, SourceId = source
    };

    [Fact]
    public void Only_the_notices_about_the_plugin_offer_connect()
    {
        Assert.True(FlyoutNotice.InstallPlugin.CanConnect);
        Assert.True(FlyoutNotice.PluginTip.CanConnect);
        Assert.False(FlyoutNotice.SignIn.CanConnect);
        Assert.Equal("Connect…", FlyoutNotice.ConnectLabel);
    }

    [Fact]
    public void An_expired_sign_in_asks_to_sign_in_again_in_every_layout()
    {
        var unknown = new UsageSnapshot { RequiresReauth = true, IsStale = true, FetchedAt = Now };
        var plan = Plan(ClaudeOAuthUsageSource.Id) with { RequiresReauth = true, IsStale = true };
        var monthly = Monthly(ClaudeOAuthUsageSource.Id) with { RequiresReauth = true, IsStale = true };

        foreach (var (usage, mode) in new[] { (unknown, UsageMode.Unknown), (plan, UsageMode.PlanLimits), (monthly, UsageMode.MonthlySpend) })
        {
            var view = View(usage);
            Assert.Equal(mode, view.Mode);
            Assert.Same(FlyoutNotice.SignIn, view.Notice);
        }
        Assert.Equal(ClaudeCommands.SignIn, FlyoutNotice.SignIn.CopyText);
        Assert.False(FlyoutNotice.SignIn.CanDismiss);
    }

    [Fact]
    public void An_empty_feed_offers_the_plugin_install_command()
    {
        var waiting = new UsageSnapshot
        {
            IsStale = true, FetchedAt = Now, SourceId = ClaudeFeedUsageSource.Id, StatusDetail = ClaudeFeedUsageSource.WaitingDetail
        };

        var view = View(waiting);

        Assert.Equal(UsageState.Unavailable, view.State);
        Assert.Equal(WidgetViewModel.WaitingText, view.PrimaryText);
        Assert.Equal(ClaudeFeedUsageSource.WaitingDetail, view.UsageTooltip);
        Assert.Same(FlyoutNotice.InstallPlugin, view.Notice);
        Assert.Equal(ClaudeCommands.InstallPlugin, view.Notice!.CopyText);
    }

    [Theory]
    [InlineData(ClaudeFeedUsageSource.NoLimitsDetail)]
    [InlineData(AppServices.UsageOffDetail)]
    [InlineData(null)]
    public void Other_empty_readings_have_no_notice(string? detail)
    {
        var view = View(new UsageSnapshot { IsStale = true, FetchedAt = Now, StatusDetail = detail });

        Assert.Null(view.Notice);
        Assert.Equal("Usage unavailable", view.PrimaryText);
    }

    [Fact]
    public void Plan_limits_from_the_usage_endpoint_carry_the_plugin_tip_until_dismissed()
    {
        Assert.Same(FlyoutNotice.PluginTip, View(Plan(ClaudeOAuthUsageSource.Id)).Notice);
        Assert.True(FlyoutNotice.PluginTip.CanDismiss);

        Assert.Null(View(Plan(ClaudeOAuthUsageSource.Id), new AppSettings { PluginTipDismissed = true }).Notice);
    }

    [Fact]
    public void Plan_limits_from_the_feed_and_monthly_caps_have_no_tip()
    {
        Assert.Null(View(Plan(ClaudeFeedUsageSource.Id)).Notice);
        // A monthly cap needs the usage endpoint: the plugin can't replace it.
        Assert.Null(View(Monthly(ClaudeOAuthUsageSource.Id)).Notice);
    }
}

/// <summary>The words of Settings → Usage source.</summary>
public class UsageSourceOptionsTests
{
    [Fact]
    public void Every_mode_is_offered_once_with_the_plugin_first()
    {
        Assert.Equal([ClaudeSourceMode.Feed, ClaudeSourceMode.Auto, ClaudeSourceMode.Legacy, ClaudeSourceMode.Off],
            UsageSourceOptions.All.Select(o => o.Mode));
        foreach (var mode in Enum.GetValues<ClaudeSourceMode>())
            Assert.Equal(mode, UsageSourceOptions.For(mode).Mode);
    }

    [Fact]
    public void Exactly_the_modes_that_read_the_sign_in_carry_the_unofficial_warning()
    {
        foreach (var option in UsageSourceOptions.All)
            Assert.Equal(option.Mode.UsesUsageEndpoint(), option.Warning is not null);
        Assert.All(UsageSourceOptions.All, o => Assert.False(string.IsNullOrWhiteSpace(o.Description)));
    }

    [Fact]
    public void The_install_line_shows_only_for_the_modes_that_read_the_plugin()
    {
        Assert.True(UsageSourceOptions.ShowsPluginInstall(ClaudeSourceMode.Feed));
        Assert.True(UsageSourceOptions.ShowsPluginInstall(ClaudeSourceMode.Auto));
        Assert.False(UsageSourceOptions.ShowsPluginInstall(ClaudeSourceMode.Legacy));
        Assert.False(UsageSourceOptions.ShowsPluginInstall(ClaudeSourceMode.Off));
        // Already connected: nothing to install (seen on a Mac: the command stayed on show).
        Assert.False(UsageSourceOptions.ShowsPluginInstall(ClaudeSourceMode.Feed, pluginConnected: true));
        Assert.False(UsageSourceOptions.ShowsPluginInstall(ClaudeSourceMode.Auto, pluginConnected: true));
    }

    [Fact]
    public void The_install_command_names_the_plugin_and_marketplace_this_repository_ships()
    {
        var root = RepositoryRoot();
        var plugin = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "plugin", ".claude-plugin", "plugin.json")))!;
        var marketplace = JsonNode.Parse(File.ReadAllText(Path.Combine(root, ".claude-plugin", "marketplace.json")))!;
        var listed = marketplace["plugins"]!.AsArray().Select(p => p!["name"]!.GetValue<string>());

        Assert.StartsWith($"claude plugin install {plugin["name"]!.GetValue<string>()} --marketplace ", ClaudeCommands.InstallPlugin);
        Assert.Contains(plugin["name"]!.GetValue<string>(), listed);
        // The README tells people the same command.
        Assert.Contains(ClaudeCommands.InstallPlugin, File.ReadAllText(Path.Combine(root, "README.md")));
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "StickyAIusage.sln")))
                return dir.FullName;
        }
        throw new DirectoryNotFoundException("The repository root (StickyAIusage.sln) is not above the test folder.");
    }
}
