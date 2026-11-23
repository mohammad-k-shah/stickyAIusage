using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using StickyAIusage.Forecasting;
using StickyAIusage.Health;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.ViewModels;

namespace StickyAIusage.UiTests;

/// <summary>
/// Renders every screen with sample data, off-screen, to PNG. This catches
/// XAML problems that only surface at runtime (a missing resource, a broken
/// binding in a template, a bad trigger), and the images are uploaded by CI so
/// the design can be reviewed without running the app. Everything runs in one
/// test on one STA thread: WPF objects belong to the thread that created them,
/// and an Application can only be created once per process.
/// </summary>
public class ScreenshotTests
{
    private static readonly string OutDir = Environment.GetEnvironmentVariable("UI_SCREENSHOT_DIR")
        ?? Path.Combine(Path.GetTempPath(), "stickyaiusage-screenshots");

    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    [Fact]
    public void Every_screen_renders()
    {
        RunOnSta(() =>
        {
            Directory.CreateDirectory(OutDir);
            var app = new App();
            app.InitializeComponent();

            var desktop = new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3D));
            var settings = new AppSettings();

            // What `--self-test` checks in the published exe, on the test build too
            // (it ends on the dark palette, as a start does).
            var selfTest = new SelfTest("UI tests");
            SelfTestRunner.CheckTrayIcon(selfTest);
            SelfTestRunner.CheckThemes(selfTest, app);
            SelfTestRunner.CheckScreens(selfTest, settings, Now);
            Assert.True(selfTest.Passed, selfTest.Report());
            Assert.Equal(9, selfTest.Results.Count);

            ShareCards(settings);
            FirstRunAndHealth(app);
            MarketingRenders.Render(desktop);

            // Collapsed pill states.
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), spentToday: 4.20m), desktop, "01-pill.png");
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), spentToday: 13.60m), desktop, "18-pill-day-over-target.png");
            Save(Pill(new AppSettings { ShowDayTargetAmount = false, ShowDayTargetPercent = false }, Usage(96.75m, 48.4), Session(),
                spentToday: 4.20m), desktop, "19-pill-day-ring-only.png");
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), spentToday: 4.20m, compact: true), desktop, "20-pill-taskbar.png");
            Save(Pill(settings, Usage(176.10m, 88.1, staleFor: TimeSpan.FromMinutes(12)), Session()), desktop, "02-pill-orange-stale.png");
            Save(Pill(settings, new UsageSnapshot { HasMonthlySpend = false, IsStale = true, RequiresReauth = true }, Session()),
                desktop, "03-pill-sign-in.png");
            Save(Pill(settings, Usage(96.75m, 48.4), Session(), paused: true), desktop, "04-pill-paused.png");

            // Pro / Max plan layout: rolling windows and extra-usage credits.
            Save(Pill(settings, PlanUsage(), Session()), desktop, "08-pill-plan.png");
            Save(Pill(settings, PlanUsage(), Session(), compact: true), desktop, "10-pill-plan-taskbar.png");

            // Claude Code's feed: an older reading reads "as of", and a gateway
            // spend limit known only as a percentage shows no dollars.
            Save(Pill(settings, FeedPlanUsage(TimeSpan.FromMinutes(12)), Session()), desktop, "23-pill-plan-feed-as-of.png");
            Save(Pill(settings, FeedSpendLimit(), Session()), desktop, "24-pill-spend-limit-percent.png");

            // Pace forecast: the limit time replaces the countdown, red within
            // 30 minutes; the compact pill keeps its width (within 12 px).
            Save(Pill(settings, PlanUsage(fiveHour: 88), Session(), forecasts: PlanForecasts()), desktop, "30-pill-plan-forecast.png");
            var compactToday = Pill(settings, PlanUsage(fiveHour: 88), Session(), compact: true);
            var compactForecast = Pill(settings, PlanUsage(fiveHour: 88), Session(), compact: true, forecasts: PlanForecasts());
            Assert.Contains("→ " + Format.ShortTimeNoDesignator(Now.AddMinutes(25)), Texts(compactForecast));
            // The chip's width follows the clock ("→ 12:58" is a digit wider than "→ 1:11"), so
            // both ends are measured at fixed times rather than at whatever time the test runs:
            // the widest may not grow the pill by more than 12 px, the narrowest is one digit less.
            var widest = Pill(settings, PlanUsage(fiveHour: 88), Session(), compact: true, forecasts: PlanForecasts(LocalTimeToday(12, 58)));
            var narrowest = Pill(settings, PlanUsage(fiveHour: 88), Session(), compact: true, forecasts: PlanForecasts(LocalTimeToday(1, 11)));
            Assert.InRange(Width(widest) - Width(compactToday), -12, 12);
            Assert.InRange(Width(narrowest) - Width(compactToday), -18, 12);
            Save(compactForecast, desktop, "31-pill-plan-forecast-taskbar.png");
            Save(Pill(settings, Usage(176.10m, 88.1), Session(), spentToday: 4.20m, forecasts: [MonthForecast()]), desktop,
                "32-pill-monthly-projection.png");

            var pace = new DockedStripWindow(settings);
            pace.ApplyUsage(PlanUsage(fiveHour: 88), Session(), forecasts: PlanForecasts());
            pace.ToggleExpanded();
            pace.ShowSessions(SampleCards().Take(1).ToList());
            var paceContent = Detach(pace);
            Save(paceContent, desktop, "33-flyout-plan-pace.png");
            var paceTexts = Texts(paceContent);
            Assert.Contains(paceTexts, t => t.StartsWith("At this pace: limit at "));
            Assert.Contains(paceTexts, t => t.Contains("points ahead of an even pace"));

            var monthPace = new DockedStripWindow(settings);
            monthPace.ApplyUsage(Usage(176.10m, 88.1), Session(), 4.20m, [MonthForecast()]);
            monthPace.ToggleExpanded();
            monthPace.ShowSessions(SampleCards().Take(1).ToList());
            var monthContent = Detach(monthPace);
            Save(monthContent, desktop, "34-flyout-monthly-pace.png");
            Assert.Contains(Texts(monthContent), t => t.Contains("(cap $200.00)"));

            var planWindow = new DockedStripWindow(settings);
            planWindow.ApplyUsage(PlanUsage(), Session());
            planWindow.ToggleExpanded();
            planWindow.ShowSessions(SampleCards().Take(1).ToList());
            Save(Detach(planWindow), desktop, "09-flyout-plan.png");

            // Codex beside Claude: its own card under Claude's, and "Codex 91%" on the pill
            // while it is closer to its limit; without Codex no trace of it.
            var codex = new DockedStripWindow(settings);
            codex.ApplyUsage(PlanUsage(fiveHour: 43), Session(), others: [CodexReading()]);
            codex.ToggleExpanded();
            codex.ShowSessions(SampleCards().Take(1).ToList());
            Assert.Equal(["Codex 91%"], codex.ExtraChipTexts);
            var codexContent = Detach(codex);
            Save(codexContent, desktop, "43-flyout-codex.png");
            var codexTexts = Texts(codexContent);
            Assert.Contains("Codex · Plus", codexTexts);
            Assert.Contains("from Codex's local session log", codexTexts);
            Assert.Contains(codexTexts, t => t.StartsWith("91% · Resets in 1h 3"));
            Assert.Contains(codexTexts, t => t.StartsWith("34% · Resets "));
            Save(Pill(settings, PlanUsage(fiveHour: 43), Session(), compact: true, others: [CodexReading()]), desktop,
                "44-pill-codex-chip-taskbar.png");
            var calm = new DockedStripWindow(settings);
            calm.ApplyUsage(PlanUsage(fiveHour: 43), Session(), others: [CodexReading(fiveHour: 20)]);
            Assert.Empty(calm.ExtraChipTexts);
            var noLimits = new DockedStripWindow(settings);
            noLimits.ApplyUsage(PlanUsage(), Session(), others: [CodexWithoutLimits()]);
            noLimits.ToggleExpanded();
            var noLimitsContent = Detach(noLimits);
            Save(noLimitsContent, desktop, "45-flyout-codex-no-limits.png");
            Assert.Contains(CodexUsageProvider.NoLimitsDetail, Texts(noLimitsContent));
            var noCodex = new DockedStripWindow(settings);
            noCodex.ApplyUsage(PlanUsage(), Session());
            noCodex.ToggleExpanded();
            Assert.DoesNotContain(Texts(Detach(noCodex)), t => t.Contains("Codex"));

            // A newer version (Check for updates on): a line at the foot of the flyout with what's
            // new and, for a winget install, the command; a download gets no command; none: no line.
            var updated = new DockedStripWindow(settings);
            updated.ApplyUsage(PlanUsage(), Session());
            updated.ToggleExpanded();
            updated.ShowSessions(SampleCards().Take(1).ToList());
            var notice = new Updates.UpdateNotice(Updates.SemanticVersion.Parse("2.0.1")!, Updates.SemanticVersion.Parse("2.0.0")!,
                Updates.UpdateChecker.ReleasePageBase + "v2.0.1", Updates.InstallMethod.Winget,
                Updates.InstallMethods.UpdateCommand(Updates.InstallMethod.Winget, false, false), "PowerShell");
            updated.ApplyUpdate(notice);
            Assert.Equal(notice, updated.LatestUpdate);
            var updatedContent = Detach(updated);
            Save(updatedContent, desktop, "54-flyout-update.png");
            var updateTexts = Texts(updatedContent);
            Assert.Contains("Update available: v2.0.1 — what's new", updateTexts);
            Assert.Contains("To update, run this in PowerShell:", updateTexts);
            Assert.Equal("winget upgrade MohammadKShah.StickyAIUsage", updated.UpdateCommandText.Text);
            Assert.Equal(Visibility.Visible, updated.UpdateCommandRow.Visibility);
            updated.ApplyUpdate(notice with { Method = Updates.InstallMethod.Download, Command = null, CommandPlace = null });
            Assert.Equal(Visibility.Collapsed, updated.UpdateCommandRow.Visibility);
            Assert.Equal("Download it from the release page.", updated.UpdateHowText.Text);
            updated.ApplyUpdate(null);
            Assert.Equal(Visibility.Collapsed, updated.UpdatePanel.Visibility);
            Assert.Null(updated.LatestUpdate);
            Assert.Equal(Visibility.Collapsed, noCodex.UpdatePanel.Visibility);

            // Expanded flyout with three sessions.
            var window = new DockedStripWindow(settings);
            window.ApplyUsage(Usage(96.75m, 48.4), Session(), 4.20m);
            window.ToggleExpanded();
            window.ShowSessions(SampleCards());
            Save(Detach(window), desktop, "05-flyout.png");

            // Where each session's cost comes from: Claude Code's own figure
            // ("cost"), an estimate ("est. cost"), and "≈" for an unknown model.
            var exactSession = Session(cost: 3.05m);
            exactSession.ExactCostUsd = 3.41m;
            var roughSession = Session(id: "9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10", name: "Try the new model",
                model: "claude-nova-1", cost: 1.12m, rough: true);
            var costs = new DockedStripWindow(settings);
            costs.ApplyUsage(PlanUsage(), exactSession);
            costs.ToggleExpanded();
            costs.ShowSessions([SessionCardViewModel.From(exactSession, Now), SampleCards()[1], SessionCardViewModel.From(roughSession, Now)]);
            var costsContent = Detach(costs);
            Save(costsContent, desktop, "29-flyout-session-costs.png");
            var costTexts = Texts(costsContent);
            Assert.Contains("$3.41", costTexts);
            Assert.Contains("cost", costTexts);
            Assert.Contains("≈$1.12", costTexts);
            Assert.Contains("est. cost", costTexts);

            // The flyout's notice: sign in again (terminal + copy), install
            // the plugin (copy), and the one-time plugin tip (copy + dismiss).
            var signIn = new DockedStripWindow(settings);
            signIn.ApplyUsage(PlanUsage() with { RequiresReauth = true, IsStale = true, SourceId = ClaudeOAuthUsageSource.Id }, Session());
            signIn.ToggleExpanded();
            Assert.Equal(Visibility.Visible, signIn.NoticeBar.Visibility);
            Assert.Equal(Visibility.Visible, signIn.SignInButton.Visibility);
            Assert.Equal("Copy command", signIn.CopyNoticeButton.Content);
            Assert.Equal(Visibility.Collapsed, signIn.DismissNoticeButton.Visibility);
            Assert.Equal(Visibility.Collapsed, signIn.ConnectNoticeButton.Visibility);
            Save(Detach(signIn), desktop, "25-flyout-sign-in.png");

            var waiting = new DockedStripWindow(settings);
            waiting.ApplyUsage(new UsageSnapshot
            {
                IsStale = true, FetchedAt = Now, SourceId = ClaudeFeedUsageSource.Id, StatusDetail = ClaudeFeedUsageSource.WaitingDetail
            }, Session());
            waiting.ToggleExpanded();
            Assert.Equal("Copy install command", waiting.CopyNoticeButton.Content);
            Assert.Equal(Visibility.Collapsed, waiting.SignInButton.Visibility);
            Assert.Equal((Visibility.Visible, FlyoutNotice.ConnectLabel), (waiting.ConnectNoticeButton.Visibility, waiting.ConnectNoticeButton.Content));
            var noticeConnects = 0;
            waiting.ConnectRequested += (_, _) => noticeConnects++;
            waiting.ConnectNoticeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, noticeConnects);
            Save(Detach(waiting), desktop, "26-flyout-install-plugin.png");

            var tipSettings = new AppSettings();
            var tip = new DockedStripWindow(tipSettings);
            tip.ApplyUsage(PlanUsage() with { SourceId = ClaudeOAuthUsageSource.Id }, Session());
            tip.ToggleExpanded();
            Assert.Equal(Visibility.Visible, tip.DismissNoticeButton.Visibility);
            var tipContent = Detach(tip);
            Save(tipContent, desktop, "27-flyout-plugin-tip.png");
            tip.Content = tipContent;
            tip.DismissNoticeButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(tipSettings.PluginTipDismissed);
            Assert.NotEqual(FlyoutNotice.PluginTipMessage, tip.NoticeText.Text);
            // The window's own first refresh (an empty test feed) may have
            // landed meanwhile; the endpoint's reading again brings no tip.
            tip.ApplyUsage(PlanUsage() with { SourceId = ClaudeOAuthUsageSource.Id }, Session());
            Assert.Equal(Visibility.Collapsed, tip.NoticeBar.Visibility);

            // Session card field customizer, and what a drag does to a card.
            var fields = new FieldsWindow(settings, () => { });
            var fieldsContent = Detach(fields);
            fieldsContent.Width = 800;
            Save(fieldsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "14-fields.png");

            var rearranged = new FieldsWindow(settings, () => { });
            rearranged.MoveField("cost", rowIndex: 0, position: 0);
            rearranged.MoveField("git-branch", rowIndex: 3, position: 0);
            Assert.Equal("cost,model,effort", string.Join(",", rearranged.Layout.Rows[0]));
            Assert.Equal("git-branch", string.Join(",", rearranged.Layout.Rows[3]));
            var rearrangedContent = Detach(rearranged);
            rearrangedContent.Width = 800;
            Save(rearrangedContent, (Brush)app.FindResource("WindowBackgroundBrush"), "15-fields-rearranged.png");

            // A group stays together: picking Session ID from the catalog puts
            // it beside the other Core fields rather than at the end.
            var grouped = new FieldsWindow(settings, () => { });
            grouped.AddField("session-id");
            Assert.Equal("model,effort,session-id", string.Join(",", grouped.Layout.Rows[0]));

            // A row holds eight. A ninth chip dropped on it stays where it
            // was, and the card says why.
            var refused = new FieldsWindow(settings, () => { });
            foreach (var field in SessionFields.InCategory(FieldCategory.Git).Take(8))
                refused.AddField(field.Id);
            var before = refused.Layout.Rows.Select(r => string.Join(",", r)).ToList();
            refused.MoveField("model", rowIndex: 3, position: 8);
            Assert.Equal(before, refused.Layout.Rows.Select(r => string.Join(",", r)));
            var refusedContent = Detach(refused);
            refusedContent.Width = 1100;
            refusedContent.Height = 820;
            Save(refusedContent, (Brush)app.FindResource("WindowBackgroundBrush"), "17-fields-row-full.png");

            // Every field on the card: the rows and the preview each keep
            // their share of the window and scroll, rather than overlapping.
            var allFields = new FieldsWindow(settings, () => { });
            foreach (var field in SessionFields.All)
                allFields.AddField(field.Id);
            var allFieldsContent = Detach(allFields);
            allFieldsContent.Width = 1100;
            allFieldsContent.Height = 820;
            Save(allFieldsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "16-fields-all.png");

            // Light theme: the same screens with the light palette merged in.
            ThemeService.ApplyForTests(dark: false);
            var paper = new SolidColorBrush(Color.FromRgb(0xD8, 0xDA, 0xE0));
            Save(Pill(settings, PlanUsage(), Session()), paper, "11-pill-plan-light.png");
            Save(Pill(settings, PlanUsage(fiveHour: 88), Session(), forecasts: PlanForecasts()), paper, "35-pill-plan-forecast-light.png");
            var lightLights = new DockedStripWindow(settings);
            lightLights.ApplyUsage(PlanUsage(), Session());
            lightLights.ApplyActivities(Activities(), hasPlugin: true);
            Save(Detach(lightLights), paper, "41-pill-session-lights-light.png");

            var lightFlyout = new DockedStripWindow(settings);
            lightFlyout.ApplyUsage(PlanUsage(), Session());
            lightFlyout.ToggleExpanded();
            lightFlyout.ShowSessions(SampleCards().Take(1).ToList());
            Save(Detach(lightFlyout), paper, "12-flyout-plan-light.png");

            var lightSettings = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            var lightSettingsContent = Detach(lightSettings);
            lightSettingsContent.Width = 484;
            Save(lightSettingsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "13-settings-light.png");
            ThemeService.ApplyForTests(dark: true);

            // Settings, valid and with an invalid threshold.
            var codexProbe = SettingsWindow.CodexPresent;
            SettingsWindow.CodexPresent = () => false;
            var settingsWindow = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            SettingsWindow.CodexPresent = codexProbe;
            Assert.Equal(OtherProviders.CodexSettingsHint(false), settingsWindow.CodexHintText.Text);
            Assert.True(settingsWindow.ShowCodexCheck.IsChecked);
            var settingsContent = Detach(settingsWindow);
            settingsContent.Width = 484;
            Save(settingsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "06-settings.png");

            var invalid = new SettingsWindow(settings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            invalid.YellowBox.Text = "90";
            Assert.False(invalid.ValidateThresholds().IsValid);
            Assert.False(invalid.SaveButton.IsEnabled);
            var invalidContent = Detach(invalid);
            invalidContent.Width = 484;
            Save(invalidContent, (Brush)app.FindResource("WindowBackgroundBrush"), "07-settings-invalid.png");

            var actions = new SettingsActions(() => { }, () => { }, () => { }, () => { });
            var badTarget = new SettingsWindow(settings, actions, UsageMode.MonthlySpend);
            badTarget.DayTargetBox.Text = "0";
            Assert.False(badTarget.ValidateDayTarget().IsValid);
            Assert.False(badTarget.SaveButton.IsEnabled);
            var badTargetContent = Detach(badTarget);
            badTargetContent.Width = 484;
            Save(badTargetContent, (Brush)app.FindResource("WindowBackgroundBrush"), "21-settings-day-target-invalid.png");

            // Pro/Max: the day target is off, with a note - and an invalid
            // target there doesn't block Save.
            var plan = new SettingsWindow(settings, actions, UsageMode.PlanLimits);
            Assert.Equal(Visibility.Collapsed, plan.DayTargetSection.Visibility);
            Assert.Equal(Visibility.Visible, plan.DayTargetNote.Visibility);
            plan.DayTargetBox.Text = "0";
            Assert.True(plan.SaveButton.IsEnabled);
            var planContent = Detach(plan);
            planContent.Width = 484;
            Save(planContent, (Brush)app.FindResource("WindowBackgroundBrush"), "22-settings-plan-day-target-off.png");

            // Usage source: a new install's settings show the plugin selected
            // with its install line; Legacy shows the unofficial warning.
            var connectedProbe = SettingsWindow.PluginConnected;
            SettingsWindow.PluginConnected = () => true;
            // Once the plugin has connected the install line goes.
            Assert.Equal(Visibility.Collapsed, new SettingsWindow(new AppSettings(), actions).PluginInstallRow.Visibility);
            SettingsWindow.PluginConnected = () => false;
            var feedSource = new SettingsWindow(new AppSettings(), actions);
            SettingsWindow.PluginConnected = connectedProbe;
            Assert.Equal(Visibility.Visible, feedSource.PluginInstallRow.Visibility);
            Assert.Equal(Visibility.Collapsed, feedSource.SourceWarning.Visibility);
            var legacySettings = new AppSettings();
            var legacySource = new SettingsWindow(legacySettings, actions);
            // Checked the way UI Automation (a screen reader) toggles it: no Click.
            var legacySegment = legacySource.SourceSegments.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>()
                .Single(b => Equals(b.Tag, ClaudeSourceMode.Legacy));
            legacySegment.IsChecked = true;
            legacySegment.IsChecked = false; // unchecking the chosen one keeps it chosen
            Assert.True(legacySegment.IsChecked);
            Assert.Equal(Visibility.Visible, legacySource.SourceWarning.Visibility);
            Assert.Equal(Visibility.Collapsed, legacySource.PluginInstallRow.Visibility);
            Assert.Equal(UsageSourceOptions.UnofficialWarning, legacySource.SourceWarning.Text);
            var legacyContent = Detach(legacySource);
            legacyContent.Width = 484;
            Save(legacyContent, (Brush)app.FindResource("WindowBackgroundBrush"), "28-settings-source-legacy.png");
            Assert.Equal("feed", legacySettings.ClaudeSource); // applies only on Save

            // Check for updates: off for a new install, on after the switch and Save.
            var updateSettings = new AppSettings();
            var updates = new SettingsWindow(updateSettings, actions);
            Assert.False(updates.CheckForUpdatesCheck.IsChecked);
            updates.CheckForUpdatesCheck.IsChecked = true;
            Assert.False(updateSettings.CheckForUpdates);
            updates.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.True(updateSettings.CheckForUpdates);

            // The pace forecast: on by default, off after the switch and Save.
            var forecastSettings = new AppSettings();
            var forecastWindow = new SettingsWindow(forecastSettings, actions);
            Assert.True(forecastWindow.ShowForecastCheck.IsChecked);
            forecastWindow.ShowForecastCheck.IsChecked = false;
            Assert.True(forecastSettings.ShowForecast);
            forecastWindow.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(forecastSettings.ShowForecast);

            // Settings name the build that is running (version, profile, source).
            var buildInfo = new SettingsWindow(new AppSettings { ClaudeSource = "auto" }, actions).BuildInfoText.Text;
            Assert.StartsWith("Sticky AI Usage ", buildInfo);
            Assert.EndsWith($"· {AppIdentity.Profile(AppPaths.Current)} · usage source: Automatic", buildInfo); // the tests' own data folder

            // Usage alerts: on by default; quiet hours are checked before Save;
            // the test button asks the App for one notification.
            var testAlerts = 0;
            var alertSettings = new AppSettings();
            var alertWindow = new SettingsWindow(alertSettings, actions with { SendTestAlert = () => testAlerts++ });
            Assert.True(alertWindow.AlertsEnabledCheck.IsChecked);
            Assert.True(alertWindow.AlertOnThresholdCheck.IsChecked & alertWindow.AlertOnForecastCheck.IsChecked
                        & alertWindow.AlertOnLimitCheck.IsChecked & alertWindow.AlertOnResetCheck.IsChecked);
            Assert.False(alertWindow.QuietHoursCheck.IsChecked);
            Assert.False(alertWindow.QuietHoursOptions.IsEnabled);
            alertWindow.QuietHoursCheck.IsChecked = true;
            Assert.True(alertWindow.QuietHoursOptions.IsEnabled);
            Assert.Equal(("22:00", "07:00"), (alertWindow.QuietFromBox.Text, alertWindow.QuietToBox.Text));
            alertWindow.QuietToBox.Text = "later";
            alertWindow.ValidateQuietHours();
            Assert.False(alertWindow.SaveButton.IsEnabled);
            Assert.Equal(Visibility.Visible, alertWindow.QuietHoursError.Visibility);
            alertWindow.QuietToBox.Text = "6:30";
            alertWindow.ValidateQuietHours();
            Assert.True(alertWindow.SaveButton.IsEnabled);
            alertWindow.AlertOnResetCheck.IsChecked = false;
            alertWindow.TestAlertButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, testAlerts);
            Assert.Null(alertSettings.AlertQuietFrom); // applies only on Save
            alertWindow.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(("22:00", "06:30"), (alertSettings.AlertQuietFrom, alertSettings.AlertQuietTo));
            Assert.False(alertSettings.AlertOnReset);
            Assert.True(alertSettings.AlertsEnabled);
            var alertsShown = new SettingsWindow(alertSettings, actions);
            Assert.True(alertsShown.QuietHoursCheck.IsChecked);
            alertsShown.AlertsEnabledCheck.IsChecked = false;
            Assert.False(alertsShown.AlertOptions.IsEnabled);
            alertsShown.AlertsEnabledCheck.IsChecked = true;
            var alertsContent = Detach(alertsShown);
            alertsContent.Width = 484;
            Save(alertsContent, (Brush)app.FindResource("WindowBackgroundBrush"), "37-settings-alerts.png");

            // A refresh's alerts go to the App (which owns the tray icon); none, nothing.
            var alerting = new DockedStripWindow(settings);
            var raised = new List<Alerts.Alert>();
            alerting.AlertsRaised += alerts => raised.AddRange(alerts);
            alerting.RaiseAlerts([]);
            Assert.Empty(raised);
            alerting.RaiseAlerts([Alerts.AlertPlanner.TestAlert()]);
            Assert.Equal("Test alert", Assert.Single(raised).Title);
            // Clicking the toast (the tray balloon) opens the flyout.
            using (var tray = new Hardcodet.Wpf.TaskbarNotification.TaskbarIcon())
            {
                var opened = 0;
                App.ConnectAlerts(tray, () => opened++);
                tray.RaiseEvent(new RoutedEventArgs(Hardcodet.Wpf.TaskbarNotification.TaskbarIcon.TrayBalloonTipClickedEvent));
                Assert.Equal(1, opened);
            }

            // Session lights: a dot per session (needs you first), one in the
            // taskbar; the cards carry the status instead of "Live", the
            // waiting one first; details open with the toggle and stay open.
            var activities = Activities();
            var lit = new DockedStripWindow(settings);
            lit.ApplyUsage(PlanUsage(), Session());
            lit.ApplyActivities(activities, hasPlugin: true);
            Assert.Equal(Visibility.Visible, lit.LightsGroup.Visibility);
            Assert.Equal([LightKind.NeedsYou, LightKind.Working, LightKind.Done],
                lit.LightsDots.Children.OfType<FrameworkElement>().Select(d => (LightKind)d.Tag));
            Assert.Contains("waiting for permission to use Bash", (string)lit.LightsGroup.ToolTip);
            var byId = activities.ToDictionary(a => a.SessionId);
            var litCards = SessionCardViewModel.NeedsYouFirst(SampleCards()
                .Select(c => SessionCardViewModel.From(Session(c.SessionId), Now, null, byId.GetValueOrDefault(c.SessionId)))
                .ToList());
            lit.ToggleExpanded();
            lit.ShowSessions(litCards);
            Assert.Equal("Needs you: permission", litCards[0].StatusText);
            Assert.Equal(Visibility.Collapsed, lit.LightsHintText.Visibility);
            Assert.Equal(Visibility.Collapsed, lit.LightsConnectButton.Visibility);
            lit.SessionsList.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
                new System.Windows.Controls.Button { Tag = "details", DataContext = litCards[0] }));
            Assert.True(litCards[0].IsExpanded);
            var refreshed = litCards.Select(c => SessionCardViewModel.From(Session(c.SessionId), Now)).ToList();
            lit.ShowSessions(refreshed);
            Assert.True(refreshed[0].IsExpanded);
            Assert.False(refreshed[1].IsExpanded);
            lit.ShowSessions(litCards);
            var litContent = Detach(lit);
            Save(litContent, desktop, "38-flyout-session-lights.png"); // lays the cards out
            var litTexts = Texts(litContent);
            Assert.Contains("Needs you: permission", litTexts);
            Assert.Contains("Working", litTexts);
            var litPill = new DockedStripWindow(settings);
            litPill.ApplyUsage(PlanUsage(), Session());
            litPill.ApplyActivities(activities, hasPlugin: true);
            Save(Detach(litPill), desktop, "39-pill-session-lights.png");
            var litCompact = new DockedStripWindow(settings);
            litCompact.ApplyUsage(PlanUsage(), Session());
            litCompact.SetCompact(true);
            litCompact.ApplyActivities(activities, hasPlugin: true);
            Assert.Equal([LightKind.NeedsYou], litCompact.LightsDots.Children.OfType<FrameworkElement>().Select(d => (LightKind)d.Tag));
            Save(Detach(litCompact), desktop, "40-pill-session-lights-taskbar.png");
            // Without the plugin: no dots and one line; switched off: nothing.
            var unlit = new DockedStripWindow(new AppSettings { ClaudeSource = "auto" });
            unlit.ApplyUsage(PlanUsage(), Session());
            unlit.ApplyActivities([], hasPlugin: false);
            Assert.Equal(Visibility.Collapsed, unlit.LightsGroup.Visibility);
            Assert.Equal(Visibility.Visible, unlit.LightsHintText.Visibility);
            Assert.Equal(SessionLights.ConnectHint, unlit.LightsHintText.Text);
            Assert.Equal((Visibility.Visible, SessionLights.ConnectAction), (unlit.LightsConnectButton.Visibility, unlit.LightsConnectButton.Content));
            var connectRequests = 0;
            unlit.ConnectRequested += (_, _) => connectRequests++;
            unlit.LightsConnectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, connectRequests);
            var dark = new DockedStripWindow(new AppSettings { ShowSessionLights = false });
            dark.ApplyActivities(activities, hasPlugin: true);
            Assert.Equal(Visibility.Collapsed, dark.LightsGroup.Visibility);
            // Settings: the lights and the two session alerts, saved.
            var lightsSettings = new AppSettings();
            var lightsWindow = new SettingsWindow(lightsSettings, actions);
            Assert.True(lightsWindow.ShowSessionLightsCheck.IsChecked);
            Assert.True(lightsWindow.AlertOnNeedsYouCheck.IsChecked);
            Assert.False(lightsWindow.AlertOnDoneCheck.IsChecked);
            lightsWindow.ShowSessionLightsCheck.IsChecked = false;
            lightsWindow.AlertOnDoneCheck.IsChecked = true;
            lightsWindow.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.False(lightsSettings.ShowSessionLights);
            Assert.True(lightsSettings.AlertOnDone);

            // No reading and no notice: the usage card says why, with one button.
            var failed = new DockedStripWindow(settings);
            failed.ApplyUsage(new UsageSnapshot { IsStale = true, FetchedAt = Now }, Session());
            failed.ToggleExpanded();
            Assert.Equal(Visibility.Collapsed, failed.NoticeBar.Visibility);
            Assert.Equal(Visibility.Visible, failed.MonthSummary.Visibility);
            Assert.Equal(HealthLine.FailedDetail, failed.HealthText.Text);
            Assert.Equal("Open Health", failed.HealthButton.Content);
            Assert.Equal(Visibility.Visible, failed.HealthButton.Visibility);
            var healthRequests = 0;
            failed.HealthRequested += (_, _) => healthRequests++;
            failed.HealthButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(1, healthRequests);
            Save(Detach(failed), desktop, "36-flyout-health-failed.png");
            var current = new DockedStripWindow(settings);
            current.ApplyUsage(PlanUsage(), Session());
            Assert.Equal(Visibility.Collapsed, current.HealthPanel.Visibility);

            // A restart keeps the theme and the spot the pill was left at: a
            // new window from the saved settings, as App.OnStartup builds it.
            SettingsService.Save(new AppSettings { Theme = "light", WidgetDocked = false, DockMode = "free", WidgetRight = 912, WidgetBottom = 712 });
            var restarted = App.LoadSettings();
            Assert.Equal("light", restarted.Theme);
            ThemeService.Initialize(restarted);
            Assert.False(ThemeService.IsDark);
            ThemeService.ApplyForTests(dark: true);
            var moved = new DockedStripWindow(restarted);
            moved.RestorePosition();
            Assert.Equal((900.0, 700.0), moved.PillCorner);
            Assert.Null(moved.DockedAt);
            // "Reset widget position" docks it again (in the taskbar, the default), and that is remembered.
            moved.ResetPosition();
            Assert.Equal(("taskbar", true), (App.LoadSettings().DockMode, App.LoadSettings().WidgetDocked));

            // A spot off every screen docks for this run (at once above the tray) and keeps the saved spot.
            var lost = new AppSettings { WidgetDocked = false, DockMode = "free", WidgetRight = -5000, WidgetBottom = 712 };
            var lostWindow = new DockedStripWindow(lost);
            lostWindow.RestorePosition();
            Assert.True(lostWindow.PillCorner.Right > SystemParameters.VirtualScreenLeft);
            Assert.Equal(DockMode.Above, lostWindow.DockedAt!.Mode);
            Assert.Equal((-5000.0, "free", false), (lost.WidgetRight, lost.DockMode, lost.WidgetDocked));

            // In the taskbar: compact, at the taskbar's height (a short taskbar: 28), centered by its spot.
            var docked = new DockedStripWindow(settings);
            docked.ApplyUsage(PlanUsage(), Session());
            docked.ApplyDockSpot(new DockSpot(DockMode.Taskbar, 600, 100, 28));
            Assert.Equal((28.0, (600.0, 100.0), DockMode.Taskbar), (docked.RootBorder.Height, docked.PillCorner, docked.DockedAt!.Mode));
            Assert.Equal(14, docked.RootBorder.CornerRadius.TopLeft);
            Save(Detach(docked), new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)), "52-pill-in-taskbar-28.png");
            var above = new DockedStripWindow(settings);
            above.ApplyUsage(PlanUsage(), Session());
            above.ApplyDockSpot(new DockSpot(DockMode.Above, 600, 100, 32, DockPlacement.NoRoomReason));
            Assert.Equal(32.0, above.RootBorder.Height);
            Assert.Equal((true, false), (docked.IsCompact, above.IsCompact)); // compact inside the taskbar, full form above it

            // Settings → Widget position: two places, the monitor with several, full screen.
            var monitorProbe = SettingsWindow.MonitorList;
            SettingsWindow.MonitorList = () => [(@"\\.\DISPLAY1", true), (@"\\.\DISPLAY2", false)];
            var dockSettings = new AppSettings { DockMode = "free", DockMonitor = @"\\.\DISPLAY2" };
            var dockWindow = new SettingsWindow(dockSettings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            SettingsWindow.MonitorList = monitorProbe;
            Assert.Equal((false, false, (DockMode?)null), (dockWindow.DockTaskbarButton.IsChecked == true, dockWindow.DockAboveButton.IsChecked == true, dockWindow.DockChoice));
            Assert.Equal(DockModes.SettingsHint(DockMode.Free), dockWindow.DockHintText.Text);
            Assert.Equal(Visibility.Visible, dockWindow.DockMonitorRow.Visibility);
            Assert.Equal(@"\\.\DISPLAY2", dockWindow.DockMonitorChoice);
            Assert.Equal(["Main display", "Display 2"], dockWindow.DockMonitorSegments.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().Select(b => (string)b.Content));
            // Toggled as a screen reader does (no Click): the choice follows the checked state.
            dockWindow.DockAboveButton.IsChecked = true;
            Assert.Equal((DockMode.Above, false), (dockWindow.DockChoice!.Value, dockWindow.DockTaskbarButton.IsChecked == true));
            dockWindow.DockAboveButton.IsChecked = false; // a segment can't switch itself off
            Assert.True(dockWindow.DockAboveButton.IsChecked);
            dockWindow.DockMonitorSegments.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>().First().IsChecked = true;
            dockWindow.HideWhenFullscreenCheck.IsChecked = false;
            var dockContent = Detach(dockWindow);
            dockContent.Width = 484;
            Save(dockContent, (Brush)app.FindResource("WindowBackgroundBrush"), "53-settings-widget-position.png");
            dockWindow.Content = dockContent;
            dockWindow.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(("above", "primary", false, true), (dockSettings.DockMode, dockSettings.DockMonitor, dockSettings.HideWhenFullscreen, dockSettings.WidgetDocked));
            var oneMonitor = new SettingsWindow(new AppSettings(), new SettingsActions(() => { }, () => { }, () => { }, () => { }));
            Assert.True(oneMonitor.DockTaskbarButton.IsChecked);
        });

        Assert.True(File.Exists(Path.Combine(OutDir, "05-flyout.png")));
    }

    /// <summary>Every share card - limits, spend, session - wide and portrait, dark and
    /// light (the README samples come from these), what they say and don't say, and
    /// the flyout's Share button, the cards' Share link and the Settings rows.</summary>
    private static void ShareCards(AppSettings settings)
    {
        var sessions = new[] { Session(), Session(id: "7481c4b9-6458-421f-b183-b35bb06d1e5d", name: "Write API integration tests", cost: 0.48m) };
        foreach (var shape in new[] { "wide", "portrait" })
        {
            var shaped = new AppSettings { ShareCardShape = shape };
            var cards = new (string Name, Sharing.ShareCardModel Card)[]
            {
                ("limits", Sharing.ShareCards.ForUsage(PlanUsage(fiveHour: 88), PlanForecasts(), sessions, null, shaped, Now)!),
                ("spend", Sharing.ShareCards.ForUsage(Usage(176.10m, 88.1), [MonthForecast()], sessions, 4.20m, shaped, Now)!),
                ("session", Sharing.ShareCards.ForSession(Session(), shaped, Now)),
                ("year", Sharing.ShareCards.ForYear(Sharing.YearCard.Sample(2026), shaped)!)
            };
            foreach (var (name, card) in cards)
            {
                foreach (var dark in new[] { true, false })
                {
                    var bitmap = ShareCardView.Render(card, dark);
                    Assert.Equal(card.Shape.Size(), (bitmap.PixelWidth, bitmap.PixelHeight));
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(OutDir, $"42-share-{name}-{shape}-{(dark ? "dark" : "light")}.png"));
                    encoder.Save(file);
                }
                // What the view draws is the model's text and nothing else: no project, session or branch.
                var view = new ShareCardView();
                view.Show(card, Sharing.ShareCardPalette.Dark);
                view.Measure(new Size(card.Shape.Size().Width, card.Shape.Size().Height));
                view.Arrange(new Rect(0, 0, card.Shape.Size().Width, card.Shape.Size().Height));
                var texts = Texts(view);
                Assert.Contains(Sharing.ShareCards.ShortRepositoryUrl, texts);
                Assert.DoesNotContain(texts, t => t.Contains("acme", StringComparison.OrdinalIgnoreCase)
                                                 || t.Contains("Refactor payment") || t.Contains("checkout-redesign"));
                Assert.All(texts.Where(t => t.Length > 0), t => Assert.Contains(t, card.AllText())); // empty: a part the card leaves out
                // The URL stays readable at half size: still 11 px there.
                Assert.True(view.FooterUrlText.FontSize >= 22);
            }
        }

        // The flyout: Share is off without a reading, on with one; a click on a card's
        // Share runs through the window with the image the menu would make.
        var none = new DockedStripWindow(settings) { ShareClock = () => November };
        none.ApplyUsage(new UsageSnapshot { IsStale = true, FetchedAt = Now }, Session());
        Assert.False(none.ShareButton.IsEnabled);
        Assert.Equal(Sharing.ShareFlow.NoReadingTooltip, none.ShareButton.ToolTip);
        var flyout = new DockedStripWindow(settings) { ShareClock = () => November };
        flyout.ApplyUsage(PlanUsage(), Session());
        Assert.True(flyout.ShareButton.IsEnabled);
        flyout.ToggleExpanded();
        flyout.ShowSessions(SampleCards().Take(1).ToList());
        var content = Detach(flyout);
        Save(content, new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3D)), "43-flyout-share.png"); // lays the cards out
        Assert.Contains("Share", Texts(content));
        flyout.Content = content;
        // A card's Share and the header's Share open the same menu, never a second one beside it.
        var shareLink = new System.Windows.Controls.Button { Tag = "share", DataContext = SampleCards()[0] };
        flyout.SessionsList.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, shareLink));
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky"], flyout.ShareMenuItems);
        flyout.ShareButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(5, flyout.ShareMenuItems.Count);
        Assert.All(flyout.ShareMenuItemsEnabled, Assert.True);
        var output = new FakeShareOutput();
        flyout.ShareOutput = output;
        flyout.ShareAsync(Sharing.ShareAction.Copy,
            () => Task.FromResult<Sharing.ShareCardModel?>(Sharing.ShareCards.ForSession(Session(), settings, Now))).GetAwaiter().GetResult();
        Assert.Equal(Sharing.ShareFlow.Copied, flyout.ShareStatusText.Text);
        Assert.Equal(Visibility.Visible, flyout.ShareStatusText.Visibility);
        var png = Assert.Single(output.Copied);
        Assert.Equal((1200, 630), PngSize(png));
        flyout.ShareAsync(Sharing.ShareAction.Copy, () => Task.FromResult<Sharing.ShareCardModel?>(null)).GetAwaiter().GetResult();
        Assert.Equal(Sharing.ShareFlow.NoReadingTooltip + ".", flyout.ShareStatusText.Text);
        Assert.Single(output.Copied);

        // December and January: the header's menu adds the year card (no reading needed), a
        // card's menu doesn't; the line under the header says when the year has nothing.
        var season = new DockedStripWindow(settings) { ShareClock = () => December, YearStats = SampleYearStats() };
        season.ApplyUsage(new UsageSnapshot { IsStale = true, FetchedAt = Now }, Session());
        Assert.True(season.ShareButton.IsEnabled);
        Assert.Equal(Sharing.ShareFlow.ShareYearTooltip, season.ShareButton.ToolTip);
        season.ShareButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky", "My 2026 with Claude Code"],
            season.ShareMenuItems);
        Assert.Equal(["Copy image", "Save image…", "Post on X", "Post on LinkedIn", "Post on Bluesky"], season.ShareYearMenuItems);
        Assert.Equal([false, false, false, false, false, true], season.ShareMenuItemsEnabled); // today's card needs a reading
        var yearOutput = new FakeShareOutput();
        season.ShareOutput = yearOutput;
        season.ShareAsync(Sharing.ShareAction.Copy, () => season.YearCardAsync(2026)).GetAwaiter().GetResult();
        Assert.Equal(Sharing.ShareFlow.Copied, season.ShareStatusText.Text);
        Assert.Equal((1200, 630), PngSize(Assert.Single(yearOutput.Copied)));
        // A cancelled Save leaves no progress line behind.
        season.ShareAsync(Sharing.ShareAction.Save, () => season.YearCardAsync(2026)).GetAwaiter().GetResult();
        Assert.Equal(Visibility.Collapsed, season.ShareStatusText.Visibility);
        var yearCard = season.YearCardAsync(2026).GetAwaiter().GetResult()!;
        Assert.Equal("3k", yearCard.Headline);
        Assert.Contains(new Sharing.ShareStat("Favorite model", "Opus 5.5"), yearCard.Stats);
        var quiet = new DockedStripWindow(settings) { ShareClock = () => December, YearStats = SampleYearStats(empty: true) };
        quiet.ShareAsync(Sharing.ShareAction.Copy, () => quiet.YearCardAsync(2026), Sharing.YearCard.NothingYet(2026)).GetAwaiter().GetResult();
        Assert.Equal("No Claude Code sessions in 2026 to show yet.", quiet.ShareStatusText.Text);
        // Out of season the menu is as before, and a card's menu never has the year.
        flyout.SessionsList.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, shareLink));
        Assert.Empty(flyout.ShareYearMenuItems);
        season.SessionsList.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent,
            new System.Windows.Controls.Button { Tag = "share", DataContext = SampleCards()[0] }));
        Assert.Empty(season.ShareYearMenuItems);

        // The customizer's preview card has no Share link.
        var preview = SessionCardViewModel.From(Session(), Now);
        preview.CanFold = false;
        Assert.False(preview.ShowShare);

        // Settings: the shape and the names switch, saved.
        var shareSettings = new AppSettings();
        var window = new SettingsWindow(shareSettings, new SettingsActions(() => { }, () => { }, () => { }, () => { }));
        Assert.True(window.ShareWideButton.IsChecked);
        Assert.False(window.ShareIncludeProjectNamesCheck.IsChecked);
        window.SharePortraitButton.IsChecked = true;
        window.ShareIncludeProjectNamesCheck.IsChecked = true;
        Assert.False(window.ShareWideButton.IsChecked);
        Save(Detach(window), new SolidColorBrush(Color.FromRgb(0x2A, 0x2E, 0x3D)), "44-settings-share.png");
        window.SaveButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal("portrait", shareSettings.ShareCardShape);
        Assert.True(shareSettings.ShareIncludeProjectNames);
    }

/// <summary>Mid-December 2026: the year card's season. Out of it: mid-November.</summary>
    private static readonly DateTimeOffset December = new(2026, 12, 15, 10, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset November = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);

    /// <summary>The year card's numbers over one sample session (14 March 2026, Opus 5.5) or none,
    /// in a temp folder, read as of mid-December; already read, so the card needs no wait.</summary>
    private static Stats.YearStatsService SampleYearStats(bool empty = false)
    {
        var folder = Path.Combine(Path.GetTempPath(), "stickyaiusage-uitests", Guid.NewGuid().ToString("N"));
        var project = Path.Combine(folder, "projects", "C--work-acme-webapp");
        Directory.CreateDirectory(project);
        if (!empty)
        {
            const string id = "3d0b8c7e-5f1a-4c2e-9b6d-7a8e9f0a1b2c";
            File.WriteAllText(Path.Combine(project, id + ".jsonl"),
                "{\"type\":\"assistant\",\"sessionId\":\"" + id + "\",\"cwd\":\"/work/acme-webapp\",\"timestamp\":\"2026-03-14T09:00:00Z\"," +
                "\"message\":{\"id\":\"msg_1\",\"model\":\"claude-opus-5-5\",\"usage\":{\"input_tokens\":1000,\"output_tokens\":2000}}}\n");
        }
        var stats = new Stats.YearStatsService(new TranscriptIndex(Path.Combine(folder, "projects")), Path.Combine(folder, "stats"),
            () => December, TimeZoneInfo.Utc);
        stats.GetAsync(2026).GetAwaiter().GetResult(); // no await: nothing here waits for the UI thread
        return stats;
    }

    /// <summary>A PNG's width and height from its header.</summary>
    private static (int Width, int Height) PngSize(byte[] png) =>
        (png[16] << 24 | png[17] << 16 | png[18] << 8 | png[19], png[20] << 24 | png[21] << 16 | png[22] << 8 | png[23]);

    private sealed class FakeShareOutput : Sharing.IShareOutput
    {
        public readonly List<byte[]> Copied = [];

        public Task<bool> CopyImageAsync(byte[] png)
        {
            Copied.Add(png);
            return Task.FromResult(true);
        }

        public Task<string?> SaveImageAsync(byte[] png, string suggestedName) => Task.FromResult<string?>(null);
        public Task<string?> SaveToPicturesAsync(byte[] png, string fileName) => Task.FromResult<string?>(null);
        public Task<bool> OpenUrlAsync(string url) => Task.FromResult(true);
    }

    private static FrameworkElement Pill(AppSettings settings, UsageSnapshot usage, SessionInfo session,
        bool paused = false, bool compact = false, decimal? spentToday = null, IReadOnlyList<PaceForecast>? forecasts = null,
        IReadOnlyList<ProviderReading>? others = null)
    {
        var window = new DockedStripWindow(settings);
        window.ApplyUsage(usage, session, spentToday, forecasts, others);
        window.SetCompact(compact);
        window.SetRunning(!paused);
        return Detach(window);
    }

    /// <summary>Codex beside Claude: a reading as Codex's session log gives it.</summary>
    private static ProviderReading CodexReading(double fiveHour = 91, double weekly = 34) =>
        new(new ProviderUsage("codex", "Codex", "Plus",
            [
                new UsageWindow(UsageWindowKinds.FiveHour, fiveHour, Now.AddMinutes(95), TimeSpan.FromHours(5)),
                new UsageWindow(UsageWindowKinds.SevenDay, weekly, Now.AddDays(3), TimeSpan.FromDays(7))
            ], null, Now.AddMinutes(-2), CodexUsageProvider.SourceIdValue, SourceTrust.Derived, UsageHealth.Ok, null)
        {
            WholePointsOnly = true,
            SourceLabel = CodexUsageProvider.SourceLabelText,
            WindowLabels = new Dictionary<string, string> { ["five_hour"] = "5-hour window", ["seven_day"] = "Weekly" }
        }, []);

    private static ProviderReading CodexWithoutLimits() =>
        new(new ProviderUsage("codex", "Codex", null, [], null, Now, CodexUsageProvider.SourceIdValue, SourceTrust.Derived,
            UsageHealth.Unavailable, CodexUsageProvider.NoLimitsDetail) { SourceLabel = CodexUsageProvider.SourceLabelText }, []);

    /// <summary>Takes a window's content out of the (never shown) window so it
    /// can be measured and rendered on its own.</summary>
    /// <summary>Answers --version and the install from a script; the test releases the install.</summary>
    private sealed class ScriptedCli(CliRun install) : IClaudeCli
    {
        public List<string> Ran { get; } = [];

        public Task<CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ran.Add(string.Join(' ', arguments));
            return Task.FromResult(arguments[0] == "--version" ? new CliRun(0, "2.1.325 (Claude Code)", "") : install);
        }
    }

    private static readonly UsageSnapshot FeedNumbers = new()
    {
        Mode = UsageMode.PlanLimits, FiveHourUtilizationPercent = 43, WeeklyUtilizationPercent = 12, FetchedAt = Now,
        SourceId = ClaudeFeedUsageSource.Id, Trust = SourceTrust.Official
    };

    /// <summary>The first-run window (three steps, the upgrade screen, Connect waiting →
    /// connected, a refused install) and Settings → Health, dark and light; then what
    /// Finish, legacy mode and the Health buttons do.</summary>
    private static void FirstRunAndHealth(App app)
    {
        foreach (var dark in new[] { true, false })
        {
            ThemeService.ApplyForTests(dark);
            var theme = dark ? "dark" : "light";
            var background = (Brush)app.FindResource("WindowBackgroundBrush");
            for (var step = 1; step <= 3; step++)
            {
                var window = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun,
                    new ConnectSession(() => null, () => false, new ScriptedCli(new CliRun(0, "", "")), null, PluginInstaller.DefaultSource),
                    new OnboardingActions(() => { }, _ => { }));
                window.ShowStep(step);
                Assert.Equal(Onboarding.StepCaption(step), window.StepCaptionText.Text);
                Assert.Equal(step == 3 ? "Finish" : "Next", window.NextButton.Content);
                Assert.Equal(step > 1 ? Visibility.Visible : Visibility.Collapsed, window.BackButton.Visibility);
                if (step == 2)
                {
                    // No command line on this "computer": only the copy path works.
                    Assert.False(window.RunButton.IsEnabled);
                    Assert.StartsWith(Onboarding.NoCliHint, window.CopyHintText.Text);
                    Assert.Equal(PluginInstaller.ShellCommand(PluginInstaller.DefaultSource), window.CommandText.Text);
                }
                Save(AtWindowWidth(Detach(window)), background, $"46-first-run-step{step}-{theme}.png");
            }

            // Connect: run it → installed, waiting → the first numbers turn it green.
            UsageSnapshot? usage = null;
            var wrote = false;
            var cli = new ScriptedCli(new CliRun(0, """{"outcome":"ok","message":"Successfully installed plugin: sticky-usage@sticky-ai-usage (scope: user)"}""", ""));
            var connect = new ConnectSession(() => usage, () => wrote, cli, "claude", PluginInstaller.DefaultSource);
            var waiting = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, connect, new OnboardingActions(() => { }, _ => { }));
            waiting.ShowStep(2);
            Assert.True(waiting.RunButton.IsEnabled);
            connect.RunAsync().GetAwaiter().GetResult(); // the fake answers at once, on this thread
            Assert.Equal(["--version", "plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage --json"], cli.Ran);
            Assert.Equal(ConnectFlow.InstalledWaiting, waiting.StatusText.Text);
            Assert.Equal(Visibility.Visible, waiting.StatusBorder.Visibility);
            Save(AtWindowWidth(Detach(waiting)), background, $"47-connect-waiting-{theme}.png");

            var connected = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, connect, new OnboardingActions(() => { }, _ => { }));
            connected.ShowStep(2);
            wrote = true;
            usage = FeedNumbers;
            connect.Refresh();
            Assert.Equal("Connected: 5-hour 43% · weekly 12%.", connected.StatusText.Text);
            Assert.Equal(((SolidColorBrush)app.FindResource("GoodBrush")).Color, ((SolidColorBrush)connected.StatusDot.Fill).Color);
            Save(AtWindowWidth(Detach(connected)), background, $"48-connect-connected-{theme}.png");

            var refused = new ConnectSession(() => null, () => false,
                new ScriptedCli(new CliRun(1, "", "error: managed settings do not allow this marketplace")), "claude", PluginInstaller.DefaultSource);
            var failedWindow = new OnboardingWindow(new AppSettings(), OnboardingScreen.FirstRun, refused, new OnboardingActions(() => { }, _ => { }));
            failedWindow.ShowStep(2);
            refused.RunAsync().GetAwaiter().GetResult();
            Assert.Contains("organization may not allow user plugins", failedWindow.StatusText.Text);
            Assert.Equal(Visibility.Visible, failedWindow.OutputBox.Visibility);
            Assert.Contains("managed settings do not allow", failedWindow.OutputBox.Text);
            failedWindow.LegacyLinkButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Assert.Equal(Visibility.Visible, failedWindow.LegacyPanel.Visibility);
            Save(AtWindowWidth(Detach(failedWindow)), background, $"49-connect-failed-{theme}.png");

            var whatsNew = new OnboardingWindow(new AppSettings(), OnboardingScreen.WhatsNew,
                new ConnectSession(() => null, () => false, new ScriptedCli(new CliRun(0, "", "")), "claude", PluginInstaller.DefaultSource),
                new OnboardingActions(() => { }, _ => { }));
            Assert.Equal((2, "Done", Onboarding.WhatsNewTitle, ""), (whatsNew.Step, whatsNew.NextButton.Content, whatsNew.WindowTitleText.Text, whatsNew.StepCaptionText.Text));
            Assert.Equal(Visibility.Collapsed, whatsNew.ReadsPanel.Visibility);
            Save(AtWindowWidth(Detach(whatsNew)), background, $"50-whats-new-{theme}.png");

            // Settings → Health: a machine whose plugin isn't connected yet.
            var facts = new HealthFacts
            {
                Now = Now, ClaudeDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
                ClaudeDirectoryExists = true, TranscriptCount = 41, ClaudeCodeVersion = "2.1.325", SessionsToday = 3,
                Usage = new UsageSnapshot { IsStale = true, FetchedAt = Now, StatusDetail = ClaudeFeedUsageSource.WaitingDetail }
            };
            var items = HealthCheck.Build(facts);
            var ran = new List<string>();
            var healthSettings = new AppSettings();
            var settingsWindow = new SettingsWindow(healthSettings, new SettingsActions(() => { }, () => { }, () => { }, () => { },
                CheckHealth: () => Task.FromResult(new HealthResult(items, HealthCheck.Report(items, "test", Now))),
                RunHealthAction: id => { ran.Add(id); if (id == HealthActionIds.ToggleStartup) healthSettings.StartWithWindows = true; }));
            settingsWindow.ShowHealth(new HealthResult(items, HealthCheck.Report(items, "test", Now)));
            Assert.Equal(10, settingsWindow.HealthItems.Count);
            Assert.Equal("2 things need a look.", settingsWindow.HealthNoteText.Text);
            Assert.True(settingsWindow.CopyReportButton.IsEnabled);
            // A click on the item's own button (through the template) reaches the app.
            var content = (FrameworkElement)settingsWindow.Content;
            content.Measure(new Size(484, double.PositiveInfinity));
            content.Arrange(new Rect(content.DesiredSize));
            var connectButton = FindButton(settingsWindow.HealthList, b => b.Tag as string == HealthActionIds.Connect)!;
            Assert.Equal("Connect", connectButton.Content);
            connectButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            settingsWindow.StartWithWindowsCheck.IsChecked = true; // unsaved
            settingsWindow.RunHealthAction(HealthActionIds.OpenUsageSource); // the window's own: scrolls, asks nothing
            settingsWindow.RunHealthAction(HealthActionIds.SendTestAlert);
            Assert.True(settingsWindow.StartWithWindowsCheck.IsChecked); // another button leaves the unsaved switch alone
            settingsWindow.StartWithWindowsCheck.IsChecked = false;
            settingsWindow.RunHealthAction(HealthActionIds.ToggleStartup);
            Assert.Equal([HealthActionIds.Connect, HealthActionIds.SendTestAlert, HealthActionIds.ToggleStartup], ran);
            Assert.True(settingsWindow.StartWithWindowsCheck.IsChecked); // the switch follows the change made at once
            var card = settingsWindow.HealthCard;
            Detach(settingsWindow);
            ((Panel)card.Parent).Children.Remove(card);
            card.Width = 484;
            Save(card, background, $"51-settings-health-{theme}.png");
        }
        ThemeService.ApplyForTests(dark: true);

        // Finish applies the three switches and marks the window done; the update check stays off unless chosen.
        var chosen = new AppSettings();
        bool? startAtLogin = null;
        var changed = 0;
        var finish = new OnboardingWindow(chosen, OnboardingScreen.FirstRun,
            new ConnectSession(() => null, () => false, new ScriptedCli(new CliRun(0, "", "")), null, PluginInstaller.DefaultSource),
            new OnboardingActions(() => changed++, on => startAtLogin = on));
        Assert.False(finish.UpdateCheck.IsChecked);
        Assert.True(finish.StartAtLoginCheck.IsChecked); // on in this screen, off in Settings until confirmed
        finish.AlertsCheck.IsChecked = false;
        Assert.True(finish.DockTaskbarChoice.IsChecked); // in the taskbar unless chosen otherwise
        finish.DockAboveChoice.IsChecked = true;
        finish.Finish();
        Assert.Equal((true, Onboarding.CurrentVersion, false, false, true), (chosen.OnboardingCompleted, chosen.OnboardingVersion,
            chosen.CheckForUpdates, chosen.AlertsEnabled, chosen.StartWithWindows));
        Assert.Equal("above", chosen.DockMode);
        Assert.Equal((true, 1, true), (startAtLogin, changed, finish.Finished));

        // Legacy mode from the Connect step: chosen at once, the widget told.
        var legacy = new AppSettings();
        var legacyChanged = 0;
        var legacyWindow = new OnboardingWindow(legacy, OnboardingScreen.FirstRun,
            new ConnectSession(() => null, () => false, new ScriptedCli(new CliRun(0, "", "")), null, PluginInstaller.DefaultSource),
            new OnboardingActions(() => legacyChanged++, _ => { }));
        legacyWindow.ShowStep(2);
        legacyWindow.LegacyButton.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
        Assert.Equal(("legacy", 1, false), (legacy.ClaudeSource, legacyChanged, legacy.OnboardingCompleted));

        // Connect from Health finishes nothing of the first-run window.
        var fromHealth = new AppSettings();
        var connectOnly = new OnboardingWindow(fromHealth, OnboardingScreen.Connect,
            new ConnectSession(() => null, () => false, new ScriptedCli(new CliRun(0, "", "")), null, PluginInstaller.DefaultSource),
            new OnboardingActions(() => { }, _ => throw new InvalidOperationException("Connect alone must not change start at sign-in")));
        connectOnly.Finish();
        Assert.False(fromHealth.OnboardingCompleted);
    }

    private static Button? FindButton(DependencyObject parent, Func<Button, bool> match)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Button button && match(button))
                return button;
            if (FindButton(child, match) is { } found)
                return found;
        }
        return null;
    }

    private static FrameworkElement AtWindowWidth(FrameworkElement content)
    {
        content.Width = 544;
        return content;
    }

    private static FrameworkElement Detach(Window window)
    {
        var content = (FrameworkElement)window.Content;
        window.Content = null;
        return content;
    }

    private static double Width(FrameworkElement content)
    {
        content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return content.DesiredSize.Width;
    }

    /// <summary>The 5-hour limit 25 minutes away (red), the weekly window
    /// ahead of an even pace.</summary>
    /// <summary>A time of day on today's date, in the local zone the pill formats times in.</summary>
    private static DateTimeOffset LocalTimeToday(int hour, int minute) =>
        new(DateTime.Today.AddHours(hour).AddMinutes(minute));

    private static IReadOnlyList<PaceForecast> PlanForecasts(DateTimeOffset? limitAt = null) =>
    [
        new(UsageWindowKinds.FiveHour, 88, 28.8, limitAt ?? Now.AddMinutes(25), 150, 40, ForecastConfidence.High, "the last 30 minutes")
            { ResetsAt = Now.AddMinutes(197), IsActive = true },
        new(UsageWindowKinds.SevenDay, 30, 0.4, null, 70, 6.3, ForecastConfidence.Low, "the last 24 hours")
            { ResetsAt = Now.AddDays(4).AddHours(3), IsActive = true }
    ];

    /// <summary>A monthly cap heading past its limit: 106% → $212 of $200.</summary>
    private static PaceForecast MonthForecast() =>
        new(UsageWindowKinds.MonthSpend, 88.1, 0.1, null, 106, 4, ForecastConfidence.High, "the last 7 days")
            { ResetsAt = Now.AddDays(10), IsActive = true };

    /// <summary>Every TextBlock's text under a rendered element.</summary>
    private static List<string> Texts(DependencyObject root)
    {
        var texts = new List<string>();
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is TextBlock { Text: { } text })
                texts.Add(text);
            texts.AddRange(Texts(child));
        }
        return texts;
    }

    private static void Save(FrameworkElement content, Brush background, string fileName)
    {
        var host = new Border { Background = background, Padding = new Thickness(16), Child = content };
        host.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        host.Arrange(new Rect(host.DesiredSize));
        host.UpdateLayout();

        const double scale = 2; // crisp at typical 150-200% review zoom
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(host.ActualWidth * scale), (int)Math.Ceiling(host.ActualHeight * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(host);
        host.Child = null;

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(OutDir, fileName));
        encoder.Save(file);
    }

    /// <summary>Three sessions of acme-webapp: one waiting for permission, one working, one done.</summary>
    private static IReadOnlyList<SessionActivity> Activities() => SessionActivities.From(
    [
        Record("3c8e62c4-17a7-453e-8385-88147c5aaf07", "needs_you", TimeSpan.FromMinutes(2), new FeedAttention("permission", "Bash")),
        Record("7481c4b9-6458-421f-b183-b35bb06d1e5d", "working", TimeSpan.FromMinutes(6)),
        Record("9e1f0a3b-55c2-4d0e-b7a1-2c6d8e4f9a10", "done", TimeSpan.FromMinutes(3)),
    ], Now);

    private static FeedSession Record(string id, string state, TimeSpan since, FeedAttention? attention = null) =>
        new(id, "terminal", @"C:\dev\acme-webapp", "claude-sonnet-5", "2.1.325", "2.2.0", Now, Now - since - since, Now,
            null, null, null, null, null, state, Now - since, attention, TimeSpan.FromMinutes(12));

    private static UsageSnapshot Usage(decimal used, double percent, TimeSpan? staleFor = null) => new()
    {
        HasMonthlySpend = true,
        MonthlySpendUsedUsd = used,
        MonthlySpendLimitUsd = 200m,
        MonthlySpendPercent = percent,
        FetchedAt = Now - (staleFor ?? TimeSpan.FromSeconds(20)),
        IsStale = staleFor is not null
    };

    private static UsageSnapshot PlanUsage(double fiveHour = 12) => new()
    {
        Mode = UsageMode.PlanLimits,
        SubscriptionType = "pro",
        FiveHourUtilizationPercent = fiveHour,
        FiveHourResetsAt = Now.AddMinutes(197),
        WeeklyUtilizationPercent = 30,
        WeeklyResetsAt = Now.AddDays(4).AddHours(3),
        ExtraUsageEnabled = true,
        ExtraUsageLimit = 50m,
        ExtraUsageUsed = 11.06m,
        FetchedAt = Now.AddSeconds(-20)
    };

    private static UsageSnapshot FeedPlanUsage(TimeSpan age) => PlanUsage() with
    {
        SubscriptionType = null,
        ExtraUsageEnabled = null,
        ExtraUsageLimit = null,
        ExtraUsageUsed = null,
        FetchedAt = Now - age,
        SourceId = ClaudeFeedUsageSource.Id,
        Trust = SourceTrust.Official
    };

    private static UsageSnapshot FeedSpendLimit() => new()
    {
        Mode = UsageMode.MonthlySpend,
        HasMonthlySpend = true,
        MonthlySpendPercentOnly = true,
        MonthlySpendPercent = 63.4,
        MonthlySpendResetsAt = Now.AddDays(14),
        FetchedAt = Now.AddSeconds(-20),
        SourceId = ClaudeFeedUsageSource.Id,
        Trust = SourceTrust.Official
    };

    private static SessionInfo Session(
        string id = "3c8e62c4-17a7-453e-8385-88147c5aaf07", string? name = "Refactor payment retries (fork)",
        string model = "claude-sonnet-5", decimal cost = 3.05m, long input = 50, long output = 4_012,
        long cacheRead = 3_300_821, long cacheWrite = 469_573, long context = 247_309,
        TimeSpan? ran = null, TimeSpan? idle = null, GitInfo? git = null, IReadOnlyList<string>? skills = null,
        bool rough = false) => new()
    {
        CostIsRough = rough,
        SessionId = id,
        SessionName = name,
        TranscriptPath = $@"C:\Users\me\.claude\projects\C--dev-acme-webapp\{id}.jsonl",
        WorkingDirectory = @"C:\dev\acme-webapp",
        Model = model,
        ThinkingEffort = "medium",
        ClaudeCodeVersion = "2.1.273",
        StartedAt = Now - (ran ?? TimeSpan.FromMinutes(7)) - (idle ?? TimeSpan.FromSeconds(30)),
        LastActivityAt = Now - (idle ?? TimeSpan.FromSeconds(30)),
        InputTokens = input,
        OutputTokens = output,
        CacheReadTokens = cacheRead,
        CacheCreationTokens = cacheWrite,
        ContextWindowSize = ModelContextWindows.Lookup(model),
        CurrentContextTokens = context,
        EstimatedCostUsd = cost,
        Git = git,
        SkillsUsed = skills ?? Array.Empty<string>()
    };

    private static List<SessionCardViewModel> SampleCards() =>
    [
        SessionCardViewModel.From(Session(git: new GitInfo
        {
            Branch = "feature/checkout-redesign", StagedCount = 2, UnstagedCount = 1, UntrackedCount = 4,
            Insertions = 274, Deletions = 39, AheadCount = 1, OriginOwner = "acme", OriginRepo = "acme-webapp",
            PullRequestStatus = "OPEN", CiStatus = "SUCCESS"
        }, skills: ["anthropic-skills:docx"]), Now),
        SessionCardViewModel.From(Session(id: "7481c4b9-6458-421f-b183-b35bb06d1e5d", name: "Write API integration tests",
            cost: 0.48m, input: 8, output: 2_990, cacheRead: 252_393, cacheWrite: 96_142, context: 106_495,
            ran: TimeSpan.FromMinutes(103), idle: TimeSpan.FromMinutes(40)), Now),
        SessionCardViewModel.From(Session(id: "6eba8773-0000-4000-8000-000000000000", name: null,
            model: "claude-haiku-4-5-20251001", cost: 0.06m, input: 20, output: 8_958, cacheRead: 863_287,
            cacheWrite: 55_748, context: 176_000, ran: TimeSpan.FromMinutes(4), idle: TimeSpan.FromHours(3),
            git: new GitInfo { Branch = "main" }), Now)
    ];

    private static void RunOnSta(Action action)
    {
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(() =>
        {
            // As in the app: a window's own refresh continues on this thread's dispatcher.
            // Without a context its first refresh ended on the thread pool and changed the
            // session list under a test's ShowSessions ("ItemsControl is inconsistent").
            SynchronizationContext.SetSynchronizationContext(
                new System.Windows.Threading.DispatcherSynchronizationContext(System.Windows.Threading.Dispatcher.CurrentDispatcher));
            try { action(); }
            catch (Exception ex) { failure = ExceptionDispatchInfo.Capture(ex); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        failure?.Throw();
    }
}
