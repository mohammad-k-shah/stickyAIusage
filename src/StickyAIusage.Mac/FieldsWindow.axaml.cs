using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using StickyAIusage.Mac.Controls;
using StickyAIusage.Mac.Services;
using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Mac;

/// <summary>
/// Chooses and arranges the fields on a session card (the macOS counterpart of
/// the Windows FieldsWindow). The catalog on the left adds a field; the rows on
/// the right are the card's chip rows, and a chip can be dragged within a row,
/// into another row, or onto the strip at the bottom to start a new row.
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
    /// <summary>The dragged chip's field id, in a format only this app reads.</summary>
    private static readonly DataFormat<string> FieldFormat =
        DataFormat.CreateStringApplicationFormat("sticky-ai-usage.field");

    private readonly AppSettings _settings;
    private readonly Action _onSaved;
    private CardLayout _layout;

    private readonly DispatcherTimer _noticeTimer;

    private Point _pressedAt;
    private string? _pressedField;

    public FieldsWindow() : this(new AppSettings(), () => { }) { } // designer

    public FieldsWindow(AppSettings settings, Action onSaved)
    {
        _settings = settings;
        _onSaved = onSaved;
        _layout = settings.CardLayout();
        InitializeComponent();

        ContextBarToggle.IsChecked = _layout.ShowContextBar;
        GitDetailsToggle.IsChecked = _layout.ShowGitDetails;
        ContextBarToggle.IsCheckedChanged += (_, _) => BlocksChanged();
        GitDetailsToggle.IsCheckedChanged += (_, _) => BlocksChanged();
        SearchBox.TextChanged += (_, _) => BuildCatalog();

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
        RowFullNotice.IsVisible = true;
        _noticeTimer.Stop();
        _noticeTimer.Start();
    }

    private void HideNotice()
    {
        _noticeTimer.Stop();
        RowFullNotice.IsVisible = false;
    }

    private void Rebuild()
    {
        HideNotice();
        BuildCatalog();
        BuildRows();
        PreviewCard.Content = CardPreview.Card(_layout, DateTimeOffset.Now);
    }

    private void BlocksChanged()
    {
        _layout = _layout.WithBlocks(ContextBarToggle.IsChecked == true, GitDetailsToggle.IsChecked == true);
        PreviewCard.Content = CardPreview.Card(_layout, DateTimeOffset.Now);
    }

    // ---------------------------------------------------------- catalog

    private void BuildCatalog()
    {
        var search = (SearchBox.Text ?? "").Trim();
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
                Classes = { "hint" },
                Margin = new Thickness(8, 10, 8, 4)
            });
    }

    /// <summary>The band above each group of fields.</summary>
    private static Control CategoryHeading(FieldCategory category, int count)
    {
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        heading.Children.Add(new TextBlock { Text = SessionFields.CategoryName(category).ToUpperInvariant() });
        var number = new TextBlock { Text = count.ToString(), Classes = { "count" } };
        Grid.SetColumn(number, 1);
        heading.Children.Add(number);
        return new Border { Classes = { "catalogGroup" }, Child = heading };
    }

    private Control CatalogItem(SessionField field)
    {
        var used = _layout.Contains(field.Id);
        var name = new TextBlock { Text = field.Name, FontSize = 12.5 };
        var mark = new TextBlock
        {
            Text = used ? "✓" : "+",
            FontSize = 13,
            Margin = new Thickness(8, 0, 2, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeService.Resource<IBrush>(used ? "GoodBrush" : "TextTertiaryBrush")
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        row.Children.Add(name);
        Grid.SetColumn(mark, 1);
        row.Children.Add(mark);

        var button = new Button { Classes = { "catalog" }, Content = row, Margin = new Thickness(6, 0, 0, 0) };
        if (used)
            button.Classes.Add("used");
        ToolTip.SetTip(button, field.Description + (used ? "\nAlready on the card - click to remove it." : ""));
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
                Classes = { "hint" },
                Margin = new Thickness(4, 4, 0, 0)
            });
    }

    private Control FieldRow(int rowIndex)
    {
        var chips = new WrapPanel { Orientation = Orientation.Horizontal };
        var row = _layout.Rows[rowIndex];
        for (var position = 0; position < row.Count; position++)
            if (SessionFields.Find(row[position]) is { } field)
                chips.Children.Add(Chip(field, position));

        var border = new Border { Classes = { "fieldRow" }, Child = chips };
        MakeDropTarget(border, rowIndex, chips);
        return border;
    }

    private Control Chip(SessionField field, int position)
    {
        var text = new TextBlock
        {
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = Palette.SegmentText[position % Palette.SegmentText.Length],
            Text = field.Name
        };

        var remove = new TextBlock
        {
            Text = "✕",
            FontSize = 10,
            Margin = new Thickness(7, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0.7,
            Foreground = Palette.SegmentText[position % Palette.SegmentText.Length]
        };
        remove.PointerPressed += (_, e) =>
        {
            e.Handled = true;
            _layout = _layout.Remove(field.Id);
            Rebuild();
        };
        ToolTip.SetTip(remove, "Remove from the card");

        var content = new StackPanel { Orientation = Orientation.Horizontal };
        content.Children.Add(text);
        content.Children.Add(remove);

        var chip = new ChevronSegment
        {
            Height = 22,
            IsFirst = position == 0,
            Fill = Palette.SegmentFills[position % Palette.SegmentFills.Length],
            Child = content,
            Cursor = new Cursor(StandardCursorType.Hand),
            Tag = field.Id
        };
        ToolTip.SetTip(chip, field.Description + "\nDrag to move it.");

        chip.PointerPressed += (_, e) =>
        {
            _pressedAt = e.GetPosition(this);
            _pressedField = field.Id;
        };
        chip.PointerMoved += async (_, e) =>
        {
            if (_pressedField is not { } id || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                return;
            var moved = e.GetPosition(this) - _pressedAt;
            if (Math.Abs(moved.X) + Math.Abs(moved.Y) < 4)
                return;

            _pressedField = null;
            using var data = new DataTransfer();
            data.Add(DataTransferItem.Create(FieldFormat, id));
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Move);
        };
        chip.PointerReleased += (_, _) => _pressedField = null;

        return chip;
    }

    /// <summary>The strip under the last row: dropping here starts a new one.</summary>
    private Control NewRowTarget()
    {
        var full = _layout.Rows.Count >= CardLayout.MaxRows;
        var border = new Border
        {
            Classes = { "newRow" },
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = full ? $"That's the most rows a card can have ({CardLayout.MaxRows})" : "Drop a field here to start a new row",
                Classes = { "hint" },
                HorizontalAlignment = HorizontalAlignment.Center
            }
        };
        if (!full)
            MakeDropTarget(border, _layout.Rows.Count, chips: null);
        return border;
    }

    private void MakeDropTarget(Border border, int rowIndex, WrapPanel? chips)
    {
        DragDrop.SetAllowDrop(border, true);

        border.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            var id = e.DataTransfer.TryGetValue(FieldFormat);
            e.DragEffects = id is null ? DragDropEffects.None : DragDropEffects.Move;
            // A full row stays unlit: the rows that light up are the ones with
            // room for the chip.
            border.Classes.Set("over", id is not null && _layout.AcceptsDrop(id, rowIndex));
            e.Handled = true;
        });
        border.AddHandler(DragDrop.DragLeaveEvent, (_, _) => border.Classes.Set("over", false));
        border.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            border.Classes.Set("over", false);
            if (e.DataTransfer.TryGetValue(FieldFormat) is not { } id)
                return;

            DropField(id, rowIndex, DropPosition(chips, e));
            e.Handled = true;
        });
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
            var bounds = chips.Children[i].Bounds;
            var sameLine = point.Y >= bounds.Y && point.Y <= bounds.Bottom;
            if ((sameLine && point.X < bounds.Center.X) || point.Y < bounds.Y)
                return i;
        }
        return chips.Children.Count;
    }

    // ---------------------------------------------------------- buttons

    private void Reset_Click(object? sender, RoutedEventArgs e)
    {
        _layout = _layout.Reset();
        ContextBarToggle.IsChecked = _layout.ShowContextBar;
        GitDetailsToggle.IsChecked = _layout.ShowGitDetails;
        Rebuild();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e) => Close();

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.SetCardLayout(_layout);
        SettingsService.Save(_settings);
        _onSaved();
        Close();
    }
}
