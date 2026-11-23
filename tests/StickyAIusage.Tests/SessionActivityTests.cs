using System.Globalization;
using System.IO;
using StickyAIusage.Alerts;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>The session lights: the plugin's records → activities, the pill's
/// dots, the cards' chips and order, and the "needs you" / "finished" alerts.</summary>
public class SessionActivityTests
{
    private static readonly TimeZoneInfo Zone = TimeZoneInfo.Utc;
    private static readonly CultureInfo EnUs = CultureInfo.GetCultureInfo("en-US");
    private static readonly DateTimeOffset T0 = new(2026, 11, 18, 14, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Reset = T0.AddHours(2.5); // 4:30 PM

    private static FeedSession Record(string id, string? state, DateTimeOffset since, DateTimeOffset? heard = null,
        FeedAttention? attention = null, string? cwd = "C:\\work\\acme-webapp", TimeSpan? lastTurn = null) =>
        new(id, "terminal", cwd, "claude-opus-5-5", "2.1.325", "2.2.0", heard ?? T0, since, heard ?? T0,
            [new UsageWindow(UsageWindowKinds.FiveHour, 60, Reset, TimeSpan.FromHours(5))],
            null, null, null, null, state, since, attention, lastTurn);

    private static SessionActivity Only(FeedSession record, DateTimeOffset now) =>
        Assert.Single(SessionActivities.From([record], now));

    // ------------------------------------------------------------- reading

    private static FeedSession Parsed(string extra)
    {
        var text = "{\"schema\":1,\"sessionId\":\"s1\",\"observedAt\":1794000000000,\"state\":\"needs_you\"," +
                   "\"stateSince\":1793999990000" + extra + "}";
        var result = ClaudeFeedUsageSource.Parse(text, new FileInfo("claude-s1.json"));
        return Assert.IsType<FeedSession>(result.Session);
    }

    [Fact]
    public void The_feed_reads_what_a_session_waits_for_and_how_long_its_turn_took()
    {
        var session = Parsed(",\"attention\":{\"type\":\"permission\",\"tool\":\"Bash\"},\"lastTurnMs\":87000");
        Assert.Equal(new FeedAttention("permission", "Bash"), session.Attention);
        Assert.Equal(TimeSpan.FromSeconds(87), session.LastTurn);
        Assert.Equal(new FeedAttention("question", null), Parsed(",\"attention\":{\"type\":\"question\"}").Attention);
        Assert.Equal(new FeedAttention("input", null), Parsed(",\"attention\":\"input\"").Attention);
    }

    [Theory]
    [InlineData(",\"attention\":null")]
    [InlineData(",\"attention\":{\"tool\":\"Bash\"}")]
    [InlineData(",\"attention\":7")]
    [InlineData(",\"attention\":[\"permission\"]")]
    [InlineData(",\"lastTurnMs\":-5")]
    [InlineData(",\"lastTurnMs\":\"long\"")]
    [InlineData("")]
    public void Odd_attention_or_turn_values_read_as_none(string extra)
    {
        var session = Parsed(extra);
        Assert.Null(session.Attention);
        Assert.Null(session.LastTurn);
    }

    [Fact]
    public void Long_attention_values_are_cut()
    {
        var session = Parsed($",\"attention\":{{\"type\":\"{new string('t', 50)}\",\"tool\":\"{new string('x', 100)}\"}}");
        Assert.Equal(32, session.Attention!.Type.Length);
        Assert.Equal(64, session.Attention.Tool!.Length);
    }

    [Fact]
    public void The_plugin_counts_as_seen_by_its_marker_file()
    {
        using var dir = new TempDir();
        using var source = new ClaudeFeedUsageSource(dir.Path, () => T0);
        Assert.False(source.PluginSeen);
        File.WriteAllText(Path.Combine(dir.Path, ClaudeFeedUsageSource.MarkerFileName), "{}");
        Assert.True(source.PluginSeen);
    }

    // ------------------------------------------------------------- mapping

    [Theory]
    [InlineData("working", ActivityState.Working)]
    [InlineData("needs_you", ActivityState.NeedsYou)]
    [InlineData("done", ActivityState.Done)]
    [InlineData("idle", ActivityState.Idle)]
    [InlineData("started", ActivityState.Idle)]
    [InlineData("rate_limited", ActivityState.RateLimited)]
    [InlineData("NEEDS_YOU", ActivityState.NeedsYou)]
    [InlineData("thinking_hard", ActivityState.Working)] // a later plugin's state reads as busy
    [InlineData(null, ActivityState.Idle)]
    public void Each_state_word_maps(string? word, ActivityState expected) =>
        Assert.Equal(expected, Only(Record("s", word, T0.AddMinutes(-1)), T0).State);

    [Fact]
    public void Ended_sessions_and_records_without_an_id_are_left_out()
    {
        var list = SessionActivities.From([Record("a", "ended", T0), Record("", "working", T0), Record("b", "working", T0)], T0);
        Assert.Equal("b", Assert.Single(list).SessionId);
    }

    [Fact]
    public void The_project_is_the_folder_name_as_the_cards_show_it()
    {
        Assert.Equal("acme-webapp", Only(Record("s", "working", T0), T0).Project);
        Assert.Equal("api", Only(Record("s", "working", T0, cwd: "/home/me/api/"), T0).Project);
        Assert.Equal("Claude Code", Only(Record("s", "working", T0, cwd: null), T0).Project);
    }

    [Fact]
    public void A_busy_session_whose_record_stopped_has_no_signal()
    {
        var heard = T0.AddMinutes(-5);
        var silent = Only(Record("s", "working", T0.AddMinutes(-20), heard), T0);
        Assert.Equal(ActivityState.NoSignal, silent.State);
        Assert.Equal("acme-webapp — no signal for 5m", silent.Line(T0, Zone, EnUs));
        // Within the heartbeat's reach it still works.
        Assert.Equal(ActivityState.Working, Only(Record("s", "working", T0.AddMinutes(-20), T0.AddMinutes(-3.9)), T0).State);
        // A waiting session goes silent the same way; a done one doesn't (it has nothing to say).
        Assert.Equal(ActivityState.NoSignal, Only(Record("s", "needs_you", T0.AddMinutes(-20), heard), T0).State);
        Assert.Equal(ActivityState.Done, Only(Record("s", "done", T0.AddMinutes(-20), heard), T0).State);
    }

    [Fact]
    public void Needs_you_comes_first_longest_wait_first_then_working_then_the_rest_newest_first()
    {
        var list = SessionActivities.From([
            Record("done-old", "done", T0.AddMinutes(-9)),
            Record("work", "working", T0.AddMinutes(-3)),
            Record("wait-new", "needs_you", T0.AddMinutes(-1)),
            Record("limited", "rate_limited", T0.AddMinutes(-2)),
            Record("done-new", "done", T0.AddMinutes(-2)),
            Record("wait-old", "needs_you", T0.AddMinutes(-4)),
            Record("silent", "working", T0.AddMinutes(-30), T0.AddMinutes(-6)),
        ], T0);
        Assert.Equal(["wait-old", "wait-new", "work", "limited", "silent", "done-new", "done-old"], list.Select(a => a.SessionId));
    }

    [Fact]
    public void A_needs_you_record_without_attention_waits_for_input()
    {
        var activity = Only(Record("s", "needs_you", T0.AddMinutes(-1)), T0);
        Assert.Equal("Needs you: input", activity.Chip(T0));
    }

    // -------------------------------------------------------------- words

    [Fact]
    public void The_chip_and_the_tooltip_line_say_what_and_how_long()
    {
        var permission = Only(Record("s", "needs_you", T0.AddMinutes(-2), attention: new FeedAttention("permission", "Bash")), T0);
        Assert.Equal("Needs you: permission", permission.Chip(T0));
        Assert.Equal("acme-webapp — waiting for permission to use Bash (2m)", permission.Line(T0));

        var question = Only(Record("s", "needs_you", T0.AddSeconds(-30), attention: new FeedAttention("question", null)), T0);
        Assert.Equal("Needs you: a question", question.Chip(T0));
        Assert.Equal("acme-webapp — waiting for your answer to a question (30s)", question.Line(T0));

        var mcp = Only(Record("s", "needs_you", T0, attention: new FeedAttention("permission", "mcp__github__create_issue")), T0);
        Assert.Equal("acme-webapp — waiting for permission to use github create_issue (0s)", mcp.Line(T0));

        Assert.Equal("Working", Only(Record("s", "working", T0.AddMinutes(-5)), T0).Chip(T0));
        Assert.Equal("acme-webapp — working (5m)", Only(Record("s", "working", T0.AddMinutes(-5)), T0).Line(T0));
        Assert.Equal("Done 3m ago", Only(Record("s", "done", T0.AddMinutes(-3)), T0).Chip(T0));
        Assert.Equal("Idle 3m ago", Only(Record("s", "idle", T0.AddMinutes(-3)), T0).Chip(T0));
        Assert.Equal("Rate limited · resets 4:30 PM", Only(Record("s", "rate_limited", T0), T0).Chip(T0, Zone, EnUs));
        Assert.Equal("acme-webapp — rate limited, resets 4:30 PM", Only(Record("s", "rate_limited", T0), T0).Line(T0, Zone, EnUs));
        Assert.Equal("No signal", Only(Record("s", "working", T0, T0.AddMinutes(-9)), T0).Chip(T0));
    }

    [Fact]
    public void A_rate_limit_whose_reset_passed_says_no_time()
    {
        var limited = Only(Record("s", "rate_limited", T0, Reset.AddMinutes(1)), Reset.AddMinutes(1));
        Assert.Equal(ActivityState.RateLimited, limited.State);
        Assert.Equal("Rate limited", limited.Chip(Reset.AddMinutes(1), Zone, EnUs));
    }

    // -------------------------------------------------------------- lights

    [Fact]
    public void Three_sessions_show_three_dots_in_their_states()
    {
        var lights = SessionLights.From(SessionActivities.From([
            Record("a", "working", T0.AddMinutes(-3)),
            Record("b", "needs_you", T0.AddMinutes(-1), attention: new FeedAttention("permission", "Bash")),
            Record("c", "done", T0.AddMinutes(-2)),
        ], T0), T0, enabled: true);

        Assert.NotNull(lights);
        Assert.Equal([LightKind.NeedsYou, LightKind.Working, LightKind.Done], lights!.Dots);
        Assert.Null(lights.MoreText);
        Assert.Equal(LightKind.NeedsYou, lights.CompactDot);
        Assert.Equal((1, 1, 1), (lights.NeedsYou, lights.Working, lights.Done));
        Assert.Equal("1 needs you, 1 working, 1 done", lights.Summary);
        Assert.Equal(
            "acme-webapp — waiting for permission to use Bash (1m)\nacme-webapp — working (3m)\nacme-webapp — done 2m ago",
            lights.Tooltip);
    }

    [Fact]
    public void The_spoken_summary_names_every_kind_of_light()
    {
        var lights = SessionLights.From(SessionActivities.From([
            Record("a", "rate_limited", T0), Record("b", "working", T0.AddMinutes(-9), T0.AddMinutes(-5)),
        ], T0), T0, enabled: true)!;
        Assert.Equal("1 rate limited, 1 no signal", lights.Summary);
        Assert.Equal([LightKind.RateLimited, LightKind.NoSignal], lights.Dots);
    }

    [Fact]
    public void Ten_sessions_are_four_dots_and_plus_six_needs_you_first()
    {
        var records = Enumerable.Range(0, 9).Select(i => Record($"w{i}", "working", T0.AddMinutes(-i))).ToList();
        records.Add(Record("wait", "needs_you", T0.AddMinutes(-1)));
        var lights = SessionLights.From(SessionActivities.From(records, T0), T0, enabled: true)!;
        Assert.Equal([LightKind.NeedsYou, LightKind.Working, LightKind.Working, LightKind.Working], lights.Dots);
        Assert.Equal("+6", lights.MoreText);
    }

    [Fact]
    public void The_compact_dot_is_amber_then_green_then_none()
    {
        LightKind? Compact(params FeedSession[] records) =>
            SessionLights.From(SessionActivities.From(records, T0), T0, enabled: true)?.CompactDot;

        Assert.Equal(LightKind.Working, Compact(Record("a", "working", T0), Record("b", "done", T0)));
        Assert.Null(Compact(Record("b", "done", T0)));
        Assert.Null(Compact(Record("b", "rate_limited", T0)));
    }

    [Fact]
    public void Finished_and_silent_sessions_leave_the_pill_after_ten_minutes()
    {
        Assert.NotNull(SessionLights.From(SessionActivities.From([Record("a", "done", T0.AddMinutes(-9))], T0), T0, true));
        Assert.Null(SessionLights.From(SessionActivities.From([Record("a", "done", T0.AddMinutes(-11))], T0), T0, true));
        Assert.Null(SessionLights.From(SessionActivities.From([Record("a", "idle", T0.AddMinutes(-11))], T0), T0, true));
        Assert.NotNull(SessionLights.From(SessionActivities.From([Record("a", "working", T0.AddHours(-1), T0.AddMinutes(-9))], T0), T0, true));
        Assert.Null(SessionLights.From(SessionActivities.From([Record("a", "working", T0.AddHours(-1), T0.AddMinutes(-11))], T0), T0, true));
        // A long wait stays: it is what the lights are for.
        Assert.NotNull(SessionLights.From(SessionActivities.From([Record("a", "needs_you", T0.AddHours(-2))], T0), T0, true));
    }

    [Fact]
    public void Switched_off_or_without_sessions_there_are_no_lights()
    {
        Assert.Null(SessionLights.From(SessionActivities.From([Record("a", "working", T0)], T0), T0, enabled: false));
        Assert.Null(SessionLights.From([], T0, enabled: true));
    }

    // --------------------------------------------------------------- cards

    private static SessionInfo Session(string id) => new()
    {
        SessionId = id,
        TranscriptPath = $"C:\\t\\{id}.jsonl",
        WorkingDirectory = "C:\\work\\acme-webapp",
        Model = "claude-opus-5-5",
        StartedAt = T0.AddMinutes(-30),
        LastActivityAt = T0.AddSeconds(-30)
    };

    [Fact]
    public void A_card_with_a_plugin_record_shows_its_status_instead_of_live()
    {
        var activity = Only(Record("s1", "needs_you", T0.AddMinutes(-1), attention: new FeedAttention("question", null)), T0);
        var card = SessionCardViewModel.From(Session("s1"), T0, null, activity);
        Assert.True(card.HasStatus);
        Assert.Equal("Needs you: a question", card.StatusText);
        Assert.Equal(LightKind.NeedsYou, card.StatusKind);
        Assert.True(card.IsLive);
        Assert.False(card.ShowLive);

        var plain = SessionCardViewModel.From(Session("s1"), T0);
        Assert.False(plain.HasStatus);
        Assert.True(plain.ShowLive);
        Assert.NotEqual(card.Signature, plain.Signature);
    }

    [Fact]
    public void The_lighter_card_names_the_model_and_keeps_its_details_behind_a_toggle()
    {
        var card = SessionCardViewModel.From(Session("s1"), T0);
        Assert.StartsWith("Opus 5.5 · #s1", card.Subtitle);
        Assert.True(card.HasDetails);
        Assert.False(card.ShowDetails);
        Assert.Equal("Details ▾", card.DetailsToggleText);

        var changed = new List<string?>();
        card.PropertyChanged += (_, e) => changed.Add(e.PropertyName);
        card.IsExpanded = true;
        Assert.True(card.ShowDetails);
        Assert.Equal("Less ▴", card.DetailsToggleText);
        Assert.Contains(nameof(SessionCardViewModel.ShowDetails), changed);
        card.IsExpanded = true;
        Assert.Equal(3, changed.Count); // no second round for the same value
    }

    [Fact]
    public void The_customizer_preview_card_is_always_open()
    {
        var preview = CardPreview.Card(CardLayout.Default, T0);
        Assert.True(preview.IsExpanded);
        Assert.True(preview.ShowDetails);
        Assert.False(preview.ShowDetailsToggle); // nothing to fold there
        Assert.NotEmpty(preview.Rows);
        Assert.True(SessionCardViewModel.From(CardPreview.Sample(T0), T0).ShowDetailsToggle);
    }

    [Fact]
    public void Cards_that_need_you_sort_first_longest_wait_first()
    {
        var cards = new[] { "a", "b", "c", "d" }.Select(id => SessionCardViewModel.From(Session(id), T0)).ToList();
        cards[1] = SessionCardViewModel.From(Session("b"), T0, null, Only(Record("b", "needs_you", T0.AddMinutes(-1)), T0));
        cards[3] = SessionCardViewModel.From(Session("d"), T0, null, Only(Record("d", "needs_you", T0.AddMinutes(-5)), T0));
        cards[2] = SessionCardViewModel.From(Session("c"), T0, null, Only(Record("c", "working", T0), T0));
        Assert.Equal(["d", "b", "a", "c"], SessionCardViewModel.NeedsYouFirst(cards).Select(c => c.SessionId));
    }

    // -------------------------------------------------------------- alerts

    private sealed class Run(AlertSettings? settings = null)
    {
        public AlertSettings Settings { get; set; } = settings ?? AlertSettings.Default;
        public AlertState State { get; set; } = AlertState.Empty;
        public IReadOnlyList<Alert> Dropped { get; private set; } = [];

        public IReadOnlyList<Alert> Plan(DateTimeOffset now, params FeedSession[] records)
        {
            var plan = AlertPlanner.Plan([], Settings, State, now, Zone, EnUs, SessionActivities.From(records, now));
            State = plan.State;
            Dropped = plan.Dropped;
            return plan.Alerts;
        }
    }

    [Fact]
    public void Needs_you_notifies_after_the_delay_once_per_waiting_episode()
    {
        var run = new Run();
        var since = T0;
        var waiting = Record("s1", "needs_you", since, attention: new FeedAttention("permission", "Bash"));

        Assert.Empty(run.Plan(since.AddSeconds(19), waiting));
        var alert = Assert.Single(run.Plan(since.AddSeconds(20), waiting));
        Assert.Equal(AlertKind.NeedsYou, alert.Kind);
        Assert.Equal("Claude needs you", alert.Title);
        Assert.Equal("acme-webapp: waiting for permission to use Bash (20s).", alert.Body);
        Assert.Equal(UsageLevel.Orange, alert.Level);
        Assert.Empty(run.Plan(since.AddMinutes(3), waiting));

        // Answered, then a new wait: a new episode, a new alert.
        Assert.Empty(run.Plan(since.AddMinutes(6), Record("s1", "working", since.AddMinutes(6))));
        var again = Record("s1", "needs_you", since.AddMinutes(7), since.AddMinutes(7.5), new FeedAttention("question", null));
        Assert.Empty(run.Plan(since.AddMinutes(7), again));
        Assert.Equal("acme-webapp: waiting for your answer to a question (30s).",
            Assert.Single(run.Plan(since.AddMinutes(7.5), again)).Body);
    }

    [Fact]
    public void An_episode_told_before_a_restart_is_not_told_again()
    {
        var first = new Run();
        var waiting = Record("s1", "needs_you", T0);
        Assert.Single(first.Plan(T0.AddSeconds(30), waiting));

        var saved = AlertStateFile.Serialize(first.State);
        using var dir = new TempDir();
        var path = Path.Combine(dir.Path, AlertStateFile.FileName);
        File.WriteAllText(path, saved);
        var restarted = new Run { State = AlertStateFile.Load(path) };
        Assert.Empty(restarted.Plan(T0.AddMinutes(2), waiting));
        // Forgotten a day after the episode began.
        Assert.NotEmpty(restarted.State.Told);
        restarted.Plan(T0.AddDays(1.1));
        Assert.Empty(restarted.State.Told);
    }

    [Fact]
    public void Each_waiting_session_gets_its_own_alert()
    {
        var run = new Run();
        var alerts = run.Plan(T0.AddMinutes(1),
            Record("a", "needs_you", T0), Record("b", "needs_you", T0, cwd: "/home/me/api"), Record("c", "working", T0));
        Assert.Equal(["acme-webapp", "api"], alerts.Select(a => a.Body.Split(':')[0]));
    }

    [Fact]
    public void The_delay_and_the_switches_are_followed()
    {
        var waiting = Record("s1", "needs_you", T0);
        Assert.Single(new Run(AlertSettings.Default with { AttentionDelay = TimeSpan.Zero }).Plan(T0, waiting));
        Assert.Empty(new Run(AlertSettings.Default with { OnNeedsYou = false }).Plan(T0.AddMinutes(1), waiting));
        Assert.Empty(new Run(AlertSettings.Default with { Enabled = false }).Plan(T0.AddMinutes(1), waiting));
    }

    [Fact]
    public void Quiet_hours_and_the_mute_drop_the_alert_and_count_it_as_told()
    {
        var run = new Run(AlertSettings.Default with { MutedUntil = T0.AddHours(1) });
        var waiting = Record("s1", "needs_you", T0);
        Assert.Empty(run.Plan(T0.AddMinutes(1), waiting));
        Assert.Equal(AlertKind.NeedsYou, Assert.Single(run.Dropped).Kind);
        run.Settings = AlertSettings.Default;
        Assert.Empty(run.Plan(T0.AddHours(2), waiting));
    }

    [Fact]
    public void A_session_that_waits_but_went_silent_gives_no_alert()
    {
        var run = new Run();
        Assert.Empty(run.Plan(T0.AddMinutes(10), Record("s1", "needs_you", T0, T0.AddMinutes(1))));
    }

    [Fact]
    public void Finished_notifies_only_when_switched_on_for_a_long_turn_and_recent()
    {
        var done = Record("s1", "done", T0, lastTurn: TimeSpan.FromMinutes(12));
        Assert.Empty(new Run().Plan(T0.AddSeconds(5), done)); // off by default

        var on = AlertSettings.Default with { OnDone = true };
        var run = new Run(on);
        var alert = Assert.Single(run.Plan(T0.AddSeconds(5), done));
        Assert.Equal(AlertKind.AgentDone, alert.Kind);
        Assert.Equal("Claude finished", alert.Title);
        Assert.Equal("acme-webapp (12m).", alert.Body);
        Assert.Empty(run.Plan(T0.AddSeconds(20), done));

        Assert.Empty(new Run(on).Plan(T0.AddSeconds(5), Record("s1", "done", T0, lastTurn: TimeSpan.FromSeconds(59))));
        Assert.Empty(new Run(on).Plan(T0.AddSeconds(5), Record("s1", "done", T0)));
        Assert.Empty(new Run(on).Plan(T0.AddMinutes(11), Record("s1", "done", T0, T0.AddMinutes(11), lastTurn: TimeSpan.FromMinutes(5))));
    }

    [Fact]
    public void The_refresh_comes_back_when_the_next_wait_falls_due()
    {
        var activities = SessionActivities.From([
            Record("a", "needs_you", T0.AddSeconds(-5)), Record("b", "needs_you", T0.AddSeconds(-15)), Record("c", "needs_you", T0.AddMinutes(-2)),
        ], T0);
        Assert.Equal(T0.AddSeconds(5), UsageFeed.AttentionDueAt(activities, AlertSettings.Default, T0));
        Assert.Null(UsageFeed.AttentionDueAt(activities, AlertSettings.Default with { OnNeedsYou = false }, T0));
        Assert.Null(UsageFeed.AttentionDueAt(SessionActivities.From([Record("c", "needs_you", T0.AddMinutes(-2))], T0),
            AlertSettings.Default, T0));
    }

    [Fact]
    public void The_new_settings_default_to_lights_on_needs_you_on_finished_off()
    {
        var settings = new AppSettings();
        Assert.True(settings.ShowSessionLights);
        Assert.True(settings.AlertOnNeedsYou);
        Assert.False(settings.AlertOnDone);
        Assert.Equal(20, settings.AttentionDelaySeconds);
        Assert.Equal(TimeSpan.FromSeconds(20), AlertSettings.From(settings).AttentionDelay);
        settings.AttentionDelaySeconds = -5;
        Assert.Equal(TimeSpan.Zero, AlertSettings.From(settings).AttentionDelay);
        settings.AttentionDelaySeconds = 99_999;
        Assert.Equal(TimeSpan.FromHours(1), AlertSettings.From(settings).AttentionDelay);
    }
}
