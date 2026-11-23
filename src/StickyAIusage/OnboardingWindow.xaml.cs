using System.Windows;
using System.Windows.Automation;
using System.Windows.Media;
using StickyAIusage.Health;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>What the first-run window can ask the rest of the app to do.</summary>
/// <param name="SettingsChanged">The settings object changed and was saved (the widget re-reads it).</param>
/// <param name="SetStartAtLogin">Turns start at sign-in on or off (the Run value and the tray menu).</param>
public sealed record OnboardingActions(Action SettingsChanged, Action<bool> SetStartAtLogin);

/// <summary>
/// The first-run window (Core <see cref="Onboarding"/>): three steps for a new
/// install, one screen for an upgrade, or Connect alone from Settings → Health.
/// Every word comes from Core, so both apps say the same. The Connect step
/// follows the widget's readings live (<see cref="ConnectSession"/>) and turns
/// green the moment the plugin's first numbers arrive.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly AppSettings _settings;
    private readonly OnboardingActions _actions;
    private int _step;

    public OnboardingScreen Screen { get; }
    public ConnectSession Connect { get; }

    /// <summary>Finish was pressed (step 3's choices were applied).</summary>
    public bool Finished { get; private set; }

    public OnboardingWindow(AppSettings settings, OnboardingScreen screen, ConnectSession connect, OnboardingActions actions)
    {
        _settings = settings;
        _actions = actions;
        Screen = screen;
        Connect = connect;
        InitializeComponent();

        Title = Onboarding.Caption(screen);
        WindowTitleText.Text = Onboarding.Heading(screen);

        ReadsTitleText.Text = Onboarding.ReadsTitle;
        ReadsList.ItemsSource = Onboarding.ReadsLines;
        NetworkLineText.Text = Onboarding.NetworkLine;
        UpdateCheckLabel.Text = Onboarding.UpdateCheckLabel;
        AutomationProperties.SetName(UpdateCheck, Onboarding.UpdateCheckLabel);
        UpdateCheck.IsChecked = settings.CheckForUpdates;

        ConnectTitleText.Text = Onboarding.ConnectTitle;
        // The upgrade screen and Connect from Health have their title in the header already.
        ConnectTitleText.Visibility = screen == OnboardingScreen.FirstRun ? Visibility.Visible : Visibility.Collapsed;
        ConnectIntroText.Text = screen == OnboardingScreen.WhatsNew ? Onboarding.WhatsNewIntro : Onboarding.ConnectIntro;
        CommandText.Text = connect.ShellCommand;
        RunButton.Content = Onboarding.RunLabel;
        CopyButton.Content = Onboarding.CopyLabel;
        LegacyLinkButton.Content = Onboarding.LegacyLink;
        LegacyWarningText.Text = UsageSourceOptions.UnofficialWarning;
        LegacyButton.Content = Onboarding.LegacyButton;
        LegacyChosenText.Text = Onboarding.LegacyChosen;

        YoursTitleText.Text = Onboarding.YoursTitle;
        StartAtLoginLabel.Text = Onboarding.StartAtLoginLabel(isMac: false);
        AutomationProperties.SetName(StartAtLoginCheck, StartAtLoginLabel.Text);
        AlertsLabel.Text = Onboarding.AlertsLabel;
        AutomationProperties.SetName(AlertsCheck, Onboarding.AlertsLabel);
        AlertsCheck.IsChecked = settings.AlertsEnabled;
        PlacementText.Text = Onboarding.PlacementLine(isMac: false);
        PlacementTitleText.Text = Onboarding.PlacementTitle;
        var dock = DockModes.Parse(settings.DockMode);
        DockTaskbarChoice.IsChecked = dock != DockMode.Above;
        DockAboveChoice.IsChecked = dock == DockMode.Above;
        FinishNoteText.Text = Onboarding.FinishNote;

        connect.Changed += ShowConnect;
        Closed += (_, _) => connect.Changed -= ShowConnect;
        // The status dot's color is set from code: paint it again with the new theme.
        ThemeService.Changed += ShowConnect;
        Closed += (_, _) => ThemeService.Changed -= ShowConnect;
        ShowStep(screen == OnboardingScreen.FirstRun ? 1 : 2);
        ShowConnect();
    }

    /// <summary>The step on screen (1–3); upgrade and Connect screens are step 2 alone.</summary>
    internal int Step => _step;

    internal void ShowStep(int step)
    {
        _step = step;
        var firstRun = Screen == OnboardingScreen.FirstRun;
        ReadsPanel.Visibility = step == 1 ? Visibility.Visible : Visibility.Collapsed;
        ConnectPanel.Visibility = step == 2 ? Visibility.Visible : Visibility.Collapsed;
        YoursPanel.Visibility = step == 3 ? Visibility.Visible : Visibility.Collapsed;
        StepCaptionText.Text = firstRun ? Onboarding.StepCaption(step) : "";
        BackButton.Visibility = firstRun && step > 1 ? Visibility.Visible : Visibility.Collapsed;
        NextButton.Content = !firstRun ? "Done" : step == 3 ? "Finish" : "Next";
    }

    /// <summary>Paints the Connect step from the session's view.</summary>
    private void ShowConnect()
    {
        var view = Connect.View;
        RunButton.IsEnabled = view.CanRun && Connect.HasCli;
        RunButton.ToolTip = Connect.HasCli ? $"Runs {Connect.ShellCommand}" : Onboarding.NoCliHint;
        System.Windows.Controls.ToolTipService.SetShowOnDisabled(RunButton, true);
        CopyHintText.Text = Connect.HasCli ? Onboarding.CopyHint(Connect.Source) : Onboarding.NoCliHint + " " + Onboarding.CopyHint(Connect.Source);
        StatusBorder.Visibility = view.Phase == ConnectPhase.Ready ? Visibility.Collapsed : Visibility.Visible;
        StatusText.Text = view.Status;
        StatusDot.Fill = (Brush)FindResource(view.Phase switch
        {
            ConnectPhase.Connected or ConnectPhase.ConnectedNoNumbers => "GoodBrush",
            ConnectPhase.Failed => "DangerBrush",
            _ => "WarnBrush"
        });
        OutputBox.Text = view.Output ?? "";
        OutputBox.Visibility = view.Output is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Connect.RunAsync();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("OnboardingWindow.Run_Click", ex);
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        CopyCommandButton.Copy(CopyButton, Connect.SessionCommand);
        Connect.MarkCopied();
    }

    private void LegacyLink_Click(object sender, RoutedEventArgs e) =>
        LegacyPanel.Visibility = LegacyPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Legacy mode now: the widget switches at its next refresh, no restart.</summary>
    private void Legacy_Click(object sender, RoutedEventArgs e)
    {
        _settings.ClaudeSource = ClaudeSourceMode.Legacy.ToSetting();
        SettingsService.Save(_settings);
        DiagnosticLog.WriteInfo("First run: legacy mode chosen.");
        _actions.SettingsChanged();
        LegacyChosenText.Visibility = Visibility.Visible;
        LegacyButton.IsEnabled = false;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(Math.Max(1, _step - 1));

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (Screen == OnboardingScreen.FirstRun && _step < 3)
        {
            ShowStep(_step + 1);
            return;
        }
        Finish();
    }

    /// <summary>Applies the choices (first run: step 1's update check and step 3's
    /// switches), marks the window done and closes it.</summary>
    internal void Finish()
    {
        if (Screen == OnboardingScreen.FirstRun)
        {
            _settings.CheckForUpdates = UpdateCheck.IsChecked == true;
            _settings.AlertsEnabled = AlertsCheck.IsChecked == true;
            _settings.StartWithWindows = StartAtLoginCheck.IsChecked == true;
            _actions.SetStartAtLogin(_settings.StartWithWindows);
            _settings.DockMode = (DockAboveChoice.IsChecked == true ? DockMode.Above : DockMode.Taskbar).ToSetting();
            _settings.WidgetDocked = true;
        }
        if (Screen != OnboardingScreen.Connect)
            Onboarding.Complete(_settings);
        SettingsService.Save(_settings);
        _actions.SettingsChanged();
        Finished = true;
        DiagnosticLog.WriteInfo($"First-run window finished ({Screen}).");
        Close();
    }
}
