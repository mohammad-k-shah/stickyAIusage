using Avalonia;
using Avalonia.Platform;
using Avalonia.Styling;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac.Services;

/// <summary>
/// Light/dark appearance for the whole app. The preference is "system" by
/// default: macOS reports its current appearance through Avalonia's platform
/// settings and raises ColorValuesChanged when it changes, including the
/// automatic switch at sunset, so the widget follows along without a restart.
/// The sun/moon button pins light or dark instead (<see cref="Toggle"/>).
///
/// Colors themselves live in Theme.axaml's theme dictionaries; this only
/// selects the variant and tells the windows to re-apply the brushes they set
/// from code (DynamicResource covers the rest).
/// </summary>
public static class ThemeService
{
    private static AppSettings? _settings;

    /// <summary>Whether what's on screen right now is the dark theme.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the variant changed, on the UI thread.</summary>
    public static event Action? Changed;

    /// <summary>Applies the saved preference and starts following the OS.</summary>
    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        if (Application.Current?.PlatformSettings is { } platform)
            platform.ColorValuesChanged += (_, _) =>
            {
                if (Preference == ThemePreference.System)
                    Apply();
            };
        Apply();
    }

    public static ThemePreference Preference
    {
        get => ThemePreferences.Parse(_settings?.Theme);
        set
        {
            if (_settings is null || Preference == value)
                return;
            _settings.Theme = value.ToSetting();
            SettingsService.Save(_settings);
            Apply();
        }
    }

    /// <summary>The sun/moon button: pins the opposite of what's showing.</summary>
    public static void Toggle() => Preference = ThemePreferences.Toggled(IsDark);

    /// <summary>
    /// Looks a themed resource up for the variant on screen. Avalonia's
    /// element-level FindResource needs the element to be attached before it
    /// can resolve a theme dictionary, and these are wanted from constructors
    /// too, so ask the application directly.
    /// </summary>
    public static T Resource<T>(string key)
    {
        var app = Application.Current!;
        return app.TryGetResource(key, app.ActualThemeVariant, out var value) && value is T typed
            ? typed
            : throw new KeyNotFoundException($"Theme resource '{key}' is missing.");
    }

    private static bool SystemIsDark =>
        Application.Current?.PlatformSettings?.GetColorValues().ThemeVariant != PlatformThemeVariant.Light;

    /// <summary>Test hook: switches the variant without touching the saved
    /// settings, so the screenshot tests can render both themes.</summary>
    internal static void ApplyForTests(bool dark) => Apply(dark);

    private static void Apply() => Apply(ThemePreferences.IsDark(Preference, SystemIsDark));

    private static void Apply(bool dark)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (IsDark == dark)
            return;
        IsDark = dark;
        Controls.Palette.IsDark = dark;
        Changed?.Invoke();
    }
}
