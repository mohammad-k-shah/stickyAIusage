using System.Windows;
using Hardcodet.Wpf.TaskbarNotification;
using Microsoft.Win32;
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
    private System.Windows.Controls.MenuItem? _startWithWindowsItem;
    private System.Windows.Controls.MenuItem? _hideItem;
    private AppSettings _settings = new();

    // A second copy would mean two widgets and double the calls to
    // Anthropic's rate-limited usage endpoint.
    private static Mutex? _singleInstance;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstance = new Mutex(initiallyOwned: true, @"Local\StickyAIusage.SingleInstance", out var isFirstInstance);
        if (!isFirstInstance)
        {
            Shutdown();
            return;
        }

        _settings = SettingsService.Load();
        // Every run starts dark, the way every run starts at the tray corner:
        // the sun/moon button and Settings change it until the app is closed.
        _settings.Theme = ThemePreference.Dark.ToSetting();
        ThemeService.Initialize(_settings);

        _stripWindow = new DockedStripWindow(_settings);
        _stripWindow.SettingsRequested += (_, _) => ShowSettings();
        // Show it once the first reading is in, so it opens in the right layout
        // (monthly spend or plan limits); don't wait long on a slow network.
        await Task.WhenAny(_stripWindow.FirstRefresh, Task.Delay(TimeSpan.FromSeconds(5)));
        _stripWindow.Show();

        _trayIcon = new TaskbarIcon
        {
            ToolTipText = "Sticky AI Usage",
            Icon = LoadTrayIcon(),
            ContextMenu = BuildTrayMenu()
        };
        _trayIcon.TrayLeftMouseUp += (_, _) => ToggleWidget();
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

        _startWithWindowsItem = new System.Windows.Controls.MenuItem
        {
            Header = "Start with Windows",
            IsCheckable = true,
            IsChecked = _settings.StartWithWindows
        };
        _startWithWindowsItem.Click += (_, _) =>
        {
            _settings.StartWithWindows = _startWithWindowsItem.IsChecked;
            SettingsService.Save(_settings);
            StartupRegistration.SetEnabled(_settings.StartWithWindows);
        };

        var resetPositionItem = new System.Windows.Controls.MenuItem { Header = "Reset widget position" };
        resetPositionItem.Click += (_, _) => _stripWindow?.ResetPosition();

        var moveToItem = new System.Windows.Controls.MenuItem { Header = "Move widget to" };
        foreach (var (title, corner) in new[]
        {
            ("Top right corner", WidgetCorner.TopRight), ("Top left corner", WidgetCorner.TopLeft),
            ("Bottom right corner", WidgetCorner.BottomRight), ("Bottom left corner", WidgetCorner.BottomLeft)
        })
        {
            var item = new System.Windows.Controls.MenuItem { Header = title };
            item.Click += (_, _) => _stripWindow?.MoveToCorner(corner);
            moveToItem.Items.Add(item);
        }

        var openLogFolderItem = new System.Windows.Controls.MenuItem { Header = "Open log folder" };
        openLogFolderItem.Click += (_, _) => OpenLogFolder();

        _hideItem = new System.Windows.Controls.MenuItem { Header = "Hide" };
        _hideItem.Click += (_, _) => ToggleWidget();

        var exitItem = new System.Windows.Controls.MenuItem { Header = "Exit" };
        exitItem.Click += (_, _) => Shutdown();

        menu.Items.Add(settingsItem);
        menu.Items.Add(_startWithWindowsItem);
        menu.Items.Add(resetPositionItem);
        menu.Items.Add(moveToItem);
        menu.Items.Add(openLogFolderItem);
        menu.Items.Add(_hideItem);
        menu.Items.Add(new System.Windows.Controls.Separator());
        menu.Items.Add(exitItem);
        return menu;
    }

    /// <summary>Opens the settings window, or brings the open one to the front -
    /// two open copies would save over each other.</summary>
    private void ShowSettings()
    {
        // The flyout is topmost; close it so Settings is fully visible.
        _stripWindow?.Collapse();

        if (_settingsWindow is { IsLoaded: true })
        {
            _settingsWindow.Activate();
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
            CustomizeFields: ShowFields),
            _stripWindow?.CurrentMode ?? Models.UsageMode.Unknown);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
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
        _singleInstance?.Dispose();
        base.OnExit(e);
    }

    /// <summary>Loads the project's own icon (Resources/app.ico, copied next
    /// to the exe at build time - see the csproj) for the tray icon, instead
    /// of the generic system icon previously shown there. Falls back to the
    /// system icon if the file is somehow missing, rather than throwing on
    /// startup over a cosmetic issue.</summary>
    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            var iconPath = System.IO.Path.Combine(AppContext.BaseDirectory, "Resources", "app.ico");
            return new System.Drawing.Icon(iconPath);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("App.LoadTrayIcon", ex);
            return System.Drawing.SystemIcons.Application;
        }
    }
}

/// <summary>Registers/unregisters StickyAIusage under HKCU Run so it starts
/// with Windows. HKCU requires no elevation.</summary>
internal static class StartupRegistration
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "StickyAIusage";

    public static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key is null) return;

        if (enabled)
        {
            // Assembly.Location is empty in a single-file publish; ProcessPath isn't.
            var exePath = Environment.ProcessPath ?? System.IO.Path.Combine(AppContext.BaseDirectory, "StickyAIusage.exe");
            key.SetValue(ValueName, $"\"{exePath}\"");
        }
        else
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
