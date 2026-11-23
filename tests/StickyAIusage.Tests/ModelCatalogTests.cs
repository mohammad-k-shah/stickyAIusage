using System.Text.Json;
using System.Text.Json.Nodes;
using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>
/// The price list as data (data/models.json, embedded). Expected values are the
/// published list prices (platform.claude.com/docs/en/about-claude/pricing and the
/// models overview, both checked 2026-11-17), written out here by hand so a wrong
/// number in the JSON fails a test.
/// </summary>
public class ModelCatalogTests
{
    private static ModelCatalog BuiltIn => ModelCatalog.BuiltIn;

    [Theory]
    // id, input, output, cache read, 5-minute write, 1-hour write, context window
    [InlineData("claude-fable-5-2", 8, 40, 0.20, 10, 16, 1_000_000L)]
    [InlineData("claude-mythos-5-2", 8, 40, 0.20, 10, 16, 1_000_000L)]
    [InlineData("claude-fable-5-1", 10, 50, 0.25, 12.5, 20, 1_000_000L)]
    [InlineData("claude-mythos-5-1", 10, 50, 0.25, 12.5, 20, 1_000_000L)]
    [InlineData("claude-fable-5", 10, 50, 1, 12.5, 20, 1_000_000L)]
    [InlineData("claude-mythos-5", 10, 50, 1, 12.5, 20, 1_000_000L)]
    [InlineData("claude-opus-5-5", 4, 20, 0.20, 5, 8, 1_000_000L)]
    [InlineData("claude-opus-5", 5, 25, 0.50, 6.25, 10, 1_000_000L)]
    [InlineData("claude-opus-4-8", 5, 25, 0.50, 6.25, 10, 1_000_000L)]
    [InlineData("claude-opus-4-7", 5, 25, 0.50, 6.25, 10, 1_000_000L)]
    [InlineData("claude-opus-4-6[1m]", 5, 25, 0.50, 6.25, 10, 1_000_000L)]
    [InlineData("claude-opus-4-5-20251101", 5, 25, 0.50, 6.25, 10, 200_000L)]
    [InlineData("claude-opus-4-1-20250805", 15, 75, 1.50, 18.75, 30, 200_000L)]
    [InlineData("claude-opus-4-20250514", 15, 75, 1.50, 18.75, 30, 200_000L)]
    [InlineData("claude-3-opus-20240229", 15, 75, 1.50, 18.75, 30, 200_000L)]
    [InlineData("claude-sonnet-5-5", 2, 10, 0.10, 2.5, 4, 1_000_000L)]
    [InlineData("claude-sonnet-5", 2, 10, 0.20, 2.5, 4, 1_000_000L)]
    [InlineData("claude-sonnet-4-6", 3, 15, 0.30, 3.75, 6, 1_000_000L)]
    [InlineData("claude-sonnet-4-5-20250929", 3, 15, 0.30, 3.75, 6, 200_000L)]
    [InlineData("claude-sonnet-4-20250514", 3, 15, 0.30, 3.75, 6, 200_000L)]
    [InlineData("claude-3-7-sonnet-20250219", 3, 15, 0.30, 3.75, 6, 200_000L)]
    [InlineData("claude-haiku-5-5", 0.10, 0.50, 0.01, 0.125, 0.20, 1_000_000L)]
    [InlineData("claude-haiku-4-5-20251001", 1, 5, 0.10, 1.25, 2, 200_000L)]
    [InlineData("claude-3-5-haiku-20241022", 0.80, 4, 0.08, 1, 1.6, 200_000L)]
    [InlineData("claude-3-haiku-20240307", 0.25, 1.25, 0.03, 0.3125, 0.5, 200_000L)]
    // Cloud ids: Bedrock's prefix and suffix, Vertex's "@" date.
    [InlineData("us.anthropic.claude-opus-4-5-20251101-v1:0", 5, 25, 0.50, 6.25, 10, 200_000L)]
    [InlineData("claude-opus-4-5@20251101", 5, 25, 0.50, 6.25, 10, 200_000L)]
    [InlineData("anthropic.claude-haiku-5-5", 0.10, 0.50, 0.01, 0.125, 0.20, 1_000_000L)]
    public void Every_listed_model_has_its_published_prices_and_context_window(string model, double input,
        double output, double cacheRead, double write5m, double write1h, long contextWindow)
    {
        var match = BuiltIn.Find(model);
        Assert.True(match.IsKnown, model);
        var p = match.Row.Prices;
        Assert.Equal(((decimal)input, (decimal)output, (decimal)cacheRead, (decimal)write5m, (decimal)write1h),
            (p.Input, p.Output, p.CacheRead, p.CacheWrite5m, p.CacheWrite1h));
        Assert.Equal(contextWindow, match.Row.ContextWindow);
    }

    [Fact]
    public void The_public_lookups_read_the_same_catalog()
    {
        Assert.Equal(4m, PriceTable.For("claude-opus-5-5").Input);
        Assert.Equal(1_000_000L, ModelContextWindows.Lookup("claude-haiku-5-5"));
        Assert.Null(ModelContextWindows.Lookup(""));
    }

    [Fact]
    public void Haiku_5_5_prompts_above_100000_tokens_pay_the_long_prompt_prices()
    {
        // 100,000 prompt tokens: standard ($0.10 in, $0.50 out per MTok).
        Assert.Equal(0.01m + 0.50m, PriceTable.CostOf("claude-haiku-5-5", 100_000, 1_000_000, 0, 0, 0));
        // 100,001: every token of that response at $0.50 in / $2.50 out.
        Assert.Equal(100_001 * 0.50m / 1_000_000m + 2.50m, PriceTable.CostOf("claude-haiku-5-5", 100_001, 1_000_000, 0, 0, 0));
    }

    [Fact]
    public void The_prompt_size_counts_cache_reads_and_writes_too()
    {
        // 1 input + 60,000 cache reads + 40,000 5-minute writes = 100,001 → long prices:
        // 1 × 0.50 + 60,000 × 0.05 + 40,000 × 0.625, per million.
        Assert.Equal((0.50m + 3_000m + 25_000m) / 1_000_000m,
            PriceTable.CostOf("claude-haiku-5-5", 1, 0, 60_000, 40_000, 0));
        // The same split one token shorter stays standard: 60,000 × 0.01 + 39,999 × 0.125.
        Assert.Equal((0.10m + 600m + 39_999 * 0.125m) / 1_000_000m,
            PriceTable.CostOf("claude-haiku-5-5", 1, 0, 60_000, 39_999, 0));
    }

    [Theory]
    [InlineData("claude-fable-5-2", 96)]   // $16 + $80
    [InlineData("claude-mythos-5-2", 96)]
    [InlineData("claude-opus-5-5", 48)]    // $8 + $40
    [InlineData("claude-opus-5", 60)]      // $10 + $50
    [InlineData("claude-opus-4-8", 60)]
    [InlineData("claude-opus-4-7", 30)]    // no fast pricing
    [InlineData("claude-fable-5-1", 60)]   // no fast pricing
    [InlineData("claude-sonnet-5-5", 12)]  // no fast pricing
    public void Fast_mode_doubles_the_models_that_have_it(string model, double expected) =>
        Assert.Equal((decimal)expected, PriceTable.CostOf(model, 1_000_000, 1_000_000, 0, 0, 0, fast: true));

    [Fact]
    public void Us_only_inference_adds_ten_percent_on_top_of_fast_mode()
    {
        Assert.Equal(26.4m, PriceTable.CostOf("claude-opus-5-5", 1_000_000, 1_000_000, 0, 0, 0, usOnly: true));
        Assert.Equal(52.8m, PriceTable.CostOf("claude-opus-5-5", 1_000_000, 1_000_000, 0, 0, 0, fast: true, usOnly: true));
    }

    [Theory]
    [InlineData("something-new")]
    [InlineData("gpt-5")]
    [InlineData("")]
    [InlineData(null)]
    public void A_model_of_no_listed_family_gets_the_fallback_and_a_rough_cost(string? model)
    {
        var estimate = PriceTable.Estimate(model, 1_000_000, 1_000_000, 0, 0, 0);
        Assert.True(estimate.IsRough);
        Assert.Equal(18m, estimate.Usd); // fallback $3 + $15
        Assert.Same(BuiltIn.Fallback, BuiltIn.Find(model).Row);
        Assert.Equal(200_000L, BuiltIn.Fallback.ContextWindow);
    }

    [Theory]
    [InlineData("claude-opus-6", "Claude Opus 5.5")]
    [InlineData("claude-sonnet-6-1", "Claude Sonnet 5.5")]
    [InlineData("claude-haiku-6", "Claude Haiku 5.5")]
    [InlineData("claude-mythos-preview", "Claude Fable 5.2, Claude Mythos 5.2")]
    public void A_newer_version_of_a_listed_family_gets_its_newest_row_and_a_rough_cost(string model, string row)
    {
        var match = BuiltIn.Find(model);
        Assert.False(match.IsKnown);
        Assert.Equal(row, match.Row.Name);
        Assert.True(PriceTable.Estimate(model, 1, 1, 0, 0, 0).IsRough);
    }

    [Fact]
    public void A_listed_model_is_not_rough() =>
        Assert.False(PriceTable.Estimate("claude-sonnet-5-5", 1, 1, 0, 0, 0).IsRough);

    [Fact]
    public void An_unknown_model_is_logged_once_per_id()
    {
        var catalog = Parse(MinimalJson());
        var lines = new List<string>();
        catalog.Log = lines.Add;

        catalog.Find("claude-future-1");
        catalog.Find("claude-future-1");
        catalog.Find("CLAUDE-FUTURE-1");
        catalog.Find("claude-sonnet-5");
        catalog.Find("claude-other-2");

        Assert.Equal(2, lines.Count);
        Assert.Contains("claude-future-1", lines[0]);
        Assert.Contains("claude-other-2", lines[1]);
    }

    [Theory]
    [InlineData("claude-opus-4-6", 4.6)]
    [InlineData("claude-sonnet-5", 5)]
    [InlineData("claude-sonnet-4-6[1m]", 4.6)]
    [InlineData("claude-3-5-haiku-20241022", 3.5)]
    [InlineData("claude-opus-4-20250514", 4)]
    [InlineData("claude-opus-4-5@20251101", 4.5)]
    [InlineData("us.anthropic.claude-sonnet-4-5-20250929-v1:0", 4.5)]
    [InlineData("claude-mythos-preview", 0)]
    [InlineData("", 0)]
    public void Versions_are_read_from_the_id(string id, double expected) =>
        Assert.Equal((decimal)expected, ModelCatalog.Version(id));

    // ------------------------------------------------------------- the file

    [Fact]
    public void The_embedded_catalog_is_the_repository_data_file()
    {
        var repoFile = FindRepoFile(Path.Combine("data", "models.json"));
        using var stream = typeof(ModelCatalog).Assembly.GetManifestResourceStream(ModelCatalog.ResourceName)!;
        using var reader = new StreamReader(stream);
        Assert.Equal(File.ReadAllText(repoFile).ReplaceLineEndings(), reader.ReadToEnd().ReplaceLineEndings());
    }

    [Fact]
    public void The_embedded_catalog_is_valid_dated_and_sourced()
    {
        Assert.True(BuiltIn.Models.Count >= ModelCatalog.MinRows);
        // Dated: not before the first checked list, and (like a download) not after tomorrow. A price update moves it.
        Assert.InRange(BuiltIn.Updated, new DateOnly(2026, 11, 17), DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1));
        Assert.Equal("https://platform.claude.com/docs/en/about-claude/pricing", BuiltIn.Source);
        Assert.Equal("built-in", BuiltIn.Origin);
        Assert.All(BuiltIn.Models, row => Assert.False(row.CacheReadDerived, row.Name)); // the page lists every cache price
    }

    [Fact]
    public void No_two_rows_of_the_embedded_catalog_cover_the_same_model()
    {
        // The parser refuses overlapping rows, so the embedded list parsing at all
        // (BuiltIn) proves it has none; this checks the parser does refuse them.
        var json = JsonNode.Parse(File.ReadAllText(FindRepoFile(Path.Combine("data", "models.json"))))!;
        var models = json["models"]!.AsArray();
        models.Add(models[0]!.DeepClone()); // a duplicate of the first row
        Assert.False(ModelCatalog.TryParse(json.ToJsonString(), out _, out var problem));
        Assert.Contains("both cover the same versions", problem);
    }

    [Theory]
    [InlineData("schema", "2", "schema must be 1")]
    [InlineData("updated", "\"17 November\"", "\"updated\" must be a date")]
    [InlineData("models", "[]", "only 0 model rows")]
    [InlineData("fallback", "null", "the fallback row is not an object")]
    public void A_catalog_with_a_bad_top_level_value_is_refused(string property, string value, string expected)
    {
        var json = JsonNode.Parse(MinimalJson())!;
        json[property] = JsonNode.Parse(value);
        Assert.False(ModelCatalog.TryParse(json.ToJsonString(), out var catalog, out var problem));
        Assert.Null(catalog);
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("input", "1001", "\"input\" must be a price between 0 and 1000")]
    [InlineData("input", "-1", "\"input\" must be a price")]
    [InlineData("output", "\"ten\"", "\"output\" must be a price")]
    [InlineData("cacheRead", "5000", "\"cacheRead\" must be a price")]
    [InlineData("match", "\"(unclosed\"", "\"match\" is not a valid pattern")]
    [InlineData("match", "\" \"", "has no \"match\"")]
    [InlineData("contextWindow", "0", "\"contextWindow\" must be a whole number")]
    [InlineData("contextWindow", "1.5", "\"contextWindow\" must be a whole number")]
    [InlineData("fastMultiplier", "50", "\"fastMultiplier\" must be between 1 and 10")]
    [InlineData("maxVersion", "1", "the version range is empty")]
    public void A_row_with_a_bad_value_is_refused(string property, string value, string expected)
    {
        var json = JsonNode.Parse(MinimalJson())!;
        json["models"]![0]![property] = JsonNode.Parse(value);
        Assert.False(ModelCatalog.TryParse(json.ToJsonString(), out _, out var problem));
        Assert.Contains(expected, problem);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("")]
    public void Text_that_is_no_catalog_is_refused(string text) =>
        Assert.False(ModelCatalog.TryParse(text, out _, out _));

    [Fact]
    public void Unknown_properties_are_ignored_and_a_missing_cache_read_is_derived()
    {
        var json = JsonNode.Parse(MinimalJson())!;
        json["somethingNew"] = "ignored";
        json["models"]![0]!["note"] = "ignored too";
        json["models"]![0]!.AsObject().Remove("cacheRead");
        Assert.True(ModelCatalog.TryParse(json.ToJsonString(), out var catalog, out var problem), problem);
        var row = catalog!.Find("claude-sonnet-5").Row;
        Assert.True(row.CacheReadDerived);
        Assert.Equal(0.2m, row.Prices.CacheRead); // 0.1 × $2
    }

    [Fact]
    public void A_row_can_override_the_cache_write_rules()
    {
        var json = JsonNode.Parse(MinimalJson())!;
        json["models"]![0]!["cacheWrite5m"] = 3;
        json["models"]![0]!["cacheWrite1h"] = 5;
        Assert.True(ModelCatalog.TryParse(json.ToJsonString(), out var catalog, out _));
        var p = catalog!.Find("claude-sonnet-5").Row.Prices;
        Assert.Equal((3m, 5m), (p.CacheWrite5m, p.CacheWrite1h));
    }

    // ---------------------------------------------- built-in vs downloaded

    [Fact]
    public void A_valid_newer_downloaded_list_wins_over_the_built_in_one()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine(ModelCatalog.CacheFileName), MinimalJson(updated: DayAfterBuiltIn));
        var cached = ModelCatalog.ReadCached(dir.Path);
        Assert.NotNull(cached);
        Assert.Equal("downloaded", cached!.Origin);
        Assert.Same(cached, ModelCatalog.ChooseNewer(BuiltIn, cached));
    }

    [Fact]
    public void An_older_or_same_day_downloaded_list_loses_to_the_built_in_one()
    {
        var older = Parse(MinimalJson(updated: "2026-01-01"));
        var sameDay = Parse(MinimalJson(updated: BuiltIn.Updated.ToString("yyyy-MM-dd")));
        Assert.Same(BuiltIn, ModelCatalog.ChooseNewer(BuiltIn, older));
        Assert.Same(BuiltIn, ModelCatalog.ChooseNewer(BuiltIn, sameDay));
        Assert.Same(BuiltIn, ModelCatalog.ChooseNewer(BuiltIn, null));
    }

    [Fact]
    public void An_invalid_or_missing_downloaded_list_is_ignored()
    {
        using var dir = new TempDir();
        Assert.Null(ModelCatalog.ReadCached(dir.Path));
        File.WriteAllText(dir.Combine(ModelCatalog.CacheFileName), "{ \"schema\": 1 }");
        Assert.Null(ModelCatalog.ReadCached(dir.Path));
        File.WriteAllText(dir.Combine(ModelCatalog.CacheFileName), new string(' ', ModelCatalog.MaxBytes + 1));
        Assert.Null(ModelCatalog.ReadCached(dir.Path));
    }

    // ---------------------------------------------------------- helpers

    /// <summary>A day newer than the repository's price list, whatever date a price update gives it.</summary>
    internal static string DayAfterBuiltIn => BuiltIn.Updated.AddDays(1).ToString("yyyy-MM-dd");

    internal static ModelCatalog Parse(string json) =>
        ModelCatalog.TryParse(json, out var catalog, out var problem) ? catalog! : throw new Xunit.Sdk.XunitException(problem);

    /// <summary>A small valid catalog: five Sonnet rows by version, and a fallback.</summary>
    internal static string MinimalJson(string updated = "2026-11-17", decimal sonnet5Input = 2m)
    {
        object Row(string name, decimal min, decimal max, decimal input) => new Dictionary<string, object>
        {
            ["name"] = name, ["match"] = "sonnet", ["minVersion"] = min, ["maxVersion"] = max,
            ["input"] = input, ["output"] = input * 5, ["cacheRead"] = input / 10, ["contextWindow"] = 1_000_000
        };
        return JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["schema"] = 1,
            ["updated"] = updated,
            ["source"] = "test",
            ["models"] = new[]
            {
                Row("Sonnet 5", 5, 5, sonnet5Input), Row("Sonnet 4.6", 4.6m, 4.6m, 3), Row("Sonnet 4.5", 4.5m, 4.5m, 3),
                Row("Sonnet 4", 4, 4, 3), Row("Sonnet 3", 0, 3.9m, 3)
            },
            ["fallback"] = new Dictionary<string, object> { ["input"] = 3, ["output"] = 15, ["contextWindow"] = 200_000 }
        });
    }

    internal static string FindRepoFile(string relative)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, relative);
            if (File.Exists(candidate))
                return candidate;
        }
        throw new FileNotFoundException("Not found above the test folder: " + relative);
    }
}
