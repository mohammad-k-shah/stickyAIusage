using System.Diagnostics;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
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
    private NativeMenuItem? _launchAtLoginItem;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override async void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime)
            return;

        _settings = SettingsService.Load();
        ThemeService.Initialize(_settings);
        DiagnosticLog.WriteInfo($"App started (macOS). theme={_settings.Theme} dark={ThemeService.IsDark}");

        _widget = new WidgetWindow(_settings);
        _widget.SettingsRequested += (_, _) => ShowSettings();
        _widget.QuitRequested += (_, _) => Quit();
        CreateMenuBarIcon();

        // Show it once the first reading is in, so it opens in the right
        // layout (monthly spend or plan limits); don't wait long on a slow network.
        await Task.WhenAny(_widget.FirstRefresh, Task.Delay(TimeSpan.FromSeconds(5)));
        _widget.Show();
    }

    private void CreateMenuBarIcon()
    {
        var menu = new NativeMenu();

        var settingsItem = new NativeMenuItem("Settings…");
        settingsItem.Click += (_, _) => ShowSettings();

        _launchAtLoginItem = new NativeMenuItem("Launch at login")
        {
            ToggleType = NativeMenuItemToggleType.CheckBox,
            IsChecked = _settings.StartWithWindows
        };
        _launchAtLoginItem.Click += (_, _) =>
        {
            _settings.StartWithWindows = !_settings.StartWithWindows;
            _launchAtLoginItem.IsChecked = _settings.StartWithWindows;
            SettingsService.Save(_settings);
            LoginItem.SetEnabled(_settings.StartWithWindows);
        };

        var resetItem = new NativeMenuItem("Reset widget position");
        resetItem.Click += (_, _) => _widget?.ResetPosition();

        var logsItem = new NativeMenuItem("Open log folder");
        logsItem.Click += (_, _) => OpenLogFolder();

        var quitItem = new NativeMenuItem("Quit Sticky AI Usage");
        quitItem.Click += (_, _) => Quit();

        menu.Items.Add(settingsItem);
        menu.Items.Add(_launchAtLoginItem);
        menu.Items.Add(resetItem);
        menu.Items.Add(logsItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(quitItem);

        var icon = new TrayIcon
        {
            Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://StickyAIusage/Assets/menubar.png"))),
            ToolTipText = "Sticky AI Usage",
            Menu = menu
        };
        // A template image: macOS tints it to match the menu bar (light/dark).
        MacOSProperties.SetIsTemplateIcon(icon, true);
        TrayIcon.SetIcons(this, [icon]);
    }

    /// <summary>Opens the settings window, or brings the open one to the front.</summary>
    private void ShowSettings()
    {
        // The flyout floats above other windows; close it so Settings is fully visible.
        _widget?.Collapse();

        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
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
            CustomizeFields: ShowFields),
            _widget?.CurrentMode ?? Models.UsageMode.Unknown);
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
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.Shutdown();
    }
}

/// <summary>What the settings window can ask the rest of the app to do.</summary>
public sealed record SettingsActions(Action OnSaved, Action ResetPosition, Action OpenLogFolder, Action CustomizeFields);
