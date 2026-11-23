using StickyAIusage.Health;

namespace StickyAIusage.Tests;

/// <summary>Connect → Run it for me: the commands for each Claude Code version,
/// quoting, finding <c>claude</c>, and what its output means. A fake command
/// line answers: no test starts a process.</summary>
public class PluginInstallerTests
{
    private const string Source = PluginInstaller.DefaultSource;

    [Fact]
    public void The_shown_and_copied_commands_name_the_plugin_and_the_marketplace()
    {
        Assert.Equal("claude plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage", PluginInstaller.ShellCommand(Source));
        Assert.Equal("/plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage", PluginInstaller.SessionCommand(Source));
        Assert.Equal(ViewModels.ClaudeCommands.InstallPlugin, PluginInstaller.ShellCommand(Source));
    }

    [Fact]
    public void The_marketplace_can_be_pointed_at_a_local_copy_for_testing()
    {
        Assert.Equal(Source, PluginInstaller.Source(_ => null));
        Assert.Equal(Source, PluginInstaller.Source(_ => ""));
        Assert.Equal(@"C:\work\my fork", PluginInstaller.Source(name => name == PluginInstaller.SourceVariable ? @" C:\work\my fork " : null));
        Assert.Equal(@"claude plugin install sticky-usage --marketplace ""C:\work\my fork""", PluginInstaller.ShellCommand(@"C:\work\my fork"));
    }

    [Theory]
    [InlineData("2.1.325", "plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage --json")]
    [InlineData("2.1.318", "plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage --json")]
    [InlineData("2.1.317", "plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage")]
    [InlineData("2.1.292", "plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage")]
    [InlineData("2.1.291", "plugin marketplace add mohammad-k-shah/stickyAIusage|plugin install sticky-usage@sticky-ai-usage")]
    [InlineData(null, "plugin marketplace add mohammad-k-shah/stickyAIusage|plugin install sticky-usage@sticky-ai-usage")]
    public void Older_builds_get_the_two_step_form(string? version, string expected)
    {
        var steps = PluginInstaller.Steps(HealthCheck.ParseVersion(version), Source);
        Assert.Equal(expected, string.Join('|', steps.Select(s => string.Join(' ', s))));
    }

    [Theory]
    [InlineData("2.1.318 (Claude Code)\n", 2, 1, 318)]
    [InlineData("\n2.1.325 (Claude Code)", 2, 1, 325)]
    public void The_version_comes_from_claude_version(string output, int major, int minor, int build) =>
        Assert.Equal(new Version(major, minor, build), PluginInstaller.ParseVersion(output));

    [Fact]
    public void Claude_is_found_on_path_first_then_where_its_installers_put_it()
    {
        const string home = @"C:\Users\pat";
        string? Env(string name) => name switch
        {
            "PATH" => @"C:\Windows\system32;""C:\tools\"";;C:\Users\pat\AppData\Roaming\npm",
            "APPDATA" => @"C:\Users\pat\AppData\Roaming",
            _ => null
        };

        // npm's shim on PATH.
        Assert.Equal(@"C:\Users\pat\AppData\Roaming\npm\claude.cmd",
            PluginInstaller.FindClaude(Env, p => p == @"C:\Users\pat\AppData\Roaming\npm\claude.cmd", isWindows: true, home));
        // The native exe beats the shim in the same folder, and a quoted PATH entry works.
        Assert.Equal(@"C:\tools\claude.exe", PluginInstaller.FindClaude(Env, p => p.StartsWith(@"C:\tools"), isWindows: true, home));
        // Not on PATH: the native installer's folder.
        Assert.Equal(@"C:\Users\pat\.local\bin\claude.exe",
            PluginInstaller.FindClaude(_ => null, p => p == @"C:\Users\pat\.local\bin\claude.exe", isWindows: true, home));
        Assert.Null(PluginInstaller.FindClaude(Env, _ => false, isWindows: true, home));
    }

    [Fact]
    public void A_mac_app_from_finder_with_a_short_path_still_finds_homebrew_and_the_native_install()
    {
        const string home = "/Users/pat";
        string? ShortPath(string name) => name == "PATH" ? "/usr/bin:/bin:/usr/sbin:/sbin" : null;
        Assert.Equal("/opt/homebrew/bin/claude", PluginInstaller.FindClaude(ShortPath, p => p == "/opt/homebrew/bin/claude", isWindows: false, home));
        Assert.Equal("/Users/pat/.local/bin/claude",
            PluginInstaller.FindClaude(ShortPath, p => p == "/Users/pat/.local/bin/claude", isWindows: false, home));
        // A PATH entry wins over the fallbacks.
        Assert.Equal("/usr/bin/claude", PluginInstaller.FindClaude(ShortPath, p => p.EndsWith("claude"), isWindows: false, home));
    }

    [Theory]
    [InlineData("""{"command":"install","outcome":"ok","message":"Successfully installed plugin: sticky-usage@sticky-ai-usage (scope: user)\n9 userConfig options not yet set"}""",
        0, InstallOutcome.Installed, "Successfully installed plugin: sticky-usage@sticky-ai-usage (scope: user)")]
    [InlineData("""{"command":"install","outcome":"ok","message":"Plugin \"sticky-usage@sticky-ai-usage\" is already installed (scope: user)"}""",
        0, InstallOutcome.AlreadyInstalled, "Plugin \"sticky-usage@sticky-ai-usage\" is already installed (scope: user)")]
    [InlineData("""{"command":"install","outcome":"failed","message":"Plugin \"sticky-usage\" not found in marketplace \"sticky-ai-usage\".","failureCode":"not_found"}""",
        1, InstallOutcome.NotFound, "Plugin \"sticky-usage\" not found in marketplace \"sticky-ai-usage\".")]
    [InlineData("""{"command":"install","outcome":"failed","message":"Marketplace source is blocked by your organization's policy (strictKnownMarketplaces)","failureCode":"blocked"}""",
        1, InstallOutcome.Policy, "Marketplace source is blocked by your organization's policy (strictKnownMarketplaces)")]
    [InlineData("""{"command":"install","outcome":"failed","message":"Failed to clone: getaddrinfo ENOTFOUND github.com","failureCode":"git_failed"}""",
        1, InstallOutcome.Offline, "Failed to clone: getaddrinfo ENOTFOUND github.com")]
    [InlineData("""{"command":"install","outcome":"failed","message":"Path does not exist: C:\\nonexistent","failureCode":"invalid_source"}""",
        1, InstallOutcome.Failed, "Path does not exist: C:\\nonexistent")]
    public void Claude_codes_json_line_decides(string output, int exitCode, InstallOutcome outcome, string message)
    {
        var (got, said) = PluginInstaller.Classify(new CliRun(exitCode, "some progress text\n" + output + "\n", "✘ a human line on stderr"));
        Assert.Equal((outcome, message), (got, said));
    }

    [Theory]
    [InlineData(0, "Installing plugin \"sticky-usage@sticky-ai-usage\"...✔ Successfully installed plugin", "", InstallOutcome.Installed)]
    [InlineData(0, "✔ Plugin \"sticky-usage@sticky-ai-usage\" is already installed (scope: user)", "", InstallOutcome.AlreadyInstalled)]
    [InlineData(1, "", "✘ Failed to install plugin \"sticky-usage@sticky-ai-usage\": Plugin \"sticky-usage\" not found in marketplace \"sticky-ai-usage\".", InstallOutcome.NotFound)]
    [InlineData(1, "", "error: managed settings do not allow this marketplace", InstallOutcome.Policy)]
    [InlineData(1, "", "fatal: unable to access 'https://github.com/x/y.git/': Could not resolve host: github.com", InstallOutcome.Offline)]
    [InlineData(1, "", "error: unknown option '--marketplace'", InstallOutcome.Failed)]
    [InlineData(2, "", "", InstallOutcome.Failed)]
    public void Without_json_the_words_and_the_exit_code_decide(int exitCode, string output, string error, InstallOutcome outcome)
    {
        var (got, message) = PluginInstaller.Classify(new CliRun(exitCode, output, error));
        Assert.Equal(outcome, got);
        Assert.False(string.IsNullOrWhiteSpace(message));
        Assert.DoesNotContain("✘", message);
    }

    [Fact]
    public void A_run_that_never_ends_is_a_failure()
    {
        var (outcome, message) = PluginInstaller.Classify(new CliRun(-1, "", "", TimedOut: true));
        Assert.Equal(InstallOutcome.Failed, outcome);
        Assert.Contains("three minutes", message);
    }

    /// <summary>Answers each command from a script, and records what was asked.</summary>
    private sealed class FakeCli(Func<IReadOnlyList<string>, CliRun> answer) : IClaudeCli
    {
        public List<string> Ran { get; } = [];

        public Task<CliRun> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Ran.Add(executable + " " + string.Join(' ', arguments));
            return Task.FromResult(answer(arguments));
        }
    }

    [Fact]
    public async Task A_current_build_installs_with_one_command()
    {
        var cli = new FakeCli(args => args[0] == "--version"
            ? new CliRun(0, "2.1.325 (Claude Code)\n", "")
            : new CliRun(0, """{"outcome":"ok","message":"Successfully installed plugin: sticky-usage@sticky-ai-usage (scope: user)"}""", ""));

        var result = await PluginInstaller.InstallAsync(cli, "claude", Source);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.True(result.Succeeded);
        Assert.Equal(["claude --version", "claude plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage --json"], cli.Ran);
        Assert.Contains("> claude plugin install sticky-usage --marketplace mohammad-k-shah/stickyAIusage --json", result.Transcript);
        Assert.Contains("(exit code 0)", result.Transcript);
    }

    [Fact]
    public async Task An_old_build_adds_the_marketplace_first_even_when_it_is_already_there()
    {
        var cli = new FakeCli(args => args switch
        {
            ["--version"] => new CliRun(0, "2.1.290 (Claude Code)", ""),
            ["plugin", "marketplace", "add", ..] => new CliRun(1, "", "Marketplace 'sticky-ai-usage' is already added"),
            _ => new CliRun(0, "✔ Successfully installed plugin: sticky-usage@sticky-ai-usage", "")
        });

        var result = await PluginInstaller.InstallAsync(cli, "claude", Source);

        Assert.Equal(InstallOutcome.Installed, result.Outcome);
        Assert.Equal(3, cli.Ran.Count);
        Assert.EndsWith("plugin install sticky-usage@sticky-ai-usage", cli.Ran[2]);
    }

    [Fact]
    public async Task A_failed_marketplace_add_stops_before_the_install()
    {
        var cli = new FakeCli(args => args switch
        {
            ["--version"] => new CliRun(1, "", "unknown"),
            ["plugin", "marketplace", "add", ..] => new CliRun(1, "", "fatal: unable to access 'https://github.com/': Could not resolve host"),
            _ => throw new InvalidOperationException("the install must not run")
        });

        var result = await PluginInstaller.InstallAsync(cli, "claude", Source);

        Assert.Equal(InstallOutcome.Offline, result.Outcome);
        Assert.Equal(2, cli.Ran.Count);
    }

    [Fact]
    public async Task Without_claude_nothing_runs_and_the_copy_path_is_offered()
    {
        var cli = new FakeCli(_ => throw new InvalidOperationException("nothing may run"));
        var result = await PluginInstaller.InstallAsync(cli, null, Source);
        Assert.Equal(InstallOutcome.NoCli, result.Outcome);
        Assert.Contains("Copy the command", result.Message);
        Assert.Empty(cli.Ran);
    }

    [Fact]
    public async Task A_command_line_that_cannot_start_is_a_failure_not_a_crash()
    {
        var cli = new FakeCli(_ => throw new System.ComponentModel.Win32Exception("The system cannot find the file specified"));
        var result = await PluginInstaller.InstallAsync(cli, "claude", Source);
        Assert.Equal(InstallOutcome.Failed, result.Outcome);
        Assert.Contains("cannot find the file", result.Message);
    }

    [Fact]
    public void Arguments_are_quoted_only_where_a_shell_would_split_them()
    {
        Assert.Equal("claude plugin install a --marketplace b/c", PluginInstaller.Display(["plugin", "install", "a", "--marketplace", "b/c"]));
        Assert.Equal("claude \"two words\" \"a&b\" \"\"", PluginInstaller.Display(["two words", "a&b", ""]));
    }
}
