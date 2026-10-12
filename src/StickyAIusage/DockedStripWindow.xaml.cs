using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using StickyAIusage.Models;
using StickyAIusage.Native;
using StickyAIusage.Services;
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
    private const double PillHeight = 32;
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
    private readonly UsageFeed _feed = new();
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
    private bool _refreshInFlight;
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

    /// <summary>The first usage reading. The App waits for it (briefly) before
    /// showing the window, so the pill appears already in the right layout -
    /// monthly spend or plan limits - instead of flashing a placeholder.</summary>
    public Task FirstRefresh { get; }

    public DockedStripWindow(AppSettings settings)
    {
        _settings = settings;
        _isRunning = settings.IsRunning;
        InitializeComponent();
        SessionsList.ItemsSource = _sessions;
        BuildRangeTabs();
        _feed.Layout = settings.CardLayout();

        Loaded += (_, _) =>
        {
            // Every start docks at the tray corner, the same placement the
            // tray menu's "Reset widget position" uses. A position the user
            // drags to lasts until they exit.
            ResetPosition();

            ApplyRunningState();
            DiagnosticLog.WriteInfo($"App started. isRunning={_isRunning}");
        };
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

        // The taskbar is raised above every other topmost window whenever it
        // is clicked, which buries a pill on or near it; put the pill back.
        var keepOnTopTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        keepOnTopTimer.Tick += (_, _) => KeepOnTop();
        keepOnTopTimer.Start();

        // A monitor change or an unlock can strand the pill off-screen or
        // under the taskbar: bring it back.
        SystemEvents.DisplaySettingsChanged += OnDisplayOrSessionChanged;
        SystemEvents.SessionSwitch += OnDisplayOrSessionChanged;
        Closed += (_, _) =>
        {
            keepOnTopTimer.Stop();
            SystemEvents.DisplaySettingsChanged -= OnDisplayOrSessionChanged;
            SystemEvents.SessionSwitch -= OnDisplayOrSessionChanged;
        };

        FirstRefresh = RefreshAsync();
    }

    /// <summary>Hides the widget, or shows it again where it was.</summary>
    public bool ToggleVisible()
    {
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
            if (!IsMostlyOnScreen())
            {
                DiagnosticLog.WriteInfo("Screen layout changed and the widget is off-screen; re-docking.");
                ResetPosition();
            }
            KeepOnTop();
        });

    public void ToggleExpanded()
    {
        _isExpanded = !_isExpanded;
        _opensAbove = null; // decide afresh from where the pill is now
        ExpandedPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
        ExpandButton.ToolTip = _isExpanded ? "Hide sessions" : "Show sessions";
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
        _feed.Layout = _settings.CardLayout();
        _ = RefreshAsync();
    }

    /// <summary>Puts the widget in the work-area corner nearest the tray: the
    /// bottom right when the taskbar is at the bottom (just above the tray
    /// icons), the top right when it is at the top (just below them). The
    /// work area excludes the taskbar, so the widget is never under it.</summary>
    public void ResetPosition() =>
        MoveToCorner(SystemParameters.WorkArea.Top > 0.5 ? WidgetCorner.TopRight : WidgetCorner.BottomRight);

    /// <summary>Moves the widget to a corner of the work area (the screen
    /// minus the taskbar) and remembers it.</summary>
    public void MoveToCorner(WidgetCorner corner)
    {
        Collapse();
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
        SettingsService.Save(_settings);
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
            _settings.WidgetRight = _pillRight + OuterMargin;
            _settings.WidgetBottom = _pillBottom + OuterMargin;
            SettingsService.Save(_settings);
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

    /// <summary>Opens a terminal running Claude Code's login (it then opens the browser to confirm).
    /// /k keeps the window open so any error stays readable.</summary>
    private void SignInButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/k claude auth login") { UseShellExecute = true });
            DiagnosticLog.WriteInfo("Sign in: started 'claude auth login' in a terminal.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DockedStripWindow.SignInButton_Click", ex);
        }
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
            _feed.ForgetLoggedSession();
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
            DiagnosticLog.WriteError("DockedStripWindow.RefreshAsync", ex);
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
        ShowView(vm);
    }

    /// <summary>Test hook and position handler: the compact (in the taskbar)
    /// or full (on the desktop) form of the pill.</summary>
    internal void SetCompact(bool compact)
    {
        _compact = compact;
        if (_lastView is not null)
            ShowView(_lastView);
    }

    /// <summary>Monthly cap: "$96.75 48% │ Today: $4.20 42% │ Session: $3.05". Plan: "5h: 43% -
    /// Resets: 2h 6m │ 1w: 14% │ Rem. Credits: $9.32 │ Updated: just now", or
    /// in the taskbar just the windows: "5h: 43% - 2h 6m │ 1w: 14%".</summary>
    private void ShowView(WidgetViewModel vm)
    {
        SignInButton.Visibility = vm.NeedsSignIn ? Visibility.Visible : Visibility.Collapsed;
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
        SetOptionalText(StaleText, vm.StaleText);
        SetOptionalText(WeeklyText, vm.WeeklyText, WeeklyDivider);
        SetOptionalText(CreditsText, _compact ? null : vm.CreditsText, CreditsDivider);
        SetOptionalText(UpdatedText, _compact ? null : vm.UpdatedText, UpdatedDivider);
        SpendGroup.ToolTip = vm.UsageTooltip;
        SpendGroup.Visibility = vm.ShowUsage ? Visibility.Visible : Visibility.Collapsed;

        SummaryTitleText.Text = vm.SummaryTitle;
        SummaryUpdatedText.Text = vm.SummaryUpdatedText ?? "";
        SummaryRowsList.ItemsSource = vm.SummaryRows;
        MonthSummary.Visibility = vm.SummaryRows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
                cards = await _feed.GetCardsAsync(days);
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
            var work = IsLoaded ? ToBox(SystemParameters.WorkArea) : unbounded;

            Box? flyout = null;
            if (_isExpanded)
            {
                var layout = FlyoutPlacement.Place(pill, FlyoutWidth, NaturalFlyoutHeight(), screen, work,
                    preferAbove: true, gap: FlyoutGap, forceAbove: _opensAbove,
                    maxHeightCap: IsLoaded ? SystemParameters.PrimaryScreenHeight * MaxFlyoutScreenShare : null);
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

    /// <summary>Whether the visible pill is within the virtual screen (all monitors).</summary>
    private bool IsMostlyOnScreen()
    {
        var left = SystemParameters.VirtualScreenLeft;
        var top = SystemParameters.VirtualScreenTop;
        var right = left + SystemParameters.VirtualScreenWidth;
        var bottom = top + SystemParameters.VirtualScreenHeight;

        return _pillRight > left + 40 && _pillRight - 40 < right &&
               _pillBottom > top + PillHeight && _pillBottom - PillHeight / 2 < bottom;
    }
}
