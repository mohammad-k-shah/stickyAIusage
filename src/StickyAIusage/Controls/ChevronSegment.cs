using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace StickyAIusage.Controls;

/// <summary>
/// A powerline-style segment: a filled block with an arrow tip on the right
/// and, unless it's the first in its row, a matching notch on the left. Laid
/// side by side with a small negative margin, each tip sits in the next
/// segment's notch with a thin gap (the powerline look).
/// The tip depth is fixed in pixels regardless of the segment's width, which
/// a stretched Path couldn't do - hence drawing the shape here.
/// </summary>
public sealed class ChevronSegment : Decorator
{
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(ChevronSegment),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TipWidthProperty = DependencyProperty.Register(
        nameof(TipWidth), typeof(double), typeof(ChevronSegment),
        new FrameworkPropertyMetadata(7.0, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsFirstProperty = DependencyProperty.Register(
        nameof(IsFirst), typeof(bool), typeof(ChevronSegment),
        new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public double TipWidth
    {
        get => (double)GetValue(TipWidthProperty);
        set => SetValue(TipWidthProperty, value);
    }

    public bool IsFirst
    {
        get => (bool)GetValue(IsFirstProperty);
        set => SetValue(IsFirstProperty, value);
    }

    private double LeftInset => IsFirst ? 0 : TipWidth;

    protected override Size MeasureOverride(Size constraint)
    {
        var extra = LeftInset + TipWidth;
        if (Child is null)
            return new Size(extra, 0);

        Child.Measure(new Size(Math.Max(0, constraint.Width - extra), constraint.Height));
        return new Size(Child.DesiredSize.Width + extra, Child.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size arrangeSize)
    {
        Child?.Arrange(new Rect(LeftInset, 0,
            Math.Max(0, arrangeSize.Width - LeftInset - TipWidth), arrangeSize.Height));
        return arrangeSize;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight, t = TipWidth;
        if (w <= t || h <= 0)
            return;

        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            if (IsFirst)
            {
                const double r = 4;
                ctx.BeginFigure(new Point(r, 0), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(w - t, 0), false, false);
                ctx.LineTo(new Point(w, h / 2), false, false);
                ctx.LineTo(new Point(w - t, h), false, false);
                ctx.LineTo(new Point(r, h), false, false);
                ctx.ArcTo(new Point(0, h - r), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
                ctx.LineTo(new Point(0, r), false, false);
                ctx.ArcTo(new Point(r, 0), new Size(r, r), 0, false, SweepDirection.Clockwise, false, false);
            }
            else
            {
                ctx.BeginFigure(new Point(0, 0), isFilled: true, isClosed: true);
                ctx.LineTo(new Point(w - t, 0), false, false);
                ctx.LineTo(new Point(w, h / 2), false, false);
                ctx.LineTo(new Point(w - t, h), false, false);
                ctx.LineTo(new Point(0, h), false, false);
                ctx.LineTo(new Point(t, h / 2), false, false);
            }
        }
        geometry.Freeze();
        dc.DrawGeometry(Fill, null, geometry);
    }
}
