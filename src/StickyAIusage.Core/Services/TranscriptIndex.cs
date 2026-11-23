using System.Diagnostics;
using System.IO;

namespace StickyAIusage.Services;

/// <summary>One transcript file as the index knows it: where it is, how long it is
/// and when it was last written (UTC).</summary>
public readonly record struct TranscriptFile(string Path, long Length, DateTime LastWriteUtc);

/// <summary>How the index keeps up with the projects folder.</summary>
public enum TranscriptIndexMode
{
    /// <summary>Not started yet (nothing has asked).</summary>
    NotStarted,

    /// <summary>A file-system watcher reports every change; a safety walk every 10 minutes.</summary>
    Watching,

    /// <summary>The watcher couldn't start or stopped (a network or synced folder, the folder
    /// was removed): a full walk every minute, trying the watcher again each time.</summary>
    Polling,

    /// <summary>The projects folder doesn't exist yet (Claude Code never ran): its parent is
    /// watched, and its existence checked every minute.</summary>
    WaitingForFolder
}

/// <summary>The file-system calls the index makes, so a test can count the directory
/// enumerations.</summary>
internal interface ITranscriptFileSystem
{
    bool DirectoryExists(string path);

    /// <summary>Every transcript under <paramref name="folder"/>, its subfolders included:
    /// the only directory enumeration the index ever does.</summary>
    List<TranscriptFile> Walk(string folder);

    /// <summary>The file as it is now, or null when it is gone (or is a folder).</summary>
    TranscriptFile? Stat(string path);
}

internal sealed class DiskTranscriptFileSystem : ITranscriptFileSystem
{
    public static readonly DiskTranscriptFileSystem Instance = new();

    // Hidden and system files count (the old walk counted them too); a folder the
    // account can't open is skipped rather than ending the walk.
    private static readonly EnumerationOptions WalkOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
        MatchType = MatchType.Simple
    };

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public List<TranscriptFile> Walk(string folder)
    {
        var files = new List<TranscriptFile>();
        // The enumeration's own data (one directory read), not a stat per file.
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles("*" + TranscriptIndex.Extension, WalkOptions))
            files.Add(new TranscriptFile(file.FullName, file.Length, file.LastWriteTimeUtc));
        return files;
    }

    public TranscriptFile? Stat(string path)
    {
        var file = new FileInfo(path);
        return file.Exists ? new TranscriptFile(file.FullName, file.Length, file.LastWriteTimeUtc) : null;
    }
}

/// <summary>
/// Every Claude Code transcript under one projects folder (sessions and subagents),
/// with its length and last write, kept current without walking the folder on every
/// tick. One walk when first asked (on the thread pool), then a recursive
/// <see cref="FileSystemWatcher"/> keeps it up to date; a full walk again when the
/// watcher's buffer overflows, every minute when it can't run (network or synced home
/// folder), and every 10 minutes regardless, as a safety net. The newest write is kept
/// as it changes, so the "is Claude Code active" probe the usage sources call on every
/// poll costs nothing. A heavy user has thousands of transcripts; the old reader
/// enumerated and stat'ed all of them several times a minute, forever.
/// Thread-safe; never throws from background work (it logs).
/// </summary>
public sealed class TranscriptIndex : IDisposable
{
    public const string Extension = ".jsonl";

    internal static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan SafetyWalkInterval = TimeSpan.FromMinutes(10);

    /// <summary>How long a question waits for the first walk before it is answered
    /// with what is known so far.</summary>
    internal static readonly TimeSpan FirstWalkWait = TimeSpan.FromSeconds(15);

    private const int WatcherBufferBytes = 64 * 1024;

    private static readonly StringComparer PathComparer =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private readonly ITranscriptFileSystem _files;
    private readonly Func<DateTimeOffset> _clock;
    private readonly bool _watch;
    private readonly bool _runTimer;
    private readonly Action<string> _log;

    private readonly object _lock = new();
    private readonly object _walkGate = new();
    private Dictionary<string, TranscriptFile> _entries = new(PathComparer);
    private DateTime? _newest;
    private bool _newestStale;
    private HashSet<string>? _touchedDuringWalk;

    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _parentWatcher;
    private Timer? _ticker;
    private Task? _firstWalk;
    private int _walkQueued;
    private long _lastWalkTicks = DateTimeOffset.MinValue.UtcTicks; // UTC ticks: the timer reads it without the walk gate
    private bool _watcherFailureLogged;
    private bool _disposed;

    public TranscriptIndex(string projectsRoot)
        : this(projectsRoot, DiskTranscriptFileSystem.Instance, () => DateTimeOffset.UtcNow)
    {
    }

    /// <param name="watch">False: no file-system watcher (polling only).</param>
    /// <param name="runTimer">False: no minute timer; a test calls <see cref="Tick"/>.</param>
    /// <param name="log">Where the index's few log lines go (default: the diagnostics log).</param>
    internal TranscriptIndex(string projectsRoot, ITranscriptFileSystem files, Func<DateTimeOffset> clock,
        bool watch = true, bool runTimer = true, Action<string>? log = null)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(projectsRoot));
        _files = files;
        _clock = clock;
        _watch = watch;
        _runTimer = runTimer;
        _log = log ?? DiagnosticLog.WriteInfo;
    }

    /// <summary>The projects folder (full path).</summary>
    public string Root { get; }

    public TranscriptIndexMode Mode
    {
        get => _mode;
        private set => _mode = value;
    }

    // Set by walks and the watcher's error handler, read by the timer.
    private volatile TranscriptIndexMode _mode = TranscriptIndexMode.NotStarted;

    internal bool IsDisposed => _disposed;

    /// <summary>How many full walks have run (the first one included).</summary>
    public int WalkCount { get; private set; }

    /// <summary>How long the first walk took (null until it finished).</summary>
    public TimeSpan? FirstWalkDuration { get; private set; }

    /// <summary>The newest write of any transcript (UTC), subagents included: a running
    /// subagent means Claude Code is working. Null when there are none. Kept as files
    /// change, so this costs nothing.</summary>
    public DateTime? MostRecentWriteUtc
    {
        get
        {
            EnsureStarted();
            lock (_lock)
            {
                if (_newestStale)
                {
                    _newest = _entries.Count == 0 ? null : _entries.Values.Max(f => f.LastWriteUtc);
                    _newestStale = false;
                }
                return _newest;
            }
        }
    }

    public int Count
    {
        get
        {
            EnsureStarted();
            lock (_lock)
                return _entries.Count;
        }
    }

    /// <summary>The transcripts last written from <paramref name="fromUtc"/> (inclusive) to
    /// <paramref name="toUtc"/> (exclusive). No file-system access.</summary>
    public List<TranscriptFile> WrittenBetween(DateTime fromUtc, DateTime toUtc)
    {
        EnsureStarted();
        var found = new List<TranscriptFile>();
        lock (_lock)
        {
            foreach (var file in _entries.Values)
                if (file.LastWriteUtc >= fromUtc && file.LastWriteUtc < toUtc)
                    found.Add(file);
        }
        return found;
    }

    /// <summary>The most recently written transcript that <paramref name="where"/> accepts.</summary>
    public TranscriptFile? Newest(Func<string, bool> where)
    {
        EnsureStarted();
        TranscriptFile? newest = null;
        lock (_lock)
        {
            foreach (var file in _entries.Values)
                if ((newest is null || file.LastWriteUtc > newest.Value.LastWriteUtc) && where(file.Path))
                    newest = file;
        }
        return newest;
    }

    /// <summary>Every transcript known now (a copy), for a pass over all of them.</summary>
    public List<TranscriptFile> Files()
    {
        EnsureStarted();
        lock (_lock)
            return [.. _entries.Values];
    }

    /// <summary>What a reader just saw when it opened a file: a later write than the
    /// index knows is taken (so a missed watcher event heals at the next read).</summary>
    public void Observe(TranscriptFile file)
    {
        try
        {
            file = file with { Path = Path.GetFullPath(file.Path) };
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }
        if (!IsUnderRoot(file.Path) || !IsTranscript(file.Path))
            return;
        lock (_lock)
        {
            if (!_entries.TryGetValue(file.Path, out var known) || file.LastWriteUtc > known.LastWriteUtc)
                Set(file);
        }
    }

    /// <summary>Blocks until the first walk finished (or <paramref name="timeout"/>);
    /// true when it did.</summary>
    public bool WaitUntilReady(TimeSpan timeout) => Start().Wait(timeout); // a walk logs its own errors, never throws

    /// <summary>Starts the first walk and the watcher (once); later calls return the
    /// same task. Questions start it themselves.</summary>
    public Task Start()
    {
        lock (_lock)
        {
            if (_firstWalk is not null)
                return _firstWalk;
            if (_disposed)
                return Task.CompletedTask; // a question after shutdown: nothing is known
            _firstWalk = Task.Run(() => Walk("start"));
            if (_runTimer)
                _ticker = new Timer(_ => Tick(), null, TickInterval, TickInterval);
            return _firstWalk;
        }
    }

    private void EnsureStarted()
    {
        var first = _firstWalk ?? Start();
        if (!first.IsCompleted)
            WaitUntilReady(FirstWalkWait);
    }

    /// <summary>The minute timer: waits for the folder, polls while there is no watcher,
    /// and walks every 10 minutes regardless.</summary>
    internal void Tick()
    {
        try
        {
            if (_disposed)
                return;
            switch (Mode)
            {
                case TranscriptIndexMode.WaitingForFolder:
                    if (_files.DirectoryExists(Root))
                        RequestWalk("the projects folder appeared");
                    break;
                case TranscriptIndexMode.Polling:
                    RequestWalk("polling");
                    break;
                case TranscriptIndexMode.Watching:
                    if (_clock().UtcTicks - Interlocked.Read(ref _lastWalkTicks) >= SafetyWalkInterval.Ticks)
                        RequestWalk("safety");
                    break;
            }
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("TranscriptIndex.Tick", ex);
        }
    }

    /// <summary>A walk on the thread pool; requests while one waits are merged into it.</summary>
    private void RequestWalk(string reason)
    {
        if (Interlocked.Exchange(ref _walkQueued, 1) == 1)
            return;
        Task.Run(() => Walk(reason));
    }

    /// <summary>A full walk now, on the calling thread (tests).</summary>
    internal void WalkNow() => Walk("asked");

    private void Walk(string reason)
    {
        lock (_walkGate)
        {
            Interlocked.Exchange(ref _walkQueued, 0);
            if (_disposed)
                return;
            try
            {
                WalkLocked(reason);
            }
            catch (Exception ex)
            {
                DiagnosticLog.WriteError($"TranscriptIndex walk ({reason})", ex);
            }
        }
    }

    private void WalkLocked(string reason)
    {
        var timer = Stopwatch.StartNew();
        if (!_files.DirectoryExists(Root))
        {
            StopWatcher();
            lock (_lock)
            {
                _entries = new Dictionary<string, TranscriptFile>(PathComparer);
                _newestStale = true;
            }
            if (Mode != TranscriptIndexMode.WaitingForFolder)
                _log($"Transcript index: {Root} doesn't exist yet; waiting for it.");
            Mode = TranscriptIndexMode.WaitingForFolder;
            if (_watch)
                StartParentWatcher();
            Finish(timer);
            return;
        }

        StopParentWatcher();
        // The watcher first, so nothing written during the walk is missed.
        var previousMode = Mode;
        Mode = _watch && EnsureWatcher() ? TranscriptIndexMode.Watching : TranscriptIndexMode.Polling;

        lock (_lock)
            _touchedDuringWalk = new HashSet<string>(PathComparer);
        List<TranscriptFile> found;
        try
        {
            found = _files.Walk(Root);
        }
        catch
        {
            lock (_lock)
                _touchedDuringWalk = null;
            throw; // logged by Walk; what was known stays
        }

        HashSet<string> touched;
        int corrected;
        lock (_lock)
        {
            var next = new Dictionary<string, TranscriptFile>(found.Count, PathComparer);
            foreach (var file in found)
            {
                // A directory read can lag a file's own metadata; a later write
                // the index already saw wins.
                next[file.Path] = _entries.TryGetValue(file.Path, out var known) && known.LastWriteUtc > file.LastWriteUtc
                    ? known
                    : file;
            }
            corrected = Differences(_entries, next);
            touched = _touchedDuringWalk ?? [];
            _touchedDuringWalk = null;
            _entries = next;
            _newestStale = true;
        }
        // Changes reported while the walk ran: the file as it is now wins.
        foreach (var path in touched)
            Refresh(path);

        var first = FirstWalkDuration is null;
        Finish(timer);
        if (first || previousMode != Mode)
        {
            _log($"Transcript index: {(Mode == TranscriptIndexMode.Watching ? "watching" : "polling every minute")} " +
                 $"{Root} ({found.Count:N0} transcripts, walk {timer.Elapsed.TotalMilliseconds:0} ms).");
        }
        else if (corrected > 0 && reason == "safety")
        {
            // The watcher missed something: worth knowing when a session shows up late.
            _log($"Transcript index: the safety walk corrected {corrected} transcript(s).");
        }
    }

    private void Finish(Stopwatch timer)
    {
        WalkCount++;
        Interlocked.Exchange(ref _lastWalkTicks, _clock().UtcTicks);
        FirstWalkDuration ??= timer.Elapsed;
    }

    private static int Differences(Dictionary<string, TranscriptFile> before, Dictionary<string, TranscriptFile> after)
    {
        var count = before.Keys.Count(path => !after.ContainsKey(path));
        foreach (var (path, file) in after)
            if (!before.TryGetValue(path, out var known) || known != file)
                count++;
        return count;
    }

    /// <summary>True when the watcher runs (started now or before).</summary>
    private bool EnsureWatcher()
    {
        if (_watcher is not null)
            return true;
        try
        {
            var watcher = new FileSystemWatcher(Root)
            {
                IncludeSubdirectories = true,
                InternalBufferSize = WatcherBufferBytes,
                // No name filter: a transcript can arrive by a rename, and a moved
                // project folder only reports the folder.
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite |
                               NotifyFilters.Size | NotifyFilters.CreationTime
            };
            watcher.Created += OnWatcherEvent;
            watcher.Changed += OnWatcherEvent;
            watcher.Deleted += OnWatcherEvent;
            watcher.Renamed += OnWatcherEvent;
            watcher.Error += (_, e) =>
            {
                try
                {
                    OnWatcherError(e.GetException());
                }
                catch (Exception ex)
                {
                    DiagnosticLog.WriteError("TranscriptIndex watcher error", ex);
                }
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
            _watcherFailureLogged = false;
            return true;
        }
        catch (Exception ex)
        {
            if (!_watcherFailureLogged)
                _log($"Transcript index: can't watch {Root} ({ex.GetType().Name}: {ex.Message}); walking it every minute instead.");
            _watcherFailureLogged = true;
            return false;
        }
    }

    private void StopWatcher()
    {
        var watcher = Interlocked.Exchange(ref _watcher, null);
        watcher?.Dispose();
    }

    /// <summary>The projects folder doesn't exist yet: hear about it the moment Claude
    /// Code creates it (the minute timer checks too, for a parent that is missing).</summary>
    private void StartParentWatcher()
    {
        if (_parentWatcher is not null || Path.GetDirectoryName(Root) is not { } parent || !_files.DirectoryExists(parent))
            return;
        try
        {
            var watcher = new FileSystemWatcher(parent)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.DirectoryName
            };
            FileSystemEventHandler appeared = (_, e) =>
            {
                if (string.Equals(Path.TrimEndingDirectorySeparator(e.FullPath), Root, PathComparison))
                    RequestWalk("the projects folder appeared");
            };
            watcher.Created += appeared;
            watcher.Renamed += (sender, e) => appeared(sender, e);
            watcher.EnableRaisingEvents = true;
            _parentWatcher = watcher;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("TranscriptIndex.StartParentWatcher", ex);
        }
    }

    private void StopParentWatcher()
    {
        var watcher = Interlocked.Exchange(ref _parentWatcher, null);
        watcher?.Dispose();
    }

    /// <summary>Raised on a thread-pool thread when the watcher reports a transcript
    /// created, written or renamed into place (its path): Claude Code is writing.</summary>
    public event Action<string>? TranscriptWritten;

    private void OnWatcherEvent(object? sender, FileSystemEventArgs e)
    {
        try
        {
            if (_disposed)
                return;
            if (e is RenamedEventArgs renamed)
            {
                Left(renamed.OldFullPath);
                Arrived(renamed.FullPath);
            }
            else if (e.ChangeType == WatcherChangeTypes.Deleted)
                Left(e.FullPath);
            else if (e.ChangeType == WatcherChangeTypes.Created)
                Arrived(e.FullPath);
            else if (IsTranscript(e.FullPath))
                Refresh(e.FullPath);

            if (e.ChangeType != WatcherChangeTypes.Deleted && IsTranscript(e.FullPath))
                TranscriptWritten?.Invoke(e.FullPath);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("TranscriptIndex event", ex);
        }
    }

    /// <summary>The watcher's Error event. An overflow lost events: walk again. Anything
    /// else stopped the watcher (folder removed, network gone): poll until it starts again.</summary>
    internal void OnWatcherError(Exception? error)
    {
        if (_disposed)
            return;
        if (error is InternalBufferOverflowException)
        {
            _log("Transcript index: the watcher missed changes (buffer overflow); walking again.");
            RequestWalk("overflow");
            return;
        }
        _log($"Transcript index: the watcher stopped ({error?.GetType().Name}: {error?.Message}); walking every minute.");
        StopWatcher();
        Mode = TranscriptIndexMode.Polling;
        RequestWalk("watcher stopped");
    }

    private void Arrived(string path)
    {
        if (IsTranscript(path))
        {
            Refresh(path);
            return;
        }
        // A folder moved in brings its transcripts without an event for each one.
        if (_files.DirectoryExists(path) && IsUnderRoot(path))
        {
            List<TranscriptFile> moved;
            try
            {
                moved = _files.Walk(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return; // gone again already; the safety walk settles it
            }
            foreach (var file in moved)
            {
                lock (_lock)
                {
                    _touchedDuringWalk?.Add(file.Path);
                    Set(file);
                }
            }
        }
    }

    private void Left(string path)
    {
        if (IsTranscript(path))
        {
            Refresh(path); // gone, or already back (a rewrite by rename)
            return;
        }
        // A folder removed or renamed away takes its transcripts with it.
        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        lock (_lock)
        {
            foreach (var gone in _entries.Keys.Where(key => key.StartsWith(prefix, PathComparison)).ToList())
            {
                _touchedDuringWalk?.Add(gone);
                Remove(gone);
            }
        }
    }

    /// <summary>Looks at one file and records what is there now.</summary>
    private void Refresh(string path)
    {
        TranscriptFile? now;
        try
        {
            now = _files.Stat(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            now = null;
        }
        lock (_lock)
        {
            _touchedDuringWalk?.Add(path);
            if (now is { } file)
                Set(file with { Path = path });
            else
                Remove(path);
        }
    }

    // Under _lock.
    private void Set(TranscriptFile file)
    {
        if (_entries.TryGetValue(file.Path, out var known) && known.LastWriteUtc == _newest && file.LastWriteUtc < known.LastWriteUtc)
            _newestStale = true;
        _entries[file.Path] = file;
        if (!_newestStale && (_newest is null || file.LastWriteUtc > _newest))
            _newest = file.LastWriteUtc;
    }

    // Under _lock.
    private void Remove(string path)
    {
        if (_entries.Remove(path, out var gone) && gone.LastWriteUtc == _newest)
            _newestStale = true;
    }

    internal static bool IsTranscript(string path) => path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase);

    private bool IsUnderRoot(string path) =>
        path.StartsWith(Root + Path.DirectorySeparatorChar, PathComparison) ||
        path.StartsWith(Root + Path.AltDirectorySeparatorChar, PathComparison);

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        _ticker?.Dispose();
        // After a walk under way: it could still start a watcher, which would then
        // hold the folder open for good (a walk checks _disposed first).
        lock (_walkGate)
        {
            StopWatcher();
            StopParentWatcher();
        }
    }
}
