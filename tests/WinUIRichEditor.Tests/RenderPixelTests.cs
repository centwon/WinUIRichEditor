using System;
using System.Collections.Generic;
using Windows.UI;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The first tests in this repo that look at PIXELS.
/// <para>The gap they close was found the way the roadmap predicted it would be: by a human looking at
/// the demo. Every format now round-trips a paragraph fill inside a table cell, the model kept it, and
/// the cell RENDERED it as nothing — <c>DrawCellBlockList</c> drew run backgrounds, list markers,
/// highlights and text, and never the paragraph's own fill or its quote bar, both of which the top-level
/// block loop has always drawn. No model or formatter test can see that, because nothing about the model
/// is wrong.</para>
/// <para>The way in is <see cref="RichEditor.RenderPrintPage"/>: it renders to an offscreen Win2D target
/// on <c>CanvasDevice.GetSharedDevice()</c>, so it needs the UI thread but no visual tree — which is what
/// makes a pixel assertion possible here at all (a <c>Measure</c> outside a visual tree kills the
/// process).</para>
/// </summary>
[Collection(UiTests.Collection)]
public class RenderPixelTests
{
    private static readonly Color Fill = Color.FromArgb(255, 255, 0, 0);       // pure red, in no theme
    private static readonly Color QuoteBar = Microsoft.UI.Colors.Silver; // == RichEditor.QuoteBarColor

    private static Paragraph P(string text) => new() { Inlines = { new Run { Text = text } } };

    // One 1x1 table whose only cell holds `inner`.
    private static FlowDocument DocWithCell(Paragraph inner)
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 1);
        tb.Cells[0][0].Blocks.Clear();
        tb.Cells[0][0].Blocks.Add(inner);
        doc.Blocks.Add(tb);
        return doc;
    }

    // How many pixels of the rendered first page are (approximately) this colour. Approximate because the
    // target is premultiplied BGRA and the page is composited — an exact match would be brittle for a
    // reason that has nothing to do with what is being tested.
    private static int CountColor(FlowDocument doc, Color want)
    {
        return UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = doc };
            using var rt = ed.RenderPrintPage(0, 96);
            var px = rt.GetPixelColors();
            int n = 0;
            foreach (var c in px)
                if (Math.Abs(c.R - want.R) <= 8 && Math.Abs(c.G - want.G) <= 8 && Math.Abs(c.B - want.B) <= 8)
                    n++;
            return n;
        });
    }

    [Fact]
    public void AParagraphFillInsideACell_IsActuallyPainted()
    {
        var filled = P("filled");
        filled.Background = Fill;

        int painted = CountColor(DocWithCell(filled), Fill);
        // A cell's paragraph box is far bigger than a handful of pixels; the threshold only has to be
        // clear of antialiasing noise from the glyphs and the cell border.
        Assert.True(painted > 500, $"expected a filled band inside the cell, found {painted} red pixels");
    }

    // The control: an identical document with no fill must produce none of that colour, so the assertion
    // above cannot pass on something else that happens to be red.
    [Fact]
    public void WithoutAFill_NothingIsPainted()
    {
        Assert.True(CountColor(DocWithCell(P("plain")), Fill) < 50);
    }

    [Fact]
    public void AQuoteInsideACell_DrawsItsBar()
    {
        var quoted = P("quoted");
        quoted.IsQuote = true;

        int bar = CountColor(DocWithCell(quoted), QuoteBar);
        int none = CountColor(DocWithCell(P("quoted")), QuoteBar);
        // The bar is 3px wide by the paragraph's height, so it is small — compared against the same
        // document without the flag rather than against an absolute threshold.
        Assert.True(bar > none + 20, $"expected a quote bar in the cell: with={bar}, without={none}");
    }

    // The top-level path was never broken; pinning it keeps a future refactor from "fixing" the cell path
    // by moving the fill out of the loop that still works.
    [Fact]
    public void AParagraphFillAtTheTopLevel_IsStillPainted()
    {
        var doc = new FlowDocument();
        var p = P("top level");
        p.Background = Fill;
        doc.Blocks.Add(p);

        Assert.True(CountColor(doc, Fill) > 500);
    }
}
