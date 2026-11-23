using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>Settings → Save applies only a change made with the window's start-at-login switch,
/// never undoing one made meanwhile from Health or the tray/menu bar item.</summary>
public class StartAtLoginSwitchTests
{
    [Fact]
    public void An_untouched_switch_leaves_a_change_made_elsewhere_alone()
    {
        // Opened with it off; the menu bar item turned it on; Save with the switch still showing off.
        var startAtLogin = new StartAtLoginSwitch(shown: false);
        Assert.Null(startAtLogin.OnSave(switchValue: false, current: true));
    }

    [Fact]
    public void A_change_made_with_the_switch_is_applied()
    {
        var startAtLogin = new StartAtLoginSwitch(shown: false);
        Assert.True(startAtLogin.OnSave(switchValue: true, current: false));
        Assert.False(new StartAtLoginSwitch(shown: true).OnSave(switchValue: false, current: true));
    }

    [Fact]
    public void After_Health_turns_it_on_and_off_the_switch_follows_and_Save_keeps_it()
    {
        // The case seen on a Mac: Health → Turn off, Health → Turn on, then Save.
        var startAtLogin = new StartAtLoginSwitch(shown: true);
        startAtLogin.ChangedElsewhere(false);
        startAtLogin.ChangedElsewhere(true);
        Assert.True(startAtLogin.Shown);
        Assert.Null(startAtLogin.OnSave(switchValue: true, current: true));
    }

    [Fact]
    public void Nothing_to_do_when_the_switch_already_matches()
    {
        var startAtLogin = new StartAtLoginSwitch(shown: false);
        startAtLogin.ChangedElsewhere(true);
        // The user then switched it off again in the window: applied.
        Assert.False(startAtLogin.OnSave(switchValue: false, current: true));
        Assert.Null(startAtLogin.OnSave(switchValue: true, current: true));
    }
}
