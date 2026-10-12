using System.IO;
using System.Text;
using System.Text.Json;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>
/// Reads Claude Desktop Code-tab session transcripts from
/// %USERPROFILE%\.claude\projects\&lt;project&gt;\&lt;session-guid&gt;.jsonl - the same
/// files Claude Code CLI writes (Desktop's Code tab writes them too, with
/// entrypoint: "claude-desktop").
///
/// Counting rules, each one found by comparing against Claude Code's own
/// /usage on real transcripts:
/// - Usage is counted once per message.id. Claude Code writes one "assistant"
///   record per content block of a single API response, each repeating the
///   same usage snapshot - summing every line counted one response up to 16x.
/// - isSidechain records (Task/Agent subagent turns) are skipped.
/// - Only records whose own sessionId matches the file's name count. A forked
///   conversation's file also contains the parent session's records copied in
///   verbatim under the parent's sessionId; counting those inflated output
///   tokens ~9-10x and made a 7-minute fork report 6 hours elapsed. If no
///   record in a file matches its name (unexpected shape), every record counts
///   rather than silently reporting an empty session.
/// - Only GUID-named files are sessions. Subagent transcripts live alongside
///   them as subagents/agent-&lt;id&gt;.jsonl and used to show up as
///   "unknown model · $0.00" sessions, sometimes taking over the collapsed strip.
///
/// Known gap: cumulative totals don't account for compaction boundaries, and current-context size is the
/// last counted turn's input side, which can be off right after a compaction.
///
/// Parsing is incremental: each file's parse state is cached, and a later call
/// only reads bytes appended since the previous one. The widget polls every 15
/// seconds, and re-parsing every multi-MB transcript on each tick was a
/// measurable cost.
/// </summary>
public static class ClaudeSessionLogReader
{
    public static string DefaultProjectsRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, TranscriptState> Cache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxCachedTranscripts = 256;

    /// <summary>Returns one SessionInfo per session transcript with activity on
    /// the given local date ("sessions active today"), most recently
    /// active first. Never throws; unreadable files are skipped.</summary>
    public static List<SessionInfo> GetSessionsForDate(DateOnly localDate, string? projectsRoot = null,
        bool onlyWithWork = true) =>
        GetSessionsInRange(localDate, localDate, projectsRoot, onlyWithWork);

    /// <summary>Like GetSessionsForDate, for sessions last active on any local
    /// date from <paramref name="from"/> to <paramref name="to"/> inclusive -
    /// the flyout's "Last 7 days" and similar views.</summary>
    public static List<SessionInfo> GetSessionsInRange(DateOnly from, DateOnly to, string? projectsRoot = null,
        bool onlyWithWork = true)
    {
        var results = new List<SessionInfo>();
        foreach (var file in EnumerateSessionFiles(projectsRoot ?? DefaultProjectsRoot))
        {
            // Cheap pre-filter on the OS timestamp before touching the content.
            DateTime lastWrite;
            try { lastWrite = File.GetLastWriteTime(file); }
            catch { continue; }

            var writeDate = DateOnly.FromDateTime(lastWrite);
            if (writeDate < from || writeDate > to)
                continue;

            if (ReadSession(file) is { } session && (!onlyWithWork || HasWorkIn(session, from, to)))
                results.Add(session);
        }

        return results.OrderByDescending(s => s.LastActivityAt).ToList();
    }

    /// <summary>
    /// Whether a session actually did something in that date range, and so is
    /// worth listing. Opening Claude Code in a folder writes a transcript before
    /// any message is sent; such a session has no tokens and a $0.00 cost, and
    /// showing it is just noise.
    /// </summary>
    internal static bool HasWorkIn(SessionInfo session, DateOnly from, DateOnly to)
    {
        var lastActive = DateOnly.FromDateTime(session.LastActivityAt.LocalDateTime);
        return session.TotalTokens > 0 &&
               Math.Round(session.EstimatedCostUsd, 2) > 0 &&
               lastActive >= from && lastActive <= to;
    }

    /// <summary>Returns the session the collapsed strip shows: the top of
    /// today's list (so the strip and the expanded panel can never disagree),
    /// or the newest session file from any date if nothing ran today.</summary>
    public static SessionInfo? GetMostRecentSession(string? projectsRoot = null)
    {
        var root = projectsRoot ?? DefaultProjectsRoot;
        var today = GetSessionsForDate(DateOnly.FromDateTime(DateTime.Now), root);
        if (today.Count > 0)
            return today[0];

        try
        {
            var newestFile = EnumerateSessionFiles(root)
                .OrderByDescending(File.GetLastWriteTime)
                .FirstOrDefault();
            return newestFile is null ? null : ReadSession(newestFile);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Returns the most recent OS last-write time across every
    /// transcript file, or null if there are none. Deliberately includes
    /// subagent transcripts - a running subagent means Claude Code is active -
    /// and deliberately cheap (a directory scan and a stat per file), since
    /// ClaudeUsageProvider calls it on every poll tick to choose between its
    /// active and idle polling intervals.</summary>
    public static DateTime? GetMostRecentActivityUtc(string? projectsRoot = null)
    {
        var root = projectsRoot ?? DefaultProjectsRoot;
        if (!Directory.Exists(root))
            return null;

        try
        {
            DateTime? maxTime = null;
            foreach (var file in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
            {
                DateTime t;
                try { t = File.GetLastWriteTimeUtc(file); }
                catch { continue; }

                if (maxTime is null || t > maxTime)
                    maxTime = t;
            }
            return maxTime;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>A main session transcript is named after its session's GUID;
    /// anything else (notably subagents/agent-&lt;id&gt;.jsonl) is not a session.</summary>
    internal static bool IsSessionTranscript(string path) =>
        Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _) &&
        !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("subagents", StringComparer.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateSessionFiles(string root)
    {
        if (!Directory.Exists(root))
            return Array.Empty<string>();

        try
        {
            // Materialized so an enumeration failure midway surfaces here,
            // inside the try, rather than later in the caller's loop.
            return Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories)
                .Where(IsSessionTranscript)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Parses (or incrementally updates) one transcript and returns a
    /// fresh SessionInfo snapshot, or null if the file isn't a readable session
    /// transcript. Each call returns a new object, so callers may mutate it
    /// (e.g. attach Git info) without affecting the cached parse state.</summary>
    internal static SessionInfo? ReadSession(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;

            TranscriptState state;
            lock (CacheLock)
            {
                // Shorter than what's already consumed, or a different file at
                // the same path: start over rather than parse from a bogus offset.
                if (!Cache.TryGetValue(path, out state!) ||
                    file.Length < state.Offset ||
                    file.CreationTimeUtc != state.CreationTimeUtc)
                {
                    if (Cache.Count >= MaxCachedTranscripts)
                        Cache.Clear();
                    state = new TranscriptState(path, file.CreationTimeUtc);
                    Cache[path] = state;
                }
            }

            lock (state)
            {
                state.ReadAppended(file.Length);
                return state.ToSessionInfo(file.CreationTime, file.LastWriteTime);
            }
        }
        catch
        {
            return null; // Any unexpected shape: skip this session rather than crash.
        }
    }

    /// <summary>Everything parsed from one transcript so far, plus the byte
    /// offset up to which it has been consumed.</summary>
    private sealed class TranscriptState(string path, DateTime creationTimeUtc)
    {
        private readonly string _canonicalSessionId = Path.GetFileNameWithoutExtension(path);

        // Kept in parallel: records belonging to this session only, and every
        // record (the fallback when nothing in the file matches its name).
        private readonly Totals _own = new();
        private readonly Totals _all = new();

        private string? _contentSessionId, _cwd, _version;

        public DateTime CreationTimeUtc { get; } = creationTimeUtc;
        public long Offset { get; private set; }

        /// <summary>Consumes complete lines between Offset and fileLength. A
        /// trailing partial line (Claude Code mid-write) is left for the next
        /// call, so it is never parsed half-written and then skipped forever.</summary>
        public void ReadAppended(long fileLength)
        {
            if (fileLength <= Offset)
                return;

            byte[] buffer;
            // FileShare.ReadWrite | Delete: Claude Code may hold the file open
            // for writing; File.ReadLines' FileShare.Read would fail on that.
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite | FileShare.Delete))
            {
                stream.Seek(Offset, SeekOrigin.Begin);
                buffer = new byte[fileLength - Offset];
                var read = 0;
                while (read < buffer.Length)
                {
                    var n = stream.Read(buffer, read, buffer.Length - read);
                    if (n == 0) break;
                    read += n;
                }
                if (read < buffer.Length)
                    Array.Resize(ref buffer, read);
            }

            // 0x0A never occurs inside a multi-byte UTF-8 sequence, so splitting
            // on the raw byte is safe.
            var lastNewline = Array.LastIndexOf(buffer, (byte)'\n');
            if (lastNewline < 0)
                return;

            var text = Encoding.UTF8.GetString(buffer, 0, lastNewline);
            foreach (var line in text.Split('\n'))
                Feed(line.TrimEnd('\r').TrimStart('﻿'));

            Offset += lastNewline + 1;
        }

        private void Feed(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return;

            JsonElement root;
            try
            {
                using var doc = JsonDocument.Parse(line);
                root = doc.RootElement.Clone();
            }
            catch
            {
                return; // Skip malformed lines rather than aborting the whole file.
            }

            var lineSessionId = GetString(root, "sessionId");
            _contentSessionId ??= lineSessionId;
            _cwd ??= GetString(root, "cwd");
            _version ??= GetString(root, "version");

            _all.Apply(root);

            // A record with no sessionId at all (summaries, snapshots) isn't
            // evidence of a different session, so it counts as this one's.
            var isOwn = lineSessionId is null ||
                        string.Equals(lineSessionId, _canonicalSessionId, StringComparison.OrdinalIgnoreCase);
            if (!isOwn)
                return;

            if (lineSessionId is not null)
                _own.MatchedSessionId = true;
            _own.Apply(root);
        }

        public SessionInfo? ToSessionInfo(DateTime fileCreated, DateTime fileLastWrite)
        {
            if (_contentSessionId is null || _cwd is null)
                return null; // Not a session transcript.

            var t = _own.MatchedSessionId ? _own : _all;
            var session = new SessionInfo
            {
                SessionId = _canonicalSessionId,
                SessionName = t.SessionName,
                TranscriptPath = path,
                WorkingDirectory = _cwd,
                Model = t.Model,
                ThinkingEffort = t.Effort,
                ClaudeCodeVersion = _version,
                StartedAt = t.FirstTimestamp ?? fileCreated,
                LastActivityAt = t.LastTimestamp ?? fileLastWrite,
                InputTokens = t.InputTokens,
                OutputTokens = t.OutputTokens,
                CacheReadTokens = t.CacheReadTokens,
                CacheCreationTokens = t.CacheCreationTokens,
                ContextWindowSize = ModelContextWindows.Lookup(t.Model),
                CurrentContextTokens = t.LastTurnContextTokens,
                CompactionCount = t.CompactionCount,
                SkillsUsed = t.Skills.ToList()
            };
            session.EstimatedCostUsd = Math.Round(t.CostUsd, 4);
            return session;
        }
    }

    /// <summary>Running totals for one view of a transcript (see TranscriptState).</summary>
    private sealed class Totals
    {
        private readonly HashSet<string> _seenMessageIds = new();

        public bool MatchedSessionId { get; set; }
        public string? SessionName { get; private set; }
        public string? Model { get; private set; }
        public string? Effort { get; private set; }
        public DateTimeOffset? FirstTimestamp { get; private set; }
        public DateTimeOffset? LastTimestamp { get; private set; }
        public long InputTokens { get; private set; }
        public long OutputTokens { get; private set; }
        public long CacheReadTokens { get; private set; }
        public long CacheCreationTokens { get; private set; }
        public long LastTurnContextTokens { get; private set; }
        public int CompactionCount { get; private set; }

        /// <summary>Estimated cost, summed per response at that response's
        /// own model, cache-write lifetime (5m/1h), speed and region.</summary>
        public decimal CostUsd { get; private set; }
        public List<string> Skills { get; } = new();

        private static string? ReadEffort(JsonElement root, string property)
        {
            if (!root.TryGetProperty(property, out var el))
                return null;
            var value = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Object when el.TryGetProperty("level", out var level) && level.ValueKind == JsonValueKind.String
                    => level.GetString(),
                _ => null
            };
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        public void Apply(JsonElement root)
        {
            if (GetString(root, "customTitle") is { } title)
                SessionName ??= title;

            // Current Claude Code writes the effort level on each assistant
            // record as a plain string ("effort":"high"), with perTurnEffort
            // set when a single turn overrides it; older versions wrote
            // {"effort":{"level":"high"}}. The latest value wins.
            if ((ReadEffort(root, "perTurnEffort") ?? ReadEffort(root, "effort")) is { } level)
                Effort = level;

            if (GetString(root, "subtype") == "compact_boundary")
                CompactionCount++;

            if (GetBool(root, "isSidechain"))
                return;

            if (GetTimestamp(root) is { } timestamp)
            {
                FirstTimestamp ??= timestamp;
                LastTimestamp = timestamp;
            }

            if (GetString(root, "type") != "assistant" ||
                !root.TryGetProperty("message", out var message))
            {
                return;
            }

            // The latest model, so a session that switched models shows what
            // it's on now. "<synthetic>" marks locally generated messages.
            if (GetString(message, "model") is { } model && model != "<synthetic>")
                Model = model;

            if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
                return;

            // A record with no id (unexpected shape) is treated as always-new
            // rather than silently dropped.
            var messageId = GetString(message, "id");
            if (messageId is not null && !_seenMessageIds.Add(messageId))
                return;

            var inTok = GetLong(usage, "input_tokens");
            var cacheReadTok = GetLong(usage, "cache_read_input_tokens");
            var cacheCreationTok = GetLong(usage, "cache_creation_input_tokens");

            var outTok = GetLong(usage, "output_tokens");

            // Cache writes kept 1 hour cost more than 5-minute ones; Claude
            // Code reports the split under cache_creation. Without it, all
            // writes are priced as 5-minute ones.
            long write1h = 0;
            if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
                write1h = Math.Min(GetLong(creation, "ephemeral_1h_input_tokens"), cacheCreationTok);

            CostUsd += PriceTable.CostOf(GetString(message, "model") ?? Model, inTok, outTok, cacheReadTok,
                cacheCreationTok - write1h, write1h,
                fast: GetString(usage, "speed") == "fast",
                usOnly: GetString(usage, "inference_geo") == "us");

            InputTokens += inTok;
            OutputTokens += outTok;
            CacheReadTokens += cacheReadTok;
            CacheCreationTokens += cacheCreationTok;
            LastTurnContextTokens = inTok + cacheReadTok + cacheCreationTok;

            foreach (var skill in ExtractSkillInvocations(message))
                if (!Skills.Contains(skill))
                    Skills.Add(skill);
        }
    }

    private static IEnumerable<string> ExtractSkillInvocations(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var block in content.EnumerateArray())
        {
            if (GetString(block, "type") == "tool_use" && GetString(block, "name") == "Skill" &&
                block.TryGetProperty("input", out var input) &&
                GetString(input, "skill") is { } skillName)
            {
                yield return skillName;
            }
        }
    }

    private static string? GetString(JsonElement el, string property) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long GetLong(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    private static bool GetBool(JsonElement el, string property) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? GetTimestamp(JsonElement el)
    {
        var raw = GetString(el, "timestamp");
        return raw is not null && DateTimeOffset.TryParse(raw, out var dt) ? dt : null;
    }
}
