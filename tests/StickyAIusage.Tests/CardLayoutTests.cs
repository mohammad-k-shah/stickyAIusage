using StickyAIusage.Services;
using StickyAIusage.ViewModels;

namespace StickyAIusage.Tests;

/// <summary>The rules behind the field customizer: what a layout accepts,
/// what it throws away, and what dragging a chip does.</summary>
public class CardLayoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 20, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_default_layout_is_what_the_card_always_showed()
    {
        Assert.Equal(
            [["model", "effort"], ["in", "out", "cached", "total"], ["cache-hit", "cache-read", "cache-write"]],
            CardLayout.Default.Rows);
        // Compactions is a field to add, not one the card starts with - and
        // "reset to defaults" doesn't bring it back either.
        Assert.False(CardLayout.Default.Contains("compactions"));
        Assert.False(CardLayout.Default.Reset().Contains("compactions"));
        Assert.NotNull(SessionFields.Find("compactions"));
        Assert.True(CardLayout.Default.ShowContextBar);
        Assert.True(CardLayout.Default.ShowGitDetails);
    }

    [Fact]
    public void A_layout_round_trips_through_settings()
    {
        var layout = CardLayout.Create([["model", "cost"], ["git-branch", "git-diff"]], showContextBar: false, showGitDetails: true);
        var settings = new AppSettings();
        settings.SetCardLayout(layout);

        Assert.Equal("model,cost|git-branch,git-diff", settings.CardFields);
        var reloaded = settings.CardLayout();
        Assert.Equal(layout.Rows, reloaded.Rows);
        Assert.False(reloaded.ShowContextBar);
        Assert.True(reloaded.ShowGitDetails);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nothing,here")] // only unknown ids: better the default card than an empty one
    public void Unusable_settings_fall_back_to_the_default(string? saved) =>
        Assert.Equal(CardLayout.Default.Rows, CardLayout.Parse(saved).Rows);

    [Fact]
    public void Settings_still_holding_the_old_default_card_get_the_new_one()
    {
        // The app used to save its own default, so this string means "never
        // customized" rather than "wanted Compactions".
        var saved = "model,effort,compactions|in,out,cached,total|cache-hit,cache-read,cache-write";

        Assert.Equal(CardLayout.Default.Rows, CardLayout.Parse(saved).Rows);
        // A card that really does want it keeps it.
        Assert.True(CardLayout.Parse("model,compactions").Contains("compactions"));
    }

    [Fact]
    public void Unknown_ids_duplicates_and_empty_rows_are_dropped()
    {
        var layout = CardLayout.Parse("model, made-up ,effort||model,out");

        Assert.Equal([["model", "effort"], ["out"]], layout.Rows);
    }

    [Fact]
    public void Adding_and_removing_fields()
    {
        var layout = CardLayout.Create([["model"]]).Add("cost").Add("git-branch", rowIndex: 0);

        Assert.Equal([["model", "cost", "git-branch"]], layout.Rows);
        Assert.True(layout.Contains("cost"));
        // Already on the card, and unknown ids, change nothing.
        Assert.Equal(layout.Rows, layout.Add("cost").Rows);
        Assert.Equal(layout.Rows, layout.Add("made-up").Rows);

        var without = layout.Remove("cost");
        Assert.Equal([["model", "git-branch"]], without.Rows);
        // Emptying a row removes the row.
        Assert.Empty(CardLayout.Create([["model"]]).Remove("model").Rows);
    }

    [Fact]
    public void Picking_a_field_from_the_catalog_puts_it_beside_its_own_group()
    {
        // Session ID is a Core field, and the first row is the Core row.
        var layout = CardLayout.Default.AddGrouped("session-id");
        Assert.Equal(["model", "effort", "session-id"], layout.Rows[0]);

        // A group with no row of its own starts one at the end, and the next
        // field of that group joins it there.
        var git = layout.AddGrouped("git-branch");
        Assert.Equal(["git-branch"], git.Rows[^1]);
        Assert.Equal(["git-branch", "git-sha"], git.AddGrouped("git-sha").Rows[^1]);
    }

    [Fact]
    public void A_full_group_row_sends_the_next_field_of_that_group_to_a_new_row()
    {
        var git = SessionFields.InCategory(FieldCategory.Git).Select(f => f.Id).ToList();
        var full = git.Take(CardLayout.MaxFieldsPerRow).ToList();

        var layout = CardLayout.Create([full]).AddGrouped(git[CardLayout.MaxFieldsPerRow]);

        Assert.Equal(full, layout.Rows[0]);
        Assert.Equal([git[CardLayout.MaxFieldsPerRow]], layout.Rows[1]);
    }

    [Fact]
    public void A_chip_dropped_on_a_full_row_stays_where_it_was()
    {
        var full = SessionFields.InCategory(FieldCategory.Git).Take(CardLayout.MaxFieldsPerRow).Select(f => f.Id).ToList();
        var layout = CardLayout.Create([full, ["model", "effort"]]);

        // The ninth chip was being dropped on the floor; now the drop is
        // refused and the card is left as it was.
        Assert.False(layout.AcceptsDrop("model", rowIndex: 0));
        Assert.Same(layout, layout.Move("model", rowIndex: 0, position: 8));
        Assert.Equal("A row can have a max. of 8 fields", CardLayout.RowFullMessage);

        // Reordering inside a full row is a move, not a ninth field.
        Assert.True(layout.AcceptsDrop(full[0], rowIndex: 0));
        Assert.Equal(full.Skip(1).Append(full[0]), layout.Move(full[0], 0, 8).Rows[0]);

        // Rows with room, and the strip below the last row, take it.
        Assert.True(layout.AcceptsDrop(full[0], rowIndex: 1));
        Assert.True(layout.AcceptsDrop(full[0], rowIndex: 2));
        Assert.False(layout.AcceptsDrop("made-up", rowIndex: 1));
    }

    [Fact]
    public void Dragging_a_chip_moves_it_and_recolors_by_position()
    {
        var layout = CardLayout.Create([["model", "effort", "cost"], ["in", "out"]]);

        // To the front of the second row.
        var moved = layout.Move("cost", rowIndex: 1, position: 0);
        Assert.Equal([["model", "effort"], ["cost", "in", "out"]], moved.Rows);

        // Colors come from the position, so the moved chip takes its new row's
        // first color and pushes the others along.
        var card = SessionCardViewModel.From(CardPreview.Sample(Now), Now, moved);
        Assert.Equal(["Cost", "In", "Out"], card.Rows[1].Segments.Select(s => s.Label));
        Assert.Equal([0, 1, 2], card.Rows[1].Segments.Select(s => s.ColorIndex));
        Assert.Equal([true, false, false], card.Rows[1].Segments.Select(s => s.IsFirst));
    }

    [Fact]
    public void Dragging_within_a_row_accounts_for_the_gap_the_chip_leaves()
    {
        var layout = CardLayout.Create([["model", "effort", "cost"]]);

        // Dropped at the end: "model" is removed first, so position 3 is the end.
        Assert.Equal([["effort", "cost", "model"]], layout.Move("model", 0, 3).Rows);
        // Dropped before itself: unchanged.
        Assert.Equal([["model", "effort", "cost"]], layout.Move("model", 0, 0).Rows);
    }

    [Fact]
    public void Dropping_past_the_last_row_starts_a_new_one()
    {
        var layout = CardLayout.Create([["model", "effort"]]);

        Assert.Equal([["model"], ["effort"]], layout.Move("effort", rowIndex: 1, position: 0).Rows);
    }

    [Fact]
    public void A_field_with_nothing_to_show_leaves_no_gap_on_the_card()
    {
        // A session with no git and nothing compacted.
        var session = CardPreview.Sample(Now);
        session.Git = null;
        var layout = CardLayout.Create([["model", "git-branch"], ["git-diff"]]);

        var card = SessionCardViewModel.From(session, Now, layout);

        // The git-only row disappears; the remaining row has just the model.
        Assert.Equal([["Model"]], card.Rows.Select(r => r.Segments.Select(s => s.Label)));
    }

    [Fact]
    public void Every_catalog_field_has_something_to_show_in_the_preview()
    {
        var sample = CardPreview.Sample(Now);

        var empty = SessionFields.All.Where(f => f.Value(sample) is null or "").Select(f => f.Id).ToList();

        Assert.Empty(empty);
        Assert.Equal(SessionFields.All.Count, SessionFields.All.Select(f => f.Id).Distinct().Count());
        Assert.All(SessionFields.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
    }

    [Fact]
    public void A_full_row_uses_eight_colors_without_repeating_one()
    {
        var row = SessionFields.All.Take(CardLayout.MaxFieldsPerRow).Select(f => f.Id).ToList();
        var card = SessionCardViewModel.From(CardPreview.Sample(Now), Now, CardLayout.Create([row]));

        var colors = card.Rows[0].Segments.Select(s => s.ColorIndex).ToList();
        Assert.Equal(Enumerable.Range(0, CardLayout.MaxFieldsPerRow), colors);
        Assert.Equal(colors.Count, colors.Distinct().Count());
        Assert.Equal(CardLayout.MaxFieldsPerRow, Segment.Colors);
    }

    [Fact]
    public void Fields_are_grouped_the_way_the_picker_lists_them()
    {
        // Same category names and order, minus the groups none of our fields
        // fall into (Jujutsu, Token Speed, Usage, Custom, Layout).
        Assert.Equal(["Core", "Git", "Environment", "Tokens", "Cache", "Context", "Session"],
            Enum.GetValues<FieldCategory>().Select(SessionFields.CategoryName));
        Assert.All(Enum.GetValues<FieldCategory>(), c => Assert.NotEmpty(SessionFields.InCategory(c)));
        Assert.Equal(SessionFields.All.Count,
            Enum.GetValues<FieldCategory>().Sum(c => SessionFields.InCategory(c).Count()));
    }

    [Fact]
    public void Blocks_can_be_turned_off()
    {
        var card = SessionCardViewModel.From(CardPreview.Sample(Now), Now,
            CardLayout.Default.WithBlocks(showContextBar: false, showGitDetails: false));

        Assert.False(card.HasContext);
        Assert.False(card.HasGit);
    }
}
