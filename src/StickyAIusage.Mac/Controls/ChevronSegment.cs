using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace StickyAIusage.Mac.Controls;

/// <summary>
/// One powerline-style segment: a filled shape with a pointed right edge and,
/// unless it's the first in its row, a notch on the left that the previous
/// segment's point sits in (negative left margin). Same drawing as the
/// Windows app's ChevronSegment: the powerline look, in the app's own colors.
/// </summary>
public sealed class ChevronSegment : Decorator
{
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<ChevronSegment, IBrush?>(nameof(Fill), Brushes.Gray);

    public static readonly StyledProperty<double> TipWidthProperty =
        AvaloniaProperty.Register<ChevronSegment, double>(nameof(TipWidth), 7.0);

    public static readonly StyledProperty<bool> IsFirstProperty =
        AvaloniaProperty.Register<ChevronSegment, bool>(nameof(IsFirst));

    static ChevronSegment()
    {
        AffectsRender<ChevronSegment>(FillProperty, TipWidthProperty, IsFirstProperty);
        AffectsMeasure<ChevronSegment>(TipWidthProperty, IsFirstProperty);
    }

    public ChevronSegment()
    {
        Height = 20;
        UpdateMargins();
    }

    public IBrush? Fill { get => GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public double TipWidth { get => GetValue(TipWidthProperty); set => SetValue(TipWidthProperty, value); }
    public bool IsFirst { get => GetValue(IsFirstProperty); set => SetValue(IsFirstProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsFirstProperty)
            UpdateMargins();
    }

    /// <summary>Non-first segments tuck under the previous one's point.</summary>
    private void UpdateMargins()
    {
        Margin = IsFirst ? new Thickness(0, 0, 0, 5) : new Thickness(-5, 0, 0, 5);
        Padding = IsFirst ? new Thickness(9, 0, 3, 0) : new Thickness(5, 0, 3, 0);
    }

    private double LeftInset => IsFirst ? 0 : TipWidth;

    protected override Size MeasureOverride(Size availableSize)
    {
        var extra = LeftInset + TipWidth + Padding.Left + Padding.Right;
        if (Child is null)
            return new Size(extra, 0);
        Child.Measure(new Size(Math.Max(0, availableSize.Width - extra), availableSize.Height));
        return new Size(Child.DesiredSize.Width + extra, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var left = LeftInset + Padding.Left;
        Child?.Arrange(new Rect(left, 0, Math.Max(0, finalSize.Width - left - TipWidth - Padding.Right), finalSize.Height));
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        double w = Bounds.Width, h = Bounds.Height, t = TipWidth;
        if (w <= t || h <= 0)
            return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (IsFirst)
            {
                const double r = 4;
                ctx.BeginFigure(new Point(r, 0), isFilled: true);
                ctx.LineTo(new Point(w - t, 0));
                ctx.LineTo(new Point(w, h / 2));
                ctx.LineTo(new Point(w - t, h));
                ctx.LineTo(new Point(r, h));
                ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise);
                ctx.LineTo(new Point(0, r));
                ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise);
            }
            else
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: true);
                ctx.LineTo(new Point(w - t, 0));
                ctx.LineTo(new Point(w, h / 2));
                ctx.LineTo(new Point(w - t, h));
                ctx.LineTo(new Point(0, h));
                ctx.LineTo(new Point(t, h / 2));
            }
            ctx.EndFigure(isClosed: true);
        }
        context.DrawGeometry(Fill, null, geometry);
    }
}
