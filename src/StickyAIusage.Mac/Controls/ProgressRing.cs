using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace StickyAIusage.Mac.Controls;

/// <summary>A thin circular gauge (0-100) - the pill's spend ring. Same look
/// as the Windows app's ProgressRing.</summary>
public sealed class ProgressRing : Control
{
    public static readonly StyledProperty<double> ValueProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(Value));

    public static readonly StyledProperty<double> ThicknessProperty =
        AvaloniaProperty.Register<ProgressRing, double>(nameof(Thickness), 2.5);

    public static readonly StyledProperty<IBrush?> StrokeProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(Stroke), Brushes.White);

    public static readonly StyledProperty<IBrush?> TrackBrushProperty =
        AvaloniaProperty.Register<ProgressRing, IBrush?>(nameof(TrackBrush), Brushes.DimGray);

    static ProgressRing() =>
        AffectsRender<ProgressRing>(ValueProperty, ThicknessProperty, StrokeProperty, TrackBrushProperty);

    public double Value { get => GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Thickness { get => GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public IBrush? Stroke { get => GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public IBrush? TrackBrush { get => GetValue(TrackBrushProperty); set => SetValue(TrackBrushProperty, value); }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        var radius = (Math.Min(size.Width, size.Height) - Thickness) / 2;
        if (radius <= 0)
            return;

        var center = new Point(size.Width / 2, size.Height / 2);
        context.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 100);
        if (value <= 0)
            return;

        var pen = new Pen(Stroke, Thickness, lineCap: PenLineCap.Round);
        if (value >= 100)
        {
            context.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        var angle = value / 100 * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));

        var arc = new StreamGeometry();
        using (var ctx = arc.Open())
        {
            ctx.BeginFigure(start, isFilled: false);
            ctx.ArcTo(end, new Size(radius, radius), 0, isLargeArc: value > 50, SweepDirection.Clockwise);
            ctx.EndFigure(isClosed: false);
        }
        context.DrawGeometry(null, pen, arc);
    }
}
