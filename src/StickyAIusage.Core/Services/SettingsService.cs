using System.IO;
using System.Text.Json;

namespace StickyAIusage.Services;

/// <summary>User-configurable options. Persisted as
/// plain JSON under the app data folder (AppPaths) so settings survive restarts
/// without needing an installer or registry access.</summary>
public sealed class AppSettings
{
    public bool ShowMonthlySpend { get; set; } = true;
    public bool ShowSessionCost { get; set; } = true;

    /// <summary>Day target (monthly spend cap accounts only): today's spend
    /// against a daily target in dollars. The widget can show any of the
    /// amount, the % of the target and a progress ring; with none of them on,
    /// or the whole thing off, the pill leaves it out.</summary>
    public bool ShowDayTarget { get; set; } = true;
    public decimal DayTargetUsd { get; set; } = 10m;
    public bool ShowDayTargetAmount { get; set; } = true;
    public bool ShowDayTargetPercent { get; set; } = true;
    public bool ShowDayTargetRing { get; set; } = true;
    /// <summary>Start at sign-in: the HKCU Run key on Windows, a LaunchAgent
    /// on macOS. Keeps its v1 name so existing settings files still load.</summary>
    public bool StartWithWindows { get; set; }

    /// <summary>Last user-dragged position of the strip's bottom-right corner,
    /// in device-independent pixels. Null until the user drags it at least
    /// once, in which case the app docks next to the tray as before.</summary>
    public double? WidgetRight { get; set; }
    public double? WidgetBottom { get; set; }

    /// <summary>macOS: last dragged position of the pill's top-right corner,
    /// in screen points. The Mac widget hangs down from the menu bar, so it
    /// anchors at the top instead of the bottom.</summary>
    public double? MacWidgetRight { get; set; }
    public double? MacWidgetTop { get; set; }

    /// <summary>Light or dark appearance: "dark" (the default), "light" or
    /// "system" (follow the OS). The sun/moon button in the flyout sets
    /// light or dark; Settings can put it on "system".</summary>
    public string Theme { get; set; } = "dark";

    /// <summary>Which fields the session cards show, and how they're grouped
    /// into colored rows: "model,effort|in,out,cached,total". Null means the
    /// shipped grouping (CardLayout.Default).</summary>
    public string? CardFields { get; set; }

    /// <summary>The laid-out blocks under a card's chips.</summary>
    public bool ShowContextBar { get; set; } = true;
    public bool ShowGitDetails { get; set; } = true;

    /// <summary>Whether live polling is currently running. Persisted so a
    /// user-paused widget stays paused across restarts.</summary>
    public bool IsRunning { get; set; } = true;

    // Color thresholds; Settings → Usage alerts changes them.
    public double YellowThresholdPercent { get; set; } = 50;
    public double OrangeThresholdPercent { get; set; } = 80;
    public double RedThresholdPercent { get; set; } = 95;
}

public static class AppSettingsExtensions
{
    /// <summary>The session card layout this settings file describes.</summary>
    public static ViewModels.CardLayout CardLayout(this AppSettings settings) =>
        ViewModels.CardLayout.Parse(settings.CardFields, settings.ShowContextBar, settings.ShowGitDetails);

    /// <summary>Stores a layout (without saving the file).</summary>
    public static void SetCardLayout(this AppSettings settings, ViewModels.CardLayout layout)
    {
        settings.CardFields = layout.ToSetting();
        settings.ShowContextBar = layout.ShowContextBar;
        settings.ShowGitDetails = layout.ShowGitDetails;
    }
}

public static class SettingsService
{
    private static string SettingsPath => AppPaths.SettingsPath;

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(SettingsPath))
                return new AppSettings();

            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings(); // Corrupt/missing settings should never block startup.
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            var dir = Path.GetDirectoryName(SettingsPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort; failing to persist settings shouldn't crash the app.
        }
    }
}
