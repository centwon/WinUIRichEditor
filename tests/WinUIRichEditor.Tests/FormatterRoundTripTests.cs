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
}
