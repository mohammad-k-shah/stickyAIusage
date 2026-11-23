using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>Where the app's data lives per profile and override, and where
/// Claude Code's folder is found.</summary>
public class AppPathsTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly Dictionary<string, string> _environment = new();

    private string Home => _dir.Combine("home");
    private string LocalAppData => _dir.Combine("local");

    public AppPathsTests()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(LocalAppData);
    }

    public void Dispose() => _dir.Dispose();

    private AppLocations Resolve(bool isMac = false, params string[] args) =>
        AppPaths.Resolve(name => _environment.GetValueOrDefault(name), args, isMac, Home, LocalAppData);

    [Fact]
    public void Default_paths_are_the_v1_ones()
    {
        var windows = Resolve();
        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage"), windows.DataDirectory);
        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage", "logs"), windows.LogDirectory);
        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage", "feed"), windows.FeedDirectory);
        Assert.Equal(Path.Combine(Home, ".claude"), windows.ClaudeDirectory);
        Assert.Equal(Path.Combine(Home, ".codex"), windows.CodexDirectory);
        Assert.True(windows.IsDefaultInstance);
        Assert.Empty(windows.Warnings);
        Assert.Equal("", windows.NameSuffix);
        Assert.Equal([], windows.RelaunchArguments);

        var mac = Resolve(isMac: true);
        Assert.Equal(Path.Combine(Home, "Library", "Application Support", "StickyAIusage"), mac.DataDirectory);
        Assert.Equal(Path.Combine(Home, "Library", "Logs", "StickyAIusage"), mac.LogDirectory);
    }

    [Fact]
    public void A_profile_gets_its_own_folder_lock_and_login_entry_but_shares_the_feed()
    {
        var dev = Resolve(false, "--profile", "dev");

        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage-dev"), dev.DataDirectory);
        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage-dev", "logs"), dev.LogDirectory);
        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage", "feed"), dev.FeedDirectory);
        Assert.Equal("dev", dev.Profile);
        Assert.Equal("dev", dev.InstanceKey);
        Assert.Equal("-dev", dev.NameSuffix);
        Assert.Equal(["--profile", "dev"], dev.RelaunchArguments);
        Assert.Equal("profile dev", dev.InstanceLabel);

        var mac = Resolve(true, "--profile=dev");
        Assert.Equal(Path.Combine(Home, "Library", "Logs", "StickyAIusage-dev"), mac.LogDirectory);
    }

    [Fact]
    public void The_profile_argument_wins_over_the_variable()
    {
        _environment[AppPaths.ProfileVariable] = "beta";
        Assert.Equal("beta", Resolve().Profile);
        Assert.Equal("dev", Resolve(false, "--profile", "dev").Profile);
    }

    [Theory]
    [InlineData("has space")]
    [InlineData("../escape")]
    [InlineData("dev.old")]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456")] // 33 characters
    public void An_invalid_profile_name_is_ignored_and_reported(string name)
    {
        var locations = Resolve(false, "--profile", name);

        Assert.Null(locations.Profile);
        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage"), locations.DataDirectory);
        Assert.Contains(locations.Warnings, w => w.Contains("profile name"));
    }

    [Fact]
    public void The_longest_allowed_profile_name_is_32_characters()
    {
        Assert.True(AppPaths.IsValidProfileName(new string('a', 32)));
        Assert.True(AppPaths.IsValidProfileName("Dev_2-b"));
        Assert.False(AppPaths.IsValidProfileName(""));
        Assert.False(AppPaths.IsValidProfileName(null));
    }

    [Fact]
    public void A_profile_flag_without_a_name_is_ignored()
    {
        var locations = Resolve(false, "--profile", "--other");

        Assert.Null(locations.Profile);
        Assert.Contains(locations.Warnings, w => w.Contains("no profile name"));
        Assert.Null(Resolve(false, "--profile").Profile);
    }

    [Fact]
    public void A_data_folder_override_wins_and_gets_a_lock_of_its_own()
    {
        var custom = _dir.Combine("portable");
        _environment[AppPaths.DataDirectoryVariable] = custom;

        var locations = Resolve(true, "--profile", "dev");

        Assert.Equal(custom, locations.DataDirectory);
        Assert.Equal(Path.Combine(custom, "logs"), locations.LogDirectory);
        Assert.StartsWith("dir-", locations.InstanceKey);
        Assert.Null(locations.RelaunchArguments);
        Assert.Equal("a custom data folder", locations.InstanceLabel);

        // The same folder always maps to the same lock, another folder to another.
        Assert.Equal(locations.InstanceKey, Resolve().InstanceKey);
        _environment[AppPaths.DataDirectoryVariable] = _dir.Combine("portable-2");
        Assert.NotEqual(locations.InstanceKey, Resolve().InstanceKey);
    }

    [Fact]
    public void A_relative_data_folder_is_ignored()
    {
        _environment[AppPaths.DataDirectoryVariable] = "relative/folder";

        var locations = Resolve();

        Assert.Equal(Path.Combine(LocalAppData, "StickyAIusage"), locations.DataDirectory);
        Assert.True(locations.IsDefaultInstance);
        Assert.Contains(locations.Warnings, w => w.Contains(AppPaths.DataDirectoryVariable));
    }

    [Fact]
    public void The_feed_folder_can_be_moved_for_tests()
    {
        var feed = _dir.Combine("feed-elsewhere");
        _environment[AppPaths.FeedDirectoryVariable] = feed;

        Assert.Equal(feed, Resolve(false, "--profile", "dev").FeedDirectory);
    }

    [Fact]
    public void CLAUDE_CONFIG_DIR_is_honoured_when_it_is_an_existing_folder()
    {
        var claude = _dir.Combine("work-claude");
        Directory.CreateDirectory(claude);
        _environment[AppPaths.ClaudeConfigVariable] = claude;

        var locations = Resolve();

        Assert.Equal(claude, locations.ClaudeDirectory);
        Assert.Empty(locations.Warnings);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("relative")]
    public void CLAUDE_CONFIG_DIR_pointing_nowhere_falls_back_to_the_home_folder(string kind)
    {
        _environment[AppPaths.ClaudeConfigVariable] = kind == "missing" ? _dir.Combine("no-such-folder") : "some/relative";

        var locations = Resolve();

        Assert.Equal(Path.Combine(Home, ".claude"), locations.ClaudeDirectory);
        Assert.Single(locations.Warnings, w => w.Contains(AppPaths.ClaudeConfigVariable));
    }

    [Fact]
    public void CODEX_HOME_is_honoured()
    {
        var codex = _dir.Combine("codex");
        _environment[AppPaths.CodexHomeVariable] = codex;

        Assert.Equal(codex, Resolve().CodexDirectory);
    }

    [Fact]
    public void Blank_variables_count_as_unset()
    {
        _environment[AppPaths.ProfileVariable] = "  ";
        _environment[AppPaths.DataDirectoryVariable] = "";
        _environment[AppPaths.ClaudeConfigVariable] = " ";

        var locations = Resolve();

        Assert.True(locations.IsDefaultInstance);
        Assert.Empty(locations.Warnings);
    }

    [Fact]
    public void The_transcript_folder_follows_the_Claude_folder()
    {
        Assert.Equal(Path.Combine(AppPaths.ClaudeDirectory, "projects"), ClaudeSessionLogReader.DefaultProjectsRoot);
        Assert.Equal(Path.Combine(AppPaths.ClaudeDirectory, ".credentials.json"), ClaudeCredentialsReader.CredentialsPath);
    }

    [Fact]
    public void The_test_run_never_uses_the_real_data_folder()
    {
        // Set by TestIsolation before any test runs.
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "stickyaiusage-tests"), AppPaths.DataDirectory);
        Assert.StartsWith(Path.Combine(Path.GetTempPath(), "stickyaiusage-tests"), AppPaths.SettingsPath);
    }
}

public class FileSignalTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    [Fact]
    public void A_second_launch_reaches_the_running_copy()
    {
        var path = _dir.Combine("app.lock.signal");
        using var received = new ManualResetEventSlim();
        using var signal = new FileSignal(path, received.Set);

        Assert.True(FileSignal.Send(path));

        Assert.True(received.Wait(TimeSpan.FromSeconds(10)), "the watcher never saw the signal");
    }

    [Fact]
    public void Sending_creates_a_missing_folder()
    {
        var path = _dir.Combine("not-yet", "app.lock.signal");

        Assert.True(FileSignal.Send(path));
        Assert.True(File.Exists(path));
    }
}
