namespace StickyAIusage.ViewModels;

/// <summary>What the user chose in Settings, as opposed to what's on screen:
/// <see cref="System"/> follows the OS appearance (including its automatic
/// sunset switch), the others pin one.</summary>
public enum ThemePreference { System, Light, Dark }

public static class ThemePreferences
{
    public static ThemePreference Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => ThemePreference.Light,
        "dark" => ThemePreference.Dark,
        _ => ThemePreference.System
    };

    public static string ToSetting(this ThemePreference preference) => preference switch
    {
        ThemePreference.Light => "light",
        ThemePreference.Dark => "dark",
        _ => "system"
    };

    /// <summary>What to show, given the preference and the OS appearance.</summary>
    public static bool IsDark(ThemePreference preference, bool systemIsDark) => preference switch
    {
        ThemePreference.Light => false,
        ThemePreference.Dark => true,
        _ => systemIsDark
    };

    /// <summary>What the sun/moon button switches to: the opposite of what's
    /// on screen now, pinned (so one click always visibly flips it, even when
    /// the preference was "system").</summary>
    public static ThemePreference Toggled(bool isDarkNow) => isDarkNow ? ThemePreference.Light : ThemePreference.Dark;
}
