using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class GitInfoReaderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Git(string args) =>
        GitInfoReader.RunProcess(_dir.Path, "git", args.Split(' ')) ?? string.Empty;

    private static bool GitIsInstalled() =>
        GitInfoReader.RunProcess(Path.GetTempPath(), "git", ["--version"]) is not null;

    [Fact]
    public void Status_output_larger_than_the_pipe_buffer_is_read_in_full()
    {
        Git("init");
        Git("config user.email test@example.com");
        Git("config user.name Test");
        File.WriteAllText(_dir.Combine("tracked.txt"), "one\n");
        Git("add tracked.txt");
        Git("commit -m init");

        File.WriteAllText(_dir.Combine("tracked.txt"), "one\ntwo\nthree\n");  // unstaged, +2 lines
        File.WriteAllText(_dir.Combine("staged.txt"), "new\n");
        Git("add staged.txt");                                                // staged
        // ~2000 untracked files -> tens of KB of `git status` output, far past
        // the ~4KB pipe buffer that made the old reader time out and report "clean".
        for (var i = 0; i < 2000; i++)
            File.WriteAllText(_dir.Combine($"untracked_file_number_{i:D5}.txt"), "x");

        var info = GitInfoReader.Default.TryRead(_dir.Path);

        Assert.NotNull(info);
        Assert.Equal(2000, info.UntrackedCount);
        Assert.Equal(1, info.StagedCount);
        Assert.Equal(1, info.UnstagedCount);
        Assert.False(info.IsClean);
        Assert.Equal(3, info.Insertions);  // numstat HEAD: tracked +2, staged.txt +1
        Assert.False(string.IsNullOrEmpty(info.Branch));
        Assert.Equal(7, info.Sha!.Length);
    }

    [Fact]
    public void Non_repo_directory_returns_null() =>
        Assert.Null(GitInfoReader.Default.TryRead(_dir.Path));

    [Fact]
    public void Porcelain_v2_is_parsed()
    {
        const string output =
            "# branch.oid 1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b\n" +
            "# branch.head feature/x\n" +
            "# branch.upstream origin/feature/x\n" +
            "# branch.ab +3 -2\n" +
            "1 M. N... 100644 100644 100644 aaa bbb staged-only.cs\n" +
            "1 .M N... 100644 100644 100644 aaa bbb unstaged-only.cs\n" +
            "1 MM N... 100644 100644 100644 aaa bbb both.cs\n" +
            "2 R. N... 100644 100644 100644 aaa bbb R100 new.cs\told.cs\n" +
            "u UU N... 100644 100644 100644 100644 aaa bbb ccc conflict.cs\n" +
            "? untracked.cs\n";

        var s = GitInfoReader.ParseStatusV2(output);

        Assert.Equal("feature/x", s.Branch);
        Assert.Equal("1a2b3c4", s.Sha);
        Assert.Equal((3, 2), (s.Ahead, s.Behind));
        Assert.Equal(3, s.Staged);
        Assert.Equal(2, s.Unstaged);
        Assert.Equal(1, s.Conflicts);
        Assert.Equal(1, s.Untracked);
    }

    [Fact]
    public void Repo_without_commits_has_no_sha() =>
        Assert.Null(GitInfoReader.ParseStatusV2("# branch.oid (initial)\n# branch.head main\n").Sha);

    // ------------------------------------------------------------ hardening

    [Fact]
    public void Every_git_call_turns_off_the_repository_s_programs()
    {
        var runner = new FakeRunner(origin: "https://github.com/acme/app.git");

        new GitInfoReader(runner.Run).TryRead(_dir.Path);

        var gitCalls = runner.Calls.Where(c => c.File == "git").ToList();
        Assert.Equal(4, gitCalls.Count);
        Assert.All(gitCalls, c => Assert.Equal(["-c", "core.fsmonitor=false"], c.Arguments.Take(2)));
        var diff = Assert.Single(gitCalls, c => c.Arguments.Contains("diff"));
        Assert.Contains("--no-ext-diff", diff.Arguments);
        Assert.Contains("--no-textconv", diff.Arguments);
    }

    [Fact]
    public void Git_and_gh_never_stop_to_ask()
    {
        var environment = GitInfoReader.QuietEnvironment;
        Assert.Equal("0", environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", environment["GCM_INTERACTIVE"]);
        Assert.Equal("1", environment["GH_PROMPT_DISABLED"]);
        Assert.Equal("1", environment["GH_NO_UPDATE_NOTIFIER"]);
        Assert.Equal("0", environment["GIT_OPTIONAL_LOCKS"]);
    }

    [Fact]
    public void A_repository_s_fsmonitor_command_does_not_run()
    {
        if (!GitIsInstalled())
            return; // nothing to prove without git
        Git("init");
        var marker = _dir.Combine("fsmonitor-ran");
        // git runs core.fsmonitor through its shell on every status.
        GitInfoReader.RunProcess(_dir.Path, "git", ["config", "core.fsmonitor", "touch fsmonitor-ran; false"]);
        Assert.Contains("touch", Git("config --get core.fsmonitor"));

        // Without the flag, the command runs: the check below can see it.
        GitInfoReader.RunProcess(_dir.Path, "git", ["status", "--porcelain=v2"]);
        Assert.True(File.Exists(marker), "control: plain git status should have run the fsmonitor command");
        File.Delete(marker);

        Assert.NotNull(GitInfoReader.Default.ReadLocal(_dir.Path));

        Assert.False(File.Exists(marker));
    }

    // ------------------------------------------------------------------- gh

    [Theory]
    [InlineData("https://gitlab.com/acme/app.git")]
    [InlineData("git@bitbucket.org:acme/app.git")]
    [InlineData("https://dev.azure.com/acme/_git/app")]
    [InlineData(null)]
    public void Gh_is_not_asked_about_a_repository_that_is_not_on_GitHub(string? origin)
    {
        var runner = new FakeRunner(origin);

        var info = new GitInfoReader(runner.Run).TryRead(_dir.Path);

        Assert.NotNull(info);
        Assert.DoesNotContain(runner.Calls, c => c.File == "gh");
        Assert.Null(info.PullRequestUrl);
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git")]
    [InlineData("git@github.com:acme/app.git")]
    [InlineData("ssh://git@ssh.github.com:443/acme/app.git")]
    [InlineData("https://acme.ghe.com/acme/app")]
    public void Gh_is_asked_about_a_GitHub_repository(string origin)
    {
        var runner = new FakeRunner(origin);

        var info = new GitInfoReader(runner.Run).TryRead(_dir.Path);

        Assert.Single(runner.Calls, c => c.File == "gh");
        Assert.Equal("https://github.com/acme/app/pull/7", info!.PullRequestUrl);
        Assert.Equal("OPEN", info.PullRequestStatus);
        Assert.Equal("SUCCESS", info.CiStatus);
    }

    [Theory]
    [InlineData("https://github.com/acme/app.git", "github.com")]
    [InlineData("https://user@github.com:8443/acme/app.git", "github.com")]
    [InlineData("ssh://git@ssh.github.com:443/acme/app.git", "ssh.github.com")]
    [InlineData("git@github.com:acme/app.git", "github.com")]
    [InlineData("github.com:acme/app.git", "github.com")]
    [InlineData("git@gitlab.example.org:group/app.git", "gitlab.example.org")]
    [InlineData(@"C:\repos\app", null)]
    [InlineData("C:/repos/app", null)]
    [InlineData("../app.git", null)]
    [InlineData("/srv/git/app.git", null)]
    [InlineData("file:///srv/git/app.git", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_origin_host_is_read_from_every_url_form(string? url, string? host) =>
        Assert.Equal(host, GitInfoReader.RemoteHost(url));

    [Theory]
    [InlineData("github.com", true)]
    [InlineData("GitHub.com", true)]
    [InlineData("ssh.github.com", true)]
    [InlineData("acme.ghe.com", true)]
    [InlineData("github.acme.internal", true)]
    [InlineData("gitlab.com", false)]
    [InlineData("notgithub.com", false)]
    [InlineData("bitbucket.org", false)]
    [InlineData(null, false)]
    public void GitHub_hosts_are_recognized(string? host, bool expected) =>
        Assert.Equal(expected, GitInfoReader.IsGitHubHost(host));

    // ---------------------------------------------------------------- cache

    [Fact]
    public void The_pull_request_is_asked_for_at_most_every_five_minutes()
    {
        var runner = new FakeRunner("https://github.com/acme/app.git");
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var cache = new GitInfoCache(new GitInfoReader(runner.Run), () => now);

        // The flyout's 15-second refresh for just under five minutes.
        for (var i = 0; i < 20; i++, now = now.AddSeconds(15))
            Assert.Equal("https://github.com/acme/app/pull/7", cache.Get(_dir.Path)!.PullRequestUrl);

        Assert.Equal(1, runner.Count("gh"));
        Assert.Equal(10, runner.Count("status")); // local state every 30 s

        // The loop left the clock five minutes after the first ask.
        cache.Get(_dir.Path);
        Assert.Equal(2, runner.Count("gh"));
    }

    [Fact]
    public void A_new_branch_asks_for_its_pull_request_at_once()
    {
        var runner = new FakeRunner("https://github.com/acme/app.git");
        var now = new DateTimeOffset(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
        var cache = new GitInfoCache(new GitInfoReader(runner.Run), () => now);
        cache.Get(_dir.Path);

        runner.Branch = "feature/next";
        now = now.AddSeconds(31); // the local state is read again
        var info = cache.Get(_dir.Path);

        Assert.Equal("feature/next", info!.Branch);
        Assert.Equal(2, runner.Count("gh"));
    }

    [Fact]
    public void A_failing_reader_gives_no_git_block_and_no_exception()
    {
        var cache = new GitInfoCache(new GitInfoReader((_, _, _) => throw new InvalidOperationException("boom")));
        Assert.Null(cache.Get(_dir.Path));
    }

    /// <summary>Answers like git and gh would for a repository with one
    /// origin, and records each call.</summary>
    private sealed class FakeRunner(string? origin)
    {
        public List<(string File, IReadOnlyList<string> Arguments)> Calls { get; } = [];
        public string Branch { get; set; } = "main";

        public int Count(string what) => Calls.Count(c => c.File == what || c.Arguments.Contains(what));

        public string? Run(string workingDirectory, string fileName, IReadOnlyList<string> arguments)
        {
            Calls.Add((fileName, arguments.ToList()));
            if (fileName == "gh")
                return """{"url":"https://github.com/acme/app/pull/7","state":"OPEN","statusCheckRollup":[{"conclusion":"SUCCESS"}]}""";
            if (arguments.Contains("status"))
                return $"# branch.oid 1a2b3c4d5e6f7a8b9c0d1e2f3a4b5c6d7e8f9a0b\n# branch.head {Branch}\n";
            if (arguments.Contains("remote"))
                return origin is null ? null : $"origin\t{origin} (fetch)\norigin\t{origin} (push)";
            if (arguments.Contains("rev-parse"))
                return "/home/me/app";
            return null;
        }
    }
}
