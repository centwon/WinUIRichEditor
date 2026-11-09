using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Graphics.Canvas;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>A table row that does not fit at the bottom of a page moves to the next page — and was drawn on BOTH: each page
/// was clipped to its whole content box, so the room the moved row left at the bottom of the first page showed the top of
/// the next page's content (2026-11-08, screen; printing and PDF the same). Each page is now clipped to its own
/// slice of the document, as on the Avalonia control.</summary>
[Collection(UiTests.Collection)]
public class ControlPageSliceTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { ViewMode = RichEditorViewMode.Page, PageSize = RichEditorPageSize.A4 });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static object? Call(RichEditor ed, string name, params object?[] args) => T.GetMethod(name, NP)!.Invoke(ed, args);

    // A first table filling the page but ~200 DIP, then a 300-DIP row that cannot fit and moves to page 2.
    private static (RichEditor ed, double sliceH) Load()
    {
        var ed = Shared.Value;
        double sliceH = 0;
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var filler = new TableBlock(1, 1);
            filler.RowHeights.Clear(); filler.RowHeights.Add(ed.PaperContentHeight - 200);
            var moved = new TableBlock(1, 2);
            moved.RowHeights.Clear(); moved.RowHeights.Add(300);
            doc.Blocks.Add(filler);
            doc.Blocks.Add(moved);
            ed.LoadJson(DocumentSerializer.Serialize(doc));
            Call(ed, "RelayoutToViewport");
            var breaks = (List<double>)Call(ed, "EnsurePageBreaks")!;
            Assert.True(breaks.Count >= 2, "the second table should start a second page");
            sliceH = breaks[1] - breaks[0];
            Assert.True(ed.PaperContentHeight - sliceH > 40, $"page 1's slice {sliceH} leaves no room to look at");
        });
        return (ed, sliceH);
    }

    // BGRA; anything not white paper in the rectangle, inset 2 px on each side.
    private static int Ink(byte[] px, int width, double left, double right, double top, double bottom)
    {
        int ink = 0;
        for (int y = (int)top + 2; y < (int)bottom - 2; y++)
            for (int x = (int)left + 2; x < (int)right - 2; x++)
            {
                int o = (y * width + x) * 4;
                if (px[o] < 240 || px[o + 1] < 240 || px[o + 2] < 240) ink++;
            }
        return ink;
    }

    [Fact]
    public void OnScreen_TheRoomBelowAShortSliceIsBlank()
    {
        var (ed, sliceH) = Load(); // outside Run: hosting the shared editor waits on the UI thread
        UiThread.Run(() =>
        {
            double left = (double)T.GetProperty("PageDeskX", NP)!.GetValue(ed)! + ed.PagePadLeft;
            double top = RichEditor.PageGap + ed.PagePadTop; // page 1's content box, view space at 100 %
            // Selected, the moved table drew its frame into the room too, and its top edge as a 1-px line at the slice's end.
            T.GetField("_selectedBlock", NP)!.SetValue(ed, ed.Document!.Blocks.OfType<TableBlock>().Last());
            using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1400, 1400, 96);
            using (var ds = rt.CreateDrawingSession()) Call(ed, "DrawDocument", ds, new Rect(0, 0, 1400, 1400));
            var px = rt.GetPixelBytes();
            int w = (int)rt.SizeInPixels.Width;
            Assert.True(Ink(px, w, left, left + ed.PaperContentWidth, top, top + sliceH) > 100, "page 1 drew nothing — vacuous");
            Assert.Equal(0, Ink(px, w, left, left + ed.PaperContentWidth, top + sliceH - 5, top + ed.PaperContentHeight));
            T.GetField("_selectedBlock", NP)!.SetValue(ed, null);
        });
    }

    // A click in that room is page 1's last line. Mapped to the slice's very end it landed on the moved table's top-edge grab
    // zone, and the room acted like the table.
    [Fact]
    public void AClickInTheRoom_DoesNotReachTheMovedTable()
    {
        var (ed, sliceH) = Load();
        UiThread.Run(() =>
        {
            double left = (double)T.GetProperty("PageDeskX", NP)!.GetValue(ed)! + ed.PagePadLeft;
            double top = RichEditor.PageGap + ed.PagePadTop;
            // Drawn first: the tables' grab geometry is recorded as they draw.
            using (var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1400, 2400, 96))
            using (var ds = rt.CreateDrawingSession()) Call(ed, "DrawDocument", ds, new Rect(0, 0, 1400, 2400));
            var doc = (Point)Call(ed, "ViewToDoc", new Point(left + 30, top + sliceH + 40))!;
            Assert.False((bool)Call(ed, "TrySelectTableBlock", doc)!, $"a click in the room at doc {doc} selected the moved table");
            T.GetField("_selectedBlock", NP)!.SetValue(ed, null);
        });
    }

    [Fact]
    public void Printed_TheRoomBelowAShortSliceIsBlank()
    {
        var (ed, sliceH) = Load();
        UiThread.Run(() =>
        {
            using var page = ed.RenderPrintPage(0); // 96 DPI: paper DIPs = pixels
            var px = page.GetPixelBytes();
            int w = (int)page.SizeInPixels.Width;
            double left = ed.PagePadLeft, top = ed.PagePadTop;
            Assert.True(Ink(px, w, left, left + ed.PaperContentWidth, top, top + sliceH) > 100, "page 1 printed nothing — vacuous");
            Assert.Equal(0, Ink(px, w, left, left + ed.PaperContentWidth, top + sliceH - 5, top + ed.PaperContentHeight));
        });
    }
}
