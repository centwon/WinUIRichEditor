using System.Collections.Generic;
using System.Linq;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The document's canonical block order (<see cref="BlockWalk"/>) — headless, no runtime needed.
/// <para>Four walks used to implement this order separately: the control's <c>ParagraphsInBlocks</c>,
/// <c>TextRange.CollectParagraphs</c>, <c>TextPointer.CompareTo</c> and <c>UndoManager.WalkParagraphs</c>.
/// Three of them carried a comment saying they mirror one of the others, which is four copies held in
/// agreement by nothing but a request to the next editor — and TextPointer's comment records that the
/// agreement had already failed once.</para>
/// <para>Both rules below were found by falsification, not by reading: with the order unified, dropping
/// the inline-table descent failed exactly ONE existing test, and walking a merged table's COVERED cells
/// instead of its logical anchors failed <b>none</b> of the 318 — even though three comments call that
/// rule load-bearing ("or the index-based range loops disagree with the control on merged tables").</para></summary>
public class BlockWalkTests
{
    private static Paragraph P(string text) => new() { Inlines = { new Run { Text = text } } };

    private static List<string> TextsIn(IEnumerable<Block> blocks)
        => BlockWalk.Paragraphs(blocks)
            .Select(p => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)))
            .ToList();

    // A 1x2 table whose two cells are horizontally merged.
    //
    // ⚠ MergeCells MOVES the covered cell's content into the anchor and leaves a fresh empty paragraph
    // behind (see TableBlock.MergeCells). So the paragraph to assert about is the one that exists AFTER
    // the merge — asserting the pre-merge object is not in the order is vacuously true, because that
    // object is no longer in the document at all. The first version of this test did exactly that and
    // passed under a falsification that walked every covered cell.
    private static (FlowDocument doc, Paragraph anchorPara, Paragraph coveredPara) MergedTableDoc()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 2);

        var anchor = P("anchor cell");
        tb.Cells[0][0].Blocks.Clear();
        tb.Cells[0][0].Blocks.Add(anchor);

        tb.Cells[0][1].Blocks.Clear();
        tb.Cells[0][1].Blocks.Add(P("covered cell"));

        tb.MergeCells(0, 0, 0, 1);
        doc.Blocks.Add(tb);

        // Still reachable through tb.Cells — just not through LogicalCells, which is the whole point.
        var covered = (Paragraph)tb.Cells[0][1].Blocks[0];
        return (doc, anchor, covered);
    }

    [Fact]
    public void ACoveredCellsContentIsNotInTheOrder()
    {
        var (doc, anchor, covered) = MergedTableDoc();
        var table = (TableBlock)doc.Blocks[0];
        Assert.True(table.IsCovered(0, 1), "the fixture did not actually merge");
        Assert.Same(covered, table.Cells[0][1].Blocks[0]); // it really is in the grid

        var paragraphs = BlockWalk.Paragraphs(doc.Blocks).ToList();

        Assert.Contains(anchor, paragraphs);
        Assert.DoesNotContain(covered, paragraphs);
    }

    // The other half of the same rule: positions. Every block takes exactly one index, and a covered cell
    // taking one would shift everything after the table.
    [Fact]
    public void ACoveredCellDoesNotConsumeAnIndex()
    {
        var (doc, _, _) = MergedTableDoc();
        doc.Blocks.Add(P("after the table"));

        var order = BlockWalk.DocumentOrder(doc.Blocks).ToList();

        // table, its one logical cell's paragraph, then the trailing paragraph — three, not four.
        Assert.Equal(3, order.Count);
        Assert.IsType<TableBlock>(order[0]);
        Assert.Equal("after the table", ((Paragraph)order[2]).Inlines.OfType<Run>().Single().Text);
    }

    // The table itself takes a position BEFORE its cells. TextPointer comparisons and undo's caret
    // restoration both index off this walk, and undo restores a caret by its global index across two
    // different document instances — so the numbering is a contract, not an implementation detail.
    [Fact]
    public void ATableTakesItsOwnPositionBeforeItsCells()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 1);
        tb.Cells[0][0].Blocks.Clear();
        tb.Cells[0][0].Blocks.Add(P("in the cell"));
        doc.Blocks.Add(P("before"));
        doc.Blocks.Add(tb);
        doc.Blocks.Add(P("after"));

        var order = BlockWalk.DocumentOrder(doc.Blocks).ToList();

        Assert.Equal(4, order.Count);
        Assert.IsType<Paragraph>(order[0]);
        Assert.IsType<TableBlock>(order[1]);   // the table, then what it holds
        Assert.IsType<Paragraph>(order[2]);
        Assert.Equal("in the cell", ((Paragraph)order[2]).Inlines.OfType<Run>().Single().Text);
    }

    // A paragraph comes before the cell content of an inline table it hosts: the paragraph is where the
    // caret is, the cells hang off it. Dropping this descent is the failure TextPointer's comment records
    // (a pointer inside an inline table kept index -1, so selections spanning one compared as reversed).
    [Fact]
    public void AHostParagraphPrecedesItsInlineTablesCells()
    {
        var doc = new FlowDocument();
        var host = P("host ");
        var inner = new TableBlock(1, 1);
        inner.Cells[0][0].Blocks.Clear();
        inner.Cells[0][0].Blocks.Add(P("inline cell"));
        host.Inlines.Add(new InlineTable { Table = inner });
        doc.Blocks.Add(host);
        doc.Blocks.Add(P("last"));

        Assert.Equal(new[] { "host ", "inline cell", "last" }, TextsIn(doc.Blocks));
    }

    // Nesting closes at any depth, and cells are row-major.
    [Fact]
    public void NestedTablesAreWalkedRowMajorAtAnyDepth()
    {
        var doc = new FlowDocument();
        var outer = new TableBlock(1, 2);
        outer.Cells[0][0].Blocks.Clear();
        outer.Cells[0][0].Blocks.Add(P("c00"));

        var nested = new TableBlock(1, 2);
        nested.Cells[0][0].Blocks.Clear();
        nested.Cells[0][0].Blocks.Add(P("n00"));
        nested.Cells[0][1].Blocks.Clear();
        nested.Cells[0][1].Blocks.Add(P("n01"));

        outer.Cells[0][1].Blocks.Clear();
        outer.Cells[0][1].Blocks.Add(nested);
        doc.Blocks.Add(outer);

        Assert.Equal(new[] { "c00", "n00", "n01" }, TextsIn(doc.Blocks));
    }

    [Fact]
    public void HoldsFindsAParagraphAtAnyDepth()
    {
        var doc = new FlowDocument();
        var outer = new TableBlock(1, 1);
        var nested = new TableBlock(1, 1);
        var deep = P("deep");
        nested.Cells[0][0].Blocks.Clear();
        nested.Cells[0][0].Blocks.Add(deep);
        outer.Cells[0][0].Blocks.Clear();
        outer.Cells[0][0].Blocks.Add(nested);
        doc.Blocks.Add(outer);

        Assert.True(BlockWalk.Holds(outer, deep));
        Assert.True(BlockWalk.Holds(deep, deep));
        Assert.False(BlockWalk.Holds(outer, P("not in there")));
    }
}
