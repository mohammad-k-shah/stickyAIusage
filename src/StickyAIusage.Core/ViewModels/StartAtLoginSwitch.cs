namespace StickyAIusage.ViewModels;

/// <summary>
/// The Settings window's "Start with Windows" / "Launch at login" switch. The same setting can
/// change while the window is open (Health's Turn on/off, the tray or menu bar item), so Save
/// must apply only a change made with this switch: comparing the switch with the current setting
/// would undo a change made elsewhere (seen on a Mac: Health → Turn on, then Save turned it off).
/// </summary>
public sealed class StartAtLoginSwitch(bool shown)
{
    /// <summary>The value the switch showed last without the user touching it.</summary>
    public bool Shown { get; private set; } = shown;

    /// <summary>The setting changed outside the switch: the window shows the new value and
    /// takes it as the untouched state.</summary>
    public void ChangedElsewhere(bool value) => Shown = value;

    /// <summary>What Save should set the setting to, or null to leave it as it is now.</summary>
    /// <param name="switchValue">The switch as the user left it.</param>
    /// <param name="current">The setting right now (it may have changed elsewhere).</param>
    public bool? OnSave(bool switchValue, bool current) =>
        switchValue != Shown && switchValue != current ? switchValue : null;
}
