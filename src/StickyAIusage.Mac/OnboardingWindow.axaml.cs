using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using StickyAIusage.Health;
using StickyAIusage.Mac.Services;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac;

/// <summary>What the first-run window can ask the rest of the app to do.</summary>
/// <param name="SettingsChanged">The settings object changed and was saved (the widget re-reads it).</param>
/// <param name="SetStartAtLogin">Turns launch at login on or off (the LaunchAgent and the menu).</param>
public sealed record OnboardingActions(Action SettingsChanged, Action<bool> SetStartAtLogin);

/// <summary>
/// The first-run window (Core <see cref="Onboarding"/>), the macOS counterpart of
/// the Windows one: three steps for a new install, one screen for an upgrade, or
/// Connect alone from Settings → Health. A normal window: the app lives in the
/// menu bar, so it activates itself when shown.
/// </summary>
public partial class OnboardingWindow : Window
{
    private readonly AppSettings _settings;
    private readonly OnboardingActions _actions;
    private int _step;
    private int _copyConfirmation;

    public OnboardingScreen Screen { get; }
    public ConnectSession Connect { get; }

    /// <summary>Finish was pressed (step 3's choices were applied).</summary>
    public bool Finished { get; private set; }

    public OnboardingWindow() : this(new AppSettings(), OnboardingScreen.FirstRun,
        new ConnectSession(() => null, () => false, new ClaudeCliProcess(), null, PluginInstaller.DefaultSource),
        new OnboardingActions(() => { }, _ => { })) { } // designer

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
        ConnectTitleText.IsVisible = screen == OnboardingScreen.FirstRun;
        ConnectIntroText.Text = screen == OnboardingScreen.WhatsNew ? Onboarding.WhatsNewIntro : Onboarding.ConnectIntro;
        CommandText.Text = connect.ShellCommand;
        RunButton.Content = Onboarding.RunLabel;
        CopyButton.Content = Onboarding.CopyLabel;
        LegacyLinkButton.Content = Onboarding.LegacyLink;
        LegacyWarningText.Text = UsageSourceOptions.UnofficialWarning;
        LegacyButton.Content = Onboarding.LegacyButton;
        LegacyChosenText.Text = Onboarding.LegacyChosen;

        YoursTitleText.Text = Onboarding.YoursTitle;
        StartAtLoginLabel.Text = Onboarding.StartAtLoginLabel(isMac: true);
        AutomationProperties.SetName(StartAtLoginCheck, StartAtLoginLabel.Text);
        AlertsLabel.Text = Onboarding.AlertsLabel;
        AutomationProperties.SetName(AlertsCheck, Onboarding.AlertsLabel);
        AlertsCheck.IsChecked = settings.AlertsEnabled;
        PlacementText.Text = Onboarding.PlacementLine(isMac: true);
        FinishNoteText.Text = Onboarding.FinishNote;

        connect.Changed += ShowConnect;
        Closed += (_, _) => connect.Changed -= ShowConnect;
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
        ReadsPanel.IsVisible = step == 1;
        ConnectPanel.IsVisible = step == 2;
        YoursPanel.IsVisible = step == 3;
        StepCaptionText.Text = firstRun ? Onboarding.StepCaption(step) : "";
        BackButton.IsVisible = firstRun && step > 1;
        NextButton.Content = !firstRun ? "Done" : step == 3 ? "Finish" : "Next";
    }

    /// <summary>Paints the Connect step from the session's view.</summary>
    private void ShowConnect()
    {
        var view = Connect.View;
        RunButton.IsEnabled = view.CanRun && Connect.HasCli;
        ToolTip.SetTip(RunButton, Connect.HasCli ? $"Runs {Connect.ShellCommand}" : Onboarding.NoCliHint);
        ToolTip.SetShowOnDisabled(RunButton, true);
        CopyHintText.Text = Connect.HasCli ? Onboarding.CopyHint(Connect.Source) : Onboarding.NoCliHint + " " + Onboarding.CopyHint(Connect.Source);
        StatusBorder.IsVisible = view.Phase != ConnectPhase.Ready;
        StatusText.Text = view.Status;
        StatusDot.Fill = ThemeService.Resource<IBrush>(view.Phase switch
        {
            ConnectPhase.Connected or ConnectPhase.ConnectedNoNumbers => "GoodBrush",
            ConnectPhase.Failed => "DangerBrush",
            _ => "WarnBrush"
        });
        OutputBox.Text = view.Output ?? "";
        OutputBox.IsVisible = view.Output is not null;
    }

    private async void Run_Click(object? sender, RoutedEventArgs e)
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

    private async void Copy_Click(object? sender, RoutedEventArgs e)
    {
        var confirmation = ++_copyConfirmation;
        try
        {
            if (Clipboard is not { } clipboard)
                throw new InvalidOperationException("No clipboard for this window.");
            await clipboard.SetTextAsync(Connect.SessionCommand);
            CopyButton.Content = "Copied";
            Connect.MarkCopied();
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("OnboardingWindow.Copy_Click", ex);
            CopyButton.Content = "Couldn't copy";
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (confirmation == _copyConfirmation)
            CopyButton.Content = Onboarding.CopyLabel;
    }

    private void LegacyLink_Click(object? sender, RoutedEventArgs e) => LegacyPanel.IsVisible = !LegacyPanel.IsVisible;

    /// <summary>Legacy mode now: the widget switches at its next refresh, no restart.</summary>
    private void Legacy_Click(object? sender, RoutedEventArgs e)
    {
        _settings.ClaudeSource = ClaudeSourceMode.Legacy.ToSetting();
        SettingsService.Save(_settings);
        DiagnosticLog.WriteInfo("First run: legacy mode chosen.");
        _actions.SettingsChanged();
        LegacyChosenText.IsVisible = true;
        LegacyButton.IsEnabled = false;
    }

    private void Back_Click(object? sender, RoutedEventArgs e) => ShowStep(Math.Max(1, _step - 1));

    private void Next_Click(object? sender, RoutedEventArgs e)
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
