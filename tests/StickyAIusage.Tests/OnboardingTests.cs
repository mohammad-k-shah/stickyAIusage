using StickyAIusage.Health;
using StickyAIusage.Models;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>The first-run window: who sees which screen (and only once), the
/// settings migration that decides it, and the Connect step's states.</summary>
public class OnboardingTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string SettingsPath => _dir.Combine("settings.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_new_install_sees_the_three_steps_until_it_finishes_or_closes_them()
    {
        var settings = SettingsService.Load(SettingsPath); // no file: a first start
        Assert.Equal(OnboardingScreen.FirstRun, Onboarding.ScreenFor(settings, pluginConnected: false));
        // Even with the plugin already writing (installed for another copy), a new install gets the intro.
        Assert.Equal(OnboardingScreen.FirstRun, Onboarding.ScreenFor(settings, pluginConnected: true));

        Assert.True(Onboarding.Complete(settings));
        SettingsService.Save(settings, SettingsPath);
        var again = SettingsService.Load(SettingsPath);
        Assert.Equal((true, Onboarding.CurrentVersion), (again.OnboardingCompleted, again.OnboardingVersion));
        Assert.Equal(OnboardingScreen.None, Onboarding.ScreenFor(again, pluginConnected: false));
        Assert.False(Onboarding.Complete(again)); // nothing left to save
    }

    [Theory]
    [InlineData("""{ "Theme": "dark", "ShowMonthlySpend": true }""")] // v1: no version key
    [InlineData("""{ "SchemaVersion": 3, "ClaudeSource": "auto" }""")]
    [InlineData("""{ "SchemaVersion": 4, "ClaudeSource": "feed", "Theme": "system" }""")]
    public void An_upgrade_sees_the_short_screen_once(string olderFile)
    {
        File.WriteAllText(SettingsPath, olderFile);
        var settings = SettingsService.Load(SettingsPath);
        Assert.Equal(AppSettings.CurrentSchemaVersion, settings.SchemaVersion);
        Assert.Equal(OnboardingScreen.WhatsNew, Onboarding.ScreenFor(settings, pluginConnected: false));

        Onboarding.Complete(settings);
        SettingsService.Save(settings, SettingsPath);
        Assert.Equal(OnboardingScreen.None, Onboarding.ScreenFor(SettingsService.Load(SettingsPath), pluginConnected: false));
    }

    [Fact]
    public void An_upgrade_whose_plugin_already_writes_has_nothing_new_to_hear()
    {
        File.WriteAllText(SettingsPath, """{ "SchemaVersion": 4, "ClaudeSource": "feed" }""");
        Assert.Equal(OnboardingScreen.None, Onboarding.ScreenFor(SettingsService.Load(SettingsPath), pluginConnected: true));
    }

    [Fact]
    public void A_data_folder_with_an_older_versions_cache_but_no_settings_is_an_upgrade()
    {
        Directory.CreateDirectory(_dir.Combine("cache"));
        File.WriteAllText(_dir.Combine("cache", "claude-usage.json"), "{}");
        Assert.Equal(OnboardingScreen.WhatsNew, Onboarding.ScreenFor(SettingsService.Load(SettingsPath), pluginConnected: false));
    }

    [Fact]
    public void A_file_of_this_format_keeps_its_answer()
    {
        File.WriteAllText(SettingsPath, $$"""{ "SchemaVersion": {{AppSettings.CurrentSchemaVersion}}, "OnboardingCompleted": false }""");
        Assert.Equal(OnboardingScreen.FirstRun, Onboarding.ScreenFor(SettingsService.Load(SettingsPath), pluginConnected: false));
        // A newer build that raised the version shows its "what's new" to someone who finished version 1.
        var finishedOne = new AppSettings { OnboardingCompleted = true, OnboardingVersion = Onboarding.CurrentVersion };
        Assert.Equal(OnboardingScreen.None, Onboarding.ScreenFor(finishedOne, pluginConnected: false));
        var finishedAnOlderOne = new AppSettings { OnboardingCompleted = true, OnboardingVersion = Onboarding.CurrentVersion - 1 };
        Assert.Equal(OnboardingScreen.WhatsNew, Onboarding.ScreenFor(finishedAnOlderOne, pluginConnected: false));
    }

    [Fact]
    public void The_copy_hint_names_the_desktop_code_tabs_own_way()
    {
        var hint = Onboarding.CopyHint("o/r");
        Assert.Contains("The line above runs in a terminal", hint);
        Assert.Contains("copies /plugin install sticky-usage --marketplace o/r instead, to paste into a Claude Code session", hint);
        Assert.Contains("Plugins, Add marketplace o/r, then Add plugin", hint);
        Assert.Equal("Copy for Claude Code", Onboarding.CopyLabel);
    }

    [Fact]
    public void The_window_is_never_captioned_like_the_widget()
    {
        foreach (var screen in new[] { OnboardingScreen.FirstRun, OnboardingScreen.WhatsNew, OnboardingScreen.Connect })
        {
            Assert.NotEqual("Sticky AI Usage", Onboarding.Caption(screen));
            Assert.False(string.IsNullOrWhiteSpace(Onboarding.Heading(screen)));
        }
        Assert.Equal("Sticky AI Usage - What's new", Onboarding.Caption(OnboardingScreen.WhatsNew));
        Assert.Equal(Onboarding.WhatsNewTitle, Onboarding.Heading(OnboardingScreen.WhatsNew));
    }

    // ------------------------------------------------------------------ Connect

    private const string Shell = "claude plugin install sticky-usage --marketplace o/r";
    private const string Session = "/plugin install sticky-usage --marketplace o/r";

    private static ConnectView View(bool installing = false, InstallResult? result = null, bool copied = false,
        UsageSnapshot? usage = null, bool pluginWrote = false) =>
        ConnectFlow.View(installing, result, copied, usage, pluginWrote, Shell, Session);

    private static readonly UsageSnapshot FeedNumbers = new()
    {
        FiveHourUtilizationPercent = 43.2, WeeklyUtilizationPercent = 12, SourceId = ClaudeFeedUsageSource.Id, Trust = SourceTrust.Official
    };

    [Fact]
    public void Connect_goes_from_ready_to_installing_to_waiting_to_connected()
    {
        Assert.Equal(ConnectPhase.Ready, View().Phase);
        Assert.Equal((ConnectPhase.Installing, false), (View(installing: true).Phase, View(installing: true).CanRun));
        var installed = new InstallResult(InstallOutcome.Installed, "Installed.", "> claude …");
        Assert.Equal((ConnectPhase.Waiting, ConnectFlow.InstalledWaiting), (View(result: installed).Phase, View(result: installed).Status));
        Assert.Equal(ConnectPhase.Waiting, View(result: installed with { Outcome = InstallOutcome.AlreadyInstalled }).Phase);
        Assert.Equal((ConnectPhase.Waiting, ConnectFlow.CopiedWaiting), (View(copied: true).Phase, View(copied: true).Status));

        // The first record (limits still empty at session start), then the numbers.
        Assert.Equal((ConnectPhase.ConnectedNoNumbers, true), (View(result: installed, pluginWrote: true).Phase, View(pluginWrote: true).IsDone));
        var connected = View(result: installed, pluginWrote: true, usage: FeedNumbers);
        Assert.Equal((ConnectPhase.Connected, "Connected: 5-hour 43% · weekly 12%."), (connected.Phase, connected.Status));
    }

    [Fact]
    public void Numbers_from_the_usage_service_are_not_the_plugin_connecting()
    {
        var legacy = FeedNumbers with { SourceId = ClaudeOAuthUsageSource.Id, Trust = SourceTrust.Unofficial };
        Assert.Equal(ConnectPhase.Ready, View(usage: legacy).Phase);
        Assert.Equal(ConnectPhase.Ready, View(usage: new UsageSnapshot { SourceId = ClaudeFeedUsageSource.Id }).Phase);
    }

    [Theory]
    [InlineData(InstallOutcome.Policy, "organization may not allow user plugins")]
    [InlineData(InstallOutcome.Offline, "couldn't reach GitHub")]
    [InlineData(InstallOutcome.NotFound, "couldn't find the plugin")]
    [InlineData(InstallOutcome.Failed, "The install didn't work")]
    public void A_failure_shows_the_output_and_the_manual_steps(InstallOutcome outcome, string says)
    {
        var view = View(result: new InstallResult(outcome, "Claude Code said no.", "> claude plugin install …\nerror"));
        Assert.Equal(ConnectPhase.Failed, view.Phase);
        Assert.Contains(says, view.Status);
        Assert.Contains("Claude Code said no.", view.Status);
        Assert.Contains($"run {Shell} in a terminal, or paste {Session} into Claude Code", view.Status);
        Assert.Equal("> claude plugin install …\nerror", view.Output);
        Assert.True(view.CanRun); // try again
    }

    [Fact]
    public void A_failure_writes_the_home_folder_as_a_tilde()
    {
        // Seen on a Mac: "Marketplace file not found at /Users/<name>/.claude/plugins/…".
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = Path.Combine(home, ".claude", "plugins", "marketplaces", "x", "marketplace.json");
        var view = View(result: new InstallResult(InstallOutcome.NotFound, $"Marketplace file not found at {path}", $"> install\n{path}"));
        Assert.DoesNotContain(home, view.Status);
        Assert.DoesNotContain(home, view.Output!);
        Assert.Contains(Path.Combine("~", ".claude", "plugins"), view.Status);
    }

    [Fact]
    public void Claude_codes_words_end_with_a_full_stop_before_ours()
    {
        var view = View(result: new InstallResult(InstallOutcome.Policy, "error: managed settings do not allow this marketplace", ""));
        Assert.StartsWith("Claude Code refused: error: managed settings do not allow this marketplace. Your organization", view.Status);
        Assert.Null(view.Output);
        var asked = View(result: new InstallResult(InstallOutcome.Failed, "Is it?", ""));
        Assert.StartsWith("The install didn't work: Is it? To do it by hand", asked.Status);
    }

    [Fact]
    public void Without_the_command_line_only_the_copy_path_is_named()
    {
        var view = View(result: new InstallResult(InstallOutcome.NoCli, "Claude Code's command line wasn't found on this computer.", ""));
        Assert.Equal(ConnectPhase.Failed, view.Phase);
        Assert.Equal("Claude Code's command line wasn't found on this computer.", view.Status);
        Assert.Null(view.Output);
    }

    private sealed class OneAnswerCli(CliRun install) : IClaudeCli
    {
        public int Runs;
        public TaskCompletionSource Gate { get; } = new();

        public async Task<CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Runs++;
            if (arguments[0] == "--version")
                return new CliRun(0, "2.1.325 (Claude Code)", "");
            await Gate.Task;
            return install;
        }
    }

    [Fact]
    public async Task A_session_runs_once_at_a_time_and_repaints_at_each_step()
    {
        UsageSnapshot? usage = null;
        var wrote = false;
        var cli = new OneAnswerCli(new CliRun(0, """{"outcome":"ok","message":"Successfully installed plugin"}""", ""));
        var session = new ConnectSession(() => usage, () => wrote, cli, "claude", "o/r");
        var phases = new List<ConnectPhase>();
        session.Changed += () => phases.Add(session.View.Phase);

        var run = session.RunAsync();
        var second = session.RunAsync(); // ignored while the first runs: done at once
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ConnectPhase.Installing, session.View.Phase);
        cli.Gate.SetResult();
        await run;
        Assert.Equal(2, cli.Runs); // --version and one install
        Assert.Equal(ConnectPhase.Waiting, session.View.Phase);

        wrote = true;
        session.Refresh();
        usage = FeedNumbers;
        session.Refresh();
        Assert.Equal([ConnectPhase.Installing, ConnectPhase.Waiting, ConnectPhase.ConnectedNoNumbers, ConnectPhase.Connected], phases);
        Assert.Equal("claude plugin install sticky-usage --marketplace o/r", session.ShellCommand);
        Assert.Equal("/plugin install sticky-usage --marketplace o/r", session.SessionCommand);
    }

    [Fact]
    public void A_feed_folder_that_cannot_be_read_is_not_connected()
    {
        var session = new ConnectSession(() => null, () => throw new UnauthorizedAccessException(), new OneAnswerCli(new CliRun(0, "", "")), null, "o/r");
        Assert.False(session.HasCli);
        Assert.Equal(ConnectPhase.Ready, session.View.Phase);
        session.MarkCopied();
        Assert.Equal(ConnectPhase.Waiting, session.View.Phase);
    }

    [Fact]
    public void A_spend_cap_from_the_plugin_reads_as_connected_too()
    {
        var spend = new UsageSnapshot { HasMonthlySpend = true, MonthlySpendPercentOnly = true, MonthlySpendPercent = 63, SourceId = ClaudeFeedUsageSource.Id };
        Assert.Equal("Connected: 63% of your spend limit.", View(usage: spend).Status);
    }
}
