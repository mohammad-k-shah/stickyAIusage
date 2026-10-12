using StickyAIusage.Services;

namespace StickyAIusage.Tests;

/// <summary>List prices from platform.claude.com/docs/en/about-claude/pricing (2026-09-19).</summary>
public class PriceTableTests
{
    [Theory]
    [InlineData("claude-opus-5", 5, 25, 0.5)]
    [InlineData("claude-opus-4-8", 5, 25, 0.5)]
    [InlineData("claude-opus-4-6[1m]", 5, 25, 0.5)]
    [InlineData("claude-opus-4-5-20251101", 5, 25, 0.5)]
    [InlineData("claude-opus-4-1-20250805", 15, 75, 1.5)]
    [InlineData("claude-opus-4-20250514", 15, 75, 1.5)]
    [InlineData("claude-sonnet-5", 2, 10, 0.2)]
    [InlineData("claude-sonnet-4-6", 3, 15, 0.3)]
    [InlineData("claude-3-5-sonnet-20241022", 3, 15, 0.3)]
    [InlineData("claude-haiku-4-5-20251001", 1, 5, 0.1)]
    [InlineData("claude-3-5-haiku-20241022", 0.8, 4, 0.08)]
    [InlineData("claude-fable-5-1", 10, 50, 0.25)]
    [InlineData("claude-fable-5", 10, 50, 1)]
    [InlineData("something-new", 3, 15, 0.3)]
    public void Model_prices(string model, double input, double output, double cacheRead)
    {
        var p = PriceTable.For(model);
        Assert.Equal(((decimal)input, (decimal)output, (decimal)cacheRead), (p.Input, p.Output, p.CacheRead));
    }

    [Fact]
    public void One_hour_cache_writes_cost_2x_input_and_five_minute_ones_1_25x()
    {
        // Opus 5: 1M 5m-writes = $6.25, 1M 1h-writes = $10.
        Assert.Equal(6.25m, PriceTable.CostOf("claude-opus-5", 0, 0, 0, 1_000_000, 0));
        Assert.Equal(10m, PriceTable.CostOf("claude-opus-5", 0, 0, 0, 0, 1_000_000));
    }

    [Fact]
    public void Fast_mode_doubles_opus_5_and_us_only_adds_ten_percent()
    {
        Assert.Equal(60m, PriceTable.CostOf("claude-opus-5", 1_000_000, 1_000_000, 0, 0, 0, fast: true)); // $10 + $50
        Assert.Equal(18m, PriceTable.CostOf("claude-sonnet-4-6", 1_000_000, 1_000_000, 0, 0, 0, fast: true)); // no fast pricing
        Assert.Equal(33m, PriceTable.CostOf("claude-opus-5", 1_000_000, 1_000_000, 0, 0, 0, usOnly: true));
    }

    [Fact]
    public void Session_cost_is_summed_per_response_at_each_responses_own_model_and_cache_lifetime()
    {
        using var dir = new TempDir();
        var id = Guid.NewGuid().ToString();
        var path = dir.Combine("proj", id + ".jsonl");
        var now = DateTimeOffset.UtcNow;
        string Line(string model, long output, long write5m, long write1h) => System.Text.Json.JsonSerializer.Serialize(new
        {
            type = "assistant", sessionId = id, cwd = "/work/acme-webapp", timestamp = now.ToString("O"),
            message = new
            {
                id = Guid.NewGuid().ToString(), model,
                usage = new
                {
                    input_tokens = 0, output_tokens = output, cache_read_input_tokens = 0,
                    cache_creation_input_tokens = write5m + write1h,
                    cache_creation = new { ephemeral_5m_input_tokens = write5m, ephemeral_1h_input_tokens = write1h }
                }
            }
        });

        Transcript.Write(path,
            Line("claude-opus-5", 1_000_000, 0, 1_000_000),    // $25 + $10
            Line("claude-sonnet-5", 1_000_000, 1_000_000, 0));  // $10 + $2.50

        var session = ClaudeSessionLogReader.ReadSession(path)!;
        Assert.Equal(47.5m, session.EstimatedCostUsd);
        Assert.Equal("claude-sonnet-5", session.Model); // the model it's on now
    }
}
