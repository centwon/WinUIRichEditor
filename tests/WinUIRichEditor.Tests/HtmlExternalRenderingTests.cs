using System;
using System.Linq;
using WinUIRichEditor.Documents;
using Xunit;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Tests;

/// <summary>Two HTML defects only an OUTSIDE renderer shows, ported from upstream round 9 (AvaloniaRichEditor
/// <c>HtmlExternalRenderingTests</c>, 2026-08-08) on 2026-09-24 — the port had neither fix. Both round-trip
/// through this project's own reader perfectly, because the reader is lenient about exactly what its own writer
/// emits; upstream found them by measuring the output in a browser. So these assertions read the WRITTEN
/// MARKUP, not just the model — a symmetry check cannot see a line break that was never written.</summary>
public class HtmlExternalRenderingTests
{
    private static string Text(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private static FlowDocument Doc(params Block[] blocks)
    {
        var doc = new FlowDocument();
        doc.Blocks.AddRange(blocks);
        return doc;
    }

    private static Paragraph P(string? text)
    {
        var p = new Paragraph();
        if (text != null) p.Inlines.Add(new Run { Text = text });
        return p;
    }

    // The markup between a marked element's opening and its closing tag.
    private static string MarkedElement(string html, string closeTag)
    {
        int at = html.IndexOf("data-are-empty", StringComparison.Ordinal);
        Assert.True(at >= 0, "the blank line must still be marked for this reader");
        return html.Substring(at, html.IndexOf(closeTag, at, StringComparison.Ordinal) - at);
    }

    // ---- 1. An author's blank line was invisible outside this editor ------------------------------

    // The blank line went out as `<p data-are-empty="1"></p>`. The marker told THIS reader about it, but an
    // element with no content has zero height, so a browser showed nothing (upstream measured: the gap across
    // the blank line was the same 16px as between any two paragraphs). The <br> is what gives it a line —
    // and it has to be INSIDE the marked element, which is what gives that element height.
    [Fact]
    public void AnAuthorsBlankLine_IsGivenALineForOutsideRenderers()
    {
        string html = HtmlDocumentFormatter.ToHtml(Doc(P("위"), P(null), P("아래")));

        Assert.Contains("<br", MarkedElement(html, "</p>"));
    }

    // The same for an empty list item: a blank numbered item is a zero-height <li> too.
    [Fact]
    public void AnEmptyListItem_IsGivenALineForOutsideRenderers()
    {
        var blank = P(null);
        blank.ListType = ListKind.Ordered;
        var a = P("a"); a.ListType = ListKind.Ordered;
        var b = P("b"); b.ListType = ListKind.Ordered;

        string html = HtmlDocumentFormatter.ToHtml(Doc(a, blank, b));

        Assert.Contains("<br", MarkedElement(html, "</li>"));
    }

    // The <br> is the blank line's rendering, not its content. Read back as content, every save/load turns
    // one blank line into a line holding a break — two lines. Twice, as round trips are run here.
    [Fact]
    public void AnAuthorsBlankLine_DoesNotGrowOnRepeatedRoundTrips()
    {
        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(Doc(P("위"), P(null), P("아래"))));
        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));

        foreach (var round in new[] { once, twice })
        {
            Assert.Equal(3, round.Blocks.Count);
            // Not just "no text": a <br> read as content leaves a run holding "\n", which renders as a SECOND
            // line inside the one blank paragraph.
            Assert.DoesNotContain(((Paragraph)round.Blocks[1]).Inlines.OfType<Run>(), r => !string.IsNullOrEmpty(r.Text));
        }
    }

    // Foreign EMPTY elements keep being dropped — web pages use empty <p>/<div> for spacing, and honouring
    // them gives every paste that page's vertical rhythm. Only the marker opts in.
    [Fact]
    public void AForeignEmptyParagraph_IsStillDropped()
    {
        var doc = HtmlDocumentFormatter.ParseHtml("<p>a</p><p></p><p>b</p>");

        Assert.Equal(2, doc.Blocks.Count);
    }

    // A foreign `<p><br></p>` is NOT empty and never was dropped — it is what contenteditable editors write for
    // a blank line. It is why the new <br> is safe: a consumer that strips data- attributes still gets the
    // blank line, and this reader only needs the marker to know the <br> is not content of its own.
    [Fact]
    public void AForeignBrOnlyParagraph_IsStillABlankLine()
    {
        var doc = HtmlDocumentFormatter.ParseHtml("<p>a</p><p><br></p><p>b</p>");

        Assert.Equal(3, doc.Blocks.Count);
        Assert.Equal("", Text((Paragraph)doc.Blocks[1]).Trim());
    }

    // ---- 2. A picture in a cell sat beside the text instead of under it ---------------------------

    private static TableBlock CaptionAndPicture()
    {
        var t = new TableBlock(1, 1);
        var cell = t.Cells[0][0];
        cell.Blocks.Clear();
        cell.Blocks.Add(P("캡션"));
        var pic = new ImageBlock { Width = 80, Height = 50 };
        pic.SetImageData(OnePixelPng, "image/png");
        cell.Blocks.Add(pic);
        return t;
    }

    private static string CellHtml(string html)
    {
        int td = html.IndexOf("<td", StringComparison.Ordinal);
        return html.Substring(td, html.IndexOf("</td>", td, StringComparison.Ordinal) - td);
    }

    // A cell's paragraph keeps the bare-inline form when that form can represent it, and the rule counted
    // PARAGRAPHS. A cell holding one paragraph and a block image has only one, so the paragraph went out bare —
    // and <img> is inline, so the picture landed on the caption's line. <hr> and <table> are block-level and
    // break the line themselves; only an image forces this.
    [Fact]
    public void APictureInACell_StartsItsOwnLineInsteadOfJoiningTheText()
    {
        string cellHtml = CellHtml(HtmlDocumentFormatter.ToHtml(Doc(CaptionAndPicture())));

        Assert.Contains("<p", cellHtml);
        Assert.True(cellHtml.IndexOf("<p", StringComparison.Ordinal) < cellHtml.IndexOf("<img", StringComparison.Ordinal),
                    "the caption's element must come before the picture");
    }

    // The promotion must not change what comes back: still a paragraph and an image, still two blocks.
    [Fact]
    public void APictureInACell_StillRoundTripsAsTwoBlocks()
    {
        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(Doc(CaptionAndPicture())));
        var cell = back.Blocks.OfType<TableBlock>().Single().Cells[0][0];

        Assert.Equal("캡션", Text(cell.Blocks.OfType<Paragraph>().First()));
        Assert.Single(cell.Blocks.OfType<ImageBlock>());
    }

    // A plain one-paragraph cell keeps the bare form — the reason the rule exists; the whitespace behaviour
    // earned there depends on those exact bytes.
    [Fact]
    public void APlainCell_KeepsItsBareForm()
    {
        var t = new TableBlock(1, 1);
        t.Cells[0][0].Para.Inlines[0] = new Run { Text = "평문" };

        Assert.DoesNotContain("<p", CellHtml(HtmlDocumentFormatter.ToHtml(Doc(t))));
    }

    // 1x1 transparent PNG.
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
}
