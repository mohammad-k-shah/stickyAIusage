using System.Net;
using System.Net.Http;
using System.Text.Json;
using StickyAIusage.Health;
using StickyAIusage.Services;
using StickyAIusage.Updates;

namespace StickyAIusage.Tests;

/// <summary>Release versions in the order Semantic Versioning gives them.</summary>
public class SemanticVersionTests
{
    [Theory]
    [InlineData("2.0.0", 2, 0, 0, "")]
    [InlineData("v2.0.1", 2, 0, 1, "")]
    [InlineData("V10.20.30", 10, 20, 30, "")]
    [InlineData("2.0.0-beta.1", 2, 0, 0, "beta.1")]
    [InlineData("v2.0.0-rc.1", 2, 0, 0, "rc.1")]
    [InlineData("1.0.0+3967cd3abc", 1, 0, 0, "")]
    [InlineData("2.0.0-beta.1+3967cd3", 2, 0, 0, "beta.1")]
    [InlineData(" 2.1.0 ", 2, 1, 0, "")]
    [InlineData("1.0.0-alpha-1.x-y", 1, 0, 0, "alpha-1.x-y")]
    public void Versions_and_tags_parse(string text, int major, int minor, int patch, string preRelease)
    {
        var version = SemanticVersion.Parse(text);

        Assert.NotNull(version);
        Assert.Equal((major, minor, patch), (version.Major, version.Minor, version.Patch));
        Assert.Equal(preRelease, string.Join('.', version.PreRelease));
        Assert.Equal(preRelease.Length > 0, version.IsPreRelease);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("2.0")]
    [InlineData("v2.0.0.1")]
    [InlineData("02.0.0")]
    [InlineData("2.0.0-")]
    [InlineData("2.0.0-beta..1")]
    [InlineData("2.0.0-beta.01")]
    [InlineData("2.0.0+")]
    [InlineData("2.0.0-beta_1")]
    [InlineData("vv2.0.0")]
    [InlineData("2.0.x")]
    [InlineData("99999999999.0.0")]
    [InlineData("unknown version")]
    public void Malformed_tags_are_not_versions(string? text) => Assert.Null(SemanticVersion.Parse(text));

    [Fact]
    public void Precedence_follows_semantic_versioning()
    {
        // The example list of semver.org §11, lowest first, plus the release that follows.
        string[] ordered =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta", "1.0.0-beta.2", "1.0.0-beta.11",
            "1.0.0-rc.1", "1.0.0", "1.0.1", "1.1.0", "2.0.0-beta.1", "2.0.0-beta.2", "2.0.0-beta.10", "2.0.0", "10.0.0"
        ];
        for (var i = 0; i < ordered.Length; i++)
        for (var j = 0; j < ordered.Length; j++)
        {
            var a = SemanticVersion.Parse(ordered[i])!;
            var b = SemanticVersion.Parse(ordered[j])!;
            Assert.Equal(Math.Sign(i.CompareTo(j)), Math.Sign(a.CompareTo(b)));
        }
    }

    [Fact]
    public void Equal_versions_are_equal_whatever_their_tag_or_build()
    {
        Assert.Equal(SemanticVersion.Parse("v2.0.0"), SemanticVersion.Parse("2.0.0+abc1234"));
        Assert.Equal(SemanticVersion.Parse("2.0.0-beta.1")!.GetHashCode(), SemanticVersion.Parse("v2.0.0-beta.1")!.GetHashCode());
        Assert.False(SemanticVersion.Parse("2.0.0")!.IsNewerThan(SemanticVersion.Parse("v2.0.0")!));
        Assert.Equal("2.0.0-beta.1", SemanticVersion.Parse("v2.0.0-beta.1+abc")!.ToString());
    }

    [Fact]
    public void The_running_build_s_release_version_comes_from_its_informational_version()
    {
        // The SDK stamps "<Version>+<commit>"; the test assembly has the project's default.
        var version = AppIdentity.ReleaseVersion(typeof(SemanticVersionTests).Assembly);
        Assert.NotNull(version);
        Assert.False(version.IsPreRelease);
    }
}

/// <summary>The command each way of installing updates with, and how the way is found.</summary>
public class InstallMethodTests
{
    [Theory]
    [InlineData("winget", InstallMethod.Winget)]
    [InlineData("setup\r\n", InstallMethod.Setup)]
    [InlineData("SCRIPT", InstallMethod.Script)]
    [InlineData("something else", InstallMethod.Download)]
    [InlineData(null, InstallMethod.Download)]
    public void The_installers_one_word_file_names_the_way(string? word, InstallMethod expected)
    {
        var asked = new List<string>();
        var method = InstallMethods.Detect(@"C:\Users\you\AppData\Local\Programs\Sticky AI Usage", isMac: false,
            path => { asked.Add(path); return word; });

        Assert.Equal(expected, method);
        Assert.Equal(Path.Combine(@"C:\Users\you\AppData\Local\Programs\Sticky AI Usage", "install-method"), Assert.Single(asked));
    }

    [Theory]
    [InlineData(@"C:\Users\you\scoop\apps\sticky-ai-usage\current")]
    [InlineData(@"D:\Tools\Scoop\apps\sticky-ai-usage\2.0.1\")]
    [InlineData("C:/Users/you/scoop/apps/sticky-ai-usage/current")]
    public void Scoop_is_known_by_its_folder(string folder) =>
        Assert.Equal(InstallMethod.Scoop, InstallMethods.Detect(folder, isMac: false, _ => "setup"));

    [Fact]
    public void A_Mac_copy_always_updates_with_the_install_script_and_nothing_is_read()
    {
        var method = InstallMethods.Detect("/Applications/Sticky AI Usage.app/Contents/MacOS/", isMac: true,
            _ => throw new InvalidOperationException("nothing is read inside the app bundle"));
        Assert.Equal(InstallMethod.Script, method);
    }

    [Fact]
    public void A_missing_or_unreadable_file_reads_as_a_download()
    {
        using var dir = new TempDir();
        Assert.Equal(InstallMethod.Download, InstallMethods.Detect(dir.Path, isMac: false));
        File.WriteAllText(dir.Combine("install-method"), "winget\n");
        Assert.Equal(InstallMethod.Winget, InstallMethods.Detect(dir.Path, isMac: false));
        File.WriteAllText(dir.Combine("install-method"), new string('x', 200));
        Assert.Equal(InstallMethod.Download, InstallMethods.Detect(dir.Path, isMac: false));
    }

    [Theory]
    [InlineData(InstallMethod.Winget, false, false, "winget upgrade MohammadKShah.StickyAIUsage", "PowerShell")]
    [InlineData(InstallMethod.Scoop, false, false, "scoop update sticky-ai-usage", "PowerShell")]
    [InlineData(InstallMethod.Script, false, false,
        "irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1 | iex", "PowerShell")]
    [InlineData(InstallMethod.Script, false, true,
        "& ([scriptblock]::Create((irm https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.ps1))) -Prerelease",
        "PowerShell")]
    [InlineData(InstallMethod.Script, true, false,
        "curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh", "Terminal")]
    [InlineData(InstallMethod.Script, true, true,
        "curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh -s -- --prerelease",
        "Terminal")]
    [InlineData(InstallMethod.Setup, false, false, null, null)]
    [InlineData(InstallMethod.Download, false, false, null, null)]
    public void Each_way_has_its_command(InstallMethod method, bool isMac, bool preRelease, string? command, string? place)
    {
        Assert.Equal(command, InstallMethods.UpdateCommand(method, isMac, preRelease));
        Assert.Equal(place, InstallMethods.CommandPlace(method, isMac));
    }

    [Fact]
    public void The_commands_match_the_README_and_the_packages()
    {
        var root = RepoRoot();
        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        Assert.Contains(InstallMethods.UpdateCommand(InstallMethod.Script, false, false)!, readme);
        Assert.Contains(InstallMethods.UpdateCommand(InstallMethod.Script, true, false)!, readme);
        Assert.True(File.Exists(Path.Combine(root, "packaging", "scoop", InstallMethods.ScoopName + ".json.template")));
        Assert.True(Directory.Exists(Path.Combine(root, "packaging", "winget", "manifests", "m", "MohammadKShah", "StickyAIUsage")));
        Assert.Equal("MohammadKShah.StickyAIUsage", InstallMethods.WingetId);
    }

    /// <summary>Each installer leaves the word the app reads: the Setup "setup" (winget's switch "winget"), install.ps1 "script".</summary>
    [Fact]
    public void Every_installer_writes_the_word_the_app_reads()
    {
        var root = RepoRoot();
        var setup = File.ReadAllText(Path.Combine(root, "installer", "windows", "setup.iss"));
        Assert.Contains("{app}\\" + InstallMethods.FileName, setup);
        Assert.Contains("{param:CHANNEL|setup}", setup);
        var winget = Directory.GetFiles(Path.Combine(root, "packaging", "winget"), "*.installer.yaml.template", SearchOption.AllDirectories);
        Assert.Contains("/CHANNEL=winget", File.ReadAllText(Assert.Single(winget)));
        var script = File.ReadAllText(Path.Combine(root, "scripts", "install.ps1"));
        Assert.Contains($"'{InstallMethods.FileName}'", script);
        Assert.Contains("'script'", script);
    }

    internal static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "StickyAIusage.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}

/// <summary>The daily "is there a newer version?" check: when it asks, what it asks,
/// what it keeps, what it shows, and that nothing asks GitHub while Check for updates is off.</summary>
public class UpdateCheckerTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly FakeReleases _github = new();
    private readonly List<string> _log = [];
    private DateTimeOffset _now = new(2026, 12, 3, 9, 0, 0, TimeSpan.Zero);

    public void Dispose() => _dir.Dispose();

    private UpdateChecker Checker(string running = "2.0.0", InstallMethod method = InstallMethod.Winget, bool isMac = false) =>
        new(new HttpClient(_github), _dir.Path, () => _now, SemanticVersion.Parse(running), method, isMac) { Log = _log.Add };

    private static string Releases(params (string Tag, bool PreRelease, bool Draft)[] releases) =>
        JsonSerializer.Serialize(releases.Select(r => new Dictionary<string, object>
        {
            ["tag_name"] = r.Tag, ["prerelease"] = r.PreRelease, ["draft"] = r.Draft,
            ["html_url"] = "https://example.invalid/never-opened", ["body"] = "notes"
        }));

    private string StateFile => _dir.Combine(UpdateChecker.StateFileName);

    [Fact]
    public async Task A_newer_stable_release_is_shown_with_the_command_for_this_install()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false), ("v2.0.0", false, false)), etag: "\"abc\"");
        var checker = Checker();

        Assert.Equal(UpdateCheckOutcome.NewerFound, await checker.CheckAsync(CancellationToken.None));

        var notice = checker.Notice!;
        Assert.Equal("2.0.1", notice.Version.ToString());
        Assert.Equal("Update available: v2.0.1 — what's new", notice.LinkText);
        Assert.Equal("https://github.com/mohammad-k-shah/stickyAIusage/releases/tag/v2.0.1", notice.ReleaseUrl);
        Assert.Equal("winget upgrade MohammadKShah.StickyAIUsage", notice.Command);
        Assert.Equal("To update, run this in PowerShell:", notice.HowText);
        Assert.Equal("Version 2.0.1 is out (this is 2.0.0). To update, run in PowerShell: winget upgrade MohammadKShah.StickyAIUsage",
            notice.HealthDetail);
        Assert.Equal("Updates: version 2.0.1 is out (this is 2.0.0).", Assert.Single(_log));
        Assert.Equal(_now, checker.LastChecked);
    }

    [Fact]
    public async Task What_is_asked_names_the_app_and_nothing_else()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.0", false, false)));
        await Checker().CheckAsync(CancellationToken.None);

        var request = Assert.Single(_github.Requests);
        Assert.Equal(UpdateChecker.DefaultSource, request.Uri);
        Assert.Equal("https://api.github.com/repos/mohammad-k-shah/stickyAIusage/releases?per_page=10", request.Uri.ToString());
        Assert.Equal("StickyAIusage", request.UserAgent);
        Assert.Equal("application/vnd.github+json", request.Accept);
        Assert.Null(request.IfNoneMatch);
        Assert.False(request.HasAuthorization);
        Assert.Empty(request.Uri.Query.Replace("?per_page=10", ""));
    }

    [Fact]
    public async Task The_same_version_and_an_older_one_are_not_updates()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.0", false, false), ("v1.0.0", false, false)));
        var checker = Checker();
        Assert.Equal(UpdateCheckOutcome.UpToDate, await checker.CheckAsync(CancellationToken.None));
        Assert.Null(checker.Notice);
        Assert.Equal("Updates: this version (2.0.0) is the newest.", Assert.Single(_log));

        Assert.Null(Checker(running: "2.1.0").Notice);
    }

    [Fact]
    public async Task A_stable_build_ignores_pre_releases_and_drafts()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.1.0-beta.1", true, false), ("v2.2.0", false, true),
            ("v2.1.0", true, false), ("v2.0.0", false, false)));
        var checker = Checker();

        Assert.Equal(UpdateCheckOutcome.UpToDate, await checker.CheckAsync(CancellationToken.None));
        Assert.Null(checker.Notice);
    }

    [Fact]
    public async Task A_pre_release_build_hears_of_the_next_pre_release_and_of_the_release()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.0-beta.2", true, false), ("v2.0.0-beta.1", true, false)));
        var beta = Checker(running: "2.0.0-beta.1", method: InstallMethod.Script);
        await beta.CheckAsync(CancellationToken.None);
        Assert.Equal("2.0.0-beta.2", beta.Notice!.Version.ToString());
        Assert.Equal("https://github.com/mohammad-k-shah/stickyAIusage/releases/tag/v2.0.0-beta.2", beta.Notice.ReleaseUrl);
        Assert.EndsWith("install.ps1))) -Prerelease", beta.Notice.Command);

        // The release itself outranks every beta, and is updated to without -Prerelease.
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.0", false, false), ("v2.0.0-beta.2", true, false)));
        _now += UpdateChecker.Interval;
        var later = Checker(running: "2.0.0-beta.2", method: InstallMethod.Script, isMac: true);
        await later.CheckAsync(CancellationToken.None);
        Assert.Equal("2.0.0", later.Notice!.Version.ToString());
        Assert.Equal("curl -fsSL https://raw.githubusercontent.com/mohammad-k-shah/stickyAIusage/main/scripts/install.sh | sh",
            later.Notice.Command);
        Assert.Equal("To update, run this in Terminal:", later.Notice.HowText);
    }

    [Fact]
    public async Task Malformed_tags_are_skipped_and_a_wrong_answer_changes_nothing()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)), etag: "\"first\"");
        var checker = Checker();
        await checker.CheckAsync(CancellationToken.None);

        _github.Respond(HttpStatusCode.OK, Releases(("nightly", false, false), ("v3", false, false), ("v2.0.2+x", false, false)));
        _now += UpdateChecker.Interval;
        Assert.Equal(UpdateCheckOutcome.UpToDate, await checker.CheckAsync(CancellationToken.None));
        Assert.Null(checker.Notice);

        foreach (var wrong in new[] { "{\"message\":\"Not Found\"}", "not json", "[1, \"x\", null]" })
        {
            _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)));
            await checker.CheckAsync(CancellationToken.None);
            _log.Clear();
            _github.Respond(HttpStatusCode.OK, wrong);
            var outcome = await checker.CheckAsync(CancellationToken.None);
            Assert.Equal(wrong.StartsWith('[') ? UpdateCheckOutcome.UpToDate : UpdateCheckOutcome.Invalid, outcome);
            if (outcome == UpdateCheckOutcome.Invalid)
            {
                Assert.Equal("2.0.1", checker.Notice!.Version.ToString());
                Assert.Equal("Updates: check failed: the answer isn't a list of releases.", Assert.Single(_log));
            }
        }
    }

    [Fact]
    public async Task Not_modified_keeps_the_last_answer_and_sends_its_etag()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)), etag: "\"abc\"");
        await Checker().CheckAsync(CancellationToken.None);

        // The next day, a new start: the answer and its ETag come from the file.
        _now += UpdateChecker.Interval + UpdateChecker.StartDelay;
        _github.Respond(HttpStatusCode.NotModified, "");
        _log.Clear();
        var next = Checker();
        Assert.Equal("2.0.1", next.Notice!.Version.ToString());
        Assert.Equal(UpdateCheckOutcome.NotModified, await next.CheckAsync(CancellationToken.None));

        Assert.Equal("\"abc\"", _github.Requests[^1].IfNoneMatch);
        Assert.Equal("2.0.1", next.Notice!.Version.ToString());
        Assert.Equal(_now, next.LastChecked);
        Assert.Equal("Updates: no new release since the last check; version 2.0.1 is out.", Assert.Single(_log));
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, UpdateCheckOutcome.RateLimited, "GitHub declined (HTTP 403")]
    [InlineData(HttpStatusCode.TooManyRequests, UpdateCheckOutcome.RateLimited, "GitHub declined (HTTP 429")]
    [InlineData(HttpStatusCode.NotFound, UpdateCheckOutcome.Failed, "check failed: HTTP 404")]
    [InlineData(HttpStatusCode.InternalServerError, UpdateCheckOutcome.Failed, "check failed: HTTP 500")]
    public async Task Refusals_change_nothing_and_log_one_line(HttpStatusCode status, UpdateCheckOutcome expected, string line)
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)), etag: "\"abc\"");
        var checker = Checker();
        await checker.CheckAsync(CancellationToken.None);
        _log.Clear();

        _now += UpdateChecker.Interval;
        _github.Respond(status, "{\"message\":\"API rate limit exceeded\"}");
        Assert.Equal(expected, await checker.CheckAsync(CancellationToken.None));

        Assert.Equal("2.0.1", checker.Notice!.Version.ToString());
        Assert.Contains(line, Assert.Single(_log));
        Assert.EndsWith("trying again in a day.", _log[0]);
        // Tried: the next try is a day later, not on the next tick.
        Assert.Equal(_now, checker.LastChecked);
        Assert.False(checker.IsDue());
        Assert.Contains("\"etag\":\"\\u0022abc\\u0022\"", File.ReadAllText(StateFile));
    }

    [Fact]
    public async Task Offline_changes_nothing_and_logs_one_line()
    {
        _github.Throw = new HttpRequestException("No such host is known.");
        var checker = Checker();

        Assert.Equal(UpdateCheckOutcome.Failed, await checker.CheckAsync(CancellationToken.None));
        Assert.Null(checker.Notice);
        Assert.Equal("Updates: check failed: HttpRequestException: No such host is known.; trying again in a day.", Assert.Single(_log));

        _github.Throw = new TaskCanceledException("timeout");
        _now += UpdateChecker.Interval;
        _log.Clear();
        Assert.Equal(UpdateCheckOutcome.Failed, await checker.CheckAsync(CancellationToken.None));
        Assert.Equal("Updates: check failed: GitHub didn't answer in time; trying again in a day.", Assert.Single(_log));
    }

    [Fact]
    public async Task An_answer_over_a_megabyte_is_refused()
    {
        _github.Respond(HttpStatusCode.OK, "[" + new string(' ', UpdateChecker.MaxBytes) + "]");
        var checker = Checker();
        Assert.Equal(UpdateCheckOutcome.Invalid, await checker.CheckAsync(CancellationToken.None));
        Assert.Contains("too large", Assert.Single(_log));
    }

    [Fact]
    public void Not_in_the_first_minute_then_once_a_day()
    {
        var checker = Checker();
        Assert.False(checker.IsDue());
        _now += UpdateChecker.StartDelay - TimeSpan.FromSeconds(1);
        Assert.False(checker.IsDue());
        _now += TimeSpan.FromSeconds(1);
        Assert.True(checker.IsDue());

        File.WriteAllText(StateFile, $"{{\"schema\":1,\"checkedAt\":\"{_now.AddHours(-23):O}\"}}");
        var recent = Checker();
        _now += UpdateChecker.StartDelay;
        Assert.False(recent.IsDue());
        _now += TimeSpan.FromHours(1);
        Assert.True(recent.IsDue());
    }

    [Fact]
    public void A_check_stamped_in_the_future_means_the_clock_went_back()
    {
        File.WriteAllText(StateFile, $"{{\"schema\":1,\"checkedAt\":\"{_now.AddDays(3):O}\"}}");
        var checker = Checker();
        _now += UpdateChecker.StartDelay;
        Assert.True(checker.IsDue());
    }

    [Fact]
    public async Task Due_checks_run_in_the_background_one_at_a_time()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)));
        _github.Gate = new TaskCompletionSource();
        var checker = Checker();
        _now += UpdateChecker.StartDelay;

        checker.RefreshIfDue();
        checker.RefreshIfDue();
        await _github.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(checker.IsDue());
        _github.Gate.SetResult();
        for (var i = 0; i < 200 && checker.Notice is null; i++)
            await Task.Delay(10);

        Assert.Single(_github.Requests);
        Assert.Equal("2.0.1", checker.Notice!.Version.ToString());
    }

    [Fact]
    public void A_build_without_a_release_version_never_asks()
    {
        var checker = new UpdateChecker(new HttpClient(_github), _dir.Path, () => _now, running: null, InstallMethod.Download,
            isMac: false) { Log = _log.Add };
        _now += UpdateChecker.Interval;

        Assert.False(checker.IsDue());
        checker.RefreshIfDue();
        Assert.Empty(_github.Requests);
        Assert.Null(checker.Notice);
    }

    [Fact]
    public void After_dispose_a_due_check_starts_nothing()
    {
        var checker = Checker();
        _now += UpdateChecker.StartDelay;
        Assert.True(checker.IsDue());

        checker.Dispose();
        checker.RefreshIfDue();
        checker.Dispose();

        Assert.Empty(_github.Requests);
    }

    [Theory]
    [InlineData("{\"schema\":2,\"checkedAt\":\"2026-12-03T08:00:00Z\",\"newestStable\":\"v9.0.0\"}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schema\":1,\"newestStable\":\"v9.0.0-beta.1\"}")]
    [InlineData("{\"schema\":\"1\",\"newestStable\":\"v9.0.0\"}")]
    public void A_state_file_of_another_schema_or_shape_counts_as_no_check(string content)
    {
        File.WriteAllText(StateFile, content);
        var checker = Checker();
        Assert.Null(checker.Notice);
        _now += UpdateChecker.StartDelay;
        Assert.True(checker.IsDue());
    }

    [Fact]
    public async Task The_state_file_is_small_and_names_no_one()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false), ("v2.1.0-beta.1", true, false)), etag: "W/\"x\"");
        await Checker().CheckAsync(CancellationToken.None);

        using var json = JsonDocument.Parse(File.ReadAllText(StateFile));
        var names = json.RootElement.EnumerateObject().Select(p => p.Name).ToList();
        Assert.Equal(["schema", "checkedAt", "etag", "newest", "newestIsPreRelease", "newestStable"], names);
        Assert.Equal("v2.1.0-beta.1", json.RootElement.GetProperty("newest").GetString());
        Assert.Equal("v2.0.1", json.RootElement.GetProperty("newestStable").GetString());
        Assert.Equal("W/\"x\"", json.RootElement.GetProperty("etag").GetString());
    }

    [Fact]
    public void The_self_test_fails_a_build_whose_version_the_check_cannot_compare()
    {
        var failure = Assert.Throws<SelfTestFailure>(() => SelfTest.CheckUpdates(null, "unknown version", _dir.Path, isMac: false));
        Assert.Contains("\"unknown version\" isn't a release version", failure.Message);

        Assert.StartsWith("2.0.0-beta.1 is a release version; a sample list's v2.0.1 reads as an update (winget upgrade",
            SelfTest.CheckUpdates(SemanticVersion.Parse("2.0.0-beta.1"), "2.0.0-beta.1", _dir.Combine("beta"), isMac: false));
        // A Mac names its own way: the install script (seen on a Mac: the line named winget).
        Assert.Contains("(curl -fsSL", SelfTest.CheckUpdates(SemanticVersion.Parse("2.0.0"), "2.0.0", _dir.Combine("mac"), isMac: true));
        Assert.Empty(_github.Requests);
    }

    // --- Wiring: built only with Check for updates on; each tick asks it; the summary carries its notice ---

    private static (IClaudeUsageSource, IReadOnlyList<IDisposable>) NoEndpoint(Func<DateTimeOffset> clock) =>
        throw new InvalidOperationException("Not used by these tests.");

    [Fact]
    public async Task With_Check_for_updates_off_the_check_is_never_built_and_nothing_asks_GitHub()
    {
        var settings = new AppSettings();
        Assert.False(settings.CheckForUpdates);

        using var services = AppServices.Create(settings, new ClaudeSessionLogSource(_dir.Combine("projects")),
            _dir.Combine("feed"), watchFeed: false, NoEndpoint,
            createUpdateChecker: _ => throw new InvalidOperationException("built with Check for updates off"));
        _now += UpdateChecker.Interval;
        var summary = await services.Feed.GetSummaryAsync();

        Assert.Null(services.AppUpdates);
        Assert.Null(summary.Update);
        Assert.Empty(_github.Requests);
    }

    [Fact]
    public async Task With_it_on_a_tick_starts_the_check_and_a_later_summary_carries_the_notice()
    {
        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)));
        using var services = AppServices.Create(new AppSettings { CheckForUpdates = true, ClaudeSource = "off" },
            new ClaudeSessionLogSource(_dir.Combine("projects")), _dir.Combine("feed"), watchFeed: false, NoEndpoint,
            createUpdateChecker: _ =>
            {
                var checker = Checker();
                return (checker, [checker]);
            });
        _now += UpdateChecker.StartDelay;

        await services.Feed.GetSummaryAsync();
        for (var i = 0; i < 200 && services.AppUpdates!.Notice is null; i++)
            await Task.Delay(10);
        var summary = await services.Feed.GetSummaryAsync();

        Assert.Single(_github.Requests);
        Assert.Equal("Update available: v2.0.1 — what's new", summary.Update!.LinkText);
    }

    [Fact]
    public async Task Health_says_what_the_check_found()
    {
        var off = HealthCheck.Build(HealthCheckTests.Healthy()).Single(i => i.Id == "update");
        Assert.Equal(HealthStatus.Info, off.Status);
        Assert.StartsWith("Off: the app makes no network request", off.Detail);

        var notAsked = Item(HealthCheckTests.Healthy() with { CheckForUpdates = true, RunningVersion = "2.0.0" });
        Assert.Equal(HealthStatus.Info, notAsked.Status);
        Assert.Contains("Not asked yet", notAsked.Detail);

        var noVersion = Item(HealthCheckTests.Healthy() with { CheckForUpdates = true });
        Assert.Contains("has no release version", noVersion.Detail);

        var now = HealthCheckTests.Healthy().Now;
        var upToDate = Item(HealthCheckTests.Healthy() with
        {
            CheckForUpdates = true, RunningVersion = "2.0.0", UpdateCheckedAt = now - TimeSpan.FromHours(3)
        });
        Assert.Equal(HealthStatus.Ok, upToDate.Status);
        Assert.Equal("On: GitHub was asked 3h ago; 2.0.0 is the newest version.", upToDate.Detail);

        _github.Respond(HttpStatusCode.OK, Releases(("v2.0.1", false, false)));
        var checker = Checker(method: InstallMethod.Setup);
        await checker.CheckAsync(CancellationToken.None);
        var found = Item(HealthCheckTests.Healthy() with
        {
            CheckForUpdates = true, RunningVersion = "2.0.0", UpdateCheckedAt = now, Update = checker.Notice
        });
        Assert.Equal(HealthStatus.Warn, found.Status);
        Assert.Equal("Version 2.0.1 is out (this is 2.0.0). Download it from the release page.", found.Detail);
        Assert.Equal(HealthActionIds.OpenReleases, found.ActionId);
        Assert.Equal("What's new", found.ActionLabel);

        static HealthItem Item(HealthFacts facts) => HealthCheck.Build(facts).Single(i => i.Id == "update");
    }

    private sealed record SeenRequest(Uri Uri, string UserAgent, string? Accept, string? IfNoneMatch, bool HasAuthorization);

    private sealed class FakeReleases : HttpMessageHandler
    {
        private HttpStatusCode _status = HttpStatusCode.NotFound;
        private string _body = "";
        private string? _etag;

        public List<SeenRequest> Requests { get; } = [];
        public Exception? Throw { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Respond(HttpStatusCode status, string body, string? etag = null) => (_status, _body, _etag) = (status, body, etag);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (Requests)
                Requests.Add(new SeenRequest(request.RequestUri!, request.Headers.UserAgent.ToString(),
                    request.Headers.Accept.FirstOrDefault()?.MediaType, request.Headers.IfNoneMatch.FirstOrDefault()?.ToString(),
                    request.Headers.Authorization is not null));
            Started.TrySetResult();
            if (Gate is { } gate)
                await gate.Task.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
            if (Throw is { } error)
                throw error;
            var response = new HttpResponseMessage(_status) { Content = new StringContent(_body) };
            if (_etag is not null)
                response.Headers.ETag = System.Net.Http.Headers.EntityTagHeaderValue.Parse(_etag);
            return response;
        }
    }
}
