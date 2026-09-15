using System;
using System.Collections;
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

/// <summary>Table column and row drags (RichEditor.TableResize.cs) — 303 lines no test had ever referenced
/// (audit 2026-09-15).
/// <para>The drag handles are recorded as the table DRAWS, so these tests draw: the screen path
/// (<c>DrawDocument</c>) into an offscreen target, which records exactly the bands a user grabs — no planted
/// rectangles. A press is <c>BeginColumnResizeAt</c> (the handler minus its pointer capture), a move is
/// <c>ResizeColumn</c>, a release is <c>FinishColumnResize</c>.</para>
/// <para>Measured before the fixes: opening a document mid-drag marked it modified with an undo step; a
/// nested table grew through its neighbours (480px in a 190px cell), and an inline table in a cell did too;
/// a press without a drag padded <c>ColumnWidths</c>/<c>RowHeights</c> with no undo step. Controls that were
/// already right are pinned alongside (one edit and one undo step per drag, the inline host line reflowing
/// mid-drag, top-level tables uncapped).</para>
/// <para>⚠ The wiring itself — PointerPressed/Moved/Released/CaptureLost reaching these — is outside
/// automated reach (a PointerRoutedEventArgs cannot be constructed).</para></summary>
[Collection(UiTests.Collection)]
public class ControlTableResizeTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

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

    // The recorded grab bands of one table: (column or row index, band) in document space.
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

    private static Point Mid(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
    private static Point ColumnEdge(RichEditor ed, TableBlock tb, int col) => Mid(Bands(ed, "_columnBoundaries", tb)[col].rect);
    private static Point RowEdge(RichEditor ed, TableBlock tb, int row) => Mid(Bands(ed, "_rowBoundaries", tb)[row].rect);

    private static bool PressColumn(RichEditor ed, Point p) => (bool)Call(ed, "BeginColumnResizeAt", p)!;
    private static void MoveColumn(RichEditor ed, Point p, double dx) => Call(ed, "ResizeColumn", new Point(p.X + dx, p.Y));
    private static void ReleaseColumn(RichEditor ed) => Call(ed, "FinishColumnResize");

    private static void DragColumn(RichEditor ed, TableBlock tb, int col, double dx)
    {
        var p = ColumnEdge(ed, tb, col);
        Assert.True(PressColumn(ed, p));
        MoveColumn(ed, p, dx);
        ReleaseColumn(ed);
    }

    private static TableBlock Table(int rows, int cols, params double[] widths)
    {
        var tb = new TableBlock(rows, cols);
        for (int c = 0; c < cols; c++) tb.ColumnWidths[c] = widths.Length == 1 ? widths[0] : widths[c];
        foreach (var (r, c, cell) in tb.LogicalCells()) ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c}";
        return tb;
    }

    private static FlowDocument Around(Block b)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "top" } } });
        doc.Blocks.Add(b);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "end" } } });
        return doc;
    }

    // Through LoadJson so every test starts clean: not modified, nothing to undo.
    private static void Load(RichEditor ed, FlowDocument doc)
    {
        ed.LoadJson(DocumentSerializer.Serialize(doc));
        Draw(ed);
    }

    private static TableBlock TopTable(RichEditor ed) => (TableBlock)ed.Document!.Blocks[1];

    // A table nested in cell (0,0) of a 1×2 outer table of 200px columns — a 190px content box.
    private static (TableBlock outer, TableBlock inner) Nested(RichEditor ed, params double[] innerWidths)
    {
        var outer = Table(1, 2, 200);
        outer.Cells[0][0].Blocks.Add(Table(1, 2, innerWidths));
        outer.Cells[0][0].Blocks.Add(new Paragraph());
        Load(ed, Around(outer));
        var o = TopTable(ed);
        return (o, o.Cells[0][0].Blocks.OfType<TableBlock>().Single());
    }

    // ---- a drag is one edit ------------------------------------------------------------------------

    [Fact]
    public void ADrag_IsOneEdit_ReportedOnce_AndOneUndoStep() => Hosted(ed =>
    {
        Load(ed, Around(Table(1, 3, 100)));
        int modified = 0, text = 0;
        EventHandler m = (_, _) => modified++, t = (_, _) => text++;
        ed.IsModifiedChanged += m; ed.TextChanged += t;
        try
        {
            var tb = TopTable(ed);
            DragColumn(ed, tb, 0, 30);
            Assert.Equal(new[] { 130.0, 70, 100 }, tb.ColumnWidths);
            Assert.True(ed.IsModified);
            Assert.Equal(1, modified);
            Assert.Equal(1, text);

            ed.Undo();
            Assert.Equal(new[] { 100.0, 100, 100 }, TopTable(ed).ColumnWidths);
            Assert.False(ed.CanUndo);
        }
        finally { ed.IsModifiedChanged -= m; ed.TextChanged -= t; }
    });

    [Fact]
    public void ARowDrag_SetsTheRowsHeight_AndMovesTheEdgesBelow() => Hosted(ed =>
    {
        Load(ed, Around(Table(2, 2, 100)));
        var tb = TopTable(ed);
        double top = RowEdge(ed, tb, 0).Y, next = RowEdge(ed, tb, 1).Y;
        var p = RowEdge(ed, tb, 0);
        Assert.True((bool)Call(ed, "BeginRowResizeAt", p)!);
        Call(ed, "ResizeRow", new Point(p.X, p.Y + 40));
        Call(ed, "FinishRowResize");
        Draw(ed);
        Assert.Equal(top + 40, RowEdge(ed, tb, 0).Y, 3);
        Assert.Equal(next + 40, RowEdge(ed, tb, 1).Y, 3);
        Assert.True(ed.IsModified);
    });

    // A click on a border that does not become a drag changes nothing — not even the widths LayoutTable already
    // draws as 100. The press padded ColumnWidths (1 → 3 here) and RowHeights with no undo step and no
    // IsModified, so the document the host saves next differed from the one it was told was unchanged.
    [Fact]
    public void APressWithoutADrag_WritesNothing() => Hosted(ed =>
    {
        var src = Table(2, 3, 100);
        src.ColumnWidths.RemoveRange(1, 2);
        Load(ed, Around(src));
        var tb = TopTable(ed);
        Assert.Single(tb.ColumnWidths);
        Assert.Empty(tb.RowHeights);

        Assert.True(PressColumn(ed, ColumnEdge(ed, tb, 0)));
        ReleaseColumn(ed);
        Assert.True((bool)Call(ed, "BeginRowResizeAt", RowEdge(ed, tb, 0))!);
        Call(ed, "FinishRowResize");

        Assert.Single(tb.ColumnWidths);
        Assert.Empty(tb.RowHeights);
        Assert.False(ed.IsModified);
        Assert.False(ed.CanUndo);
    });

    // The first move of a drag over a short ColumnWidths still works — the padding moved there, after the
    // undo checkpoint, so undo takes the table back to the short list it was loaded with.
    [Fact]
    public void ADragOverShortColumnWidths_PadsAfterTheCheckpoint() => Hosted(ed =>
    {
        var src = Table(1, 3, 100);
        src.ColumnWidths.RemoveRange(1, 2);
        Load(ed, Around(src));
        DragColumn(ed, TopTable(ed), 0, 30);
        Assert.Equal(new[] { 130.0, 70, 100 }, TopTable(ed).ColumnWidths);
        ed.Undo();
        Assert.Single(TopTable(ed).ColumnWidths);
    });

    [Fact]
    public void AViewer_OffersNoHandles() => Hosted(ed =>
    {
        Load(ed, Around(Table(2, 2, 100)));
        ed.IsReadOnly = true;
        var tb = TopTable(ed);
        Assert.False(PressColumn(ed, ColumnEdge(ed, tb, 0)));
        Assert.False((bool)Call(ed, "BeginRowResizeAt", RowEdge(ed, tb, 0))!);
    });

    // ---- a drag belongs to the document it started in -----------------------------------------------

    // Measured: a document opened while a drag was held came up modified, with an undo step, on the first
    // pointer move — the drag wrote into the OLD table (invisibly) and checkpointed the NEW document.
    [Fact]
    public void OpeningADocument_MidDrag_EndsTheDrag() => Hosted(ed =>
    {
        Load(ed, Around(Table(1, 3, 100)));
        var old = TopTable(ed);
        var p = ColumnEdge(ed, old, 0);
        Assert.True(PressColumn(ed, p));

        Load(ed, Around(Table(1, 3, 100)));
        Assert.False((bool)Field(ed, "_resizingColumn")!);
        MoveColumn(ed, p, 30); // what a PointerMoved would still have done had the flag survived

        Assert.False(ed.IsModified);
        Assert.False(ed.CanUndo);
        Assert.Equal(new[] { 100.0, 100, 100 }, TopTable(ed).ColumnWidths);
        Assert.Equal(new[] { 100.0, 100, 100 }, old.ColumnWidths);
    });

    // Undo swaps in a snapshot the same way, so a Ctrl+Z pressed mid-drag ends it too — the drag went on
    // writing into the table the undo had just replaced, doing nothing the user could see.
    [Fact]
    public void Undo_MidDrag_EndsTheDrag_AndKeepsRedo() => Hosted(ed =>
    {
        Load(ed, Around(Table(1, 3, 100)));
        var p = ColumnEdge(ed, TopTable(ed), 0);
        Assert.True(PressColumn(ed, p));
        MoveColumn(ed, p, 30);
        ed.Undo();

        Assert.False((bool)Field(ed, "_resizingColumn")!);
        Assert.True(ed.CanRedo);
        Assert.Equal(new[] { 100.0, 100, 100 }, TopTable(ed).ColumnWidths);
    });

    // A lost capture — no release ever reaches the canvas — ends every drag, or the next plain hover goes on
    // resizing. The handler ignores its arguments, which is what lets a test call it.
    [Fact]
    public void ALostCapture_EndsEveryDrag() => Hosted(ed =>
    {
        Load(ed, Around(Table(2, 2, 100)));
        var tb = TopTable(ed);
        void Lose() => Call(ed, "OnCanvasPointerCaptureLost", null, null);

        Assert.True(PressColumn(ed, ColumnEdge(ed, tb, 0)));
        Lose();
        Assert.False((bool)Field(ed, "_resizingColumn")!);
        Assert.False((bool)Field(ed, "_dragUndoPending")!);

        Assert.True((bool)Call(ed, "BeginRowResizeAt", RowEdge(ed, tb, 0))!);
        Lose();
        Assert.False((bool)Field(ed, "_resizingRow")!);

        T.GetField("_resizingImage", NP)!.SetValue(ed, new ImageBlock());
        Lose();
        Assert.Null(Field(ed, "_resizingImage"));

        T.GetField("_isSelecting", NP)!.SetValue(ed, true);
        Lose();
        Assert.False((bool)Field(ed, "_isSelecting")!);

        Assert.False(ed.IsModified); // nothing moved, nothing written
    });

    // ---- a table inside a cell stays inside it ------------------------------------------------------

    // Measured: the nested table's last column dragged +300 made it 480 wide in a 190px content box, drawn
    // through the neighbouring cell. Upstream caps it (EnclosingCellInnerWidth); the port did not.
    [Fact]
    public void ANestedTable_GrowsOnlyToItsCellsContentBox() => Hosted(ed =>
    {
        var (outer, inner) = Nested(ed, 90, 90);
        DragColumn(ed, inner, 1, 300);
        Assert.Equal(190, inner.ColumnWidths.Sum(), 3);

        Draw(ed);
        double cellContentRight = ColumnEdge(ed, outer, 0).X - 5; // the cell border minus CellPad
        Assert.True(ColumnEdge(ed, inner, 1).X <= cellContentRight + 0.01,
            $"inner right edge {ColumnEdge(ed, inner, 1).X} past the cell's content box {cellContentRight}");
    });

    [Fact]
    public void ANestedTable_StillShrinks() => Hosted(ed =>
    {
        var (_, inner) = Nested(ed, 90, 90);
        DragColumn(ed, inner, 1, -40);
        Assert.Equal(new[] { 90.0, 50 }, inner.ColumnWidths);
    });

    // A table that already overflows (a file says so) keeps its width when grabbed: upstream's cap would snap
    // its last column down to the room left (here nothing — the floor, 20) on the first pixel of movement.
    [Fact]
    public void AnOverflowingNestedTable_DoesNotSnapWhenGrabbed_ButShrinks() => Hosted(ed =>
    {
        var (_, inner) = Nested(ed, 150, 150);
        var p = ColumnEdge(ed, inner, 1);
        Assert.True(PressColumn(ed, p));
        MoveColumn(ed, p, 1);
        Assert.Equal(150, inner.ColumnWidths[1], 3);
        MoveColumn(ed, p, 100);
        Assert.Equal(150, inner.ColumnWidths[1], 3);
        MoveColumn(ed, p, -30);
        Assert.Equal(120, inner.ColumnWidths[1], 3);
        ReleaseColumn(ed);
    });

    // The same overflow for an inline table in a cell's paragraph (measured: 420 wide in the same cell) —
    // upstream's cap only looks at a table whose parent is the cell itself, so it has this one too.
    [Fact]
    public void AnInlineTableInACell_GrowsOnlyToTheParagraphsWidth() => Hosted(ed =>
    {
        var outer = Table(1, 2, 200);
        var host = new Paragraph();
        host.Inlines.Add(new Run { Text = "x" });
        host.Inlines.Add(new InlineTable { Table = Table(1, 2, 60) });
        outer.Cells[0][0].Blocks.Clear();
        outer.Cells[0][0].Blocks.Add(host);
        Load(ed, Around(outer));
        var h = (Paragraph)TopTable(ed).Cells[0][0].Blocks[0];
        var inner = h.Inlines.OfType<InlineTable>().Single().Table;

        DragColumn(ed, inner, 1, 300);
        double room = (double)Call(ed, "ParagraphWrapWidth", h)!;
        Assert.Equal(room, inner.ColumnWidths.Sum(), 3);
    });

    // The cap is for tables inside cells only: a top-level table may run past the margin (Word and upstream).
    [Fact]
    public void ATopLevelTable_IsNotCapped() => Hosted(ed =>
    {
        Load(ed, Around(Table(1, 2, 100)));
        DragColumn(ed, TopTable(ed), 1, 900);
        Assert.Equal(1000, TopTable(ed).ColumnWidths[1], 3);
    });

    // ---- an inline table's line follows the drag --------------------------------------------------------

    // Upstream measured an inline table resized mid-drag keeping its old line box (80 -> 80 until the next
    // edit). Here the host paragraph's signature covers the table's widths, so the text after it moves with
    // the drag, before any release.
    [Fact]
    public void AnInlineTable_PushesTheTextAfterIt_MidDrag() => Hosted(ed =>
    {
        var host = new Paragraph();
        host.Inlines.Add(new Run { Text = "before" });
        host.Inlines.Add(new InlineTable { Table = Table(1, 2, 60) });
        host.Inlines.Add(new Run { Text = "after" });
        var doc = new FlowDocument();
        doc.Blocks.Add(host);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "end" } } });
        Load(ed, doc);
        var h = (Paragraph)ed.Document!.Blocks[0];
        var inner = h.Inlines.OfType<InlineTable>().Single().Table;
        double AfterX()
        {
            object? boxed = Call(ed, "CaretToDocPoint", new TextPointer(h, "before".Length + 2));
            return (double)boxed!.GetType().GetField("Item1")!.GetValue(boxed)!;
        }

        double x0 = AfterX();
        var p = ColumnEdge(ed, inner, 1);
        Assert.True(PressColumn(ed, p));
        MoveColumn(ed, p, 100);
        Draw(ed);
        Assert.Equal(x0 + 100, AfterX(), 1);
        ReleaseColumn(ed);
    });
}
