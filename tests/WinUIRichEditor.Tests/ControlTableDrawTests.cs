using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>"Draw table" mode (RichEditor.TableDraw.cs) — pick rows × columns, then drag from the caret to size
/// the table. No test had referenced the file (audit 2026-09-19). A press is <c>TableDrawPressAt</c>, a move
/// <c>TableDrawPointerMoved</c>, a release <c>TableDrawReleaseAt</c> — the handlers minus pointer capture.</summary>
[Collection(UiTests.Collection)]
public class ControlTableDrawTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void Hosted(Action<RichEditor> body)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            try { body(ed); }
            finally { Call(ed, "CancelTableDraw"); }
        });
    }

    private static object? Call(RichEditor ed, string name, params object?[] args)
    {
        try { return T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static void Draw(RichEditor ed)
    {
        Call(ed, "RelayoutToViewport");
        using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 2000, 96);
        using var ds = rt.CreateDrawingSession();
        Call(ed, "DrawDocument", ds, new Rect(0, 0, 1000, 2000));
    }

    private static void Load(RichEditor ed, params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        ed.LoadJson(DocumentSerializer.Serialize(doc));
        Draw(ed);
    }

    private static Paragraph Para(string text) => new() { Inlines = { new Run { Text = text } } };

    private static bool Armed(RichEditor ed) => T.GetField("_pendingTableDraw", NP)!.GetValue(ed) != null;

    private static Point CaretPoint(RichEditor ed)
    {
        object boxed = Call(ed, "CaretToDocPoint", T.GetField("_caret", NP)!.GetValue(ed))!;
        var ty = boxed.GetType();
        return new Point((double)ty.GetField("Item1")!.GetValue(boxed)!, (double)ty.GetField("Item2")!.GetValue(boxed)!);
    }

    // Press at the caret, drag by (dx, dy), release.
    private static void DrawTable(RichEditor ed, double dx, double dy)
    {
        var at = CaretPoint(ed);
        Assert.True(ed.TableDrawPressAt(at));
        Call(ed, "TableDrawPointerMoved", new Point(at.X + dx, at.Y + dy));
        Assert.True(ed.TableDrawReleaseAt());
    }

    private static int Tables(RichEditor ed) => ed.Document!.Blocks.OfType<TableBlock>().Count();

    // Control: the mode does what it says — one table, sized to the drag, one undo step.
    [Fact]
    public void ADrag_InsertsATableSizedToIt_AsOneUndoStep()
    {
        Hosted(ed =>
        {
            // Enough below the caret that the drag is not clamped to the document's height.
            Load(ed, Para("before"), Para("a"), Para("b"), Para("c"), Para("d"), Para("e"), Para("after"));
            ed.BeginTableDraw(2, 3);
            DrawTable(ed, 300, 80);
            var tb = ed.Document!.Blocks.OfType<TableBlock>().Single();
            Assert.Equal(3, tb.ColumnWidths.Count);
            Assert.All(tb.ColumnWidths, w => Assert.Equal(100, w, 1));
            Assert.All(tb.RowHeights, h => Assert.Equal(40, h, 1));
            Assert.False(Armed(ed));
            ed.Undo();
            Assert.Equal(0, Tables(ed));
        });
    }

    // Armed, then a document is opened: the pick belonged to the document that is gone. Left armed, the first
    // click in the new one inserted a table — an edit nobody asked for, in a file just opened.
    [Fact]
    public void OpeningADocument_DisarmsTheMode()
    {
        Hosted(ed =>
        {
            Load(ed, Para("old"));
            ed.BeginTableDraw(2, 2);
            Load(ed, Para("new file"), Para("second"));
            Assert.False(Armed(ed), "still armed after a document was opened");
            Assert.False(ed.TableDrawPressAt(CaretPoint(ed)));
            Assert.Equal(0, Tables(ed));
            Assert.False(ed.IsModified);
        });
    }

    // Undo swaps in a snapshot through the same path; the armed pick was made on the state being left.
    [Fact]
    public void Undo_DisarmsTheMode()
    {
        Hosted(ed =>
        {
            Load(ed, Para("text"));
            ed.InsertText("x");
            ed.BeginTableDraw(2, 2);
            ed.Undo();
            Assert.False(Armed(ed), "still armed after an undo");
        });
    }

    // A lost pointer capture (another window takes it, a touch cancel) is not a release: the drag is abandoned,
    // not completed with whatever rectangle it had reached.
    [Fact]
    public void LosingThePointerMidDrag_AbandonsIt_WithoutInserting()
    {
        Hosted(ed =>
        {
            Load(ed, Para("before"), Para("after"));
            ed.BeginTableDraw(2, 2);
            var at = CaretPoint(ed);
            Assert.True(ed.TableDrawPressAt(at));
            Call(ed, "TableDrawPointerMoved", new Point(at.X + 200, at.Y + 60));
            Call(ed, "EndPointerDrags"); // what PointerCaptureLost runs
            Assert.False(Armed(ed), "still armed after the capture was lost");
            Assert.False(ed.TableDrawReleaseAt());
            Assert.Equal(0, Tables(ed));
        });
    }

    // Drawn from a caret inside a table cell, the new table nests in that cell. The drag is clamped to the
    // DOCUMENT width, so a wide drag made a nested table wider than the cell holding it — drawn over the
    // neighbouring cells, the same defect the column drag had (ControlTableResizeTests).
    [Fact]
    public void DrawnInACell_TheTableFitsTheCell()
    {
        Hosted(ed =>
        {
            var outer = new TableBlock(1, 2);
            outer.ColumnWidths[0] = 200; outer.ColumnWidths[1] = 200;
            Load(ed, Para("top"), outer, Para("end"));
            var cellPara = ed.Document!.Blocks.OfType<TableBlock>().Single().Cells[0][0].Blocks.OfType<Paragraph>().First();
            T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(cellPara, 0));
            Draw(ed);

            ed.BeginTableDraw(1, 2);
            DrawTable(ed, 600, 40);

            var cell = ed.Document!.Blocks.OfType<TableBlock>().Single().Cells[0][0];
            var nested = cell.Blocks.OfType<TableBlock>().Single();
            double total = nested.ColumnWidths.Sum();
            Assert.True(total <= 200, $"a table drawn in a 200px cell is {total}px wide");
        });
    }

    // In page view the rubber band showed away from the pointer — off by the desk and page-margin offset — while the
    // table it inserted landed under the pointer (2026-11-08): the content is drawn page by page, translated
    // onto its paper, and the overlay was not. Drawn now, the band's middle is under the middle of the drag on screen.
    [Fact]
    public void InPageView_TheRubberBandIsDrawnUnderThePointer()
    {
        Hosted(ed =>
        {
            ed.ViewMode = RichEditorViewMode.Page;
            ed.PageSize = RichEditorPageSize.A4;
            try
            {
                Load(ed, Para("first line"), Para("second line"));
                Call(ed, "RelayoutToViewport");
                // Pressed on the first line (the drag is clamped to the content's height), dragged 60 × 20.
                var docStart = new Point(40, 4);
                var press = (Point)Call(ed, "DocToView", docStart)!; // where that is on screen: on the paper, at 100 %
                ed.BeginTableDraw(2, 2);
                Assert.True(ed.TableDrawPressAt(docStart));
                Call(ed, "TableDrawPointerMoved", new Point(docStart.X + 60, docStart.Y + 20));

                using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1000, 1400, 96);
                using (var ds = rt.CreateDrawingSession()) Call(ed, "DrawDocument", ds, new Rect(0, 0, 1000, 1400));
                var px = rt.GetPixelBytes();
                // BGRA. The band's fill is translucent blue: blue clearly above red, which neither paper, desk nor text is.
                bool Tinted(double x, double y)
                {
                    int o = ((int)y * (int)rt.SizeInPixels.Width + (int)x) * 4;
                    return px[o] - px[o + 2] > 15;
                }
                Assert.True(press.X > docStart.X + 60 && press.Y > docStart.Y + 20, "page view should move the content on screen");
                Assert.True(Tinted(press.X + 30, press.Y + 10), "no rubber band under the middle of the drag");
                Assert.False(Tinted(docStart.X + 30, docStart.Y + 10), "the band is drawn at its document position, not its page's");
            }
            finally
            {
                ed.ViewMode = RichEditorViewMode.Continuous;
                ed.PageSize = RichEditorPageSize.Continuous;
            }
        });
    }
    // Pressed in the empty area below the last line: the band starts at the content's bottom, where the table goes (the
    // caret lands on the last line), and the drag sizes it downwards. The far corner was clamped to the content's height
    // as well, so at the end of a document no drag made a taller table (2026-11-08).
    [Fact]
    public void PressedBelowTheLastLine_TheTableGoesAfterIt_SizedByTheDrag()
    {
        Hosted(ed =>
        {
            Load(ed, Para("a"), Para("b"));
            double bottom = (double)T.GetField("_measuredHeight", NP)!.GetValue(ed)!;
            ed.BeginTableDraw(2, 2);
            Assert.True(ed.TableDrawPressAt(new Point(40, bottom + 150)));
            Assert.Equal(bottom, ((Point)T.GetField("_tableDrawStart", NP)!.GetValue(ed)!).Y, 1);
            Call(ed, "TableDrawPointerMoved", new Point(160, bottom + 200));
            Assert.True(ed.TableDrawReleaseAt());

            var blocks = ed.Document!.Blocks;
            var tb = blocks.OfType<TableBlock>().Single();
            Assert.True(blocks.IndexOf(tb) > 1, "the table should follow the last line");
            Assert.Equal(200, tb.RowHeights.Sum(), 1);
        });
    }
    // The table lands where the drag began, not at the caret (2026-11-08 — as on the Avalonia control).
    [Fact]
    public void TheTable_LandsWhereTheDragBegan_NotAtTheCaret()
    {
        Hosted(ed =>
        {
            Load(ed, Para("first"), Para("a"), Para("b"), Para("target"), Para("c"), Para("d"), Para("e"), Para("last"));
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToList();
            var target = paras.First(p => p.Inlines.OfType<Run>().Any(r => r.Text == "target"));
            object boxed = Call(ed, "CaretToDocPoint", new TextPointer(target, 3))!; // the caret itself stays in "first"
            var ty = boxed.GetType();
            var at = new Point((double)ty.GetField("Item1")!.GetValue(boxed)!, (double)ty.GetField("Item2")!.GetValue(boxed)! + 2);

            ed.BeginTableDraw(2, 2);
            Assert.True(ed.TableDrawPressAt(at));
            Call(ed, "TableDrawPointerMoved", new Point(at.X + 200, at.Y + 60));
            Assert.True(ed.TableDrawReleaseAt());

            var blocks = ed.Document!.Blocks;
            int ti = blocks.IndexOf(blocks.OfType<TableBlock>().Single());
            Assert.Contains("tar", string.Concat(((Paragraph)blocks[ti - 1]).Inlines.OfType<Run>().Select(r => r.Text)));
        });
    }

}
