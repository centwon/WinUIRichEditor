using System.IO;
using System.Linq;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

public class FormatterRoundTripTests
{
    // Plain struct values so tests construct without activating the WinUI runtime (no WinRT statics).
    private static readonly FontWeight Bold = new() { Weight = 700 };
    private static readonly Color Red = new() { A = 255, R = 255, G = 0, B = 0 };

    private static FlowDocument Sample()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph
        {
            HeadingLevel = 1,
            Inlines = { new Run { Text = "Title" } },
        });
        doc.Blocks.Add(new Paragraph
        {
            TextAlignment = TextAlignment.Center,
            Inlines =
            {
                new Run { Text = "Hello ", FontWeight = Bold },
                new Run { Text = "world", FontStyle = FontStyle.Italic, Foreground = Red },
            },
        });
        return doc;
    }

    private static string Plain(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    [Fact]
    public void Json_RoundTrips_TextHeadingAlignmentAndRuns()
    {
        var json = DocumentSerializer.Serialize(Sample());
        var back = DocumentSerializer.Deserialize(json);

        Assert.Equal(2, back.Blocks.Count);
        var title = Assert.IsType<Paragraph>(back.Blocks[0]);
        Assert.Equal(1, title.HeadingLevel);
        Assert.Equal("Title", Plain(title));

        var body = Assert.IsType<Paragraph>(back.Blocks[1]);
        Assert.Equal(TextAlignment.Center, body.TextAlignment);
        Assert.Equal("Hello world", Plain(body));
        var runs = body.Inlines.OfType<Run>().ToList();
        Assert.True(runs[0].FontWeight.IsBold());
        Assert.Equal(FontStyle.Italic, runs[1].FontStyle);
        Assert.Equal(Red, runs[1].Foreground);
    }

    [Fact]
    public void Html_RoundTrips_PlainTextAndBold()
    {
        var html = HtmlDocumentFormatter.ToHtml(Sample());
        var back = HtmlDocumentFormatter.ParseHtml(html);

        Assert.NotEmpty(back.Blocks);
        var allText = string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain));
        Assert.Contains("Title", allText);
        Assert.Contains("Hello world", allText);
        // Some run in the document must be bold after the HTML round-trip.
        Assert.Contains(back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()),
            r => r.FontWeight.IsBold());
    }

    [Fact]
    public void Rtf_LooksLikeRtf_And_RoundTripsText()
    {
        var rtf = RtfDocumentFormatter.Write(Sample());
        Assert.True(RtfDocumentFormatter.LooksLikeRtf(rtf));
        Assert.False(RtfDocumentFormatter.LooksLikeRtf("plain text"));

        var back = RtfDocumentFormatter.Parse(rtf);
        var allText = string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain));
        Assert.Contains("Title", allText);
        Assert.Contains("world", allText);
    }

    // A 2×1 table whose cells each hold two paragraphs — the reported copy/paste scenario. Verifies the
    // clipboard formats a fall-through paste might use (HTML, RTF) keep BOTH rows.
    private static TableBlock TwoRowSingleColumn()
    {
        var tb = new TableBlock(2, 1);
        for (int r = 0; r < 2; r++)
        {
            tb.Cells[r][0].Blocks.Clear();
            tb.Cells[r][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"top-r{r}" } } });
            tb.Cells[r][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"bot-r{r}" } } });
        }
        return tb;
    }

    [Fact]
    public void Html_RoundTrips_TwoRowSingleColumnTable()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(TwoRowSingleColumn());
        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var tb = back.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, tb.Rows);
        Assert.Equal(1, tb.Columns);
        var r0 = string.Concat(tb.Cells[0][0].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(x => x.Text));
        var r1 = string.Concat(tb.Cells[1][0].Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(x => x.Text));
        Assert.Contains("top-r0", r0);
        Assert.Contains("top-r1", r1);
    }

    [Fact]
    public void Rtf_RoundTrips_TwoRowSingleColumnTable()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(TwoRowSingleColumn());
        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var tb = back.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, tb.Rows);
        Assert.Equal(1, tb.Columns);
    }

    [Fact]
    public void FlowPackage_RoundTrips()
    {
        using var ms = new MemoryStream();
        DocumentPackage.Save(Sample(), ms);
        ms.Position = 0;
        var back = DocumentPackage.Load(ms);

        Assert.Equal(2, back.Blocks.Count);
        Assert.Equal("Hello world", Plain((Paragraph)back.Blocks[1]));
    }

    [Fact]
    public void Json_RoundTrips_EmptyDocument()
    {
        var json = DocumentSerializer.Serialize(new FlowDocument());
        var back = DocumentSerializer.Deserialize(json);
        Assert.NotNull(back);
    }

    [Fact]
    public void Json_RoundTrips_PageSetup()
    {
        var doc = Sample();
        doc.PageSetup = new PageSetup
        {
            PageSize = RichEditorPageSize.A4,
            Orientation = RichEditorPageOrientation.Landscape,
            ShowPageBoundaries = true,
            Header = "My header",
            Footer = "My footer",
            ShowPageNumbers = true,
        };

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));

        Assert.NotNull(back.PageSetup);
        Assert.Equal(RichEditorPageSize.A4, back.PageSetup!.PageSize);
        Assert.Equal(RichEditorPageOrientation.Landscape, back.PageSetup.Orientation);
        Assert.True(back.PageSetup.ShowPageBoundaries);
        Assert.Equal("My header", back.PageSetup.Header);
        Assert.Equal("My footer", back.PageSetup.Footer);
        Assert.True(back.PageSetup.ShowPageNumbers);
    }

    [Fact]
    public void Json_PlainDocument_OmitsPageSetup()
    {
        // A Continuous (default) document carries no page info → no "PageSetup" key, null on read.
        var json = DocumentSerializer.Serialize(Sample());
        Assert.DoesNotContain("PageSetup", json);
        Assert.Null(DocumentSerializer.Deserialize(json).PageSetup);
    }

    [Fact]
    public void FlowPackage_RoundTrips_PageSetup()
    {
        var doc = Sample();
        doc.PageSetup = new PageSetup { PageSize = RichEditorPageSize.Letter };

        using var ms = new MemoryStream();
        DocumentPackage.Save(doc, ms);
        ms.Position = 0;
        var back = DocumentPackage.Load(ms);

        Assert.NotNull(back.PageSetup);
        Assert.Equal(RichEditorPageSize.Letter, back.PageSetup!.PageSize);
    }

    [Fact]
    public void Rtf_RoundTrips_FontFamily()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph
        {
            Inlines =
            {
                new Run { Text = "Alpha", FontFamily = "Batang" },
                new Run { Text = "Beta" }, // no family -> stays default on the way back
            },
        });
        var rtf = RtfDocumentFormatter.Write(doc);
        var back = RtfDocumentFormatter.Parse(rtf);

        var runs = back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToList();
        Assert.Contains(runs, r => r.FontFamily == "Batang" && (r.Text ?? "").Contains("Alpha"));
        Assert.Contains(runs, r => string.IsNullOrEmpty(r.FontFamily) && (r.Text ?? "").Contains("Beta"));
    }

    [Fact]
    public void Rtf_Parses_FontTable_WithCp949NameAndBody()
    {
        // Korean font name AND body as \'hh bytes under \fcharset129 (cp949): "굴림" / "안녕". The
        // per-font charset must win over the document's \ansicpg1252 for both.
        string rtf = @"{\rtf1\ansi\ansicpg1252{\fonttbl{\f0\fnil Arial;}{\f1\fnil\fcharset129 \'b1\'bc\'b8\'b2;}}{\f1 \'be\'c8\'b3\'e7}}";
        var back = RtfDocumentFormatter.Parse(rtf);

        var run = back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First();
        Assert.Equal("굴림", run.FontFamily);
        Assert.Equal("안녕", run.Text);
    }

    [Fact]
    public void Rtf_RoundTrips_Hyperlink()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph
        {
            Inlines =
            {
                new Run { Text = "see " },
                new Run { Text = "here", NavigateUri = "https://example.com/a" },
                new Run { Text = " end" },
            },
        });
        var rtf = RtfDocumentFormatter.Write(doc);
        Assert.Contains("HYPERLINK", rtf); // emitted as a real \field, not just underline

        var back = RtfDocumentFormatter.Parse(rtf);
        var runs = back.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).ToList();
        Assert.Contains(runs, r => r.NavigateUri == "https://example.com/a" && (r.Text ?? "").Contains("here"));
        Assert.Contains(runs, r => string.IsNullOrEmpty(r.NavigateUri) && (r.Text ?? "").Contains("see"));
        Assert.Contains(runs, r => string.IsNullOrEmpty(r.NavigateUri) && (r.Text ?? "").Contains("end"));
    }

    // A sub-bullet must stay UNDER the item it belongs to. Our writer emits a deeper level as a <ul>
    // that is a SIBLING of the previous <li>, not a child of it, so the reader has to keep <li> and
    // <ul> in document order — reading all sublists first (which a fix for a different shape once did)
    // lifts every nested item above its parent, and an ordinary two-level list comes back scrambled.
    [Fact]
    public void Html_RoundTrips_NestedListItems_InDocumentOrder()
    {
        static Paragraph Bullet(string text, int level)
        {
            var p = new Paragraph { ListType = ListKind.Bullet, ListLevel = level };
            p.Inlines.Add(new Run { Text = text });
            return p;
        }

        var doc = new FlowDocument();
        doc.Blocks.Add(Bullet("A", 0));
        doc.Blocks.Add(Bullet("B", 1));
        doc.Blocks.Add(Bullet("C", 0));

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var items = back.Blocks.OfType<Paragraph>().ToList();

        Assert.Equal(new[] { "A", "B", "C" }, items.Select(Plain));
        Assert.Equal(new[] { 0, 1, 0 }, items.Select(p => p.ListLevel));
    }

    // The shape that motivated the two-pass version: a document whose ONLY list item is indented, so
    // the export nests <ul><ul><li> with no <li> at the outer level. Its items must still arrive (they
    // used to vanish, and a document made only of them fell through to the raw-markup fallback).
    [Fact]
    public void Html_RoundTrips_ListWhoseOnlyItemIsIndented()
    {
        var doc = new FlowDocument();
        var only = new Paragraph { ListType = ListKind.Bullet, ListLevel = 2 };
        only.Inlines.Add(new Run { Text = "deep" });
        doc.Blocks.Add(only);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var item = Assert.Single(back.Blocks.OfType<Paragraph>());
        Assert.Equal("deep", Plain(item));
        Assert.Equal(2, item.ListLevel);
        Assert.Equal(ListKind.Bullet, item.ListType);
    }

    // Whitespace BETWEEN inline siblings is a word separator and has to survive; the same whitespace
    // before the closing tag is padding and must NOT turn into content. Both directions in one test,
    // because fixing either one alone is what broke the other twice.
    // These are the BLOCK walk (bare inline content with no <p> wrapper) — the path a table cell's
    // contents take, and the one the merged-cell space was lost on. Content inside a <p> takes the
    // inline path instead, which has always kept a trailing space; that is bounded (it never adds a
    // second) and is left as it is, because <p> whitespace is tuned against browser copy and Word paste.
    [Theory]
    [InlineData("<span>a</span> <span>b</span>", "a b")]
    [InlineData("<span>a</span>\n<span>b</span>", "a b")]
    [InlineData("<span>a</span>   <span>b</span>", "a b")]  // collapses to one
    [InlineData("<span>a</span> ", "a")]                     // trailing: padding, not content
    [InlineData("<span>a</span> <span></span>", "a")]        // separator with nothing after it
    [InlineData("<span>a</span> <br/>", "a\n")]              // bare space before a break is padding
    [InlineData("<span>a</span>&nbsp;<br/>", "a \n")]        // one we meant to keep is written &nbsp;
    public void Html_WhitespaceBetweenInlineSiblings_IsASeparatorButNeverTrailingContent(string html, string expected)
    {
        var back = HtmlDocumentFormatter.ParseHtml(html);
        Assert.Equal(expected, string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    // The defect that motivated the separator: MergeCells joins the covered cell's text with a space,
    // the writer emits that as whitespace between two <span>s, and the block walk used to drop it — so
    // a merged cell lost the word boundary on the SECOND round trip (the first still had one run).
    [Fact]
    public void Html_RoundTrips_MergedCellText_KeepsItsWordBoundary()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 2);
        foreach (var (r, c, cell) in tb.LogicalCells())
            ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c}";
        tb.MergeCells(0, 0, 0, 1);
        doc.Blocks.Add(tb);

        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));

        static string CellText(FlowDocument d) => string.Concat(
            d.Blocks.OfType<TableBlock>().SelectMany(t => t.LogicalCells())
             .SelectMany(x => x.cell.Blocks.OfType<Paragraph>()).Select(Plain));

        Assert.Equal("c00 c01", CellText(once));
        Assert.Equal(CellText(once), CellText(twice)); // and it does not grow either
    }

    // Saving an empty document as HTML and reopening it used to put the editor's own tags on screen as
    // body text: the export is `<p style="…"></p>`, the walk yields no block, and the "input was not
    // markup" fallback dumped the source. Plain text must still come through that fallback.
    [Fact]
    public void Html_EmptyDocument_RoundTripsEmpty_NotAsItsOwnMarkup()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { HeadingLevel = 4, Indent = 40 });

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        string text = string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain));
        Assert.DoesNotContain("<", text);
        Assert.Equal("", text);
    }

    [Fact]
    public void Html_PlainTextInput_StillBecomesItsText()
    {
        var back = HtmlDocumentFormatter.ParseHtml("just some text");
        Assert.Equal("just some text", string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    // HTML folds a run of whitespace to one space, so the editor's own consecutive spaces used to be
    // gone on the FIRST save/load. They ride out as alternating space/&nbsp; (what Word emits); the
    // first space of each run stays collapsible so the line can still wrap there.
    [Theory]
    [InlineData("a  b")]
    [InlineData("a   b")]
    [InlineData("a     b")]
    [InlineData("  leading")]
    [InlineData("trailing  ")]
    [InlineData("a  b  c")]
    public void Html_RoundTrips_ConsecutiveSpaces(string text)
    {
        var doc = new FlowDocument();
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        doc.Blocks.Add(p);

        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        Assert.Equal(text, string.Concat(once.Blocks.OfType<Paragraph>().Select(Plain)));

        // And it must not grow on the way back out either.
        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));
        Assert.Equal(text, string.Concat(twice.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    // A run made only of spaces is authored content — MergeCells' join separator is exactly this shape.
    // It used to be indistinguishable from a pretty-printer's indentation once written, so the reader's
    // separator logic ate it: at a paragraph's start there was no previous inline to attach it to, and
    // after a run already ending in a space the de-duplication guard dropped it.
    [Theory]
    [InlineData(true)]   // the space run OPENS the paragraph
    [InlineData(false)]  // it follows a run that already ends in a space
    public void Html_RoundTrips_ARunOfNothingButSpaces(bool atStart)
    {
        var doc = new FlowDocument();
        var p = new Paragraph();
        if (!atStart) p.Inlines.Add(new Run { Text = "before " });
        p.Inlines.Add(new Run { Text = " " });
        p.Inlines.Add(new Run { Text = "after", Foreground = Red });
        doc.Blocks.Add(p);

        string expected = (atStart ? "" : "before ") + " after";
        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        Assert.Equal(expected, string.Concat(once.Blocks.OfType<Paragraph>().Select(Plain)));

        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));
        Assert.Equal(expected, string.Concat(twice.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    [Fact]
    public void Html_ForeignNbsp_IsContent_NotACollapsibleSeparator()
    {
        // A text node of nothing but &nbsp; is content: it must not be mistaken for layout whitespace.
        var back = HtmlDocumentFormatter.ParseHtml("<p>a<span>&nbsp;&nbsp;</span>b</p>");
        Assert.Equal("a  b", string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    // A link keeps the colour the DOCUMENT gave it; a foreign page's anchor colour still yields to the
    // blue rule, because that rule exists to stop dark/white site anchors disappearing in this editor.
    [Fact]
    public void Html_RoundTrips_HyperlinkColour_ButStillOverridesForeignAnchors()
    {
        var orange = new Color { A = 255, R = 0xEE, G = 0x80, B = 0x6B };
        var doc = new FlowDocument();
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "link", NavigateUri = "https://example.com/1", Foreground = orange });
        doc.Blocks.Add(p);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var run = back.Blocks.OfType<Paragraph>().SelectMany(x => x.Inlines.OfType<Run>()).First(r => r.Text == "link");
        Assert.Equal(orange, run.Foreground);
        Assert.Equal("https://example.com/1", run.NavigateUri);

        var foreign = HtmlDocumentFormatter.ParseHtml(
            "<p><a href=\"https://example.com/2\"><span style=\"color:#FFFFFF\">btn</span></a></p>");
        var fr = foreign.Blocks.OfType<Paragraph>().SelectMany(x => x.Inlines.OfType<Run>()).First(r => r.Text == "btn");
        Assert.Equal(Microsoft.UI.Colors.Blue, fr.Foreground);
    }

    // A paragraph can be a list item AND a heading; <li> wins the tag, so the level rides a marker.
    [Fact]
    public void Html_RoundTrips_HeadingLevel_OnAListItem()
    {
        var doc = new FlowDocument();
        var p = new Paragraph { ListType = ListKind.Bullet, ListLevel = 1, HeadingLevel = 2 };
        p.Inlines.Add(new Run { Text = "heading item" });
        doc.Blocks.Add(p);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var item = Assert.Single(back.Blocks.OfType<Paragraph>());
        Assert.Equal(2, item.HeadingLevel);
        Assert.Equal(1, item.ListLevel);
        Assert.Equal(ListKind.Bullet, item.ListType);
        Assert.Equal("heading item", Plain(item));
    }

    // ---- images and dividers -----------------------------------------------------------------
    // The fuzz never generated either until 2026-08-06, so this whole axis went unexercised; every
    // test below pins something that was actually broken when it first ran.

    private static readonly byte[] TinyPng = System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static ImageBlock Img(double w = 120, double h = 80)
    {
        var ib = new ImageBlock { Width = w, Height = h };
        ib.SetImageData(TinyPng, "image/png");
        return ib;
    }

    private static string Shape(FlowDocument d)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var b in d.Blocks)
            switch (b)
            {
                case Paragraph p:
                    sb.Append("P[").Append(string.Concat(p.Inlines.Select(i => i is Run r ? r.Text : "<IMG>"))).Append(']');
                    break;
                case ImageBlock: sb.Append("IMGBLK"); break;
                case DividerBlock: sb.Append("HR"); break;
                case TableBlock t: sb.Append($"T{t.Rows}x{t.Columns}"); break;
            }
        return sb.ToString();
    }

    // RTF spells a block picture as `\pard <pict>\par`. Reading that \par as content added a blank
    // paragraph under every image — and another on the next cycle, so a document saved and reopened a
    // few times grew a widening gap under each picture.
    [Fact]
    public void Rtf_BlockImage_DoesNotGrowABlankParagraph()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "a" } } });
        doc.Blocks.Add(Img());
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "b" } } });

        var once = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var twice = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(once));
        Assert.Equal("P[a]IMGBLKP[b]", Shape(once));
        Assert.Equal("P[a]IMGBLKP[b]", Shape(twice));
    }

    // A blank line the author typed still has to survive next to an image, so the \par that TERMINATES
    // the picture must not eat it: the writer gives the blank paragraph its own \pard\par.
    [Fact]
    public void Rtf_BlankLineAfterAnImage_Survives()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Img());
        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "b" } } });

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        Assert.Equal("IMGBLKP[]P[b]", Shape(back));
    }

    // A table is only pending rows until FinalizeTable runs, so a picture after `\row` was appended
    // first and jumped ahead of the table it followed.
    [Fact]
    public void Rtf_ImageAfterATable_StaysAfterIt()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new TableBlock(1, 1));
        doc.Blocks.Add(Img());
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "b" } } });

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        Assert.Equal("T1x1IMGBLKP[b]", Shape(back));
    }

    // RTF has no rule control word; Word (and this writer) spell one as an empty paragraph with a
    // bottom border. Only the writing half existed, so every divider came back as a blank line.
    [Fact]
    public void Rtf_RoundTrips_Dividers()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "a" } } });
        doc.Blocks.Add(new DividerBlock());
        doc.Blocks.Add(new DividerBlock());
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "b" } } });

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        Assert.Equal("P[a]HRHRP[b]", Shape(back));
    }

    // A blank line is content; a foreign page's empty <p>/<div> is layout scaffolding. The marker is
    // what separates them — without it, keeping one means adding blank lines to every web paste.
    [Fact]
    public void Html_RoundTrips_BlankParagraph_ButStillDropsForeignEmptyElements()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "a" } } });
        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "b" } } });

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        Assert.Equal("P[a]P[]P[b]", Shape(back));

        var foreign = HtmlDocumentFormatter.ParseHtml("<p>a</p><p></p><div>  </div><p>b</p>");
        Assert.Equal("P[a]P[b]", Shape(foreign));
    }

    // A <p> holding nothing but an image is walked as a block (an <img> is block-or-media), so there is
    // no pending paragraph on import and the image used to rejoin the PRECEDING one — a picture on its
    // own line jumped up into the paragraph above it on every second round trip.
    [Fact]
    public void Html_ImageAloneInItsParagraph_KeepsItsOwnLine()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
        var p = new Paragraph();
        var im = new InlineImage { Width = 20, Height = 20 };
        im.SetImageData(TinyPng, "image/png");
        p.Inlines.Add(im);
        doc.Blocks.Add(p);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below" } } });

        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));
        Assert.Equal("P[above]P[<IMG>]P[below]", Shape(once));
        Assert.Equal("P[above]P[<IMG>]P[below]", Shape(twice));
    }

    // Truncation is the common damage — a half-copied file, a download cut short — and it does not
    // throw: the reader runs out of input and finalizes what it has, which looked like a clean parse of
    // a SHORTER document. TryParse is the entry point that must not let that replace an open one.
    [Theory]
    [InlineData(@"{\rtf1\ansi {\*\broken")]              // truncated inside a nested group
    [InlineData(@"{\rtf1\ansi hello there")]             // truncated after readable text
    [InlineData(@"{\rtf1\ansi\trowd\cellx1000 a\cell")]  // truncated mid-table
    [InlineData(@"{\rtf1\ansi\b bold text\par")]         // no closing brace at all
    public void Rtf_TryParse_ReportsTruncatedInput(string truncated)
    {
        Assert.False(RtfDocumentFormatter.TryParse(truncated, out var doc, out string? error));
        Assert.NotNull(error);
        Assert.Contains("truncated", error);
        Assert.Empty(doc.Blocks);
    }

    [Theory]
    [InlineData(@"{\rtf1\ansi hello\par}")]  // ordinary
    [InlineData(@"{\rtf1\ansi}")]            // genuinely empty is a SUCCESS, not damage
    [InlineData(@"{\rtf1\ansi hi\par}}}}")]  // trailing junk braces: tolerated, as before
    public void Rtf_TryParse_AcceptsWellFormedInput(string rtf)
    {
        Assert.True(RtfDocumentFormatter.TryParse(rtf, out _, out string? error));
        Assert.Null(error);
    }

    // Parse() is the PASTE path and stays lenient on purpose: for a clipboard fragment, whatever was
    // readable beats nothing. Only TryParse — which guards an open document — is strict.
    [Fact]
    public void Rtf_Parse_StaysLenient_OnTruncatedInput()
    {
        var doc = RtfDocumentFormatter.Parse(@"{\rtf1\ansi hello there");
        Assert.Contains("hello there", string.Concat(doc.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    [Fact]
    public void Html_Pre_PreservesWhitespaceAndNewlines()
    {
        var back = HtmlDocumentFormatter.ParseHtml("<pre>if (a)\n    return  1;</pre>");
        string all = string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain));
        Assert.Contains("if (a)\n    return  1;", all); // indentation + double space survive
    }

    [Fact]
    public void Html_Paste_ReadsMsClipboardFileImage_FromTemp_WithPtSize()
    {
        // HWP/Word CF_HTML reference copied pictures via %TEMP% files; the paste path's
        // HtmlFormatHelper.GetStaticFragment rewrites file:/// to ms-clipboard-file:///, and HWP uses
        // pt units in the width/height ATTRIBUTES. All three quirks must parse (even with local file
        // images blocked — temp paths are exempt), or every HWP image paste silently drops.
        var png = System.Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
        string path = Path.Combine(Path.GetTempPath(), "wre_paste_test.png");
        File.WriteAllBytes(path, png);
        try
        {
            string html = $"<p>before <img src=\"ms-clipboard-file:///{path}\" width=100pt height=75pt> after</p>";
            var doc = HtmlDocumentFormatter.ParseHtml(html, allowLocalFileImages: false);

            var img = Assert.Single(doc.Blocks.OfType<ImageBlock>()); // 100pt ≈ 133px > icon cutoff → block
            Assert.Equal(100 * 96.0 / 72.0, img.Width, 1);
            Assert.Equal(75 * 96.0 / 72.0, img.Height, 1);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Html_RoundTrips_SoftLineBreak()
    {
        // Soft break (Shift+Enter, '\n' inside a run) must export as <br/> and parse back to '\n'.
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "one\ntwo" } } });

        var html = HtmlDocumentFormatter.ToHtml(doc);
        Assert.Contains("<br/>", html);

        var back = HtmlDocumentFormatter.ParseHtml(html);
        string all = string.Concat(back.Blocks.OfType<Paragraph>().Select(Plain));
        Assert.Contains("one", all);
        Assert.Contains("two", all);
        Assert.Contains('\n', all);
    }

    // Paragraph-level formatting inside a table cell, out through HTML and back. The writer used to emit
    // a cell's paragraphs as BARE INLINES separated by <br>, and bare inlines can carry nothing
    // paragraph-level — so a bulleted, centred, indented, shaded or heading cell paragraph lost all of it
    // on export, including into the clipboard's HTML flavour (a bulleted cell pasted into Word as plain
    // lines). The READER could always do it: foreign Word tables with bulleted cells parse correctly, so
    // this was the writer alone.
    [Fact]
    public void Html_RoundTrips_ParagraphFormatting_InsideACell()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 2);
        var c0 = tb.Cells[0][0];
        c0.Blocks.Clear();
        c0.Blocks.Add(new Paragraph { ListType = ListKind.Bullet, ListMarker = ListMarkerStyle.Square, Inlines = { new Run { Text = "one" } } });
        c0.Blocks.Add(new Paragraph { ListType = ListKind.Bullet, ListMarker = ListMarkerStyle.Square, ListLevel = 1, Inlines = { new Run { Text = "two" } } });
        var c1 = tb.Cells[0][1];
        c1.Blocks.Clear();
        c1.Blocks.Add(new Paragraph { HeadingLevel = 2, TextAlignment = TextAlignment.Center, Inlines = { new Run { Text = "head" } } });
        c1.Blocks.Add(new Paragraph { Indent = 40, LineSpacing = 2.0, Background = Red, Inlines = { new Run { Text = "body" } } });
        doc.Blocks.Add(tb);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var t = Assert.IsType<TableBlock>(back.Blocks.Single(b => b is TableBlock));

        var a = t.Cells[0][0].Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, a.Count);
        Assert.All(a, p => Assert.Equal(ListKind.Bullet, p.ListType));
        Assert.All(a, p => Assert.Equal(ListMarkerStyle.Square, p.ListMarker));
        Assert.Equal(0, a[0].ListLevel);
        Assert.Equal(1, a[1].ListLevel);

        var b2 = t.Cells[0][1].Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, b2.Count);
        Assert.Equal(2, b2[0].HeadingLevel);
        Assert.Equal(TextAlignment.Center, b2[0].TextAlignment);
        Assert.Equal(40, b2[1].Indent);
        Assert.Equal(2.0, b2[1].LineSpacing);
        Assert.Equal(Red, b2[1].Background);
    }

    // Two paragraphs in a cell stay two. They were joined with <br>, which the reader has never read as a
    // paragraph boundary — it comes back as a newline inside ONE paragraph — so every HTML cycle collapsed
    // a multi-paragraph cell. The round-trip fuzz could not see it: once collapsed it stays collapsed, so
    // cycle 2 matches cycle 1 and the loss is perfectly idempotent.
    [Fact]
    public void Html_RoundTrips_TwoParagraphsInACell_AsTwo()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(1, 1);
        var cell = tb.Cells[0][0];
        cell.Blocks.Clear();
        cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "first" } } });
        cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "second" } } });
        doc.Blocks.Add(tb);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var cellBack = Assert.IsType<TableBlock>(back.Blocks.Single(b => b is TableBlock)).Cells[0][0];
        var ps = cellBack.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, ps.Count);
        Assert.Equal("first", Plain(ps[0]));
        Assert.Equal("second", Plain(ps[1]));
        // A soft break inside ONE cell paragraph is still a newline, not a split — the two must not be
        // confused now that <br> no longer separates paragraphs here.
        Assert.DoesNotContain('\n', Plain(ps[0]));
    }

    // A nested block table inside a cell must not leave a space glued to the text after it. This is a
    // GUARD, not proof of a fix: it passes with or without EmitTable honouring `tight`, because the
    // reader ignores that whitespace while no paragraph is pending. It pins the property the writer's
    // cell handling now depends on.
    [Fact]
    public void Html_NestedTableInACell_DoesNotGrowASpaceAfterIt()
    {
        var doc = new FlowDocument();
        var outer = new TableBlock(1, 1);
        var cell = outer.Cells[0][0];
        cell.Blocks.Clear();
        var inner = new TableBlock(1, 1);
        ((Run)inner.Cells[0][0].Para.Inlines[0]).Text = "inner";
        cell.Blocks.Add(inner);
        cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });
        doc.Blocks.Add(outer);

        // Two cycles: this is the accumulating class, so one cycle is not the test.
        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));
        foreach (var d in new[] { once, twice })
        {
            var c = Assert.IsType<TableBlock>(d.Blocks.Single(b => b is TableBlock)).Cells[0][0];
            var text = string.Concat(c.Blocks.OfType<Paragraph>().Select(Plain));
            Assert.Equal("after", text);
        }
    }

    // A paragraph that holds a PICTURE keeps its own formatting on import. The walker's
    // "recurse into anything containing block-or-media" branch ran before the one that reads a
    // paragraph element's style, so an <img> anywhere in the paragraph turned <p style="…"> into a mere
    // container and every paragraph-level value was dropped — a captioned picture lost its centring on
    // every HTML load, at the top level as much as in a cell.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Html_ParagraphHoldingAnImage_KeepsItsOwnFormatting(bool inCell)
    {
        // 20x20 is below the icon threshold, so it lands as an inline image on a text line.
        const string img = "<img src=\"data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=\" width=\"20\" height=\"20\"/>";
        const string para = "<p style=\"text-align:center;margin-left:40px;line-height:200%;\">caption" + img + "</p>";
        string html = inCell ? $"<table><tr><td>{para}</td></tr></table>" : para;

        var doc = HtmlDocumentFormatter.ParseHtml(html);
        var p = inCell
            ? Assert.IsType<TableBlock>(doc.Blocks.Single(b => b is TableBlock)).Cells[0][0].Blocks.OfType<Paragraph>().First()
            : doc.Blocks.OfType<Paragraph>().First();

        Assert.Equal(TextAlignment.Center, p.TextAlignment);
        Assert.Equal(40, p.Indent);
        Assert.Equal(2.0, p.LineSpacing);
        Assert.Contains(p.Inlines, i => i is InlineImage); // the picture is still there
        Assert.Contains("caption", string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)));
    }

    // The narrowing that keeps the fix from changing foreign HTML: a <div> that wraps real block
    // children is still walked as a container, and its styling does NOT descend onto them. Only an
    // element whose sole block-or-media content is media counts as "a paragraph holding a picture".
    [Fact]
    public void Html_ContainerWrappingBlocks_DoesNotPushItsStyleOntoThem()
    {
        var doc = HtmlDocumentFormatter.ParseHtml(
            "<div style=\"text-align:center;margin-left:60px\"><p>a</p><p>b</p></div>");
        var ps = doc.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(2, ps.Count);
        Assert.All(ps, p => Assert.Equal(TextAlignment.Left, p.TextAlignment));
        Assert.All(ps, p => Assert.Equal(0, p.Indent));
    }

    // A paragraph fill inside a table cell, through the two NATIVE save formats — which have to be
    // lossless. The legacy one-paragraph cell encoding shares a single Background field between the
    // cell's fill and the paragraph's, and the cell's assignment came last: with no cell fill the
    // paragraph's was overwritten with null and gone. Both cases are pinned, because the second (fills
    // on both) is the one that still reads as "the cell kept its colour" while the paragraph's is lost.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NativeFormats_KeepAParagraphFillInsideACell(bool cellFilledToo)
    {
        var cellFill = new Color { A = 255, R = 0, G = 0, B = 255 };
        FlowDocument Build()
        {
            var doc = new FlowDocument();
            var tb = new TableBlock(1, 1);
            var cell = tb.Cells[0][0];
            cell.Blocks.Clear();
            cell.Blocks.Add(new Paragraph { Background = Red, Inlines = { new Run { Text = "filled" } } });
            if (cellFilledToo) cell.Background = cellFill;
            doc.Blocks.Add(tb);
            return doc;
        }

        static Paragraph FirstCellParagraph(FlowDocument d)
            => Assert.IsType<Paragraph>(Assert.IsType<TableBlock>(d.Blocks[0]).Cells[0][0].Blocks[0]);

        var viaJson = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(Build()));
        Assert.Equal(Red, FirstCellParagraph(viaJson).Background);

        using var ms = new MemoryStream();
        DocumentPackage.Save(Build(), ms);
        ms.Position = 0;
        var viaFlow = DocumentPackage.Load(ms);
        Assert.Equal(Red, FirstCellParagraph(viaFlow).Background);

        // The cell's own fill still round-trips — the two are separate values, not one.
        var cellAfter = Assert.IsType<TableBlock>(viaFlow.Blocks[0]).Cells[0][0];
        Assert.Equal(cellFilledToo ? cellFill : (Color?)null, cellAfter.Background);
    }
}
