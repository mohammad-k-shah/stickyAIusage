using System.Diagnostics;
using System.IO;
using System.Text.Json;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Runs `git` (and, if present, `gh`) against a session's working directory to
/// fill in the git/PR/CI block. Every external
/// process call is wrapped defensively: missing `git`/`gh`, a non-repo folder,
/// or a slow/hung process must never crash or freeze the UI thread - callers
/// should invoke this off the UI thread and it applies its own short timeouts.
/// </summary>
public static class GitInfoReader
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(3);

    public static GitInfo? TryRead(string workingDirectory)
    {
        if (!Directory.Exists(workingDirectory))
            return null;

        // One call yields branch, SHA, ahead/behind and every file's state
        // (previously five separate git invocations).
        var status = RunGit(workingDirectory, "status --porcelain=v2 --branch");
        if (status is null)
            return null; // Not a git repo, or git isn't on PATH - not an error.

        var parsed = ParseStatusV2(status);
        var (insertions, deletions) = ParseNumstat(RunGit(workingDirectory, "diff --numstat HEAD") ?? string.Empty);
        var topLevel = RunGit(workingDirectory, "rev-parse --show-toplevel");
        var remotes = ParseRemotes(RunGit(workingDirectory, "remote -v") ?? string.Empty);

        var (originOwner, originRepo) = ParseOwnerRepo(remotes.GetValueOrDefault("origin"));
        var (upstreamOwner, upstreamRepo) = ParseOwnerRepo(remotes.GetValueOrDefault("upstream"));
        var rootName = topLevel is null ? null : Path.GetFileName(topLevel.TrimEnd('/', '\\'));

        var info = new GitInfo
        {
            Branch = parsed.Branch,
            Sha = parsed.Sha,
            AheadCount = parsed.Ahead,
            BehindCount = parsed.Behind,
            StagedCount = parsed.Staged,
            UnstagedCount = parsed.Unstaged,
            UntrackedCount = parsed.Untracked,
            ConflictCount = parsed.Conflicts,
            Insertions = insertions,
            Deletions = deletions,
            RootDirectoryName = rootName,
            OriginOwner = originOwner,
            OriginRepo = originRepo,
            UpstreamOwner = upstreamOwner,
            UpstreamRepo = upstreamRepo,
            IsFork = upstreamOwner is not null && originOwner is not null && upstreamOwner != originOwner,
            WorktreeName = rootName
        };

        return TryEnrichWithGitHub(workingDirectory, info);
    }

    /// <summary>Adds PR/CI info via `gh` if it's installed and authenticated
    /// (optional, hidden if `gh` isn't available). Uses `gh`'s raw
    /// --json output parsed with System.Text.Json rather than a jq filter
    /// string, to avoid fragile nested-quoting across the process boundary.</summary>
    private static GitInfo TryEnrichWithGitHub(string workingDirectory, GitInfo info)
    {
        var prJson = RunProcess(workingDirectory, "gh", "pr view --json url,state,statusCheckRollup");
        if (prJson is null)
            return info;

        try
        {
            using var doc = JsonDocument.Parse(prJson);
            var root = doc.RootElement;

            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            var state = root.TryGetProperty("state", out var s) ? s.GetString() : null;

            string? ciStatus = null;
            if (root.TryGetProperty("statusCheckRollup", out var checks) &&
                checks.ValueKind == JsonValueKind.Array &&
                checks.GetArrayLength() > 0)
            {
                var first = checks[0];
                ciStatus = first.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String
                    ? c.GetString()
                    : (first.TryGetProperty("state", out var st) ? st.GetString() : null);
            }

            return info with { PullRequestUrl = url, PullRequestStatus = state, CiStatus = ciStatus };
        }
        catch
        {
            return info; // Unexpected gh output shape - skip PR/CI enrichment, keep the rest.
        }
    }

    private static string? RunGit(string workingDirectory, string arguments) =>
        RunProcess(workingDirectory, "git", arguments);

    internal static string? RunProcess(string workingDirectory, string fileName, string arguments)
    {
        try
        {
            var psi = new ProcessStartInfo(fileName, arguments)
            {
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            // Don't take index.lock for status's opportunistic index refresh -
            // a background poller must never make the user's own git commands
            // fail with "another git process seems to be running".
            psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";

            using var process = Process.Start(psi);
            if (process is null) return null;

            // Drain both pipes concurrently *before* waiting. Waiting first
            // deadlocks once a command writes more than the pipe buffer
            // (~4KB): git blocks on the full pipe, the wait times out, and the
            // result is lost - e.g. `git status` in a repo with a few hundred
            // untracked files read as "clean".
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit((int)ProcessTimeout.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
                return null;
            }

            Task.WaitAll(stdout, stderr);
            if (process.ExitCode != 0) return null;

            var output = stdout.Result.Trim();
            return string.IsNullOrEmpty(output) ? null : output;
        }
        catch
        {
            // fileName not found on PATH, or any other failure launching it -
            // treat as "tool not available", never throw.
            return null;
        }
    }

    internal sealed record StatusV2(
        string? Branch, string? Sha, int Ahead, int Behind,
        int Staged, int Unstaged, int Untracked, int Conflicts);

    /// <summary>Parses `git status --porcelain=v2 --branch`
    /// (https://git-scm.com/docs/git-status#_porcelain_format_version_2).</summary>
    internal static StatusV2 ParseStatusV2(string output)
    {
        string? branch = null, sha = null;
        int ahead = 0, behind = 0, staged = 0, unstaged = 0, untracked = 0, conflicts = 0;

        foreach (var rawLine in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.TrimEnd('\r');
            if (line.StartsWith("# branch.oid "))
            {
                var oid = line["# branch.oid ".Length..];
                sha = oid == "(initial)" ? null : oid[..Math.Min(7, oid.Length)];
            }
            else if (line.StartsWith("# branch.head "))
            {
                branch = line["# branch.head ".Length..];
            }
            else if (line.StartsWith("# branch.ab "))
            {
                foreach (var part in line["# branch.ab ".Length..].Split(' '))
                {
                    if (part.StartsWith('+')) int.TryParse(part[1..], out ahead);
                    else if (part.StartsWith('-')) int.TryParse(part[1..], out behind);
                }
            }
            else if (line.StartsWith("1 ") || line.StartsWith("2 "))
            {
                // "1 XY ..." ordinary / "2 XY ..." renamed; '.' means unchanged.
                if (line.Length < 4) continue;
                if (line[2] != '.') staged++;
                if (line[3] != '.') unstaged++;
            }
            else if (line.StartsWith("u "))
            {
                conflicts++;
            }
            else if (line.StartsWith("? "))
            {
                untracked++;
            }
        }

        return new StatusV2(branch, sha, ahead, behind, staged, unstaged, untracked, conflicts);
    }

    private static (int insertions, int deletions) ParseNumstat(string numstat)
    {
        int insertions = 0, deletions = 0;
        foreach (var line in numstat.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var cols = line.Split('\t');
            if (cols.Length < 2) continue;
            if (int.TryParse(cols[0], out var ins)) insertions += ins;
            if (int.TryParse(cols[1], out var del)) deletions += del;
        }
        return (insertions, deletions);
    }

    /// <summary>Parses `git remote -v` ("name\turl (fetch)") into name → url.</summary>
    private static Dictionary<string, string> ParseRemotes(string output)
    {
        var remotes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            if (tab <= 0) continue;
            var url = line[(tab + 1)..].Trim();
            var space = url.LastIndexOf(' ');
            if (space > 0) url = url[..space];
            remotes.TryAdd(line[..tab], url);
        }
        return remotes;
    }

    private static (string? owner, string? repo) ParseOwnerRepo(string? remoteUrl)
    {
        if (remoteUrl is null) return (null, null);

        // Handles both "git@github.com:owner/repo.git" and
        // "https://github.com/owner/repo.git" forms.
        var trimmed = remoteUrl.Trim().TrimEnd('/');
        if (trimmed.EndsWith(".git")) trimmed = trimmed[..^4];

        var separatorIndex = trimmed.LastIndexOfAny(['/', ':']);
        if (separatorIndex < 0) return (null, null);

        var repo = trimmed[(separatorIndex + 1)..];
        var beforeRepo = trimmed[..separatorIndex];
        var ownerSeparator = beforeRepo.LastIndexOfAny(['/', ':']);
        var owner = ownerSeparator < 0 ? beforeRepo : beforeRepo[(ownerSeparator + 1)..];

        return (string.IsNullOrEmpty(owner) ? null : owner, string.IsNullOrEmpty(repo) ? null : repo);
    }
}
