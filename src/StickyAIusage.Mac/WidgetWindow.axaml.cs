using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using StickyAIusage.Mac.Controls;
using StickyAIusage.Mac.Native;
using StickyAIusage.Forecasting;
using StickyAIusage.Mac.Services;
using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.Sharing;
using StickyAIusage.Stats;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac;

/// <summary>
/// The macOS widget: the same pill and flyout as the Windows app, as a
/// floating always-on-top window. On first launch it sits in the menu bar
/// strip just left of the status icons (vertically centered in the bar); it
/// can be dragged anywhere and remembers where it was left.
///
/// The flyout opens wherever there's room - below the pill by default, above
/// it near the bottom of the screen - and shifts sideways so that it and the
/// pill are always fully on screen (FlyoutPlacement). The expand arrow points
/// the way the flyout will open, and the way it will close once open.
/// </summary>
public partial class WidgetWindow : Window
{
    // Must match RootGrid's side margin in the XAML (shadow room).
    private const double SideMargin = 12;
    private const double DragThreshold = 4;
    private const double GapBeforeStatusItems = 6;
    private const double FlyoutGap = 8;
    private const double FlyoutWidth = 500;
    // Used to point the collapsed arrow before the flyout's real size is known.
    private const double TypicalFlyoutHeight = 520;

    private readonly AppSettings _settings;
    private readonly bool _live;
    private readonly AppServicesHost _services;
    private UsageFeed Feed => _services.Current.Feed;
    private readonly DispatcherTimer _pollTimer;
    private readonly ObservableCollection<SessionCardViewModel> _sessions = new();

    // Where the collapsed pill's top-right corner is pinned, in screen pixels
    // (Avalonia's Position units). While the flyout is open the pill may be
    // pulled onto the screen; it returns here when the flyout closes.
    private double _pillRight;
    private double _pillTop;
    private bool _positioned;
    private bool _inLayout;

    private bool _isExpanded;
    private bool? _opensAbove;
    private bool _isRunning;
    private readonly RefreshGate _refreshGate = new();
    private bool _sessionsRefreshInFlight;
    private WidgetViewModel? _lastView;
    private IReadOnlyList<SessionCardViewModel> _lastCards = Array.Empty<SessionCardViewModel>();

    /// <summary>Days of sessions the flyout lists (SessionRange); starts on today every launch.</summary>
    private int _sessionDays = 1;

    // Compact while sitting in the menu bar strip, where space between the
    // notch and the status icons is tight: no buttons, credits, update time,
    // or "Session:" label.
    private bool _compact;

    // How much further the compact pill was squeezed to fit next to the
    // notch: 0 = as is, 1 = without the ring, 2 = also smaller text and
    // spacing. Undone once the pill leaves the menu bar.
    private int _menuBarSqueeze;
    private const int MaxMenuBarSqueeze = 2;

    private PixelPoint? _dragStart;
    private bool _didDrag;

    public event EventHandler? SettingsRequested;
    public event EventHandler? QuitRequested;

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

    /// <summary>The first usage reading; the App waits for it (briefly) before
    /// showing the window, so the pill opens in the right layout.</summary>
    public Task FirstRefresh { get; }

    public WidgetWindow() : this(new AppSettings(), live: false) { } // designer

    /// <param name="live">False for the designer and screenshot tests: no
    /// polling, no network, no transcripts, no native window changes.</param>
    public WidgetWindow(AppSettings settings, bool live = true)
    {
        _settings = settings;
        _live = live;
        _isRunning = settings.IsRunning;
        _services = new AppServicesHost(settings);
        Closed += (_, _) => _services.Dispose();
        InitializeComponent();
        SessionsList.ItemsSource = _sessions;
        SessionsList.AddHandler(Button.ClickEvent, CardDetailsToggle_Click);
        BuildRangeTabs();

        PillBorder.PointerPressed += Pill_PointerPressed;
        PillBorder.PointerMoved += Pill_PointerMoved;
        PillBorder.PointerReleased += Pill_PointerReleased;
        PillBorder.SizeChanged += (_, _) => ApplyLayout();
        ExpandedPanel.SizeChanged += (_, _) => ApplyLayout();

        ApplyTheme();
        ThemeService.Changed += ApplyTheme;
        Closed += (_, _) => ThemeService.Changed -= ApplyTheme;

        // Clicking anywhere else closes the flyout, the way a menu does.
        Deactivated += (_, _) =>
        {
            if (_isExpanded)
                DiagnosticLog.WriteInfo("Flyout closed: clicked outside the widget.");
            Collapse();
        };

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        ApplyRunningState();
        if (!live)
        {
            FirstRefresh = Task.CompletedTask;
            return;
        }

        Opened += (_, _) =>
        {
            MacNative.FloatAboveMenuBar(this);
            FitPillToMenuBar();
            PlaceInitially();
            ApplyRunningState();
        };

        _pollTimer.Tick += async (_, _) => { if (_isRunning) await RefreshAsync(); };
        _pollTimer.Start();
        // The Claude Code plugin's feed says when it wrote: refresh at once.
        _services.UsageChanged += OnUsageChanged;
        Closed += (_, _) =>
        {
            _services.UsageChanged -= OnUsageChanged;
            _attentionTimer?.Stop();
        };
        FirstRefresh = RefreshAsync();
    }

    /// <summary>Raised on a thread-pool thread; the refresh runs on the UI thread.</summary>
    private void OnUsageChanged() =>
        Dispatcher.UIThread.Post(async () =>
        {
            // A refresh already under way may have read the folder before this
            // write (an endpoint request can take seconds): the gate runs one
            // more after it.
            if (_isRunning && _refreshGate.RequestRefresh())
                await RefreshAsync();
        });

    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _opensAbove = null; // decide afresh from where the pill is now
        ExpandedPanel.IsVisible = _isExpanded;
        ToolTip.SetTip(ExpandButton, _isExpanded ? "Hide sessions" : "Show sessions");
        // The pill's tooltips repeat what the open panel shows, and one drawn over the panel hid its
        // header buttons (seen on a Mac): none while the panel is open.
        ToolTip.SetServiceEnabled(PillBorder, !_isExpanded);
        ApplyLayout();
        if (_isExpanded && _live)
        {
            // Come to the front, so that clicking anywhere else deactivates
            // the app and closes the flyout (Deactivated, in the constructor).
            MacNative.ActivateApp();
            Activate();
            _ = RefreshSessionsAsync(logCount: true);
        }
    }

    /// <summary>Closes the flyout if it's open - done before showing
    /// Settings, so the flyout (always on top) never covers it.</summary>
    public void Collapse()
    {
        if (_isExpanded)
            ToggleExpanded();
    }

    /// <summary>A second launch of this profile: open the flyout (which also
    /// brings the app to the front). Ignored until the widget has been shown.</summary>
    public void ShowAndExpand()
    {
        if (!IsVisible)
            return;
        if (!_isExpanded)
            ToggleExpanded();
    }

    public void RefreshFromSettings()
    {
        // A new usage source is built at the start of the next refresh (no fetch runs then).
        _services.SettingsChanged();
        Feed.Layout = _settings.CardLayout();
        Feed.ShowSessionLights = _settings.ShowSessionLights;
        if (_live)
            _ = RefreshAsync();
    }

    /// <summary>Forgets any dragged position and goes back to the menu bar.</summary>
    public void ResetPosition()
    {
        _settings.MacWidgetRight = null;
        _settings.MacWidgetTop = null;
        SettingsService.Save(_settings);
        PlaceInMenuBar();
    }

    // ------------------------------------------------------------- placement

    private Screen? MenuBarScreen => Screens.Primary ?? Screens.All.FirstOrDefault();

    /// <summary>The menu bar's height in device-independent units (the gap
    /// between the screen's top and its working area). 24 when it's hidden.</summary>
    private double MenuBarHeight
    {
        get
        {
            if (MenuBarScreen is not { } screen)
                return 24;
            var px = screen.WorkingArea.Y - screen.Bounds.Y;
            return px > 0 ? px / screen.Scaling : 24;
        }
    }

    /// <summary>A pill slightly shorter than the menu bar, so it reads as part of it.</summary>
    private void FitPillToMenuBar()
    {
        var height = Math.Clamp(MenuBarHeight - 4, 20, 28);
        PillBorder.Height = height;
        PillBorder.CornerRadius = new CornerRadius(height / 2);
    }

    private void PlaceInitially()
    {
        if (_settings.MacWidgetRight is { } right && _settings.MacWidgetTop is { } top && IsOnScreen(right, top))
        {
            _pillRight = right;
            _pillTop = top;
            _positioned = true;
            UpdateCompactForPosition();
            ApplyLayout();
            return;
        }
        PlaceInMenuBar();
    }

    /// <summary>
    /// Default spot: in the menu bar strip, just left of the leftmost status
    /// icon, in the compact form. If even that doesn't fit - it would run into
    /// a MacBook's camera notch, or the status icons can't be found - it goes
    /// just below that spot instead, in the full form.
    /// </summary>
    private void PlaceInMenuBar()
    {
        if (MenuBarScreen is not { } screen)
            return;

        var display = MacNative.MainDisplayBounds();
        // Avalonia positions are in pixels; CoreGraphics reports points.
        var pxPerPoint = display.Width > 0 ? screen.Bounds.Width / display.Width : screen.Scaling;
        var menuBar = MenuBarHeight;

        var statusLeft = MacNative.StatusItemsLeftEdge(menuBar);
        var notchRight = MacNative.NotchRightEdge();

        var pillRightPt = (statusLeft ?? display.Right) - GapBeforeStatusItems;
        bool Fits(double width) => statusLeft is not null && (notchRight is not { } nr || pillRightPt - width >= nr + 2);

        // Compact first; if that's still too wide, squeeze it a step at a time.
        _menuBarSqueeze = 0;
        SetCompact(true);
        var compactWidth = MeasuredPillSize().Width;
        var widths = $"{compactWidth:0}";
        while (!Fits(compactWidth) && statusLeft is not null && _menuBarSqueeze < MaxMenuBarSqueeze)
        {
            _menuBarSqueeze++;
            SetCompact(true);
            compactWidth = MeasuredPillSize().Width;
            widths += $"/{compactWidth:0}";
        }
        var fits = Fits(compactWidth);

        double pillTopPt;
        if (fits)
        {
            pillTopPt = (menuBar - PillBorder.Height) / 2;
        }
        else
        {
            _menuBarSqueeze = 0;
            SetCompact(false);
            pillTopPt = menuBar + 6;
        }

        DiagnosticLog.WriteInfo(
            $"Placement: screen={screen.Bounds} scale={screen.Scaling} display(pt)={display} menuBar={menuBar:0.#} " +
            $"statusLeft={statusLeft?.ToString("0") ?? "none"} notchRight={notchRight?.ToString("0") ?? "none"} " +
            $"compactPill={widths} room={(statusLeft is { } l && notchRight is { } n ? (l - GapBeforeStatusItems - n - 2).ToString("0") : "n/a")} " +
            $"-> {(fits ? "in the menu bar" : "below the menu bar")}");

        _pillRight = screen.Bounds.X + pillRightPt * pxPerPoint;
        _pillTop = screen.Bounds.Y + pillTopPt * pxPerPoint;
        _positioned = true;
        ApplyLayout();
    }

    private Size MeasuredPillSize()
    {
        // Invalidate every child too: a direct Measure reuses the cached sizes
        // of children whose own measure is still valid, so a ring just hidden
        // or a font just shrunk wouldn't count yet.
        PillBorder.InvalidateMeasure();
        foreach (var child in PillBorder.GetVisualDescendants().OfType<Layoutable>())
            child.InvalidateMeasure();
        PillBorder.Measure(Size.Infinity);
        return PillBorder.DesiredSize;
    }

    /// <summary>Test hook: the compact pill at a squeeze step, and its width.</summary>
    internal double SqueezeForMenuBar(int step)
    {
        _menuBarSqueeze = Math.Clamp(step, 0, MaxMenuBarSqueeze);
        SetCompact(true);
        return MeasuredPillSize().Width;
    }

    internal void SetCompact(bool compact)
    {
        _compact = compact;
        RunToggleButton.IsVisible = ExpandButton.IsVisible = !compact;
        SessionLabel.IsVisible = !compact;
        DayRing.Width = DayRing.Height = compact ? 13 : 14;
        PillBorder.Padding = compact ? new Thickness(6, 0, 8, 0) : new Thickness(9, 0, 2, 0);
        UsageRing.Margin = new Thickness(0, 0, compact ? 5 : 7, 0);
        UsageRing.Width = UsageRing.Height = compact ? 13 : 14;
        var tight = compact && _menuBarSqueeze >= 2;
        PrimaryText.FontSize = SessionCostText.FontSize = WeeklyText.FontSize = DayAmountText.FontSize =
            !compact ? 12.5 : tight ? 11 : 11.5;
        SecondaryText.FontSize = ForecastText.FontSize = CreditsText.FontSize = DayPercentText.FontSize = !compact ? 12 : tight ? 10.5 : 11;
        SecondaryText.Margin = ForecastText.Margin = StaleText.Margin = new Thickness(!compact ? 6 : tight ? 3 : 4, 0, 0, 0);
        foreach (var divider in new[] { WeeklyDivider, CreditsDivider, UpdatedDivider, DayDivider, PillDivider })
            divider.Margin = new Thickness(!compact ? 9 : tight ? 4 : 6, 0);
        if (tight)
            PillBorder.Padding = new Thickness(5, 0, 6, 0);
        if (_lastView is not null)
            ShowPill(_lastView);
    }

    /// <summary>Compact while the pill is inside the menu bar strip, full
    /// elsewhere. Dropped into the menu bar, it's squeezed as far as needed to
    /// clear the camera notch.</summary>
    private void UpdateCompactForPosition()
    {
        if (MenuBarScreen is not { } screen)
            return;
        var inMenuBar = (_pillTop - screen.Bounds.Y) / DesktopScaling < MenuBarHeight - 2;
        if (inMenuBar == _compact)
            return;

        _menuBarSqueeze = 0;
        SetCompact(inMenuBar);
        if (!inMenuBar || MacNative.NotchRightEdge() is not { } notchRight)
            return;

        var display = MacNative.MainDisplayBounds();
        var pxPerPoint = display.Width > 0 ? screen.Bounds.Width / display.Width : screen.Scaling;
        var scale = DesktopScaling > 0 ? DesktopScaling : 1;
        if ((_pillRight - screen.Bounds.X) / pxPerPoint <= notchRight)
            return; // left of the notch, among the app menus
        bool ClearsNotch() => (_pillRight - MeasuredPillSize().Width * scale - screen.Bounds.X) / pxPerPoint >= notchRight + 2;
        while (!ClearsNotch() && _menuBarSqueeze < MaxMenuBarSqueeze)
        {
            _menuBarSqueeze++;
            SetCompact(true);
        }
    }

    /// <summary>
    /// Positions the window, the pill and (when open) the flyout inside it.
    /// Collapsed: the pill sits at its pinned spot. Open: FlyoutPlacement picks
    /// the side and keeps both on screen; the flyout's height is capped to the
    /// room on that side (its sessions list scrolls).
    /// </summary>
    private void ApplyLayout()
    {
        if (_inLayout)
            return;
        _inLayout = true;
        try
        {
            var scale = DesktopScaling > 0 ? DesktopScaling : 1;
            var pillSize = PillBorder.Bounds.Width > 0 ? PillBorder.Bounds.Size : MeasuredPillSize();
            var pill = new Box(_pillRight - pillSize.Width * scale, _pillTop, pillSize.Width * scale, pillSize.Height * scale);

            var screen = ScreenFor(pill);
            var screenBox = screen is null ? new Box(-1e6, -1e6, 2e6, 2e6) : ToBox(screen.Bounds);
            var workBox = screen is null ? screenBox : ToBox(screen.WorkingArea);

            Box? flyout = null;
            if (_isExpanded)
            {
                var natural = NaturalFlyoutHeight() * scale;
                var layout = FlyoutPlacement.Place(pill, FlyoutWidth * scale, natural, screenBox, workBox,
                    preferAbove: false, gap: FlyoutGap * scale, forceAbove: _opensAbove);
                _opensAbove = layout.OpensAbove;
                pill = layout.Pill;
                flyout = layout.Flyout;
                var maxHeight = layout.MaxFlyoutHeight / scale;
                if (Math.Abs(ExpandedPanel.MaxHeight - maxHeight) > 0.5)
                    ExpandedPanel.MaxHeight = maxHeight;
            }

            UpdateExpandIcon(pill, workBox, scale);

            var minX = Math.Min(pill.X, flyout?.X ?? pill.X);
            var minY = Math.Min(pill.Y, flyout?.Y ?? pill.Y);
            PillBorder.Margin = new Thickness((pill.X - minX) / scale, (pill.Y - minY) / scale, 0, 0);
            if (flyout is { } f)
                ExpandedPanel.Margin = new Thickness((f.X - minX) / scale, (f.Y - minY) / scale, 0, 0);

            if (_positioned)
                Position = new PixelPoint((int)Math.Round(minX - SideMargin * scale), (int)Math.Round(minY));
        }
        finally
        {
            _inLayout = false;
        }
    }

    /// <summary>The flyout's full height if nothing capped it (its sessions
    /// list has its own 560 limit).</summary>
    private double NaturalFlyoutHeight()
    {
        if (ExpandedPanel.Child is not Control content)
            return TypicalFlyoutHeight;
        content.Measure(new Size(FlyoutWidth - 2, double.PositiveInfinity));
        return content.DesiredSize.Height + 2; // + border
    }

    /// <summary>Collapsed, the arrow points where the flyout will open;
    /// open, it points the way it will close.</summary>
    private void UpdateExpandIcon(Box pill, Box workArea, double scale)
    {
        var opensAbove = _isExpanded && _opensAbove is { } above
            ? above
            : FlyoutPlacement.WouldOpenAbove(pill, TypicalFlyoutHeight * scale, workArea, preferAbove: false, FlyoutGap * scale);
        var pointsUp = _isExpanded ? !opensAbove : opensAbove;
        ExpandIcon.Data = ThemeService.Resource<Geometry>(pointsUp ? "ChevronUpIcon" : "ChevronDownIcon");
    }

    private Screen? ScreenFor(Box pill) =>
        Screens.ScreenFromPoint(new PixelPoint((int)(pill.X + pill.Width / 2), (int)(pill.Y + pill.Height / 2)))
        ?? MenuBarScreen;

    private static Box ToBox(PixelRect r) => new(r.X, r.Y, r.Width, r.Height);

    private bool IsOnScreen(double right, double top) =>
        Screens.All.Any(s => right - 40 > s.Bounds.X && right - 40 < s.Bounds.Right &&
                             top >= s.Bounds.Y - 1 && top + 20 < s.Bounds.Bottom);

    // ----------------------------------------------------------------- input

    private void Pill_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(PillBorder).Properties.IsLeftButtonPressed)
            return;
        _dragStart = this.PointToScreen(e.GetPosition(this));
        _didDrag = false;
        e.Pointer.Capture(PillBorder);
    }

    private void Pill_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start)
            return;

        var current = this.PointToScreen(e.GetPosition(this));
        var dx = current.X - start.X;
        var dy = current.Y - start.Y;
        if (!_didDrag && Math.Sqrt(dx * dx + dy * dy) < DragThreshold * DesktopScaling)
            return;

        _didDrag = true;
        _pillRight += dx;
        _pillTop += dy;
        _dragStart = current;
        _opensAbove = null; // moved: pick the side again
        ApplyLayout();
    }

    private void Pill_PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is null)
            return;
        _dragStart = null;
        e.Pointer.Capture(null);

        if (_didDrag)
        {
            UpdateCompactForPosition();
            _settings.MacWidgetRight = _pillRight;
            _settings.MacWidgetTop = _pillTop;
            SettingsService.Save(_settings);
            return;
        }
        ToggleExpanded();
    }

    private void ExpandButton_Click(object? sender, RoutedEventArgs e) => ToggleExpanded();

    /// <summary>Fills the Today | 2d | 1w | 2w | 30d tabs under the header icons.</summary>
    private void BuildRangeTabs()
    {
        foreach (var days in SessionRange.Options)
        {
            var tab = new RadioButton
            {
                Content = SessionRange.Tab(days),
                GroupName = "SessionRange",
                IsChecked = days == _sessionDays,
                Classes = { "segment" }
            };
            tab.IsCheckedChanged += (_, _) =>
            {
                if (tab.IsChecked != true)
                    return;
                _sessionDays = days;
                RangeTitleText.Text = SessionRange.Title(days);
                SessionsSummaryText.Text = "Loading…";
                _ = RefreshSessionsAsync(logCount: true);
            };
            RangeTabs.Children.Add(tab);
        }
    }

    private void SettingsButton_Click(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);

    // ---------------------------------------------------------------- share

    private IShareOutput? _shareOutput;

    /// <summary>Copies, saves or posts cards; a test can swap it for a fake.</summary>
    internal IShareOutput ShareOutput
    {
        get => _shareOutput ??= new MacShareService(() => this);
        set => _shareOutput = value;
    }

    private DispatcherTimer? _shareStatusTimer;
    private MenuFlyout? _shareMenu;

    /// <summary>Test hook: the Share menu's items, as last opened.</summary>
    internal IReadOnlyList<string> ShareMenuItems =>
        _shareMenu?.Items.OfType<MenuItem>().Select(i => (string)i.Header!).ToList() ?? [];

    /// <summary>Test hook: whether each of the Share menu's items can be chosen, as last opened.</summary>
    internal IReadOnlyList<bool> ShareMenuItemsEnabled =>
        _shareMenu?.Items.OfType<MenuItem>().Select(i => i.IsEnabled).ToList() ?? [];

    /// <summary>Test hook: the year submenu's items, as last opened (empty outside the season).</summary>
    internal IReadOnlyList<string> ShareYearMenuItems =>
        _shareMenu?.Items.OfType<MenuItem>().LastOrDefault(i => i.Items.Count > 0)?.Items.OfType<MenuItem>()
            .Select(i => (string)i.Header!).ToList() ?? [];

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
    private void ShareButton_Click(object? sender, RoutedEventArgs e) =>
        OpenShareMenu(ShareButton, TodayCardAsync, _hasReading, YearCard.SeasonYear(ShareClock()));

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

    internal async Task<ShareCardModel?> TodayCardAsync()
    {
        if (_lastReading is not { } reading)
            return null;
        var today = await Feed.GetTodaySessionsAsync();
        return ShareCards.ForUsage(reading.Usage, reading.Forecasts, today, reading.SpentToday, _settings, DateTimeOffset.UtcNow);
    }

    /// <summary>Copy image, Save image…, Post on X / LinkedIn / Bluesky, under the button;
    /// with a <paramref name="year"/>, the same for the year card in a submenu.</summary>
    private void OpenShareMenu(Control anchor, Func<Task<ShareCardModel?>> card, bool cardReady = true, int? year = null)
    {
        // One menu, its items made afresh each time, as on Windows.
        var menu = _shareMenu ??= new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        menu.Hide();
        menu.Items.Clear();
        AddShareItems(menu.Items, action => ShareAsync(action, card), cardReady);
        if (year is { } y)
        {
            menu.Items.Add(new Separator());
            var yearMenu = new MenuItem { Header = YearCard.Title(y) };
            AddShareItems(yearMenu.Items, action => ShareAsync(action, () => YearCardAsync(y), YearCard.NothingYet(y)), enabled: true);
            menu.Items.Add(yearMenu);
            // Start reading the year now: it is usually done by the time a choice is made.
            _ = YearStats.GetAsync(y);
        }
        menu.ShowAt(anchor);
    }

    private static void AddShareItems(ItemCollection items, Func<ShareAction, Task> run, bool enabled)
    {
        foreach (var action in ShareFlow.Actions)
        {
            if (action == ShareAction.PostX)
                items.Add(new Separator());
            var item = new MenuItem { Header = ShareFlow.MenuText(action), IsEnabled = enabled };
            if (!enabled)
            {
                ToolTip.SetShowOnDisabled(item, true);
                ToolTip.SetTip(item, ShareFlow.NoReadingTooltip);
            }
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
            var png = ShareCardView.Png(model, ThemeService.IsDark);
            DiagnosticLog.WriteInfo($"Share: {action} of a {model.Variant} card ({model.Shape}).");
            ShowShareStatus(await ShareFlow.RunAsync(action, png, model.Variant, ShareOutput, DateTimeOffset.UtcNow, year: model.Year));
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("WidgetWindow.ShareAsync", ex);
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
        ShareStatusText.IsVisible = true;
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
        ShareStatusText.IsVisible = false;
    }

    /// <summary>The health line's one way forward.</summary>
    private void HealthButton_Click(object? sender, RoutedEventArgs e)
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

    private void ThemeToggleButton_Click(object? sender, RoutedEventArgs e) => ThemeService.Toggle();

    /// <summary>
    /// Repaints what the theme dictionaries can't: the brushes and icons this
    /// code sets by hand, and the session cards, whose colors come from
    /// converters that only re-run when their item changes.
    /// </summary>
    private void ApplyTheme()
    {
        var dark = ThemeService.IsDark;
        ThemeToggleIcon.Data = ThemeService.Resource<Geometry>(dark ? "SunIcon" : "MoonIcon");
        ToolTip.SetTip(ThemeToggleButton, dark ? "Switch to the light theme" : "Switch to the dark theme");

        ApplyRunningState();
        if (_lastView is not null)
            ShowPill(_lastView);
        if (_sessions.Count > 0)
        {
            _sessions.Clear();
            foreach (var card in _lastCards)
                _sessions.Add(card);
        }
    }
    private void SettingsMenu_Click(object? sender, RoutedEventArgs e) => SettingsRequested?.Invoke(this, EventArgs.Empty);
    private void ResetMenu_Click(object? sender, RoutedEventArgs e) => ResetPosition();
    private void QuitMenu_Click(object? sender, RoutedEventArgs e) => QuitRequested?.Invoke(this, EventArgs.Empty);

    private void RunToggleButton_Click(object? sender, RoutedEventArgs e)
    {
        _isRunning = !_isRunning;
        ApplyRunningState();
        _settings.IsRunning = _isRunning;
        SettingsService.Save(_settings);
        if (_isRunning)
        {
            DiagnosticLog.WriteInfo("Resumed from paused state.");
            Feed.ForgetLoggedSession();
            if (_live)
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

    /// <summary>Paused: dimmed pill and a play button.</summary>
    private void ApplyRunningState()
    {
        PillBorder.Background = ThemeService.Resource<IBrush>(_isRunning ? "StripBackground" : "StripBackgroundStopped");
        UsageGroup.Opacity = DayGroup.Opacity = SessionGroup.Opacity = _isRunning ? 1 : 0.5;
        var icon = ThemeService.Resource<Geometry>(_isRunning ? "PauseIcon" : "PlayIcon");
        var label = _isRunning ? "Pause live updates" : "Resume live updates";
        RunToggleIcon.Data = FlyoutRunToggleIcon.Data = icon;
        ToolTip.SetTip(RunToggleButton, label);
        ToolTip.SetTip(FlyoutRunToggleButton, label);
        PauseMenuItem.Header = label;
    }

    // --------------------------------------------------------------- refresh

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
            DiagnosticLog.WriteError("WidgetWindow.RefreshAsync", ex);
        }

        if (_refreshGate.Finish())
        {
            await RefreshAsync();
            return;
        }

        if (_isExpanded)
            await RefreshSessionsAsync(logCount: false);
    }

    /// <summary>Raised on the UI thread with a refresh's notifications; the App shows them.</summary>
    public event Action<IReadOnlyList<Alerts.Alert>>? AlertsRaised;

    /// <summary>Hands the alerts on and pulses the pill twice (not with Reduce motion).</summary>
    internal void RaiseAlerts(IReadOnlyList<Alerts.Alert> alerts)
    {
        if (alerts.Count == 0)
            return;
        AlertsRaised?.Invoke(alerts);
        Motion.Pulse(PillBorder);
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
        _lastView = vm;
        ShowPill(vm);

        // Nothing to put on today's card without a reading (a session card still works);
        // in December and January the year card needs none.
        _hasReading = vm.Mode != UsageMode.Unknown;
        var yearInSeason = YearCard.SeasonYear(ShareClock()) is not null;
        ShareButton.IsEnabled = _hasReading || yearInSeason;
        ToolTip.SetTip(ShareButton, ShareFlow.HeaderTooltip(_hasReading, yearInSeason));
        SummaryTitleText.Text = vm.SummaryTitle;
        SummaryUpdatedText.Text = vm.SummaryUpdatedText ?? "";
        SummaryRowsList.ItemsSource = vm.SummaryRows;
        HealthPanel.IsVisible = vm.Health is not null;
        HealthText.Text = vm.Health?.Detail ?? "";
        HealthButton.Content = vm.Health?.ActionLabel;
        HealthButton.IsVisible = vm.Health?.ActionLabel is not null;
        SummaryCard.IsVisible = vm.SummaryRows.Count > 0 || vm.Health is not null;
        ProviderSectionsList.ItemsSource = vm.ProviderSections;
        ProviderSectionsList.IsVisible = vm.ProviderSections.Count > 0;
        _lastReading = (usage, session, spentToday, forecasts, others);
        ShowNotice(vm.Notice);
        UsageApplied?.Invoke();
    }

    private (UsageSnapshot Usage, SessionInfo? Session, decimal? SpentToday, IReadOnlyList<PaceForecast>? Forecasts,
        IReadOnlyList<ProviderReading>? Others)? _lastReading;
    private int _copyConfirmation;

    /// <summary>The note at the top of the flyout: the message and a button
    /// that copies its command; the plugin tip can be dismissed.</summary>
    private void ShowNotice(FlyoutNotice? notice)
    {
        NoticeBar.IsVisible = notice is not null;
        // A dismissed or resolved notice leaves no text behind in the hidden bar.
        if (notice is null)
        {
            NoticeText.Text = "";
            return;
        }
        NoticeText.Text = notice.Message;
        // Leave "Copied" up for its two seconds; the next refresh sets the label.
        if (_copyConfirmation == 0)
            CopyNoticeButton.Content = notice.CopyLabel;
        ToolTip.SetTip(CopyNoticeButton, "Copies: " + notice.CopyText);
        DismissNoticeButton.IsVisible = notice.CanDismiss;
        ConnectNoticeButton.Content = FlyoutNotice.ConnectLabel;
        ConnectNoticeButton.IsVisible = notice.CanConnect;
        // One primary button: beside Connect the copy button is the secondary way.
        CopyNoticeButton.Classes.Set("primary", !notice.CanConnect);
        CopyNoticeButton.Classes.Set("secondary", notice.CanConnect);
    }

    private async void CopyNoticeButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_lastView?.Notice is not { } notice)
            return;
        var confirmation = ++_copyConfirmation;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                throw new InvalidOperationException("No clipboard for this window.");
            await clipboard.SetTextAsync(notice.CopyText);
            CopyNoticeButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("WidgetWindow.CopyNoticeButton_Click", ex);
            CopyNoticeButton.Content = "Couldn't copy";
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (confirmation != _copyConfirmation)
            return; // clicked again meanwhile: that click restores the label
        _copyConfirmation = 0;
        CopyNoticeButton.Content = (_lastView?.Notice ?? notice).CopyLabel;
    }

    // ------------------------------------------------------------ updates

    private Updates.UpdateNotice? _update;
    private int _updateCopyConfirmation;

    /// <summary>A newer version, from the last refresh (Check for updates on); the menu bar menu shows it too.</summary>
    public Updates.UpdateNotice? LatestUpdate => _update;

    /// <summary>Test hook and refresh step: the flyout's update line, or none.</summary>
    internal void ApplyUpdate(Updates.UpdateNotice? notice)
    {
        if (Equals(notice, _update))
            return;
        _update = notice;
        UpdatePanel.IsVisible = notice is not null;
        if (notice is null)
            return;
        UpdateLinkButton.Content = notice.LinkText;
        UpdateHowText.Text = notice.HowText;
        UpdateCommandText.Text = notice.Command ?? "";
        UpdateCommandRow.IsVisible = notice.Command is not null;
        DiagnosticLog.WriteInfo($"Updates: the flyout shows version {notice.Version} ({notice.Method.ToString().ToLowerInvariant()}).");
    }

    private void UpdateLink_Click(object? sender, RoutedEventArgs e)
    {
        if (_update is { } notice)
            OpenReleasePage(notice);
    }

    private async void CopyUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_update?.Command is not { } command)
            return;
        var confirmation = ++_updateCopyConfirmation;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
                throw new InvalidOperationException("No clipboard for this window.");
            await clipboard.SetTextAsync(command);
            CopyUpdateButton.Content = "Copied";
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("WidgetWindow.CopyUpdate_Click", ex);
            CopyUpdateButton.Content = "Couldn't copy";
        }
        await Task.Delay(TimeSpan.FromSeconds(2));
        if (confirmation != _updateCopyConfirmation)
            return;
        _updateCopyConfirmation = 0;
        CopyUpdateButton.Content = "Copy";
    }

    /// <summary>The release page of a notice: always one of this project's (the
    /// checker builds the address from the tag; nothing from the network is opened).</summary>
    internal static void OpenReleasePage(Updates.UpdateNotice notice)
    {
        if (!notice.ReleaseUrl.StartsWith(Updates.UpdateChecker.ReleasePageBase, StringComparison.Ordinal))
            return;
        try
        {
            System.Diagnostics.Process.Start("/usr/bin/open", notice.ReleaseUrl);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("WidgetWindow.OpenReleasePage", ex);
        }
    }

    /// <summary>The plugin tip goes for good; the view is worked out again
    /// from the last reading, so a repaint never brings it back.</summary>
    private void DismissNoticeButton_Click(object? sender, RoutedEventArgs e)
    {
        _settings.PluginTipDismissed = true;
        SettingsService.Save(_settings);
        DiagnosticLog.WriteInfo("Plugin tip dismissed.");
        if (_lastReading is { } last)
            ApplyUsage(last.Usage, last.Session, last.SpentToday, last.Forecasts, last.Others);
    }

    /// <summary>Fills the pill. Monthly cap: "$96.75 48% │ Today: $4.20 42% │
    /// Session: $3.05".
    /// Plan: "5h: 43% - Resets: 2h 6m │ 1w: 14% │ Rem. Credits: $9.32 │
    /// Updated: just now" - the compact menu bar form keeps only the windows:
    /// "5h: 43% - 2h 6m │ 1w: 14%".</summary>
    private void ShowPill(WidgetViewModel vm)
    {
        UsageRing.IsVisible = vm.ShowRing && !(_compact && _menuBarSqueeze >= 1);
        UsageRing.Value = vm.RingPercent;
        UsageRing.Stroke = Palette.ForLevel(vm.Level);
        PrimaryText.Text = vm.PrimaryText;
        PrimaryText.Foreground = ThemeService.Resource<IBrush>(vm.State switch
        {
            UsageState.SignIn => "CautionBrush",
            UsageState.Unavailable => "TextTertiaryBrush",
            _ => "TextPrimaryBrush"
        });
        SetOptionalText(SecondaryText, _compact ? vm.SecondaryCompactText : vm.SecondaryText);
        ShowForecastChip(_compact ? vm.ForecastChipCompactText : vm.ForecastChipText, vm.ForecastLevel);
        ShowExtraChips(vm.ExtraChips);
        SetOptionalText(StaleText, vm.StaleText);
        // Squeezed for the menu bar, the chip stays and the weekly text goes first.
        SetOptionalText(WeeklyText, _compact && _menuBarSqueeze >= 1 && ForecastText.IsVisible ? null : vm.WeeklyText, WeeklyDivider);
        WeeklyText.Foreground = vm.WeeklyLevel is { } weeklyLevel ? Palette.ForLevel(weeklyLevel) : ThemeService.Resource<IBrush>("TextPrimaryBrush");
        SetOptionalText(CreditsText, _compact ? null : vm.CreditsText, CreditsDivider);
        SetOptionalText(UpdatedText, _compact ? null : vm.UpdatedText, UpdatedDivider);
        ToolTip.SetTip(UsageGroup, vm.UsageTooltip);
        UsageGroup.IsVisible = vm.ShowUsage;

        // Compact (menu bar): no "Today:" label, like "Session:"; squeezed, no ring.
        DayGroup.IsVisible = vm.ShowDayTarget;
        var ringOnly = vm.DayAmountText is null && vm.DayPercentText is null;
        DayRing.IsVisible = vm.ShowDayRing && !(_compact && _menuBarSqueeze >= 1 && !ringOnly);
        DayRing.Value = vm.DayRingPercent;
        DayRing.Stroke = Palette.ForLevel(vm.DayLevel);
        DayRing.Margin = new Thickness(0, 0, ringOnly ? 0 : _compact ? 5 : 7, 0);
        DayLabel.IsVisible = !_compact && !ringOnly;
        SetOptionalText(DayAmountText, vm.DayAmountText);
        SetOptionalText(DayPercentText, vm.DayPercentText);
        DayPercentText.Margin = new Thickness(vm.DayAmountText is null ? 0 : _compact ? 4 : 6, 0, 0, 0);
        ToolTip.SetTip(DayGroup, vm.DayTooltip);
        DayDivider.IsVisible = vm.ShowUsage && vm.ShowDayTarget;

        SessionGroup.IsVisible = vm.ShowSession;
        SessionCostText.Text = vm.SessionText;
        ToolTip.SetTip(SessionGroup, vm.SessionTooltip);
        PillDivider.IsVisible = vm.ShowSession && (vm.ShowUsage || vm.ShowDayTarget);
        ShowLights();
    }

    /// <summary>"Codex 91%" after the usage, each behind a divider, in its level's color.</summary>
    private void ShowExtraChips(IReadOnlyList<ProviderChip> chips)
    {
        ExtraChipsPanel.Children.Clear();
        foreach (var chip in chips)
        {
            var divider = new Avalonia.Controls.Shapes.Rectangle();
            divider.Classes.Add("pillDivider");
            divider.IsVisible = true;
            divider.Margin = WeeklyDivider.Margin;
            ExtraChipsPanel.Children.Add(divider);
            var text = new TextBlock
            {
                Text = chip.Text, FontSize = ForecastText.FontSize, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center,
                Foreground = Palette.ForLevel(chip.Level)
            };
            ToolTip.SetTip(text, chip.Tooltip);
            ExtraChipsPanel.Children.Add(text);
        }
        ExtraChipsPanel.IsVisible = chips.Count > 0;
    }

    /// <summary>Test hook: the texts of the pill's other-tool chips.</summary>
    internal IReadOnlyList<string> ExtraChipTexts => ExtraChipsPanel.Children.OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    /// <summary>The pace chip in its color; it fades in when it appears.</summary>
    private void ShowForecastChip(string? text, UsageLevel level)
    {
        var appearing = !string.IsNullOrEmpty(text) && !ForecastText.IsVisible;
        SetOptionalText(ForecastText, text);
        ForecastText.Foreground = Palette.ForLevel(level);
        if (appearing)
            Motion.FadeIn(ForecastText);
    }

    private static void SetOptionalText(TextBlock block, string? text, Control? divider = null)
    {
        block.Text = text ?? "";
        block.IsVisible = !string.IsNullOrEmpty(text);
        if (divider is not null)
            divider.IsVisible = block.IsVisible;
    }

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
            DiagnosticLog.WriteError("WidgetWindow.RefreshSessionsAsync", ex);
            NoSessionsText.Text = "Couldn't load sessions - see diagnostics.log (menu bar icon → Open log folder).";
            NoSessionsText.IsVisible = true;
        }
        finally
        {
            _sessionsRefreshInFlight = false;
        }
    }

    /// <summary>Updates the card list in place, so an open panel keeps its scroll position.</summary>
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
        NoSessionsText.IsVisible = cards.Count == 0;
    }

    // ------------------------------------------------------- session lights

    private IReadOnlyList<SessionActivity> _activities = [];
    private bool _hasPlugin;
    private DispatcherTimer? _attentionTimer;
    private readonly HashSet<string> _expandedCards = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Test hook and refresh step: what each Claude Code session is
    /// doing, and whether the plugin is there at all.</summary>
    internal void ApplyActivities(IReadOnlyList<SessionActivity> activities, bool hasPlugin)
    {
        _activities = activities;
        _hasPlugin = hasPlugin;
        ShowLights();
    }

    /// <summary>The dots after the usage text (one in the menu bar), their
    /// tooltip, and the flyout's hint when there is no plugin.</summary>
    private void ShowLights()
    {
        var lights = SessionLights.From(_activities, DateTimeOffset.UtcNow, _settings.ShowSessionLights);
        LightsDots.Children.Clear();
        IReadOnlyList<LightKind> dots = _compact ? (lights?.CompactDot is { } one ? [one] : []) : lights?.Dots ?? [];
        foreach (var kind in dots)
            LightsDots.Children.Add(Dot(kind));
        SetOptionalText(LightsMoreText, _compact ? null : lights?.MoreText);
        LightsGroup.IsVisible = dots.Count > 0;
        LightsGroup.Margin = new Thickness(_compact ? 5 : 8, 0, 0, 0);
        ToolTip.SetTip(LightsGroup, lights?.Tooltip);
        Avalonia.Automation.AutomationProperties.SetName(LightsGroup, lights is null ? "" : "Claude Code sessions: " + lights.Summary);

        LightsHintText.Text = SessionLights.ConnectHint;
        LightsHintText.IsVisible = _settings.ShowSessionLights && !_hasPlugin && _lastView?.Notice?.Kind != FlyoutNoticeKind.InstallPlugin;
        LightsConnectButton.Content = SessionLights.ConnectAction;
        LightsConnectButton.IsVisible = LightsHintText.IsVisible;
    }

    /// <summary>Raised by the session-lights hint's "Connect to Claude Code…".</summary>
    public event EventHandler? ConnectRequested;

    private void LightsConnect_Click(object? sender, RoutedEventArgs e) => ConnectRequested?.Invoke(this, EventArgs.Empty);

    private void ConnectNoticeButton_Click(object? sender, RoutedEventArgs e) => ConnectRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>One dot: filled amber (pulsing) for needs you, filled green
    /// (soft pulse) for working, red for rate limited, a grey ring otherwise.</summary>
    private static Control Dot(LightKind kind)
    {
        var filled = kind is LightKind.NeedsYou or LightKind.Working or LightKind.RateLimited;
        var brush = Palette.ForLight(kind);
        var dot = new Avalonia.Controls.Shapes.Ellipse
        {
            Width = 7,
            Height = 7,
            Margin = new Thickness(2, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Fill = filled ? brush : Brushes.Transparent,
            Stroke = filled ? null : brush,
            StrokeThickness = 1.4,
            Opacity = kind == LightKind.NoSignal ? 0.5 : 1,
            Tag = kind
        };
        if (kind is LightKind.NeedsYou or LightKind.Working)
            _ = Motion.Breathe(dot, strong: kind == LightKind.NeedsYou);
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
        if (_attentionTimer is null)
        {
            _attentionTimer = new DispatcherTimer();
            _attentionTimer.Tick += async (_, _) =>
            {
                _attentionTimer.Stop();
                if (_isRunning && _live)
                    await RefreshAsync();
            };
        }
        _attentionTimer.Interval = wait < TimeSpan.Zero ? TimeSpan.Zero : wait;
        _attentionTimer.Start();
    }

    /// <summary>A card's Details / Less toggle (the template lives in
    /// CardTemplates.axaml, so its click is caught here as it bubbles up).</summary>
    private void CardDetailsToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (e.Source is Button { Tag: "share", DataContext: SessionCardViewModel { Session: { } session } } share)
        {
            OpenShareMenu(share, () => Task.FromResult<ShareCardModel?>(ShareCards.ForSession(session, _settings, DateTimeOffset.UtcNow)));
            return;
        }
        if (e.Source is not Button { Tag: "details", DataContext: SessionCardViewModel card })
            return;
        card.IsExpanded = !card.IsExpanded;
        if (card.IsExpanded)
            _expandedCards.Add(card.SessionId);
        else
            _expandedCards.Remove(card.SessionId);
    }

    /// <summary>Test hook: pins the pill's top-right corner at a screen
    /// position (pixels), as if it had been dragged there.</summary>
    internal void PinPill(double right, double top)
    {
        _pillRight = right;
        _pillTop = top;
        _positioned = true;
        _opensAbove = null;
        ApplyLayout();
    }

    internal bool? OpensAbove => _isExpanded ? _opensAbove : null;
}
