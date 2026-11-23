using Avalonia.Headless.XUnit;
using StickyAIusage.Mac;
using StickyAIusage.Services;

namespace StickyAIusage.Mac.UiTests;

/// <summary>What <c>--self-test</c> checks once Avalonia is set up (both theme
/// variants, the menu bar icon, every asset), on the test build. The published
/// binary runs the whole self-test in CI.</summary>
public class SelfTestChecksTests
{
    [AvaloniaFact]
    public void The_app_resources_pass_the_self_test_checks()
    {
        var selfTest = new SelfTest("UI tests");

        SelfTestRunner.CheckAppResources(selfTest);

        Assert.True(selfTest.Passed, selfTest.Report());
        Assert.Equal(["theme dark", "theme light", "menu bar icon", "share card", "assets"], selfTest.Results.Select(result => result.Name));
        Assert.Contains(selfTest.Results, result => result.ToString() == $"PASS menu bar icon: {SelfTestRunner.MenuBarIcon} (36x36)");
    }
}
