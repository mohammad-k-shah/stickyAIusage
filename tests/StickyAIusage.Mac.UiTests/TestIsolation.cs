using System.IO;
using System.Runtime.CompilerServices;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.UiTests;

/// <summary>Runs before any test: the windows under test keep their cache,
/// settings and log in a temp folder instead of the app's real data folder
/// (on a developer's machine the installed widget uses it).</summary>
internal static class TestIsolation
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        var root = Path.Combine(Path.GetTempPath(), "stickyaiusage-mac-uitests");
        Environment.SetEnvironmentVariable(AppPaths.DataDirectoryVariable, Path.Combine(root, "data"));
        Environment.SetEnvironmentVariable(AppPaths.FeedDirectoryVariable, Path.Combine(root, "feed"));
        // Times of day follow the OS format; the expected texts and the
        // screenshots are written for a 12-hour English clock.
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        // Screenshots show the final state, not a ring halfway through easing.
        StickyAIusage.Mac.Controls.Motion.Enabled = false;
    }
}
