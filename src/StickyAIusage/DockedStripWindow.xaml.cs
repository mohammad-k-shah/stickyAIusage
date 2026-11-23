using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StickyAIusage.Controls;
using StickyAIusage.Forecasting;
using StickyAIusage.Models;
using StickyAIusage.Native;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.Stats;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>
/// The always-on-top pill docked next to the taskbar's tray icons.
/// Collapsed, it shows the account's usage - monthly spend and the current
/// session's cost, or a Pro/Max plan's 5-hour and weekly windows.
/// While it sits in the taskbar a plan account gets the compact form
/// ("5h: 43% - 2h 6m │ 1w: 14%"), the same as the macOS menu bar; dragged out
/// onto the desktop, the full one.
/// Clicking it opens a flyout listing today's sessions as cards -
/// part of the same window, never a separate one.
///
/// The flyout opens wherever there's room - above the pill by default (the
/// taskbar is at the bottom), below it when the pill has been dragged near the
/// top - and shifts sideways so that it and the pill are always fully on
/// screen (FlyoutPlacement). The expand arrow points the way the flyout will
/// open, and the way it will close once open.
/// </summary>
public enum WidgetCorner { TopLeft, TopRight, BottomLeft, BottomRight }

public partial class DockedStripWindow : Window
{
    // Must match RootPanel's Margin and the pill's Height in the XAML.
    private const double OuterMargin = 12;
    // The pill's height: 32 outside the taskbar, the taskbar's height minus 8 inside it (DockPlacement).
    private double _pillHeight = DockPlacement.FullHeight;
    private double PillHeight => _pillHeight;
    private const double FlyoutWidth = 540;
    private const double FlyoutGap = 8;
    private const double CornerGap = 8;
    // The flyout never grows taller than this share of the screen; the
    // sessions list scrolls inside it.
    private const double MaxFlyoutScreenShare = 0.75;
    // Used to point the collapsed arrow before the flyout's real size is known.
    private const double TypicalFlyoutHeight = 560;

    private const string SunGlyph = "\uE706";
    private const string MoonGlyph = "\uE708";
    private const string PauseGlyph = "\uE769";
    private const string PlayGlyph = "\uE768";
    private const string ChevronUpGlyph = "\uE70E";
    private const string ChevronDownGlyph = "\uE70D";

    private readonly AppSettings _settings;
    private readonly AppServicesHost _services;
    private UsageFeed Feed => _services.Current.Feed;
    private readonly DispatcherTimer _pollTimer;
    private readonly ObservableCollection<SessionCardViewModel> _sessions = new();
    private IReadOnlyList<SessionCardViewModel> _lastCards = Array.Empty<SessionCardViewModel>();

    /// <summary>Days of sessions the flyout lists (SessionRange); starts on today every launch.</summary>
    private int _sessionDays = 1;

    // Where the collapsed pill's bottom-right corner is pinned, in screen
    // DIPs. While the flyout is open the pill may be pulled onto the screen;
    // it returns here when the flyout closes.
    private double _pillRight;
    private double _pillBottom;
    private bool _inLayout;

    private bool _isExpanded;
    private bool? _opensAbove;
    private bool _compact;
    private WidgetViewModel? _lastView;
    private bool _isRunning = true;
    private readonly RefreshGate _refreshGate = new();
    private bool _sessionsRefreshInFlight;

    // Drag-vs-click disambiguation: DragMove() alone would eat the click that
    // should toggle the flyout, so a drag only starts once the mouse has moved
    // past a small threshold; otherwise MouseLeftButtonUp toggles the flyout.
    private Point? _dragStartScreenPoint;
    private bool _didDrag;
    private const double DragThreshold = 4.0;

    /// <summary>Raised when the flyout's settings button is clicked; the App
    /// owns the (single) settings window.</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>Raised by the health line's "Open log folder".</summary>
    public event EventHandler? LogFolderRequested;

    /// <summary>Raised by the health line's "Open Health": Settings, at its Health section.</summary>
    public event EventHandler? HealthRequested;

    /// <summary>Raised on the UI thread after each reading is shown (the first-run
    /// window's Connect step follows it).</summary>
    public event Action? UsageApplied;

    /// <summary>The latest reading; null before the first refresh.</summary>
    public UsageSnapshot? LatestUsage => _lastReading?.Usage;

    /// <summary>The services this window reads through (Settings → Health checks them).</summary>
    internal AppServicesHost Services => _services;

    /// <summary>The plugin has written to the feed folder: its marker, or a session record.</summary>
    public bool PluginWrote => _services.Current.ClaudeFeed.PluginSeen || _services.Current.ClaudeFeed.Sessions.Count > 0;

    /// <summary>The first usage reading. The App waits for it (briefly) before
    /// showing the window, so the pill appears already in the right layout -
    /// monthly spend or plan limits - instead of flashing a placeholder.</summary>
    public Task FirstRefresh { get; }

    public DockedStripWindow(AppSettings settings)
    {
        _settings = settings;
        _isRunning = settings.IsRunning;
        _services = new AppServicesHost(settings);
        ShareOutput = new ShareService(() => IsVisible ? this : null);
        InitializeComponent();
        SessionsList.ItemsSource = _sessions;
        BuildRangeTabs();

        Loaded += (_, _) =>
        {
            RestorePosition();
            ApplyRunningState();
            DiagnosticLog.WriteInfo($"Widget shown. isRunning={_isRunning} dock={_settings.DockMode}");
            // The taskbar buries a topmost pill when it is clicked, and its free space changes as apps
            // open and close: Windows says when (the foreground changes, the taskbar's windows move).
            _taskbarEvents = new TaskbarEvents(OnTaskbarEvent);
        };
        SourceInitialized += (_, _) =>
        {
            // Explorer restarted: a new taskbar, a new process to watch.
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(OnWindowMessage);
        };
        // A move to a monitor with other scaling: the taskbar and the pill in new DIPs.
        DpiChanged += (_, _) => ScheduleRedock(readButtons: true);
        RootBorder.SizeChanged += (_, _) => ApplyLayout();
        ExpandedPanel.SizeChanged += (_, _) => ApplyLayout();
        // SizeToContent settles the window's size after Loaded, and the first
        // pass measures unconstrained (far wider than the pill ends up). Place
        // the window again once the real size is in, or it stays where that
        // oversized first pass put it - off the left edge of the screen.
        SizeChanged += (_, _) => ApplyLayout();

        ApplyTheme();
        ThemeService.Changed += ApplyTheme;
        Closed += (_, _) => ThemeService.Changed -= ApplyTheme;

        // Clicking anywhere else closes the flyout, the way a menu does.
        Deactivated += (_, _) => Collapse();

        // Session transcripts are re-read on a light poll rather than a file
        // watcher; each tick only reads newly appended bytes.
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _pollTimer.Tick += async (_, _) => { if (_isRunning) await RefreshAsync(); };
        _pollTimer.Start();
        // The Claude Code plugin's feed says when it wrote: refresh at once.
        _services.UsageChanged += OnUsageChanged;
        SessionsList.AddHandler(System.Windows.Controls.Primitives.ButtonBase.ClickEvent, new RoutedEventHandler(CardDetailsToggle_Click));
        Closed += (_, _) =>
        {
            _pollTimer.Stop();
            _attentionTimer?.Stop();
            _services.UsageChanged -= OnUsageChanged;
            _services.Dispose();
        };

        // The events above do the work; this only catches what they miss (a full-screen app has no event).
        var keepOnTopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        keepOnTopTimer.Tick += (_, _) =>
        {
            KeepOnTop();
            CheckFullScreen();
        };
        keepOnTopTimer.Start();
        _eventTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _eventTimer.Tick += (_, _) => HandleTaskbarEvents();

        // A monitor change or an unlock can strand the pill off-screen or
        // under the taskbar: bring it back.
        SystemEvents.DisplaySettingsChanged += OnDisplayOrSessionChanged;
        SystemEvents.SessionSwitch += OnDisplayOrSessionChanged;
        Closed += (_, _) =>
        {
            keepOnTopTimer.Stop();
            _eventTimer.Stop();
            _taskbarEvents?.Dispose();
            SystemEvents.DisplaySettingsChanged -= OnDisplayOrSessionChanged;
            SystemEvents.SessionSwitch -= OnDisplayOrSessionChanged;
        };

        FirstRefresh = RefreshAsync();
    }

    /// <summary>Hides the widget, or shows it again where it was.</summary>
    public bool ToggleVisible()
    {
        // The user's own choice wins over a full-screen hide.
        _hiddenForFullScreen = false;
        if (IsVisible)
        {
            Collapse();
            Hide();
        }
        else
        {
            Show();
            KeepOnTop();
        }
        return IsVisible;
    }

    /// <summary>A second launch of this profile: show the widget if hidden
    /// and open the flyout, at the front so a click elsewhere closes it again.
    /// Ignored until the window has been shown for the first time.</summary>
    public void ShowAndExpand()
    {
        if (!IsLoaded)
            return;
        if (!IsVisible)
            ToggleVisible();
        if (!_isExpanded)
            ToggleExpanded();
        Activate();
    }

    private void KeepOnTop()
    {
        if (!IsVisible)
            return;
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source)
            TaskbarInterop.KeepOnTop(source.Handle);
    }

    // Raised on a system thread.
    private void OnDisplayOrSessionChanged(object? sender, EventArgs e) =>
        Dispatcher.BeginInvoke(() =>
        {
            if (DockModes.Parse(_settings.DockMode) != DockMode.Free)
                ScheduleRedock(readButtons: true);
            else if (!IsMostlyOnScreen())
            {
                // For this run only: once that monitor is back, the next start uses the saved spot again.
                DiagnosticLog.WriteInfo("Screen layout changed and the widget is off-screen; docking it for now.");
                _ = DockAsync(DockMode.Taskbar);
            }
            KeepOnTop();
        });

    // ------------------------------------------------------------- docking

    private TaskbarEvents? _taskbarEvents;
    private readonly DispatcherTimer _eventTimer;
    private bool _eventPending;
    private DateTime _lastRedock = DateTime.MinValue;
    private bool _hiddenForFullScreen;
    private string? _lastDockLine;
    private int _dockGeneration;

    /// <summary>Where the pill was last docked; null while it is free.</summary>
    internal DockSpot? DockedAt { get; private set; }

    /// <summary>The foreground changed or the taskbar's windows moved. Many come at once: they are
    /// handled together a quarter of a second later.</summary>
    private void OnTaskbarEvent(bool taskbarChanged)
    {
        _eventPending = true;
        if (!_eventTimer.IsEnabled)
            _eventTimer.Start();
    }

    private void HandleTaskbarEvents()
    {
        _eventTimer.Stop();
        if (!_eventPending)
            return;
        _eventPending = false;
        KeepOnTop();
        CheckFullScreen();
        // A window opened or closed (it takes or gives up the foreground) or the taskbar moved: the
        // taskbar's free space may have changed. Read it again at most every three seconds.
        if (DockModes.Parse(_settings.DockMode) == DockMode.Taskbar && DateTime.UtcNow - _lastRedock > TimeSpan.FromSeconds(3))
            ScheduleRedock(readButtons: true);
    }

    private void ScheduleRedock(bool readButtons)
    {
        if (DockModes.Parse(_settings.DockMode) == DockMode.Free || !IsLoaded)
            return;
        _ = DockAsync(null, readButtons);
    }

    private IntPtr OnWindowMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)TaskbarDock.TaskbarCreatedMessage && TaskbarDock.TaskbarCreatedMessage != 0)
        {
            DiagnosticLog.WriteInfo("The taskbar was created again (Explorer restarted): docking again.");
            _taskbarEvents?.Rehook();
            ScheduleRedock(readButtons: true);
        }
        return IntPtr.Zero;
    }

    /// <summary>Docks the pill as <paramref name="mode"/> (default: the setting) on the chosen
    /// monitor. The taskbar is read off the UI thread (UI Automation can take a moment); an open
    /// flyout is never moved from under the pointer: it docks when the flyout closes.</summary>
    internal async Task DockAsync(DockMode? mode = null, bool readButtons = true)
    {
        var wanted = mode ?? DockModes.Parse(_settings.DockMode);
        if (wanted == DockMode.Free)
            return;
        var generation = ++_dockGeneration;
        _lastRedock = DateTime.UtcNow;
        var device = _settings.DockMonitor;
        var read = await Task.Run(() =>
        {
            try
            {
                var monitor = TaskbarDock.Chosen(device);
                return (monitor, taskbar: monitor is null ? null : TaskbarDock.TaskbarOn(monitor, readButtons && wanted == DockMode.Taskbar));
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError("DockedStripWindow.DockAsync", ex);
                return (null, null);
            }
        });
        if (generation != _dockGeneration || _isExpanded || _dragStartScreenPoint is not null)
            return; // a newer dock, an open flyout or a drag: not now

        var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        Box Dips(RECT r)
        {
            var a = toDips.Transform(new Point(r.Left, r.Top));
            var b = toDips.Transform(new Point(r.Right, r.Bottom));
            return new Box(a.X, a.Y, b.X - a.X, b.Y - a.Y);
        }
        TaskbarFacts? facts = null;
        var fallback = ToBox(SystemParameters.WorkArea);
        if (read.monitor is { } m)
        {
            fallback = Dips(m.Work);
            if (read.taskbar is { } t)
                facts = new TaskbarFacts(Dips(t.Rect), t.Tray is { } tray ? Dips(tray) : null,
                    t.ButtonsRight is { } right ? toDips.Transform(new Point(right, 0)).X : null,
                    Dips(m.Bounds), Dips(m.Work), t.AutoHide);
        }
        SetCompact(true);
        var compactWidth = MeasuredWidth(RootBorder);
        ApplyDockSpot(DockPlacement.Place(wanted, facts, compactWidth, fallback));
    }

    /// <summary>Puts the pill at a dock spot: compact and the taskbar's height inside it, full size above it.</summary>
    internal void ApplyDockSpot(DockSpot spot)
    {
        DockedAt = spot;
        _pillHeight = spot.Height;
        RootBorder.Height = spot.Height;
        RootBorder.CornerRadius = new CornerRadius(spot.Height / 2);
        _pillRight = spot.Right;
        _pillBottom = spot.Bottom;
        _opensAbove = null;
        SetCompact(spot.Mode == DockMode.Taskbar);
        ApplyLayout();
        KeepOnTop();
        var line = spot.Mode == DockMode.Taskbar ? "Docked in the taskbar." : $"Docked above the taskbar{(spot.Reason is { } why ? $" ({why})" : "")}.";
        if (line != _lastDockLine)
            DiagnosticLog.WriteInfo(line);
        _lastDockLine = line;
    }

    /// <summary>Hides the pill while a full-screen app or a presentation is on its monitor (a film, a
    /// screen share), and shows it again afterwards (Settings: Hide in full screen).</summary>
    private void CheckFullScreen()
    {
        if (!IsLoaded || (!IsVisible && !_hiddenForFullScreen))
            return;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || TaskbarDock.MonitorOf(handle) is not { } monitor)
            return;
        var hide = FullScreenRule.ShouldHide(_settings.HideWhenFullscreen, TaskbarDock.NotificationState(), TaskbarDock.ForegroundFills(monitor));
        if (hide && IsVisible)
        {
            _hiddenForFullScreen = true;
            Collapse();
            Hide();
            DiagnosticLog.WriteInfo("Full screen on the widget's monitor: hidden until it ends.");
        }
        else if (!hide && _hiddenForFullScreen)
        {
            _hiddenForFullScreen = false;
            Show();
            KeepOnTop();
            DiagnosticLog.WriteInfo("Full screen ended: the widget is back.");
        }
    }

    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _opensAbove = null; // decide afresh from where the pill is now
        ExpandedPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.ToolTip = _isExpanded ? "Hide sessions" : "Show sessions";
        // The pill's tooltips repeat what the open panel shows and can cover its header: none while it is open.
        System.Windows.Controls.ToolTipService.SetIsEnabled(RootBorder, !_isExpanded);
        ApplyLayout();
        if (_isExpanded)
            _ = RefreshSessionsAsync(logCount: true);
    }

    /// <summary>Closes the flyout if it's open - done before showing
    /// Settings, so the flyout (always on top) never covers it.</summary>
    public void Collapse()
    {
        if (_isExpanded)
            ToggleExpanded();
    }

    public void RefreshFromSettings()
    {
        // Settings → Widget position: a new mode or monitor docks at once.
        var dock = (_settings.DockMode, _settings.DockMonitor);
        if (dock != _appliedDock)
        {
            _appliedDock = dock;
            if (IsLoaded)
            {
                if (DockModes.Parse(_settings.DockMode) == DockMode.Free)
                    RestorePosition();
                else
                    _ = DockAsync();
            }
        }
        // A new usage source is built at the start of the next refresh (no fetch runs then).
        _services.SettingsChanged();
        Feed.Layout = _settings.CardLayout();
        Feed.ShowSessionLights = _settings.ShowSessionLights;
        _ = RefreshAsync();
    }

    private (string, string) _appliedDock;

    /// <summary>"Reset widget position": docks again. A pill left where it was dragged goes back into
    /// the taskbar (the default); one set to dock above stays above.</summary>
    public void ResetPosition()
    {
        if (DockModes.Parse(_settings.DockMode) == DockMode.Free)
            SetDockMode(DockMode.Taskbar);
        Collapse();
        _ = DockAsync();
    }

    /// <summary>The tray menu's "Move widget to → Taskbar" (and a monitor): docks there and remembers it.</summary>
    public void DockInTaskbar(string? monitorDevice = null)
    {
        if (monitorDevice is not null)
            _settings.DockMonitor = monitorDevice;
        SetDockMode(DockMode.Taskbar);
        Collapse();
        _ = DockAsync();
    }

    private void SetDockMode(DockMode mode)
    {
        _settings.DockMode = mode.ToSetting();
        _settings.WidgetDocked = mode != DockMode.Free;
        _appliedDock = (_settings.DockMode, _settings.DockMonitor);
        if (mode == DockMode.Free)
            DockedAt = null;
        SettingsService.Save(_settings);
    }

    /// <summary>Moves the widget to a corner of the work area (the screen
    /// minus the taskbar) and remembers that spot.</summary>
    public void MoveToCorner(WidgetCorner corner) => PlaceAtCorner(corner);

    /// <summary>At start: where it was left, if that is still on a screen;
    /// otherwise docked at the tray (the saved spot is kept for a start when
    /// that monitor is back).</summary>
    internal void RestorePosition()
    {
        _appliedDock = (_settings.DockMode, _settings.DockMonitor);
        var free = DockModes.Parse(_settings.DockMode) == DockMode.Free;
        var screen = new Box(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        if (!free || WidgetPosition.Saved(_settings, screen, OuterMargin, DockPlacement.FullHeight) is not { } corner)
        {
            if (free)
                DiagnosticLog.WriteInfo("The widget's saved spot is off every screen; docking it for now.");
            // At once in the corner above the tray, which never covers anything; the taskbar is read
            // off the UI thread (a moment) and the pill moves in when there is room.
            ApplyDockSpot(DockPlacement.Place(DockMode.Above, null, 0, MonitorWorkArea() ?? ToBox(SystemParameters.WorkArea)));
            _ = DockAsync(free ? DockMode.Taskbar : null);
            return;
        }
        DockedAt = null;
        _pillHeight = DockPlacement.FullHeight;
        RootBorder.Height = _pillHeight;
        RootBorder.CornerRadius = new CornerRadius(_pillHeight / 2);
        _pillRight = corner.Right;
        _pillBottom = corner.Bottom;
        _opensAbove = null;
        UpdateCompactForPosition();
        ApplyLayout();
        KeepOnTop();
    }

    /// <summary>Test hook: the pinned bottom-right corner of the pill.</summary>
    internal (double Right, double Bottom) PillCorner => (_pillRight, _pillBottom);

    /// <summary>"Move widget to" a corner: left there ("free"), as if dragged.</summary>
    private void PlaceAtCorner(WidgetCorner corner)
    {
        Collapse();
        _pillHeight = DockPlacement.FullHeight;
        RootBorder.Height = _pillHeight;
        RootBorder.CornerRadius = new CornerRadius(_pillHeight / 2);
        var work = SystemParameters.WorkArea;
        var right = corner is WidgetCorner.TopRight or WidgetCorner.BottomRight;
        var top = corner is WidgetCorner.TopLeft or WidgetCorner.TopRight;

        // Full form first (outside the taskbar), so the width is the final one.
        _pillRight = work.Right - CornerGap;
        _pillBottom = work.Bottom - CornerGap;
        UpdateCompactForPosition();
        var width = MeasuredWidth(RootBorder);

        _pillRight = right ? work.Right - CornerGap : work.Left + CornerGap + width;
        _pillBottom = top ? work.Top + CornerGap + PillHeight : work.Bottom - CornerGap;
        _opensAbove = null;
        UpdateCompactForPosition();
        ApplyLayout();

        _settings.WidgetRight = _pillRight + OuterMargin;
        _settings.WidgetBottom = _pillBottom + OuterMargin;
        SetDockMode(DockMode.Free);
        KeepOnTop();
    }

    // ---------------------------------------------------------------- input

    private void CollapsedStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartScreenPoint = PointToScreen(e.GetPosition(this));
        _didDrag = false;
        RootBorder.CaptureMouse();
        RootBorder.MouseMove += CollapsedStrip_MouseMove;
    }

    private void CollapsedStrip_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartScreenPoint is not { } start || e.LeftButton != MouseButtonState.Pressed)
            return;

        var current = PointToScreen(e.GetPosition(this));
        var delta = current - start;

        if (!_didDrag && delta.Length < DragThreshold)
            return;

        _didDrag = true;

        // Move by the same delta, converted from device pixels to DIPs.
        var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var deltaDips = toDips.Transform(new Point(delta.X, delta.Y)) - toDips.Transform(new Point(0, 0));

        _pillRight += deltaDips.X;
        _pillBottom += deltaDips.Y;
        _dragStartScreenPoint = current;
        _opensAbove = null; // moved: pick the side again
        UpdateCompactForPosition();
        ApplyLayout();
    }

    private void CollapsedStrip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // No matching MouseDown on the pill itself (e.g. the press started on
        // one of its buttons) - not a click or drag of ours.
        if (_dragStartScreenPoint is null)
            return;

        RootBorder.ReleaseMouseCapture();
        RootBorder.MouseMove -= CollapsedStrip_MouseMove;
        _dragStartScreenPoint = null;

        if (_didDrag)
        {
            // Out of the taskbar it takes its full height again.
            if (_pillHeight != DockPlacement.FullHeight)
            {
                _pillHeight = DockPlacement.FullHeight;
                RootBorder.Height = _pillHeight;
                RootBorder.CornerRadius = new CornerRadius(_pillHeight / 2);
                ApplyLayout();
            }
            _settings.WidgetRight = _pillRight + OuterMargin;
            _settings.WidgetBottom = _pillBottom + OuterMargin;
            SetDockMode(DockMode.Free);
            return;
        }

        ToggleExpanded();
    }

    private void ExpandButton_Click(object sender, RoutedEventArgs e) => ToggleExpanded();

    /// <summary>Fills the Today | 2d | 1w | 2w | 30d tabs under the header icons.</summary>
    private void BuildRangeTabs()
    {
        foreach (var days in SessionRange.Options)
        {
            var tab = new System.Windows.Controls.RadioButton
            {
                Content = SessionRange.Tab(days),
                GroupName = "SessionRange",
                IsChecked = days == _sessionDays,
                Style = (Style)FindResource("SegmentTab")
            };
            tab.Checked += (_, _) =>
            {
                _sessionDays = days;
                RangeTitleText.Text = SessionRange.Title(days);
                SessionsSummaryText.Text = "Loading…";
                _ = RefreshSessionsAsync(logCount: true);
            };
            RangeTabs.Children.Add(tab);
        }
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        SettingsRequested?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- share

    /// <summary>Copies, saves or posts cards; a test can swap it for a fake.</summary>
    internal IShareOutput ShareOutput { get; set; }

    private DispatcherTimer? _shareStatusTimer;
    private System.Windows.Controls.ContextMenu? _shareMenu;

    /// <summary>Test hook: the Share menu's items, as last opened.</summary>
    internal IReadOnlyList<string> ShareMenuItems =>
        _shareMenu?.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => (string)i.Header).ToList() ?? [];

    /// <summary>Test hook: whether each of the Share menu's items can be chosen, as last opened.</summary>
    internal IReadOnlyList<bool> ShareMenuItemsEnabled =>
        _shareMenu?.Items.OfType<System.Windows.Controls.MenuItem>().Select(i => i.IsEnabled).ToList() ?? [];

    /// <summary>Test hook: the year submenu's items, as last opened (empty outside the season).</summary>
    internal IReadOnlyList<string> ShareYearMenuItems =>
        _shareMenu?.Items.OfType<System.Windows.Controls.MenuItem>().LastOrDefault(i => i.Items.Count > 0)?.Items
            .OfType<System.Windows.Controls.MenuItem>().Select(i => (string)i.Header).ToList() ?? [];

    /// <summary>Test hook: the clock the Share menu reads (whether the year card is in season).</summary>
    internal Func<DateTimeOffset> ShareClock { get; set; } = () => DateTimeOffset.UtcNow;

    private YearStatsService? _yearStats;
    private bool _hasReading;
    private int? _readingYear;

    /// <summary>The year card's numbers (a test hands in its own).</summary>
    internal YearStatsService YearStats
    {
        get => _yearStats ?? _services.YearStats;
        set => _yearStats = value;
    }

    /// <summary>The header's Share: today's card for the current reading, and in
    /// December and January the year card.</summary>
    private void ShareButton_Click(object sender, RoutedEventArgs e) =>
        OpenShareMenu(ShareButton, TodayCardAsync, _hasReading, YearCard.SeasonYear(ShareClock()));

    private async Task<ShareCardModel?> TodayCardAsync()
    {
        if (_lastReading is not { } reading)
            return null;
        var today = await Feed.GetTodaySessionsAsync();
        return ShareCards.ForUsage(reading.Usage, reading.Forecasts, today, reading.SpentToday, _settings, DateTimeOffset.UtcNow);
    }

    /// <summary>The year card, once the year's transcripts are read; the line under the
    /// header counts them meanwhile.</summary>
    internal async Task<ShareCardModel?> YearCardAsync(int year)
    {
        _readingYear = year;
        ShowShareStatus(YearCard.ProgressText(year, default), sticky: true);
        var progress = new Progress<StatsProgress>(p =>
        {
            if (_readingYear == year)
                ShowShareStatus(YearCard.ProgressText(year, p), sticky: true);
        });
        try
        {
            return ShareCards.ForYear(await YearStats.GetAsync(year, progress), _settings);
        }
        finally
        {
            _readingYear = null;
        }
    }

    /// <summary>Copy image, Save image…, Post on X / LinkedIn / Bluesky, under the button;
    /// with a <paramref name="year"/>, the same for the year card in a submenu.</summary>
    private void OpenShareMenu(FrameworkElement anchor, Func<Task<ShareCardModel?>> card, bool cardReady = true, int? year = null)
    {
        // One menu, its items made afresh each time: a closed menu's items would
        // otherwise linger where UI Automation (a screen reader) can still invoke
        // them, sharing the card that menu was opened for.
        var menu = _shareMenu ??= new System.Windows.Controls.ContextMenu
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };
        menu.IsOpen = false;
        menu.Items.Clear();
        menu.PlacementTarget = anchor;
        AddShareItems(menu.Items, action => ShareAsync(action, card), cardReady);
        if (year is { } y)
        {
            menu.Items.Add(new System.Windows.Controls.Separator());
            var yearMenu = new System.Windows.Controls.MenuItem { Header = YearCard.Title(y) };
            AddShareItems(yearMenu.Items, action => ShareAsync(action, () => YearCardAsync(y), YearCard.NothingYet(y)), enabled: true);
            menu.Items.Add(yearMenu);
            // Start reading the year now: it is usually done by the time a choice is made.
            _ = YearStats.GetAsync(y);
        }
        menu.IsOpen = true;
    }

    private static void AddShareItems(System.Windows.Controls.ItemCollection items, Func<ShareAction, Task> run, bool enabled)
    {
        foreach (var action in ShareFlow.Actions)
        {
            if (action == ShareAction.PostX)
                items.Add(new System.Windows.Controls.Separator());
            var item = new System.Windows.Controls.MenuItem { Header = ShareFlow.MenuText(action), IsEnabled = enabled };
            if (!enabled)
                System.Windows.Controls.ToolTipService.SetShowOnDisabled(item, true);
            item.ToolTip = enabled ? null : ShareFlow.NoReadingTooltip;
            item.Click += async (_, _) => await run(action);
            items.Add(item);
        }
    }

    /// <summary>Renders the card in the current theme and runs the choice; the
    /// outcome shows under the header for a few seconds.</summary>
    /// <param name="nothing">What to say when there is no card (default: no reading yet).</param>
    internal async Task ShareAsync(ShareAction action, Func<Task<ShareCardModel?>> card, string? nothing = null)
    {
        try
        {
            if (await card() is not { } model)
            {
                ShowShareStatus(nothing ?? ShareFlow.NoReadingTooltip + ".");
                return;
            }
            var png = ShareService.Png(model, ThemeService.IsDark);
            DiagnosticLog.WriteInfo($"Share: {action} of a {model.Variant} card ({model.Shape}).");
            ShowShareStatus(await ShareFlow.RunAsync(action, png, model.Variant, ShareOutput, DateTimeOffset.UtcNow, year: model.Year));
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DockedStripWindow.ShareAsync", ex);
            ShowShareStatus(ShareFlow.NothingWorked);
        }
    }

    /// <param name="text">The line; null (a cancelled save) clears a line still showing, such as a progress line.</param>
    /// <param name="sticky">Stays until the next line (a progress line), instead of 8 seconds.</param>
    private void ShowShareStatus(string? text, bool sticky = false)
    {
        if (text is null)
        {
            HideShareStatus(null, EventArgs.Empty);
            return;
        }
        ShareStatusText.Text = text;
        ShareStatusText.Visibility = Visibility.Visible;
        _shareStatusTimer?.Stop();
        if (sticky)
            return;
        _shareStatusTimer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
        _shareStatusTimer.Tick -= HideShareStatus;
        _shareStatusTimer.Tick += HideShareStatus;
        _shareStatusTimer.Start();
    }

    private void HideShareStatus(object? sender, EventArgs e)
    {
        _shareStatusTimer?.Stop();
        ShareStatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>The health line's one way forward.</summary>
    private void HealthButton_Click(object sender, RoutedEventArgs e)
    {
        switch (_lastView?.Health?.Action)
        {
            case HealthAction.OpenLogFolder:
                LogFolderRequested?.Invoke(this, EventArgs.Empty);
                break;
            case HealthAction.OpenSettings:
                SettingsRequested?.Invoke(this, EventArgs.Empty);
                break;
            case HealthAction.OpenHealth:
                HealthRequested?.Invoke(this, EventArgs.Empty);
                break;
        }
    }

    /// <summary>Opens a terminal running Claude Code's login (it then opens the browser to confirm).
    /// /k keeps the window open so any error stays readable.</summary>
    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k " + ClaudeCommands.SignIn) { UseShellExecute = true });
            DiagnosticLog.WriteInfo($"Sign in: started '{ClaudeCommands.SignIn}' in a terminal.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DockedStripWindow.SignInButton_Click", ex);
        }
    }

    private void CopyNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lastView?.Notice is { } notice)
            CopyCommandButton.Copy(CopyNoticeButton, notice.CopyText);
    }

    /// <summary>The plugin tip goes for good; the view is worked out again
    /// from the last reading, so a repaint never brings it back.</summary>
    private void DismissNoticeButton_Click(object sender, RoutedEventArgs e)
    {
        _settings.PluginTipDismissed = true;
        SettingsService.Save(_settings);
        DiagnosticLog.WriteInfo("Plugin tip dismissed.");
        if (_lastReading is { } last)
            ApplyUsage(last.Usage, last.Session, last.SpentToday, last.Forecasts, last.Others);
    }

    private void ThemeToggleButton_Click(object sender, RoutedEventArgs e) => ThemeService.Toggle();

    /// <summary>
    /// Repaints what the theme dictionaries can't: the brushes this code sets
    /// by hand, and the session cards, whose segment colors come from triggers
    /// that only re-run when the item changes.
    /// </summary>
    private void ApplyTheme()
    {
        var dark = ThemeService.IsDark;
        ThemeToggleButton.Content = dark ? SunGlyph : MoonGlyph;
        ThemeToggleButton.ToolTip = dark ? "Switch to the light theme" : "Switch to the dark theme";

        ApplyRunningState();
        if (_lastView is not null)
            ShowView(_lastView);
        ShowLights();
        if (_sessions.Count > 0)
        {
            _sessions.Clear();
            foreach (var card in _lastCards)
                _sessions.Add(card);
        }
    }

    private void RunToggleButton_Click(object sender, RoutedEventArgs e)
    {
        SetRunning(!_isRunning);
        _settings.IsRunning = _isRunning;
        SettingsService.Save(_settings);
        if (_isRunning)
        {
            // Force a fresh log line on resume even if the selected session is
            // unchanged - otherwise "nothing updated for hours" and "the
            // widget was paused" look identical in diagnostics.log.
            DiagnosticLog.WriteInfo("Resumed from paused state.");
            Feed.ForgetLoggedSession();
            _ = RefreshAsync();
        }
        else
        {
            DiagnosticLog.WriteInfo("Paused by user.");
        }
    }

    internal void SetRunning(bool running)
    {
        _isRunning = running;
        ApplyRunningState();
    }

    /// <summary>Paused: dimmed pill and a play button, so the state reads at a glance.</summary>
    private void ApplyRunningState()
    {
        RootBorder.Background = (Brush)FindResource(_isRunning ? "StripBackground" : "StripBackgroundStopped");
        SpendGroup.Opacity = DayGroup.Opacity = SessionGroup.Opacity = _isRunning ? 1 : 0.5;
        RunToggleButton.Content = _isRunning ? PauseGlyph : PlayGlyph;
        RunToggleButton.ToolTip = _isRunning ? "Pause live updates" : "Resume live updates";
    }

    // -------------------------------------------------------------- refresh

    /// <summary>Always called on the UI thread. The usage fetch and the
    /// transcript scans run on the thread pool (UsageFeed); a tick that fires
    /// while the previous refresh is still running is skipped rather than stacked.</summary>
    /// <summary>Raised on a thread-pool thread; the refresh runs on the UI thread.</summary>
    private void OnUsageChanged() =>
        Dispatcher.InvokeAsync(async () =>
        {
            // A refresh already under way may have read the folder before this
            // write (an endpoint request can take seconds): the gate runs one
            // more after it.
            if (_isRunning && _refreshGate.RequestRefresh())
                await RefreshAsync();
        });

    private async Task RefreshAsync()
    {
        if (!_refreshGate.TryStart())
            return;
        try
        {
            _services.ApplyPendingChange();
            var summary = await Feed.GetSummaryAsync();
            ApplyUsage(summary.Usage, summary.Session, summary.SpentToday, summary.Forecasts, summary.Others);
            ApplyActivities(summary.Activities, summary.HasPlugin);
            ApplyUpdate(summary.Update);
            RaiseAlerts(summary.Alerts);
            ScheduleAttentionCheck(summary.AttentionDueAt);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DockedStripWindow.RefreshAsync", ex);
        }

        if (_refreshGate.Finish())
        {
            await RefreshAsync();
            return;
        }

        if (_isExpanded)
            await RefreshSessionsAsync(logCount: false);
    }

    /// <summary>Raised on the UI thread with a refresh's notifications; the App
    /// owns the tray icon that shows them.</summary>
    public event Action<IReadOnlyList<Alerts.Alert>>? AlertsRaised;

    /// <summary>Hands the alerts on and pulses the pill twice (not when the
    /// OS asks for less motion).</summary>
    internal void RaiseAlerts(IReadOnlyList<Alerts.Alert> alerts)
    {
        if (alerts.Count == 0)
            return;
        AlertsRaised?.Invoke(alerts);
        Motion.Pulse(RootBorder);
    }

    /// <summary>The plan layout of the latest reading; Settings uses it to
    /// turn off what doesn't apply (the day target on Pro/Max).</summary>
    public UsageMode CurrentMode => _lastView?.Mode ?? UsageMode.Unknown;

    /// <param name="others">The other tools' readings (Codex): a card each in the flyout, a chip on the pill.</param>
    internal void ApplyUsage(UsageSnapshot usage, SessionInfo? session, decimal? spentToday = null,
        IReadOnlyList<PaceForecast>? forecasts = null, IReadOnlyList<ProviderReading>? others = null)
    {
        var vm = WidgetViewModel.From(usage, session, _settings, DateTimeOffset.UtcNow, spentToday: spentToday, forecasts: forecasts,
            others: others);
        _lastReading = (usage, session, spentToday, forecasts, others);
        _lastView = vm;
        ShowView(vm);
        UsageApplied?.Invoke();
    }

    private (UsageSnapshot Usage, SessionInfo? Session, decimal? SpentToday, IReadOnlyList<PaceForecast>? Forecasts,
        IReadOnlyList<ProviderReading>? Others)? _lastReading;

    /// <summary>Test hook: the pill shows its compact (in the taskbar) form.</summary>
    internal bool IsCompact => _compact;

    /// <summary>Test hook and position handler: the compact (in the taskbar)
    /// or full (on the desktop) form of the pill.</summary>
    internal void SetCompact(bool compact)
    {
        _compact = compact;
        if (_lastView is not null)
            ShowView(_lastView);
        ShowLights();
    }

    // ------------------------------------------------------- session lights

    private IReadOnlyList<SessionActivity> _activities = [];
    private bool _hasPlugin;
    private DispatcherTimer? _attentionTimer;

    /// <summary>Test hook and refresh step: what each Claude Code session is
    /// doing, and whether the plugin is there at all.</summary>
    internal void ApplyActivities(IReadOnlyList<SessionActivity> activities, bool hasPlugin)
    {
        _activities = activities;
        _hasPlugin = hasPlugin;
        ShowLights();
    }

    /// <summary>The dots after the usage text (one in the compact pill), their
    /// tooltip, and the flyout's hint when there is no plugin.</summary>
    private void ShowLights()
    {
        var lights = SessionLights.From(_activities, DateTimeOffset.UtcNow, _settings.ShowSessionLights);
        LightsDots.Children.Clear();
        var dots = _compact ? (lights?.CompactDot is { } one ? [one] : []) : lights?.Dots ?? [];
        foreach (var kind in dots)
            LightsDots.Children.Add(Dot(kind));
        SetOptionalText(LightsMoreText, _compact ? null : lights?.MoreText);
        LightsGroup.Visibility = dots.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LightsGroup.ToolTip = lights?.Tooltip;
        System.Windows.Automation.AutomationProperties.SetName(LightsGroup, lights is null ? "" : "Claude Code sessions: " + lights.Summary);

        LightsHintText.Text = SessionLights.ConnectHint;
        LightsHintText.Visibility = _settings.ShowSessionLights && !_hasPlugin && _lastView?.Notice?.Kind != FlyoutNoticeKind.InstallPlugin
            ? Visibility.Visible
            : Visibility.Collapsed;
        LightsConnectButton.Content = SessionLights.ConnectAction;
        LightsConnectButton.Visibility = LightsHintText.Visibility;
    }

    // ------------------------------------------------------------ updates

    private Updates.UpdateNotice? _update;

    /// <summary>A newer version, from the last refresh (Check for updates on); the tray menu shows it too.</summary>
    public Updates.UpdateNotice? LatestUpdate => _update;

    /// <summary>Test hook and refresh step: the flyout's update line, or none.</summary>
    internal void ApplyUpdate(Updates.UpdateNotice? notice)
    {
        if (Equals(notice, _update))
            return;
        _update = notice;
        UpdatePanel.Visibility = notice is null ? Visibility.Collapsed : Visibility.Visible;
        if (notice is null)
            return;
        UpdateLinkButton.Content = notice.LinkText;
        UpdateHowText.Text = notice.HowText;
        UpdateCommandText.Text = notice.Command ?? "";
        UpdateCommandRow.Visibility = notice.Command is null ? Visibility.Collapsed : Visibility.Visible;
        DiagnosticLog.WriteInfo($"Updates: the flyout shows version {notice.Version} ({notice.Method.ToString().ToLowerInvariant()}).");
    }

    private void UpdateLink_Click(object sender, RoutedEventArgs e)
    {
        if (_update is { } notice)
            OpenReleasePage(notice);
    }

    private void CopyUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_update?.Command is { } command)
            CopyCommandButton.Copy(CopyUpdateButton, command);
    }

    /// <summary>The release page of a notice: always one of this project's (the
    /// checker builds the address from the tag; nothing from the network is opened).</summary>
    internal static void OpenReleasePage(Updates.UpdateNotice notice)
    {
        if (!notice.ReleaseUrl.StartsWith(Updates.UpdateChecker.ReleasePageBase, StringComparison.Ordinal))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(notice.ReleaseUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DockedStripWindow.OpenReleasePage", ex);
        }
    }

    /// <summary>Raised by the session-lights hint's "Connect to Claude Code…".</summary>
    public event EventHandler? ConnectRequested;

    private void LightsConnect_Click(object sender, RoutedEventArgs e) => ConnectRequested?.Invoke(this, EventArgs.Empty);

    private void ConnectNoticeButton_Click(object sender, RoutedEventArgs e) => ConnectRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>One dot: filled amber (pulsing) for needs you, filled green
    /// (soft pulse) for working, red for rate limited, a grey ring otherwise.</summary>
    private FrameworkElement Dot(LightKind kind)
    {
        var filled = kind is LightKind.NeedsYou or LightKind.Working or LightKind.RateLimited;
        var brush = (Brush)FindResource(kind switch
        {
            LightKind.NeedsYou => "ColorOrange",
            LightKind.Working => "ColorGreen",
            LightKind.RateLimited => "ColorRed",
            _ => "TextTertiaryBrush"
        });
        var dot = new System.Windows.Shapes.Ellipse
        {
            Width = 8,
            Height = 8,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = filled ? brush : Brushes.Transparent,
            Stroke = filled ? null : brush,
            StrokeThickness = 1.5,
            Opacity = kind == LightKind.NoSignal ? 0.5 : 1,
            Tag = kind
        };
        if (kind is LightKind.NeedsYou or LightKind.Working)
            Motion.Breathe(dot, strong: kind == LightKind.NeedsYou);
        return dot;
    }

    /// <summary>A refresh right when a waiting session's "needs you" alert
    /// falls due, so it comes 20 seconds into the wait, not up to 15 later.</summary>
    private void ScheduleAttentionCheck(DateTimeOffset? dueAt)
    {
        _attentionTimer?.Stop();
        if (dueAt is not { } due)
            return;
        var wait = due - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(250);
        if (wait > TimeSpan.FromSeconds(15))
            return; // the next tick comes first
        _attentionTimer ??= new DispatcherTimer();
        _attentionTimer.Tick -= OnAttentionDue;
        _attentionTimer.Tick += OnAttentionDue;
        _attentionTimer.Interval = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        _attentionTimer.Start();
    }

    private async void OnAttentionDue(object? sender, EventArgs e)
    {
        _attentionTimer?.Stop();
        if (_isRunning)
            await RefreshAsync();
    }

    /// <summary>Monthly cap: "$96.75 48% │ Today: $4.20 42% │ Session: $3.05". Plan: "5h: 43% -
    /// Resets: 2h 6m │ 1w: 14% │ Rem. Credits: $9.32 │ Updated: just now", or
    /// in the taskbar just the windows: "5h: 43% - 2h 6m │ 1w: 14%".</summary>
    private void ShowView(WidgetViewModel vm)
    {
        NoticeBar.Visibility = vm.Notice is null ? Visibility.Collapsed : Visibility.Visible;
        // A dismissed or resolved notice leaves no text behind in the hidden bar.
        if (vm.Notice is null)
            NoticeText.Text = "";
        if (vm.Notice is { } notice)
        {
            NoticeText.Text = notice.Message;
            CopyCommandButton.SetLabel(CopyNoticeButton, notice.CopyLabel);
            CopyNoticeButton.ToolTip = "Copies: " + notice.CopyText;
            SignInButton.Visibility = notice.Kind == FlyoutNoticeKind.SignIn ? Visibility.Visible : Visibility.Collapsed;
            ConnectNoticeButton.Content = FlyoutNotice.ConnectLabel;
            ConnectNoticeButton.Visibility = notice.CanConnect ? Visibility.Visible : Visibility.Collapsed;
            DismissNoticeButton.Visibility = notice.CanDismiss ? Visibility.Visible : Visibility.Collapsed;
        }
        SpendRing.Visibility = vm.ShowRing ? Visibility.Visible : Visibility.Collapsed;
        SpendRing.Value = vm.RingPercent;
        SpendRing.Stroke = LevelBrush(vm.Level);
        MonthlySpendText.Text = vm.PrimaryText;
        MonthlySpendText.Foreground = (Brush)FindResource(vm.State switch
        {
            UsageState.SignIn => "CautionBrush",
            UsageState.Unavailable => "TextTertiaryBrush",
            _ => "TextPrimaryBrush"
        });
        SetOptionalText(MonthlyPercentText, _compact ? vm.SecondaryCompactText : vm.SecondaryText);
        ShowForecastChip(_compact ? vm.ForecastChipCompactText : vm.ForecastChipText, vm.ForecastLevel);
        SetOptionalText(StaleText, vm.StaleText);
        SetOptionalText(WeeklyText, vm.WeeklyText, WeeklyDivider);
        WeeklyText.Foreground = vm.WeeklyLevel is { } weeklyLevel ? LevelBrush(weeklyLevel) : (Brush)FindResource("TextPrimaryBrush");
        SetOptionalText(CreditsText, _compact ? null : vm.CreditsText, CreditsDivider);
        SetOptionalText(UpdatedText, _compact ? null : vm.UpdatedText, UpdatedDivider);
        SpendGroup.ToolTip = vm.UsageTooltip;
        SpendGroup.Visibility = vm.ShowUsage ? Visibility.Visible : Visibility.Collapsed;

        // Nothing to put on today's card without a reading (a session card still works);
        // in December and January the year card needs none.
        _hasReading = vm.Mode != UsageMode.Unknown;
        var yearInSeason = YearCard.SeasonYear(ShareClock()) is not null;
        ShareButton.IsEnabled = _hasReading || yearInSeason;
        ShareButton.ToolTip = ShareFlow.HeaderTooltip(_hasReading, yearInSeason);
        SummaryTitleText.Text = vm.SummaryTitle;
        SummaryUpdatedText.Text = vm.SummaryUpdatedText ?? "";
        SummaryRowsList.ItemsSource = vm.SummaryRows;
        HealthPanel.Visibility = vm.Health is null ? Visibility.Collapsed : Visibility.Visible;
        HealthText.Text = vm.Health?.Detail ?? "";
        HealthButton.Content = vm.Health?.ActionLabel;
        HealthButton.Visibility = vm.Health?.ActionLabel is null ? Visibility.Collapsed : Visibility.Visible;
        MonthSummary.Visibility = vm.SummaryRows.Count > 0 || vm.Health is not null ? Visibility.Visible : Visibility.Collapsed;
        ProviderSectionsList.ItemsSource = vm.ProviderSections;
        ProviderSectionsList.Visibility = vm.ProviderSections.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowExtraChips(vm.ExtraChips);

        DayGroup.Visibility = vm.ShowDayTarget ? Visibility.Visible : Visibility.Collapsed;
        DayRing.Visibility = vm.ShowDayRing ? Visibility.Visible : Visibility.Collapsed;
        DayRing.Value = vm.DayRingPercent;
        DayRing.Stroke = LevelBrush(vm.DayLevel);
        // Just a ring: no "Today:" label trailing after it with nothing to label.
        // In the taskbar (compact) the labels and the day's percentage go: the
        // ring already shows the percentage, and the tooltips name each figure.
        var dayPercent = _compact && vm.ShowDayRing ? null : vm.DayPercentText;
        var ringOnly = vm.DayAmountText is null && dayPercent is null;
        var hideDayLabel = ringOnly || (_compact && vm.ShowDayRing);
        DayLabel.Visibility = hideDayLabel ? Visibility.Collapsed : Visibility.Visible;
        DayRing.Margin = new Thickness(0, 0, ringOnly ? 0 : 8, 0);
        SetOptionalText(DayAmountText, vm.DayAmountText);
        SetOptionalText(DayPercentText, dayPercent);
        DayAmountText.Margin = new Thickness(hideDayLabel ? 0 : 5, 0, 0, 0);
        DayPercentText.Margin = new Thickness(vm.DayAmountText is null ? 5 : 6, 0, 0, 0);
        DayGroup.ToolTip = vm.DayTooltip;
        DayDivider.Visibility = vm.ShowUsage && vm.ShowDayTarget ? Visibility.Visible : Visibility.Collapsed;

        SessionGroup.Visibility = vm.ShowSession ? Visibility.Visible : Visibility.Collapsed;
        SessionCostText.Text = vm.SessionText;
        SessionLabel.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        SessionCostText.Margin = new Thickness(_compact ? 0 : 5, 0, 0, 0);
        SessionGroup.ToolTip = vm.SessionTooltip;
        StripDivider.Visibility = vm.ShowSession && (vm.ShowUsage || vm.ShowDayTarget) ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>"Codex 91%" after the usage, each behind a divider, in its level's color.</summary>
    private void ShowExtraChips(IReadOnlyList<ProviderChip> chips)
    {
        ExtraChipsPanel.Children.Clear();
        foreach (var chip in chips)
        {
            ExtraChipsPanel.Children.Add(new System.Windows.Shapes.Rectangle
            {
                Width = 1, Height = 14, Margin = new Thickness(11, 0, 11, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = (Brush)FindResource("StrongBorderBrush")
            });
            ExtraChipsPanel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = chip.Text, FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = LevelBrush(chip.Level), ToolTip = chip.Tooltip
            });
        }
        ExtraChipsPanel.Visibility = chips.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Test hook: the texts of the pill's other-tool chips.</summary>
    internal IReadOnlyList<string> ExtraChipTexts =>
        ExtraChipsPanel.Children.OfType<System.Windows.Controls.TextBlock>().Select(t => t.Text).ToList();

    /// <summary>The pace chip in its color; it fades in when it appears.</summary>
    private void ShowForecastChip(string? text, UsageLevel level)
    {
        var appearing = !string.IsNullOrEmpty(text) && ForecastText.Visibility != Visibility.Visible;
        SetOptionalText(ForecastText, text);
        ForecastText.Foreground = LevelBrush(level);
        if (appearing)
            Motion.FadeIn(ForecastText);
    }

    private static void SetOptionalText(System.Windows.Controls.TextBlock block, string? text, UIElement? divider = null)
    {
        block.Text = text ?? "";
        block.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
        if (divider is not null)
            divider.Visibility = block.Visibility;
    }

    private Brush LevelBrush(UsageLevel level) => (Brush)FindResource(level switch
    {
        UsageLevel.Red => "ColorRed",
        UsageLevel.Orange => "ColorOrange",
        UsageLevel.Yellow => "ColorYellow",
        _ => "ColorGreen"
    });

    // ------------------------------------------------------------- sessions

    private async Task RefreshSessionsAsync(bool logCount)
    {
        if (_sessionsRefreshInFlight)
            return;
        _sessionsRefreshInFlight = true;
        try
        {
            int days;
            List<SessionCardViewModel> cards;
            do // re-fetch if the range was changed while loading
            {
                days = _sessionDays;
                cards = await Feed.GetCardsAsync(days);
            } while (days != _sessionDays);

            if (logCount)
                DiagnosticLog.WriteInfo($"Expand panel: found {cards.Count} session(s) for {SessionRange.Title(days)}.");
            ShowSessions(cards);
        }
        catch (Exception ex)
        {
            // An exception here used to leave the panel silently empty.
            DiagnosticLog.WriteError("DockedStripWindow.RefreshSessionsAsync", ex);
            NoSessionsText.Text = "Couldn't load sessions - see diagnostics.log (tray menu → Open log folder).";
            NoSessionsText.Visibility = Visibility.Visible;
        }
        finally
        {
            _sessionsRefreshInFlight = false;
        }
    }

    /// <summary>Updates the card list in place: only cards whose content
    /// changed are replaced, so an open panel keeps its scroll position.</summary>
    internal void ShowSessions(IReadOnlyList<SessionCardViewModel> cards)
    {
        foreach (var card in cards)
            card.IsExpanded = _expandedCards.Contains(card.SessionId);
        _lastCards = cards;
        if (!_sessions.Select(c => c.SessionId).SequenceEqual(cards.Select(c => c.SessionId)))
        {
            _sessions.Clear();
            foreach (var card in cards)
                _sessions.Add(card);
        }
        else
        {
            for (var i = 0; i < cards.Count; i++)
                if (_sessions[i].Signature != cards[i].Signature)
                    _sessions[i] = cards[i];
        }

        var days = _sessionDays;
        RangeTitleText.Text = SessionRange.Title(days);
        SessionsSummaryText.Text = WidgetViewModel.SessionsSummary(cards, days);
        NoSessionsText.Text = SessionRange.EmptyText(days);
        NoSessionsText.Visibility = cards.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Card ids whose details are open; kept across refreshes, which replace the cards.</summary>
    private readonly HashSet<string> _expandedCards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A card's Details / Less toggle (the template lives in Theme.xaml,
    /// so its click is caught here as it bubbles up the list).</summary>
    private void CardDetailsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Button { Tag: "share", DataContext: SessionCardViewModel { Session: { } session } } share)
        {
            OpenShareMenu(share, () => Task.FromResult<ShareCardModel?>(ShareCards.ForSession(session, _settings, DateTimeOffset.UtcNow)));
            return;
        }
        if (e.OriginalSource is not System.Windows.Controls.Button { Tag: "details", DataContext: SessionCardViewModel card })
            return;
        card.IsExpanded = !card.IsExpanded;
        if (card.IsExpanded)
            _expandedCards.Add(card.SessionId);
        else
            _expandedCards.Remove(card.SessionId);
    }

    // -------------------------------------------------------------- placement

    /// <summary>Compact while the pill's middle is inside the taskbar (its
    /// default spot, where room is tight), full once it's dragged out.</summary>
    private void UpdateCompactForPosition()
    {
        var inTaskbar = false;
        if (TaskbarInterop.GetTaskbarRect() is { } tb)
        {
            var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var topLeft = toDips.Transform(new Point(tb.Left, tb.Top));
            var bottomRight = toDips.Transform(new Point(tb.Right, tb.Bottom));
            var middleY = _pillBottom - PillHeight / 2;
            inTaskbar = middleY >= topLeft.Y && middleY <= bottomRight.Y &&
                        _pillRight > topLeft.X && _pillRight <= bottomRight.X + 1;
        }
        if (inTaskbar != _compact)
            SetCompact(inTaskbar);
    }

    /// <summary>
    /// Positions the window, the pill and (when open) the flyout inside it.
    /// Collapsed: the pill sits at its pinned corner. Open: FlyoutPlacement
    /// picks the side and keeps both on screen; the flyout's height is capped
    /// to the room on that side (its sessions list scrolls). Before the window
    /// is shown (the screenshot tests) there's no screen to fit, so the flyout
    /// simply opens above the pill.
    /// </summary>
    private void ApplyLayout()
    {
        if (_inLayout)
            return;
        _inLayout = true;
        try
        {
            var pillWidth = RootBorder.ActualWidth > 0 ? RootBorder.ActualWidth : MeasuredWidth(RootBorder);
            var pill = new Box(_pillRight - pillWidth, _pillBottom - PillHeight, pillWidth, PillHeight);

            var unbounded = new Box(-1e6, -1e6, 2e6, 2e6);
            var screen = IsLoaded
                ? new Box(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                    SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight)
                : unbounded;
            // The work area of the monitor the pill is on (the primary one's was used before).
            var work = IsLoaded ? MonitorWorkArea() ?? ToBox(SystemParameters.WorkArea) : unbounded;

            Box? flyout = null;
            if (_isExpanded)
            {
                var layout = FlyoutPlacement.Place(pill, FlyoutWidth, NaturalFlyoutHeight(), screen, work,
                    preferAbove: true, gap: FlyoutGap, forceAbove: _opensAbove,
                    maxHeightCap: IsLoaded ? work.Height * MaxFlyoutScreenShare : null);
                _opensAbove = layout.OpensAbove;
                pill = layout.Pill;
                flyout = layout.Flyout;
                if (Math.Abs(ExpandedPanel.MaxHeight - layout.MaxFlyoutHeight) > 0.5)
                    ExpandedPanel.MaxHeight = layout.MaxFlyoutHeight;
            }

            // Collapsed, the arrow points where the flyout will open; open, the
            // way it will close.
            var opensAbove = _isExpanded && _opensAbove is { } above
                ? above
                : FlyoutPlacement.WouldOpenAbove(pill, TypicalFlyoutHeight, work, preferAbove: true, FlyoutGap);
            var pointsUp = _isExpanded ? !opensAbove : opensAbove;
            ExpandButton.Content = pointsUp ? ChevronUpGlyph : ChevronDownGlyph;

            var minX = Math.Min(pill.X, flyout?.X ?? pill.X);
            var minY = Math.Min(pill.Y, flyout?.Y ?? pill.Y);
            RootBorder.Margin = new Thickness(pill.X - minX, pill.Y - minY, 0, 0);
            if (flyout is { } f)
                ExpandedPanel.Margin = new Thickness(f.X - minX, f.Y - minY, 0, 0);

            Left = minX - OuterMargin;
            Top = minY - OuterMargin;
        }
        finally
        {
            _inLayout = false;
        }
    }

    private static double MeasuredWidth(FrameworkElement element)
    {
        element.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return element.DesiredSize.Width;
    }

    /// <summary>The flyout's full height if nothing capped it (its sessions
    /// list has its own 520 limit).</summary>
    private double NaturalFlyoutHeight()
    {
        if (ExpandedPanel.Child is not FrameworkElement content)
            return TypicalFlyoutHeight;
        content.Measure(new Size(FlyoutWidth - 2, double.PositiveInfinity));
        return content.DesiredSize.Height + 2; // + border
    }

    private static Box ToBox(Rect r) => new(r.X, r.Y, r.Width, r.Height);

    /// <summary>The work area of the monitor this window is on, in its DIPs; null before it has a handle.</summary>
    private Box? MonitorWorkArea()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero || TaskbarDock.MonitorOf(handle) is not { } monitor)
            return null;
        var toDips = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var a = toDips.Transform(new Point(monitor.Work.Left, monitor.Work.Top));
        var b = toDips.Transform(new Point(monitor.Work.Right, monitor.Work.Bottom));
        return new Box(a.X, a.Y, b.X - a.X, b.Y - a.Y);
    }

    /// <summary>Whether the visible pill is within the virtual screen (all monitors).</summary>
    private bool IsMostlyOnScreen() =>
        WidgetPosition.IsMostlyOnScreen(_pillRight, _pillBottom,
            new Box(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight), PillHeight);
}
