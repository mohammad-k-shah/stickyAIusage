using StickyAIusage.Models;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Health;

/// <summary>Which first-run window a start shows.</summary>
public enum OnboardingScreen
{
    None,

    /// <summary>A new install: the three steps.</summary>
    FirstRun,

    /// <summary>An upgrade from an older version: one screen with Connect.</summary>
    WhatsNew,

    /// <summary>Connect alone, opened from Settings → Health (never by <see cref="Onboarding.ScreenFor"/>).</summary>
    Connect
}

/// <summary>
/// The first-run window: when it shows, and every word it says (both apps
/// show the same). A new install gets three steps (what the app reads,
/// connect to Claude Code, make it yours); an upgrade gets one screen about
/// the plugin, once. Closing the window counts as done: Settings → Health has
/// everything it offers, and a window that comes back at every start would
/// push people away.
/// </summary>
public static class Onboarding
{
    /// <summary>Raise it to show a "what's new" screen to everyone who finished an older one.</summary>
    public const int CurrentVersion = 1;

    /// <param name="pluginConnected">The plugin has written to the feed folder:
    /// the upgrade screen (which is about connecting it) has nothing to say.</param>
    public static OnboardingScreen ScreenFor(AppSettings settings, bool pluginConnected) =>
        !settings.OnboardingCompleted ? OnboardingScreen.FirstRun
        : settings.OnboardingVersion < CurrentVersion && !pluginConnected ? OnboardingScreen.WhatsNew
        : OnboardingScreen.None;

    /// <summary>Marks the window done (Finish, or closed). Returns true when that changed anything to save.</summary>
    public static bool Complete(AppSettings settings)
    {
        if (settings.OnboardingCompleted && settings.OnboardingVersion >= CurrentVersion)
            return false;
        settings.OnboardingCompleted = true;
        settings.OnboardingVersion = Math.Max(settings.OnboardingVersion, CurrentVersion);
        return true;
    }

    public const string WindowTitle = "Welcome to Sticky AI Usage";

    /// <summary>The window's caption (taskbar, Alt+Tab, screen readers): never just
    /// "Sticky AI Usage", which is the widget's own.</summary>
    public static string Caption(OnboardingScreen screen) => screen switch
    {
        OnboardingScreen.WhatsNew => "Sticky AI Usage - What's new",
        OnboardingScreen.Connect => "Sticky AI Usage - " + ConnectTitle,
        _ => WindowTitle
    };

    /// <summary>The large title at the top of the window.</summary>
    public static string Heading(OnboardingScreen screen) => screen switch
    {
        OnboardingScreen.WhatsNew => WhatsNewTitle,
        OnboardingScreen.Connect => ConnectTitle,
        _ => WindowTitle
    };

    public static string StepCaption(int step) => $"Step {step} of 3";

    public const string ReadsTitle = "What this app reads";

    /// <summary>Step 1's three lines.</summary>
    public static IReadOnlyList<string> ReadsLines { get; } =
    [
        "Claude Code's session files on this computer, for each session's tokens, cost and context.",
        "The numbers Claude Code hands to its plugin after each answer: your 5-hour and weekly limits.",
        "Nothing is sent anywhere. There is no account, no telemetry, and your sign-in is never read."
    ];

    public const string NetworkLine =
        "The one exception, off unless you turn it on: once a day, ask the project on GitHub whether a newer version is out, " +
        "and fetch the latest model prices. " +
        "Nothing about you is sent.";

    public const string UpdateCheckLabel = "Check for updates once a day";

    public const string ConnectTitle = "Connect to Claude Code";

    /// <summary>What the plugin does, all of it: besides the numbers it gives Claude a usage tool and,
    /// near a limit, a one-line note in its context (a few tokens), both of which can be turned off.</summary>
    public const string ConnectIntro =
        "A small Claude Code plugin hands your plan limits to the widget after each answer. It makes no network requests " +
        "and never calls a model. It also gives Claude a usage tool and, near a limit, adds a one-line usage note to its " +
        "context; turn either off with /plugin configure sticky-usage. Install it once:";

    public const string RunLabel = "Run it for me";
    public const string CopyLabel = "Copy for Claude Code";

    /// <summary>Where the copied line goes, and the Desktop Code tab's own way (it has a Plugins
    /// menu; the docs give <c>/plugin</c> for the terminal only, checked 2026-11-20).</summary>
    public static string CopyHint(string source) =>
        $"The line above runs in a terminal as it is. Copy for Claude Code copies {PluginInstaller.SessionCommand(source)} " +
        $"instead, to paste into a Claude Code session. In Claude Desktop's Code tab: + next to the prompt box, " +
        $"then Plugins, Add marketplace {source}, then Add plugin.";

    public const string NoCliHint = "Claude Code's command line wasn't found here: use Copy for Claude Code instead.";

    public const string LegacyLink = "Use legacy mode instead (unofficial)";
    public const string LegacyButton = "Use legacy mode";
    public const string LegacyChosen = "Legacy mode is on: the widget reads your plan limits from Claude's usage service.";

    public const string YoursTitle = "Make it yours";
    public static string StartAtLoginLabel(bool isMac) => isMac ? "Launch at login" : "Start with Windows";
    public const string AlertsLabel = "Notify me near a limit and when it resets";

    public static string PlacementLine(bool isMac) => isMac
        ? "The widget hangs from your menu bar, next to the status icons. Drag it anywhere; it stays where you leave it."
        : "Drag it anywhere to leave it there; Settings → Widget position changes this later.";

    /// <summary>Windows: the label over step 3's dock choice.</summary>
    public const string PlacementTitle = "Where the widget sits";

    public const string FinishNote = "Settings → Health checks all of this again whenever you like.";

    public const string WhatsNewTitle = "New: official numbers through the plugin";

    public const string WhatsNewIntro =
        "Sticky AI Usage can now get your plan limits from Claude Code itself, through a small plugin, without reading your " +
        "sign-in. Your current setup keeps working; connect the plugin when you're ready:";
}

/// <summary>Where the Connect step stands.</summary>
public enum ConnectPhase
{
    /// <summary>Nothing done yet.</summary>
    Ready,

    /// <summary>"Run it for me" is running.</summary>
    Installing,

    /// <summary>Installed or copied: waiting for Claude Code's first record.</summary>
    Waiting,

    /// <summary>The plugin wrote, but the limits come with Claude Code's next answer.</summary>
    ConnectedNoNumbers,

    /// <summary>Real numbers from the plugin.</summary>
    Connected,

    /// <summary>The install didn't work: the output and the manual steps are shown.</summary>
    Failed
}

/// <param name="Status">The line under the buttons.</param>
/// <param name="Output">What the commands printed, after a failure; null otherwise.</param>
public sealed record ConnectView(ConnectPhase Phase, string Status, string? Output)
{
    public bool CanRun => Phase is not ConnectPhase.Installing;
    public bool IsDone => Phase is ConnectPhase.Connected or ConnectPhase.ConnectedNoNumbers;
}

/// <summary>The Connect step's state, from what was done and what the feed shows. Pure.</summary>
public static class ConnectFlow
{
    public const string InstallingStatus = "Installing the plugin with Claude Code…";

    public const string InstalledWaiting =
        "Installed. Waiting for Claude Code… start a new session (or the Code tab of Claude Desktop) and send any message.";

    public const string CopiedWaiting =
        "Copied. Paste it into Claude Code and send any message there. Waiting for Claude Code…";

    public const string ConnectedNoNumbersStatus =
        "Connected to Claude Code. Your limits come with its next answer.";

    /// <param name="installing">"Run it for me" is under way.</param>
    /// <param name="result">The last install's result; null: none ran.</param>
    /// <param name="copied">"Copy for Claude Code" was used.</param>
    /// <param name="usage">The window's latest reading.</param>
    /// <param name="pluginWrote">The feed folder has the plugin's marker or a record.</param>
    public static ConnectView View(bool installing, InstallResult? result, bool copied, UsageSnapshot? usage, bool pluginWrote,
        string shellCommand, string sessionCommand)
    {
        if (usage is { HasReading: true, SourceId: ClaudeFeedUsageSource.Id } reading)
            return new ConnectView(ConnectPhase.Connected, "Connected: " + Numbers(reading) + ".", null);
        if (pluginWrote)
            return new ConnectView(ConnectPhase.ConnectedNoNumbers, ConnectedNoNumbersStatus, null);
        if (installing)
            return new ConnectView(ConnectPhase.Installing, InstallingStatus, null);
        if (result is { Succeeded: true })
            return new ConnectView(ConnectPhase.Waiting, InstalledWaiting, null);
        if (result is { } failed)
            return new ConnectView(ConnectPhase.Failed,
                Services.DiagnosticLog.HideHome(FailureStatus(failed, shellCommand, sessionCommand)),
                failed.Transcript.Length == 0 ? null : Services.DiagnosticLog.HideHome(failed.Transcript));
        if (copied)
            return new ConnectView(ConnectPhase.Waiting, CopiedWaiting, null);
        return new ConnectView(ConnectPhase.Ready, "", null);
    }

    /// <summary>What went wrong and what to do by hand.</summary>
    public static string FailureStatus(InstallResult result, string shellCommand, string sessionCommand)
    {
        var said = Sentence(result.Message);
        var what = result.Outcome switch
        {
            InstallOutcome.NoCli => said,
            InstallOutcome.Policy => $"Claude Code refused: {said} Your organization may not allow user plugins; " +
                                     "legacy mode is available below.",
            InstallOutcome.Offline => $"Claude Code couldn't reach GitHub: {said} Check the connection and try again.",
            InstallOutcome.NotFound => $"Claude Code couldn't find the plugin: {said}",
            _ => $"The install didn't work: {said}"
        };
        return result.Outcome == InstallOutcome.NoCli
            ? what
            : $"{what} To do it by hand, run {shellCommand} in a terminal, or paste {sessionCommand} into Claude Code.";
    }

    /// <summary>Claude Code's words end with a full stop before ours follow.</summary>
    private static string Sentence(string text)
    {
        var trimmed = text.Trim();
        return trimmed.Length == 0 || ".!?".Contains(trimmed[^1]) ? trimmed : trimmed + ".";
    }

    private static string Numbers(UsageSnapshot usage)
    {
        var parts = new List<string>();
        if (usage.HasMonthlySpend)
            parts.Add(usage.MonthlySpendPercentOnly
                ? $"{Format.WholePercent(usage.MonthlySpendPercent)} of your spend limit"
                : $"{Format.Usd(usage.MonthlySpendUsedUsd)} of {Format.Usd(usage.MonthlySpendLimitUsd)}");
        if (usage.FiveHourUtilizationPercent is { } fiveHour)
            parts.Add($"5-hour {Format.WholePercent(fiveHour)}");
        if (usage.WeeklyUtilizationPercent is { } weekly)
            parts.Add($"weekly {Format.WholePercent(weekly)}");
        return parts.Count == 0 ? "your limits are in" : string.Join(" · ", parts);
    }
}
