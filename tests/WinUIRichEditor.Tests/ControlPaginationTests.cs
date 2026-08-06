using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Pagination: where the page breaks fall, and how big the paper is.
/// <para>The break walk is the one piece of layout the user sees as a hard promise — a line or a table
/// row must never be sliced across a page boundary — and it feeds both the on-screen page view and
/// printing. <c>ComputePageBreaks</c> takes the page box as parameters, so the whole algorithm can be
/// driven at any page size without resizing anything.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlPaginationTests
{
    private const BindingFlags Any = BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    // One editor, hosted once: the break walk measures paragraphs with a real text layout, which needs
    // the control in a visual tree. See UiThread for why the window is never closed.
    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor
        {
            Document = new FlowDocument(),
            PageSize = RichEditorPageSize.A4,
        });
        UiThread.Host(ed);
        return ed;
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static FlowDocument Paragraphs(int count)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < count; i++)
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"{i}: a line of ordinary text" } } });
        return doc;
    }

    private static List<double> Breaks(RichEditor ed, double contentWidth, double pageHeight)
        => (List<double>)T.GetMethod("ComputePageBreaks", Any)!.Invoke(ed, new object[] { contentWidth, pageHeight })!;

    private static void With(FlowDocument doc, Action<RichEditor> body)
    {
        var ed = Shared.Value;
        UiThread.Run(() => { ed.Document = doc; body(ed); });
    }

    // ---- paper geometry (no layout involved) --------------------------------------------------------

    [Theory]
    [InlineData(RichEditorPageSize.A4, 794, 1123)]
    [InlineData(RichEditorPageSize.A3, 1123, 1587)]
    [InlineData(RichEditorPageSize.A5, 559, 794)]
    [InlineData(RichEditorPageSize.Letter, 816, 1056)]
    [InlineData(RichEditorPageSize.Legal, 816, 1344)]
    [InlineData(RichEditorPageSize.Tabloid, 1056, 1632)]
    public void PaperPixelSize_MatchesTheStandardSizeAt96Dpi(RichEditorPageSize size, double w, double h)
        => With(new FlowDocument(), ed =>
        {
            ed.PageSize = size;
            ed.PageOrientation = RichEditorPageOrientation.Portrait;
            var paper = ed.GetPaperPixelSize();
            Assert.Equal(w, paper.Width, 0);
            Assert.Equal(h, paper.Height, 0);
        });

    [Fact]
    public void LandscapeSwapsThePaperDimensions()
        => With(new FlowDocument(), ed =>
        {
            ed.PageSize = RichEditorPageSize.A4;
            ed.PageOrientation = RichEditorPageOrientation.Portrait;
            var portrait = ed.GetPaperPixelSize();
            ed.PageOrientation = RichEditorPageOrientation.Landscape;
            var landscape = ed.GetPaperPixelSize();
            ed.PageOrientation = RichEditorPageOrientation.Portrait; // shared editor: put it back

            Assert.Equal(portrait.Height, landscape.Width, 0);
            Assert.Equal(portrait.Width, landscape.Height, 0);
        });

    // Continuous has no paper, but hosts still ask — print math and fit-to-width need a number, so it
    // reports the A4 fallback rather than zero.
    [Fact]
    public void ContinuousReportsTheA4Fallback()
        => With(new FlowDocument(), ed =>
        {
            ed.PageSize = RichEditorPageSize.A4;
            var a4 = ed.GetPaperPixelSize();
            ed.PageSize = RichEditorPageSize.Continuous;
            Assert.Equal(a4, ed.GetPaperPixelSize());
            ed.PageSize = RichEditorPageSize.A4;
        });

    // ---- where the breaks fall -----------------------------------------------------------------------

    // The two invariants that make a page break correct, on the same walk:
    //   · strictly increasing — every page holds at least one atom, so no zero-height page and no loop;
    //   · no page taller than the page box — nothing is pushed past the paper it belongs on.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(60)]
    [InlineData(200)]
    public void PageBreaks_AdvanceAndNeverOverfillAPage(int paragraphs)
        => With(Paragraphs(paragraphs), ed =>
        {
            const double width = 698, pageHeight = 1043;
            var breaks = Breaks(ed, width, pageHeight);

            Assert.NotEmpty(breaks);
            Assert.Equal(0, breaks[0]);
            for (int i = 1; i < breaks.Count; i++)
            {
                Assert.True(breaks[i] > breaks[i - 1],
                    $"page {i} starts at or before page {i - 1} ({breaks[i - 1]} -> {breaks[i]})");
                Assert.True(breaks[i] - breaks[i - 1] <= pageHeight + 0.5,
                    $"page {i - 1} holds {breaks[i] - breaks[i - 1]:0.0}px, more than the {pageHeight}px page");
            }
        });

    // A single ATOM taller than the whole page cannot be split — the walk has to place it anyway and move
    // on. Getting this wrong is not cosmetic: a break emitted at the page's own start never advances, and
    // the page list grows without the walk ever moving.
    //
    // It has to be a genuinely oversized atom. A tall TABLE does not qualify — its atoms are ROWS, each
    // of them small — which is why the first version of this test passed against a deliberately removed
    // advance guard. A block image is one atom of whatever height it is given.
    [Fact]
    public void AnAtomTallerThanThePage_StillAdvances()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "before" } } });
        doc.Blocks.Add(new ImageBlock { Width = 300, Height = 900 });   // one atom, 4.5x the page below
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });

        With(doc, ed =>
        {
            var breaks = Breaks(ed, 698, 200);
            Assert.True(breaks.Count > 1, "a document far taller than the page produced a single page");
            for (int i = 1; i < breaks.Count; i++)
                Assert.True(breaks[i] > breaks[i - 1],
                    $"page break {i} did not advance ({breaks[i - 1]} -> {breaks[i]})");
            Assert.Equal(breaks.Count, breaks.Distinct().Count());
        });
    }

    // An oversized atom must not open a page at the position that page already starts at — that would be
    // a duplicate break, a page holding nothing.
    //
    // ⚠ KNOWN LIMIT: this does NOT cover the `y > pageStart` advance guard in PlaceAtom. Removing that
    // guard was tried against an oversized first paragraph, an oversized image, a tall table, and a
    // one-pixel page, and this suite stayed green through all four — by the time the first atom is
    // placed, `y` has already moved past the page start, so the guard is never the thing that decides.
    // Reaching it needs a case none of those produce. Counting pages is not a substitute: block
    // normalization's own leading and trailing paragraphs make extra page starts legitimate, and a
    // count-based version of this test failed against correct code as readily as against broken code.
    // What is asserted below is real and would catch a walk that stalls or repeats; the guard itself is
    // simply not yet discriminated by anything here.
    [Fact]
    public void AnOversizedFirstAtom_DoesNotOpenAPageAtItsOwnStart()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { LineSpacing = 20.0, Inlines = { new Run { Text = "one very tall line" } } });
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });

        With(doc, ed =>
        {
            // A one-pixel page: EVERY atom overflows, so the very first one is placed at y == pageStart
            // and the guard is the only thing standing between that and a page start at 0 twice over.
            var breaks = Breaks(ed, 698, 1);
            Assert.Equal(0, breaks[0]);
            Assert.Equal(breaks.Count, breaks.Distinct().Count());
            for (int i = 1; i < breaks.Count; i++)
                Assert.True(breaks[i] > breaks[i - 1],
                    $"page break {i} did not advance ({breaks[i - 1]} -> {breaks[i]})");
        });
    }

    // More content means more pages; a bigger page means fewer. Both directions, because a count that is
    // simply constant would satisfy either one alone.
    [Fact]
    public void PageCount_GrowsWithContent_AndShrinksWithABiggerPage()
    {
        int few = 0, many = 0, manyOnATallPage = 0;
        With(Paragraphs(20), ed => few = Breaks(ed, 698, 1043).Count);
        With(Paragraphs(400), ed =>
        {
            many = Breaks(ed, 698, 1043).Count;
            manyOnATallPage = Breaks(ed, 698, 4000).Count;
        });

        Assert.True(many > few, $"400 paragraphs did not need more pages than 20 ({few} vs {many})");
        Assert.True(manyOnATallPage < many, $"a 4x taller page did not reduce the count ({many} vs {manyOnATallPage})");
    }

    // Recomputing must give the same answer — the walk caches per-paragraph line metrics, and a cache
    // that answered differently on the second call would make the page view and the printout disagree.
    [Fact]
    public void PageBreaks_AreStableAcrossRecomputation()
        => With(Paragraphs(200), ed =>
        {
            var first = Breaks(ed, 698, 1043);
            var second = Breaks(ed, 698, 1043);
            Assert.Equal(first, second);
        });

    // ---- the public count ----------------------------------------------------------------------------

    // An empty document is still one page — a printout of nothing is a blank sheet, not zero sheets, and
    // the status bar has to show something.
    [Fact]
    public void PrintPageCount_IsAtLeastOne_EvenWhenEmpty()
        => With(new FlowDocument(), ed => Assert.True(ed.GetPrintPageCount() >= 1));

    [Fact]
    public void PrintPageCount_TracksTheDocument()
    {
        int one = 0, several = 0;
        With(Paragraphs(5), ed => one = ed.GetPrintPageCount());
        With(Paragraphs(400), ed => several = ed.GetPrintPageCount());

        Assert.Equal(1, one);
        Assert.True(several > one, $"400 paragraphs still reported {several} page(s)");
    }
}
