using System.IO;

namespace StickyAIusage.Tests.Shared;

/// <summary>
/// Every picture the READMEs show, by folder under <c>docs/images</c>, and which test project
/// renders it from the demo's data: the macOS screens, the share cards, the social preview and
/// the GIF by the Avalonia headless tests (any OS, a locked screen too), the Windows screens by
/// the WPF tests (an unlocked Windows session). <c>scripts/refresh-screenshots.ps1</c> runs both
/// and copies the output over the README's pictures; the Core tests check that the READMEs use
/// exactly these. Only <see cref="HandMade"/> is drawn by hand.
/// </summary>
public static class ReadmePictures
{
    public static readonly string[] Mac =
    [
        "fields.png", "flyout-codex.png", "flyout-monthly.png", "flyout-plan-light.png", "flyout-plan.png",
        "flyout-session-lights.png", "pill-codex-menubar.png", "pill-monthly-menubar.png", "pill-monthly.png", "pill-paused.png",
        "pill-plan-menubar.png", "pill-plan-stale.png", "pill-plan.png", "pill-session-lights.png", "pill-sign-in.png",
        "settings-light.png", "settings.png"
    ];

    public static readonly string[] Windows =
    [
        "flyout-codex.png", "flyout-plan-pace.png", "pill-orange-stale.png", "pill-paused.png", "pill-plan-feed-as-of.png",
        "pill-plan-forecast.png"
    ];

    public static readonly string[] Share = ["limits-wide-dark.png", "session-wide-light.png", "year-wide-dark.png"];

    /// <summary>At the top of <c>docs/images</c>: the GitHub social preview and the README's moving picture.</summary>
    public static readonly string[] Top = ["social-preview.png", "demo.gif"];

    /// <summary>The logo, the one picture no test draws.</summary>
    public static readonly string[] HandMade = ["logo.png"];

    /// <summary>Every rendered picture as a path under <c>docs/images</c> ("mac/pill-plan.png").</summary>
    public static IEnumerable<string> Rendered =>
        Mac.Select(f => "mac/" + f).Concat(Windows.Select(f => "windows/" + f)).Concat(Share.Select(f => "share/" + f)).Concat(Top);

    /// <summary>Where a marketing run writes (UI_MARKETING_DIR), or a temporary folder.</summary>
    public static string OutputFolder =>
        Environment.GetEnvironmentVariable("UI_MARKETING_DIR") is { Length: > 0 } folder
            ? folder
            : Path.Combine(Path.GetTempPath(), "stickyaiusage-marketing");
}
