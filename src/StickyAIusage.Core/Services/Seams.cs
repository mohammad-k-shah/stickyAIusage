using StickyAIusage.Forecasting;
using StickyAIusage.Models;

namespace StickyAIusage.Services;

/// <summary>Works out the pace of each window of a reading
/// (<see cref="UsageForecaster"/>; the demo's scripted pace in demo mode).</summary>
public interface IPaceForecaster
{
    /// <summary>One forecast per window of the reading; never throws.</summary>
    IReadOnlyList<PaceForecast> Update(ProviderUsage usage);
}

/// <summary>A session folder's git state and pull request for its card
/// (<see cref="GitInfoCache"/>; the demo's made-up repositories in demo mode).</summary>
public interface IGitInfoSource
{
    /// <summary>Null when the folder isn't a repository; never throws.</summary>
    GitInfo? Get(string workingDirectory);
}
