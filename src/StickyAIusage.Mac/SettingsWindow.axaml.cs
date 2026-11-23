using System.Globalization;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Media;
using StickyAIusage.Health;
using StickyAIusage.Alerts;
using StickyAIusage.Mac.Native;
using StickyAIusage.Mac.Services;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac;

/// <summary>Settings (the macOS counterpart of the Windows settings window).
/// Changes apply on Save; Reset position and Open log folder act immediately.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly StartAtLoginSwitch _startAtLogin;
    private readonly SettingsActions _actions;

    /// <summary>Whether Codex is on this computer, for the Codex row's hint; tests replace it.</summary>
    /// <summary>Whether the Claude Code plugin has connected (its marker in the feed folder): then
    /// Usage source needn't offer its install command. Tests set their own.</summary>
    internal static Func<bool> PluginConnected { get; set; } =
        () => File.Exists(Path.Combine(AppPaths.FeedDirectory, ClaudeFeedUsageSource.MarkerFileName));

    internal static Func<bool> CodexPresent { get; set; } =
        () => new CodexUsageProvider(AppPaths.CodexDirectory, () => DateTimeOffset.UtcNow).IsPresent;
    private Thresholds.Result _thresholds;
    private DayTarget.Result? _dayTarget;
    private readonly bool _dayTargetApplies;

    public SettingsWindow() : this(new AppSettings(), new SettingsActions(() => { }, () => { }, () => { }, () => { })) { } // designer

    /// <param name="mode">The plan layout of the latest reading: the day
    /// target is turned off for plans with rolling limits.</param>
    public SettingsWindow(AppSettings settings, SettingsActions actions, UsageMode mode = UsageMode.Unknown)
    {
        _settings = settings;
        _actions = actions;
        _dayTargetApplies = DayTarget.AppliesTo(mode);
        InitializeComponent();

        BuildInfoText.Text = AppIdentity.Describe(AppIdentity.Version(typeof(App).Assembly), AppPaths.Current, settings);
        ShowUsageToggle.IsChecked = settings.ShowMonthlySpend;
        ShowForecastToggle.IsChecked = settings.ShowForecast;
        ShowSessionLightsToggle.IsChecked = settings.ShowSessionLights;
        ShowCodexToggle.IsChecked = settings.IsProviderEnabled(CodexUsageProvider.Id);
        CodexHintText.Text = OtherProviders.CodexSettingsHint(CodexPresent());
        ShowSessionCostToggle.IsChecked = settings.ShowSessionCost;
        LaunchAtLoginToggle.IsChecked = settings.StartWithWindows;
        _startAtLogin = new StartAtLoginSwitch(settings.StartWithWindows);
        CheckForUpdatesToggle.IsChecked = settings.CheckForUpdates;
        var portrait = ShareCardShapes.Parse(settings.ShareCardShape) == ShareCardShape.Portrait;
        SharePortraitButton.IsChecked = portrait;
        ShareWideButton.IsChecked = !portrait;
        // The checked state, not Click: a screen reader's Toggle raises no Click.
        ShareWideButton.IsCheckedChanged += (_, _) => SyncShareShape(ShareWideButton, SharePortraitButton);
        SharePortraitButton.IsCheckedChanged += (_, _) => SyncShareShape(SharePortraitButton, ShareWideButton);
        ShareIncludeProjectNamesToggle.IsChecked = settings.ShareIncludeProjectNames;
        ShowDayTargetToggle.IsChecked = settings.ShowDayTarget;
        ShowDayAmountToggle.IsChecked = settings.ShowDayTargetAmount;
        ShowDayPercentToggle.IsChecked = settings.ShowDayTargetPercent;
        ShowDayRingToggle.IsChecked = settings.ShowDayTargetRing;
        DayTargetBox.Text = DayTarget.ToText(settings.DayTargetUsd);
        YellowBox.Text = settings.YellowThresholdPercent.ToString("0.##", CultureInfo.CurrentCulture);
        OrangeBox.Text = settings.OrangeThresholdPercent.ToString("0.##", CultureInfo.CurrentCulture);
        RedBox.Text = settings.RedThresholdPercent.ToString("0.##", CultureInfo.CurrentCulture);

        ShowFieldsSummary();
        ShowTheme();
        ThemeService.Changed += ShowTheme;
        Closed += (_, _) => ThemeService.Changed -= ShowTheme;

        if (actions.CheckHealth is null)
            HealthTitle.IsVisible = HealthCard.IsVisible = false;
        else
            RefreshHealth();
        // The dots' colors come from the theme: paint them again when it changes.
        ThemeService.Changed += RepaintHealth;
        Closed += (_, _) => ThemeService.Changed -= RepaintHealth;

        _thresholds = ValidateThresholds();
        YellowBox.TextChanged += (_, _) => _thresholds = ValidateThresholds();
        OrangeBox.TextChanged += (_, _) => _thresholds = ValidateThresholds();
        RedBox.TextChanged += (_, _) => _thresholds = ValidateThresholds();

        // Taller than a 13-inch MacBook's screen; scroll rather than run off it.
        Opened += (_, _) =>
        {
            if (Screens.ScreenFromWindow(this) is { } screen)
                MaxHeight = screen.WorkingArea.Height / screen.Scaling - 24;
        };

        _dayTarget = ValidateDayTarget();
        DayTargetBox.TextChanged += (_, _) => _dayTarget = ValidateDayTarget();
        ShowDayTargetState();

        _source = ClaudeSourceModes.Parse(settings.ClaudeSource);
        BuildSourceSegments();
        PluginCommandText.Text = ClaudeCommands.InstallPlugin;
        ShowSource();

        AlertsEnabledToggle.IsChecked = settings.AlertsEnabled;
        AlertOnThresholdToggle.IsChecked = settings.AlertOnThreshold;
        AlertOnForecastToggle.IsChecked = settings.AlertOnForecast;
        AlertOnLimitToggle.IsChecked = settings.AlertOnLimit;
        AlertOnResetToggle.IsChecked = settings.AlertOnReset;
        AlertOnNeedsYouToggle.IsChecked = settings.AlertOnNeedsYou;
        AlertOnDoneToggle.IsChecked = settings.AlertOnDone;
        var quiet = QuietHours.FromSettings(settings.AlertQuietFrom, settings.AlertQuietTo);
        QuietHoursToggle.IsChecked = quiet is not null;
        QuietFromBox.Text = QuietHours.ToSetting(quiet?.From ?? DefaultQuietFrom);
        QuietToBox.Text = QuietHours.ToSetting(quiet?.To ?? DefaultQuietTo);
        _quiet = ValidateQuietHours();
        QuietFromBox.TextChanged += (_, _) => _quiet = ValidateQuietHours();
        QuietToBox.TextChanged += (_, _) => _quiet = ValidateQuietHours();
        ShowAlertState();
    }

    private static readonly TimeOnly DefaultQuietFrom = new(22, 0);
    private static readonly TimeOnly DefaultQuietTo = new(7, 0);
    private QuietHours.Result _quiet = new(true, default, default, false, false, null);

    private void Alerts_Changed(object? sender, RoutedEventArgs e)
    {
        // Fires while the constructor is still filling in the window.
        if (AlertOptions is null || QuietHoursOptions is null || QuietToBox is null)
            return;
        ShowAlertState();
        _quiet = ValidateQuietHours();
    }

    /// <summary>The kinds and the quiet hours follow their switches, like the day target's options.</summary>
    private void ShowAlertState()
    {
        var on = AlertsEnabledToggle.IsChecked == true;
        AlertOptions.IsEnabled = on;
        AlertOptions.Opacity = on ? 1 : 0.45;
        var quiet = QuietHoursToggle.IsChecked == true;
        QuietHoursOptions.IsEnabled = quiet;
        QuietHoursOptions.Opacity = quiet ? 1 : 0.45;
    }

    /// <summary>The two times only have to be valid while quiet hours are on;
    /// Save is disabled while they aren't.</summary>
    internal QuietHours.Result ValidateQuietHours()
    {
        var result = QuietHours.Validate(QuietHoursToggle.IsChecked == true, QuietFromBox.Text ?? "", QuietToBox.Text ?? "");
        QuietFromBox.Classes.Set("error", result.FromInvalid);
        QuietToBox.Classes.Set("error", result.ToInvalid);
        QuietHoursError.Text = result.Error ?? "";
        QuietHoursError.IsVisible = !result.IsValid;
        _quiet = result;
        UpdateSaveEnabled();
        return result;
    }

    private void TestAlert_Click(object? sender, RoutedEventArgs e) => _actions.SendTestAlert?.Invoke();

    private ClaudeSourceMode _source;

    /// <summary>Plugin | Automatic | Legacy | Off, as segments like Theme.</summary>
    private void BuildSourceSegments()
    {
        foreach (var option in UsageSourceOptions.All)
        {
            var segment = new Avalonia.Controls.Primitives.ToggleButton { Content = option.Label, Tag = option.Mode };
            segment.Classes.Add("segment");
            Avalonia.Automation.AutomationProperties.SetName(segment, "Usage source: " + option.Label);
            // The checked state, not Click: VoiceOver's toggle checks the
            // button without clicking it. Unchecking the chosen one puts it back.
            segment.IsCheckedChanged += (_, _) =>
            {
                if (_showingSource)
                    return;
                if (segment.IsChecked == true)
                    _source = option.Mode;
                ShowSource();
            };
            SourceSegments.Children.Add(segment);
        }
    }

    private bool _showingSource;

    /// <summary>The chosen source applies on Save, like the other settings.</summary>
    internal void ShowSource()
    {
        _showingSource = true;
        foreach (var segment in SourceSegments.Children.OfType<Avalonia.Controls.Primitives.ToggleButton>())
            segment.IsChecked = segment.Tag is ClaudeSourceMode mode && mode == _source;
        _showingSource = false;
        var option = UsageSourceOptions.For(_source);
        SourceDescription.Text = option.Description;
        SourceWarning.Text = option.Warning ?? "";
        SourceWarning.IsVisible = option.Warning is not null;
        PluginInstallRow.IsVisible = UsageSourceOptions.ShowsPluginInstall(_source, PluginConnected());
    }

    // ---------------------------------------------------------------- health

    /// <summary>A health item's dot: green, amber, red, or grey for a fact.</summary>
    public static readonly FuncValueConverter<HealthStatus, IBrush> HealthDotBrush = new(status => ThemeService.Resource<IBrush>(status switch
    {
        HealthStatus.Ok => "GoodBrush",
        HealthStatus.Warn => "WarnBrush",
        HealthStatus.Fail => "DangerBrush",
        _ => "TextTertiaryBrush"
    }));

    private HealthResult? _health;

    /// <summary>The items on screen, for tests.</summary>
    internal IReadOnlyList<HealthItem> HealthItems => _health?.Items ?? [];

    /// <summary>Runs the checks again (after a button, or when the Connect window closes).</summary>
    public async void RefreshHealth()
    {
        try
        {
            await RefreshHealthAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("SettingsWindow.RefreshHealth", ex);
        }
    }

    internal async Task RefreshHealthAsync()
    {
        if (_actions.CheckHealth is not { } check)
            return;
        HealthNoteText.Text = "Checking…";
        CheckHealthButton.IsEnabled = CopyReportButton.IsEnabled = false;
        ShowHealth(await check());
    }

    /// <summary>Paints a health result (tests hand one in directly).</summary>
    internal void ShowHealth(HealthResult result)
    {
        _health = result;
        HealthList.ItemsSource = result.Items;
        var needs = result.Items.Count(i => i.Status is HealthStatus.Fail or HealthStatus.Warn);
        HealthNoteText.Text = needs == 0 ? "Everything checked is working." : needs == 1 ? "One thing needs a look." : $"{needs} things need a look.";
        CheckHealthButton.IsEnabled = true;
        CopyReportButton.IsEnabled = result.Report.Length > 0;
    }

    private void RepaintHealth()
    {
        if (_health is { } health)
        {
            HealthList.ItemsSource = null;
            HealthList.ItemsSource = health.Items;
        }
    }

    /// <summary>Brings the Health section into view (the flyout's "Open Health").</summary>
    public void ShowHealth()
    {
        if (IsLoaded)
            HealthTitle.BringIntoView();
        else
            Opened += (_, _) => HealthTitle.BringIntoView();
    }

    private void CheckHealth_Click(object? sender, RoutedEventArgs e) => RefreshHealth();

    private async void CopyReport_Click(object? sender, RoutedEventArgs e)
    {
        if (_health is { Report.Length: > 0 } health)
            await CopyAsync(CopyReportButton, health.Report, "Copy report");
    }

    /// <summary>A health item's button: copies and links here, the rest by the app.</summary>
    private async void HealthAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string id } button)
            await RunHealthActionAsync(id, button);
    }

    internal async Task RunHealthActionAsync(string id, Button? button = null)
    {
        if (HealthActionIds.CopyText(id) is { } text)
        {
            if (button is not null)
                await CopyAsync(button, text, button.Content as string ?? "Copy");
            return;
        }
        if (HealthActionIds.Url(id) is { } url)
        {
            try
            {
                System.Diagnostics.Process.Start("open", url);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError("SettingsWindow.RunHealthAction", ex);
            }
            return;
        }
        if (id == HealthActionIds.OpenUsageSource)
        {
            SourceSegments.BringIntoView();
            return;
        }
        _actions.RunHealthAction?.Invoke(id);
        // Launch at login changed at once: the switch below shows it too (and only then,
        // so an unsaved change to it isn't undone by another button).
        if (id == HealthActionIds.ToggleStartup)
            StartAtLoginChangedElsewhere(_settings.StartWithWindows);
        if (id != HealthActionIds.Connect)
            RefreshHealth();
    }

    /// <summary>Launch at login changed outside the switch (Health, the menu bar menu): the switch
    /// shows it, and Save won't undo it.</summary>
    internal void StartAtLoginChangedElsewhere(bool on)
    {
        _startAtLogin.ChangedElsewhere(on);
        LaunchAtLoginToggle.IsChecked = on;
    }

    private async Task CopyAsync(Button button, string text, string label)
    {
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                throw new InvalidOperationException("No clipboard for this window.");
            await clipboard.SetTextAsync(text);
            button.Content = "Copied";
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("SettingsWindow.CopyAsync", ex);
            button.Content = "Couldn't copy";
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        button.Content = label;
    }

    private int _copyConfirmation;

    private async void CopyPluginCommand_Click(object? sender, RoutedEventArgs e)
    {
        var confirmation = ++_copyConfirmation;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                throw new InvalidOperationException("No clipboard for this window.");
            await clipboard.SetTextAsync(ClaudeCommands.InstallPlugin);
            CopyPluginCommandButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("SettingsWindow.CopyPluginCommand_Click", ex);
            CopyPluginCommandButton.Content = "Couldn't copy";
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (confirmation == _copyConfirmation)
            CopyPluginCommandButton.Content = "Copy";
    }

    private void DayTarget_Changed(object? sender, RoutedEventArgs e)
    {
        // Fires while the constructor is still filling in the window.
        if (DayTargetOptions is null || DayTargetBox is null)
            return;
        ShowDayTargetState();
        _dayTarget = ValidateDayTarget();
    }

    private bool DayTargetInUse => _dayTargetApplies && ShowDayTargetToggle.IsChecked == true;

    /// <summary>Hidden for plans with rolling limits, with a note saying why (five greyed-out
    /// controls read as broken); otherwise the options follow the Day target switch.</summary>
    private void ShowDayTargetState()
    {
        DayTargetSection.IsVisible = _dayTargetApplies;
        DayTargetNote.Text = DayTarget.NotApplicableNote;
        DayTargetNote.IsVisible = !_dayTargetApplies;

        DayTargetOptions.IsEnabled = DayTargetInUse;
        DayTargetOptions.Opacity = DayTargetInUse || !_dayTargetApplies ? 1 : 0.45;
    }

    /// <summary>The daily target only has to be valid while it's in use;
    /// Save is disabled while it isn't.</summary>
    internal DayTarget.Result ValidateDayTarget()
    {
        var result = DayTarget.Validate(DayTargetBox.Text ?? "");
        var showError = DayTargetInUse && !result.IsValid;
        DayTargetBox.Classes.Set("error", showError);
        DayTargetError.Text = result.Error ?? "";
        DayTargetError.IsVisible = showError;
        _dayTarget = result;
        UpdateSaveEnabled();
        return result;
    }

    private void UpdateSaveEnabled()
    {
        var dayOk = _dayTarget is null || _dayTarget.IsValid || !DayTargetInUse;
        SaveButton.IsEnabled = _thresholds is { IsValid: true } && dayOk && _quiet is not { IsValid: false };
    }

    /// <summary>Validates the thresholds, flags offending boxes, and resizes
    /// the zone preview. Save is disabled while they're invalid.</summary>
    internal Thresholds.Result ValidateThresholds()
    {
        var result = Thresholds.Validate(YellowBox.Text ?? "", OrangeBox.Text ?? "", RedBox.Text ?? "");

        YellowBox.Classes.Set("error", result.InvalidFields.HasFlag(Thresholds.Field.Yellow));
        OrangeBox.Classes.Set("error", result.InvalidFields.HasFlag(Thresholds.Field.Orange));
        RedBox.Classes.Set("error", result.InvalidFields.HasFlag(Thresholds.Field.Red));

        ThresholdError.Text = result.Error ?? "";
        ThresholdError.IsVisible = !result.IsValid;
        _thresholds = result;
        UpdateSaveEnabled();

        if (result.IsValid)
        {
            ZoneBar.ColumnDefinitions[0].Width = new GridLength(result.Yellow, GridUnitType.Star);
            ZoneBar.ColumnDefinitions[1].Width = new GridLength(result.Orange - result.Yellow, GridUnitType.Star);
            ZoneBar.ColumnDefinitions[2].Width = new GridLength(result.Red - result.Orange, GridUnitType.Star);
            ZoneBar.ColumnDefinitions[3].Width = new GridLength(Math.Max(0.5, 100 - result.Red), GridUnitType.Star);
        }
        return result;
    }

    /// <summary>The theme applies immediately (it's what you're looking at),
    /// so it isn't part of Save/Cancel.</summary>
    private void Theme_Click(object? sender, RoutedEventArgs e)
    {
        ThemeService.Preference =
            ReferenceEquals(sender, ThemeLightButton) ? ThemePreference.Light :
            ReferenceEquals(sender, ThemeDarkButton) ? ThemePreference.Dark :
            ThemePreference.System;
        ShowTheme();
    }

    private void ShowTheme()
    {
        var preference = ThemeService.Preference;
        ThemeSystemButton.IsChecked = preference == ThemePreference.System;
        ThemeLightButton.IsChecked = preference == ThemePreference.Light;
        ThemeDarkButton.IsChecked = preference == ThemePreference.Dark;
    }

    /// <summary>"12 fields in 3 rows", so the row says something without
    /// opening the customizer.</summary>
    internal void ShowFieldsSummary()
    {
        var layout = _settings.CardLayout();
        var fields = layout.Rows.Sum(r => r.Count);
        FieldsSummary.Text = fields == 0
            ? "No fields - the cards show only their heading"
            : $"{fields} field{(fields == 1 ? "" : "s")} in {layout.Rows.Count} row{(layout.Rows.Count == 1 ? "" : "s")}, " +
              $"chosen from {SessionFields.All.Count}";
    }

    private void CustomizeFields_Click(object? sender, RoutedEventArgs e) => _actions.CustomizeFields();

    private bool _syncingShareShape;

    /// <summary>The two shape segments act as one choice: checking one unchecks
    /// the other, and the chosen one can't be unchecked into no choice.</summary>
    private void SyncShareShape(Avalonia.Controls.Primitives.ToggleButton changed, Avalonia.Controls.Primitives.ToggleButton other)
    {
        if (_syncingShareShape)
            return;
        _syncingShareShape = true;
        if (changed.IsChecked == true)
            other.IsChecked = false;
        else if (other.IsChecked != true)
            changed.IsChecked = true;
        _syncingShareShape = false;
    }

    private void ResetPosition_Click(object? sender, RoutedEventArgs e) => _actions.ResetPosition();
    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) => _actions.OpenLogFolder();
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;

        _settings.ShowMonthlySpend = ShowUsageToggle.IsChecked == true;
        _settings.ShowForecast = ShowForecastToggle.IsChecked == true;
        _settings.ShowSessionLights = ShowSessionLightsToggle.IsChecked == true;
        _settings.SetProviderEnabled(CodexUsageProvider.Id, ShowCodexToggle.IsChecked == true);
        _settings.ShowDayTargetAmount = ShowDayAmountToggle.IsChecked == true;
        _settings.ShowDayTargetPercent = ShowDayPercentToggle.IsChecked == true;
        _settings.ShowDayTargetRing = ShowDayRingToggle.IsChecked == true;
        // Only a plan with a monthly cap can change these; a Pro/Max account
        // keeps whatever was set, ready for a monthly-cap account.
        if (_dayTargetApplies)
        {
            _settings.ShowDayTarget = ShowDayTargetToggle.IsChecked == true;
            if (_dayTarget is { IsValid: true } day)
                _settings.DayTargetUsd = day.Usd;
        }
        _settings.ShowSessionCost = ShowSessionCostToggle.IsChecked == true;
        _settings.YellowThresholdPercent = _thresholds.Yellow;
        _settings.OrangeThresholdPercent = _thresholds.Orange;
        _settings.RedThresholdPercent = _thresholds.Red;
        _settings.ClaudeSource = _source.ToSetting();
        _settings.CheckForUpdates = CheckForUpdatesToggle.IsChecked == true;
        _settings.ShareCardShape = (SharePortraitButton.IsChecked == true ? ShareCardShape.Portrait : ShareCardShape.Wide).ToSetting();
        _settings.ShareIncludeProjectNames = ShareIncludeProjectNamesToggle.IsChecked == true;
        _settings.AlertsEnabled = AlertsEnabledToggle.IsChecked == true;
        _settings.AlertOnThreshold = AlertOnThresholdToggle.IsChecked == true;
        _settings.AlertOnForecast = AlertOnForecastToggle.IsChecked == true;
        _settings.AlertOnLimit = AlertOnLimitToggle.IsChecked == true;
        _settings.AlertOnReset = AlertOnResetToggle.IsChecked == true;
        _settings.AlertOnNeedsYou = AlertOnNeedsYouToggle.IsChecked == true;
        _settings.AlertOnDone = AlertOnDoneToggle.IsChecked == true;
        var quietOn = QuietHoursToggle.IsChecked == true;
        _settings.AlertQuietFrom = quietOn ? QuietHours.ToSetting(_quiet.From) : null;
        _settings.AlertQuietTo = quietOn ? QuietHours.ToSetting(_quiet.To) : null;

        // Only a change made with this switch: Health or the menu bar item may have changed it meanwhile.
        if (_startAtLogin.OnSave(LaunchAtLoginToggle.IsChecked == true, _settings.StartWithWindows) is { } launchAtLogin)
        {
            _settings.StartWithWindows = launchAtLogin;
            LoginItem.SetEnabled(launchAtLogin);
        }

        SettingsService.Save(_settings);
        _actions.OnSaved();
        Close();
    }
}
