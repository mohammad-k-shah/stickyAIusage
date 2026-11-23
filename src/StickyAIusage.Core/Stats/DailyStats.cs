using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using StickyAIusage.Services;

namespace StickyAIusage.Stats;

/// <summary>One API response as the transcript counting rules see it (see
/// <see cref="ClaudeSessionLogReader.CountResponse"/>).</summary>
public readonly record struct CountedResponse(string? Model, long InputTokens, long OutputTokens, long CacheReadTokens,
    long CacheCreationTokens, decimal CostUsd, bool CostIsRough)
{
    public long TotalTokens => InputTokens + OutputTokens + CacheReadTokens + CacheCreationTokens;
}

/// <summary>One model's tokens and estimated cost (list prices) on one day.</summary>
public sealed class ModelDayStats
{
    public long Input { get; set; }
    public long Output { get; set; }
    public long CacheRead { get; set; }
    public long CacheWrite { get; set; }
    public decimal CostUsd { get; set; }
    public int Responses { get; set; }

    [JsonIgnore]
    public long Tokens => Input + Output + CacheRead + CacheWrite;

    internal void Add(CountedResponse response)
    {
        Input += response.InputTokens;
        Output += response.OutputTokens;
        CacheRead += response.CacheReadTokens;
        CacheWrite += response.CacheCreationTokens;
        CostUsd += response.CostUsd;
        Responses++;
    }

    internal void Add(ModelDayStats other)
    {
        Input += other.Input;
        Output += other.Output;
        CacheRead += other.CacheRead;
        CacheWrite += other.CacheWrite;
        CostUsd += other.CostUsd;
        Responses += other.Responses;
    }
}

/// <summary>
/// One local day of Claude Code work, rolled up from the transcripts with the
/// session cards' counting rules (once per message id; subagent turns, forked
/// copies of a parent session and subagent files left out). Stored per month in
/// <c>cache/stats/&lt;yyyy-mm&gt;.json</c>; the year card reads it now and the
/// full year-and-month recap can read it later without scanning again.
/// </summary>
public sealed class DayStats
{
    /// <summary>The model id a response named (<see cref="UnknownModel"/> when none) → its use that day.</summary>
    public SortedDictionary<string, ModelDayStats> Models { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The sessions (transcript file names, GUIDs) that used tokens that day.</summary>
    public SortedSet<string> Sessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Responses per local hour, 0–23.</summary>
    public int[] Hours { get; set; } = new int[24];

    /// <summary>A response used a model the price list doesn't know: the cost is rough.</summary>
    public bool CostIsRough { get; set; }

    public const string UnknownModel = "unknown";

    [JsonIgnore]
    public long Tokens => Models.Values.Sum(m => m.Tokens);

    [JsonIgnore]
    public decimal CostUsd => Models.Values.Sum(m => m.CostUsd);

    /// <summary>One response at that local hour (the session is added by whoever knows it).</summary>
    internal void Add(CountedResponse response, int hour)
    {
        var key = string.IsNullOrWhiteSpace(response.Model) ? UnknownModel : response.Model;
        if (!Models.TryGetValue(key, out var model))
            Models[key] = model = new ModelDayStats();
        model.Add(response);
        CostIsRough |= response.CostIsRough;
        if (hour is >= 0 and < 24)
            Hours[hour]++;
    }

    internal void Add(DayStats other)
    {
        foreach (var (key, use) in other.Models)
        {
            if (!Models.TryGetValue(key, out var model))
                Models[key] = model = new ModelDayStats();
            model.Add(use);
        }
        Sessions.UnionWith(other.Sessions);
        for (var hour = 0; hour < 24 && hour < other.Hours.Length; hour++)
            Hours[hour] += other.Hours[hour];
        CostIsRough |= other.CostIsRough;
    }

    /// <summary>A day read back from a file of another shape is fixed up, not trusted blindly.</summary>
    internal void Normalize()
    {
        Models = new SortedDictionary<string, ModelDayStats>(
            (Models ?? []).Where(m => m.Value is not null).ToDictionary(m => m.Key, m => m.Value), StringComparer.Ordinal);
        Sessions = new SortedSet<string>(Sessions ?? [], StringComparer.OrdinalIgnoreCase);
        var hours = new int[24];
        if (Hours is not null)
            Array.Copy(Hours, hours, Math.Min(24, Hours.Length));
        Hours = hours;
    }
}

/// <summary>
/// The month files under <c>cache/stats/</c>: <c>2026-03.json</c> holds every
/// finished day of March 2026 that a pass has read, in the time zone it was
/// read in and the price list it was priced with. Days are only ever written once
/// they are over, so a file never needs reading again for them. Format (schema 1):
/// <code>
/// { "schema": 1, "month": "2026-03", "zone": "India Standard Time", "prices": "2026-11-17",
///   "days": { "2026-03-14": { "models": { "claude-opus-5-5": { "input": 12, "output": 3400,
///     "cacheRead": 81000, "cacheWrite": 9000, "costUsd": 1.2345, "responses": 41 } },
///     "sessions": [ "…guid…" ], "hours": [ 0, …24 values… ], "costIsRough": false } } }
/// </code>
/// A file in another zone, priced with another price list (so the costs match the
/// session cards, which re-price too), of another schema or that doesn't parse is
/// read as empty (those days are counted again); a newer schema's file is never overwritten.
/// </summary>
public sealed class DailyStatsStore(string directory)
{
    public const int Schema = 1;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public string Directory { get; } = directory;

    public string PathFor(int year, int month) =>
        Path.Combine(Directory, year.ToString("D4", CultureInfo.InvariantCulture) + "-" + month.ToString("D2", CultureInfo.InvariantCulture) + ".json");

    /// <summary>The days kept for that month in that zone; empty when there are none
    /// (or the file can't be used). <paramref name="writable"/> is false for a file a
    /// newer version wrote.</summary>
    /// <param name="prices">The price list the costs must come from (its date); null: any.</param>
    public Dictionary<DateOnly, DayStats> Load(int year, int month, string zoneId, out bool writable, string? prices = null)
    {
        writable = true;
        var days = new Dictionary<DateOnly, DayStats>();
        var path = PathFor(year, month);
        try
        {
            if (!File.Exists(path))
                return days;
            var file = JsonSerializer.Deserialize<MonthFile>(File.ReadAllText(path), Json);
            if (file is null)
                return days;
            if (file.Schema > Schema)
            {
                writable = false;
                return days;
            }
            if (file.Schema != Schema || file.Zone != zoneId || file.Days is null || (prices is not null && file.Prices != prices))
                return days;
            foreach (var (key, day) in file.Days)
            {
                if (day is null || !DateOnly.TryParseExact(key, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    date.Year != year || date.Month != month)
                    continue;
                day.Normalize();
                days[date] = day;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            DiagnosticLog.WriteInfo($"Year stats: {Path.GetFileName(path)} can't be read ({ex.GetType().Name}); its days are counted again.");
            days.Clear();
        }
        return days;
    }

    /// <summary>Writes that month's days (atomically). Never throws: a failure is logged
    /// and the days are simply counted again next time.</summary>
    public bool Save(int year, int month, string zoneId, string prices, IReadOnlyDictionary<DateOnly, DayStats> days)
    {
        var path = PathFor(year, month);
        try
        {
            var file = new MonthFile
            {
                Schema = Schema,
                Month = $"{year:D4}-{month:D2}",
                Zone = zoneId,
                Prices = prices,
                Days = days.Where(d => d.Key.Year == year && d.Key.Month == month)
                    .OrderBy(d => d.Key)
                    .ToDictionary(d => d.Key.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), d => (DayStats?)d.Value)
            };
            System.IO.Directory.CreateDirectory(Directory);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(file, Json));
            return true;
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError($"DailyStatsStore.Save({Path.GetFileName(path)})", ex);
            return false;
        }
    }

    private sealed class MonthFile
    {
        public int Schema { get; set; }
        public string? Month { get; set; }
        public string? Zone { get; set; }
        public string? Prices { get; set; }
        public Dictionary<string, DayStats?>? Days { get; set; }
    }
}
