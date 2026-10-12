using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using StickyAIusage.Controls;
using StickyAIusage.Native;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage;

/// <summary>
/// Chooses and arranges the fields on a session card (the Windows counterpart
/// of the macOS FieldsWindow). The catalog on the left adds a field; the rows
/// on the right are the card's chip rows, and a chip can be dragged within a
/// row, into another row, or onto the strip underneath to start a new row.
/// Picking from the catalog keeps a group together - the field joins the row
/// its group already lives on, or starts a new one - while dragging puts a
/// chip wherever you like. The one rule a drag keeps is eight chips to a row:
/// a full row doesn't light up, and a chip dropped on one stays where it was.
///
/// Colors are never chosen by hand: a chip takes the color of its position in
/// its row (rose, sand, steel, sage, sky, violet, blush, lime - one per slot
/// in a full row), so a drop recolors everything it passed. The preview underneath is the real card template with sample
/// data, so what you arrange is exactly what the panel will show.
/// </summary>
public partial class FieldsWindow : Window
{
    private const string DragFormat = "StickyAIusage.Field";
    private const double DragThreshold = 4;

    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private CardLayout _layout;

    private readonly DispatcherTimer _noticeTimer;

    private Point _pressedAt;
    private string? _pressedField;

    public FieldsWindow(AppSettings settings, Action onSaved)
    {
        _settings = settings;
        _onSaved = onSaved;
        _layout = settings.CardLayout();
        InitializeComponent();

        ContextBarToggle.IsChecked = _layout.ShowContextBar;
        GitDetailsToggle.IsChecked = _layout.ShowGitDetails;

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _noticeTimer.Tick += (_, _) => HideNotice();

        ThemeService.Changed += Rebuild;
        Closed += (_, _) => ThemeService.Changed -= Rebuild;

        Rebuild();
    }

    /// <summary>The layout being edited, for the screenshot tests.</summary>
    internal CardLayout Layout => _layout;

    /// <summary>Test hook: clicking a field in the catalog.</summary>
    internal void AddField(string fieldId)
    {
        _layout = _layout.AddGrouped(fieldId);
        Rebuild();
    }

    /// <summary>Test hook: what dropping a chip on a row does.</summary>
    internal void MoveField(string fieldId, int rowIndex, int position) => DropField(fieldId, rowIndex, position);

    /// <summary>A dropped chip: into the row when it has room, and otherwise
    /// left where it was, with a note beside "On the card" saying why.</summary>
    private void DropField(string fieldId, int rowIndex, int position)
    {
        if (!_layout.AcceptsDrop(fieldId, rowIndex))
        {
            ShowRowFullNotice();
            return;
        }

        _layout = _layout.Move(fieldId, rowIndex, position);
        Rebuild();
    }

    /// <summary>The refused-drop notice, which leaves again by itself.</summary>
    internal void ShowRowFullNotice()
    {
        RowFullNoticeText.Text = CardLayout.RowFullMessage;
        RowFullNotice.Visibility = Visibility.Visible;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        RowFullNotice.Visibility = Visibility.Collapsed;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowTheme.ApplyTitleBar(this, (Color)FindResource("WindowBackgroundColor"), ThemeService.IsDark);
    }

    private void Rebuild()
    {
        HideNotice();
        BuildCatalog();
        BuildRows();
        PreviewCard.Content = CardPreview.Card(_layout, DateTimeOffset.Now);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => BuildCatalog();

    private void Blocks_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded)
            return;
        _layout = _layout.WithBlocks(ContextBarToggle.IsChecked == true, GitDetailsToggle.IsChecked == true);
        PreviewCard.Content = CardPreview.Card(_layout, DateTimeOffset.Now);
    }

    // ---------------------------------------------------------- catalog

    private void BuildCatalog()
    {
        var search = SearchBox.Text.Trim();
        var matches = SessionFields.All
            .Where(f => search.Length == 0
                        || f.Name.Contains(search, StringComparison.OrdinalIgnoreCase)
                        || f.Description.Contains(search, StringComparison.OrdinalIgnoreCase))
            .ToList();

        CatalogTitle.Text = search.Length == 0
            ? $"Available fields ({SessionFields.All.Count})"
            : $"{matches.Count} of {SessionFields.All.Count} fields";

        CatalogPanel.Children.Clear();
        foreach (var category in Enum.GetValues<FieldCategory>())
        {
            var inCategory = matches.Where(f => f.Category == category).ToList();
            if (inCategory.Count == 0)
                continue;

            CatalogPanel.Children.Add(CategoryHeading(category, inCategory.Count));
            foreach (var field in inCategory)
                CatalogPanel.Children.Add(CatalogItem(field));
        }

        if (matches.Count == 0)
            CatalogPanel.Children.Add(new TextBlock
            {
                Text = "No field matches that.",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(8, 10, 8, 4)
            });
    }

    /// <summary>The band above each group of fields: tinted and in the accent
    /// color, so it can't be mistaken for one of the fields under it.</summary>
    private UIElement CategoryHeading(FieldCategory category, int count)
    {
        var heading = new Grid();
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.Children.Add(new TextBlock
        {
            Text = SessionFields.CategoryName(category).ToUpperInvariant(),
            FontSize = 10,
            FontWeight = FontWeights.Bold,
            Foreground = (Brush)FindResource("AccentBrush")
        });

        var number = new TextBlock
        {
            Text = count.ToString(),
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextTertiaryBrush")
        };
        Grid.SetColumn(number, 1);
        heading.Children.Add(number);

        return new Border
        {
            Background = (Brush)FindResource("CardHoverBrush"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(2, 12, 2, 5),
            Child = heading
        };
    }

    private UIElement CatalogItem(SessionField field)
    {
        var used = _layout.Contains(field.Id);

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new TextBlock { Text = field.Name, FontSize = 12.5 });

        var mark = new TextBlock
        {
            Text = used ? "✓" : "+",
            FontSize = 13,
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource(used ? "GoodBrush" : "TextTertiaryBrush")
        };
        Grid.SetColumn(mark, 1);
        row.Children.Add(mark);

        var button = new Button
        {
            Style = (Style)FindResource("CatalogItemButton"),
            Content = row,
            Opacity = used ? 0.45 : 1,
            Margin = new Thickness(6, 0, 0, 0),
            ToolTip = field.Description + (used ? "\nAlready on the card - click to remove it." : "")
        };
        button.Click += (_, _) =>
        {
            _layout = _layout.Contains(field.Id) ? _layout.Remove(field.Id) : _layout.AddGrouped(field.Id);
            Rebuild();
        };
        return button;
    }

    // ------------------------------------------------------------ rows

    private void BuildRows()
    {
        RowsPanel.Children.Clear();

        for (var i = 0; i < _layout.Rows.Count; i++)
            RowsPanel.Children.Add(FieldRow(i));

        NewRowHost.Content = NewRowTarget();

        if (_layout.Rows.Count == 0)
            RowsPanel.Children.Add(new TextBlock
            {
                Text = "No fields on the card yet - add some from the list on the left.",
                Style = (Style)FindResource("Hint"),
                Margin = new Thickness(4, 4, 0, 0)
            });
    }

    private UIElement FieldRow(int rowIndex)
    {
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var row = _layout.Rows[rowIndex];
        for (var position = 0; position < row.Count; position++)
            if (SessionFields.Find(row[position]) is { } field)
                chips.Children.Add(Chip(field, position));

        var border = new Border
        {
            Background = (Brush)FindResource("CardBrush"),
            BorderBrush = (Brush)FindResource("SubtleBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 8, 9, 3),
            Margin = new Thickness(0, 0, 0, 8),
            MinHeight = 44,
            Child = chips
        };
        MakeDropTarget(border, rowIndex, chips);
        return border;
    }

    private UIElement Chip(SessionField field, int position)
    {
        var index = position % Segment.Colors;
        var foreground = (Brush)FindResource($"Segment{index}TextBrush");

        var label = new TextBlock
        {
            Text = field.Name,
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = foreground
        };

        var remove = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            Foreground = foreground,
            Cursor = Cursors.Hand,
            ToolTip = "Remove from the card"
        };
        remove.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            _layout = _layout.Remove(field.Id);
            Rebuild();
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = position == 0 ? new Thickness(9, 0, 3, 0) : new Thickness(12, 0, 3, 0)
        };
        content.Children.Add(label);
        content.Children.Add(remove);

        var chip = new ChevronSegment
        {
            Height = 22,
            Margin = position == 0 ? new Thickness(0, 0, 0, 5) : new Thickness(-5, 0, 0, 5),
            IsFirst = position == 0,
            Fill = (Brush)FindResource($"Segment{index}Brush"),
            Child = content,
            Cursor = Cursors.Hand,
            ToolTip = field.Description + "\nDrag to move it."
        };

        chip.MouseLeftButtonDown += (_, e) =>
        {
            _pressedAt = e.GetPosition(this);
            _pressedField = field.Id;
        };
        chip.MouseMove += (_, e) =>
        {
            if (_pressedField is not { } id || e.LeftButton != MouseButtonState.Pressed)
                return;
            var moved = e.GetPosition(this) - _pressedAt;
            if (Math.Abs(moved.X) + Math.Abs(moved.Y) < DragThreshold)
                return;

            _pressedField = null;
            DragDrop.DoDragDrop(chip, new DataObject(DragFormat, id), DragDropEffects.Move);
        };
        chip.MouseLeftButtonUp += (_, _) => _pressedField = null;

        return chip;
    }

    /// <summary>The strip under the last row: dropping here starts a new one.</summary>
    private UIElement NewRowTarget()
    {
        var full = _layout.Rows.Count >= CardLayout.MaxRows;
        var border = new Border
        {
            BorderBrush = (Brush)FindResource("StrongBorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(9, 10, 9, 10),
            Margin = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text = full
                    ? $"That's the most rows a card can have ({CardLayout.MaxRows})"
                    : "Drop a field here to start a new row",
                Style = (Style)FindResource("Hint"),
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        if (!full)
            MakeDropTarget(border, _layout.Rows.Count, chips: null);
        return border;
    }

    private void MakeDropTarget(Border border, int rowIndex, WrapPanel? chips)
    {
        border.AllowDrop = true;

        void Highlight(bool on) => border.BorderBrush =
            (Brush)FindResource(on ? "AccentBrush" : chips is null ? "StrongBorderBrush" : "SubtleBorderBrush");

        border.DragOver += (_, e) =>
        {
            var id = e.Data.GetData(DragFormat) as string;
            e.Effects = id is null ? DragDropEffects.None : DragDropEffects.Move;
            // A full row stays unlit: the rows that light up are the ones with
            // room for the chip.
            Highlight(id is not null && _layout.AcceptsDrop(id, rowIndex));
            e.Handled = true;
        };
        border.DragLeave += (_, _) => Highlight(false);
        border.Drop += (_, e) =>
        {
            Highlight(false);
            if (e.Data.GetData(DragFormat) is not string id)
                return;

            DropField(id, rowIndex, DropPosition(chips, e));
            e.Handled = true;
        };
    }

    /// <summary>Where in the row the chip was dropped: before the first chip
    /// whose middle is right of the pointer.</summary>
    private static int DropPosition(WrapPanel? chips, DragEventArgs e)
    {
        if (chips is null)
            return 0;

        var point = e.GetPosition(chips);
        for (var i = 0; i < chips.Children.Count; i++)
        {
            if (chips.Children[i] is not FrameworkElement child)
                continue;
            var origin = child.TranslatePoint(new Point(0, 0), chips);
            var sameLine = point.Y >= origin.Y && point.Y <= origin.Y + child.ActualHeight;
            if ((sameLine && point.X < origin.X + child.ActualWidth / 2) || point.Y < origin.Y)
                return i;
        }
        return chips.Children.Count;
    }

    // ---------------------------------------------------------- buttons

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        _layout = _layout.Reset();
        ContextBarToggle.IsChecked = _layout.ShowContextBar;
        GitDetailsToggle.IsChecked = _layout.ShowGitDetails;
        Rebuild();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.SetCardLayout(_layout);
        SettingsService.Save(_settings);
        _onSaved();
        Close();
    }
}
