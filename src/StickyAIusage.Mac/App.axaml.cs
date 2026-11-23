using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using StickyAIusage.Alerts;
using StickyAIusage.Health;
using StickyAIusage.Mac.Native;
using StickyAIusage.Mac.Services;
using StickyAIusage.Services;

namespace StickyAIusage.Mac;

/// <summary>
/// App entry point: the widget (a floating pill that starts in the menu bar
/// strip, next to the status icons) plus a menu bar icon whose menu holds
/// Settings, Launch at login, Reset widget position, Open log folder and
/// Quit - the macOS counterpart of the Windows tray icon.
/// </summary>
public partial class App : Application
{
    private AppSettings _settings = new();
    private WidgetWindow? _widget;
    private SettingsWindow? _settingsWindow;
    private FieldsWindow? _fieldsWindow;
    private OnboardingWindow? _onboardingWindow;
    private DateTimeOffset? _lastTestAlertAt;
    private NativeMenuItem? _launchAtLoginItem;
    private FileSignal? _showSignal;
    private IAlertSink? _alertSink;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;
        // macOS's own quit (Cmd-Q, AppleScript "quit", logging out) ends the lifetime here and may
        // end the process before Main's cleanup runs: the demo's temporary profile goes now.
        desktop.Exit += (_, _) => Demo.DemoMode.Active?.Dispose();

        // Nothing may end the app without a line in the log: a UI-thread
        // exception is logged and the pill stays (Program.Main hooks the rest).
        Dispatcher.UIThread.UnhandledException += (_, args) => args.Handled = UnhandledErrors.Shared.OnUiThread(args.Exception);

        // async void: an exception here would end the process unseen.
        try
        {
            await StartAsync();
        }
        catch (Exception ex)
        {
            UnhandledErrors.Shared.OnStartupFailed(ex);
            if (_widget is not { IsVisible: true })
                Quit(); // no menu bar item to quit from: the log says why
        }
    }

    private async Task StartAsync()
    {
        _settings = SettingsService.Load();
        ThemeService.Initialize(_settings);
        DiagnosticLog.WriteInfo(AppIdentity.StartLine("macOS", AppIdentity.Version(typeof(App).Assembly), AppPaths.Current, _settings) +
                                $" · theme {_settings.Theme} (dark: {ThemeService.IsDark})");

        _alertSink = new MacAlertSink(Demo.DemoMode.InstanceLabel(AppPaths.Current));
        _widget = new WidgetWindow(_settings);
        _widget.AlertsRaised += alerts => _alertSink?.Show(alerts);
        _widget.SettingsRequested += (_, _) => ShowSettings();
        _widget.LogFolderRequested += (_, _) => OpenLogFolder();
        _widget.HealthRequested += (_, _) => ShowSettings(showHealth: true);
        _widget.ConnectRequested += (_, _) =>
        {
            _widget.Collapse();
            ShowOnboarding(OnboardingScreen.Connect);
        };
        _widget.QuitRequested += (_, _) => Quit();
        CreateMenuBarIcon();

        // A second launch of this profile touches the signal file (Program.Main).
        _showSignal = new FileSignal(SingleInstance.SignalPath, () => Dispatcher.UIThread.Post(() =>
        {
            DiagnosticLog.WriteInfo("Started again while running: opening the flyout.");
            _widget?.ShowAndExpand();
        }));

        // Show it once the first reading is in, so it opens in the right
        // layout (monthly spend or plan limits); don't wait long on a slow network.
        await Task.WhenAny(_widget.FirstRefresh, Task.Delay(TimeSpan.FromSeconds(5)));
        _widget.Show();
        ShowOnboardingIfDue();
    }

    /// <summary>A new install's three steps, or an upgrade's one screen, once.
    /// Closing the window counts as done, like Finish without its choices.</summary>
    private void ShowOnboardingIfDue()
    {
        if (_widget is null)
            return;
        var screen = Onboarding.ScreenFor(_settings, _widget.PluginWrote);
        if (screen == OnboardingScreen.None)
        {
            // An upgrade whose plugin already writes: nothing new to say, and nothing to ask next time.
            if (Onboarding.Complete(_settings))
                SettingsService.Save(_settings);
            return;
        }
        DiagnosticLog.WriteInfo($"First-run window: {screen}.");
        ShowOnboarding(screen);
    }

    /// <summary>The first-run window, or Connect alone (from Settings → Health). A menu
    /// bar app has no Dock icon to bring forward: the window activates itself.</summary>
    private void ShowOnboarding(OnboardingScreen screen)
    {
        if (_widget is not { } widget)
            return;
        if (_onboardingWindow is not null)
        {
            _onboardingWindow.Activate();
            return;
        }
        var connect = ConnectSession.ForThisComputer(() => widget.LatestUsage, () => widget.PluginWrote);
        void Repaint() => connect.Refresh();
        widget.UsageApplied += Repaint;
        _onboardingWindow = new OnboardingWindow(_settings, screen, connect,
            new OnboardingActions(() => widget.RefreshFromSettings(), SetLaunchAtLogin));
        _onboardingWindow.Closed += (_, _) =>
        {
            widget.UsageApplied -= Repaint;
            if (screen != OnboardingScreen.Connect && Onboarding.Complete(_settings))
            {
                SettingsService.Save(_settings);
                DiagnosticLog.WriteInfo("First-run window closed.");
            }
            _onboardingWindow = null;
            _settingsWindow?.RefreshHealth();
        };
        _onboardingWindow.Show();
        _onboardingWindow.Activate();
    }

    /// <summary>Launch at login, from the menu, Settings → Health or the first-run window: at once.</summary>
    private void SetLaunchAtLogin(bool on)
    {
        _settings.StartWithWindows = on;
        SettingsService.Save(_settings);
        LoginItem.SetEnabled(on);
        if (_launchAtLoginItem is not null)
            _launchAtLoginItem.IsChecked = on;
        _settingsWindow?.StartAtLoginChangedElsewhere(on);
    }

    private void CreateMenuBarIcon()
    {
        var menu = new NativeMenu();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowSettings();

        var muteItem = new NativeMenuItem(AlertMute.MenuLabel(_settings, DateTimeOffset.UtcNow));
        muteItem.Click += (_, _) =>
        {
            AlertMute.Toggle(_settings, DateTimeOffset.UtcNow);
            SettingsService.Save(_settings);
            DiagnosticLog.WriteInfo(_settings.AlertsMutedUntil is { } until
                ? $"Alerts muted until {until.ToLocalTime():HH:mm}."
                : "Alerts unmuted.");
            muteItem.Header = AlertMute.MenuLabel(_settings, DateTimeOffset.UtcNow);
        };
        // The label says until when; it is worked out each time the menu opens.
        menu.NeedsUpdate += (_, _) => muteItem.Header = AlertMute.MenuLabel(_settings, DateTimeOffset.UtcNow);

        _launchAtLoginItem = new NativeMenuItem("Launch at login")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _settings.StartWithWindows
        };
        _launchAtLoginItem.Click += (_, _) => SetLaunchAtLogin(!_settings.StartWithWindows);

        var resetItem = new NativeMenuItem("Reset widget position");
        resetItem.Click += (_, _) => _widget?.ResetPosition();

        var logsItem = new NativeMenuItem("Open log folder");
        logsItem.Click += (_, _) => OpenLogFolder();

        var quitItem = new NativeMenuItem("Quit Sticky AI Usage");
        quitItem.Click += (_, _) => Quit();

        // "Update available: v2.0.1 — what's new" at the top while a newer version is known
        // (Check for updates on): added and removed as the menu opens.
        var updateItem = new NativeMenuItem();
        var updateSeparator = new NativeMenuItemSeparator();
        updateItem.Click += (_, _) =>
        {
            if (_widget?.LatestUpdate is { } notice)
                WidgetWindow.OpenReleasePage(notice);
        };
        menu.NeedsUpdate += (_, _) =>
        {
            var notice = _widget?.LatestUpdate;
            var shown = menu.Items.Contains(updateItem);
            if (notice is null && shown)
            {
                menu.Items.Remove(updateItem);
                menu.Items.Remove(updateSeparator);
            }
            else if (notice is not null)
            {
                updateItem.Header = notice.LinkText;
                if (!shown)
                {
                    menu.Items.Insert(0, updateSeparator);
                    menu.Items.Insert(0, updateItem);
                }
            }
        };

        menu.Items.Add(settingsItem);
        menu.Items.Add(muteItem);
        menu.Items.Add(_launchAtLoginItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://StickyAIusage/Assets/menubar.png"))),
            // A dev profile says so, so two menu bar icons can be told apart.
            ToolTipText = Demo.DemoMode.InstanceLabel(AppPaths.Current) is { } label ? $"Sticky AI Usage ({label})" : "Sticky AI Usage",
            Menu = menu
        };
        // A template image: macOS tints it to match the menu bar (light/dark).
        MacOSProperties.SetIsTemplateIcon(icon, true);
        TrayIcon.SetIcons(this, [icon]);
    }

    /// <summary>Opens the settings window, or brings the open one to the front.</summary>
    /// <param name="showHealth">Scroll to Settings → Health (the flyout's "Open Health").</param>
    private void ShowSettings(bool showHealth = false)
    {
        // The flyout floats above other windows; close it so Settings is fully visible.
        _widget?.Collapse();

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            if (showHealth)
                _settingsWindow.ShowHealth();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, new SettingsActions(
            OnSaved: () =>
            {
                _widget?.RefreshFromSettings();
                if (_launchAtLoginItem is not null)
                    _launchAtLoginItem.IsChecked = _settings.StartWithWindows;
            },
            ResetPosition: () => _widget?.ResetPosition(),
            OpenLogFolder: OpenLogFolder,
            CustomizeFields: ShowFields,
            SendTestAlert: SendTestAlert,
            CheckHealth: CheckHealthAsync,
            RunHealthAction: RunHealthAction),
            _widget?.CurrentMode ?? Models.UsageMode.Unknown);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (showHealth)
            _settingsWindow.ShowHealth();
    }

    private void SendTestAlert()
    {
        DiagnosticLog.WriteInfo("Test alert sent from Settings.");
        _lastTestAlertAt = DateTimeOffset.UtcNow;
        _alertSink?.Show([AlertPlanner.TestAlert()]);
    }

    private Task<HealthResult> CheckHealthAsync() =>
        _widget is { } widget
            ? HealthService.CheckAsync(widget.Services.Current, _settings, widget.LatestUsage, _lastTestAlertAt,
                AppIdentity.Describe(AppIdentity.Version(typeof(App).Assembly), AppPaths.Current, _settings))
            : Task.FromResult(new HealthResult([], ""));

    /// <summary>Settings → Health's buttons that reach outside the Settings window.</summary>
    private void RunHealthAction(string actionId)
    {
        switch (actionId)
        {
            case HealthActionIds.Connect:
                ShowOnboarding(OnboardingScreen.Connect);
                break;
            case HealthActionIds.SendTestAlert:
                SendTestAlert();
                break;
            case HealthActionIds.ToggleStartup:
                SetLaunchAtLogin(!_settings.StartWithWindows);
                break;
            case HealthActionIds.OpenLogFolder:
                OpenLogFolder();
                break;
        }
    }

    /// <summary>The session card field customizer, opened from Settings.</summary>
    private void ShowFields()
    {
        if (_fieldsWindow is not null)
        {
            _fieldsWindow.Activate();
            return;
        }

        _fieldsWindow = new FieldsWindow(_settings, onSaved: () => _widget?.RefreshFromSettings());
        _fieldsWindow.Closed += (_, _) => _fieldsWindow = null;
        _fieldsWindow.Show();
        _fieldsWindow.Activate();
    }

    private static void OpenLogFolder()
    {
        Directory.CreateDirectory(AppPaths.LogDirectory);
        Process.Start("open", $"\"{AppPaths.LogDirectory}\"");
    }

    private void Quit()
    {
        _showSignal?.Dispose();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}

/// <summary>What the settings window can ask the rest of the app to do.</summary>
/// <param name="CheckHealth">Runs Settings → Health; null: the section is hidden.</param>
/// <param name="RunHealthAction">A Health button the app carries out (<see cref="HealthActionIds"/>: Connect,
/// test alert, launch at login, log folder); copy, link and Usage source buttons the window handles itself.</param>
public sealed record SettingsActions(Action OnSaved, Action ResetPosition, Action OpenLogFolder, Action CustomizeFields,
    Action? SendTestAlert = null, Func<Task<HealthResult>>? CheckHealth = null, Action<string>? RunHealthAction = null);
