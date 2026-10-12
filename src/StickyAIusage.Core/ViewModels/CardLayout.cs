namespace StickyAIusage.ViewModels;

/// <summary>
/// Which fields a session card shows and how they're grouped: one list of
/// field ids per chip row. Colors aren't stored - a chip's color comes from
/// its position in its row (rose, sand, steel, sage, repeating), so moving a
/// chip recolors it, and a row always reads in the same order.
///
/// Persisted as one string in settings ("model,effort|in,out,cached,total"),
/// which stays readable if anyone opens settings.json, and ignores ids it
/// doesn't recognize instead of failing to load.
/// </summary>
public sealed class CardLayout
{
    /// <summary>The grouping the app ships with: what the session card
    /// showed before any of this was configurable.</summary>
    public static readonly IReadOnlyList<IReadOnlyList<string>> DefaultRows =
    [
        ["model", "effort"],
        ["in", "out", "cached", "total"],
        ["cache-hit", "cache-read", "cache-write"]
    ];

    /// <summary>The grouping that shipped before Compactions came off the
    /// first row. Settings holding exactly this never had a choice made in
    /// them - the app saved its own default - so they read as today's
    /// default rather than pinning a card to the old one.</summary>
    private const string SupersededDefault =
        "model,effort,compactions|in,out,cached,total|cache-hit,cache-read,cache-write";

    public const int MaxRows = 8;
    public const int MaxFieldsPerRow = 8;

    /// <summary>Why a drop on a full row was refused, in the words the
    /// customizer shows next to "On the card".</summary>
    public static string RowFullMessage => $"A row can have a max. of {MaxFieldsPerRow} fields";

    public IReadOnlyList<IReadOnlyList<string>> Rows { get; }

    /// <summary>The context bar under the chips, and the git line under it:
    /// laid out rather than chipped, so they keep their bar and their
    /// +/− coloring. Turn them off to get chips only.</summary>
    public bool ShowContextBar { get; }
    public bool ShowGitDetails { get; }

    private CardLayout(IReadOnlyList<IReadOnlyList<string>> rows, bool showContextBar, bool showGitDetails)
    {
        Rows = rows;
        ShowContextBar = showContextBar;
        ShowGitDetails = showGitDetails;
    }

    public static CardLayout Default { get; } = new(DefaultRows, true, true);

    public static CardLayout Create(IEnumerable<IEnumerable<string>> rows, bool showContextBar = true, bool showGitDetails = true)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleaned = new List<IReadOnlyList<string>>();

        foreach (var row in rows)
        {
            // A field appears once on a card: two chips of the same value is
            // never what someone meant, and it makes drag-and-drop ambiguous.
            var fields = row
                .Where(id => SessionFields.Find(id) is not null && seen.Add(id))
                .Take(MaxFieldsPerRow)
                .ToList();
            if (fields.Count > 0)
                cleaned.Add(fields);
            if (cleaned.Count == MaxRows)
                break;
        }

        return new CardLayout(cleaned, showContextBar, showGitDetails);
    }

    /// <summary>Reads the stored form; anything unparseable falls back to the
    /// default layout, and unknown field ids are dropped.</summary>
    public static CardLayout Parse(string? text, bool showContextBar = true, bool showGitDetails = true)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Trim().Equals(SupersededDefault, StringComparison.OrdinalIgnoreCase))
            return new CardLayout(DefaultRows, showContextBar, showGitDetails);

        var layout = Create(
            text.Split('|', StringSplitOptions.RemoveEmptyEntries)
                .Select(row => row.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            showContextBar, showGitDetails);

        // Every id was unknown (a settings file from a much later version, or
        // hand-edited): better the default card than an empty one.
        return layout.Rows.Count > 0 ? layout : new CardLayout(DefaultRows, showContextBar, showGitDetails);
    }

    public string ToSetting() => string.Join("|", Rows.Select(row => string.Join(",", row)));

    public CardLayout WithBlocks(bool showContextBar, bool showGitDetails) =>
        new(Rows, showContextBar, showGitDetails);

    /// <summary>Whether the card currently shows this field.</summary>
    public bool Contains(string fieldId) =>
        Rows.Any(row => row.Contains(fieldId, StringComparer.OrdinalIgnoreCase));

    /// <summary>Adds a field to the end of a row (a new last row when
    /// <paramref name="rowIndex"/> is past the end, or the row is full).</summary>
    public CardLayout Add(string fieldId, int? rowIndex = null)
    {
        if (SessionFields.Find(fieldId) is null || Contains(fieldId))
            return this;

        var rows = Mutable();
        var index = rowIndex ?? rows.Count - 1;
        if (index < 0 || index >= rows.Count || rows[index].Count >= MaxFieldsPerRow)
        {
            if (rows.Count >= MaxRows)
                return this;
            rows.Add([fieldId]);
        }
        else
        {
            rows[index].Add(fieldId);
        }
        return Create(rows, ShowContextBar, ShowGitDetails);
    }

    /// <summary>
    /// Adds a field the way picking it from the catalog should: beside its own
    /// group. The first row that already holds fields of the same group and
    /// has room takes it; otherwise the field starts a new row, and only when
    /// the card is out of rows does it fall back to the first row with room.
    /// Dragging stays unrestricted - this is what the catalog does, not a rule
    /// about what a row may hold.
    /// </summary>
    public CardLayout AddGrouped(string fieldId)
    {
        if (SessionFields.Find(fieldId) is not { } field || Contains(fieldId))
            return this;

        var rows = Mutable();

        // A row of this group alone first, then one that merely holds a field
        // of it (someone dragged a chip in): both are where the group lives.
        var index = Fitting(rows, field.Category, wholeRow: true);
        if (index < 0)
            index = Fitting(rows, field.Category, wholeRow: false);

        if (index < 0 && rows.Count < MaxRows)
        {
            rows.Add([fieldId]);
            return Create(rows, ShowContextBar, ShowGitDetails);
        }
        if (index < 0)
            index = rows.FindIndex(row => row.Count < MaxFieldsPerRow);
        if (index < 0)
            return this;

        rows[index].Add(fieldId);
        return Create(rows, ShowContextBar, ShowGitDetails);
    }

    /// <summary>The first row with room whose fields are all in this group
    /// (or, when <paramref name="wholeRow"/> is false, any of them).</summary>
    private static int Fitting(List<List<string>> rows, FieldCategory category, bool wholeRow)
    {
        bool Grouped(string id) => SessionFields.Find(id)?.Category == category;
        return rows.FindIndex(row => row.Count < MaxFieldsPerRow
                                     && (wholeRow ? row.All(Grouped) : row.Any(Grouped)));
    }

    /// <summary>Whether a dragged field can land in a row: a full row only
    /// takes a chip it already holds, which is a reorder rather than a ninth
    /// field, and the strip below the last row only while a row is to spare.
    /// The customizer asks this while dragging, to light up the rows that can
    /// take the chip and to leave the field where it was otherwise.</summary>
    public bool AcceptsDrop(string fieldId, int rowIndex)
    {
        if (rowIndex < 0 || SessionFields.Find(fieldId) is null)
            return false;
        if (rowIndex >= Rows.Count)
            return Rows.Count < MaxRows;

        var row = Rows[rowIndex];
        return row.Count < MaxFieldsPerRow || row.Contains(fieldId, StringComparer.OrdinalIgnoreCase);
    }

    public CardLayout Remove(string fieldId)
    {
        var rows = Mutable();
        foreach (var row in rows)
            row.RemoveAll(id => string.Equals(id, fieldId, StringComparison.OrdinalIgnoreCase));
        return Create(rows, ShowContextBar, ShowGitDetails);
    }

    /// <summary>
    /// Drag-and-drop: puts a field at a position in a row, taking it out of
    /// wherever it was. A <paramref name="rowIndex"/> of Rows.Count means
    /// "a new row at the end" - the drop zone below the last one.
    /// </summary>
    public CardLayout Move(string fieldId, int rowIndex, int position)
    {
        // A full row would truncate the row on the way out, losing the chip
        // the drag came from: refuse the drop and leave the card alone.
        if (!AcceptsDrop(fieldId, rowIndex))
            return this;

        var rows = Mutable();
        var from = rows.FindIndex(row => row.Contains(fieldId, StringComparer.OrdinalIgnoreCase));
        if (from >= 0)
        {
            var oldPosition = rows[from].FindIndex(id => string.Equals(id, fieldId, StringComparison.OrdinalIgnoreCase));
            rows[from].RemoveAt(oldPosition);
            // Removing before the target shifts everything after it left.
            if (from == rowIndex && oldPosition < position)
                position--;
        }

        if (rowIndex >= rows.Count)
        {
            if (rows.Count >= MaxRows)
                return this;
            rows.Add([fieldId]);
        }
        else
        {
            var row = rows[Math.Max(0, rowIndex)];
            row.Insert(Math.Clamp(position, 0, row.Count), fieldId);
        }

        return Create(rows, ShowContextBar, ShowGitDetails);
    }

    /// <summary>Puts the fields back into their shipped grouping.</summary>
    public CardLayout Reset() => new(DefaultRows, true, true);

    private List<List<string>> Mutable() => Rows.Select(row => row.ToList()).ToList();
}
