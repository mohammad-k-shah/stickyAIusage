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
}
