using System.IO;
using System.Text;
using System.Text.Json;
using StickyAIusage.Models;
using StickyAIusage.Stats;

namespace StickyAIusage.Services;

/// <summary>
/// Reads Claude Desktop Code-tab session transcripts from
/// %USERPROFILE%\.claude\projects\&lt;project&gt;\&lt;session-guid&gt;.jsonl - the same
/// files Claude Code CLI writes (Desktop's Code tab writes them too, with
/// entrypoint: "claude-desktop"). With CLAUDE_CONFIG_DIR set, Claude Code
/// writes them under that folder instead, and so this reads there (AppPaths).
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
/// measurable cost. The cache keeps the 512 most recently read transcripts.
///
/// Which files exist, and when each was last written, comes from the
/// <see cref="TranscriptIndex"/>: no question here walks the folder.
/// </summary>
public sealed class ClaudeSessionLogReader
{
    public static string DefaultProjectsRoot => Path.Combine(AppPaths.ClaudeDirectory, "projects");

    /// <summary>Parse states kept (least recently read dropped first): far more than
    /// the 30-day view lists, small enough to stay a few MB.</summary>
    internal const int MaxCachedTranscripts = 512;

    private readonly TranscriptIndex _index;
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<TranscriptState>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<TranscriptState> _recentlyRead = new(); // most recent first

    public ClaudeSessionLogReader(TranscriptIndex index)
    {
        _index = index;
    }

    /// <summary>The projects folder the index covers.</summary>
    public string ProjectsRoot => _index.Root;

    /// <summary>How many parse states are cached (tests).</summary>
    internal int CachedCount
    {
        get
        {
            lock (_cacheLock)
                return _cache.Count;
        }
    }

    internal bool IsCached(string path)
    {
        lock (_cacheLock)
            return _cache.ContainsKey(path);
    }

    /// <summary>Returns one SessionInfo per session transcript with activity on
    /// the given local date ("sessions active today"), most recently
    /// active first. Never throws; unreadable files are skipped.</summary>
    public List<SessionInfo> GetSessionsForDate(DateOnly localDate, bool onlyWithWork = true) =>
        GetSessionsInRange(localDate, localDate, onlyWithWork);

    /// <summary>Like GetSessionsForDate, for sessions last active on any local
    /// date from <paramref name="from"/> to <paramref name="to"/> inclusive -
    /// the flyout's "Last 7 days" and similar views.</summary>
    public List<SessionInfo> GetSessionsInRange(DateOnly from, DateOnly to, bool onlyWithWork = true)
    {
        var results = new List<SessionInfo>();
        // Cheap pre-filter on the write time the index keeps, before touching any file.
        foreach (var file in _index.WrittenBetween(LocalMidnightUtc(from), LocalMidnightUtc(to.AddDays(1))))
        {
            if (!IsSessionTranscript(file.Path))
                continue;
            if (ReadSession(file.Path) is { } session && (!onlyWithWork || HasWorkIn(session, from, to)))
                results.Add(session);
        }

        return results.OrderByDescending(s => s.LastActivityAt).ToList();
    }

    private static DateTime LocalMidnightUtc(DateOnly date) =>
        date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Local).ToUniversalTime();

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
    public SessionInfo? GetMostRecentSession()
    {
        var today = GetSessionsForDate(DateOnly.FromDateTime(DateTime.Now));
        if (today.Count > 0)
            return today[0];

        return _index.Newest(IsSessionTranscript) is { } newest ? ReadSession(newest.Path) : null;
    }

    /// <summary>Returns the most recent last-write time across every
    /// transcript file, or null if there are none. Deliberately includes
    /// subagent transcripts - a running subagent means Claude Code is active.
    /// The usage sources call it on every poll tick to choose between their
    /// active and idle polling intervals; the index keeps it, so it costs nothing.</summary>
    public DateTime? GetMostRecentActivityUtc() => _index.MostRecentWriteUtc;

    /// <summary>A main session transcript is named after its session's GUID;
    /// anything else (notably subagents/agent-&lt;id&gt;.jsonl) is not a session.</summary>
    internal static bool IsSessionTranscript(string path) =>
        Guid.TryParse(Path.GetFileNameWithoutExtension(path), out _) &&
        !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("subagents", StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses (or incrementally updates) one transcript and returns a
    /// fresh SessionInfo snapshot, or null if the file isn't a readable session
    /// transcript. Each call returns a new object, so callers may mutate it
    /// (e.g. attach Git info) without affecting the cached parse state.</summary>
    internal SessionInfo? ReadSession(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists)
                return null;
            // What the file says now beats a watcher event that never came.
            _index.Observe(new TranscriptFile(path, file.Length, file.LastWriteTimeUtc));

            TranscriptState state;
            lock (_cacheLock)
            {
                // Shorter than what's already consumed, or a different file at
                // the same path: start over rather than parse from a bogus offset.
                // A new price list (the daily refresh) also starts over, so the
                // costs already summed are priced again with it.
                var catalog = ModelCatalog.Current;
                if (_cache.TryGetValue(path, out var node) &&
                    file.Length >= node.Value.Offset &&
                    file.CreationTimeUtc == node.Value.CreationTimeUtc &&
                    node.Value.Catalog == catalog)
                {
                    state = node.Value;
                    _recentlyRead.Remove(node);
                    _recentlyRead.AddFirst(node);
                }
                else
                {
                    if (node is not null)
                    {
                        _recentlyRead.Remove(node);
                        _cache.Remove(path);
                    }
                    else if (_cache.Count >= MaxCachedTranscripts && _recentlyRead.Last is { } oldest)
                    {
                        _recentlyRead.RemoveLast();
                        _cache.Remove(oldest.Value.FilePath);
                    }
                    state = new TranscriptState(path, file.CreationTimeUtc, catalog);
                    _cache[path] = _recentlyRead.AddFirst(state);
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
    private sealed class TranscriptState(string path, DateTime creationTimeUtc, ModelCatalog catalog)
    {
        private readonly string _canonicalSessionId = Path.GetFileNameWithoutExtension(path);

        public string FilePath { get; } = path;

        // Kept in parallel: records belonging to this session only, and every
        // record (the fallback when nothing in the file matches its name).
        private readonly Totals _own = new(catalog);
        private readonly Totals _all = new(catalog);

        private string? _contentSessionId, _cwd, _version;

        public DateTime CreationTimeUtc { get; } = creationTimeUtc;
        public ModelCatalog Catalog { get; } = catalog;
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
            using (var stream = new FileStream(FilePath, FileMode.Open, FileAccess.Read,
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
                TranscriptPath = FilePath,
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
                SkillsUsed = t.Skills.ToList(),
                CostIsRough = t.CostIsRough
            };
            session.EstimatedCostUsd = Math.Round(t.CostUsd, 4);
            return session;
        }
    }

    /// <summary>Running totals for one view of a transcript (see TranscriptState).</summary>
    private sealed class Totals(ModelCatalog catalog)
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

        /// <summary>A response used a model the price list doesn't know.</summary>
        public bool CostIsRough { get; private set; }
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

            if (CountResponse(message, _seenMessageIds, Model, catalog) is not { } response)
                return;

            CostUsd += response.CostUsd;
            CostIsRough |= response.CostIsRough;
            InputTokens += response.InputTokens;
            OutputTokens += response.OutputTokens;
            CacheReadTokens += response.CacheReadTokens;
            CacheCreationTokens += response.CacheCreationTokens;
            LastTurnContextTokens = response.InputTokens + response.CacheReadTokens + response.CacheCreationTokens;

            foreach (var skill in ExtractSkillInvocations(message))
                if (!Skills.Contains(skill))
                    Skills.Add(skill);
        }
    }

    /// <summary>
    /// One API response's tokens and estimated cost, or null when the record adds
    /// nothing: no usage block, or a message id already counted (Claude Code
    /// repeats a response's usage on every content block it writes). The session
    /// cards and the year's stats both count through here, so their rules can't
    /// drift apart. The caller has already skipped sidechain (subagent) records.
    /// </summary>
    /// <param name="message">The record's <c>message</c> object.</param>
    /// <param name="seenMessageIds">The ids counted so far in this view of the transcript.</param>
    /// <param name="fallbackModel">The model to price a response with that names none.</param>
    internal static CountedResponse? CountResponse(JsonElement message, HashSet<string> seenMessageIds, string? fallbackModel,
        ModelCatalog catalog)
    {
        if (!message.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            return null;

        // A record with no id (unexpected shape) is treated as always-new
        // rather than silently dropped.
        var messageId = GetString(message, "id");
        if (messageId is not null && !seenMessageIds.Add(messageId))
            return null;

        var inTok = GetLong(usage, "input_tokens");
        var cacheReadTok = GetLong(usage, "cache_read_input_tokens");
        var cacheCreationTok = GetLong(usage, "cache_creation_input_tokens");
        var outTok = GetLong(usage, "output_tokens");
        var model = GetString(message, "model") is { } m && m != "<synthetic>" ? m : fallbackModel;

        // A response with no tokens costs nothing whatever its model, and
        // must not mark the session's cost rough ("<synthetic>" messages).
        if (inTok + outTok + cacheReadTok + cacheCreationTok == 0)
            return new CountedResponse(model, 0, 0, 0, 0, 0m, false);

        // Cache writes kept 1 hour cost more than 5-minute ones; Claude
        // Code reports the split under cache_creation. Without it, all
        // writes are priced as 5-minute ones.
        long write1h = 0;
        if (usage.TryGetProperty("cache_creation", out var creation) && creation.ValueKind == JsonValueKind.Object)
            write1h = Math.Min(GetLong(creation, "ephemeral_1h_input_tokens"), cacheCreationTok);

        var estimate = PriceTable.Estimate(model, inTok, outTok, cacheReadTok,
            cacheCreationTok - write1h, write1h,
            fast: GetString(usage, "speed") == "fast",
            usOnly: GetString(usage, "inference_geo") == "us",
            catalog: catalog);
        return new CountedResponse(model, inTok, outTok, cacheReadTok, cacheCreationTok, estimate.Usd, estimate.IsRough);
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

    internal static string? GetString(JsonElement el, string property) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static long GetLong(JsonElement el, string property) =>
        el.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt64()
            : 0;

    internal static bool GetBool(JsonElement el, string property) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(property, out var v) &&
        v.ValueKind == JsonValueKind.True;

    internal static DateTimeOffset? GetTimestamp(JsonElement el)
    {
        var raw = GetString(el, "timestamp");
        return raw is not null && DateTimeOffset.TryParse(raw, out var dt) ? dt : null;
    }
}
