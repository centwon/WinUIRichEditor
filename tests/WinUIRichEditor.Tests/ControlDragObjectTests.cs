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

/// <summary>Dragging a table or a picture to move it, or with Ctrl to copy it (RichEditor.DragBlock.cs,
/// 2026-09-15). The table's left/top border already showed the SizeAll (move) cursor and only selected.
/// <para>The drop rule is pinned by shape: a block dropped at a paragraph's start goes BEFORE it, at its end
/// AFTER it, and splits it only in between — the rule that keeps a table dragged down and back from growing
/// the document by a blank line per trip. A drop where the object already is must be no edit at all.</para>
/// <para>The gesture is driven the way ControlTableResizeTests drives a column drag: the press, move and
/// release minus their pointer capture (<c>ArmObjectDragAt</c> / <c>DragObjectMoved</c> /
/// <c>FinishObjectDrag</c>), at the coordinates the table was drawn at.</para>
/// <para>⚠ The wiring — PointerPressed/Moved/Released reaching these, the cursor, the "+" copy mark following
/// Ctrl — is outside automated reach (a PointerRoutedEventArgs cannot be constructed).</para></summary>
[Collection(UiTests.Collection)]
public class ControlDragObjectTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type T = typeof(RichEditor);

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
            finally { ed.IsReadOnly = false; }
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

    // Through LoadJson so every test starts clean: not modified, nothing to undo.
    private static void Load(RichEditor ed, params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        ed.LoadJson(DocumentSerializer.Serialize(doc));
        Draw(ed);
    }

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static Paragraph P(string text, params Inline[] more)
    {
        var p = new Paragraph { Inlines = { new Run { Text = text } } };
        foreach (var i in more) p.Inlines.Add(i);
        return p;
    }

    private static TableBlock Table()
    {
        var tb = new TableBlock(1, 2);
        foreach (var (r, c, cell) in tb.LogicalCells()) ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c}";
        return tb;
    }

    private static ImageBlock Image()
    {
        var ib = new ImageBlock { Width = 40, Height = 30 };
        ib.SetImageData(TinyPng, "image/png");
        return ib;
    }

    private static InlineImage InlineImg()
    {
        var ii = new InlineImage { Width = 16, Height = 16 };
        ii.SetImageData(TinyPng, "image/png");
        return ii;
    }

    // A paragraph's text with its objects as [i] / [t], so a shape reads as the document does.
    private static string Text(Paragraph p) => string.Concat(p.Inlines.Select(i => i switch
    {
        Run r => r.Text ?? "",
        InlineImage => "[i]",
        InlineTable => "[t]",
        _ => "?",
    }));

    private static int Len(Paragraph p) => p.Inlines.Sum(i => i is Run r ? (r.Text ?? "").Length : 1);

    // A block list's shape: T = table, I = image, a paragraph by its text (∅ when empty).
    private static string Shape(IEnumerable<Block> blocks) => string.Join(",", blocks.Select(b => b switch
    {
        TableBlock => "T",
        ImageBlock => "I",
        Paragraph p => Text(p) is { Length: > 0 } s ? s : "∅",
        _ => "?",
    }));

    private static string Shape(RichEditor ed) => Shape(ed.Document!.Blocks);
    private static Paragraph Para(RichEditor ed, string text) => ed.Document!.Blocks.OfType<Paragraph>().First(p => Text(p) == text);
    private static TableBlock FirstTable(RichEditor ed) => ed.Document!.Blocks.OfType<TableBlock>().First();
    private static Paragraph CellPara(TableBlock tb, int r, int c) => tb.Cells[r][c].Blocks.OfType<Paragraph>().First();

    private static bool Drop(RichEditor ed, object obj, Paragraph p, int off, bool copy = false)
        => (bool)Call(ed, "DropObject", obj, new TextPointer(p, off), copy)!;

    // ---- where a block lands ------------------------------------------------------------------------

    [Fact]
    public void DroppedAtAParagraphsStart_TheBlockGoesBeforeIt_InOneUndoStep()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            var tb = FirstTable(ed);

            Assert.True(Drop(ed, tb, Para(ed, "end"), 0));
            Assert.Equal("top,mid,T,end", Shape(ed));
            Assert.Same(tb, ed.Document!.Blocks[2]);              // moved, not re-created
            Assert.Same(tb, Field(ed, "_selectedBlock"));         // still selected, as when grabbed
            Assert.True(ed.IsModified);

            ed.Undo();
            Assert.Equal("top,T,mid,end", Shape(ed));
            Assert.False(ed.CanUndo);
        });
    }

    [Fact]
    public void DroppedAtAParagraphsEnd_TheBlockGoesAfterIt()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            Assert.True(Drop(ed, FirstTable(ed), Para(ed, "end"), 3));
            Assert.Equal("top,mid,end,T,∅", Shape(ed)); // the closing paragraph a block may not end on
        });
    }

    [Fact]
    public void DroppedInsideAParagraph_SplitsIt()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("middle"), P("end"));
            Assert.True(Drop(ed, FirstTable(ed), Para(ed, "middle"), 3));
            Assert.Equal("top,mid,T,dle,end", Shape(ed));
        });
    }

    // Right after the paragraph before it, or right before the paragraph after it: that is where it is.
    [Fact]
    public void ADropWhereTheBlockAlreadyIs_IsNoEdit()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            var tb = FirstTable(ed);

            Assert.False(Drop(ed, tb, Para(ed, "mid"), 0));
            Assert.False(Drop(ed, tb, Para(ed, "top"), 3));
            Assert.Equal("top,T,mid,end", Shape(ed));
            Assert.False(ed.CanUndo);
            Assert.False(ed.IsModified);
        });
    }

    // The accumulation class: a rule that leaves a paragraph behind per drop grows the document per trip.
    // The first trip may leave the closing paragraph a block needs; after that the shapes must repeat.
    [Fact]
    public void DraggingATableDownAndBack_DoesNotGrowTheDocument()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            var tb = FirstTable(ed);
            var downs = new List<string>();
            var backs = new List<string>();
            for (int trip = 0; trip < 3; trip++)
            {
                Assert.True(Drop(ed, tb, Para(ed, "end"), 3));
                downs.Add(Shape(ed));
                Assert.True(Drop(ed, tb, Para(ed, "mid"), 0));
                backs.Add(Shape(ed));
            }
            Assert.All(downs, s => Assert.Equal(downs[0], s));
            Assert.All(backs, s => Assert.Equal(backs[0], s));
        });
    }

    // ---- copy, and a table's own cells -------------------------------------------------------------

    [Fact]
    public void ACopy_LeavesTheOriginal_AndDropsAClone()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            var tb = FirstTable(ed);

            Assert.True(Drop(ed, tb, Para(ed, "end"), 0, copy: true));
            Assert.Equal("top,T,mid,T,end", Shape(ed));
            var copy = (TableBlock)ed.Document!.Blocks[3];
            Assert.NotSame(tb, copy);
            Assert.Same(tb, ed.Document!.Blocks[1]);
            Assert.Equal("c01", Text(CellPara(copy, 0, 1)));
            Assert.Same(copy, Field(ed, "_selectedBlock")); // the dropped one is the one selected

            ed.Undo();
            Assert.Equal("top,T,mid,end", Shape(ed));
        });
    }

    // A table moved into one of its own cells would have to contain itself. A copy is a clone, so it may.
    [Fact]
    public void AMoveIntoItsOwnCell_IsRefused_ButACopyThereIsAllowed()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("end"));
            var tb = FirstTable(ed);
            var cell = CellPara(tb, 0, 0);

            Assert.False(RichEditor.CanDropObject(tb, new TextPointer(cell, 0), copy: false));
            Assert.False(Drop(ed, tb, cell, 0));
            Assert.Equal("top,T,end", Shape(ed));
            Assert.False(ed.CanUndo);

            Assert.True(Drop(ed, tb, CellPara(tb, 0, 0), 0, copy: true));
            Assert.Contains(tb.Cells[0][0].Blocks, b => b is TableBlock nested && !ReferenceEquals(nested, tb));
            DocumentFuzzTests.CheckInvariants(ed.Document!);
        });
    }

    [Fact]
    public void AnImage_MovesIntoACell_AndBackOut()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Image(), P("mid"), Table(), P("end"));
            var img = ed.Document!.Blocks.OfType<ImageBlock>().First();
            var tb = FirstTable(ed);

            Assert.True(Drop(ed, img, CellPara(tb, 0, 1), 3)); // after "c01"
            Assert.Equal("top,mid,T,end", Shape(ed));
            Assert.Equal("c01,I,∅", Shape(tb.Cells[0][1].Blocks));
            Assert.Same(img, Field(ed, "_selectedBlock"));

            // Before "end" is right after the table: two blocks may not touch (rule #5), so a blank line
            // separates them — the model's, not the drop's. It is there once; another trip adds none.
            Assert.True(Drop(ed, img, Para(ed, "end"), 0));
            Assert.Equal("top,mid,T,∅,I,end", Shape(ed));
            Assert.True(Drop(ed, img, CellPara(tb, 0, 1), 3));
            Assert.True(Drop(ed, img, Para(ed, "end"), 0));
            Assert.Equal("top,mid,T,∅,I,end", Shape(ed));
            Assert.DoesNotContain(tb.Cells[0][1].Blocks, b => b is ImageBlock);
            DocumentFuzzTests.CheckInvariants(ed.Document!);
        });
    }

    // ---- inline objects: one character each -------------------------------------------------------

    [Fact]
    public void AnInlineImage_MovesLaterInItsOwnParagraph()
    {
        Hosted(ed =>
        {
            var host = P("ab", InlineImg(), new Run { Text = "cdef" });
            Load(ed, host, P("end"));
            var p = ed.Document!.Blocks.OfType<Paragraph>().First();
            var img = p.Inlines.OfType<InlineImage>().First();

            // Either side of itself is where it is.
            Assert.False(Drop(ed, img, p, 2));
            Assert.False(Drop(ed, img, p, 3));
            Assert.False(ed.CanUndo);

            // Between "c" and "d" (offset 4 counts the picture): once it is taken out ahead of the drop, that
            // gap is offset 3. MID-paragraph on purpose — dropped at the very end, an unshifted offset clamps
            // to the same end and the shift goes unobserved (falsification caught exactly that).
            Assert.True(Drop(ed, img, p, 4));
            Assert.Equal("abc[i]def", Text(p));
            Assert.Equal(img, ((ValueTuple<Paragraph, InlineImage>?)Field(ed, "_selectedInline"))?.Item2);

            ed.Undo();
            Assert.Equal("ab[i]cdef", Text(ed.Document!.Blocks.OfType<Paragraph>().First()));
        });
    }

    [Fact]
    public void AnInlineTable_MovesToAnotherParagraph_ButNotIntoItself()
    {
        Hosted(ed =>
        {
            var it = new InlineTable { Table = Table() };
            Load(ed, P("host", it), P("dest"));
            var host = Para(ed, "host[t]");
            var moved = host.Inlines.OfType<InlineTable>().First();

            Assert.False(Drop(ed, moved, CellPara(moved.Table, 0, 0), 1)); // into its own cell

            Assert.True(Drop(ed, moved, Para(ed, "dest"), 0));
            Assert.Equal("host,[t]dest", Shape(ed));
            DocumentFuzzTests.CheckInvariants(ed.Document!);
        });
    }

    // ---- the gesture -------------------------------------------------------------------------------

    private static Rect DrawnTableRect(RichEditor ed, TableBlock tb)
    {
        var rects = (IDictionary)Field(ed, "_tableRects")!;
        Assert.True(rects.Contains(tb), "the table recorded no rect — did it draw?");
        return (Rect)rects[tb]!;
    }

    // Press on the left border band (where the SizeAll cursor shows), then either stay inside the click slop
    // — only the selection the press made — or travel below the document, which snaps to the last line.
    [Fact]
    public void TheGesture_AClickOnlySelects_ADragMoves()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            var tb = FirstTable(ed);
            var r = DrawnTableRect(ed, tb);
            var press = new Point(r.Left, r.Top + r.Height / 2);

            Assert.True((bool)Call(ed, "ArmObjectDragAt", tb, press)!);
            Call(ed, "DragObjectMoved", new Point(press.X + 1, press.Y + 1), false); // inside the slop
            Assert.False((bool)Call(ed, "FinishObjectDrag", false)!);
            Assert.Equal("top,T,mid,end", Shape(ed));
            Assert.False(ed.CanUndo);

            Assert.True((bool)Call(ed, "ArmObjectDragAt", tb, press)!);
            Call(ed, "DragObjectMoved", new Point(press.X, r.Bottom + 2000), false);
            Assert.True((bool)Call(ed, "FinishObjectDrag", false)!);
            Assert.Equal("top,mid,T,end", Shape(ed));
            Assert.Null(Field(ed, "_dragObject"));
            Assert.Null(Field(ed, "_dropPreview"));
        });
    }

    // A viewer's border click selects the table for Copy; it must never arm a move.
    [Fact]
    public void AViewer_NeverArmsAnObjectDrag()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("end"));
            ed.IsReadOnly = true;
            Assert.False((bool)Call(ed, "ArmObjectDragAt", FirstTable(ed), new Point(10, 10))!);
        });
    }

    // Opening a document (or undoing — it swaps in a snapshot) under a live drag: the release must not drop
    // the old document's table into the new one, nor leave the new one modified.
    [Fact]
    public void ADocumentSwapMidDrag_CancelsTheDrag()
    {
        Hosted(ed =>
        {
            Load(ed, P("top"), Table(), P("mid"), P("end"));
            var tb = FirstTable(ed);
            var r = DrawnTableRect(ed, tb);
            var press = new Point(r.Left, r.Top + r.Height / 2);
            Assert.True((bool)Call(ed, "ArmObjectDragAt", tb, press)!);
            Call(ed, "DragObjectMoved", new Point(press.X, r.Bottom + 2000), false);

            Load(ed, P("other"), Table(), P("doc"));
            Assert.False((bool)Call(ed, "FinishObjectDrag", false)!);
            Assert.Equal("other,T,doc", Shape(ed));
            Assert.False(ed.IsModified);
            Assert.False(ed.CanUndo);
        });
    }
}
