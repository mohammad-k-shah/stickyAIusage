using System.IO;

namespace StickyAIusage.Services;

/// <summary>
/// A "show yourself" signal between two copies of the app through a file:
/// the second copy rewrites it, the running copy watches it. Used on macOS,
/// where named wait handles don't exist; Windows uses a named event instead.
/// Without it a second launch did nothing at all, which looks like a crash.
/// </summary>
public sealed class FileSignal : IDisposable
{
    // One write raises several change events; they count as one signal.
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private readonly FileSystemWatcher? _watcher;
    private readonly Action _onSignal;
    private readonly object _gate = new();
    private long _lastSignalTicks = long.MinValue;

    /// <summary>Starts watching <paramref name="path"/>; <paramref name="onSignal"/>
    /// runs on a thread-pool thread. Never throws: without a watcher the app
    /// still works, a second launch just doesn't open the flyout (logged).</summary>
    public FileSignal(string path, Action onSignal)
    {
        _onSignal = onSignal;
        try
        {
            var directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime | NotifyFilters.Size
            };
            _watcher.Changed += (_, _) => Raise();
            _watcher.Created += (_, _) => Raise();
            _watcher.Renamed += (_, _) => Raise();
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("FileSignal (watch)", ex);
            _watcher?.Dispose();
            _watcher = null;
        }
    }

    /// <summary>Signals the copy watching <paramref name="path"/>. Returns
    /// false (and logs) when the file can't be written.</summary>
    public static bool Send(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("FileSignal.Send", ex);
            return false;
        }
    }

    private void Raise()
    {
        lock (_gate)
        {
            var now = Environment.TickCount64;
            if (_lastSignalTicks != long.MinValue && now - _lastSignalTicks < Debounce.TotalMilliseconds)
                return;
            _lastSignalTicks = now;
        }
        try { _onSignal(); }
        catch (Exception ex) { DiagnosticLog.WriteError("FileSignal (handler)", ex); }
    }

    public void Dispose() => _watcher?.Dispose();
}
