using System.Reflection;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Services;

/// <summary>
/// Which build is running, as one line for the Settings footer and the
/// first line of every start in diagnostics.log: the version (with the commit
/// it was built from), the profile and the usage source. A bug report then
/// says what ran without anyone having to ask.
/// </summary>
public static class AppIdentity
{
    /// <summary>"2.0.0 (3967cd3)": the build's version and the first seven
    /// characters of its commit, from the assembly's informational version
    /// ("2.0.0+3967cd3…", which the SDK stamps from git; a release passes the
    /// tag as the version).</summary>
    public static string Version(Assembly? assembly = null) =>
        FormatVersion((assembly ?? Assembly.GetEntryAssembly() ?? typeof(AppIdentity).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>The build's version as a release version ("2.0.0", "2.0.0-beta.1"); null when it
    /// isn't one. The update check compares it with the published releases.</summary>
    public static Updates.SemanticVersion? ReleaseVersion(Assembly? assembly = null) =>
        Updates.SemanticVersion.Parse((assembly ?? Assembly.GetEntryAssembly() ?? typeof(AppIdentity).Assembly)
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    internal static string FormatVersion(string? informational)
    {
        if (string.IsNullOrWhiteSpace(informational))
            return "unknown version";
        var plus = informational.IndexOf('+');
        if (plus < 0)
            return informational;
        var commit = informational[(plus + 1)..];
        return commit.Length == 0 ? informational[..plus] : $"{informational[..plus]} ({commit[..Math.Min(7, commit.Length)]})";
    }

    /// <summary>"default profile", "profile dev" or "custom data folder".</summary>
    public static string Profile(AppLocations locations) =>
        locations.DataDirectoryOverridden ? "custom data folder"
        : locations.Profile is { } profile ? $"profile {profile}"
        : "default profile";

    /// <summary>"Sticky AI Usage 2.0.0 (3967cd3) · default profile · usage source: Plugin".</summary>
    public static string Describe(string version, AppLocations locations, AppSettings settings) =>
        $"Sticky AI Usage {version} · {Profile(locations)} · usage source: " +
        UsageSourceOptions.For(ClaudeSourceModes.Parse(settings.ClaudeSource)).Label;

    /// <summary>The log's first line of a start: "App started (Windows): Sticky AI Usage 2.0.0 (3967cd3) ·
    /// default profile · usage source: Plugin · .NET 10.0.12".</summary>
    public static string StartLine(string platform, string version, AppLocations locations, AppSettings settings) =>
        $"App started ({platform}): {Describe(version, locations, settings)} · .NET {Environment.Version}";
}
