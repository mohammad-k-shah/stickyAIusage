using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using StickyAIusage.Mac.Controls;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.UiTests;

/// <summary>The session light's breathing: it runs until its dot leaves the window, and never
/// ends in an exception (an endless animation started with RunAsync used to fault on every
/// refresh: "Looping animations must not use the Run method", seen on a Mac).</summary>
public class MotionTests
{
    [AvaloniaFact]
    public async Task A_breathing_dot_runs_without_faulting_and_stops_when_removed()
    {
        var dot = new Border { Width = 8, Height = 8 };
        var panel = new StackPanel();
        panel.Children.Add(dot);
        var window = new Window { Content = panel, Width = 100, Height = 100 };
        window.Show();

        var breathing = Motion.BreatheNow(dot, strong: true);
        for (var i = 0; i < 5; i++)
        {
            window.CaptureRenderedFrame();
            await Task.Delay(20);
        }
        Assert.False(breathing.IsFaulted, breathing.Exception?.ToString());
        Assert.False(breathing.IsCompleted);

        panel.Children.Remove(dot);
        await breathing.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(breathing.IsCompletedSuccessfully);
        window.Close();
    }

    [AvaloniaFact]
    public void The_pill_shows_no_tooltips_while_the_panel_is_open()
    {
        // Seen on a Mac: the pill's tooltip covered the open panel's header buttons.
        var widget = new WidgetWindow(new AppSettings(), live: false);
        Assert.True(ToolTip.GetServiceEnabled(widget.PillBorder));
        widget.ToggleExpanded();
        Assert.False(ToolTip.GetServiceEnabled(widget.PillBorder));
        widget.ToggleExpanded();
        Assert.True(ToolTip.GetServiceEnabled(widget.PillBorder));
        widget.Close();
    }
}
