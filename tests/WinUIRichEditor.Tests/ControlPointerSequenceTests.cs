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
