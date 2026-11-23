using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace StickyAIusage.Services;

/// <summary>
/// <c>--self-test [report-path]</c>: the published app checks that it can start —
/// its embedded resources open, the price list parses, settings save and load,
/// and (each app adds its own) the tray icon loads and every screen builds —
/// writes one PASS or FAIL line per check and exits 0 or 1. The release
/// workflow runs it on the very files it publishes, because the tray-icon bug
/// once existed only in the downloaded build.
///
/// It runs in a <see cref="SelfTestSandbox"/> (temporary data, feed and Claude
/// folders), never shows a window and never touches the real data folder, a
/// running copy's lock or the network. A watchdog ends it after
/// <see cref="TimeLimit"/> with a FAIL, so a hang can't stall a release.
/// </summary>
public sealed class SelfTest
{
    public const string Argument = "--self-test";

    /// <summary>The whole run, start to exit.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(20);

    private readonly List<SelfTestResult> _results = [];
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private bool _finished;
    // Until the first Finish has written its report, a second caller sees a failure.
    private volatile int _exitCode = 1;

    /// <summary>"Windows 2.0.0 (3967cd3)": the first line of the report.</summary>
    public SelfTest(string title) => Title = title;

    public string Title { get; }

    public IReadOnlyList<SelfTestResult> Results
    {
        get { lock (_lock) return _results.ToList(); }
    }

    /// <summary>True when at least one check ran and none failed.</summary>
    public bool Passed
    {
        get { lock (_lock) return _results.Count > 0 && _results.All(result => result.Passed); }
    }

    /// <summary>Whether the command line asks for a self-test, and where its report goes:
    /// <c>--self-test</c>, <c>--self-test report.txt</c> or <c>--self-test=report.txt</c>.
    /// A following <c>--option</c> is not taken for a path.</summary>
    public static bool IsRequested(IReadOnlyList<string> args, out string? reportPath)
    {
        reportPath = null;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith(Argument + "=", StringComparison.OrdinalIgnoreCase))
            {
                reportPath = NullIfBlank(arg[(Argument.Length + 1)..]);
                return true;
            }
            if (!string.Equals(arg, Argument, StringComparison.OrdinalIgnoreCase))
                continue;
            if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                reportPath = NullIfBlank(args[i + 1]);
            return true;
        }
        return false;
    }

    /// <summary>Runs one check: it returns what it saw (the PASS line's detail) or
    /// throws (the FAIL line's). Never throws itself.</summary>
    public void Check(string name, Func<string> check)
    {
        SelfTestResult result;
        try
        {
            result = new SelfTestResult(name, true, check());
        }
        catch (Exception ex)
        {
            var reason = ex is SelfTestFailure ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";
            result = new SelfTestResult(name, false, reason);
        }
        lock (_lock)
        {
            if (!_finished)
                _results.Add(result);
        }
    }

    /// <summary>The checks every app shares, inside <paramref name="sandbox"/>. False when
    /// the app's paths aren't the sandbox's: then nothing else may run (it would read or
    /// write the real data folder), and the run has already failed.</summary>
    public bool RunCoreChecks(SelfTestSandbox sandbox, Assembly appAssembly, AppLocations? inUse = null)
    {
        // What the app resolved, not what the sandbox asked for: a start that
        // ignored the overrides would be caught here.
        var locations = inUse ?? AppPaths.Current;
        Check("data folder", () => CheckDataFolder(sandbox, locations));
        if (!Passed)
            return false;
        Check("version", () => AppIdentity.Version(appAssembly));
        Check("app resources", () => OpenEveryResource(appAssembly));
        Check("core resources", () => OpenEveryResource(typeof(SelfTest).Assembly));
        Check("price list", () => CheckPriceList(typeof(ModelCatalog).Assembly, ModelCatalog.ResourceName));
        Check("settings", () => CheckSettingsRoundTrip(Path.Combine(sandbox.Locations.DataDirectory, "settings.json")));
        Check("transcript index", () => CheckTranscriptIndex(Path.Combine(sandbox.Root, "index-check", "projects")));
        Check("year stats", () => CheckYearStats(Path.Combine(sandbox.Root, "stats-check")));
        Check("codex", () => CheckCodex(Path.Combine(sandbox.Root, "codex-check")));
        Check("health", () => CheckHealth(Path.Combine(sandbox.Root, "health-check")));
        Check("demo", () => CheckDemo(Path.Combine(sandbox.Root, "demo-check")));
        Check("update check", () => CheckUpdates(appAssembly, Path.Combine(sandbox.Root, "update-check")));
        return true;
    }

    /// <summary>The update check can work for this build: its version is a release version
    /// (a release stamps the tag), and a sample release list - answered here, nothing is
    /// asked of GitHub - with the next patch reads as an update with its command.</summary>
    internal static string CheckUpdates(Assembly appAssembly, string root) =>
        CheckUpdates(AppIdentity.ReleaseVersion(appAssembly), AppIdentity.Version(appAssembly), root, OperatingSystem.IsMacOS());

    /// <param name="isMac">Picks the sample install method: the script on a Mac, winget on Windows.</param>
    internal static string CheckUpdates(Updates.SemanticVersion? version, string versionText, string root, bool isMac)
    {
        var running = version
                      ?? throw new SelfTestFailure($"the version \"{versionText}\" isn't a release version, " +
                                                   "so the update check could never compare it");
        var next = new Updates.SemanticVersion(running.Major, running.Minor, running.Patch + 1, []);
        var list = $$"""[{"tag_name":"v{{next}}","prerelease":false,"draft":false},{"tag_name":"v{{running}}","prerelease":{{(running.IsPreRelease ? "true" : "false")}},"draft":false}]""";
        using var http = new System.Net.Http.HttpClient(new CannedAnswer(list));
        using var checker = new Updates.UpdateChecker(http, root, () => DateTimeOffset.UtcNow, running,
            isMac ? Updates.InstallMethod.Script : Updates.InstallMethod.Winget, isMac) { Log = _ => { } };
        var outcome = checker.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
        if (outcome != Updates.UpdateCheckOutcome.NewerFound || checker.Notice is not { } notice || !notice.Version.Equals(next))
            throw new SelfTestFailure($"a sample list with v{next} came out as {outcome}");
        if (!File.Exists(Path.Combine(root, Updates.UpdateChecker.StateFileName)))
            throw new SelfTestFailure("the answer wasn't kept");
        return $"{running} is a release version; a sample list's v{next} reads as an update ({notice.Command})";
    }

    /// <summary>Answers every request with one release list: the self-test never goes online.</summary>
    private sealed class CannedAnswer(string json) : System.Net.Http.HttpMessageHandler
    {
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new System.Net.Http.HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new System.Net.Http.StringContent(json)
            });
    }

    /// <summary>Settings → Health and the first-run choice over sample files in their own
    /// folder: a transcript written by Claude Code 2.1.325 and the plugin's marker must read
    /// as found, current and connected; the report hides the home folder; a data folder
    /// without settings is a first run.</summary>
    internal static string CheckHealth(string root)
    {
        var claude = Path.Combine(root, "claude");
        var transcript = Path.Combine(claude, "projects", "selftest", Guid.NewGuid() + ".jsonl");
        var feed = Path.Combine(root, "feed");
        Directory.CreateDirectory(Path.GetDirectoryName(transcript)!);
        Directory.CreateDirectory(feed);
        File.WriteAllText(transcript, """{"type":"user","version":"2.1.325","sessionId":"selftest"}""" + "\n");
        var now = DateTimeOffset.UtcNow;
        File.WriteAllText(Path.Combine(feed, ClaudeFeedUsageSource.MarkerFileName),
            """{"schema":1,"pluginVersion":"2.3.0","claudeCodeVersion":"2.1.325","surface":"terminal","observedAt":""" +
            now.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture) + "}");
        var (exists, version, marker, installed) = Health.HealthProbe.Read(Health.DiskHealthFiles.Instance, claude, feed, transcript);
        var items = Health.HealthCheck.Build(new Health.HealthFacts
        {
            Now = now, ClaudeDirectory = claude, ClaudeDirectoryExists = exists, TranscriptCount = 1, ClaudeCodeVersion = version,
            Marker = marker, PluginInstalled = installed
        });
        foreach (var id in new[] { "claude-code", "claude-version", "feed" })
            if (items.First(i => i.Id == id) is { Status: not Health.HealthStatus.Ok } item)
                throw new SelfTestFailure($"{item.Title} read {item.StatusLabel}: {item.Detail}");
        var report = Health.HealthCheck.Report(items, "self-test", now);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (home.Length > 1 && report.Contains(home, StringComparison.OrdinalIgnoreCase))
            throw new SelfTestFailure("the report shows the home folder");
        var screen = Health.Onboarding.ScreenFor(SettingsService.Load(Path.Combine(root, "settings.json")), pluginConnected: false);
        if (screen != Health.OnboardingScreen.FirstRun)
            throw new SelfTestFailure($"a data folder without settings shows {screen}, not the first-run window");
        return $"{items.Count} items, Claude Code {version}, plugin {marker?.PluginVersion} connected; report {report.Length} characters; first run: three steps";
    }

    /// <summary>Codex's reader over a sample session log of today: its own folder (the
    /// sandbox's Codex folder stays empty, so the windows show no Codex), one event with
    /// both windows, read back as a 5-hour and a weekly window.</summary>
    /// <summary>The demo (<c>--demo</c>) near the end of its climb, through the graph the windows
    /// use: the reading, its pace, three sessions (one waiting), their cards and Codex.</summary>
    internal static string CheckDemo(string root)
    {
        var now = DateTimeOffset.UtcNow;
        var data = Demo.DemoData.At(Demo.DemoVariant.Plan, now, TimeSpan.FromSeconds(110));
        using var services = AppServices.CreateDemo(new AppSettings(), data, root, () => now);
        var summary = services.Feed.GetSummaryAsync().GetAwaiter().GetResult();
        var cards = services.Feed.GetCardsAsync(1).GetAwaiter().GetResult();
        var waiting = summary.Activities.Count(a => a.State == ViewModels.ActivityState.NeedsYou);
        if (summary.Usage.FiveHourUtilizationPercent != data.FiveHourPercent(now) || summary.Forecasts.Count != 2 ||
            summary.Activities.Count != 3 || waiting != 1 || cards.Count != 3 || summary.Others.Count != 1)
            throw new SelfTestFailure($"5-hour {summary.Usage.FiveHourUtilizationPercent}%, {summary.Forecasts.Count} forecasts, " +
                                      $"{summary.Activities.Count} sessions ({waiting} waiting), {cards.Count} cards, {summary.Others.Count} other tools");
        return $"5-hour {summary.Usage.FiveHourUtilizationPercent:0}%, limit forecast, 3 sessions (1 needs you), 3 cards, Codex";
    }

    internal static string CheckCodex(string codexHome)
    {
        var now = DateTimeOffset.UtcNow;
        var local = DateTime.Now;
        var day = Path.Combine(codexHome, "sessions", local.ToString("yyyy", CultureInfo.InvariantCulture),
            local.ToString("MM", CultureInfo.InvariantCulture), local.ToString("dd", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(day);
        var reset = now.AddHours(2).ToUnixTimeSeconds();
        var stamp = now.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture);
        File.WriteAllText(Path.Combine(day, "rollout-selftest.jsonl"),
            $$$"""{"timestamp":"{{{stamp}}}Z","type":"event_msg","payload":{"type":"token_count","info":null,"rate_limits":{"primary":{"used_percent":42.0,"window_minutes":300,"resets_at":{{{reset}}}},"secondary":{"used_percent":17.0,"window_minutes":10080,"resets_at":{{{reset + 86400}}}},"plan_type":"plus"}""" + "}}\n");
        var provider = new CodexUsageProvider(codexHome, () => DateTimeOffset.UtcNow);
        var usage = provider.Read();
        if (!provider.IsPresent || usage.Window(Models.UsageWindowKinds.FiveHour)?.PercentUsed != 42 ||
            usage.Window(Models.UsageWindowKinds.SevenDay)?.PercentUsed != 17)
            throw new SelfTestFailure($"read {usage.Windows.Count} windows ({usage.Health}: {usage.HealthDetail}), not 42% and 17%");
        return $"{usage.PlanName} plan, 5-hour {usage.Windows[0].PercentUsed:0}%, weekly {usage.Windows[1].PercentUsed:0}%";
    }

    /// <summary>The year card's pass over two sample sessions (1 January and now), then a
    /// second pass by a new service, which must take the finished days from the month
    /// files and agree. Its own folder, as the index check.</summary>
    internal static string CheckYearStats(string root)
    {
        var now = DateTimeOffset.Now;
        var projects = Path.Combine(root, "projects", "C--selftest-year");
        Directory.CreateDirectory(projects);
        var newYear = new DateTimeOffset(now.Year, 1, 1, 12, 0, 0, now.Offset);
        WriteSampleTranscript(Path.Combine(projects, "00000000-0000-4000-8000-0000000000b1.jsonl"), newYear < now ? newYear : now);
        WriteSampleTranscript(Path.Combine(projects, "00000000-0000-4000-8000-0000000000b2.jsonl"), now);
        var statsFolder = Path.Combine(root, "stats");
        using var index = new TranscriptIndex(Path.Combine(root, "projects"));
        var first = new Stats.YearStatsService(index, statsFolder).Scan(now.Year);
        var second = new Stats.YearStatsService(index, statsFolder).Scan(now.Year);
        if (first.Summary.Sessions != 2 || first.Summary.TotalTokens != 2 * 20_010)
            throw new SelfTestFailure($"read {first.Summary.Sessions} sessions and {first.Summary.TotalTokens} tokens, not 2 and 40010");
        if (second.Summary != first.Summary)
            throw new SelfTestFailure("a second pass over the cache disagreed with the first");
        if (first.DaysSaved > 0 && second.DaysFromCache != first.DaysSaved)
            throw new SelfTestFailure($"{first.DaysSaved} days saved, but {second.DaysFromCache} read back from the cache");
        return $"{first.FilesRead} transcripts in {first.Duration.TotalMilliseconds:0} ms, {first.DaysSaved} days saved; " +
               $"again: {second.DaysFromCache} days from the cache, {second.FilesRead} transcripts read";
    }

    /// <summary>The start-up walk of the transcript index, then a session written after
    /// it, which must arrive through the file-system watcher alone (no second walk).
    /// Its own folder, so the windows' first refresh still sees an empty sandbox.</summary>
    internal static string CheckTranscriptIndex(string projectsRoot, TimeSpan? watcherWait = null)
    {
        var project = Path.Combine(projectsRoot, "C--selftest-sample");
        Directory.CreateDirectory(project);
        WriteSampleTranscript(Path.Combine(project, "00000000-0000-4000-8000-0000000000a1.jsonl"));
        using var sessions = new ClaudeSessionLogSource(projectsRoot);
        var index = sessions.Index;
        if (!index.WaitUntilReady(TimeSpan.FromSeconds(10)) || index.FirstWalkDuration is not { } walk)
            throw new SelfTestFailure("the first walk didn't finish within 10 s");
        if (index.Mode != TranscriptIndexMode.Watching)
            throw new SelfTestFailure($"not watching the folder ({index.Mode})");
        if (index.Count != 1)
            throw new SelfTestFailure($"the first walk found {index.Count} transcripts, not 1");

        var timer = Stopwatch.StartNew();
        WriteSampleTranscript(Path.Combine(project, "00000000-0000-4000-8000-0000000000a2.jsonl"));
        var limit = watcherWait ?? TimeSpan.FromSeconds(5);
        while (index.Count < 2 && timer.Elapsed < limit)
            Thread.Sleep(10);
        if (index.Count < 2 || index.WalkCount != 1)
            throw new SelfTestFailure($"the watcher didn't report a new transcript within {limit.TotalSeconds:0} s");
        var listed = sessions.GetSessionsForDate(DateOnly.FromDateTime(DateTime.Now)).Count;
        if (listed != 2)
            throw new SelfTestFailure($"{listed} of 2 sample sessions listed");
        return $"first walk {walk.TotalMilliseconds:0} ms, watcher saw a new session in {timer.Elapsed.TotalMilliseconds:0} ms, " +
               $"{listed} sessions listed";
    }

    private static void WriteSampleTranscript(string path, DateTimeOffset? at = null)
    {
        var id = Path.GetFileNameWithoutExtension(path);
        var now = (at ?? DateTimeOffset.UtcNow).ToString("O");
        var user = new { type = "user", sessionId = id, cwd = "/selftest/sample", timestamp = now, message = new { role = "user", content = "hi" } };
        var assistant = new
        {
            type = "assistant", sessionId = id, cwd = "/selftest/sample", timestamp = now,
            message = new { id = "msg_" + id, model = "claude-sonnet-5", role = "assistant", usage = new { input_tokens = 10, output_tokens = 20_000 } }
        };
        File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(user) + "\n" + System.Text.Json.JsonSerializer.Serialize(assistant) + "\n");
    }

    /// <summary>The report: a title, one line per check, a result line.</summary>
    public string Report()
    {
        var results = Results;
        var text = new StringBuilder();
        text.AppendLine($"Sticky AI Usage self-test - {Title}");
        foreach (var result in results)
            text.AppendLine(result.ToString());
        var failed = results.Count(result => !result.Passed);
        text.AppendLine(results.Count == 0 ? "RESULT: FAIL (no checks ran)"
            : failed == 0 ? $"RESULT: PASS ({results.Count} checks, {_clock.Elapsed.TotalSeconds:0.0} s)"
            : $"RESULT: FAIL ({failed} of {results.Count} checks failed)");
        return text.ToString();
    }

    /// <summary>Writes the report (to <paramref name="reportPath"/> when given, and to
    /// <paramref name="output"/>) and returns the exit code: 0 passed, 1 failed. The
    /// first call wins: a check that ends after it, or a second call, changes nothing.</summary>
    public int Finish(string? reportPath, TextWriter? output, string? appendix = null)
    {
        lock (_lock)
        {
            if (_finished)
                return _exitCode;
            _finished = true;
        }
        _exitCode = WriteReport(reportPath, output, appendix);
        return _exitCode;
    }

    private int WriteReport(string? reportPath, TextWriter? output, string? appendix)
    {
        var passed = Passed;
        var report = Report();
        if (!passed && !string.IsNullOrWhiteSpace(appendix))
            report += Environment.NewLine + appendix.TrimEnd() + Environment.NewLine;

        try
        {
            output?.Write(report);
            output?.Flush();
        }
        catch (IOException)
        {
            // No console (a GUI exe started without redirection): the file is the report.
        }
        if (reportPath is not null)
        {
            try
            {
                var folder = Path.GetDirectoryName(Path.GetFullPath(reportPath));
                if (!string.IsNullOrEmpty(folder))
                    Directory.CreateDirectory(folder);
                File.WriteAllText(reportPath, report);
            }
            catch (Exception ex)
            {
                // A report nobody can read must not look like a pass.
                try { output?.WriteLine($"FAIL report: {ex.GetType().Name}: {ex.Message}"); } catch (IOException) { }
                return 1;
            }
        }
        return passed ? 0 : 1;
    }

    /// <summary>The report of a run that broke off with <paramref name="error"/> before it
    /// could finish (the self-test's own code failed): always a FAIL, exit code 1.</summary>
    public static int ReportCrash(string? reportPath, TextWriter? output, Exception error)
    {
        var crashed = new SelfTest("broke off");
        crashed.Check("self-test", () => throw new SelfTestFailure($"{error.GetType().Name}: {error.Message}"));
        crashed.Finish(reportPath, output, error.ToString());
        return 1;
    }

    /// <summary>Ends the run after <paramref name="limit"/> with a FAIL line, the report
    /// and <paramref name="exit"/>(1), unless <see cref="Finish"/> came first.
    /// Dispose the result to stop it.</summary>
    public IDisposable StartWatchdog(TimeSpan limit, string? reportPath, TextWriter? output, Action<int> exit,
        Func<string?>? appendix = null) =>
        new Timer(_ =>
        {
            lock (_lock)
            {
                if (_finished)
                    return;
                _results.Add(new SelfTestResult("time limit", false, $"the self-test did not finish within {limit.TotalSeconds:0} s"));
            }
            exit(Finish(reportPath, output, appendix?.Invoke()));
        }, null, limit, Timeout.InfiniteTimeSpan);

    /// <summary>The paths in use must be the sandbox's: a self-test that read or wrote the
    /// real data folder could change a running widget's settings.</summary>
    internal static string CheckDataFolder(SelfTestSandbox sandbox, AppLocations locations)
    {
        foreach (var (what, path) in new[]
                 {
                     ("data folder", locations.DataDirectory), ("log folder", locations.LogDirectory),
                     ("feed folder", locations.FeedDirectory), ("Claude folder", locations.ClaudeDirectory),
                     ("Codex folder", locations.CodexDirectory)
                 })
        {
            if (!sandbox.Contains(path))
                throw new SelfTestFailure($"the {what} {path} is outside the temporary folder {sandbox.Root}");
        }
        return $"temporary, {sandbox.Root}";
    }

    /// <summary>Opens every resource embedded in <paramref name="assembly"/>; each must be
    /// there and hold bytes. (A single-file publish has no loose files to fall back on.)</summary>
    internal static string OpenEveryResource(Assembly assembly) =>
        OpenEveryResource(assembly.GetName().Name ?? "the assembly", assembly.GetManifestResourceNames(), assembly.GetManifestResourceStream);

    internal static string OpenEveryResource(string assemblyName, IReadOnlyList<string> names, Func<string, Stream?> open)
    {
        if (names.Count == 0)
            throw new SelfTestFailure($"{assemblyName} has no embedded resources");
        long bytes = 0;
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            using var stream = open(name) ?? throw new SelfTestFailure($"{name} is listed but doesn't open");
            var length = CountBytes(stream);
            if (length == 0)
                throw new SelfTestFailure($"{name} is empty");
            bytes += length;
        }
        return $"{names.Count} opened ({bytes:N0} bytes)";
    }

    /// <summary>The embedded price list parses and has its rows: without it every cost
    /// would be a guess.</summary>
    internal static string CheckPriceList(Assembly assembly, string resourceName)
    {
        using var stream = assembly.GetManifestResourceStream(resourceName)
                           ?? throw new SelfTestFailure($"the embedded price list {resourceName} is missing");
        using var reader = new StreamReader(stream);
        if (!ModelCatalog.TryParse(reader.ReadToEnd(), out var catalog, out var problem) || catalog is null)
            throw new SelfTestFailure($"the embedded price list doesn't parse: {problem}");
        return $"{catalog.Models.Count} models, updated {catalog.Updated:yyyy-MM-dd}";
    }

    /// <summary>Saves non-default settings and reads them back, through the same code a
    /// start and Settings → Save use.</summary>
    internal static string CheckSettingsRoundTrip(string settingsPath)
    {
        var first = SettingsService.Load(settingsPath);
        first.Theme = "light";
        first.ShowSessionCost = !first.ShowSessionCost;
        first.AlertQuietFrom = "22:15";
        SettingsService.Save(first, settingsPath);
        var again = SettingsService.Load(settingsPath);
        if (again.Theme != first.Theme || again.ShowSessionCost != first.ShowSessionCost || again.AlertQuietFrom != first.AlertQuietFrom)
            throw new SelfTestFailure("the saved settings read back differently");
        return $"saved and read back (usage source {again.ClaudeSource}, format {again.SchemaVersion})";
    }

    private static long CountBytes(Stream stream)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            total += read;
        return total;
    }

    private static string? NullIfBlank(string value) => string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>One line of the report.</summary>
public sealed record SelfTestResult(string Name, bool Passed, string Detail)
{
    public override string ToString() => $"{(Passed ? "PASS" : "FAIL")} {Name}: {Detail}";
}

/// <summary>A check's own verdict, reported without the exception type.</summary>
public sealed class SelfTestFailure(string message) : Exception(message);

/// <summary>
/// The temporary profile a self-test runs in: fresh data, feed, Claude and Codex
/// folders under the temp folder, set through the same environment overrides the
/// tests use, so nothing reads the real ones (and the instance lock is the
/// sandbox's own). <see cref="Dispose"/> deletes the folder and restores the
/// environment.
/// </summary>
public sealed class SelfTestSandbox : IDisposable
{
    private static readonly string[] Variables =
    [
        AppPaths.DataDirectoryVariable, AppPaths.FeedDirectoryVariable, AppPaths.ClaudeConfigVariable,
        AppPaths.CodexHomeVariable, AppPaths.ProfileVariable
    ];

    private readonly Dictionary<string, string?> _saved = [];
    private readonly bool _applied;

    private SelfTestSandbox(string root, bool apply)
    {
        Root = root;
        var overrides = new Dictionary<string, string?>
        {
            [AppPaths.DataDirectoryVariable] = Path.Combine(root, "data"),
            [AppPaths.FeedDirectoryVariable] = Path.Combine(root, "feed"),
            [AppPaths.ClaudeConfigVariable] = Path.Combine(root, "claude"),
            [AppPaths.CodexHomeVariable] = Path.Combine(root, "codex"),
            [AppPaths.ProfileVariable] = null
        };
        foreach (var folder in overrides.Values.OfType<string>())
            Directory.CreateDirectory(folder);
        // The home folder is inside the sandbox too, so nothing can fall back to the real one.
        var home = Path.Combine(root, "home");
        Locations = AppPaths.Resolve(name => overrides.GetValueOrDefault(name), [], OperatingSystem.IsMacOS(), home, home);
        if (!apply)
            return;

        foreach (var name in Variables)
            _saved[name] = Environment.GetEnvironmentVariable(name);
        foreach (var (name, value) in overrides)
            Environment.SetEnvironmentVariable(name, value);
        Locations = AppPaths.Configure([]);
        _applied = true;
    }

    public string Root { get; }

    /// <summary>The paths of the sandbox (after <see cref="Create"/>: the ones the app uses).</summary>
    public AppLocations Locations { get; }

    /// <summary>Makes the folders and points the whole process at them through the
    /// environment and <see cref="AppPaths.Configure"/>. Call before anything reads a path.</summary>
    /// <param name="prefix">The folder name's start: the self-test's, or the demo's (<c>--demo</c>).</param>
    public static SelfTestSandbox Create(string? parent = null, string prefix = FolderPrefix) => new(NewRoot(parent, prefix), apply: true);

    public const string FolderPrefix = "StickyAIusage-selftest-";

    /// <summary>The folders only, leaving the process's paths alone (unit tests run in parallel).</summary>
    internal static SelfTestSandbox CreateDetached(string parent, string prefix = FolderPrefix) => new(NewRoot(parent, prefix), apply: false);

    private static string NewRoot(string? parent, string prefix) =>
        Path.Combine(parent ?? Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N")[..12]);

    public bool Contains(string path)
    {
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>The run's diagnostics log, for a failed report (null when there is none).</summary>
    public string? LogText()
    {
        try
        {
            var log = Path.Combine(Locations.LogDirectory, "diagnostics.log");
            if (!File.Exists(log))
                return null;
            using var stream = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return "Log of this run:" + Environment.NewLine + reader.ReadToEnd();
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_applied)
        {
            foreach (var (name, value) in _saved)
                Environment.SetEnvironmentVariable(name, value);
        }
        for (var attempt = 0; attempt < 5 && Directory.Exists(Root); attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The log may still be open for a moment: a temp folder left behind is harmless.
                Thread.Sleep(100);
            }
        }
    }
}
