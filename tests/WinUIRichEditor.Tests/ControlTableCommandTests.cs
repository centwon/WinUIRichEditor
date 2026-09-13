using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Where the table-structure commands leave the caret when the table has merged cells.
/// <para>Found by <see cref="ControlCommandFuzzTests"/> at 2000 seeds (3 of them; none within CI's 24).
/// Each row/column command parks the caret at a fixed slot of the edited table — row <c>at</c>,
/// column 0 for rows; row 0, column <c>at</c> for columns — and that slot can be COVERED: inserting
/// inside a merge extends it over the new cells, and deleting next to one can clamp onto one. A covered
/// slot is not drawn, not hit-testable and not exported (every walk goes through
/// <see cref="TableBlock.LogicalCells"/>), so the user was left typing into a cell that shows nothing and
/// saves nothing. Upstream routes the same four commands through <c>CellCaretTarget</c>, which resolves
/// the slot to its anchor; the port set the caret on the raw slot.</para>
/// <para>The fuzz reached insert-row, insert-column and delete-row; delete-column has the same shape
/// and is pinned alongside (this repo's "other half of the pair" pattern).</para>
/// <para>This also revisits the roadmap's rejection of external-audit item 2.5, which rested on
/// "the caret can never be in a covered cell". For these four commands that was not true.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlTableCommandTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly System.Type T = typeof(RichEditor);

    private static TableBlock Table(int rows, int cols)
    {
        var tb = new TableBlock(rows, cols);
        foreach (var (r, c, cell) in tb.LogicalCells()) ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c}";
        return tb;
    }

    // Built first, then assigned: the Document setter wires Parent (see ControlUndoTests).
    private static RichEditor Host(TableBlock tb)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "top" } } });
        doc.Blocks.Add(tb);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "end" } } });
        return new RichEditor { Document = doc };
    }

    private static void CaretIn(RichEditor ed, Paragraph p)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, 0));
    }

    // The context menu's entry points (RichEditor.ContextMenu: above/left = r/c, below/right = r+1/c+1).
    private static void Menu(RichEditor ed, string method, TableBlock tb, int at)
    {
        try { T.GetMethod(method, NP)!.Invoke(ed, new object[] { tb, at }); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    // Two assertions, the structural one and the one the user would notice: the caret is in a cell the
    // table actually has (an anchor), and what is typed next appears in the document as it is drawn and
    // saved — Shape walks LogicalCells, like the renderer and every formatter.
    private static void AssertTheCaretIsInALiveCell(RichEditor ed, TableBlock tb)
    {
        var caret = (TextPointer)T.GetField("_caret", NP)!.GetValue(ed)!;
        var cell = caret.Paragraph?.Parent as TableCell;
        Assert.NotNull(cell);
        Assert.True(tb.LogicalCells().Any(x => ReferenceEquals(x.Item3, cell)),
            "the caret is in a covered slot of the table — nothing typed there is drawn or saved");

        ed.InsertText("TYPED");
        Assert.Contains("TYPED", DocumentFuzzTests.Shape(ed.Document!));
    }

    // Inserting INSIDE a merge is still reachable after the menu fix below: "insert row above" on a cell
    // beside the merge's lower rows. The merge grows over the new row, and its column-0 slot is covered.
    [Fact]
    public void InsertingARowInsideAVerticalMerge_LeavesTheCaretInALiveCell() => UiThread.Run(() =>
    {
        var tb = Table(3, 2);
        tb.MergeCells(0, 0, 2, 0);
        var ed = Host(tb);
        CaretIn(ed, tb.Cells[1][1].Para);

        Menu(ed, "TableInsertRow", tb, 1); // "above" on (1,1): inside the merge

        AssertTheCaretIsInALiveCell(ed, tb);
    });

    [Fact]
    public void InsertingAColumnInsideAHorizontalMerge_LeavesTheCaretInALiveCell() => UiThread.Run(() =>
    {
        var tb = Table(2, 3);
        tb.MergeCells(0, 0, 0, 2);
        var ed = Host(tb);
        CaretIn(ed, tb.Cells[1][1].Para);

        Menu(ed, "TableInsertColumn", tb, 1); // "left" of (1,1): inside the merge

        AssertTheCaretIsInALiveCell(ed, tb);
    });

    // "Insert row below" on a vertically merged cell adds the row under the WHOLE merge, as Word does. The
    // menu passed r + 1 — inside the merge — so the merge grew over the new row and the new cells appeared
    // only beside it. Reported from a live check; upstream passes the same r + 1.
    [Fact]
    public void InsertRowBelow_AVerticallyMergedCell_AddsTheRowUnderTheWholeMerge() => UiThread.Run(() =>
    {
        var tb = Table(3, 2);
        tb.MergeCells(0, 0, 2, 0);
        var ed = Host(tb);
        CaretIn(ed, tb.Cells[0][0].Para);

        Menu(ed, "TableInsertRow", tb, RichEditor.RowBelowIndex(tb, 0, 0));

        Assert.Equal(4, tb.Rows);
        Assert.Equal(3, tb.SpanOf(0, 0).rs); // the merge did not grow
        Assert.False(tb.IsCovered(3, 0));    // the new row is a full row of live cells
        Assert.False(tb.IsCovered(3, 1));
    });

    [Fact]
    public void InsertColumnRight_OfAHorizontallyMergedCell_AddsTheColumnAfterTheWholeMerge() => UiThread.Run(() =>
    {
        var tb = Table(2, 3);
        tb.MergeCells(0, 0, 0, 2);
        var ed = Host(tb);
        CaretIn(ed, tb.Cells[0][0].Para);

        Menu(ed, "TableInsertColumn", tb, RichEditor.ColumnRightIndex(tb, 0, 0));

        Assert.Equal(4, tb.Columns);
        Assert.Equal(3, tb.SpanOf(0, 0).cs);
        Assert.False(tb.IsCovered(0, 3));
        Assert.False(tb.IsCovered(1, 3));
    });

    [Fact]
    public void DeleteRow_BelowAVerticalMerge_LeavesTheCaretInALiveCell() => UiThread.Run(() =>
    {
        var tb = Table(3, 2);
        tb.MergeCells(0, 0, 1, 0);
        var ed = Host(tb);
        CaretIn(ed, tb.Cells[2][1].Para);

        Menu(ed, "TableDeleteRow", tb, 2); // the caret clamps to row 1, column 0 — covered by (0,0)

        AssertTheCaretIsInALiveCell(ed, tb);
    });

    [Fact]
    public void DeleteColumn_RightOfAHorizontalMerge_LeavesTheCaretInALiveCell() => UiThread.Run(() =>
    {
        var tb = Table(2, 3);
        tb.MergeCells(0, 0, 0, 1);
        var ed = Host(tb);
        CaretIn(ed, tb.Cells[1][2].Para);

        Menu(ed, "TableDeleteColumn", tb, 2); // the caret clamps to row 0, column 1 — covered by (0,0)

        AssertTheCaretIsInALiveCell(ed, tb);
    });
}
