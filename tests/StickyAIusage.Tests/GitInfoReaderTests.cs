using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class GitInfoReaderTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Git(string args) =>
        GitInfoReader.RunProcess(_dir.Path, "git", args) ?? string.Empty;

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

        var info = GitInfoReader.TryRead(_dir.Path);

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
        Assert.Null(GitInfoReader.TryRead(_dir.Path));

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
}
