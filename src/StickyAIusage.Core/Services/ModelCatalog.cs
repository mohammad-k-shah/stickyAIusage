using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace StickyAIusage.Services;

/// <summary>List prices for one model row, in US dollars per million tokens.</summary>
public sealed record ModelPrices(decimal Input, decimal Output, decimal CacheRead, decimal CacheWrite5m, decimal CacheWrite1h);

/// <summary>Higher prices for a response whose prompt is above a size (Claude
/// Haiku 5.5 above 100,000 tokens).</summary>
public sealed record LongPromptTier(long AboveTokens, ModelPrices Prices);

/// <summary>One row of the price list: which model ids it covers and what they cost.</summary>
public sealed record ModelRow(
    string Name,
    string Match,
    decimal MinVersion,
    decimal? MaxVersion,
    ModelPrices Prices,
    decimal? FastMultiplier,
    long ContextWindow,
    LongPromptTier? LongPrompt,
    bool CacheReadDerived)
{
    internal Regex? Pattern { get; init; }

    internal bool Covers(decimal version) => version >= MinVersion && (MaxVersion is null || version <= MaxVersion);

    /// <summary>The prices of a response whose prompt (input, cache reads and
    /// cache writes together) has <paramref name="promptTokens"/> tokens.</summary>
    public ModelPrices PricesFor(long promptTokens) =>
        LongPrompt is { } tier && promptTokens > tier.AboveTokens ? tier.Prices : Prices;
}

/// <summary>The row a model id resolved to. <see cref="IsKnown"/> is false when
/// the price list doesn't list that model: either a newer version of a known
/// family (priced like the family's newest row) or no family at all (the
/// fallback row). Costs from such a row are rough.</summary>
public readonly record struct ModelMatch(ModelRow Row, bool IsKnown);

/// <summary>
/// The model price list and context windows, as data: <c>data/models.json</c>
/// at the repository root, embedded in the app, so a new model or a price change
/// is a JSON-only pull request. When the daily refresh is on
/// (<see cref="ModelCatalogUpdater"/>), a newer copy downloaded to
/// <c>cache/models.json</c> takes over. Parsed once; lookups are cheap and
/// thread-safe.
/// </summary>
public sealed class ModelCatalog
{
    public const int SupportedSchema = 1;
    public const string ResourceName = "StickyAIusage.Resources.models.json";
    public const string CacheFileName = "models.json";

    /// <summary>A downloaded price list larger than this is refused unread.</summary>
    public const int MaxBytes = 256 * 1024;

    internal const int MinRows = 5;
    internal const decimal MaxPrice = 1000m;
    internal const long MaxContextWindow = 100_000_000;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    private static readonly object CurrentLock = new();
    private static readonly Lazy<ModelCatalog> BuiltInCatalog = new(LoadBuiltIn);
    private static volatile ModelCatalog? _current;

    private readonly ConcurrentDictionary<string, ModelMatch> _matches = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _loggedUnknown = new(StringComparer.OrdinalIgnoreCase);

    private ModelCatalog(DateOnly updated, string? source, IReadOnlyList<ModelRow> models, ModelRow fallback,
        decimal cacheWrite5m, decimal cacheWrite1h, decimal usOnly)
    {
        Updated = updated;
        Source = source;
        Models = models;
        Fallback = fallback;
        CacheWrite5mMultiplier = cacheWrite5m;
        CacheWrite1hMultiplier = cacheWrite1h;
        UsOnlyMultiplier = usOnly;
    }

    /// <summary>The date the prices were last checked against the source.</summary>
    public DateOnly Updated { get; }

    /// <summary>Where the prices come from (the pricing page).</summary>
    public string? Source { get; }

    public IReadOnlyList<ModelRow> Models { get; }

    /// <summary>Prices and context window for a model no row matches.</summary>
    public ModelRow Fallback { get; }

    public decimal CacheWrite5mMultiplier { get; }
    public decimal CacheWrite1hMultiplier { get; }

    /// <summary>US-only inference (<c>inference_geo: "us"</c>) costs this many times the list price.</summary>
    public decimal UsOnlyMultiplier { get; }

    /// <summary>"built-in" or "downloaded", for the log.</summary>
    public string Origin { get; private init; } = "built-in";

    /// <summary>Where unknown-model notes go; once per model id.</summary>
    internal Action<string> Log { get; set; } = DiagnosticLog.WriteInfo;

    /// <summary>The price list embedded in the app.</summary>
    public static ModelCatalog BuiltIn => BuiltInCatalog.Value;

    /// <summary>The price list in use: the built-in one, or a downloaded copy
    /// in the cache folder when it is valid and newer. Read on first use.</summary>
    public static ModelCatalog Current
    {
        get
        {
            if (_current is { } current)
                return current;
            lock (CurrentLock)
                return _current ??= ChooseNewer(BuiltIn, ReadCached(AppPaths.CacheDirectory));
        }
    }

    /// <summary>Makes <paramref name="catalog"/> the one in use (after a refresh).</summary>
    internal static void Use(ModelCatalog catalog)
    {
        lock (CurrentLock)
            _current = catalog;
    }

    /// <summary>Forgets the choice, so the next <see cref="Current"/> reads the cache folder again (tests).</summary>
    internal static void Reset()
    {
        lock (CurrentLock)
            _current = null;
    }

    /// <summary>A downloaded list dated after tomorrow (UTC) is a mistake: it would
    /// outrank every later built-in list, so it is refused.</summary>
    internal bool IsDatedInTheFuture(DateTimeOffset now) =>
        Updated > DateOnly.FromDateTime(now.UtcDateTime).AddDays(1);

    /// <summary>The newer of two price lists by <see cref="Updated"/>; the first on a tie.</summary>
    internal static ModelCatalog ChooseNewer(ModelCatalog builtIn, ModelCatalog? downloaded) =>
        downloaded is not null && downloaded.Updated > builtIn.Updated ? downloaded : builtIn;

    /// <summary>The downloaded copy in <paramref name="cacheDirectory"/>, or
    /// null when there is none or it is invalid (logged).</summary>
    internal static ModelCatalog? ReadCached(string cacheDirectory)
    {
        var path = Path.Combine(cacheDirectory, CacheFileName);
        try
        {
            if (!File.Exists(path))
                return null;
            if (new FileInfo(path).Length > MaxBytes)
            {
                DiagnosticLog.WriteInfo($"Prices: ignoring {CacheFileName} in the cache folder: larger than {MaxBytes / 1024} KB.");
                return null;
            }
            if (TryParse(File.ReadAllText(path), out var catalog, out var problem, origin: "downloaded"))
            {
                if (catalog!.IsDatedInTheFuture(DateTimeOffset.UtcNow))
                    problem = $"it is dated {catalog.Updated:yyyy-MM-dd}, in the future";
                else
                    return catalog;
            }
            DiagnosticLog.WriteInfo($"Prices: ignoring {CacheFileName} in the cache folder: {problem}.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ModelCatalog.ReadCached", ex);
        }
        return null;
    }

    private static ModelCatalog LoadBuiltIn()
    {
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ResourceName)
                           ?? throw new InvalidOperationException($"The embedded price list {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        // The build embeds the same file the tests validate
        // (The_embedded_catalog_is_the_repository_data_file and the price rows), so a
        // failure here is a packaging bug those tests catch; pricing with a guess
        // instead would hide it.
        return TryParse(reader.ReadToEnd(), out var catalog, out var problem)
            ? catalog!
            : throw new InvalidOperationException($"The embedded price list is invalid: {problem}.");
    }

    /// <summary>
    /// The row for a model id such as "claude-opus-5-5", "claude-sonnet-4-6[1m]",
    /// "claude-3-5-haiku-20241022" or "us.anthropic.claude-opus-4-5-20251101-v1:0":
    /// the first row (top-down) whose pattern is in the id and whose version range
    /// holds its version. A newer version of a listed family gets that family's
    /// newest row; an id of no listed family gets <see cref="Fallback"/>. Both are
    /// marked unknown and noted in the log once per id.
    /// </summary>
    public ModelMatch Find(string? modelId)
    {
        var id = (modelId ?? "").Trim();
        var match = _matches.GetOrAdd(id, Resolve);
        if (!match.IsKnown && id.Length > 0 && _loggedUnknown.TryAdd(id, 0))
            Log($"Prices: model \"{id}\" is not in the price list ({Origin}, {Updated:yyyy-MM-dd}); " +
                $"its cost is a rough estimate from the row \"{match.Row.Name}\".");
        return match;
    }

    private ModelMatch Resolve(string id)
    {
        var version = Version(id);
        ModelRow? newestOfFamily = null;
        foreach (var row in Models)
        {
            if (!IsMatch(row, id))
                continue;
            if (row.Covers(version))
                return new ModelMatch(row, true);
            if (newestOfFamily is null || row.MinVersion > newestOfFamily.MinVersion)
                newestOfFamily = row;
        }
        return new ModelMatch(newestOfFamily ?? Fallback, false);
    }

    private static bool IsMatch(ModelRow row, string id)
    {
        try
        {
            return row.Pattern?.IsMatch(id) == true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    /// <summary>"claude-opus-4-6" → 4.6, "claude-sonnet-5" → 5,
    /// "claude-3-5-haiku-20241022" → 3.5, "claude-opus-4-5@20251101" → 4.5;
    /// 0 if there's no version. Dates (more than two digits) are skipped.</summary>
    internal static decimal Version(string id)
    {
        id = id.ToLowerInvariant();
        var bracket = id.IndexOf('[');
        if (bracket >= 0) id = id[..bracket];
        var parts = id.Split('-', '@', '.', ':', '_', '/')
            .Where(p => p.Length is > 0 and <= 2 && p.All(char.IsAsciiDigit))
            .Take(2)
            .ToList();
        if (parts.Count == 0) return 0;
        var text = parts.Count == 1 ? parts[0] : $"{parts[0]}.{parts[1]}";
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    // ------------------------------------------------------------ parsing

    /// <summary>Parses and validates a price list: schema 1, a date, at least
    /// five rows, every price between 0 and 1,000, patterns that compile, no two
    /// rows with the same pattern and overlapping versions, a fallback row.
    /// Unknown properties are ignored, so a later app version can add some.</summary>
    public static bool TryParse(string json, out ModelCatalog? catalog, out string problem, string origin = "built-in")
    {
        catalog = null;
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip
            });
            catalog = Build(document.RootElement, origin);
            problem = "";
            return true;
        }
        catch (JsonException ex)
        {
            problem = "not valid JSON (" + ex.Message + ")";
        }
        catch (FormatException ex)
        {
            problem = ex.Message;
        }
        return false;
    }

    private static ModelCatalog Build(JsonElement root, string origin)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("not a JSON object");
        if (Number(root, "schema") is not { } schema || schema != SupportedSchema)
            throw new FormatException($"schema must be {SupportedSchema}");
        if (Text(root, "updated") is not { } updatedText ||
            !DateOnly.TryParseExact(updatedText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var updated))
            throw new FormatException("\"updated\" must be a date like 2026-11-17");

        var rules = root.TryGetProperty("rules", out var rulesElement) && rulesElement.ValueKind == JsonValueKind.Object
            ? rulesElement
            : default;
        var write5m = Multiplier(rules, "cacheWrite5m", 1.25m);
        var write1h = Multiplier(rules, "cacheWrite1h", 2m);
        var usOnly = Multiplier(rules, "usOnly", 1.1m);

        if (!root.TryGetProperty("models", out var modelsElement) || modelsElement.ValueKind != JsonValueKind.Array)
            throw new FormatException("\"models\" must be a list");
        var models = new List<ModelRow>();
        var index = 0;
        foreach (var element in modelsElement.EnumerateArray())
        {
            index++;
            models.Add(Row(element, $"model row {index}", write5m, write1h, needsMatch: true));
        }
        if (models.Count < MinRows)
            throw new FormatException($"only {models.Count} model rows (at least {MinRows} expected)");
        CheckOverlaps(models);

        if (!root.TryGetProperty("fallback", out var fallbackElement))
            throw new FormatException("\"fallback\" is missing");
        var fallback = Row(fallbackElement, "the fallback row", write5m, write1h, needsMatch: false);

        return new ModelCatalog(updated, Text(root, "source"), models, fallback, write5m, write1h, usOnly) { Origin = origin };
    }

    private static ModelRow Row(JsonElement element, string where, decimal write5m, decimal write1h, bool needsMatch)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new FormatException($"{where} is not an object");

        var match = Text(element, "match") ?? "";
        Regex? pattern = null;
        if (needsMatch)
        {
            if (match.Trim().Length == 0)
                throw new FormatException($"{where} has no \"match\"");
            try
            {
                pattern = new Regex(match, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
            }
            catch (ArgumentException)
            {
                throw new FormatException($"{where}: \"match\" is not a valid pattern");
            }
        }
        var name = Text(element, "name") ?? (needsMatch ? match : "Unknown model");

        var minVersion = Number(element, "minVersion") ?? 0;
        var maxVersion = Number(element, "maxVersion");
        if (minVersion < 0 || maxVersion < minVersion)
            throw new FormatException($"{where} ({name}): the version range is empty");

        var prices = Prices(element, where, name, write5m, write1h, out var derived);

        var fast = Number(element, "fastMultiplier");
        if (fast is not null and (< 1 or > 10))
            throw new FormatException($"{where} ({name}): \"fastMultiplier\" must be between 1 and 10");

        if (Number(element, "contextWindow") is not { } context || context < 1 || context > MaxContextWindow ||
            context != decimal.Truncate(context))
            throw new FormatException($"{where} ({name}): \"contextWindow\" must be a whole number of tokens");

        LongPromptTier? longPrompt = null;
        if (element.TryGetProperty("longPrompt", out var tier) && tier.ValueKind != JsonValueKind.Null)
        {
            if (tier.ValueKind != JsonValueKind.Object || Number(tier, "aboveTokens") is not { } above || above < 1)
                throw new FormatException($"{where} ({name}): \"longPrompt\" needs \"aboveTokens\"");
            longPrompt = new LongPromptTier((long)above, Prices(tier, where, name + ", long prompts", write5m, write1h, out _));
        }

        return new ModelRow(name, match, minVersion, maxVersion, prices, fast, (long)context, longPrompt, derived)
        {
            Pattern = pattern
        };
    }

    private static ModelPrices Prices(JsonElement element, string where, string name, decimal write5m, decimal write1h,
        out bool cacheReadDerived)
    {
        var input = Price(element, "input", where, name) ?? throw new FormatException($"{where} ({name}) has no \"input\" price");
        var output = Price(element, "output", where, name) ?? throw new FormatException($"{where} ({name}) has no \"output\" price");
        var cacheRead = Price(element, "cacheRead", where, name);
        cacheReadDerived = cacheRead is null;
        return new ModelPrices(input, output, cacheRead ?? input * 0.1m,
            Price(element, "cacheWrite5m", where, name) ?? input * write5m,
            Price(element, "cacheWrite1h", where, name) ?? input * write1h);
    }

    private static decimal? Price(JsonElement element, string property, string where, string name)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var price) || price < 0 || price > MaxPrice)
            throw new FormatException($"{where} ({name}): \"{property}\" must be a price between 0 and {MaxPrice:0}");
        return price;
    }

    private static decimal Multiplier(JsonElement rules, string property, decimal standard)
    {
        if (rules.ValueKind != JsonValueKind.Object || Number(rules, property) is not { } value)
            return standard;
        return value is >= 1 and <= 10
            ? value
            : throw new FormatException($"\"rules.{property}\" must be between 1 and 10");
    }

    /// <summary>Two rows with the same pattern must not both claim a version:
    /// the second would never be used, which hides a mistake in the list.</summary>
    private static void CheckOverlaps(IReadOnlyList<ModelRow> rows)
    {
        for (var i = 0; i < rows.Count; i++)
        for (var j = i + 1; j < rows.Count; j++)
        {
            var a = rows[i];
            var b = rows[j];
            if (!string.Equals(a.Match.Trim(), b.Match.Trim(), StringComparison.OrdinalIgnoreCase))
                continue;
            var aMax = a.MaxVersion ?? decimal.MaxValue;
            var bMax = b.MaxVersion ?? decimal.MaxValue;
            if (a.MinVersion <= bMax && b.MinVersion <= aMax)
                throw new FormatException($"\"{a.Name}\" and \"{b.Name}\" both cover the same versions");
        }
    }

    private static string? Text(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static decimal? Number(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var number)
            ? number
            : null;
}
