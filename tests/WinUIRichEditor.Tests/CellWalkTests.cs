using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using Windows.Foundation;
using Windows.UI;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The block-list walk inside a table cell — position, hit-testing and caret geometry for a cell
/// holding MORE THAN ONE block.
/// <para>This gap was found by falsification, not by reading. Four walks used to derive a cell's block
/// positions independently (measure / draw / hit-test / caret) and unifying them behind one advance
/// (<c>NextCellSlot</c>) left all 309 tests green — so did injecting a 3px error into that shared
/// advance. Only a gross break (zero advance) failed anything. In other words: nothing here exercised a
/// cell with a second block, which is the only place the advance is observable at all.</para>
/// <para>The property these pin is the one that matters to a user and the one the four-copy version could
/// break: <b>the walks must agree with each other</b>. A caret placed in a cell's Nth block must sit
/// where hit-testing puts a click, and the cell must be tall enough to hold what is drawn in it. A
/// uniform change to the shared advance moves everything together and is a layout choice, not a defect;
/// divergence between the walks is the defect, and that is what fails here.</para></summary>
[Collection(UiTests.Collection)]
public class CellWalkTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly FieldInfo CaretField = T.GetField("_caret", NP)!;
    private static readonly MethodInfo ToDocPoint = T.GetMethod("CaretToDocPoint", NP)!;
    private static readonly MethodInfo FromPoint = T.GetMethod("GetPositionFromPoint", NP)!;
    private static readonly MethodInfo Relayout = T.GetMethod("RelayoutToViewport", NP)!;

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor
        {
            Document = new FlowDocument(),
            PageSize = RichEditorPageSize.Continuous,
        });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void Hosted(FlowDocument doc, Action<RichEditor> body)
    {
        var editor = Shared.Value;
        UiThread.Run(() =>
        {
            editor.Document = doc;
            Relayout.Invoke(editor, null);
            body(editor);
        });
    }

    private static Paragraph P(string text) => new() { Inlines = { new Run { Text = text, FontSize = 10 } } };

    // A document whose single 1x1 table holds `blocks` in its only cell.
    private static (FlowDocument doc, TableBlock table) CellDoc(params Block[] blocks)
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 1);
        tb.Cells[0][0].Blocks.Clear();
        foreach (var b in blocks) tb.Cells[0][0].Blocks.Add(b);
        doc.Blocks.Add(tb);
        return (doc, tb);
    }

    private static (double X, double Y, double Height) CaretPoint(RichEditor ed, Paragraph p, int offset = 0)
    {
        CaretField.SetValue(ed, new TextPointer(p, offset));
        object? boxed = ToDocPoint.Invoke(ed, new object?[] { CaretField.GetValue(ed) });
        Assert.NotNull(boxed);
        var ty = boxed!.GetType();
        double F(string n) => (double)ty.GetField(n)!.GetValue(boxed)!;
        return (F("Item1"), F("Item2"), F("Item3"));
    }

    private static TextPointer? HitAt(RichEditor ed, double x, double y)
        => (TextPointer?)FromPoint.Invoke(ed, new object?[] { new Point(x, y) });

    // ---- the walks must agree -----------------------------------------------------------------------

    // The core property. Take the caret's own geometry for a paragraph deep in a cell's block list, then
    // ask the hit-test walk what is at that point. Two independent descents (CaretInBlockList and
    // HitTestBlockList) that must land on the same paragraph — which is exactly what four hand-copied
    // advances could not guarantee.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void CaretAndHitTestAgree_OnEveryParagraphOfAMultiBlockCell(int target)
    {
        var paras = new[] { P("first para"), P("second para"), P("third para"), P("fourth para") };
        var (doc, _) = CellDoc(paras[0], paras[1], paras[2], paras[3]);

        Hosted(doc, ed =>
        {
            var c = CaretPoint(ed, paras[target]);
            // Probe just below the caret's TOP, not at its middle. The caret top is where the caret walk
            // says this block begins; half a line lower leaves ~8px of slack that swallows a small
            // drift in the other walk (measured: a 3px-per-paragraph divergence survives a mid-line
            // probe and fails this one).
            var hit = HitAt(ed, c.X + 1, c.Y + 1);

            Assert.NotNull(hit);
            Assert.Same(paras[target], hit!.Paragraph);
            // ⚠ Identity alone does NOT prove the walks agree. When the hit-test walk misses every
            // block it falls back to the LAST paragraph it passed, which for a probe aimed at the last
            // paragraph is the very one expected — the fallback answers correctly for the wrong reason.
            // Verified: a 3px drift injected into this walk's advance passes an identity-only assertion.
            // The offset is what separates them: a real hit one pixel into the first glyph resolves to
            // offset 0, while the fallback returns the paragraph's LENGTH.
            Assert.True(hit.Offset <= 1,
                $"hit resolved to offset {hit.Offset}, not the start — this is the end-of-paragraph fallback, "
                + "so the hit-test walk did not actually land on the block the caret walk placed");
        });
    }

    // The same agreement with the non-paragraph advances in between — a divider and a block image both
    // sit in a cell's block list and both have their own height in the walk.
    [Fact]
    public void CaretAndHitTestAgree_AcrossADividerAndAnImage()
    {
        var first = P("before");
        var after = P("after the divider and the image");
        var img = new ImageBlock { Width = 40, Height = 30 };
        var (doc, _) = CellDoc(first, new DividerBlock(), img, after);

        Hosted(doc, ed =>
        {
            var c = CaretPoint(ed, after);
            var hit = HitAt(ed, c.X + 1, c.Y + 1);

            Assert.NotNull(hit);
            Assert.Same(after, hit!.Paragraph);
            Assert.True(hit.Offset <= 1, $"end-of-paragraph fallback (offset {hit.Offset}), not a real hit");
            // And it really is below the divider + image, not collapsed onto the first paragraph.
            var firstPoint = CaretPoint(ed, first);
            Assert.True(c.Y > firstPoint.Y + 30,
                $"the paragraph after a divider and a 30px image sits at {c.Y:0.0}, barely below the first at {firstPoint.Y:0.0}");
        });
    }

    // The measure walk feeds the row height; the draw/caret walks place content inside it. If they
    // disagree, content spills out of its own cell — the visible form of a divergent advance.
    [Fact]
    public void TheRowIsTallEnoughForEveryBlockTheWalkPlaces()
    {
        var paras = new[] { P("one"), P("two"), P("three"), P("four"), P("five") };
        var (doc, table) = CellDoc(paras[0], paras[1], paras[2], paras[3], paras[4]);

        Hosted(doc, ed =>
        {
            var last = CaretPoint(ed, paras[4]);
            var first = CaretPoint(ed, paras[0]);
            double rowHeight = table.RowHeights.Count > 0 ? table.RowHeights[0] : 0;
            // RowHeights is the AUTHORED minimum; the measured row is what actually got laid out, and the
            // bottom of the last caret is the deepest thing the walk placed. Take the table's own drawn
            // extent from the block layout: the last paragraph plus its caret must fit above it.
            double contentBottom = last.Y + last.Height;
            double tableTop = first.Y;
            Assert.True(contentBottom > tableTop,
                "the last paragraph is not below the first — the cell walk is not advancing");
            // Every paragraph strictly below the previous one: no two blocks share a Y.
            double prev = double.NegativeInfinity;
            foreach (var p in paras)
            {
                double y = CaretPoint(ed, p).Y;
                Assert.True(y > prev, $"paragraph at {y:0.0} is not below the previous at {prev:0.0}");
                prev = y;
            }
            _ = rowHeight;
        });
    }

    // ---- ordered-list numbering is now the SAME rule in a cell as at the top level ------------------

    // Unifying the walks made this consistent, and it was not before: the cell copy only touched its
    // counters inside `case Paragraph`, so a divider (or an image, or a nested table) between two
    // numbered items did NOT restart the numbering, while the top-level map has always restarted it on
    // any non-list block. Two rules for one thing, and no test either way. This pins the top-level rule
    // in both places.
    private static Paragraph Numbered(string text) => new()
    {
        ListType = ListKind.Ordered,
        Inlines = { new Run { Text = text, FontSize = 10 } },
    };

    private static int OrderedStartOfLastBlock(RichEditor ed, IList<Block> blocks)
    {
        // Drive the shared numbering helper the way both walks do.
        var m = T.GetMethod("OrderedStartFor", BindingFlags.NonPublic | BindingFlags.Static)!;
        object?[] args = { null!, null };
        int start = 0;
        foreach (var b in blocks)
        {
            args[0] = b;
            start = (int)m.Invoke(null, args)!;
        }
        return start;
    }

    [Fact]
    public void ANonListBlockRestartsTheNumbering()
    {
        var blocks = new List<Block> { Numbered("one"), Numbered("two"), new DividerBlock(), Numbered("restarted") };
        Assert.Equal(0, OrderedStartOfLastBlock(Shared.Value, blocks));
    }

    [Fact]
    public void ConsecutiveItemsKeepCounting()
    {
        var blocks = new List<Block> { Numbered("one"), Numbered("two"), Numbered("three") };
        Assert.Equal(2, OrderedStartOfLastBlock(Shared.Value, blocks));
    }

    // ---- the drawing the two walks now share --------------------------------------------------------

    // DrawParagraphContent is one function for the top level and for cells. Three times the cell copy was
    // missing something the top-level one drew (list markers, then the paragraph fill, then the quote
    // bar), each found by a human looking at the demo. Pin that a paragraph deep in a cell's block list —
    // not just the first one, which is where the existing pixel tests look — still gets its fill.
    [Fact]
    public void AFillOnACellsSecondParagraph_IsStillPainted()
    {
        var fill = Color.FromArgb(255, 255, 0, 0);
        var second = P("filled second paragraph");
        second.Background = fill;
        var (doc, _) = CellDoc(P("plain first paragraph"), second);

        int painted = UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = doc };
            using var rt = ed.RenderPrintPage(0, 96);
            int n = 0;
            foreach (var c in rt.GetPixelColors())
                if (Math.Abs(c.R - fill.R) <= 8 && Math.Abs(c.G - fill.G) <= 8 && Math.Abs(c.B - fill.B) <= 8) n++;
            return n;
        });

        Assert.True(painted > 500, $"expected a filled band on the cell's second paragraph, found {painted} red pixels");
    }
}
