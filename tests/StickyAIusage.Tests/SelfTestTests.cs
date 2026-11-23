using System.IO;
using System.Text;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>The <c>--self-test</c> helper both apps run on their published builds.
/// (Each app's own checks run in its UI test project; the published exe runs in CI.)</summary>
public class SelfTestTests
{
    [Theory]
    [InlineData(new string[0], false, null)]
    [InlineData(new[] { "--profile", "dev" }, false, null)]
    [InlineData(new[] { "--self-test" }, true, null)]
    [InlineData(new[] { "--SELF-TEST" }, true, null)]
    [InlineData(new[] { "--self-test", "report.txt" }, true, "report.txt")]
    [InlineData(new[] { "--self-test=out/report.txt" }, true, "out/report.txt")]
    [InlineData(new[] { "--self-test=" }, true, null)]
    [InlineData(new[] { "--self-test", "--profile", "dev" }, true, null)]
    [InlineData(new[] { "--profile", "dev", "--self-test", "r.txt" }, true, "r.txt")]
    public void The_command_line_asks_for_a_self_test_and_names_the_report(string[] args, bool requested, string? path)
    {
        Assert.Equal(requested, SelfTest.IsRequested(args, out var reportPath));
        Assert.Equal(path, reportPath);
    }

    [Fact]
    public void The_shared_checks_pass_in_a_sandbox()
    {
        using var temp = new TempDir();
        using var sandbox = SelfTestSandbox.CreateDetached(temp.Path);
        var selfTest = new SelfTest("test");

        Assert.True(selfTest.RunCoreChecks(sandbox, typeof(SelfTest).Assembly, sandbox.Locations));

        Assert.True(selfTest.Passed, selfTest.Report());
        Assert.Equal(["data folder", "version", "app resources", "core resources", "price list", "settings", "transcript index", "year stats", "codex", "health", "demo", "update check"],
            selfTest.Results.Select(result => result.Name));
        Assert.True(File.Exists(Path.Combine(sandbox.Locations.DataDirectory, "settings.json")));
        Assert.Contains("PASS price list: ", selfTest.Report());
        Assert.Contains("PASS transcript index: first walk ", selfTest.Report());
        Assert.Contains("PASS year stats: 2 transcripts in ", selfTest.Report());
        Assert.Contains("PASS codex: Plus plan, 5-hour 42%, weekly 17%", selfTest.Report());
        Assert.Contains("PASS health: 10 items, Claude Code 2.1.325, plugin 2.3.0 connected; ", selfTest.Report());
        Assert.Contains("PASS demo: 5-hour 89%, limit forecast, 3 sessions (1 needs you), 3 cards, Codex", selfTest.Report());
        Assert.Contains("is a release version; a sample list's v", selfTest.Report());
        Assert.Contains("RESULT: PASS (12 checks, ", selfTest.Report());
    }

    [Fact]
    public void Paths_outside_the_sandbox_stop_the_run_before_anything_is_read()
    {
        using var temp = new TempDir();
        using var sandbox = SelfTestSandbox.CreateDetached(temp.Path);
        var real = sandbox.Locations with { DataDirectory = temp.Combine("real-data") };
        var selfTest = new SelfTest("test");

        Assert.False(selfTest.RunCoreChecks(sandbox, typeof(SelfTest).Assembly, real));

        Assert.Equal("data folder", Assert.Single(selfTest.Results).Name);
        Assert.False(selfTest.Passed);
        Assert.False(File.Exists(Path.Combine(sandbox.Locations.DataDirectory, "settings.json")));
    }

    [Fact]
    public void A_missing_price_list_fails_the_run()
    {
        var selfTest = new SelfTest("test");

        selfTest.Check("price list", () => SelfTest.CheckPriceList(typeof(SelfTest).Assembly, "StickyAIusage.Resources.no-such.json"));

        Assert.False(selfTest.Passed);
        Assert.Equal("FAIL price list: the embedded price list StickyAIusage.Resources.no-such.json is missing",
            selfTest.Results.Single().ToString());
        Assert.Equal(1, selfTest.Finish(null, null));
    }

    [Fact]
    public void The_built_in_price_list_passes()
    {
        var detail = SelfTest.CheckPriceList(typeof(SelfTest).Assembly, ModelCatalog.ResourceName);

        Assert.StartsWith($"{ModelCatalog.BuiltIn.Models.Count} models, updated ", detail);
    }

    [Fact]
    public void A_path_outside_the_sandbox_fails_the_data_folder_check()
    {
        using var temp = new TempDir();
        using var sandbox = SelfTestSandbox.CreateDetached(temp.Path);
        var real = sandbox.Locations with { LogDirectory = Path.Combine(temp.Path, "logs") };

        var failure = Assert.Throws<SelfTestFailure>(() => SelfTest.CheckDataFolder(sandbox, real));
        Assert.StartsWith("the log folder ", failure.Message);
        Assert.StartsWith("temporary, ", SelfTest.CheckDataFolder(sandbox, sandbox.Locations));
    }

    [Fact]
    public void The_sandbox_holds_only_its_own_folder()
    {
        using var temp = new TempDir();
        using var sandbox = SelfTestSandbox.CreateDetached(temp.Path);

        Assert.True(sandbox.Contains(sandbox.Root));
        Assert.True(sandbox.Contains(Path.Combine(sandbox.Root, "data", "cache")));
        Assert.False(sandbox.Contains(sandbox.Root + "-other"));
        Assert.False(sandbox.Contains(temp.Path));
        if (OperatingSystem.IsWindows())
            Assert.True(sandbox.Contains(sandbox.Root.ToUpperInvariant()));
    }

    [Fact]
    public void A_detached_sandbox_deletes_its_folder_and_leaves_the_process_alone()
    {
        using var temp = new TempDir();
        var before = AppPaths.Current;
        var dataVariable = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        string root;
        using (var sandbox = SelfTestSandbox.CreateDetached(temp.Path))
        {
            root = sandbox.Root;
            Assert.True(Directory.Exists(sandbox.Locations.DataDirectory));
            Assert.True(Directory.Exists(sandbox.Locations.ClaudeDirectory));
            Assert.Same(before, AppPaths.Current);
        }

        Assert.False(Directory.Exists(root));
        Assert.Equal(dataVariable, Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable));
    }

    [Fact]
    public void Every_resource_must_open_and_hold_bytes()
    {
        var resources = new Dictionary<string, byte[]> { ["a.json"] = [1, 2, 3], ["b.png"] = [4] };
        Stream? Open(string name) => resources.TryGetValue(name, out var bytes) ? new MemoryStream(bytes) : null;

        Assert.Equal("2 opened (4 bytes)", SelfTest.OpenEveryResource("App", ["b.png", "a.json"], Open));
        Assert.Equal("App has no embedded resources",
            Assert.Throws<SelfTestFailure>(() => SelfTest.OpenEveryResource("App", [], Open)).Message);
        Assert.Equal("c.ico is listed but doesn't open",
            Assert.Throws<SelfTestFailure>(() => SelfTest.OpenEveryResource("App", ["a.json", "c.ico"], Open)).Message);
        resources["empty.ico"] = [];
        Assert.Equal("empty.ico is empty",
            Assert.Throws<SelfTestFailure>(() => SelfTest.OpenEveryResource("App", ["empty.ico"], Open)).Message);
    }

    [Fact]
    public void The_core_assembly_s_resources_open()
    {
        Assert.StartsWith("1 opened (", SelfTest.OpenEveryResource(typeof(SelfTest).Assembly));
    }

    [Fact]
    public void A_check_that_throws_is_a_fail_line_with_the_reason()
    {
        var selfTest = new SelfTest("test");

        selfTest.Check("good", () => "fine");
        selfTest.Check("broken", () => throw new InvalidOperationException("no window"));
        selfTest.Check("refused", () => throw new SelfTestFailure("too small"));

        Assert.Equal(["PASS good: fine", "FAIL broken: InvalidOperationException: no window", "FAIL refused: too small"],
            selfTest.Results.Select(result => result.ToString()));
        Assert.False(selfTest.Passed);
        Assert.Contains("RESULT: FAIL (2 of 3 checks failed)", selfTest.Report());
    }

    [Fact]
    public void No_checks_is_not_a_pass()
    {
        var selfTest = new SelfTest("test");

        Assert.False(selfTest.Passed);
        Assert.Contains("RESULT: FAIL (no checks ran)", selfTest.Report());
        Assert.Equal(1, selfTest.Finish(null, null));
    }

    [Fact]
    public void Finish_writes_the_report_to_the_file_and_the_output()
    {
        using var temp = new TempDir();
        var path = temp.Combine("reports", "self-test.txt");
        var output = new StringWriter();
        var selfTest = new SelfTest("Windows 2.0.0");
        selfTest.Check("good", () => "fine");

        Assert.Equal(0, selfTest.Finish(path, output, appendix: "Log of this run: only shown on a failure"));

        var report = File.ReadAllText(path);
        Assert.Equal(report, output.ToString());
        Assert.StartsWith("Sticky AI Usage self-test - Windows 2.0.0" + Environment.NewLine + "PASS good: fine" + Environment.NewLine + "RESULT: PASS (1 checks, ", report);
        Assert.DoesNotContain("Log of this run", report);
        Assert.True(Encoding.UTF8.GetByteCount(report) == report.Length, "the report is plain ASCII");
    }

    [Fact]
    public void A_failed_report_ends_with_the_log()
    {
        using var temp = new TempDir();
        var path = temp.Combine("self-test.txt");
        var selfTest = new SelfTest("test");
        selfTest.Check("broken", () => throw new SelfTestFailure("no"));

        Assert.Equal(1, selfTest.Finish(path, null, appendix: "Log of this run:\nline"));

        Assert.EndsWith("Log of this run:\nline" + Environment.NewLine, File.ReadAllText(path));
    }

    [Fact]
    public void Only_the_first_finish_counts()
    {
        using var temp = new TempDir();
        var path = temp.Combine("self-test.txt");
        var selfTest = new SelfTest("test");
        selfTest.Check("good", () => "fine");
        Assert.Equal(0, selfTest.Finish(path, null));
        File.Delete(path);

        selfTest.Check("late", () => throw new SelfTestFailure("after the report"));

        Assert.Equal(0, selfTest.Finish(path, null));
        Assert.False(File.Exists(path));
        Assert.Single(selfTest.Results);
    }

    [Fact]
    public void A_report_that_can_t_be_written_is_a_failure()
    {
        using var temp = new TempDir();
        var selfTest = new SelfTest("test");
        selfTest.Check("good", () => "fine");
        var output = new StringWriter();

        // The path is a folder: the file can't be written.
        Assert.Equal(1, selfTest.Finish(temp.Path, output));
        Assert.Contains("FAIL report: ", output.ToString());
        // Asked again (the watchdog, a late caller), the verdict stays.
        Assert.Equal(1, selfTest.Finish(null, null));
    }

    [Fact]
    public async Task The_watchdog_fails_a_run_that_does_not_finish()
    {
        using var temp = new TempDir();
        var path = temp.Combine("self-test.txt");
        var selfTest = new SelfTest("test");
        selfTest.Check("good", () => "fine");
        var exited = new TaskCompletionSource<int>();

        using var watchdog = selfTest.StartWatchdog(TimeSpan.FromMilliseconds(50), path, null, exited.SetResult, () => "Log of this run: stuck");

        Assert.Same(exited.Task, await Task.WhenAny(exited.Task, Task.Delay(TimeSpan.FromSeconds(10))));
        Assert.Equal(1, await exited.Task);
        var report = File.ReadAllText(path);
        Assert.Contains("FAIL time limit: the self-test did not finish within 0 s", report);
        Assert.Contains("Log of this run: stuck", report);
        // The run that comes back later can't overwrite the verdict.
        Assert.Equal(1, selfTest.Finish(path, null));
    }

    [Fact]
    public async Task The_watchdog_does_nothing_after_the_run_finished()
    {
        var selfTest = new SelfTest("test");
        selfTest.Check("good", () => "fine");
        Assert.Equal(0, selfTest.Finish(null, null));
        var exits = 0;

        using (selfTest.StartWatchdog(TimeSpan.FromMilliseconds(10), null, null, _ => Interlocked.Increment(ref exits)))
            await Task.Delay(300);

        Assert.Equal(0, exits);
        Assert.Single(selfTest.Results);
    }

    [Fact]
    public void A_run_that_broke_off_reports_a_failure()
    {
        using var temp = new TempDir();
        var path = temp.Combine("self-test.txt");

        Assert.Equal(1, SelfTest.ReportCrash(path, null, new InvalidOperationException("no dispatcher")));

        var report = File.ReadAllText(path);
        Assert.Contains("FAIL self-test: InvalidOperationException: no dispatcher", report);
        Assert.Contains("RESULT: FAIL", report);
    }

    [Fact]
    public void The_settings_check_reads_back_what_it_saved()
    {
        using var temp = new TempDir();
        var path = temp.Combine("settings.json");

        Assert.StartsWith("saved and read back (usage source feed, format ", SelfTest.CheckSettingsRoundTrip(path));
        var saved = SettingsService.Load(path);
        Assert.Equal("light", saved.Theme);
        Assert.Equal("22:15", saved.AlertQuietFrom);
    }

    [Fact]
    public void Settings_that_can_t_be_saved_fail_the_check()
    {
        // Save only logs a failure (a start must not crash over it), so the read-back is what notices.
        using var temp = new TempDir();
        var folder = temp.Combine("settings.json");
        Directory.CreateDirectory(folder);

        var failure = Assert.Throws<SelfTestFailure>(() => SelfTest.CheckSettingsRoundTrip(folder));
        Assert.Equal("the saved settings read back differently", failure.Message);
    }
}

/// <summary>The sandbox as the apps use it: it moves the whole process's paths, so it
/// runs alone (no other test may see its folders) and puts everything back.</summary>
[Collection(Name)]
public class SelfTestSandboxProcessTests
{
    public const string Name = "Self-test sandbox moves the process's paths";

    [Fact]
    public void Create_points_the_app_at_the_sandbox_and_dispose_puts_it_back()
    {
        var dataVariable = Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable);
        var claudeVariable = Environment.GetEnvironmentVariable(AppPaths.ClaudeConfigVariable);
        string root;
        try
        {
            using (var sandbox = SelfTestSandbox.Create())
            {
                root = sandbox.Root;
                Assert.Same(sandbox.Locations, AppPaths.Current);
                Assert.StartsWith("temporary, ", SelfTest.CheckDataFolder(sandbox, AppPaths.Current));
                Assert.Equal(Path.Combine(root, "claude"), AppPaths.ClaudeDirectory);
                Assert.Equal(Path.Combine(root, "data"), Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable));
            }

            Assert.False(Directory.Exists(root));
            Assert.Equal(dataVariable, Environment.GetEnvironmentVariable(AppPaths.DataDirectoryVariable));
            Assert.Equal(claudeVariable, Environment.GetEnvironmentVariable(AppPaths.ClaudeConfigVariable));
        }
        finally
        {
            AppPaths.Configure([]);
        }
    }
}

[CollectionDefinition(SelfTestSandboxProcessTests.Name, DisableParallelization = true)]
public class SelfTestSandboxProcessCollection;
