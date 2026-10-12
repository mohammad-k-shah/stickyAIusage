using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac.Controls;

/// <summary>Colors shared with the Windows theme: segment fills cycle rose,
/// sand, steel, sage, and usage levels map to
/// green/yellow/orange/red. The segment chips are the app's signature and
/// stay the same in both themes; the level colors are darkened for the light
/// one, where the dark theme's pastels wash out. Set by ThemeService.</summary>
public static class Palette
{
    public static bool IsDark { get; set; } = true;

    /// <summary>Eight fills - one per slot in a full row, so no row repeats a
    /// color: rose, sand, steel, sage, sky, violet, blush, lime.</summary>
    public static readonly IBrush[] SegmentFills =
    [
        Brush("#D9737E"), Brush("#E3C58F"), Brush("#5A7DB8"), Brush("#A3C293"),
        Brush("#7FC7E0"), Brush("#A695DE"), Brush("#EAA8C6"), Brush("#C9E27A")
    ];

    public static readonly IBrush[] SegmentText =
    [
        Brush("#221417"), Brush("#221A0E"), Brush("#FFFFFF"), Brush("#15200F"),
        Brush("#0E1F26"), Brush("#151033"), Brush("#2A1420"), Brush("#1B2410")
    ];

    public static IBrush Green => IsDark ? DarkGreen : LightGreen;
    public static IBrush Yellow => IsDark ? DarkYellow : LightYellow;
    public static IBrush Orange => IsDark ? DarkOrange : LightOrange;
    public static IBrush Red => IsDark ? DarkRed : LightRed;

    private static readonly IBrush DarkGreen = Brush("#4CC38A");
    private static readonly IBrush DarkYellow = Brush("#E8C15A");
    private static readonly IBrush DarkOrange = Brush("#F0954A");
    private static readonly IBrush DarkRed = Brush("#EE6B6B");
    private static readonly IBrush LightGreen = Brush("#15875A");
    private static readonly IBrush LightYellow = Brush("#9A6F0F");
    private static readonly IBrush LightOrange = Brush("#B85C18");
    private static readonly IBrush LightRed = Brush("#C0392B");

    /// <summary>A live session's card border: a green tint; otherwise the
    /// theme's subtle border.</summary>
    public static IBrush CardBorder(bool live) => live
        ? (IsDark ? Brush("#594CC38A") : Brush("#5915875A"))
        : (IsDark ? Brush("#FF33333B") : Brush("#FFE2E2E9"));

    public static IBrush ForLevel(UsageLevel level) => level switch
    {
        UsageLevel.Red => Red,
        UsageLevel.Orange => Orange,
        UsageLevel.Yellow => Yellow,
        _ => Green
    };

    public static IBrush ForContext(ContextLevel level) => level switch
    {
        ContextLevel.High => Red,
        ContextLevel.Warn => Yellow,
        _ => Green
    };

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

public static class Converters
{
    public static readonly IValueConverter SegmentFill =
        new FuncValueConverter<int, IBrush>(i => Palette.SegmentFills[i % Palette.SegmentFills.Length]);

    public static readonly IValueConverter SegmentText =
        new FuncValueConverter<int, IBrush>(i => Palette.SegmentText[i % Palette.SegmentText.Length]);

    public static readonly IValueConverter Level =
        new FuncValueConverter<UsageLevel, IBrush>(Palette.ForLevel);

    public static readonly IValueConverter Context =
        new FuncValueConverter<ContextLevel, IBrush>(Palette.ForContext);

    public static readonly IValueConverter LiveBorder =
        new FuncValueConverter<bool, IBrush>(Palette.CardBorder);

    public static readonly IValueConverter NotEmpty =
        new FuncValueConverter<string?, bool>(s => !string.IsNullOrEmpty(s));

    public static readonly IValueConverter HasValue =
        new FuncValueConverter<double?, bool>(v => v is not null);

    public static readonly IValueConverter OrZero =
        new FuncValueConverter<double?, double>(v => v ?? 0);
}
