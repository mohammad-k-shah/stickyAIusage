using StickyAIusage.Health;
using StickyAIusage.Services;

namespace StickyAIusage.Demo;

/// <summary>
/// <c>--demo</c> (or <c>--demo cap</c>): the app with the demo's sample data instead of the real
/// sources, for screenshots, recordings and contributors without a subscription. It runs in a
/// temporary profile (data, feed, Claude and Codex folders under TEMP, set through the same
/// overrides as the self-test), so it starts beside an installed copy, finds no first-run window,
/// and leaves nothing behind when it exits.
/// </summary>
public static class DemoMode
{
    public const string Argument = "--demo";
    public const string CapArgument = "cap";
    public const string FolderPrefix = "StickyAIusage-demo-";

    /// <summary>The running demo, or null; the windows' services follow it.</summary>
    public static DemoRun? Active { get; private set; }

    /// <summary>True when the arguments ask for the demo; the word after it picks the variant.</summary>
    public static bool IsRequested(IReadOnlyList<string> args, out DemoVariant variant)
    {
        variant = DemoVariant.Plan;
        for (var i = 0; i < args.Count; i++)
        {
            if (!string.Equals(args[i], Argument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Count && string.Equals(args[i + 1], CapArgument, StringComparison.OrdinalIgnoreCase))
                variant = DemoVariant.Cap;
            return true;
        }
        return false;
    }

    /// <summary>Points the whole process at a new temporary profile, writes its settings (first run
    /// already done, nothing at login) and starts the script now. Call before anything reads a path; the
    /// app then leaves the path arguments (<c>--profile</c>) alone: the profile is the demo's.</summary>
    public static DemoRun Start(DemoVariant variant, DateTimeOffset now)
    {
        var sandbox = SelfTestSandbox.Create(prefix: FolderPrefix);
        var settings = new AppSettings
        {
            OnboardingCompleted = true,
            OnboardingVersion = Onboarding.CurrentVersion,
            StartWithWindows = false
        };
        SettingsService.Save(settings);
        var run = new DemoRun(new DemoData(variant, now), sandbox);
        Active = run;
        // Every way out removes the folder: a quit from the menu ends Main (the caller's using), but
        // macOS's own quit (Cmd-Q, AppleScript "quit") can end the process without returning there.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => run.Dispose();
        DiagnosticLog.WriteInfo($"Demo mode: a temporary profile in {sandbox.Root}.");
        return run;
    }

    /// <summary>What the tray tooltip and the alerts add to the app's name: "demo", a profile,
    /// or nothing for the default copy.</summary>
    public static string? InstanceLabel(AppLocations locations) =>
        Active is not null ? "demo" : locations.IsDefaultInstance ? null : locations.InstanceLabel;

    internal static void Ended(DemoRun run)
    {
        if (ReferenceEquals(Active, run))
            Active = null;
    }
}

/// <summary>One demo run: its script and its temporary profile, removed on Dispose.</summary>
public sealed class DemoRun : IDisposable
{
    internal DemoRun(DemoData data, SelfTestSandbox sandbox)
    {
        Data = data;
        Sandbox = sandbox;
    }

    public DemoData Data { get; }

    public SelfTestSandbox Sandbox { get; }

    /// <summary>The demo's graph for a window.</summary>
    public AppServices CreateServices(AppSettings settings) => AppServices.CreateDemo(settings, Data, Sandbox.Root);

    private int _disposed;

    /// <summary>Removes the temporary profile; a second call (the exit paths overlap) does nothing.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;
        DemoMode.Ended(this);
        Sandbox.Dispose();
    }
}
