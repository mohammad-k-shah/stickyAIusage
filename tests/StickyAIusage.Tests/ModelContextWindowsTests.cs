using StickyAIusage.Services;

namespace StickyAIusage.Tests;

public class ModelContextWindowsTests
{
    [Theory]
    [InlineData("claude-sonnet-5", 1_000_000L)]
    [InlineData("claude-opus-5", 1_000_000L)]
    [InlineData("claude-opus-4-8", 1_000_000L)]
    [InlineData("claude-opus-4-6", 1_000_000L)]
    [InlineData("claude-sonnet-4-6", 1_000_000L)]
    [InlineData("claude-fable-5-1", 1_000_000L)]
    [InlineData("claude-haiku-4-5", 200_000L)]
    [InlineData("claude-opus-4-5-20251101", 200_000L)]
    [InlineData("claude-sonnet-4-5", 200_000L)]
    public void Known_models_map_to_their_context_window(string model, long expected) =>
        Assert.Equal(expected, ModelContextWindows.Lookup(model));

    [Fact]
    public void Missing_model_is_unknown() => Assert.Null(ModelContextWindows.Lookup(null));
}
