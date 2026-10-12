using System.IO;
using System.Text.Json;

namespace StickyAIusage.Services;

/// <summary>
/// Works out what was spent today on a monthly-cap account. The usage
/// endpoint only reports the month's total, so the tracker remembers what
/// that total was when the day began and subtracts it. The month's total
/// covers Chat, Code, Cowork and Chrome, so this counts all of them - not
/// just the Claude Code sessions in the transcripts.
///
/// The first reading of a day sets the day's starting point:
/// - Carried over from yesterday's last reading when the widget was running
///   yesterday (the usual case: it runs all day, every day).
/// - Otherwise (first launch, or the widget was off for more than a day),
///   today's Claude Code sessions are the only spend the widget can see from
///   before it started, so the start is the current total minus their
///   estimated cost. Chat or Cowork used before launch is then missed, which
///   is why the figure is called approximate.
/// When the month resets (the total drops), what was spent today before the
/// reset is kept and the count carries on from zero.
///
/// The state is saved under the cache folder, so a restart mid-day keeps the
/// same starting point.
/// </summary>
public sealed class DailySpendTracker
{
    /// <param name="Date">The local day this state is for.</param>
    /// <param name="Baseline">The month's total when counting (re)started today.</param>
    /// <param name="Carried">Spent today before a month reset.</param>
    /// <param name="LastUsed">The month's total at the latest reading.</param>
    public sealed record State(DateOnly Date, decimal Baseline, decimal Carried, decimal LastUsed)
    {
        public decimal SpentToday => Math.Max(0, Carried + LastUsed - Baseline);
    }

    /// <summary>A drop smaller than this is rounding noise, not a month reset.</summary>
    private const decimal ResetTolerance = 0.01m;

    private readonly string _path;
    private readonly object _lock = new();
    private State? _state;
    private bool _loaded;

    public DailySpendTracker(string? path = null) =>
        _path = path ?? Path.Combine(AppPaths.CacheDirectory, "day-spend.json");

    /// <summary>Records a reading and returns today's spend.</summary>
    /// <param name="codeSpentToday">Estimated cost of today's Claude Code
    /// sessions; only used when the day has no starting point yet. Called
    /// lazily, since it means scanning the transcripts.</param>
    public decimal Record(decimal monthlyUsed, DateOnly today, Func<decimal> codeSpentToday)
    {
        lock (_lock)
        {
            if (!_loaded)
            {
                _state = Load(_path);
                _loaded = true;
            }

            var next = Advance(_state, today, monthlyUsed, codeSpentToday);
            if (next != _state)
            {
                _state = next;
                Save(_path, next);
            }
            return next.SpentToday;
        }
    }

    /// <summary>The next state after a reading (pure, for the tests).</summary>
    public static State Advance(State? state, DateOnly today, decimal monthlyUsed, Func<decimal> codeSpentToday)
    {
        monthlyUsed = Math.Max(0, monthlyUsed);

        if (state is null || state.Date != today)
        {
            var carriedFromYesterday = state is not null && state.Date == today.AddDays(-1)
                                       && monthlyUsed >= state.LastUsed - ResetTolerance;
            var baseline = carriedFromYesterday
                ? state!.LastUsed
                : Math.Max(0, monthlyUsed - Math.Max(0, codeSpentToday()));
            return new State(today, baseline, 0, monthlyUsed);
        }

        if (monthlyUsed < state.LastUsed - ResetTolerance)
        {
            // The month reset: keep what today had spent so far and count on
            // from the new month's zero.
            return new State(today, 0, state.SpentToday, monthlyUsed);
        }

        return monthlyUsed == state.LastUsed ? state : state with { LastUsed = monthlyUsed };
    }

    private static State? Load(string path)
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) : null;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DailySpendTracker.Load", ex);
            return null;
        }
    }

    private static void Save(string path, State state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(state));
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("DailySpendTracker.Save", ex);
        }
    }
}
