using System.Globalization;
using Avalonia.Controls;
using Avalonia.Interactivity;
using StickyAIusage.Mac.Native;
using StickyAIusage.Mac.Services;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac;

/// <summary>Settings (the macOS counterpart of the Windows settings window).
/// Changes apply on Save; Reset position and Open log folder act immediately.</summary>
public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly SettingsActions _actions;
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

        ShowUsageToggle.IsChecked = settings.ShowMonthlySpend;
        ShowSessionCostToggle.IsChecked = settings.ShowSessionCost;
        LaunchAtLoginToggle.IsChecked = settings.StartWithWindows;
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

    /// <summary>Off for plans with rolling limits, with a note saying why;
    /// otherwise the options follow the Day target switch.</summary>
    private void ShowDayTargetState()
    {
        DayTargetSection.IsEnabled = _dayTargetApplies;
        DayTargetSection.Opacity = _dayTargetApplies ? 1 : 0.45;
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
        SaveButton.IsEnabled = _thresholds is { IsValid: true } && dayOk;
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

    private void ResetPosition_Click(object? sender, RoutedEventArgs e) => _actions.ResetPosition();
    private void OpenLogFolder_Click(object? sender, RoutedEventArgs e) => _actions.OpenLogFolder();
    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        if (!SaveButton.IsEnabled)
            return;

        _settings.ShowMonthlySpend = ShowUsageToggle.IsChecked == true;
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

        var launchAtLogin = LaunchAtLoginToggle.IsChecked == true;
        if (launchAtLogin != _settings.StartWithWindows)
        {
            _settings.StartWithWindows = launchAtLogin;
            LoginItem.SetEnabled(launchAtLogin);
        }

        SettingsService.Save(_settings);
        _actions.OnSaved();
        Close();
    }
}
