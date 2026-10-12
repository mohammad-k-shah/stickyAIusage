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
using StickyAIusage.Mac.Services;
using StickyAIusage.Models;
using StickyAIusage.Services;
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
    private readonly UsageFeed _feed = new();
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
    private bool _refreshInFlight;
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
        InitializeComponent();
        SessionsList.ItemsSource = _sessions;
        BuildRangeTabs();
        _feed.Layout = settings.CardLayout();

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
        FirstRefresh = RefreshAsync();
    }

    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _opensAbove = null; // decide afresh from where the pill is now
        ExpandedPanel.IsVisible = _isExpanded;
        ToolTip.SetTip(ExpandButton, _isExpanded ? "Hide sessions" : "Show sessions");
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

    public void RefreshFromSettings()
    {
        _feed.Layout = _settings.CardLayout();
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
        SecondaryText.FontSize = CreditsText.FontSize = DayPercentText.FontSize = !compact ? 12 : tight ? 10.5 : 11;
        SecondaryText.Margin = StaleText.Margin = new Thickness(!compact ? 6 : tight ? 3 : 4, 0, 0, 0);
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
            _feed.ForgetLoggedSession();
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
        if (_refreshInFlight)
            return;
        _refreshInFlight = true;
        try
        {
            var (usage, session, spentToday) = await _feed.GetSummaryAsync();
            ApplyUsage(usage, session, spentToday);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("WidgetWindow.RefreshAsync", ex);
        }
        finally
        {
            _refreshInFlight = false;
        }

        if (_isExpanded)
            await RefreshSessionsAsync(logCount: false);
    }

    /// <summary>The plan layout of the latest reading; Settings uses it to
    /// turn off what doesn't apply (the day target on Pro/Max).</summary>
    public UsageMode CurrentMode => _lastView?.Mode ?? UsageMode.Unknown;

    internal void ApplyUsage(UsageSnapshot usage, SessionInfo? session, decimal? spentToday = null)
    {
        var vm = WidgetViewModel.From(usage, session, _settings, DateTimeOffset.UtcNow, spentToday: spentToday);
        _lastView = vm;
        ShowPill(vm);

        SummaryTitleText.Text = vm.SummaryTitle;
        SummaryUpdatedText.Text = vm.SummaryUpdatedText ?? "";
        SummaryRowsList.ItemsSource = vm.SummaryRows;
        SummaryCard.IsVisible = vm.SummaryRows.Count > 0;
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
        SetOptionalText(StaleText, vm.StaleText);
        SetOptionalText(WeeklyText, vm.WeeklyText, WeeklyDivider);
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
                cards = await _feed.GetCardsAsync(days);
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
