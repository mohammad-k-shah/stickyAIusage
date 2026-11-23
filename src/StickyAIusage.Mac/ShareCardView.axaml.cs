using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using StickyAIusage.Services;
using StickyAIusage.Sharing;

namespace StickyAIusage.Mac;

/// <summary>Draws a <see cref="ShareCardModel"/> (see the XAML), the same layout as the
/// Windows card. Nothing here decides what the card says: every text comes from the model.</summary>
public partial class ShareCardView : Border
{
    private ShareCardPalette _palette = ShareCardPalette.Dark;

    public ShareCardView()
    {
        InitializeComponent();
    }

    /// <summary>The card drawn at its shape's pixel size, as PNG.</summary>
    public static byte[] Png(ShareCardModel model, bool dark)
    {
        using var bitmap = Render(model, dark);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    public static RenderTargetBitmap Render(ShareCardModel model, bool dark)
    {
        var view = new ShareCardView();
        view.Show(model, ShareCardPalette.For(dark));
        var (width, height) = model.Shape.Size();
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(new PixelSize(width, height), new Vector(96, 96));
        bitmap.Render(view);
        return bitmap;
    }

    public void Show(ShareCardModel model, ShareCardPalette palette)
    {
        _palette = palette;
        var (width, height) = model.Shape.Size();
        Width = width;
        Height = height;
        var portrait = model.Shape == ShareCardShape.Portrait;
        Background = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 0.7, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.Parse(palette.Background), 0), new GradientStop(Color.Parse(palette.BackgroundEnd), 1) }
        };
        AccentLine.Background = Brush(palette.Accent);
        Frame.Margin = new Thickness(portrait ? 72 : 56);

        Set(KickerText, model.Kicker, palette.Accent);
        Set(TitleText, model.Title, palette.TextPrimary);
        Set(SubtitleText, model.Subtitle, palette.TextSecondary);
        Set(DateText, model.DateText, palette.TextSecondary);
        Set(EstimateText, model.EstimateNote, palette.TextTertiary);
        Set(FooterNameText, model.FooterName, palette.TextPrimary);
        Set(FooterUrlText, model.FooterUrl, palette.TextSecondary);
        LogoImage.Source = Logo();
        // Portrait: the estimate note on its own line above the mark.
        Grid.SetColumnSpan(EstimateText, portrait ? 2 : 1);
        Grid.SetRow(FooterMark, portrait ? 1 : 0);
        EstimateText.Margin = portrait ? new Thickness(0, 0, 0, 16) : new Thickness(0, 0, 24, 0);

        Set(HeadlineText, model.Headline, palette.TextPrimary);
        HeadlineText.FontSize = portrait ? 120 : 104;
        Set(HeadlineNoteText, model.HeadlineNote, palette.TextSecondary);
        Set(PaceText, model.PaceLine, palette.Level(model.PaceLevel));
        PaceText.FontSize = portrait ? 30 : 24;

        GaugesPanel.Children.Clear();
        if (model.Variant == ShareCardVariant.Limits)
        {
            GaugesPanel.Orientation = Orientation.Horizontal;
            GaugesPanel.HorizontalAlignment = portrait ? HorizontalAlignment.Center : HorizontalAlignment.Left;
            foreach (var gauge in model.Gauges)
                GaugesPanel.Children.Add(Ring(gauge, portrait ? 300 : 210, model.Gauges.Count > 1));
        }
        else
        {
            GaugesPanel.Orientation = Orientation.Vertical;
            GaugesPanel.HorizontalAlignment = HorizontalAlignment.Stretch;
            foreach (var gauge in model.Gauges)
                GaugesPanel.Children.Add(Bar(gauge));
        }

        StatsPanel.Children.Clear();
        // More than three figures don't fit the wide card's column one above the
        // other: they go in rows, the label beside the value.
        var inline = !portrait && model.Stats.Count > 3;
        foreach (var stat in model.Stats)
            StatsPanel.Children.Add(inline ? InlineStat(stat) : Stat(stat, portrait));

        // Wide: the main part on the left, the figures in a column on the right.
        // Portrait: the main part on top, the figures below it.
        Body.RowDefinitions.Clear();
        Body.ColumnDefinitions.Clear();
        if (portrait)
        {
            Body.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            Body.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star));
            Grid.SetRow(SidePanel, 1);
            Grid.SetColumn(SidePanel, 0);
            MainPanel.Margin = new Thickness(0, 24, 0, 0);
            SidePanel.Margin = new Thickness(0, 56, 0, 0);
            SidePanel.VerticalAlignment = VerticalAlignment.Top;
            Body.VerticalAlignment = VerticalAlignment.Center;
        }
        else
        {
            Body.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            Body.ColumnDefinitions.Add(new ColumnDefinition(460, GridUnitType.Pixel));
            Grid.SetRow(SidePanel, 0);
            Grid.SetColumn(SidePanel, 1);
            MainPanel.Margin = new Thickness(0, 0, 48, 0);
            SidePanel.Margin = new Thickness(0);
            SidePanel.VerticalAlignment = VerticalAlignment.Center;
            Body.VerticalAlignment = VerticalAlignment.Stretch;
        }
    }

    /// <summary>A ring with its percentage inside, the window's name and reset under it.</summary>
    private Control Ring(ShareGauge gauge, double size, bool spaced)
    {
        var thickness = size / 11;
        var ring = new Grid { Width = size, Height = size, HorizontalAlignment = HorizontalAlignment.Left };
        ring.Children.Add(new Ellipse { Stroke = Brush(_palette.Track), StrokeThickness = thickness, Margin = new Thickness(thickness / 2) });
        if (Arc(size, thickness, gauge.Percent) is { } arc)
        {
            arc.Stroke = Brush(_palette.Level(gauge.Level));
            ring.Children.Add(arc);
        }
        ring.Children.Add(new TextBlock
        {
            Text = gauge.PercentText,
            FontSize = size * 0.26,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush(_palette.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });

        var cell = new StackPanel { Margin = new Thickness(0, 0, spaced ? size * 0.22 : 0, 0) };
        cell.Children.Add(ring);
        cell.Children.Add(new TextBlock
        {
            Text = gauge.Label, FontSize = size * 0.115, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, size * 0.08, 0, 0),
            Foreground = Brush(_palette.TextPrimary), HorizontalAlignment = HorizontalAlignment.Center
        });
        if (gauge.Detail is { } detail)
            cell.Children.Add(new TextBlock
            {
                Text = detail, FontSize = size * 0.09, Margin = new Thickness(0, 2, 0, 0), Foreground = Brush(_palette.TextSecondary),
                HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = size * 1.3
            });
        return cell;
    }

    /// <summary>The used part of a ring, clockwise from 12 o'clock; null at 0%.</summary>
    private static Shape? Arc(double size, double thickness, double percent)
    {
        if (percent <= 0)
            return null;
        if (percent >= 100)
            return new Ellipse { StrokeThickness = thickness, Margin = new Thickness(thickness / 2) };
        var radius = (size - thickness) / 2;
        var center = new Point(size / 2, size / 2);
        var angle = percent / 100 * 2 * Math.PI;
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(new Point(center.X, center.Y - radius), isFilled: false);
            context.ArcTo(new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle)),
                new Size(radius, radius), 0, isLargeArc: percent > 50, SweepDirection.Clockwise);
            context.EndFigure(isClosed: false);
        }
        return new Avalonia.Controls.Shapes.Path
        {
            Data = geometry,
            Width = size,
            Height = size,
            StrokeThickness = thickness,
            StrokeLineCap = PenLineCap.Round
        };
    }

    /// <summary>A spend bar: its label and percentage above a thick rounded bar.</summary>
    private Control Bar(ShareGauge gauge)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 36, 0, 0) };
        var labels = new Grid();
        labels.Children.Add(new TextBlock { Text = gauge.Label, FontSize = 22, Foreground = Brush(_palette.TextSecondary) });
        labels.Children.Add(new TextBlock
        {
            Text = gauge.PercentText, FontSize = 22, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Right,
            Foreground = Brush(_palette.Level(gauge.Level))
        });
        panel.Children.Add(labels);
        var track = new Grid { Height = 20, Margin = new Thickness(0, 10, 0, 0) };
        track.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(gauge.Percent, 0.001), GridUnitType.Star));
        track.ColumnDefinitions.Add(new ColumnDefinition(Math.Max(100 - gauge.Percent, 0.001), GridUnitType.Star));
        var background = new Border { CornerRadius = new CornerRadius(10), Background = Brush(_palette.Track) };
        Grid.SetColumnSpan(background, 2);
        track.Children.Add(background);
        if (gauge.Percent > 0)
            track.Children.Add(new Border { CornerRadius = new CornerRadius(10), Background = Brush(_palette.Level(gauge.Level)) });
        panel.Children.Add(track);
        return panel;
    }

    /// <summary>"Model ........ Sonnet 5.5" on one line.</summary>
    private Control InlineStat(ShareStat stat)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 14), ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        row.Children.Add(new TextBlock { Text = stat.Label, FontSize = 20, VerticalAlignment = VerticalAlignment.Center, Foreground = Brush(_palette.TextSecondary) });
        var value = new TextBlock
        {
            Text = stat.Value, FontSize = 26, FontWeight = FontWeight.SemiBold, Foreground = Brush(_palette.TextPrimary),
            HorizontalAlignment = HorizontalAlignment.Right, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(16, 0, 0, 0)
        };
        Grid.SetColumn(value, 1);
        row.Children.Add(value);
        return row;
    }

    private Control Stat(ShareStat stat, bool portrait)
    {
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, portrait ? 26 : 18) };
        panel.Children.Add(new TextBlock { Text = stat.Label, FontSize = portrait ? 22 : 18, Foreground = Brush(_palette.TextSecondary) });
        panel.Children.Add(new TextBlock
        {
            Text = stat.Value, FontSize = portrait ? 38 : 30, FontWeight = FontWeight.SemiBold, Foreground = Brush(_palette.TextPrimary),
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        return panel;
    }

    private static void Set(TextBlock block, string? text, string color)
    {
        block.Text = text ?? "";
        block.IsVisible = !string.IsNullOrEmpty(text);
        block.Foreground = Brush(color);
    }

    /// <summary>The app icon from the bundle's assets; null if it can't be read.</summary>
    private static Bitmap? Logo()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri(AppIcon));
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            DiagnosticLog.WriteError("ShareCardView.Logo", ex);
            return null;
        }
    }

    internal const string AppIcon = "avares://StickyAIusage/Assets/app.png";

    private static IBrush Brush(string hex) => new Avalonia.Media.Immutable.ImmutableSolidColorBrush(Color.Parse(hex));
}
