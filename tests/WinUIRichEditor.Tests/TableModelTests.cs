using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

public class TableModelTests
{
    [Fact]
    public void Constructor_BuildsGrid()
    {
        var tb = new TableBlock(2, 3);
        Assert.Equal(2, tb.Rows);
        Assert.Equal(3, tb.Columns);
        Assert.Equal(2, tb.Cells.Count);
        Assert.Equal(3, tb.Cells[0].Count);
        Assert.Equal(6, System.Linq.Enumerable.Count(tb.LogicalCells()));
    }

    [Fact]
    public void InsertRow_GrowsRows()
    {
        var tb = new TableBlock(2, 2);
        tb.InsertRow(1);
        Assert.Equal(3, tb.Rows);
        Assert.Equal(3, tb.Cells.Count);
        Assert.Equal(2, tb.Cells[1].Count);
    }

    [Fact]
    public void DeleteRow_ShrinksRows()
    {
        var tb = new TableBlock(3, 2);
        tb.DeleteRow(0);
        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Cells.Count);
    }

    [Fact]
    public void InsertAndDeleteColumn()
    {
        var tb = new TableBlock(2, 2);
        tb.InsertColumn(1);
        Assert.Equal(3, tb.Columns);
        Assert.Equal(3, tb.Cells[0].Count);
        tb.DeleteColumn(0);
        Assert.Equal(2, tb.Columns);
        Assert.Equal(2, tb.Cells[0].Count);
    }

    [Fact]
    public void MergeCells_CoversAndAnchors()
    {
        var tb = new TableBlock(3, 3);
        tb.MergeCells(0, 0, 1, 1); // 2x2 block anchored at (0,0)

        Assert.False(tb.IsCovered(0, 0));         // anchor
        Assert.True(tb.IsCovered(0, 1));          // covered
        Assert.True(tb.IsCovered(1, 0));
        Assert.True(tb.IsCovered(1, 1));
        Assert.Equal((0, 0), tb.AnchorOf(1, 1));  // covered cell -> anchor
        var (cs, rs) = tb.SpanOf(0, 0);
        Assert.Equal(2, cs);
        Assert.Equal(2, rs);

        // LogicalCells yields anchors only — a 3x3 with one 2x2 merge has 6 logical cells.
        Assert.Equal(6, System.Linq.Enumerable.Count(tb.LogicalCells()));
    }

    [Fact]
    public void UnmergeCell_RestoresGrid()
    {
        var tb = new TableBlock(2, 2);
        tb.MergeCells(0, 0, 0, 1);
        Assert.True(tb.IsCovered(0, 1));
        tb.UnmergeCell(0, 0);
        Assert.False(tb.IsCovered(0, 1));
        Assert.Equal((1, 1), tb.SpanOf(0, 0));
    }

    private static string CellText(TableBlock tb, int r, int c)
        => string.Concat(System.Linq.Enumerable.Select(
            System.Linq.Enumerable.OfType<Run>(tb.Cells[r][c].Para.Inlines), x => x.Text));

    [Fact]
    public void Extract_TwoRowsOneColumn_KeepsBothCells()
    {
        // Repro of the reported bug: a 2×1 table, drag-select both cells top→bottom, copy → the extracted
        // sub-table must keep BOTH rows' content (an earlier version collapsed to one cell).
        var tb = new TableBlock(2, 1);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "TOP";
        ((Run)tb.Cells[1][0].Para.Inlines[0]).Text = "BOTTOM";

        var sub = tb.Extract(0, 0, 1, 0);

        Assert.Equal(2, sub.Rows);
        Assert.Equal(1, sub.Columns);
        Assert.Equal("TOP", CellText(sub, 0, 0));
        Assert.Equal("BOTTOM", CellText(sub, 1, 0));
    }

    [Fact]
    public void Extract_MultiParagraphCells_KeepsEachCellContent()
    {
        // Cells holding two paragraphs each (as in the demo default text pasted into two stacked cells).
        var tb = new TableBlock(2, 1);
        foreach (int r in new[] { 0, 1 })
        {
            tb.Cells[r][0].Blocks.Clear();
            tb.Cells[r][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"line1-r{r}" } } });
            tb.Cells[r][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"line2-r{r}" } } });
        }

        var sub = tb.Extract(0, 0, 1, 0);

        Assert.Equal(2, sub.Rows);
        Assert.Equal(2, sub.Cells[0][0].Blocks.Count);
        Assert.Equal(2, sub.Cells[1][0].Blocks.Count);
        Assert.Equal("line1-r0", ((Run)((Paragraph)sub.Cells[0][0].Blocks[0]).Inlines[0]).Text);
        Assert.Equal("line1-r1", ((Run)((Paragraph)sub.Cells[1][0].Blocks[0]).Inlines[0]).Text);
    }

    [Fact]
    public void Extract_PreservesMergeInsideRectangle()
    {
        var tb = new TableBlock(3, 3);
        tb.MergeCells(0, 0, 1, 1); // 2×2 merge anchored at (0,0)
        var sub = tb.Extract(0, 0, 1, 1);
        Assert.Equal(2, sub.Rows);
        Assert.Equal(2, sub.Columns);
        var (cs, rs) = sub.SpanOf(0, 0);
        Assert.Equal(2, cs);
        Assert.Equal(2, rs);
        Assert.True(sub.IsCovered(1, 1));
    }

    [Fact]
    public void Clone_IsDeep()
    {
        var tb = new TableBlock(2, 2);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "x";
        var clone = (TableBlock)tb.Clone();
        ((Run)clone.Cells[0][0].Para.Inlines[0]).Text = "y";
        Assert.Equal("x", ((Run)tb.Cells[0][0].Para.Inlines[0]).Text);
        Assert.Equal("y", ((Run)clone.Cells[0][0].Para.Inlines[0]).Text);
    }
}
