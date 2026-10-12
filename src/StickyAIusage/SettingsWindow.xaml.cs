using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using StickyAIusage.Models;
using StickyAIusage.Native;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>What the settings window can ask the rest of the app to do.</summary>
public sealed record SettingsActions(Action OnSaved, Action ResetPosition, Action OpenLogFolder, Action CustomizeFields);

/// <summary>Settings dialog, opened from the tray menu or the
/// flyout's gear button. Changes apply on Save; Reset position and Open log
/// folder act immediately.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsActions _actions;
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

        ShowMonthlySpendCheck.IsChecked = settings.ShowMonthlySpend;
        ShowSessionCostCheck.IsChecked = settings.ShowSessionCost;
        StartWithWindowsCheck.IsChecked = settings.StartWithWindows;
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
    }

    private void DayTarget_Changed(object sender, RoutedEventArgs e)
    {
        // Fires while InitializeComponent is still building the window.
        if (DayTargetOptions is null)
            return;
        ShowDayTargetState();
        _dayTarget = ValidateDayTarget();
    }

    /// <summary>Off for plans with rolling limits, with a note saying why;
    /// otherwise the options follow the Day target switch.</summary>
    private void ShowDayTargetState()
    {
        DayTargetSection.IsEnabled = _dayTargetApplies;
        DayTargetSection.Opacity = _dayTargetApplies ? 1 : 0.45;
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
        SaveButton.IsEnabled = _thresholds is { IsValid: true } && dayOk;
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

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => _actions.ResetPosition();

    private void OpenLogFolder_Click(object sender, RoutedEventArgs e) => _actions.OpenLogFolder();

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;

        _settings.ShowMonthlySpend = ShowMonthlySpendCheck.IsChecked == true;
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

        var startWithWindows = StartWithWindowsCheck.IsChecked == true;
        if (startWithWindows != _settings.StartWithWindows)
        {
            _settings.StartWithWindows = startWithWindows;
            StartupRegistration.SetEnabled(startWithWindows);
        }

        SettingsService.Save(_settings);
        _actions.OnSaved();
        Close();
    }
}
