using System;
using System.Collections;
using System.Collections.Generic;
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

/// <summary>Whole pointer GESTURES, in the order the framework produces them: press → capture → move →
/// release → capture-lost (RichEditor.PointerPipeline.cs).
/// <para>Every other control test drives "the handler minus its pointer capture" (<c>TableDrawPressAt</c>,
/// <c>BeginColumnResizeAt</c> …), because <c>PointerRoutedEventArgs</c> cannot be constructed. That leaves
/// the capture untested, and the capture is where the defects were: <c>ReleasePointerCapture</c> raises
/// <c>PointerCaptureLost</c> SYNCHRONOUSLY, so a release handler that lets go before finishing its work is
/// cancelled by its own release. That shipped once — every table draw inserted nothing (2026-09-19), with a
/// green suite; a person found it. These tests run the cores with a fake capture that keeps that timing.</para>
/// <para>⚠ What is still outside automated reach: the framework's own routing, hit-testing and focus, and
/// whether WinUI really raises capture-lost the way <see cref="FakeCapture"/> does (measured in the app, and
/// the reason the fake is kept this small). The release gate for that is tools/fault-sweep.ps1 plus a live
/// check.</para></summary>
[Collection(UiTests.Collection)]
public class ControlPointerSequenceTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    /// <summary>The pointer capture as WinUI behaves: <c>Release</c> raises capture-lost synchronously, and
    /// only when the pointer was actually held. It also records the call order, so a test can state it.</summary>
    private sealed class FakeCapture(RichEditor editor) : RichEditor.IPointerCapture
    {
        public List<string> Order { get; } = [];
        public bool Held { get; private set; }

        public void Capture() { Held = true; Order.Add("capture"); }

        public void Release()
        {
            Order.Add("release");
            if (!Held) return;
            Held = false;
            Order.Add("lost"); // recorded so a test can state that the release really did raise it
            editor.PointerCaptureLostCore();
        }

        /// <summary>Capture taken away from the outside (another window, a touch cancel) — no release.</summary>
        public void Lose()
        {
            Order.Add("lost");
            Held = false;
            editor.PointerCaptureLostCore();
        }
    }

    // One hosted editor (coordinates need a real layout), documents swapped per test.
    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // Shared.Value OUTSIDE UiThread.Run: hosting waits on the UI thread, so resolving it inside deadlocks.
    private static void Hosted(Action<RichEditor> body)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            // These tests press real points on ONE shared editor, milliseconds apart. Without this, a press
            // at (nearly) the same point as the previous test's is a DOUBLE-CLICK — the press selects a word
            // instead of arming a drag, and the failure reads as a product defect in whichever test happens
            // to run second (it did: "the press inside the selection did not arm the drag").
            T.GetField("_lastPressTime", NP)!.SetValue(ed, DateTime.MinValue);
            T.GetField("_clickCount", NP)!.SetValue(ed, 0);
            try { body(ed); }
            finally { Call(ed, "CancelTableDraw"); Call(ed, "EndPointerDrags"); ed.IsReadOnly = false; }
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

    private static object? Field(RichEditor ed, string name) => T.GetField(name, NP)!.GetValue(ed);

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

    private static TableBlock Table(int rows = 2, int cols = 2)
    {
        var tb = new TableBlock { Rows = rows, Columns = cols };
        for (int r = 0; r < rows; r++)
        {
            var row = new List<TableCell>();
            for (int c = 0; c < cols; c++) row.Add(new TableCell { Blocks = { Para($"r{r}c{c}") } });
            tb.Cells.Add(row);
        }
        for (int c = 0; c < cols; c++) tb.ColumnWidths.Add(120);
        return tb;
    }

    // The editor is at zoom 1 in Continuous mode, so view coordinates ARE document coordinates here — the
    // cores still run ViewToDoc, which ControlZoomTests covers at 200/300 %.
    private static RichEditor.PointerStep Step(Point at, FakeCapture cap, bool ctrl = false, bool shift = false, bool right = false)
        => new(at, right, ctrl, shift, cap);

    private static Point CaretPoint(RichEditor ed) => DocPointOf(ed, Field(ed, "_caret")!);

    // Where a text position draws, in document space (the caret's top-left).
    private static Point DocPointOf(RichEditor ed, object textPointer)
    {
        object boxed = Call(ed, "CaretToDocPoint", textPointer)!;
        var ty = boxed.GetType();
        return new Point((double)ty.GetField("Item1")!.GetValue(boxed)!, (double)ty.GetField("Item2")!.GetValue(boxed)!);
    }

    private static (int i, Rect rect)[] Bands(RichEditor ed, string field, TableBlock tb)
    {
        var dict = (IDictionary)Field(ed, field)!;
        Assert.True(dict.Contains(tb), $"{field} has no bands for the table — did it draw?");
        return ((IList)dict[tb]!).Cast<object>().Select(o =>
        {
            var f = o.GetType().GetFields();
            return ((int)f[0].GetValue(o)!, (Rect)f[^1].GetValue(o)!);
        }).ToArray();
    }

    private static Point ColumnEdge(RichEditor ed, TableBlock tb, int col)
    {
        var r = Bands(ed, "_columnBoundaries", tb)[col].rect;
        return new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
    }

    private static int Tables(RichEditor ed) => ed.Document!.Blocks.OfType<TableBlock>().Count();

    // ---- the gesture that shipped broken ------------------------------------------------------------

    // The release hands the table to the document BEFORE it lets go of the pointer. Reverse the two and the
    // synchronous capture-lost cancels the draw a line before it can insert — which is exactly what shipped:
    // every drag drew a preview and left nothing behind.
    [Fact]
    public void ADrawGesture_InsertsTheTable_AlthoughTheReleaseAlsoDropsTheCapture()
    {
        Hosted(ed =>
        {
            Load(ed, Para("before"), Para("a"), Para("b"), Para("c"), Para("d"), Para("after"));
            ed.BeginTableDraw(2, 3);
            var cap = new FakeCapture(ed);
            var at = CaretPoint(ed);

            ed.PointerPressedCore(Step(at, cap));
            Assert.True(cap.Held, "the press did not take the pointer");
            ed.PointerMovedCore(Step(new Point(at.X + 300, at.Y + 80), cap));
            ed.PointerReleasedCore(Step(new Point(at.X + 300, at.Y + 80), cap));

            Assert.Equal(1, Tables(ed));
            // The "lost" is what makes this test able to fail at all: with a fake that released without
            // raising capture-lost, the reversed (broken) order passes — measured. Assert it here so a
            // weakened fake breaks THIS test instead of quietly making it vacuous.
            Assert.Equal(new[] { "capture", "release", "lost" }, cap.Order);
            Assert.False(cap.Held, "the pointer was not released");
            Assert.Null(Field(ed, "_tableDrawStart"));
        });
    }

    // A lost capture is not a release: the drag is abandoned, not completed with the rectangle it had reached.
    // Same rule as the coordinate-level test, but reached through the gesture, capture and all.
    [Fact]
    public void LosingTheCaptureMidDraw_InsertsNothing_AndALaterReleaseStaysQuiet()
    {
        Hosted(ed =>
        {
            Load(ed, Para("before"), Para("after"));
            ed.BeginTableDraw(2, 2);
            var cap = new FakeCapture(ed);
            var at = CaretPoint(ed);

            ed.PointerPressedCore(Step(at, cap));
            ed.PointerMovedCore(Step(new Point(at.X + 200, at.Y + 60), cap));
            cap.Lose();

            Assert.Equal(0, Tables(ed));
            ed.PointerReleasedCore(Step(new Point(at.X + 200, at.Y + 60), cap));
            Assert.Equal(0, Tables(ed));
            Assert.False(ed.IsModified);
        });
    }

    // ---- column drag, through the same gesture ------------------------------------------------------

    // ⚠ This does NOT guard the column release's ORDER: reversing it (release, then finish) was measured
    // to change nothing, because capture-lost FINISHES a column drag rather than cancelling it and
    // FinishColumnResize is idempotent. The order rule is load-bearing only where capture-lost cancels —
    // the table draw above, and the object drag (phase 2).
    [Fact]
    public void AColumnDragGesture_ResizesAndEndsWithNothingLive()
    {
        Hosted(ed =>
        {
            Load(ed, Para("top"), Table(), Para("end"));
            var tb = ed.Document!.Blocks.OfType<TableBlock>().Single();
            double before = tb.ColumnWidths[0];
            var edge = ColumnEdge(ed, tb, 0);
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(edge, cap));
            Assert.True(cap.Held, "the press on the column boundary did not take the pointer");
            ed.PointerMovedCore(Step(new Point(edge.X + 40, edge.Y), cap));
            ed.PointerReleasedCore(Step(new Point(edge.X + 40, edge.Y), cap));

            Assert.Equal(before + 40, tb.ColumnWidths[0], 1);
            Assert.False((bool)Field(ed, "_resizingColumn")!, "still resizing after the release");
            Assert.False(cap.Held);
            Assert.True(ed.CanUndo, "the drag left no undo step");
        });
    }

    // A lost capture ENDS a column drag rather than cancelling it — the width the user dragged to is what
    // they saw, and there is no drop to undo (EndPointerDrags: FinishColumnResize, not a revert). The point
    // of stating it here is the second half: nothing stays live, so the next hover does not go on resizing.
    [Fact]
    public void LosingTheCaptureMidColumnDrag_KeepsTheWidthAndEndsTheDrag()
    {
        Hosted(ed =>
        {
            Load(ed, Para("top"), Table(), Para("end"));
            var tb = ed.Document!.Blocks.OfType<TableBlock>().Single();
            double before = tb.ColumnWidths[0];
            var edge = ColumnEdge(ed, tb, 0);
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(edge, cap));
            ed.PointerMovedCore(Step(new Point(edge.X + 30, edge.Y), cap));
            cap.Lose();

            Assert.Equal(before + 30, tb.ColumnWidths[0], 1);
            Assert.False((bool)Field(ed, "_resizingColumn")!, "still resizing after the capture was lost");

            // The hover that follows must not resize anything (this is what an unended drag did).
            double after = tb.ColumnWidths[0];
            ed.PointerMovedCore(Step(new Point(edge.X + 300, edge.Y), cap));
            Assert.Equal(after, tb.ColumnWidths[0], 1);
        });
    }

    // Capture-lost arriving after a normal release is a no-op — the release cleared its own drag first.
    // Without that, the pair "release then lost" would run the end path twice.
    [Fact]
    public void ACaptureLostAfterANormalRelease_ChangesNothing()
    {
        Hosted(ed =>
        {
            Load(ed, Para("before"), Para("a"), Para("b"), Para("c"), Para("after"));
            ed.BeginTableDraw(2, 2);
            var cap = new FakeCapture(ed);
            var at = CaretPoint(ed);

            ed.PointerPressedCore(Step(at, cap));
            ed.PointerMovedCore(Step(new Point(at.X + 200, at.Y + 60), cap));
            ed.PointerReleasedCore(Step(new Point(at.X + 200, at.Y + 60), cap));
            string after = ed.ToJson();

            ed.PointerCaptureLostCore();

            Assert.Equal(1, Tables(ed));
            Assert.Equal(after, ed.ToJson());
        });
    }

    // ---- picture handle, object drag, text drag (phase 2) -------------------------------------------

    private static Rect TableRect(RichEditor ed, TableBlock tb)
    {
        var rects = (IDictionary)Field(ed, "_tableRects")!;
        Assert.True(rects.Contains(tb), "the table recorded no rect — did it draw?");
        return (Rect)rects[tb]!;
    }

    // The left border band, where the SizeAll cursor promises a move.
    private static Point TableMoveBorder(RichEditor ed, TableBlock tb)
    {
        var r = TableRect(ed, tb);
        return new Point(r.X + 1, r.Y + r.Height / 2);
    }

    private static string Shape(RichEditor ed) => string.Join(",", ed.Document!.Blocks.Select(b => b switch
    {
        TableBlock => "T",
        ImageBlock => "I",
        Paragraph p => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)) is { Length: > 0 } s ? s : "∅",
        _ => "?",
    }));

    // Two gestures at the same point inside ONE test are a double-click unless the timing is cleared —
    // the press then selects a word instead of doing what the test is about (see Hosted).
    private static void NoRepeat(RichEditor ed)
    {
        T.GetField("_lastPressTime", NP)!.SetValue(ed, DateTime.MinValue);
        T.GetField("_clickCount", NP)!.SetValue(ed, 0);
    }

    private static void Select(RichEditor ed, Paragraph p, int from, int to)
    {
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(p, from));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(p, to));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(p, to));
    }

    [Fact]
    public void AnObjectDragGesture_MovesTheTable_AndALostCaptureDropsNothing()
    {
        Hosted(ed =>
        {
            Load(ed, Para("top"), Table(), Para("mid"), Para("end"));
            var tb = ed.Document!.Blocks.OfType<TableBlock>().Single();
            var border = TableMoveBorder(ed, tb);
            var cap = new FakeCapture(ed);

            // A lost capture mid-drag is not a drop: the table stays where it was.
            ed.PointerPressedCore(Step(border, cap));
            Assert.True(cap.Held, "the press on the table border did not take the pointer");
            ed.PointerMovedCore(Step(new Point(border.X, border.Y + 2000), cap));
            cap.Lose();
            Assert.Equal("top,T,mid,end", Shape(ed));
            Assert.Null(Field(ed, "_dragObject"));

            // The same gesture, released: it moves.
            Draw(ed);
            border = TableMoveBorder(ed, tb);
            ed.PointerPressedCore(Step(border, cap));
            ed.PointerMovedCore(Step(new Point(border.X, border.Y + 2000), cap));
            ed.PointerReleasedCore(Step(new Point(border.X, border.Y + 2000), cap));
            Assert.Equal("top,mid,T,end", Shape(ed)); // the drop snaps to the last line it reached

            Assert.False(cap.Held);
        });
    }

    // A lost capture must not leave a text drag armed. Left armed, the NEXT click performs the drop the
    // user never made: the press does not clear the arming, and the release runs EndTextDrag with the stale
    // drop preview — the selection moves on a plain click. (Port-only: upstream has no in-document text
    // drag, only external file drop, so there was no precedent to compare against.)
    [Fact]
    public void LosingTheCaptureMidTextDrag_DoesNotMoveTheTextOnTheNextClick()
    {
        Hosted(ed =>
        {
            Load(ed, Para("drag this text"), Para("target line"));
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToArray();
            Select(ed, paras[0], 0, 14);
            var inside = DocPointOf(ed, new TextPointer(paras[0], 6));   // strictly inside the selection
            var target = DocPointOf(ed, new TextPointer(paras[1], 6));
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(inside, cap));
            Assert.True((bool)Field(ed, "_dragTextArmed")!, "the press inside the selection did not arm the drag");
            ed.PointerMovedCore(Step(target, cap));                      // past the slop: drop preview live
            cap.Lose();

            Assert.False((bool)Field(ed, "_dragTextArmed")!, "the text drag survived the lost capture");

            // What that costs when it survives: the user's next click drops the text.
            string before = ed.GetPlainText();
            ed.PointerPressedCore(Step(target, cap));
            ed.PointerReleasedCore(Step(target, cap));
            Assert.Equal(before, ed.GetPlainText());
        });
    }

    private static ImageBlock Picture(int w, int h)
    {
        var img = new ImageBlock { Width = w, Height = h };
        img.SetImageData(ControlImageDecodeTests.SolidBmp(w, h, 200, 40, 40), "image/bmp");
        return img;
    }

    // The corner handle of a selected block picture, at the rect it was DRAWN at.
    private static Point CornerHandle(RichEditor ed, ImageBlock img)
    {
        var r = ((IEnumerable<Rect>)Call(ed, "BlockImageHandleRects", img)!).First();
        return new Point(r.Right, r.Bottom);
    }

    [Fact]
    public void APictureResizeGesture_ResizesIt_AndALostCaptureEndsTheDragKeepingTheSize()
    {
        Hosted(ed =>
        {
            Load(ed, Para("top"), Picture(120, 80), Para("end"));
            // Load round-trips through the serializer, so the picture in the document is a NEW instance —
            // the one built above is not in it (that mistake cost a run here).
            var img = ed.Document!.Blocks.OfType<ImageBlock>().Single();
            T.GetField("_selectedBlock", NP)!.SetValue(ed, img);
            Draw(ed);
            var cap = new FakeCapture(ed);

            // Released normally: the picture keeps the dragged size and nothing stays live.
            var grip = CornerHandle(ed, img);
            ed.PointerPressedCore(Step(grip, cap));
            Assert.True(cap.Held, "the press on the handle did not take the pointer");
            ed.PointerMovedCore(Step(new Point(grip.X + 60, grip.Y + 40), cap));
            ed.PointerReleasedCore(Step(new Point(grip.X + 60, grip.Y + 40), cap));
            double resized = img.Width;
            Assert.True(resized > 120, $"the drag did not resize the picture (width {resized})");
            Assert.Null(Field(ed, "_resizingImage"));
            Assert.False(cap.Held);

            // Lost mid-drag: the size the user dragged to is kept (capture-lost FINISHES a resize), and the
            // drag ends — the hover that follows must not go on resizing.
            Draw(ed);
            grip = CornerHandle(ed, img);
            ed.PointerPressedCore(Step(grip, cap));
            ed.PointerMovedCore(Step(new Point(grip.X + 30, grip.Y + 20), cap));
            cap.Lose();
            double afterLost = img.Width;
            Assert.True(afterLost > resized, "the lost capture threw away the drag");
            Assert.Null(Field(ed, "_resizingImage"));
            ed.PointerMovedCore(Step(new Point(grip.X + 400, grip.Y + 300), cap));
            Assert.Equal(afterLost, img.Width, 1);
        });
    }

    // A document swapped in mid-drag (a file opened, an undo) belongs to nobody's drag: the armed object is
    // in the document that is gone, so the move and the release must leave the new one alone.
    [Fact]
    public void ReplacingTheDocumentMidDrag_LeavesTheNewOneAlone()
    {
        Hosted(ed =>
        {
            Load(ed, Para("top"), Table(), Para("end"));
            var tb = ed.Document!.Blocks.OfType<TableBlock>().Single();
            var border = TableMoveBorder(ed, tb);
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(border, cap));
            Load(ed, Para("new file"), Para("second")); // the document the drag was about is gone
            Assert.Null(Field(ed, "_dragObject"));

            ed.PointerMovedCore(Step(new Point(border.X, border.Y + 500), cap));
            ed.PointerReleasedCore(Step(new Point(border.X, border.Y + 500), cap));

            Assert.Equal("new file,second", Shape(ed));
            Assert.False(ed.IsModified, "the new document was modified by a drag from the old one");
        });
    }

    // The same rule for an armed TEXT drag. Weaker consequence than the lost-capture case above — the swap
    // collapses the selection, so the drop cannot reach the new document — but the arming left a drop caret
    // trailing the pointer over a file just opened, so this asserts the state rather than a changed document.
    [Fact]
    public void ReplacingTheDocumentMidTextDrag_DisarmsIt()
    {
        Hosted(ed =>
        {
            Load(ed, Para("drag this text"), Para("target line"));
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToArray();
            Select(ed, paras[0], 0, 14);
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(DocPointOf(ed, new TextPointer(paras[0], 6)), cap));
            Assert.True((bool)Field(ed, "_dragTextArmed")!);

            Load(ed, Para("new file"));

            Assert.False((bool)Field(ed, "_dragTextArmed")!, "the text drag survived the document swap");
            Assert.Null(Field(ed, "_dropPreview"));
        });
    }

    // ---- what the modifiers do (phase 3) ------------------------------------------------------------
    // These gestures had NO automated test before the pipeline: Ctrl and Shift were static reads of the
    // real keyboard (InputKeyboardSource), which a test cannot set. They ride on the step now.

    [Fact]
    public void CtrlAtTheDrop_CopiesTheTableInsteadOfMovingIt()
    {
        Hosted(ed =>
        {
            Load(ed, Para("top"), Table(), Para("end"));
            var tb = ed.Document!.Blocks.OfType<TableBlock>().Single();
            var border = TableMoveBorder(ed, tb);
            var cap = new FakeCapture(ed);
            var drop = new Point(border.X, border.Y + 2000);

            ed.PointerPressedCore(Step(border, cap));
            ed.PointerMovedCore(Step(drop, cap, ctrl: true));
            ed.PointerReleasedCore(Step(drop, cap, ctrl: true));

            // Copied, not moved: there are two tables and the ORIGINAL instance is still in the document.
            Assert.Equal(2, ed.Document!.Blocks.OfType<TableBlock>().Count());
            Assert.Contains(tb, ed.Document!.Blocks);
            Assert.Equal("top,T,∅,T,end", Shape(ed)); // the drop's landing paragraph is left behind empty
        });
    }

    [Fact]
    public void CtrlAtTheDrop_CopiesTheDraggedTextInsteadOfMovingIt()
    {
        Hosted(ed =>
        {
            Load(ed, Para("drag this text"), Para("target line"));
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToArray();
            Select(ed, paras[0], 0, 4); // "drag"
            var inside = DocPointOf(ed, new TextPointer(paras[0], 2));
            var target = DocPointOf(ed, new TextPointer(paras[1], 6));
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(inside, cap));
            Assert.True((bool)Field(ed, "_dragTextArmed")!, "the press inside the selection did not arm the drag");
            ed.PointerMovedCore(Step(target, cap, ctrl: true));
            ed.PointerReleasedCore(Step(target, cap, ctrl: true));

            // A copy leaves the source intact and puts a second "drag" at the drop point.
            string text = ed.GetPlainText();
            Assert.Contains("drag this text", text);
            Assert.Equal(2, System.Text.RegularExpressions.Regex.Matches(text, "drag").Count);
        });
    }

    [Fact]
    public void CtrlClickOpensALink_APlainClickDoesNot_AndADragNeverDoes()
    {
        Hosted(ed =>
        {
            var launched = new List<string>();
            ed.LaunchOverride = u => { launched.Add(u.ToString()); return Task.CompletedTask; };
            try
            {
                var link = new Run { Text = "example", NavigateUri = "https://example.com/" };
                var p = new Paragraph { Inlines = { link } };
                Load(ed, p, Para("after"));
                var para = ed.Document!.Blocks.OfType<Paragraph>().First();
                var onLink = DocPointOf(ed, new TextPointer(para, 3));
                var cap = new FakeCapture(ed);

                // Plain click: the caret moves, nothing opens.
                ed.PointerPressedCore(Step(onLink, cap));
                ed.PointerReleasedCore(Step(onLink, cap));
                Assert.Empty(launched);

                // Ctrl+click: opens the link under the POINTER.
                NoRepeat(ed);
                ed.PointerPressedCore(Step(onLink, cap, ctrl: true));
                ed.PointerReleasedCore(Step(onLink, cap, ctrl: true));
                Assert.Equal(new[] { "https://example.com/" }, launched);
            }
            finally { ed.LaunchOverride = null; }
        });
    }

    [Fact]
    public void InAViewer_APlainClickOpensTheLink_ButADragOverItOnlySelects()
    {
        Hosted(ed =>
        {
            var launched = new List<string>();
            ed.LaunchOverride = u => { launched.Add(u.ToString()); return Task.CompletedTask; };
            try
            {
                var p = new Paragraph { Inlines = { new Run { Text = "example link", NavigateUri = "https://example.com/" } } };
                Load(ed, p, Para("after"));
                ed.IsReadOnly = true;
                var para = ed.Document!.Blocks.OfType<Paragraph>().First();
                var start = DocPointOf(ed, new TextPointer(para, 1));
                var far = DocPointOf(ed, new TextPointer(para, 10));
                var cap = new FakeCapture(ed);

                // A drag across the link selects it (browser convention) — and opens nothing.
                ed.PointerPressedCore(Step(start, cap));
                ed.PointerMovedCore(Step(far, cap));
                ed.PointerReleasedCore(Step(far, cap));
                Assert.Empty(launched);
                Assert.NotEqual(((TextPointer)Field(ed, "_selStart")!).Offset,
                                ((TextPointer)Field(ed, "_selEnd")!).Offset); // it selected instead

                // A click that stays put opens it. (Without this the second press at the same point is a
                // DOUBLE-click, which selects the word — and a selection suppresses the launch.)
                NoRepeat(ed);
                ed.PointerPressedCore(Step(start, cap));
                ed.PointerReleasedCore(Step(start, cap));
                Assert.Equal(new[] { "https://example.com/" }, launched);
            }
            finally { ed.LaunchOverride = null; ed.IsReadOnly = false; }
        });
    }

    [Fact]
    public void ShiftPress_ExtendsTheSelectionFromWhereItWas()
    {
        Hosted(ed =>
        {
            Load(ed, Para("first line here"), Para("second line here"));
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToArray();
            var a = DocPointOf(ed, new TextPointer(paras[0], 2));
            var b = DocPointOf(ed, new TextPointer(paras[1], 6));
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(a, cap));
            ed.PointerReleasedCore(Step(a, cap));
            var start = (TextPointer)Field(ed, "_selStart")!;
            Assert.Same(paras[0], start.Paragraph);
            Assert.Equal(start.Offset, ((TextPointer)Field(ed, "_selEnd")!).Offset); // a plain click selects nothing

            ed.PointerPressedCore(Step(b, cap, shift: true));
            ed.PointerReleasedCore(Step(b, cap, shift: true));

            // Shift moved only the far end: the anchor is still where the first click put it.
            var end = (TextPointer)Field(ed, "_selEnd")!;
            Assert.Same(paras[0], ((TextPointer)Field(ed, "_selStart")!).Paragraph);
            Assert.Same(paras[1], end.Paragraph);
            Assert.Equal(6, end.Offset);
        });
    }

    [Fact]
    public void TheWheel_ZoomsOnlyWithCtrl()
    {
        Hosted(ed =>
        {
            Load(ed, Para("text"));
            double at100 = ed.Zoom;

            Assert.False(ed.PointerWheelCore(120, ctrl: false), "a plain wheel was handled (the view could not scroll)");
            Assert.Equal(at100, ed.Zoom, 3);

            Assert.True(ed.PointerWheelCore(120, ctrl: true));
            Assert.True(ed.Zoom > at100, "Ctrl+wheel did not zoom in");
            Assert.True(ed.PointerWheelCore(-120, ctrl: true));
            Assert.Equal(at100, ed.Zoom, 2);

            Assert.False(ed.PointerWheelCore(0, ctrl: true)); // no notch, nothing to do
        });
    }

    // Control: the press core is the ordinary press too, not just the drag branches — a plain click moves
    // the caret and takes the pointer, so these tests are running the real path and not a drag-only corner.
    [Fact]
    public void APlainPress_MovesTheCaret_AndTakesThePointer()
    {
        Hosted(ed =>
        {
            Load(ed, Para("first line"), Para("second line"));
            var second = ed.Document!.Blocks.OfType<Paragraph>().Last();
            var at = DocPointOf(ed, new TextPointer(second, 0));
            var cap = new FakeCapture(ed);

            ed.PointerPressedCore(Step(new Point(at.X + 2, at.Y + 4), cap));

            Assert.True(cap.Held, "a plain press did not take the pointer");
            Assert.Same(second, ((TextPointer)Field(ed, "_caret")!).Paragraph);

            ed.PointerReleasedCore(Step(new Point(at.X + 2, at.Y + 4), cap));
            Assert.False(cap.Held, "the release did not let go of the pointer");
        });
    }
}
