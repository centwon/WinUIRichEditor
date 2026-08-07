using System.Linq;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Header / footer / page numbers through RTF — both directions.
/// <para>The model has carried these since page setup existed and JSON/.flow persist them, but the RTF
/// writer never wrote them and the RTF reader had no case for the destinations. That made two defects at
/// once: exporting to Word silently dropped the header, and opening a Word document INSERTED its header
/// into the body as the first paragraph.</para></summary>
public class RtfPageChromeTests
{
    private static string Plain(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private static FlowDocument Doc(PageSetup ps, string body = "본문")
    {
        var d = new FlowDocument { PageSetup = ps };
        d.Blocks.Add(new Paragraph { Inlines = { new Run { Text = body } } });
        return d;
    }

    // ---- reading: another application's header must not become our content --------------------------

    // Word's spelling, with a header, a footer and a page-number placeholder. Before this, all three of
    // those texts arrived as body paragraphs ahead of the document's real first line.
    [Fact]
    public void WordHeaderAndFooter_DoNotLandInTheBody()
    {
        const string wordish = @"{\rtf1\ansi\deff0{\fonttbl{\f0\fnil Arial;}}
{\header\pard\qc My Header\par}
{\footer\pard\qc Page \chpgn\par}
\pard\sectd Body text here.\par}";

        var doc = RtfDocumentFormatter.Parse(wordish);

        var paras = doc.Blocks.OfType<Paragraph>().Where(p => Plain(p).Length > 0).ToList();
        Assert.Single(paras);
        Assert.Equal("Body text here.", Plain(paras[0]));

        Assert.NotNull(doc.PageSetup);
        Assert.Equal("My Header", doc.PageSetup!.Header);
        Assert.Equal("Page", doc.PageSetup.Footer);      // the \chpgn decoration is not text
        Assert.True(doc.PageSetup.ShowPageNumbers);
    }

    // The left/right/first-page variants are the same band as far as this model is concerned.
    [Theory]
    [InlineData("headerl")]
    [InlineData("headerr")]
    [InlineData("headerf")]
    public void HeaderVariants_AreAllRead(string word)
    {
        var doc = RtfDocumentFormatter.Parse($@"{{\rtf1\ansi{{\{word}\pard Variant\par}}\pard Body\par}}");
        Assert.Equal("Variant", doc.PageSetup?.Header);
        Assert.DoesNotContain("Variant", string.Concat(doc.Blocks.OfType<Paragraph>().Select(Plain)));
    }

    // ---- writing: the header has to reach the consumer ---------------------------------------------

    [Fact]
    public void Rtf_CarriesHeaderFooterAndPageNumbers()
    {
        // ASCII body on purpose: the ordering assertion below searches for it literally, and non-ASCII
        // text goes out as \uN escapes.
        string rtf = RtfDocumentFormatter.Write(Doc(new PageSetup
        {
            PageSize = RichEditorPageSize.A4,
            Header = "Report",
            Footer = "Confidential",
            ShowPageNumbers = true,
        }, body: "BODYTEXT"));

        Assert.Contains(@"{\header", rtf);
        Assert.Contains("Report", rtf);
        Assert.Contains(@"{\footer", rtf);
        Assert.Contains("Confidential", rtf);
        // The page number sits at an explicit RIGHT tab stop on the content edge, which is where the
        // editor draws it. Without the stop it would land on whatever default tab the reader has.
        // A4 = 794 DIPs wide, minus two 48-DIP margins, times 15 twips = 10470.
        Assert.Contains(@"\tqr\tx10470", rtf);
        Assert.Contains(@"\chpgn", rtf);
        // Both destinations belong to the document area, BEFORE the body.
        int header = rtf.IndexOf(@"{\header", System.StringComparison.Ordinal);
        int body = rtf.IndexOf("BODYTEXT", System.StringComparison.Ordinal);
        Assert.True(header >= 0 && body > header, $"header at {header}, body at {body}");
    }

    // A document with no page setup must not gain the destinations — plain documents keep their bytes.
    [Fact]
    public void Rtf_OmitsThemWhenTheDocumentHasNone()
    {
        string rtf = RtfDocumentFormatter.Write(Doc(new PageSetup()));
        Assert.DoesNotContain(@"{\header", rtf);
        Assert.DoesNotContain(@"{\footer", rtf);

        var d = new FlowDocument();
        d.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "x" } } });
        Assert.DoesNotContain(@"{\header", RtfDocumentFormatter.Write(d)); // PageSetup == null
    }

    // ---- and the two halves must agree, twice over ------------------------------------------------

    // TWO cycles, because the first version of this leaked the page-number decoration into the footer
    // text: our writer follows \chpgn with " / " and a NUMPAGES field, and collecting that as text made
    // the separator ACCUMULATE — "꼬리말/" after one cycle, "꼬리말//" after two. Cycle 1 alone looked fine.
    [Theory]
    [InlineData("머리말 텍스트", "꼬리말", true)]
    [InlineData("Header only", null, false)]
    [InlineData(null, "Footer only", false)]
    [InlineData(null, null, true)]   // page numbers with no text of their own
    public void Rtf_RoundTripsPageChrome_Twice(string? header, string? footer, bool numbers)
    {
        var cur = Doc(new PageSetup
        {
            PageSize = RichEditorPageSize.A4, Header = header, Footer = footer, ShowPageNumbers = numbers,
        });

        for (int cycle = 1; cycle <= 2; cycle++)
        {
            cur = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(cur));
            Assert.Equal(header, cur.PageSetup?.Header);
            Assert.Equal(footer, cur.PageSetup?.Footer);
            Assert.Equal(numbers, cur.PageSetup?.ShowPageNumbers ?? false);
            // And the body is still only the body.
            Assert.Equal("본문", string.Concat(cur.Blocks.OfType<Paragraph>().Select(Plain)));
        }
    }
}
