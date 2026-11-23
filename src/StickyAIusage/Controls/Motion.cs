using System.Windows;
using System.Windows.Media.Animation;

namespace StickyAIusage.Controls;

/// <summary>The small animations of the pill (the ring easing to a new value,
/// the pace chip fading in), and when to leave them out: when Windows asks
/// for fewer animations (Settings → Accessibility → Visual effects →
/// Animation effects off), and in the screenshot tests, which render at once.</summary>
internal static class Motion
{
    public static readonly Duration Length = new(TimeSpan.FromMilliseconds(300));

    /// <summary>Off in the UI tests, so a screenshot shows the final state.</summary>
    public static bool Enabled { get; set; } = true;

    public static bool Allowed => Enabled && SystemParameters.ClientAreaAnimation;

    public static IEasingFunction Easing { get; } = new CubicEase { EasingMode = EasingMode.EaseOut };

    /// <summary>Fades an element in that just became visible.</summary>
    public static void FadeIn(UIElement element)
    {
        if (!Allowed)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, Length) { EasingFunction = Easing });
    }

    /// <summary>A session light that breathes until it is replaced: strong
    /// (needs you) or soft (working). Still when motion is off.</summary>
    public static void Breathe(UIElement element, bool strong)
    {
        if (!Allowed)
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            element.Opacity = 1;
            return;
        }
        element.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(1, strong ? 0.3 : 0.6, new Duration(TimeSpan.FromMilliseconds(strong ? 700 : 1400)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            });
    }

    /// <summary>Dims and brightens an element twice: the pill's sign that it
    /// just sent a notification. Nothing when motion is off.</summary>
    public static void Pulse(UIElement element)
    {
        if (!Allowed)
            return;
        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, 0.35, new Duration(TimeSpan.FromMilliseconds(260)))
        {
            AutoReverse = true,
            RepeatBehavior = new RepeatBehavior(2),
            EasingFunction = Easing,
            FillBehavior = FillBehavior.Stop
        });
    }
}
