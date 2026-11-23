using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
using StickyAIusage.Alerts;
using StickyAIusage.Demo;
using StickyAIusage.Health;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>
/// App entry point. ShutdownMode is OnExplicitShutdown (see App.xaml) because
/// this app has no conventional main window to close-and-quit on - the docked
/// strip should stay alive for the whole desktop session, closable only from
/// the tray icon's Exit command.
/// </summary>
public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private DockedStripWindow? _stripWindow;
    private SettingsWindow? _settingsWindow;
    private FieldsWindow? _fieldsWindow;
    private OnboardingWindow? _onboardingWindow;
    private DateTimeOffset? _lastTestAlertAt;
    private System.Windows.Controls.MenuItem? _startWithWindowsItem;
    private System.Windows.Controls.MenuItem? _hideItem;
    private AppSettings _settings = new();
    private IAlertSink? _alertSink;
    private readonly List<Alert> _pendingAlerts = [];

    // A second copy of one profile would mean two widgets and double the
    // calls to Anthropic's rate-limited usage endpoint. Instead it asks the
    // running copy to show itself (ShowRequest) and exits.
    private static Mutex? _singleInstance;
    private static EventWaitHandle? _showRequest;
    private static RegisteredWaitHandle? _showRequestWait;

    // --demo: sample data in a temporary profile, removed on exit.
    private DemoRun? _demo;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A release check, not a start: no lock, no tray icon, no window, a temporary profile.
        if (SelfTest.IsRequested(e.Args, out var reportPath))
        {
            int exitCode;
            try
            {
                exitCode = await SelfTestRunner.RunAsync(this, reportPath);
            }
            catch (Exception ex)
            {
                exitCode = SelfTest.ReportCrash(reportPath, Console.Out, ex);
            }
            Shutdown(exitCode);
            return;
        }

        // The demo moves the whole app into a temporary profile before anything reads a path.
        var args = e.Args;
        if (DemoMode.IsRequested(args, out var variant))
            _demo = DemoMode.Start(variant, DateTimeOffset.UtcNow);

        // Nothing may end the app without a line in the log: a UI-thread
        // exception is logged and the pill stays; the rest is logged.
        UnhandledErrors.Install();
        DispatcherUnhandledException += (_, args) => args.Handled = UnhandledErrors.Shared.OnUiThread(args.Exception);

        // async void: an exception here would end the process unseen.
        try
        {
            await StartAsync(args);
        }
        catch (Exception ex)
        {
            UnhandledErrors.Shared.OnStartupFailed(ex);
            if (_stripWindow is { IsVisible: true } && _trayIcon is not null)
                return; // the widget is up; what failed came after it
            string where;
            try { where = AppPaths.LogDirectory; }
            catch { where = "the StickyAIusage folder in %LOCALAPPDATA%"; } // what failed may be the paths themselves
            MessageBox.Show($"Sticky AI Usage couldn't start: {ex.Message}\n\nThe log folder has the details:\n{where}",
                "Sticky AI Usage", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private async Task StartAsync(string[] args)
    {
        // The demo has already pointed the paths at its temporary profile.
        var locations = _demo is not null ? AppPaths.Current : AppPaths.Configure(args);
        _singleInstance = new Mutex(initiallyOwned: true, AppPaths.InstanceName(@"Local\StickyAIusage.SingleInstance"), out var isFirstInstance);
        var showRequestName = AppPaths.InstanceName(@"Local\StickyAIusage.ShowRequest");
        if (!isFirstInstance)
        {
            SignalRunningInstance(showRequestName);
            Shutdown();
            return;
        }
        ListenForShowRequests(showRequestName);

        _settings = LoadSettings();
        DiagnosticLog.WriteInfo(AppIdentity.StartLine("Windows", AppIdentity.Version(typeof(App).Assembly), locations, _settings));
        ThemeService.Initialize(_settings);

        _stripWindow = new DockedStripWindow(_settings);
        _stripWindow.SettingsRequested += (_, _) => ShowSettings();
        _stripWindow.LogFolderRequested += (_, _) => OpenLogFolder();
        _stripWindow.HealthRequested += (_, _) => ShowSettings(showHealth: true);
        _stripWindow.ConnectRequested += (_, _) =>
        {
            _stripWindow.Collapse();
            ShowOnboarding(OnboardingScreen.Connect);
        };
        // The first refresh runs before the tray icon exists: its alerts wait for it.
        _stripWindow.AlertsRaised += alerts =>
        {
            if (_alertSink is null)
                _pendingAlerts.AddRange(alerts);
            else
                _alertSink.Show(alerts);
        };
        // Show it once the first reading is in, so it opens in the right layout
        // (monthly spend or plan limits); don't wait long on a slow network.
        await Task.WhenAny(_stripWindow.FirstRefresh, Task.Delay(TimeSpan.FromSeconds(5)));
        _stripWindow.Show();

        _trayIcon = new TaskbarIcon
        {
            // A dev profile says so, so two tray icons can be told apart.
            ToolTipText = DemoMode.InstanceLabel(locations) is { } label ? $"Sticky AI Usage ({label})" : "Sticky AI Usage",
            Icon = LoadTrayIcon(),
            ContextMenu = BuildTrayMenu()
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleWidget();
        ConnectAlerts(_trayIcon, ShowWidgetAndFlyout);
        _alertSink = new TrayAlertSink(_trayIcon, DemoMode.InstanceLabel(locations));
        if (_pendingAlerts.Count > 0)
        {
            _alertSink.Show(_pendingAlerts.ToList());
            _pendingAlerts.Clear();
        }
        ShowOnboardingIfDue();
        InjectFaultForTesting();
    }

    /// <summary>A new install's three steps, or an upgrade's one screen, once.
    /// Closing the window counts as done, like Finish without its choices.</summary>
    private void ShowOnboardingIfDue()
    {
        if (_stripWindow is null)
            return;
        var screen = Onboarding.ScreenFor(_settings, _stripWindow.PluginWrote);
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

    /// <summary>The first-run window, or Connect alone (from Settings → Health).</summary>
    private void ShowOnboarding(OnboardingScreen screen)
    {
        if (_stripWindow is not { } strip)
            return;
        if (_onboardingWindow is not null)
        {
            _onboardingWindow.Activate();
            return;
        }
        var connect = ConnectSession.ForThisComputer(() => strip.LatestUsage, () => strip.PluginWrote);
        void Repaint() => connect.Refresh();
        strip.UsageApplied += Repaint;
        _onboardingWindow = new OnboardingWindow(_settings, screen, connect,
            new OnboardingActions(() => strip.RefreshFromSettings(), SetStartAtLogin));
        _onboardingWindow.Closed += (_, _) =>
        {
            strip.UsageApplied -= Repaint;
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

    /// <summary>Start with Windows, from the tray menu, Settings → Health or the first-run window: at once.</summary>
    private void SetStartAtLogin(bool on)
    {
        _settings.StartWithWindows = on;
        SettingsService.Save(_settings);
        StartupRegistration.SetEnabled(on);
        if (_startWithWindowsItem is not null)
            _startWithWindowsItem.IsChecked = on;
        _settingsWindow?.StartAtLoginChangedElsewhere(on);
    }

    /// <summary>Clicking an alert's toast (the tray balloon) opens the flyout.</summary>
    internal static void ConnectAlerts(TaskbarIcon icon, Action open) =>
        icon.TrayBalloonTipClicked += (_, _) =>
        {
            DiagnosticLog.WriteInfo("Alert clicked: opening the flyout.");
            open();
        };

    /// <summary>A second launch or a click on an alert: the widget and its flyout, in front.</summary>
    private void ShowWidgetAndFlyout()
    {
        _stripWindow?.ShowAndExpand();
        if (_hideItem is not null)
            _hideItem.Header = "Hide";
    }

    /// <summary>The settings a start runs with: exactly what was saved. (v1
    /// set the theme to dark here on every start, so the sun/moon button and
    /// Settings only lasted until the app closed.)</summary>
    internal static AppSettings LoadSettings() => SettingsService.Load();

    /// <summary>Debug builds only: STICKYAIUSAGE_FAULT=ui throws on the UI
    /// thread a few seconds after start, =task leaves a failed task for the
    /// garbage collector, so the crash handlers can be checked by hand. The
    /// compiler drops the call from Release builds.</summary>
    [System.Diagnostics.Conditional("DEBUG")]
    private void InjectFaultForTesting()
    {
        var fault = Environment.GetEnvironmentVariable("STICKYAIUSAGE_FAULT");
        if (string.IsNullOrEmpty(fault))
            return;
        DiagnosticLog.WriteInfo($"Fault injection: {fault} in 3 seconds (debug build).");
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (fault == "task")
            {
                _ = Task.Run(new Action(() => throw new InvalidOperationException("Injected background fault")));
                Task.Delay(500).ContinueWith(_ => { GC.Collect(); GC.WaitForPendingFinalizers(); });
                return;
            }
            throw new InvalidOperationException("Injected UI-thread fault");
        };
        timer.Start();
    }

    /// <summary>Second launch: wake the running copy. It may still be
    /// starting (no event yet), in which case there's nothing to wake.</summary>
    private static void SignalRunningInstance(string name)
    {
        try
        {
            Native.TaskbarInterop.AllowAnyProcessToTakeForeground();
            if (EventWaitHandle.TryOpenExisting(name, out var handle))
            {
                using (handle)
                    handle.Set();
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("App.SignalRunningInstance", ex);
        }
    }

    private void ListenForShowRequests(string name)
    {
        try
        {
            _showRequest = new EventWaitHandle(false, EventResetMode.AutoReset, name);
            _showRequestWait = ThreadPool.RegisterWaitForSingleObject(_showRequest,
                (_, _) => Dispatcher.BeginInvoke(() =>
                {
                    DiagnosticLog.WriteInfo("Started again while running: showing the widget and its flyout.");
                    ShowWidgetAndFlyout();
                }),
                null, Timeout.Infinite, executeOnlyOnce: false);
        }
        catch (Exception ex)
        {
            // Without it a second launch just exits, as before.
            DiagnosticLog.WriteError("App.ListenForShowRequests", ex);
        }
    }

    /// <summary>Tray icon click and the menu's Hide/Show: hides the widget, or shows it where it was.</summary>
    private void ToggleWidget()
    {
        if (_stripWindow is null)
            return;
        var visible = _stripWindow.ToggleVisible();
        if (_hideItem is not null)
            _hideItem.Header = visible ? "Hide" : "Show";
    }

    private System.Windows.Controls.ContextMenu BuildTrayMenu()
    {
        var menu = new System.Windows.Controls.ContextMenu();

        var settingsItem = new System.Windows.Controls.MenuItem { Header = "Settings…" };
        settingsItem.Click += (_, _) => ShowSettings();

        var muteItem = new System.Windows.Controls.MenuItem { Header = AlertMute.MenuLabel(_settings, DateTimeOffset.UtcNow) };
        muteItem.Click += (_, _) =>
        {
            AlertMute.Toggle(_settings, DateTimeOffset.UtcNow);
            SettingsService.Save(_settings);
            DiagnosticLog.WriteInfo(_settings.AlertsMutedUntil is { } until
                ? $"Alerts muted until {until.ToLocalTime():HH:mm}."
                : "Alerts unmuted.");
        };
        // The label says until when; it is worked out each time the menu opens.
        menu.Opened += (_, _) => muteItem.Header = AlertMute.MenuLabel(_settings, DateTimeOffset.UtcNow);

        _startWithWindowsItem = new System.Windows.Controls.MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = _settings.StartWithWindows
        };
        _startWithWindowsItem.Click += (_, _) => SetStartAtLogin(_startWithWindowsItem.IsChecked);

        var resetPositionItem = new System.Windows.Controls.MenuItem { Header = "Reset widget position" };
        resetPositionItem.Click += (_, _) => _stripWindow?.ResetPosition();

        var moveToItem = new System.Windows.Controls.MenuItem { Header = "Move widget to" };
        FillMoveToMenu(moveToItem);
        // Monitors come and go: the list is made again each time the menu opens.
        menu.Opened += (_, _) => FillMoveToMenu(moveToItem);

        var openLogFolderItem = new System.Windows.Controls.MenuItem { Header = "Open log folder" };
        openLogFolderItem.Click += (_, _) => OpenLogFolder();

        _hideItem = new System.Windows.Controls.MenuItem { Header = "Hide" };
        _hideItem.Click += (_, _) => ToggleWidget();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();

        // "Update available: v2.0.1 — what's new" while a newer version is known (Check for updates on).
        var updateItem = new System.Windows.Controls.MenuItem { Visibility = Visibility.Collapsed, FontWeight = FontWeights.SemiBold };
        var updateSeparator = new System.Windows.Controls.Separator { Visibility = Visibility.Collapsed };
        updateItem.Click += (_, _) =>
        {
            if (_stripWindow?.LatestUpdate is { } notice)
                DockedStripWindow.OpenReleasePage(notice);
        };
        menu.Opened += (_, _) =>
        {
            var notice = _stripWindow?.LatestUpdate;
            updateItem.Header = notice?.LinkText;
            updateItem.Visibility = updateSeparator.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        };

        menu.Items.Add(updateItem);
        menu.Items.Add(updateSeparator);
        menu.Items.Add(settingsItem);
        menu.Items.Add(muteItem);
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add(resetPositionItem);
        menu.Items.Add(moveToItem);
        menu.Items.Add(openLogFolderItem);
        menu.Items.Add(_hideItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    /// <summary>"Move widget to": the taskbar (on each monitor, when there are several), then the corners.</summary>
    private void FillMoveToMenu(System.Windows.Controls.MenuItem moveTo)
    {
        moveTo.Items.Clear();
        var monitors = Native.TaskbarDock.Monitors();
        foreach (var (header, device) in DockMonitors.TaskbarMenu(monitors.Select(m => (m.Device, m.IsPrimary)).ToList()))
        {
            var item = new System.Windows.Controls.MenuItem { Header = header };
            item.Click += (_, _) => _stripWindow?.DockInTaskbar(device);
            moveTo.Items.Add(item);
        }
        moveTo.Items.Add(new System.Windows.Controls.Separator());
        foreach (var (title, corner) in new[]
        {
            ("Top right corner", WidgetCorner.TopRight), ("Top left corner", WidgetCorner.TopLeft),
            ("Bottom right corner", WidgetCorner.BottomRight), ("Bottom left corner", WidgetCorner.BottomLeft)
        })
        {
            var item = new System.Windows.Controls.MenuItem { Header = title };
            item.Click += (_, _) => _stripWindow?.MoveToCorner(corner);
            moveTo.Items.Add(item);
        }
    }

    /// <summary>Opens the settings window, or brings the open one to the front -
    /// two open copies would save over each other.</summary>
    /// <param name="showHealth">Scroll to Settings → Health (the flyout's "Open Health").</param>
    private void ShowSettings(bool showHealth = false)
    {
        // The flyout is topmost; close it so Settings is fully visible.
        _stripWindow?.Collapse();

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
            if (showHealth)
                _settingsWindow.ShowHealth();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, new SettingsActions(
            OnSaved: () =>
            {
                _stripWindow?.RefreshFromSettings();
                if (_startWithWindowsItem is not null)
                    _startWithWindowsItem.IsChecked = _settings.StartWithWindows;
            },
            ResetPosition: () => _stripWindow?.ResetPosition(),
            OpenLogFolder: OpenLogFolder,
            CustomizeFields: ShowFields,
            SendTestAlert: SendTestAlert,
            CheckHealth: CheckHealthAsync,
            RunHealthAction: RunHealthAction),
            _stripWindow?.CurrentMode ?? Models.UsageMode.Unknown);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
        if (showHealth)
            _settingsWindow.ShowHealth();
    }

    private Task<HealthResult> CheckHealthAsync() =>
        _stripWindow is { } strip
            ? HealthService.CheckAsync(strip.Services.Current, _settings, strip.LatestUsage, _lastTestAlertAt,
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
                SetStartAtLogin(!_settings.StartWithWindows);
                break;
            case HealthActionIds.OpenLogFolder:
                OpenLogFolder();
                break;
        }
    }

    /// <summary>Settings → Send a test alert: shown whatever the quiet hours or mute say.</summary>
    private void SendTestAlert()
    {
        DiagnosticLog.WriteInfo("Test alert sent from Settings.");
        _lastTestAlertAt = DateTimeOffset.UtcNow;
        _alertSink?.Show([AlertPlanner.TestAlert()]);
    }

    /// <summary>The session card field customizer, opened from Settings.</summary>
    private void ShowFields()
    {
        if (_fieldsWindow is not null)
        {
            _fieldsWindow.Activate();
            return;
        }

        _fieldsWindow = new FieldsWindow(_settings, onSaved: () =>
        {
            _stripWindow?.RefreshFromSettings();
            _settingsWindow?.ShowFieldsSummary();
        });
        _fieldsWindow.Closed += (_, _) => _fieldsWindow = null;
        _fieldsWindow.Show();
        _fieldsWindow.Activate();
    }

    private static void OpenLogFolder()
    {
        var logDir = AppPaths.LogDirectory;
        System.IO.Directory.CreateDirectory(logDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(logDir) { UseShellExecute = true });
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _trayIcon?.Dispose();
        _showRequestWait?.Unregister(null);
        _showRequest?.Dispose();
        _singleInstance?.Dispose();
        _demo?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Loads the project's own icon for the tray, instead of the
    /// generic system icon. Read from the assembly's embedded copy rather than
    /// a file beside the exe: a single-file publish (what the Releases page
    /// ships) bundles content into the exe, so the path form found nothing and
    /// every downloaded copy fell back to the generic icon. Still falls back
    /// rather than throwing on startup over something cosmetic.</summary>
    /// <param name="large">The large size, for a toast, instead of the tray's own.</param>
    internal static System.Drawing.Icon LoadTrayIcon(bool large = false)
    {
        try
        {
            using var stream = typeof(App).Assembly.GetManifestResourceStream(TrayIconResourceName)
                ?? throw new System.IO.FileNotFoundException($"Embedded resource {TrayIconResourceName} is missing.");
            return large ? new System.Drawing.Icon(stream, 64, 64) : new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("App.LoadTrayIcon", ex);
            return System.Drawing.SystemIcons.Application;
        }
    }

    internal const string TrayIconResourceName = "StickyAIusage.Resources.app.ico";
}

/// <summary>Registers/unregisters StickyAIusage under HKCU Run so it starts
/// with Windows. HKCU requires no elevation. Each profile has its own value
/// ("StickyAIusage-dev", started with --profile dev), so a dev copy never
/// replaces the installed widget's entry.</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        var locations = AppPaths.Current;
        var valueName = "StickyAIusage" + locations.NameSuffix;
        if (!enabled)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        if (locations.RelaunchArguments is not { } arguments)
        {
            DiagnosticLog.WriteInfo($"Start with Windows is not available for {locations.InstanceLabel}: it is set through {AppPaths.DataDirectoryVariable}, which a sign-in start doesn't have.");
            return;
        }

        // Assembly.Location is empty in a single-file publish; ProcessPath isn't.
        var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "StickyAIusage.exe");
        key.SetValue(valueName, string.Join(' ', [$"\"{exePath}\"", .. arguments]));
    }
}
