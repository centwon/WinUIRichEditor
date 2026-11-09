using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Selection and editing rules made the same as upstream's (2026-11-09).</summary>
[Collection(UiTests.Collection)]
public class SelectionEditRulesTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static RichEditor Editor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        return new RichEditor { Document = doc };
    }

    private static void Caret(RichEditor ed, Paragraph p, int offset) => Select(ed, p, offset, p, offset);

    // A drag from (a, ao) to (b, bo): the caret sits at the drag's end.
    private static void Select(RichEditor ed, Paragraph a, int ao, Paragraph b, int bo)
    {
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(a, ao));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(b, bo));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(b, bo));
    }

    private static void Call(RichEditor ed, string name, params object?[] args)
    {
        try { T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private static string TextOf(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // ---- Tab over paragraphs ------------------------------------------------------------------------

    [Fact]
    public void Tab_OverSeveralParagraphs_IndentsEachAndKeepsTheText() => UiThread.Run(() =>
    {
        // Only the caret's list item moved, and without a list the key typed four spaces over the selection. Now each
        // paragraph steps in: a list item a level, any other an indent step; Shift+Tab steps them back.
        var a = P("first");
        var b = P("second");
        var ed = Editor(a, b);
        a.ListType = ListKind.Bullet;
        Select(ed, a, 1, b, 3);
        Call(ed, "HandleTab", false);
        Assert.Equal("first", TextOf(a));
        Assert.Equal("second", TextOf(b));
        Assert.Equal(1, a.ListLevel);
        Assert.Equal(0, a.Indent);
        Assert.Equal(20, b.Indent);

        Call(ed, "HandleTab", true);
        Assert.Equal(0, a.ListLevel);
        Assert.Equal(0, b.Indent);
    });

    [Fact]
    public void ShiftTab_TakesBackTheSpacesTabTyped_ElseOutdents() => UiThread.Run(() =>
    {
        // Shift+Tab outside a list did nothing.
        var a = P("ab");
        var ed = Editor(a);
        Caret(ed, a, 2);
        Call(ed, "HandleTab", false);
        Assert.Equal("ab    ", TextOf(a));
        Call(ed, "HandleTab", true);
        Assert.Equal("ab", TextOf(a));

        a.Indent = 40;
        Call(ed, "HandleTab", true);
        Assert.Equal(20, a.Indent);
    });

    // ---- inserting keeps what is selected ----------------------------------------------------------------

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static string Shape(RichEditor ed)
        => string.Join(",", ed.Document!.Blocks.Select(b => b is Paragraph bp ? (TextOf(bp).Length > 0 ? TextOf(bp) : "P") : b.GetType().Name));

    private static int OffsetOf<TInline>(Paragraph p) where TInline : Inline
    {
        int off = 0;
        foreach (var inl in p.Inlines) { if (inl is TInline) return off; off += inl is Run r ? (r.Text?.Length ?? 0) : 1; }
        return -1;
    }

    [Fact]
    public void InsertDivider_OverABackwardsSelection_GoesAtItsEnd() => UiThread.Run(() =>
    {
        // The block went in at the caret — the START of a selection made backwards — and the selection stood.
        var p = P("hello world");
        var ed = Editor(p);
        Select(ed, p, 8, p, 3);
        ed.InsertDivider();
        Assert.Equal("hello wo,DividerBlock,rld", Shape(ed));
    });

    [Fact]
    public void InsertTable_WithAPictureSelected_GoesRightAfterIt() => UiThread.Run(() =>
    {
        // With a picture selected the block went in where the caret had been before the click.
        var above = P("above");
        var pic = new ImageBlock { Width = 50, Height = 50 };
        pic.SetImageData(TinyPng, "image/png");
        var ed = Editor(above, pic, P("below"));
        Caret(ed, above, 0);
        T.GetField("_selectedBlock", NP)!.SetValue(ed, pic);
        ed.InsertTable(2, 2);
        Assert.Equal("above,ImageBlock,P,TableBlock,below", Shape(ed));
    });

    [Fact]
    public void InsertInlineTable_OverABackwardsSelection_GoesAtItsEnd_AndKeepsTheText() => UiThread.Run(() =>
    {
        var p = P("hello world");
        var ed = Editor(p);
        Select(ed, p, 8, p, 3);
        ed.InsertInlineTable(1, 1);
        Assert.Equal("hello world", TextOf(p));
        Assert.Equal(8, OffsetOf<InlineTable>(p));
        Assert.False(ed.HasSelection);
    });

    [Fact]
    public void InsertInlineImage_OverABackwardsSelection_GoesAtItsEnd_AndKeepsTheText() => UiThread.Run(() =>
    {
        var p = P("hello world");
        var ed = Editor(p);
        Select(ed, p, 8, p, 3);
        ed.InsertInlineImage(TinyPng);
        Assert.Equal("hello world", TextOf(p));
        Assert.Equal(8, OffsetOf<InlineImage>(p));
        Assert.False(ed.HasSelection);
    });

    // ---- several lines of plain text pasted ----------------------------------------------------------------

    [Fact]
    public void PlainLines_IntoAListItem_AreListItems_WithTheCaretsCharacterFormat() => UiThread.Run(() =>
    {
        // They were plain body paragraphs: the list and the bold were lost on every line but the first.
        var p = new Paragraph { ListType = ListKind.Bullet };
        p.Inlines.Add(new Run { Text = "item one", FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 } });
        var ed = Editor(p);
        Caret(ed, p, 4);
        Call(ed, "InsertPlainTextBlock", "x\ny\nz");
        var paras = ed.Document!.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(new[] { "itemx", "y", "z one" }, paras.Select(TextOf));
        Assert.All(paras, x => Assert.Equal(ListKind.Bullet, x.ListType));
        Assert.All(paras.SelectMany(x => x.Inlines.OfType<Run>()), r => Assert.Equal(700, r.FontWeight.Weight));
    });

    // ---- cutting a cell block --------------------------------------------------------------------------

    private static TableBlock Grid(int rows, int cols)
    {
        var t = new TableBlock(rows, cols);
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                t.Cells[r][c].Blocks.Clear();
                t.Cells[r][c].Blocks.Add(P(((char)('a' + r * cols + c)).ToString()));
            }
        return t;
    }

    private static string CellText(TableBlock t, int r, int c) => TextOf(t.Cells[r][c].Para);

    // Cut used to empty the cells of any block and leave the grid standing: whole rows, whole columns and the whole table
    // now go with the cut, as in HWP; any other rectangle is still emptied.
    [Theory]
    [InlineData(1, 0, 1, 2, "rows")]
    [InlineData(0, 2, 2, 2, "columns")]
    [InlineData(0, 0, 2, 2, "table")]
    [InlineData(0, 0, 1, 1, "contents")]
    public void CuttingACellBlock_TakesWholeRowsColumnsOrTheTable(int r0, int c0, int r1, int c1, string kind) => UiThread.Run(() =>
    {
        var t = Grid(3, 3);
        var ed = Editor(P("before"), t, P("after"));
        Select(ed, t.Cells[r0][c0].Para, 0, t.Cells[r1][c1].Para, 1);
        ed.CutAsync().GetAwaiter().GetResult();
        switch (kind)
        {
            case "rows":
                Assert.Equal(2, t.Rows);
                Assert.Equal("g", CellText(t, 1, 0));
                break;
            case "columns":
                Assert.Equal(2, t.Columns);
                Assert.Equal("b", CellText(t, 0, 1));
                break;
            case "table":
                Assert.DoesNotContain(ed.Document!.Blocks, b => b is TableBlock);
                break;
            default:
                Assert.Equal((3, 3), (t.Rows, t.Columns));
                Assert.Equal("", CellText(t, 0, 0));
                Assert.Equal("", CellText(t, 1, 1));
                Assert.Equal("c", CellText(t, 0, 2));
                break;
        }
    });

    // ---- "treat as character" inside a cell -----------------------------------------------------------

    [Fact]
    public void ATableInACell_ConvertsToInline_WithinItsCell_AndBack() => UiThread.Run(() =>
    {
        var outer = new TableBlock(1, 1);
        var nested = new TableBlock(1, 1);
        var cell = outer.Cells[0][0];
        cell.Blocks.Clear();
        var a = P("cell");
        cell.Blocks.Add(a);
        cell.Blocks.Add(nested);
        cell.Blocks.Add(P(""));
        var ed = Editor(P("top"), outer, P("end"));
        Call(ed, "ConvertTableBlockToInline", nested);
        Assert.DoesNotContain(cell.Blocks, x => x is TableBlock);
        var it = Assert.Single(a.Inlines.OfType<InlineTable>());

        Call(ed, "ConvertInlineTableToBlock", a, it);
        Assert.DoesNotContain(a.Inlines, i => i is InlineTable);
        Assert.Single(cell.Blocks.OfType<TableBlock>());
    });

    // ---- list toggle: one rule ----------------------------------------------------------------------

    [Fact]
    public void ListToggle_OnAMixedSelection_TurnsEveryParagraphOn() => UiThread.Run(() =>
    {
        // Outside a table the caret's paragraph used to decide: with the caret in the bulleted one, the toggle took the
        // bullets OFF both. A mixed selection turns the list on everywhere; only an all-bulleted selection turns it off.
        var a = P("first");
        var b = P("second");
        var ed = Editor(a, b);
        a.ListType = ListKind.Bullet;
        Select(ed, b, 2, a, 2);
        ed.ToggleBullet();
        Assert.All(ed.Document!.Blocks.OfType<Paragraph>(), p => Assert.Equal(ListKind.Bullet, p.ListType));

        ed.ToggleBullet();
        Assert.All(ed.Document.Blocks.OfType<Paragraph>(), p => Assert.Equal(ListKind.None, p.ListType));
    });

    [Fact]
    public void ListToggle_AcrossATable_ListsTheCellParagraphsToo() => UiThread.Run(() =>
    {
        // The outside-a-table path collected only top-level paragraphs, so the cells a selection crossed stayed unlisted.
        var a = P("before");
        var tb = new TableBlock(1, 1);
        var cellPara = P("cell");
        tb.Cells[0][0].Blocks.Clear();
        tb.Cells[0][0].Blocks.Add(cellPara);
        var b = P("after");
        var ed = Editor(a, tb, b);
        Select(ed, a, 0, b, 2);
        ed.ToggleBullet();
        Assert.Equal(ListKind.Bullet, cellPara.ListType);
        Assert.All(ed.Document!.Blocks.OfType<Paragraph>(), p => Assert.Equal(ListKind.Bullet, p.ListType));
    });
}
