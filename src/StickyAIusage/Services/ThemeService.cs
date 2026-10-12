using System.Windows;
using Microsoft.Win32;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Services;

/// <summary>
/// Light/dark appearance for the whole app, the counterpart of the macOS
/// ThemeService. The preference is "dark" by default; on "system" Windows
/// records the app appearance in HKCU ...\Themes\Personalize\AppsUseLightTheme
/// and raises a user-preference change when it's switched, so the widget
/// follows along without a restart. The sun/moon button pins light or dark.
///
/// The colors live in Themes/Tokens.Dark.xaml and Tokens.Light.xaml; this
/// swaps which one is merged as the application's first dictionary, which
/// repaints everything bound with DynamicResource. Brushes that code sets by
/// hand are re-applied by the windows when <see cref="Changed"/> fires.
/// </summary>
public static class ThemeService
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    private static AppSettings? _settings;

    /// <summary>Whether what's on screen right now is the dark theme.</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>Raised after the theme changed, on the UI thread.</summary>
    public static event Action? Changed;

    public static void Initialize(AppSettings settings)
    {
        _settings = settings;
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            // The appearance switch arrives as a General preference change.
            if (e.Category is UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle
                && Preference == ThemePreference.System)
                Application.Current?.Dispatcher.Invoke(Apply);
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

    private static bool SystemIsDark
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey);
                return key?.GetValue("AppsUseLightTheme") is not int light || light == 0;
            }
            catch
            {
                return true; // Unreadable (policy, older Windows): keep the dark theme.
            }
        }
    }

    /// <summary>Test hook: switches the palette without touching the saved
    /// settings, so the screenshot tests can render both themes.</summary>
    internal static void ApplyForTests(bool dark) => Apply(dark);

    private static void Apply() => Apply(ThemePreferences.IsDark(Preference, SystemIsDark));

    private static void Apply(bool dark)
    {
        if (Application.Current is not { } app)
            return;

        var source = new Uri($"/StickyAIusage;component/Themes/Tokens.{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative);
        var dictionaries = app.Resources.MergedDictionaries;
        if (dictionaries.Count > 0 && dictionaries[0].Source == source)
            return;

        var tokens = new ResourceDictionary { Source = source };
        if (dictionaries.Count == 0)
            dictionaries.Add(tokens);
        else
            dictionaries[0] = tokens;

        IsDark = dark;
        Changed?.Invoke();
    }
}
