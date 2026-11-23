using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyAIusage.Services;

namespace StickyAIusage.Alerts;

/// <summary>An alert that was already given (or dropped in quiet hours), so
/// it is never given twice. <paramref name="ResetsAt"/> is the reset of the
/// window it was about (reported, or assumed for a monthly cap), null for a
/// window without one; <paramref name="Percent"/> is the reading it was about.
/// <see cref="ClearSince"/>: for a limit forecast, since when the forecast no
/// longer says so (after 15 minutes it may be given again).</summary>
public sealed record ToldAlert(string Key, string Provider, string Kind, AlertKind AlertKind, string Detail,
    DateTimeOffset? ResetsAt, double Percent, DateTimeOffset At)
{
    public DateTimeOffset? ClearSince { get; init; }
}

/// <summary>The highest reading seen in one window (one reset), so its reset
/// can say "you're back" when the window had been busy.</summary>
public sealed record WindowPeak(string Provider, string Kind, DateTimeOffset ResetsAt, double Peak);

/// <summary>What the planner remembers between readings and restarts.</summary>
public sealed record AlertState(IReadOnlyList<ToldAlert> Told, IReadOnlyList<WindowPeak> Peaks)
{
    public static AlertState Empty { get; } = new([], []);
}

/// <summary>
/// <c>cache/alerts.json</c>: the <see cref="AlertState"/> with a schema
/// number, written through <see cref="AtomicFile"/>. A missing, broken or
/// newer file reads as empty (the worst case is one repeated alert), and
/// reading never throws.
/// </summary>
public static class AlertStateFile
{
    public const int Schema = 1;
    public const string FileName = "alerts.json";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record FileShape(int Schema, IReadOnlyList<ToldAlert>? Told, IReadOnlyList<WindowPeak>? Peaks);

    public static AlertState Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return AlertState.Empty;
            var shape = JsonSerializer.Deserialize<FileShape>(File.ReadAllText(path), Options);
            if (shape is null || shape.Schema != Schema)
            {
                DiagnosticLog.WriteInfo($"Alerts: {FileName} has schema {shape?.Schema.ToString() ?? "none"}, not {Schema}; starting afresh.");
                return AlertState.Empty;
            }
            return new AlertState(
                (shape.Told ?? []).Where(t => !string.IsNullOrEmpty(t.Key) && t.Provider is not null && t.Kind is not null)
                    .Select(t => t.Detail is null ? t with { Detail = "" } : t).ToList(),
                (shape.Peaks ?? []).Where(p => !string.IsNullOrEmpty(p.Kind)).ToList());
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteInfo($"Alerts: {FileName} could not be read ({ex.Message}); starting afresh.");
            return AlertState.Empty;
        }
    }

    public static string Serialize(AlertState state) =>
        JsonSerializer.Serialize(new FileShape(Schema, state.Told, state.Peaks), Options);
}
