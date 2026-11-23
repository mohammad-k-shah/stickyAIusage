using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickyAIusage.Alerts;
using StickyAIusage.Models;
using StickyAIusage.Native;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>What the settings window can ask the rest of the app to do.</summary>
/// <param name="CheckHealth">Runs Settings → Health; null: the section says it isn't available.</param>
/// <param name="RunHealthAction">A Health button the app carries out (<see cref="Health.HealthActionIds"/>: Connect,
/// test alert, start at sign-in, log folder); copy, link and Usage source buttons the window handles itself.</param>
public sealed record SettingsActions(Action OnSaved, Action ResetPosition, Action OpenLogFolder, Action CustomizeFields,
    Action? SendTestAlert = null, Func<Task<Health.HealthResult>>? CheckHealth = null, Action<string>? RunHealthAction = null);

/// <summary>Settings dialog, opened from the tray menu or the
/// flyout's gear button. Changes apply on Save; Reset position and Open log
/// folder act immediately.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ViewModels.StartAtLoginSwitch _startAtLogin;
    private readonly SettingsActions _actions;

    /// <summary>Whether Codex is on this computer, for the Codex row's hint; tests replace it.</summary>
    /// <summary>Whether the Claude Code plugin has connected (its marker in the feed folder): then
    /// Usage source needn't offer its install command. Tests set their own.</summary>
    internal static Func<bool> PluginConnected { get; set; } =
        () => System.IO.File.Exists(System.IO.Path.Combine(AppPaths.FeedDirectory, ClaudeFeedUsageSource.MarkerFileName));

    internal static Func<bool> CodexPresent { get; set; } =
        () => new CodexUsageProvider(AppPaths.CodexDirectory, () => DateTimeOffset.UtcNow).IsPresent;
    private Thresholds.Result _thresholds;
    private DayTarget.Result _dayTarget;
    private readonly bool _dayTargetApplies;

    /// <param name="mode">The plan layout of the latest reading: the day
    /// target is turned off for plans with rolling limits.</param>
    public SettingsWindow(AppSettings settings, SettingsActions actions, UsageMode mode = UsageMode.Unknown)
    {
        _settings = settings;
        _actions = actions;
        _dayTargetApplies = DayTarget.AppliesTo(mode);
        InitializeComponent();

        BuildInfoText.Text = AppIdentity.Describe(AppIdentity.Version(typeof(App).Assembly), AppPaths.Current, settings);
        ShowMonthlySpendCheck.IsChecked = settings.ShowMonthlySpend;
        ShowForecastCheck.IsChecked = settings.ShowForecast;
        ShowSessionLightsCheck.IsChecked = settings.ShowSessionLights;
        ShowCodexCheck.IsChecked = settings.IsProviderEnabled(CodexUsageProvider.Id);
        CodexHintText.Text = OtherProviders.CodexSettingsHint(CodexPresent());
        ShowSessionCostCheck.IsChecked = settings.ShowSessionCost;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
        _startAtLogin = new ViewModels.StartAtLoginSwitch(settings.StartWithWindows);
        CheckForUpdatesCheck.IsChecked = settings.CheckForUpdates;
        HideWhenFullscreenCheck.IsChecked = settings.HideWhenFullscreen;
        ShowDock(DockModes.Parse(settings.DockMode));
        var monitors = MonitorList();
        DockMonitorRow.Visibility = monitors.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        DockMonitorChoice = DockMonitors.Selected(settings.DockMonitor, monitors);
        foreach (var (label, setting) in DockMonitors.Choices(monitors))
        {
            var segment = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = label, Tag = setting, Style = (Style)FindResource("Segment"), IsChecked = setting == DockMonitorChoice
            };
            segment.Checked += (_, _) => ShowMonitor(setting);
            segment.Unchecked += (_, _) => { if (DockMonitorChoice == setting) ShowMonitor(setting); };
            DockMonitorSegments.Children.Add(segment);
        }
        var portrait = ShareCardShapes.Parse(settings.ShareCardShape) == ShareCardShape.Portrait;
        SharePortraitButton.IsChecked = portrait;
        ShareWideButton.IsChecked = !portrait;
        ShareIncludeProjectNamesCheck.IsChecked = settings.ShareIncludeProjectNames;
        ShowDayTargetCheck.IsChecked = settings.ShowDayTarget;
        ShowDayAmountCheck.IsChecked = settings.ShowDayTargetAmount;
        ShowDayPercentCheck.IsChecked = settings.ShowDayTargetPercent;
        ShowDayRingCheck.IsChecked = settings.ShowDayTargetRing;
        DayTargetBox.Text = DayTarget.ToText(settings.DayTargetUsd);
        YellowBox.Text = settings.YellowThresholdPercent.ToString("0.##", CultureInfo.CurrentCulture);
        OrangeBox.Text = settings.OrangeThresholdPercent.ToString("0.##", CultureInfo.CurrentCulture);
        RedBox.Text = settings.RedThresholdPercent.ToString("0.##", CultureInfo.CurrentCulture);

        ShowFieldsSummary();
        ShowTheme();
        ThemeService.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeService.Changed -= OnThemeChanged;

        _thresholds = ValidateThresholds();
        YellowBox.TextChanged += (_, _) => _thresholds = ValidateThresholds();
        OrangeBox.TextChanged += (_, _) => _thresholds = ValidateThresholds();
        RedBox.TextChanged += (_, _) => _thresholds = ValidateThresholds();

        _dayTarget = ValidateDayTarget();
        DayTargetBox.TextChanged += (_, _) => _dayTarget = ValidateDayTarget();
        ShowDayTargetState();

        _source = ClaudeSourceModes.Parse(settings.ClaudeSource);
        BuildSourceSegments();
        PluginCommandText.Text = ClaudeCommands.InstallPlugin;
        ShowSource();

        AlertsEnabledCheck.IsChecked = settings.AlertsEnabled;
        AlertOnThresholdCheck.IsChecked = settings.AlertOnThreshold;
        AlertOnForecastCheck.IsChecked = settings.AlertOnForecast;
        AlertOnLimitCheck.IsChecked = settings.AlertOnLimit;
        AlertOnResetCheck.IsChecked = settings.AlertOnReset;
        AlertOnNeedsYouCheck.IsChecked = settings.AlertOnNeedsYou;
        AlertOnDoneCheck.IsChecked = settings.AlertOnDone;
        var quiet = QuietHours.FromSettings(settings.AlertQuietFrom, settings.AlertQuietTo);
        QuietHoursCheck.IsChecked = quiet is not null;
        QuietFromBox.Text = QuietHours.ToSetting(quiet?.From ?? DefaultQuietFrom);
        QuietToBox.Text = QuietHours.ToSetting(quiet?.To ?? DefaultQuietTo);
        _quiet = ValidateQuietHours();
        QuietFromBox.TextChanged += (_, _) => _quiet = ValidateQuietHours();
        QuietToBox.TextChanged += (_, _) => _quiet = ValidateQuietHours();
        ShowAlertState();

        if (actions.CheckHealth is null)
            HealthTitle.Visibility = HealthCard.Visibility = Visibility.Collapsed;
        else
            RefreshHealth();
    }

    // ---------------------------------------------------------------- health

    private Health.HealthResult? _health;

    /// <summary>The items on screen, for tests.</summary>
    internal IReadOnlyList<Health.HealthItem> HealthItems => _health?.Items ?? [];

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
    internal void ShowHealth(Health.HealthResult result)
    {
        _health = result;
        HealthList.ItemsSource = result.Items;
        var needs = result.Items.Count(i => i.Status is Health.HealthStatus.Fail or Health.HealthStatus.Warn);
        HealthNoteText.Text = needs == 0 ? "Everything checked is working." : needs == 1 ? "One thing needs a look." : $"{needs} things need a look.";
        CheckHealthButton.IsEnabled = true;
        CopyReportButton.IsEnabled = result.Report.Length > 0;
    }

    /// <summary>Brings the Health section into view (the flyout's "Open Health").</summary>
    public void ShowHealth()
    {
        if (IsLoaded)
            HealthTitle.BringIntoView();
        else
            Loaded += (_, _) => HealthTitle.BringIntoView();
    }

    private void CheckHealth_Click(object sender, RoutedEventArgs e) => RefreshHealth();

    private void CopyReport_Click(object sender, RoutedEventArgs e)
    {
        if (_health is { Report.Length: > 0 } health)
            CopyCommandButton.Copy(CopyReportButton, health.Report);
    }

    /// <summary>A health item's button: copies and links here, the rest by the app.</summary>
    private void HealthAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string id } button)
            return;
        RunHealthAction(id, button);
    }

    internal void RunHealthAction(string id, Button? button = null)
    {
        if (Health.HealthActionIds.CopyText(id) is { } text)
        {
            if (button is not null)
                CopyCommandButton.Copy(button, text);
            return;
        }
        if (Health.HealthActionIds.Url(id) is { } url)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError("SettingsWindow.RunHealthAction", ex);
            }
            return;
        }
        if (id == Health.HealthActionIds.OpenUsageSource)
        {
            SourceSegments.BringIntoView();
            return;
        }
        _actions.RunHealthAction?.Invoke(id);
        // Start at sign-in changed at once: the switch below shows it too (and only then,
        // so an unsaved change to it isn't undone by another button).
        if (id == Health.HealthActionIds.ToggleStartup)
            StartAtLoginChangedElsewhere(_settings.StartWithWindows);
        if (id != Health.HealthActionIds.Connect)
            RefreshHealth();
    }

    /// <summary>Start with Windows changed outside the switch (Health, the tray menu): the switch
    /// shows it, and Save won't undo it.</summary>
    internal void StartAtLoginChangedElsewhere(bool on)
    {
        _startAtLogin.ChangedElsewhere(on);
        StartWithWindowsCheck.IsChecked = on;
    }

    private static readonly TimeOnly DefaultQuietFrom = new(22, 0);
    private static readonly TimeOnly DefaultQuietTo = new(7, 0);
    private QuietHours.Result _quiet = new(true, default, default, false, false, null);

    private void Alerts_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent is still building the window.
        if (AlertOptions is null || QuietHoursOptions is null || QuietToBox is null)
            return;
        ShowAlertState();
        _quiet = ValidateQuietHours();
    }

    /// <summary>The kinds and the quiet hours follow their switches, like the day target's options.</summary>
    private void ShowAlertState()
    {
        var on = AlertsEnabledCheck.IsChecked == true;
        AlertOptions.IsEnabled = on;
        AlertOptions.Opacity = on ? 1 : 0.45;
        var quiet = QuietHoursCheck.IsChecked == true;
        QuietHoursOptions.IsEnabled = quiet;
        QuietHoursOptions.Opacity = quiet ? 1 : 0.45;
    }

    /// <summary>The two times only have to be valid while quiet hours are on;
    /// Save is disabled while they aren't.</summary>
    internal QuietHours.Result ValidateQuietHours()
    {
        var result = QuietHours.Validate(QuietHoursCheck.IsChecked == true, QuietFromBox.Text, QuietToBox.Text);
        QuietFromBox.Tag = result.FromInvalid ? "error" : null;
        QuietToBox.Tag = result.ToInvalid ? "error" : null;
        QuietHoursError.Text = result.Error ?? "";
        QuietHoursError.Visibility = result.IsValid ? Visibility.Collapsed : Visibility.Visible;
        _quiet = result;
        UpdateSaveEnabled();
        return result;
    }

    private void TestAlert_Click(object sender, RoutedEventArgs e) => _actions.SendTestAlert?.Invoke();

    private ClaudeSourceMode _source;

    /// <summary>Plugin | Automatic | Legacy | Off, as segments like Theme.</summary>
    private void BuildSourceSegments()
    {
        foreach (var option in UsageSourceOptions.All)
        {
            var segment = new System.Windows.Controls.Primitives.ToggleButton
            {
                Content = option.Label,
                Tag = option.Mode,
                Style = (Style)FindResource("Segment")
            };
            System.Windows.Automation.AutomationProperties.SetName(segment, "Usage source: " + option.Label);
            // Checked, not Click: a screen reader's toggle (UI Automation)
            // checks the button without clicking it.
            segment.Checked += (_, _) =>
            {
                if (_showingSource)
                    return;
                _source = option.Mode;
                ShowSource();
            };
            // Clicking the chosen segment again would leave none chosen.
            segment.Unchecked += (_, _) =>
            {
                if (!_showingSource)
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
        foreach (var segment in SourceSegments.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
            segment.IsChecked = segment.Tag is ClaudeSourceMode mode && mode == _source;
        _showingSource = false;
        var option = UsageSourceOptions.For(_source);
        SourceDescription.Text = option.Description;
        SourceWarning.Text = option.Warning ?? "";
        SourceWarning.Visibility = option.Warning is null ? Visibility.Collapsed : Visibility.Visible;
        PluginInstallRow.Visibility = UsageSourceOptions.ShowsPluginInstall(_source, PluginConnected()) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void CopyPluginCommand_Click(object sender, RoutedEventArgs e) =>
        CopyCommandButton.Copy(CopyPluginCommandButton, ClaudeCommands.InstallPlugin);

    private void DayTarget_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent is still building the window.
        if (DayTargetOptions is null)
            return;
        ShowDayTargetState();
        _dayTarget = ValidateDayTarget();
    }

    /// <summary>Hidden for plans with rolling limits, with a note saying why (five greyed-out
    /// controls read as broken); otherwise the options follow the Day target switch.</summary>
    private void ShowDayTargetState()
    {
        DayTargetSection.Visibility = _dayTargetApplies ? Visibility.Visible : Visibility.Collapsed;
        DayTargetNote.Text = DayTarget.NotApplicableNote;
        DayTargetNote.Visibility = _dayTargetApplies ? Visibility.Collapsed : Visibility.Visible;

        var on = _dayTargetApplies && ShowDayTargetCheck.IsChecked == true;
        DayTargetOptions.IsEnabled = on;
        DayTargetOptions.Opacity = on || !_dayTargetApplies ? 1 : 0.45;
    }

    /// <summary>The daily target only has to be valid while it's in use;
    /// Save is disabled while it isn't.</summary>
    internal DayTarget.Result ValidateDayTarget()
    {
        var result = DayTarget.Validate(DayTargetBox.Text);
        var matters = _dayTargetApplies && ShowDayTargetCheck.IsChecked == true;
        var showError = matters && !result.IsValid;
        DayTargetBox.Tag = showError ? "error" : null;
        DayTargetError.Text = result.Error ?? "";
        DayTargetError.Visibility = showError ? Visibility.Visible : Visibility.Collapsed;
        UpdateSaveEnabled(result);
        return result;
    }

    private void UpdateSaveEnabled(DayTarget.Result? dayTarget = null)
    {
        var day = dayTarget ?? _dayTarget;
        var dayOk = day is null || day.IsValid || !(_dayTargetApplies && ShowDayTargetCheck.IsChecked == true);
        SaveButton.IsEnabled = _thresholds is { IsValid: true } && dayOk && _quiet is not { IsValid: false };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Taller than a 1080p screen at 125% scaling; scroll rather than run off it.
        MaxHeight = SystemParameters.WorkArea.Height - 24;
        MaxWidth = SystemParameters.WorkArea.Width - 24;
        CenterOnWorkArea();
        // SizeToContent settles the height after the window is placed, so
        // CenterScreen can leave the title bar above the screen top; re-center
        // whenever the size lands.
        SizeChanged += (_, _) => CenterOnWorkArea();
        ApplyTitleBar();
    }

    /// <summary>Equal space left/right and top/bottom inside the work area
    /// (the screen minus the taskbar).</summary>
    private void CenterOnWorkArea()
    {
        var work = SystemParameters.WorkArea;
        Left = work.Left + (work.Width - ActualWidth) / 2;
        Top = work.Top + (work.Height - ActualHeight) / 2;
    }

    private void ApplyTitleBar() =>
        WindowTheme.ApplyTitleBar(this, (Color)FindResource("WindowBackgroundColor"), ThemeService.IsDark);

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

    private void CustomizeFields_Click(object sender, RoutedEventArgs e) => _actions.CustomizeFields();

    /// <summary>The theme applies immediately (it's what you're looking at),
    /// so it isn't part of Save/Cancel.</summary>
    private void Theme_Click(object sender, RoutedEventArgs e)
    {
        ThemeService.Preference =
            ReferenceEquals(sender, ThemeLightButton) ? ThemePreference.Light :
            ReferenceEquals(sender, ThemeDarkButton) ? ThemePreference.Dark :
            ThemePreference.System;
        ShowTheme();
    }

    private void OnThemeChanged()
    {
        ShowTheme();
        ApplyTitleBar();
    }

    private void ShowTheme()
    {
        var preference = ThemeService.Preference;
        ThemeSystemButton.IsChecked = preference == ThemePreference.System;
        ThemeLightButton.IsChecked = preference == ThemePreference.Light;
        ThemeDarkButton.IsChecked = preference == ThemePreference.Dark;
    }

    /// <summary>Validates the three thresholds, flags offending boxes, and
    /// resizes the zone preview. Save is disabled while they're invalid.</summary>
    internal Thresholds.Result ValidateThresholds()
    {
        var result = Thresholds.Validate(YellowBox.Text, OrangeBox.Text, RedBox.Text);

        YellowBox.Tag = result.InvalidFields.HasFlag(Thresholds.Field.Yellow) ? "error" : null;
        OrangeBox.Tag = result.InvalidFields.HasFlag(Thresholds.Field.Orange) ? "error" : null;
        RedBox.Tag = result.InvalidFields.HasFlag(Thresholds.Field.Red) ? "error" : null;

        ThresholdError.Text = result.Error ?? "";
        ThresholdError.Visibility = result.IsValid ? Visibility.Collapsed : Visibility.Visible;
        _thresholds = result;
        UpdateSaveEnabled();

        if (result.IsValid)
        {
            GreenZone.Width = new GridLength(result.Yellow, GridUnitType.Star);
            YellowZone.Width = new GridLength(result.Orange - result.Yellow, GridUnitType.Star);
            OrangeZone.Width = new GridLength(result.Red - result.Orange, GridUnitType.Star);
            RedZone.Width = new GridLength(Math.Max(0.5, 100 - result.Red), GridUnitType.Star);
        }
        return result;
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e)
    {
        _actions.ResetPosition();
        // Reset ends "free": the choice below shows where it docked.
        ShowDock(DockModes.Parse(_settings.DockMode));
    }

    // ------------------------------------------------------------- docking

    /// <summary>The monitors (device, primary); tests replace it.</summary>
    internal static Func<IReadOnlyList<(string Device, bool IsPrimary)>> MonitorList { get; set; } =
        () => Native.TaskbarDock.Monitors().Select(m => (m.Device, m.IsPrimary)).ToList();

    /// <summary>The dock place chosen here, applied on Save; null while the pill is left where it was dragged.</summary>
    internal DockMode? DockChoice { get; private set; }

    /// <summary>The monitor chosen here ("primary" or a device name), applied on Save.</summary>
    internal string DockMonitorChoice { get; private set; } = AppSettings.PrimaryMonitor;

    private bool _showingMonitor;

    private void ShowMonitor(string setting)
    {
        if (_showingMonitor)
            return;
        _showingMonitor = true;
        DockMonitorChoice = setting;
        foreach (var segment in DockMonitorSegments.Children.OfType<System.Windows.Controls.Primitives.ToggleButton>())
            segment.IsChecked = (string)segment.Tag == setting;
        _showingMonitor = false;
    }

    private bool _showingDock;

    private void ShowDock(DockMode mode)
    {
        _showingDock = true;
        DockChoice = mode == DockMode.Free ? null : mode;
        DockTaskbarButton.IsChecked = mode == DockMode.Taskbar;
        DockAboveButton.IsChecked = mode == DockMode.Above;
        DockHintText.Text = DockModes.SettingsHint(mode);
        _showingDock = false;
    }

    // The checked state, not Click: a screen reader's Toggle raises no Click.
    private void Dock_Checked(object sender, RoutedEventArgs e)
    {
        if (_showingDock)
            return;
        ShowDock(sender == DockAboveButton ? DockMode.Above : DockMode.Taskbar);
    }

    private void Dock_Unchecked(object sender, RoutedEventArgs e)
    {
        // A segment can't be switched off by itself: the chosen one stays chosen.
        if (!_showingDock && sender is System.Windows.Controls.Primitives.ToggleButton button &&
            DockChoice == (button == DockAboveButton ? DockMode.Above : DockMode.Taskbar))
            ShowDock(DockChoice.Value);
    }

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => _actions.OpenLogFolder();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;

        _settings.ShowMonthlySpend = ShowMonthlySpendCheck.IsChecked == true;
        _settings.ShowForecast = ShowForecastCheck.IsChecked == true;
        _settings.ShowSessionLights = ShowSessionLightsCheck.IsChecked == true;
        _settings.SetProviderEnabled(CodexUsageProvider.Id, ShowCodexCheck.IsChecked == true);
        _settings.ShowDayTargetAmount = ShowDayAmountCheck.IsChecked == true;
        _settings.ShowDayTargetPercent = ShowDayPercentCheck.IsChecked == true;
        _settings.ShowDayTargetRing = ShowDayRingCheck.IsChecked == true;
        // Only a plan with a monthly cap can change these; a Pro/Max account
        // keeps whatever was set, ready for a monthly-cap account.
        if (_dayTargetApplies)
        {
            _settings.ShowDayTarget = ShowDayTargetCheck.IsChecked == true;
            if (_dayTarget.IsValid)
                _settings.DayTargetUsd = _dayTarget.Usd;
        }
        _settings.ShowSessionCost = ShowSessionCostCheck.IsChecked == true;
        _settings.YellowThresholdPercent = _thresholds.Yellow;
        _settings.OrangeThresholdPercent = _thresholds.Orange;
        _settings.RedThresholdPercent = _thresholds.Red;
        _settings.ClaudeSource = _source.ToSetting();
        _settings.CheckForUpdates = CheckForUpdatesCheck.IsChecked == true;
        _settings.ShareCardShape = (SharePortraitButton.IsChecked == true ? ShareCardShape.Portrait : ShareCardShape.Wide).ToSetting();
        _settings.ShareIncludeProjectNames = ShareIncludeProjectNamesCheck.IsChecked == true;
        _settings.AlertsEnabled = AlertsEnabledCheck.IsChecked == true;
        _settings.AlertOnThreshold = AlertOnThresholdCheck.IsChecked == true;
        _settings.AlertOnForecast = AlertOnForecastCheck.IsChecked == true;
        _settings.AlertOnLimit = AlertOnLimitCheck.IsChecked == true;
        _settings.AlertOnReset = AlertOnResetCheck.IsChecked == true;
        _settings.AlertOnNeedsYou = AlertOnNeedsYouCheck.IsChecked == true;
        _settings.AlertOnDone = AlertOnDoneCheck.IsChecked == true;
        var quietOn = QuietHoursCheck.IsChecked == true;
        _settings.AlertQuietFrom = quietOn ? QuietHours.ToSetting(_quiet.From) : null;
        _settings.AlertQuietTo = quietOn ? QuietHours.ToSetting(_quiet.To) : null;

        _settings.HideWhenFullscreen = HideWhenFullscreenCheck.IsChecked == true;
        if (DockChoice is { } dock)
        {
            _settings.DockMode = dock.ToSetting();
            _settings.WidgetDocked = true;
        }
        if (DockMonitorRow.Visibility == Visibility.Visible)
            _settings.DockMonitor = DockMonitorChoice;

        // Only a change made with this switch: Health or the tray may have changed it meanwhile.
        if (_startAtLogin.OnSave(StartWithWindowsCheck.IsChecked == true, _settings.StartWithWindows) is { } startWithWindows)
        {
            _settings.StartWithWindows = startWithWindows;
            StartupRegistration.SetEnabled(startWithWindows);
        }

        SettingsService.Save(_settings);
        _actions.OnSaved();
        Close();
    }
}
