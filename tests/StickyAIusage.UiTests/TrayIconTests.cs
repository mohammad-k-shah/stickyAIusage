namespace StickyAIusage.UiTests;

/// <summary>
/// The tray icon is read from the assembly, not from a file beside the exe,
/// because the single-file build published for the Releases page has no such
/// file. Without this, a downloaded copy silently fell back to the generic
/// system icon while developer builds looked right.
/// </summary>
public class TrayIconTests
{
    [Fact]
    public void The_tray_icon_is_embedded_in_the_assembly()
    {
        using var stream = typeof(App).Assembly.GetManifestResourceStream(App.TrayIconResourceName);

        Assert.NotNull(stream);
        Assert.True(stream!.Length > 0);
    }

    [Fact]
    public void The_tray_icon_loads_rather_than_falling_back_to_the_system_icon()
    {
        using var icon = App.LoadTrayIcon();

        Assert.NotEqual(System.Drawing.SystemIcons.Application.Handle, icon.Handle);
        Assert.True(icon.Width > 0);
    }
}
