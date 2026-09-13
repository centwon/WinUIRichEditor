using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The cell block — unified with AvaloniaRichEditor (2026-09-13). A block of several cells is derived
/// from the two selection endpoints; a ONE-cell block (F5, the menu's "셀 선택") is a marker that lives only as
/// long as the exact selection it was made with. Shift+arrow grows and shrinks a block by whole cells (HWP).
/// <para>Upstream kept a mode flag instead, reset at each entry point — and Shift+arrow across cells turned it off
/// while the renderer still filled the block, so Delete acted on characters under a painted cell block
/// (measured). Here the renderer and every command read one function (CellBlockSelection).</para></summary>
[Collection(UiTests.Collection)]
public class ControlCellBlockTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static object? Call(RichEditor ed, string name, params object?[] args)
    {
        try { return T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    // A document [p, table, p] whose every cell holds its own "rc" text; the caret in cell (r, c) at offset 1.
    private static (RichEditor ed, TableBlock tb) Grid(int rows, int cols, int r = 0, int c = 0)
    {
        var tb = new TableBlock(rows, cols);
        foreach (var (rr, cc, cell) in tb.LogicalCells()) ((Run)cell.Para.Inlines[0]).Text = $"{rr}{cc}";
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(tb);
        doc.Blocks.Add(new Paragraph());
        var ed = new RichEditor { Document = doc };
        Caret(ed, tb.Cells[r][c].Para, 1);
        return (ed, tb);
    }

    private static void Caret(RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
    }

    private static (TableBlock tb, int r0, int c0, int r1, int c1)? Block(RichEditor ed)
        => ((TableBlock tb, int r0, int c0, int r1, int c1)?)Call(ed, "CellBlockSelection");

    private static bool Key(RichEditor ed, VirtualKey key, bool shift = false)
        => (bool)Call(ed, "TryCellBlockKey", key, shift, false, false)!;

    private static string Text(TableCell cell)
        => string.Concat(cell.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));

    private static bool HasSelection(RichEditor ed) => (bool)T.GetProperty("HasSelection", NP)!.GetValue(ed)!;

    [Fact]
    public void F5_SelectsTheCaretsCell_AsABlock() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 2, 0, 1);
        Assert.Null(Block(ed));
        Assert.True(Key(ed, VirtualKey.F5));
        Assert.Equal((tb, 0, 1, 0, 1), Block(ed));
    });

    [Fact]
    public void F5_OutsideATable_IsNotHandled() => UiThread.Run(() =>
    {
        var (ed, _) = Grid(2, 2);
        Caret(ed, (Paragraph)ed.Document!.Blocks[0], 0);
        Assert.False(Key(ed, VirtualKey.F5));
        Assert.Null(Block(ed));
    });

    // An EMPTY cell's one-cell block is a zero-length range — still a selection, and it copies as a 1×1 table.
    // It read as "nothing selected", so a right-click dropped it (found by the table-menu right-click test).
    [Fact]
    public void AnEmptyCell_F5_IsStillASelection_AndCopiesAsAOneByOneTable() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 2);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "";
        Caret(ed, tb.Cells[0][0].Para, 0);
        Key(ed, VirtualKey.F5);

        Assert.True(HasSelection(ed));
        Assert.Equal((tb, 0, 0, 0, 0), Block(ed));
        _ = ed.CopyAsync();
        var clip = (FlowDocument?)T.GetField("_internalClipboardDoc", NP)!.GetValue(ed);
        var copied = Assert.IsType<TableBlock>(Assert.Single(clip!.Blocks));
        Assert.Equal((1, 1), (copied.Rows, copied.Columns));
    });

    // Delete on a one-cell block clears that cell as a unit — the cell block path, the same as a multi-cell one.
    [Fact]
    public void Delete_OnAOneCellBlock_ClearsThatCell_AndNoOther() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 2, 1, 0);
        Key(ed, VirtualKey.F5);
        Call(ed, "DeleteForward");
        Assert.Equal("", Text(tb.Cells[1][0]));
        Assert.Equal(new[] { "00", "01", "11" }, new[] { Text(tb.Cells[0][0]), Text(tb.Cells[0][1]), Text(tb.Cells[1][1]) });
    });

    // Copy takes a one-cell block as a 1×1 table — so it pastes back as a cell, not as the text inside it.
    [Fact]
    public void Copy_OfAOneCellBlock_IsAOneByOneTable() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 2, 0, 1);
        Key(ed, VirtualKey.F5);
        _ = ed.CopyAsync();
        var clip = (FlowDocument?)T.GetField("_internalClipboardDoc", NP)!.GetValue(ed);
        var copied = Assert.IsType<TableBlock>(Assert.Single(clip!.Blocks));
        Assert.Equal((1, 1), (copied.Rows, copied.Columns));
        Assert.Equal("01", Text(copied.Cells[0][0]));
    });

    [Fact]
    public void Formatting_AOneCellBlock_TakesTheWholeCell_AndNoOther() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(1, 2);
        Key(ed, VirtualKey.F5);
        ed.ToggleBold();
        Assert.All(tb.Cells[0][0].Para.Inlines.OfType<Run>(), r => Assert.True(r.FontWeight.Weight >= 700));
        Assert.All(tb.Cells[0][1].Para.Inlines.OfType<Run>(), r => Assert.True(r.FontWeight.Weight < 700));
    });

    // Any caret move ends the block — no mode to reset. And the SAME range selected again later (the Ctrl+A
    // cell stage covers exactly the cell's content) is a text selection: the marker cannot come back to life.
    [Fact]
    public void ACaretMove_EndsTheBlock_AndTheSameRangeLater_IsText() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 2);
        Key(ed, VirtualKey.F5);
        Call(ed, "MoveCaretRight", false);
        Assert.Null(Block(ed));

        Caret(ed, tb.Cells[0][0].Para, 1);
        Call(ed, "SelectAll"); // stage 1: the cell's content — the very range F5 selected
        Assert.True(HasSelection(ed));
        Assert.Null(Block(ed));
    });

    // HWP: the anchor corner stays and the active corner steps a cell; back on the anchor it is one cell again,
    // and at the table's edge nothing moves.
    [Fact]
    public void ShiftArrows_GrowAndShrinkTheBlock_ByWholeCells() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(3, 3, 1, 1);
        Key(ed, VirtualKey.F5);
        Assert.True(Key(ed, VirtualKey.Right, shift: true));
        Assert.Equal((tb, 1, 1, 1, 2), Block(ed));
        Key(ed, VirtualKey.Down, shift: true);
        Assert.Equal((tb, 1, 1, 2, 2), Block(ed));
        Key(ed, VirtualKey.Left, shift: true);
        Key(ed, VirtualKey.Left, shift: true);
        Assert.Equal((tb, 1, 0, 2, 1), Block(ed));
        Key(ed, VirtualKey.Up, shift: true);
        Assert.Equal((tb, 1, 0, 1, 1), Block(ed));
        Key(ed, VirtualKey.Right, shift: true);
        Assert.Equal((tb, 1, 1, 1, 1), Block(ed)); // back on the anchor: a one-cell block

        Caret(ed, tb.Cells[1][2].Para, 1);
        Key(ed, VirtualKey.F5);
        Assert.True(Key(ed, VirtualKey.Right, shift: true)); // handled — the block does not dissolve into text
        Assert.Equal((tb, 1, 2, 1, 2), Block(ed));
    });

    // A merged cell is one cell: F5 takes its whole span, and Shift+arrow steps past the span, not into it.
    [Fact]
    public void AMergedCell_IsOneCell_ForF5_AndForShiftArrow() => UiThread.Run(() =>
    {
        var tb = new TableBlock(2, 3);
        foreach (var (rr, cc, cell) in tb.LogicalCells()) ((Run)cell.Para.Inlines[0]).Text = $"{rr}{cc}";
        tb.MergeCells(0, 0, 0, 1);
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(tb);
        doc.Blocks.Add(new Paragraph());
        var ed = new RichEditor { Document = doc };
        Caret(ed, tb.Cells[0][0].Blocks.OfType<Paragraph>().First(), 0);

        Key(ed, VirtualKey.F5);
        Assert.Equal((tb, 0, 0, 0, 1), Block(ed));
        Key(ed, VirtualKey.Right, shift: true);
        Assert.Equal((tb, 0, 0, 0, 2), Block(ed));
    });

    // A block made by selecting across cells (not by F5) grows the same way from where it is.
    [Fact]
    public void ShiftArrow_OnADraggedBlock_GrowsItByCells() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 3);
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(tb.Cells[0][0].Para, 1));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(tb.Cells[0][1].Para, 1));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(tb.Cells[0][1].Para, 1));
        Assert.Equal((tb, 0, 0, 0, 1), Block(ed));
        Key(ed, VirtualKey.Right, shift: true);
        Assert.Equal((tb, 0, 0, 0, 2), Block(ed));
    });

    // Inside one cell's text, Shift+arrow is ordinary text selection — not taken by the block keys.
    [Fact]
    public void ShiftArrow_WithoutABlock_IsLeftToTextSelection() => UiThread.Run(() =>
    {
        var (ed, _) = Grid(2, 2);
        Assert.False(Key(ed, VirtualKey.Right, shift: true));
        Assert.Null(Block(ed));
    });

    // Ctrl+A in an inline table inside an outer table's cell climbs cell → inline table → OUTER table → document.
    // It skipped the outer table: from the inline table straight to the whole document (measured 2026-09-14).
    [Fact]
    public void CtrlA_InAnInlineTableInACell_ClimbsThroughTheOuterTable() => UiThread.Run(() =>
    {
        var outer = new TableBlock(1, 2);
        ((Run)outer.Cells[0][0].Para.Inlines[0]).Text = "host ";
        ((Run)outer.Cells[0][1].Para.Inlines[0]).Text = "other";
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "before" } } });
        doc.Blocks.Add(outer);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });
        var ed = new RichEditor { Document = doc };
        var hostPara = outer.Cells[0][0].Para;
        Caret(ed, hostPara, 5);
        ed.InsertInlineTable(1, 2);
        var inner = hostPara.Inlines.OfType<InlineTable>().Single().Table;
        var innerPara = inner.Cells[0][0].Para;
        ((Run)innerPara.Inlines[0]).Text = "in";
        Caret(ed, innerPara, 1);

        (Paragraph? s, int so, Paragraph? e, int eo) Stage()
        {
            Call(ed, "SelectAll");
            var s = (TextPointer)T.GetField("_selStart", NP)!.GetValue(ed)!;
            var e = (TextPointer)T.GetField("_selEnd", NP)!.GetValue(ed)!;
            return (s.Paragraph, s.Offset, e.Paragraph, e.Offset);
        }

        Assert.Equal((innerPara, 0, innerPara, 2), Stage());                          // 1: the cell
        Assert.Equal((innerPara, 0, inner.Cells[0][1].Para, 0), Stage());            // 2: the inline table
        Assert.Equal((hostPara, 0, outer.Cells[0][1].Para, 5), Stage());             // 3: the table around it
        Assert.Equal(((Paragraph)doc.Blocks[0], 0, (Paragraph)doc.Blocks[^1], 5), Stage()); // 4: the document
    });

    [Fact]
    public void TheTableMenu_OffersSelectCell_WithItsKey_AndItSelectsTheCell() => UiThread.Run(() =>
    {
        var (ed, tb) = Grid(2, 2, 1, 1);
        var sub = ed.BuildTableSubmenu(tb, 1, 1);
        var item = sub.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == RichEditorLocalization.GetString("SelectCell"));
        Assert.Equal("F5", item.KeyboardAcceleratorTextOverride);
        ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.MenuFlyoutItemAutomationPeer(item)).Invoke();
        Assert.Equal((tb, 1, 1, 1, 1), Block(ed));
    });
}
