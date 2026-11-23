using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Styling;
using StickyAIusage.Mac.Native;

namespace StickyAIusage.Mac.Controls;

/// <summary>The small animations of the pill (the ring easing to a new value,
/// the pace chip fading in), and when to leave them out: when macOS asks to
/// reduce motion (System Settings → Accessibility → Display), and in the
/// screenshot tests, which render at once.</summary>
internal static class Motion
{
    public static readonly TimeSpan Length = TimeSpan.FromMilliseconds(300);

    /// <summary>Off in the UI tests, so a screenshot shows the final state.</summary>
    public static bool Enabled { get; set; } = true;

    private static bool? _reduceMotion;

    /// <summary>Read once: the setting rarely changes while the app runs.</summary>
    public static bool Allowed => Enabled && !(_reduceMotion ??= MacNative.ReduceMotion());

    public static Transitions? For(AvaloniaProperty<double> property) => Allowed
        ? [new DoubleTransition { Property = property, Duration = Length, Easing = new CubicEaseOut() }]
        : null;

    /// <summary>Fades in an element that just became visible.</summary>
    public static void FadeIn(Control element)
    {
        element.Transitions = null;
        if (!Allowed)
        {
            element.Opacity = 1;
            return;
        }
        element.Opacity = 0;
        element.Transitions = For(Visual.OpacityProperty);
        element.Opacity = 1;
    }

    /// <summary>A session light that breathes until it leaves the visual tree:
    /// strong (needs you) or soft (working). Still when motion is off.</summary>
    public static Task Breathe(Control element, bool strong) =>
        Allowed ? BreatheNow(element, strong) : Task.CompletedTask;

    /// <summary>The breathing itself, whatever the motion setting (tests call it directly).</summary>
    internal static Task BreatheNow(Control element, bool strong)
    {
        // One breath per run, repeated: Avalonia refuses RunAsync for an endless
        // (IterationCount.Infinite) animation, and that refusal surfaced as an
        // unobserved task exception on every refresh.
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(strong ? 1400 : 2800),
            IterationCount = new IterationCount(1),
            Easing = new SineEaseInOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(Visual.OpacityProperty, strong ? 0.3 : 0.6) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } }
            }
        };
        // Stops when the dot is removed (the next refresh draws new ones).
        var stop = new CancellationTokenSource();
        element.DetachedFromVisualTree += (_, _) => stop.Cancel();
        return BreatheUntilStopped(animation, element, stop.Token);
    }

    private static async Task BreatheUntilStopped(Animation animation, Control element, CancellationToken stop)
    {
        try
        {
            while (!stop.IsCancellationRequested)
                await animation.RunAsync(element, stop);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            // A dot that can't breathe stays still; the log says why, once per dot.
            StickyAIusage.Services.DiagnosticLog.WriteError("Motion.Breathe", ex);
        }
    }

    /// <summary>Dims and brightens an element twice: the pill's sign that it
    /// just sent a notification. Nothing when motion is off; the opacity the
    /// element had comes back afterwards (the animation holds no value).</summary>
    public static void Pulse(Control element)
    {
        if (!Allowed)
            return;
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(520),
            IterationCount = new IterationCount(2),
            Easing = new CubicEaseOut(),
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 1.0) } },
                new KeyFrame { Cue = new Cue(0.5), Setters = { new Setter(Visual.OpacityProperty, 0.35) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1.0) } }
            }
        };
        _ = Observe(animation.RunAsync(element));
    }

    private static async Task Observe(Task animation)
    {
        try
        {
            await animation;
        }
        catch (Exception ex)
        {
            StickyAIusage.Services.DiagnosticLog.WriteError("Motion.Pulse", ex);
        }
    }
}
