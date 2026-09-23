using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The public row/column commands (upstream PR #48, 2026-09-20). They existed only behind the
/// context menu, so a host building its own toolbar — or generating a document by script — had to edit
/// <see cref="TableBlock"/> itself and skip the undo checkpoint, the parent wiring and the layout
/// invalidation the commands do.
/// <para>Two shapes over the same body: <c>Table*</c> names the table and the index, the caret six act where
/// the caret is. These tests hold what a host cannot see: the guards on a public entry point (an index from
/// anywhere, a table from another document, a read-only editor), that one call is one undo step, and that
/// "below/right" means past a merged area — the rule the menu uses (<c>RowBelowIndex</c>).</para></summary>
[Collection(UiTests.Collection)]
public class TableStructureApiTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static void PlaceCaret(RichEditor ed, Paragraph p)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            typeof(RichEditor).GetField(f, NP)!.SetValue(ed, new TextPointer(p, 0));
    }

    // Built first, then assigned: the Document setter wires Parent (see ControlUndoTests).
    private static RichEditor Editor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "plain" } } });
        doc.Blocks.AddRange(blocks);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "end" } } });
        // A Document swap sets IsModified here (upstream's load clears it) — clear it so the "nothing
        // changed" assertions measure the command, not the setup.
        var ed = new RichEditor { Document = doc };
        ed.MarkSaved();
        return ed;
    }

    private static TableBlock Table(RichEditor ed) => ed.Document!.Blocks.OfType<TableBlock>().Single();

    [Fact]
    public void InsertingARow_AddsIt_AndIsOneUndoStep() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));

        Assert.True(ed.InsertTableRow(Table(ed), 1));
        Assert.Equal(3, Table(ed).Rows);

        ed.Undo();
        Assert.Equal(2, Table(ed).Rows);
    });

    [Fact]
    public void InsertingAtTheRowCount_Appends() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);

        Assert.True(ed.InsertTableRow(tb, tb.Rows));

        Assert.Equal(3, tb.Rows);
    });

    [Fact]
    public void DeletingARow_RemovesIt_AndIsOneUndoStep() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));

        Assert.True(ed.DeleteTableRow(Table(ed), 0));
        Assert.Equal(1, Table(ed).Rows);

        ed.Undo();
        Assert.Equal(2, Table(ed).Rows);
    });

    [Fact]
    public void ColumnsBehaveLikeRows() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);

        Assert.True(ed.InsertTableColumn(tb, 0));
        Assert.Equal(3, tb.Columns);
        Assert.True(ed.DeleteTableColumn(tb, 0));
        Assert.Equal(2, tb.Columns);
    });

    // A table always keeps its last row and column — the model refuses, and the public entry has to report
    // that rather than push an undo step for an edit that did not happen.
    [Fact]
    public void TheLastRowAndColumnAreKept() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(1, 1));
        var tb = Table(ed);

        Assert.False(ed.DeleteTableRow(tb, 0));
        Assert.False(ed.DeleteTableColumn(tb, 0));

        Assert.Equal(1, tb.Rows);
        Assert.Equal(1, tb.Columns);
        Assert.False(ed.IsModified);
    });

    // An index from a host is not trusted. Deletion takes 0..count-1, insertion one more (append).
    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public void AnOutOfRangeIndex_ChangesNothing(int at) => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);

        Assert.False(ed.InsertTableRow(tb, at));   // 2 rows: 0..2 is in range, 3 is not
        Assert.False(ed.DeleteTableRow(tb, at));
        Assert.False(ed.InsertTableColumn(tb, at));
        Assert.False(ed.DeleteTableColumn(tb, at));

        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Columns);
        Assert.False(ed.IsModified); // no undo checkpoint either
    });

    // The worst input: a live table that belongs to a DIFFERENT document. Editing it would push an undo
    // checkpoint for, and mutate, a tree this editor does not show.
    [Fact]
    public void ATableFromAnotherDocument_IsRefused() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var other = Editor(new TableBlock(2, 2));
        var foreign = Table(other);

        Assert.False(ed.InsertTableRow(foreign, 0));
        Assert.False(ed.DeleteTableRow(foreign, 0));
        Assert.False(ed.InsertTableColumn(foreign, 0));
        Assert.False(ed.DeleteTableColumn(foreign, 0));

        Assert.Equal(2, foreign.Rows);
        Assert.Equal(2, foreign.Columns);
        Assert.False(ed.IsModified);
        Assert.False(other.IsModified);
    });

    // A table the editor's document USED to hold still has its Parent wired to that document — a
    // parent-chain check would call it "inside" (the round-34 lesson). The walker does not.
    [Fact]
    public void ATableRemovedFromTheDocument_IsRefused() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);
        ed.Document!.Blocks.Remove(tb);

        Assert.False(ed.InsertTableRow(tb, 0));
        Assert.Equal(2, tb.Rows);
    });

    [Fact]
    public void AReadOnlyEditor_RefusesAll() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);
        PlaceCaret(ed, tb.Cells[0][0].Para);
        ed.IsReadOnly = true;

        Assert.False(ed.InsertTableRow(tb, 0));
        Assert.False(ed.DeleteTableRow(tb, 0));
        Assert.False(ed.InsertRowBelow());
        Assert.False(ed.DeleteColumn());

        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Columns);
    });

    // ---- the caret shape --------------------------------------------------------------------------

    [Fact]
    public void TheCaretCommands_ActOnTheCaretsTable() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);
        PlaceCaret(ed, tb.Cells[0][0].Para);

        Assert.True(ed.InsertRowAbove());
        Assert.Equal(3, tb.Rows);
        Assert.True(ed.InsertColumnLeft());
        Assert.Equal(3, tb.Columns);
        Assert.True(ed.DeleteRow());
        Assert.Equal(2, tb.Rows);
        Assert.True(ed.DeleteColumn());
        Assert.Equal(2, tb.Columns);
    });

    [Fact]
    public void WithTheCaretOutsideATable_TheCaretCommandsDoNothing() => UiThread.Run(() =>
    {
        var ed = Editor(new TableBlock(2, 2));
        var tb = Table(ed);
        PlaceCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First());

        Assert.False(ed.InsertRowAbove());
        Assert.False(ed.InsertRowBelow());
        Assert.False(ed.DeleteRow());
        Assert.False(ed.InsertColumnLeft());
        Assert.False(ed.InsertColumnRight());
        Assert.False(ed.DeleteColumn());

        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Columns);
        Assert.False(ed.IsModified);
    });

    // "Below" a cell that spans two rows is below the WHOLE merge: at r+1 the new row lands inside it, the
    // merge grows over it, and only the other columns gain a row. Same rule as the context menu.
    [Fact]
    public void InsertRowBelow_GoesPastAVerticalMerge() => UiThread.Run(() =>
    {
        var built = new TableBlock(3, 2);
        built.MergeCells(0, 0, 1, 0);
        var ed = Editor(built);
        var tb = Table(ed);
        Assert.Equal((1, 2), tb.SpanOf(0, 0)); // the fixture really is merged
        PlaceCaret(ed, tb.Cells[0][0].Para);

        Assert.True(ed.InsertRowBelow());

        // The new row is row 2 — after the merged area — so the merge still spans exactly rows 0..1.
        Assert.Equal(4, tb.Rows);
        Assert.Equal((1, 2), tb.SpanOf(0, 0));
        Assert.False(tb.IsCovered(2, 0));
    });

    [Fact]
    public void InsertColumnRight_GoesPastAHorizontalMerge() => UiThread.Run(() =>
    {
        var built = new TableBlock(2, 3);
        built.MergeCells(0, 0, 0, 1);
        var ed = Editor(built);
        var tb = Table(ed);
        Assert.Equal((2, 1), tb.SpanOf(0, 0));
        PlaceCaret(ed, tb.Cells[0][0].Para);

        Assert.True(ed.InsertColumnRight());

        Assert.Equal(4, tb.Columns);
        Assert.Equal((2, 1), tb.SpanOf(0, 0));
        Assert.False(tb.IsCovered(0, 2));
    });

    // Cells hold block lists, so tables nest. FindCell resolves the INNERMOST table — the caret commands
    // must edit that one, not the table around it.
    [Fact]
    public void InANestedTable_TheCaretCommandsEditTheInnerTable() => UiThread.Run(() =>
    {
        var built = new TableBlock(1, 2);
        built.Cells[0][0].Blocks.Add(new TableBlock(1, 2));
        var ed = Editor(built);
        var outer = Table(ed);
        var inner = outer.Cells[0][0].Blocks.OfType<TableBlock>().Single();
        PlaceCaret(ed, inner.Cells[0][0].Para);

        Assert.True(ed.InsertRowBelow());

        Assert.Equal(2, inner.Rows);
        Assert.Equal(1, outer.Rows);
    });

    // An inline table lives in a paragraph's inlines, not in any block list — the containment check walks
    // there too (BlockWalk descends into inline tables), so its rows are editable like any other.
    [Fact]
    public void AnInlineTable_IsEditableToo() => UiThread.Run(() =>
    {
        var host = new Paragraph
        {
            Inlines = { new Run { Text = "x" }, new InlineTable { Table = new TableBlock(1, 2) }, new Run { Text = "y" } }
        };
        var ed = Editor(host);
        var it = ed.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineTable>()).Single();
        PlaceCaret(ed, it.Table.Cells[0][0].Para);

        Assert.True(ed.InsertTableRow(it.Table, 1));
        Assert.True(ed.InsertRowBelow());

        Assert.Equal(3, it.Table.Rows);
    });

    // ---- an object selected inside what the command removes (2026-09-24) ---------------------------
    // Pointer, key and menu paths let go of a selected object before they edit; a host call does not. A
    // nested table or picture selected in a row the host deletes stayed selected after it left the document,
    // and Delete on the inline picture then edited the detached row and parked the caret there.

    private static void SetField(RichEditor ed, string name, object? value)
        => typeof(RichEditor).GetField(name, NP)!.SetValue(ed, value);

    private static object? Field(RichEditor ed, string name)
        => typeof(RichEditor).GetField(name, NP)!.GetValue(ed);

    // By walking the document (AllParagraphs), not the parent chain: a detached row's paragraphs still
    // name their old cell, whose Parent is still the table (the round-34 lesson).
    private static bool CaretInDocument(RichEditor ed)
    {
        var caret = (TextPointer)Field(ed, "_caret")!;
        var all = (System.Collections.IEnumerable)typeof(RichEditor)
            .GetMethod("AllParagraphs", NP, System.Type.EmptyTypes)!.Invoke(ed, null)!;
        return all.Cast<Paragraph>().Any(p => ReferenceEquals(p, caret.Paragraph));
    }

    private static void DeleteSelectedObject(RichEditor ed)
        => typeof(RichEditor).GetMethod("DeleteSelectedObject", NP)!.Invoke(ed, null);

    [Fact]
    public void DeletingTheRowAroundASelectedNestedTable_LetsGoOfIt() => UiThread.Run(() =>
    {
        var built = new TableBlock(2, 1);
        built.Cells[1][0].Blocks.Add(new TableBlock(1, 1));
        var ed = Editor(built);
        var tb = Table(ed);
        var nested = tb.Cells[1][0].Blocks.OfType<TableBlock>().Single();
        PlaceCaret(ed, tb.Cells[0][0].Para);
        SetField(ed, "_selectedBlock", nested);

        Assert.True(ed.DeleteTableRow(tb, 1));

        Assert.False(ed.HasBlockSelection);
    });

    [Fact]
    public void DeletingTheColumnAroundASelectedInlinePicture_ThenDelete_KeepsTheCaretInTheDocument() => UiThread.Run(() =>
    {
        var built = new TableBlock(1, 2);
        var host = built.Cells[0][1].Para;
        var img = new InlineImage { Width = 10, Height = 10 };
        host.Inlines.Add(img);
        var ed = Editor(built);
        var tb = Table(ed);
        PlaceCaret(ed, tb.Cells[0][0].Para);
        SetField(ed, "_selectedInline", ((Paragraph, InlineImage)?)(host, img));

        Assert.True(ed.DeleteTableColumn(tb, 1));
        Assert.False(ed.HasBlockSelection);

        DeleteSelectedObject(ed); // what the Delete key does with an object selection
        Assert.True(CaretInDocument(ed));
    });

    [Fact]
    public void DeletingTheRowAroundASelectedInlineTable_LetsGoOfIt() => UiThread.Run(() =>
    {
        var built = new TableBlock(2, 1);
        var host = built.Cells[1][0].Para;
        var it = new InlineTable { Table = new TableBlock(1, 1) };
        host.Inlines.Add(it);
        var ed = Editor(built);
        var tb = Table(ed);
        PlaceCaret(ed, tb.Cells[0][0].Para);
        SetField(ed, "_selectedInlineTable", ((Paragraph, InlineTable)?)(host, it));

        Assert.True(ed.DeleteTableRow(tb, 1));

        Assert.False(ed.HasBlockSelection);
    });

    // The other half: an object the edit did NOT remove stays selected — the table held by its border while
    // a row is added to it, or a picture in a row that survives. Without this the fix could simply clear
    // every selection on every edit.
    [Fact]
    public void AnObjectTheEditLeavesInPlace_StaysSelected() => UiThread.Run(() =>
    {
        var built = new TableBlock(2, 1);
        var host = built.Cells[0][0].Para;
        var img = new InlineImage { Width = 10, Height = 10 };
        host.Inlines.Add(img);
        var ed = Editor(built);
        var tb = Table(ed);
        PlaceCaret(ed, tb.Cells[0][0].Para);
        SetField(ed, "_selectedInline", ((Paragraph, InlineImage)?)(host, img));

        Assert.True(ed.DeleteTableRow(tb, 1));
        Assert.True(ed.HasBlockSelection);

        SetField(ed, "_selectedInline", null);
        SetField(ed, "_selectedBlock", tb);
        Assert.True(ed.InsertTableRow(tb, 0));
        Assert.Same(tb, Field(ed, "_selectedBlock"));
    });
}
