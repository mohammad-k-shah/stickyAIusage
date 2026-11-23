using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Where the Windows pill docks: in the taskbar left of the tray (centered, the taskbar's
/// height minus 8 within 24–32), above it when there is no room, the taskbar hides itself or there is
/// none; secondary taskbars; scaled screens (all in DIPs); the full-screen rule; the settings migration.</summary>
public class DockPlacementTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    // This PC at 150 %: 2560×2680 px → 1706.67×1786.67 DIPs; a 72 px taskbar = 48 DIPs; tray from 2029 px.
    private static readonly Box Monitor = new(0, 0, 1706.67, 1786.67);
    private static readonly Box Work = new(0, 0, 1706.67, 1738.67);
    private static readonly Box Taskbar = new(0, 1738.67, 1706.67, 48);
    private static readonly Box Tray = new(1352.67, 1738.67, 354, 48);

    private static TaskbarFacts Facts(double? buttonsRight = 900, Box? tray = null, bool noTray = false, Box? taskbar = null, bool autoHide = false) =>
        new(taskbar ?? Taskbar, noTray ? null : tray ?? Tray, buttonsRight, Monitor, Work, autoHide);

    [Fact]
    public void A_pill_that_fits_sits_left_of_the_tray_centered_in_the_taskbar()
    {
        var spot = DockPlacement.Place(DockMode.Taskbar, Facts(), pillWidth: 180, Work);

        Assert.Equal(DockMode.Taskbar, spot.Mode);
        Assert.Null(spot.Reason);
        Assert.Equal(Tray.X - DockPlacement.TaskbarGap, spot.Right, 3);
        Assert.Equal(32, spot.Height);
        Assert.Equal(Taskbar.Y + (48 - 32) / 2.0 + 32, spot.Bottom, 3); // 8 above and below
    }

    [Fact]
    public void Without_room_beside_the_app_buttons_it_goes_above_and_says_why()
    {
        // This PC today: the last app button ends at 1942 px (1294.67 DIPs), the tray starts at 1352.67: 58 DIPs free.
        var spot = DockPlacement.Place(DockMode.Taskbar, Facts(buttonsRight: 1294.67), pillWidth: 180, Work);
        Assert.Equal((DockMode.Above, DockPlacement.NoRoomReason), (spot.Mode, spot.Reason));
        Assert.Equal((Work.Right - 8, Work.Bottom - 8, 32.0), (spot.Right, spot.Bottom, spot.Height));

        // Exactly enough: the pill's width plus both gaps.
        var exact = DockPlacement.Place(DockMode.Taskbar, Facts(buttonsRight: Tray.X - 8 - 50), pillWidth: 50, Work);
        Assert.Equal(DockMode.Taskbar, exact.Mode);
        var oneShort = DockPlacement.Place(DockMode.Taskbar, Facts(buttonsRight: Tray.X - 8 - 50 + 0.5), pillWidth: 50, Work);
        Assert.Equal(DockMode.Above, oneShort.Mode);
    }

    [Fact]
    public void Unknown_buttons_count_as_room_but_never_past_the_taskbars_left_end()
    {
        Assert.Equal(DockMode.Taskbar, DockPlacement.Place(DockMode.Taskbar, Facts(buttonsRight: null), 180, Work).Mode);
        Assert.Equal(DockMode.Above, DockPlacement.Place(DockMode.Taskbar, Facts(buttonsRight: null), Tray.X, Work).Mode);
    }

    [Fact]
    public void A_secondary_taskbar_has_no_tray_so_the_pill_keeps_clear_of_its_clock()
    {
        var secondary = new Box(1706.67, 1738.67, 1280, 48);
        var facts = new TaskbarFacts(secondary, null, 1900, new Box(1706.67, 0, 1280, 1786.67), new Box(1706.67, 0, 1280, 1738.67));
        var spot = DockPlacement.Place(DockMode.Taskbar, facts, 180, Work);
        Assert.Equal(DockMode.Taskbar, spot.Mode);
        Assert.Equal(secondary.Right - DockPlacement.SecondaryClockWidth - DockPlacement.TaskbarGap, spot.Right, 3);
    }

    [Theory]
    [InlineData(48, 32)]   // Windows 11's default
    [InlineData(40, 32)]
    [InlineData(36, 28)]
    [InlineData(30, 24)]   // a small taskbar
    [InlineData(20, 24)]
    [InlineData(72, 32)]
    public void The_pill_follows_the_taskbars_height_within_24_and_32(double taskbarHeight, double pill)
    {
        Assert.Equal(pill, DockPlacement.HeightIn(taskbarHeight));
        var bar = new Box(0, Monitor.Bottom - taskbarHeight, Monitor.Width, taskbarHeight);
        var spot = DockPlacement.Place(DockMode.Taskbar, Facts(taskbar: bar, tray: Tray with { Y = bar.Y, Height = taskbarHeight }), 180, Work);
        Assert.Equal(pill, spot.Height);
        Assert.Equal(bar.Y + (taskbarHeight + pill) / 2, spot.Bottom, 3);
    }

    [Fact]
    public void An_auto_hidden_taskbar_puts_the_pill_above_the_screen_edge()
    {
        var full = new Box(0, 0, 1706.67, 1786.67);
        var hidden = new TaskbarFacts(Taskbar with { Y = Monitor.Bottom - 2 }, Tray, 900, Monitor, full, AutoHide: true);
        var spot = DockPlacement.Place(DockMode.Taskbar, hidden, 180, full);
        Assert.Equal((DockMode.Above, DockPlacement.AutoHideReason), (spot.Mode, spot.Reason));
        Assert.Equal(full.Bottom - 8, spot.Bottom, 3);

        // Shown at this moment (the pointer is on it), it still hides itself: the pill stays out of it.
        var showing = new TaskbarFacts(Taskbar, Tray, 900, Monitor, full, AutoHide: true);
        Assert.Equal(DockMode.Above, DockPlacement.Place(DockMode.Taskbar, showing, 180, full).Mode);

        // Slid off-screen without the flag reads the same.
        var offScreen = new TaskbarFacts(Taskbar with { Y = Monitor.Bottom - 2 }, Tray, 900, Monitor, full);
        Assert.Equal(DockMode.Above, DockPlacement.Place(DockMode.Taskbar, offScreen, 180, full).Mode);
    }

    [Fact]
    public void A_taskbar_at_the_top_puts_the_above_spot_below_it()
    {
        var top = new TaskbarFacts(new Box(0, 0, 1706.67, 48), null, null, Monitor, new Box(0, 48, 1706.67, 1738.67));
        var spot = DockPlacement.Place(DockMode.Above, top, 180, Work);
        Assert.Equal((DockMode.Above, 48 + 8 + 32.0), (spot.Mode, spot.Bottom));
    }

    [Fact]
    public void Without_a_taskbar_the_pill_takes_the_work_areas_corner()
    {
        var spot = DockPlacement.Place(DockMode.Taskbar, null, 180, Work);
        Assert.Equal((DockMode.Above, DockPlacement.NoTaskbarReason, Work.Right - 8, Work.Bottom - 8), (spot.Mode, spot.Reason, spot.Right, spot.Bottom));
        Assert.Null(DockPlacement.Place(DockMode.Above, null, 180, Work).Reason);
    }

    [Fact]
    public void Above_is_the_corner_whatever_room_the_taskbar_has()
    {
        var spot = DockPlacement.Place(DockMode.Above, Facts(), 180, Work);
        Assert.Equal((DockMode.Above, null, Work.Right - 8, Work.Bottom - 8, 32.0), (spot.Mode, spot.Reason, spot.Right, spot.Bottom, spot.Height));
    }

    [Fact]
    public void Scaling_changes_nothing_but_the_units()
    {
        // The same taskbar at 100 % (px = DIPs) and 125 %: the pill's spot scales with it.
        var at100 = new TaskbarFacts(new Box(0, 1032, 1920, 48), new Box(1600, 1032, 320, 48), 900, new Box(0, 0, 1920, 1080), new Box(0, 0, 1920, 1032));
        var at125 = new TaskbarFacts(new Box(0, 825.6, 1536, 38.4), new Box(1280, 825.6, 256, 38.4), 720, new Box(0, 0, 1536, 864), new Box(0, 0, 1536, 825.6));
        var a = DockPlacement.Place(DockMode.Taskbar, at100, 180, at100.WorkArea);
        var b = DockPlacement.Place(DockMode.Taskbar, at125, 180, at125.WorkArea);
        Assert.Equal((1596.0, 32.0), (a.Right, a.Height));
        Assert.Equal((1276.0, 30.4), (b.Right, b.Height), new ToleranceComparer());
    }

    private sealed class ToleranceComparer : IEqualityComparer<(double, double)>
    {
        public bool Equals((double, double) x, (double, double) y) => Math.Abs(x.Item1 - y.Item1) < 0.01 && Math.Abs(x.Item2 - y.Item2) < 0.01;
        public int GetHashCode((double, double) obj) => 0;
    }

    [Theory]
    [InlineData("taskbar", DockMode.Taskbar)]
    [InlineData("ABOVE", DockMode.Above)]
    [InlineData(" free ", DockMode.Free)]
    [InlineData("", DockMode.Taskbar)]
    [InlineData(null, DockMode.Taskbar)]
    [InlineData("left", DockMode.Taskbar)]
    public void Dock_modes_parse_and_write_back(string? text, DockMode mode)
    {
        Assert.Equal(mode, DockModes.Parse(text));
        Assert.Equal(mode, DockModes.Parse(mode.ToSetting()));
    }

    [Fact]
    public void Monitors_read_as_people_name_them()
    {
        Assert.Equal("Main display", DockMonitors.Label(@"\\.\DISPLAY1", isPrimary: true));
        Assert.Equal("Display 2", DockMonitors.Label(@"\\.\DISPLAY2", isPrimary: false));
        Assert.Equal("WinDisc", DockMonitors.Label("WinDisc", isPrimary: false));
        Assert.Equal("DISPLAYX", DockMonitors.Label(@"\\.\DISPLAYX", isPrimary: false));

        (string, bool)[] one = [(@"\\.\DISPLAY1", true)];
        (string, bool)[] two = [(@"\\.\DISPLAY1", true), (@"\\.\DISPLAY2", false)];
        Assert.Equal([("Taskbar", (string?)null)], DockMonitors.TaskbarMenu(one));
        Assert.Equal([("Taskbar on the main display", (string?)"primary"), ("Taskbar on Display 2", @"\\.\DISPLAY2")], DockMonitors.TaskbarMenu(two));
        Assert.Equal([("Main display", "primary"), ("Display 2", @"\\.\DISPLAY2")], DockMonitors.Choices(two));
        Assert.Equal(@"\\.\DISPLAY2", DockMonitors.Selected(@"\\.\display2", two));
        Assert.Equal("primary", DockMonitors.Selected(@"\\.\DISPLAY3", two)); // unplugged: the main display
        Assert.Equal("primary", DockMonitors.Selected(@"\\.\DISPLAY1", two)); // the primary one is "primary"
    }

    [Theory]
    [InlineData(true, FullScreenRule.PresentationMode, false, true)]
    [InlineData(true, FullScreenRule.RunningD3DFullScreen, true, true)]
    [InlineData(true, FullScreenRule.Busy, true, true)]
    [InlineData(true, FullScreenRule.Busy, false, false)]   // a full-screen app on another monitor
    [InlineData(true, FullScreenRule.RunningD3DFullScreen, false, false)]
    [InlineData(true, FullScreenRule.AcceptsNotifications, true, false)]
    [InlineData(true, FullScreenRule.NotPresent, true, false)] // locked or screen saver: nothing to hide from
    [InlineData(false, FullScreenRule.PresentationMode, true, false)]
    public void The_pill_steps_aside_for_full_screen_and_presentations(bool enabled, int state, bool fillsMonitor, bool hide) =>
        Assert.Equal(hide, FullScreenRule.ShouldHide(enabled, state, fillsMonitor));

    [Theory]
    [InlineData("""{ "SchemaVersion": 5, "WidgetDocked": true }""", "taskbar")]
    [InlineData("""{ "SchemaVersion": 5, "WidgetDocked": false, "WidgetRight": 900, "WidgetBottom": 700 }""", "free")]
    [InlineData("""{ "Theme": "dark" }""", "taskbar")]
    [InlineData("""{ "SchemaVersion": 6, "DockMode": "above" }""", "above")]
    public void Older_files_keep_a_dragged_pill_free_and_move_a_docked_one_into_the_taskbar(string json, string mode)
    {
        var path = _dir.Combine("settings.json");
        File.WriteAllText(path, json);
        var settings = SettingsService.Load(path);
        Assert.Equal((mode, AppSettings.CurrentSchemaVersion), (settings.DockMode, settings.SchemaVersion));
    }

    [Fact]
    public void A_new_install_docks_in_the_taskbar_on_the_primary_monitor_and_hides_for_full_screen()
    {
        var settings = SettingsService.Load(_dir.Combine("none.json"));
        Assert.Equal(("taskbar", AppSettings.PrimaryMonitor, true), (settings.DockMode, settings.DockMonitor, settings.HideWhenFullscreen));
    }
}
