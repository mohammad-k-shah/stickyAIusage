using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class DiagnosticLogTests
{
    [Fact]
    public void A_log_past_the_size_limit_is_moved_aside_and_a_small_one_is_left_alone()
    {
        using var dir = new TempDir();
        var log = dir.Combine("diagnostics.log");
        var old = dir.Combine("diagnostics.old.log");

        File.WriteAllText(log, new string('x', 2000));
        DiagnosticLog.RotateIfLarge(log, maxBytes: 1000);
        Assert.False(File.Exists(log));
        Assert.Equal(2000, new FileInfo(old).Length);

        File.WriteAllText(log, "small");
        DiagnosticLog.RotateIfLarge(log, maxBytes: 1000);
        Assert.Equal("small", File.ReadAllText(log));
    }

    [Fact]
    public void The_home_folder_is_written_as_a_tilde()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var message = $"Could not read {Path.Combine(home, ".claude", ".credentials.json")}";

        Assert.Equal($"Could not read {Path.Combine("~", ".claude", ".credentials.json")}", DiagnosticLog.HideHome(message));
    }

    [Theory]
    // Seen in a Mac log: Claude Code's project folder names carry the home folder, encoded.
    [InlineData("/Users/jane.doe", '/',
        "path=/Users/jane.doe/.claude/projects/-Users-jane-doe-Documents-Projects-App/a.jsonl",
        "path=~/.claude/projects/~-Documents-Projects-App/a.jsonl")]
    [InlineData(@"C:\Users\Jane", '\\',
        @"path=C:\Users\Jane\.claude\projects\C--Users-jane-dev-app\b.jsonl",
        @"path=~\.claude\projects\~-dev-app\b.jsonl")]
    [InlineData("/home/jane", '/', "project -home-jane", "project -home-jane")] // not followed by a folder: left alone
    [InlineData("/Users/jane", '/', "-Users-janet-dev and /Users/janet/x", "-Users-janet-dev and /Users/janet/x")] // another account
    public void The_home_folder_is_hidden_in_Claude_Code_s_project_folder_names_too(string home, char separator, string line,
        string expected) =>
        Assert.Equal(expected, DiagnosticLog.HideHome(line, home, separator));
}
