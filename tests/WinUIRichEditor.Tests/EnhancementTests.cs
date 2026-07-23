using System.Linq;
using Windows.UI;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

// Covers the 2026-07-08 bug-fix / enhancement batch: RTF typographic control words, merged-cell and
// paragraph-format round-trips, WEBP header parsing, MergeCells content preservation, cell vertical
// alignment (JSON + HTML), and TextPointer ordering across inline tables.
public class EnhancementTests
{
    private static string PlainText(FlowDocument doc)
        => string.Join("\n", doc.Blocks.OfType<Paragraph>()
            .Select(p => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text))));

    [Fact]
    public void Rtf_TypographicControlWords_Parse()
    {
        var doc = RtfDocumentFormatter.Parse(@"{\rtf1\ansi don\rquote t \emdash{} x\bullet y\par}");
        string text = PlainText(doc);
        Assert.Contains("don’t", text);
        Assert.Contains("—", text);
        Assert.Contains("•", text);
    }

    [Fact]
    public void Rtf_MergedCells_RoundTrip()
    {
        var tb = new TableBlock(3, 3);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "anchor";
        tb.MergeCells(0, 0, 1, 1); // 2×2 merge anchored at (0,0)
        var doc = new FlowDocument();
        doc.Blocks.Add(tb);

        string rtf = RtfDocumentFormatter.Write(doc);
        var back = RtfDocumentFormatter.Parse(rtf);
        var tb2 = Assert.IsType<TableBlock>(back.Blocks.First(b => b is TableBlock));

        Assert.Equal(3, tb2.Rows);
        Assert.Equal(3, tb2.Columns);
        var (cs, rs) = tb2.SpanOf(0, 0);
        Assert.Equal(2, cs);
        Assert.Equal(2, rs);
        Assert.True(tb2.IsCovered(0, 1));
        Assert.True(tb2.IsCovered(1, 0));
        Assert.True(tb2.IsCovered(1, 1));
        Assert.False(tb2.IsCovered(2, 2));
    }

    [Fact]
    public void Rtf_AlignmentIndentHighlight_RoundTrip()
    {
        var doc = new FlowDocument();
        var p = new Paragraph
        {
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            Indent = 30,
        };
        p.Inlines.Add(new Run { Text = "hi", Background = Color.FromArgb(255, 255, 255, 0) });
        doc.Blocks.Add(p);

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var bp = Assert.IsType<Paragraph>(back.Blocks[0]);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, bp.TextAlignment);
        Assert.Equal(30, bp.Indent, 3);
        var run = bp.Inlines.OfType<Run>().First(r => !string.IsNullOrEmpty(r.Text));
        Assert.NotNull(run.Background);
        Assert.Equal((byte)255, run.Background!.Value.R);
        Assert.Equal((byte)255, run.Background!.Value.G);
        Assert.Equal((byte)0, run.Background!.Value.B);
    }

    [Fact]
    public void Webp_LossyVp8_HeaderSize()
    {
        var b = new byte[30];
        void Put(int i, string s) { foreach (char c in s) b[i++] = (byte)c; }
        Put(0, "RIFF"); Put(8, "WEBP"); Put(12, "VP8 ");
        b[23] = 0x9D; b[24] = 0x01; b[25] = 0x2A;
        b[26] = 320 & 0xFF; b[27] = 320 >> 8;   // width 320
        b[28] = 240 & 0xFF; b[29] = 240 >> 8;   // height 240
        var (w, h) = ImageInfo.GetPixelSize(b);
        Assert.Equal(320, w);
        Assert.Equal(240, h);
    }

    [Fact]
    public void Webp_LosslessVp8l_HeaderSize()
    {
        var b = new byte[25];
        void Put(int i, string s) { foreach (char c in s) b[i++] = (byte)c; }
        Put(0, "RIFF"); Put(8, "WEBP"); Put(12, "VP8L");
        b[20] = 0x2F;
        uint bits = (100u - 1) | ((50u - 1) << 14); // width 100, height 50
        b[21] = (byte)bits; b[22] = (byte)(bits >> 8); b[23] = (byte)(bits >> 16); b[24] = (byte)(bits >> 24);
        var (w, h) = ImageInfo.GetPixelSize(b);
        Assert.Equal(100, w);
        Assert.Equal(50, h);
    }

    [Fact]
    public void MergeCells_PreservesNonParagraphBlocks()
    {
        var tb = new TableBlock(1, 2);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "a";
        ((Run)tb.Cells[0][1].Para.Inlines[0]).Text = "b";
        tb.Cells[0][1].Blocks.Add(new TableBlock(1, 1)); // nested table in the covered cell

        tb.MergeCells(0, 0, 0, 1);

        var anchor = tb.Cells[0][0];
        Assert.Contains(anchor.Blocks, blk => blk is TableBlock); // nested table moved, not discarded
        string text = string.Concat(anchor.Para.Inlines.OfType<Run>().Select(r => r.Text));
        Assert.Equal("a b", text);
        // The inlines MergeCells moved in (separator + "b") are re-parented to the anchor paragraph.
        Assert.All(anchor.Para.Inlines.Skip(1), i => Assert.Same(anchor.Para, i.Parent));
    }

    [Fact]
    public void Json_CellVerticalAlignment_RoundTrip()
    {
        var tb = new TableBlock(1, 2);
        tb.Cells[0][1].VerticalAlignment = CellVerticalAlignment.Bottom;
        var doc = new FlowDocument();
        doc.Blocks.Add(tb);

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        var tb2 = Assert.IsType<TableBlock>(back.Blocks[0]);
        Assert.Equal(CellVerticalAlignment.Top, tb2.Cells[0][0].VerticalAlignment);
        Assert.Equal(CellVerticalAlignment.Bottom, tb2.Cells[0][1].VerticalAlignment);
    }

    [Fact]
    public void Html_CellVerticalAlignment_RoundTrip()
    {
        var tb = new TableBlock(1, 2);
        tb.Cells[0][0].VerticalAlignment = CellVerticalAlignment.Center;
        var doc = new FlowDocument();
        doc.Blocks.Add(tb);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var tb2 = Assert.IsType<TableBlock>(back.Blocks.First(b => b is TableBlock));
        Assert.Equal(CellVerticalAlignment.Center, tb2.Cells[0][0].VerticalAlignment);
        Assert.Equal(CellVerticalAlignment.Top, tb2.Cells[0][1].VerticalAlignment);
    }

    [Fact]
    public void Json_MalformedInput_ReturnsEmptyDocument()
    {
        // The documented contract: parse errors yield an empty document, not a JsonException
        // (the .flow zip path already behaved; this covers the plain-string LoadJson path).
        var doc = DocumentSerializer.Deserialize("{ not valid json !!");
        Assert.NotNull(doc);
        Assert.Empty(doc.Blocks);
    }

    [Fact]
    public void Html_BareInlineElement_KeepsOwnFormatting()
    {
        // CF_HTML fragments for small copies are often a single styled inline with NO block wrapper.
        // The element's own tag/style must survive (WalkBlocks used to hand it to ParseInlines, which
        // only reads formatting off the nodes it descends into — dropping the outermost element's).
        var doc = HtmlDocumentFormatter.ParseHtml("<span style=\"color:#FF0000;font-size:14pt\"><b>hot</b> word</span>");
        var p = Assert.IsType<Paragraph>(doc.Blocks[0]);
        var runs = p.Inlines.OfType<Run>().ToList();
        Assert.All(runs, r =>
        {
            Assert.NotNull(r.Foreground);
            Assert.Equal((byte)255, r.Foreground!.Value.R);
            Assert.Equal((byte)0, r.Foreground!.Value.G);
            Assert.Equal(14, r.FontSize, 3);
        });
        Assert.Contains(runs, r => r.Text!.Contains("hot") && r.FontWeight.Weight >= 600); // <b> still applies

        var bare = HtmlDocumentFormatter.ParseHtml("<b>bold</b> plain");
        var p2 = Assert.IsType<Paragraph>(bare.Blocks[0]);
        var boldRun = p2.Inlines.OfType<Run>().First(r => r.Text!.Contains("bold"));
        Assert.True(boldRun.FontWeight.Weight >= 600);
    }

    [Fact]
    public void Paragraph_CloneFormat_CarriesAllParagraphProperties()
    {
        var p = new Paragraph
        {
            ListType = ListKind.Bullet,
            ListMarker = ListMarkerStyle.Circle, // the field the Enter split used to drop
            ListLevel = 2,
            LineSpacing = 2.0,                   // ditto
            MarginRight = 12,
            IsQuote = true,
            Indent = 20,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
        };
        p.Inlines.Add(new Run { Text = "content" });

        var f = p.CloneFormat();
        Assert.Empty(f.Inlines); // format only — no content
        Assert.Equal(ListMarkerStyle.Circle, f.ListMarker);
        Assert.Equal(ListKind.Bullet, f.ListType);
        Assert.Equal(2, f.ListLevel);
        Assert.Equal(2.0, f.LineSpacing, 3);
        Assert.Equal(12, f.MarginRight, 3);
        Assert.True(f.IsQuote);
        Assert.Equal(20, f.Indent, 3);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Center, f.TextAlignment);
    }

    [Fact]
    public void Rtf_ImageBytes_HexRoundTrip()
    {
        // Covers the Convert.ToHexStringLower / FromHexString hex paths (replaced the per-byte loops):
        // the embedded bytes must survive Write → Parse byte-for-byte.
        var png = new byte[64];
        png[0] = 0x89; png[1] = (byte)'P'; png[2] = (byte)'N'; png[3] = (byte)'G';
        void BE32(int at, int v) { png[at] = (byte)(v >> 24); png[at + 1] = (byte)(v >> 16); png[at + 2] = (byte)(v >> 8); png[at + 3] = (byte)v; }
        BE32(16, 100); BE32(20, 80); // IHDR width/height the header sniffer reads
        for (int i = 24; i < png.Length; i++) png[i] = (byte)(i * 7); // arbitrary payload

        var doc = new FlowDocument();
        var ib = new ImageBlock { Width = 100, Height = 80 };
        ib.SetImageData(png, "image/png");
        doc.Blocks.Add(ib);

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var ib2 = Assert.IsType<ImageBlock>(back.Blocks.First(b => b is ImageBlock));
        Assert.NotNull(ib2.RawBytes);
        Assert.Equal(png, ib2.RawBytes);
    }

    [Fact]
    public void Html_LineSpacing_RoundTrip()
    {
        var doc = new FlowDocument();
        var p1 = new Paragraph { LineSpacing = 2.0 };
        p1.Inlines.Add(new Run { Text = "double" });
        var p2 = new Paragraph { LineHeight = 24 };
        p2.Inlines.Add(new Run { Text = "fixed" });
        doc.Blocks.Add(p1);
        doc.Blocks.Add(p2);

        var back = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var b1 = Assert.IsType<Paragraph>(back.Blocks[0]);
        var b2 = Assert.IsType<Paragraph>(back.Blocks[1]);
        Assert.Equal(2.0, b1.LineSpacing, 2);
        Assert.True(double.IsNaN(b1.LineHeight));
        Assert.Equal(24, b2.LineHeight, 2);
        Assert.True(double.IsNaN(b2.LineSpacing));
    }

    [Fact]
    public void Rtf_LineSpacing_RoundTrip()
    {
        var doc = new FlowDocument();
        var p1 = new Paragraph { LineSpacing = 1.5 };
        p1.Inlines.Add(new Run { Text = "spaced" });
        var p2 = new Paragraph { LineHeight = 30 };
        p2.Inlines.Add(new Run { Text = "exact" });
        var p3 = new Paragraph(); // unset — must stay unset after the \pard reset
        p3.Inlines.Add(new Run { Text = "plain" });
        doc.Blocks.Add(p1); doc.Blocks.Add(p2); doc.Blocks.Add(p3);

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var b1 = Assert.IsType<Paragraph>(back.Blocks[0]);
        var b2 = Assert.IsType<Paragraph>(back.Blocks[1]);
        var b3 = Assert.IsType<Paragraph>(back.Blocks[2]);
        Assert.Equal(1.5, b1.LineSpacing, 2);
        Assert.Equal(30, b2.LineHeight, 1);
        Assert.True(double.IsNaN(b2.LineSpacing));
        Assert.True(double.IsNaN(b3.LineSpacing));
        Assert.True(double.IsNaN(b3.LineHeight));
    }

    [Fact]
    public void ImageAltText_JsonAndHtml_RoundTrip()
    {
        var png = new byte[24];
        png[0] = 0x89; png[1] = (byte)'P'; png[2] = (byte)'N'; png[3] = (byte)'G';
        png[16] = 0; png[17] = 0; png[18] = 0; png[19] = 100; // width 100
        png[20] = 0; png[21] = 0; png[22] = 0; png[23] = 80;  // height 80

        var doc = new FlowDocument();
        var ib = new ImageBlock { Width = 100, Height = 80, AltText = "차트 <월별> \"매출\"" };
        ib.SetImageData(png, "image/png");
        doc.Blocks.Add(ib);

        var json = Assert.IsType<ImageBlock>(
            DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc)).Blocks.First(b => b is ImageBlock));
        Assert.Equal(ib.AltText, json.AltText);

        var html = Assert.IsType<ImageBlock>(
            HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc)).Blocks.First(b => b is ImageBlock));
        Assert.Equal(ib.AltText, html.AltText);
    }

    [Fact]
    public void Rtf_NestedOrderedList_NumbersPerLevel()
    {
        // level 0: 1,2  → level 1 sublist: 1,2 (NOT 3,4)  → back to level 0: 3
        var doc = new FlowDocument();
        void Add(int level, string text)
        {
            var p = new Paragraph { ListType = ListKind.Ordered, ListLevel = level };
            p.Inlines.Add(new Run { Text = text });
            doc.Blocks.Add(p);
        }
        Add(0, "a"); Add(0, "b"); Add(1, "c"); Add(1, "d"); Add(0, "e");

        // The writer emits literal "N." markers; parsing back yields "N.\t<text>" per paragraph, so the
        // round-trip exposes the numbering: 1,2 (level 0) → 1,2 (level 1 restarts) → 3 (level 0 resumes).
        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var texts = back.Blocks.OfType<Paragraph>()
            .Select(p => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)))
            .Where(t => !string.IsNullOrEmpty(t))
            .ToList();
        Assert.Equal(new[] { "1.\ta", "2.\tb", "1.\tc", "2.\td", "3.\te" }, texts);
    }

    [Fact]
    public void TextPointer_OrdersAcrossInlineTable()
    {
        // doc: [host paragraph with an inline table] [tail paragraph]
        var doc = new FlowDocument();
        var host = new Paragraph { Parent = null };
        var it = new InlineTable();
        host.Inlines.Add(new Run { Text = "before", Parent = host });
        host.Inlines.Add(it);
        var tail = new Paragraph();
        tail.Inlines.Add(new Run { Text = "after", Parent = tail });
        doc.Blocks.Add(host);
        doc.Blocks.Add(tail);

        // Wire parents the way the editor's UpdateParents does.
        host.Parent = doc; tail.Parent = doc;
        it.Parent = host; it.Table.Parent = it;
        var cell = it.Table.Cells[0][0];
        cell.Parent = it.Table;
        var cellPara = cell.Para;
        cellPara.Parent = cell;

        var inCell = new TextPointer(cellPara, 0);
        var inTail = new TextPointer(tail, 0);
        var inHost = new TextPointer(host, 0);

        Assert.True(inHost.CompareTo(inCell) < 0);  // host precedes its inline-table cells
        Assert.True(inCell.CompareTo(inTail) < 0);  // cell paragraphs precede the next top-level block
        Assert.True(inTail.CompareTo(inCell) > 0);
    }

    // ---- 4th review (2026-07-23) regressions ------------------------------------------------------

    // The undo history's byte budget must see content held inside an INLINE table. It used to charge a
    // flat placeholder for the whole inline table, so a document whose text lives in one looked tiny and
    // the 64MB cap never trimmed it — the exact case the budget was added for.
    [Fact]
    public void UndoEstimateCountsInlineTableContent()
    {
        static FlowDocument WithCellText(string text)
        {
            var doc = new FlowDocument();
            var host = new Paragraph();
            var it = new InlineTable { Table = new TableBlock(1, 1) };
            it.Table.Cells[0][0].Para.Inlines.Clear();
            it.Table.Cells[0][0].Para.Inlines.Add(new Run { Text = text });
            host.Inlines.Add(it);
            doc.Blocks.Add(host);
            return doc;
        }

        int small = Controls.UndoManager.EstimateBytes(WithCellText("x"));
        int large = Controls.UndoManager.EstimateBytes(WithCellText(new string('x', 5000)));

        // Text inside the inline table must move the estimate, and by roughly its UTF-16 size.
        Assert.True(large > small, "inline-table cell text was not counted");
        Assert.True(large - small >= 9000, $"expected ~10000 bytes of growth, got {large - small}");
    }

    // CloneFormat is the single source for "same paragraph formatting, new paragraph". SplitByNewlines
    // (multi-line paragraph -> list items) hand-copied 5 fields and lost the rest; assert CloneFormat
    // really carries everything those paths rely on.
    [Fact]
    public void CloneFormatCarriesEveryParagraphFormatField()
    {
        var src = new Paragraph
        {
            ListType = ListKind.Bullet,
            ListMarker = ListMarkerStyle.Circle,
            ListLevel = 2,
            Indent = 40,
            TextAlignment = Microsoft.UI.Xaml.TextAlignment.Center,
            LineSpacing = 2.0,
            LineHeight = double.NaN,
            MarginTop = 7,
            MarginBottom = 9,
            MarginRight = 11,
            HeadingLevel = 3,
            IsQuote = true,
        };
        src.Inlines.Add(new Run { Text = "body" });

        var c = src.CloneFormat();

        Assert.Equal(src.ListType, c.ListType);
        Assert.Equal(src.ListMarker, c.ListMarker);   // ◦ / "a)" glyph — dropped by the old copy
        Assert.Equal(src.ListLevel, c.ListLevel);
        Assert.Equal(src.Indent, c.Indent);
        Assert.Equal(src.TextAlignment, c.TextAlignment);
        Assert.Equal(src.LineSpacing, c.LineSpacing); // custom spacing — dropped by the old copy
        Assert.Equal(src.MarginTop, c.MarginTop);
        Assert.Equal(src.MarginBottom, c.MarginBottom);
        Assert.Equal(src.MarginRight, c.MarginRight);
        Assert.Equal(src.HeadingLevel, c.HeadingLevel);
        Assert.Equal(src.IsQuote, c.IsQuote);
        Assert.Empty(c.Inlines);                      // format only — never the content
    }

    // An RTF picture inside a table cell must stay in that cell. Pictures >= 64px took the "block image"
    // path, which appends straight to the document body — so a Word/HWP table with a photo pasted the
    // photo out of its cell and out of document order.
    [Fact]
    public void RtfPictureInsideCellStaysInTheCell()
    {
        // 1x1 PNG, hex-encoded the way \pngblip carries it.
        byte[] png =
        {
            0x89,0x50,0x4E,0x47,0x0D,0x0A,0x1A,0x0A, 0x00,0x00,0x00,0x0D,0x49,0x48,0x44,0x52,
            0x00,0x00,0x00,0x01,0x00,0x00,0x00,0x01, 0x08,0x06,0x00,0x00,0x00,0x1F,0x15,0xC4,
            0x89,0x00,0x00,0x00,0x0A,0x49,0x44,0x41, 0x54,0x78,0x9C,0x63,0x00,0x01,0x00,0x00,
            0x05,0x00,0x01,0x0D,0x0A,0x2D,0xB4,0x00, 0x00,0x00,0x00,0x49,0x45,0x4E,0x44,0xAE,
            0x42,0x60,0x82,
        };
        string hex = System.Convert.ToHexString(png).ToLowerInvariant();
        // \picwgoal/\pichgoal in twips -> /15 px, so 1500 twips = 100px (>= the 64px block threshold).
        string rtf = @"{\rtf1\ansi\trowd\cellx4000 cell " +
                     @"{\pict\pngblip\picwgoal1500\pichgoal1500 " + hex + "}" +
                     @"\cell\row}";

        var doc = RtfDocumentFormatter.Parse(rtf);

        // No image escaped into the document body...
        Assert.DoesNotContain(doc.Blocks, b => b is ImageBlock);
        // ...and the table cell holds it.
        var table = Assert.IsType<TableBlock>(doc.Blocks.First(b => b is TableBlock));
        bool imageInCell = table.Cells
            .SelectMany(row => row)
            .SelectMany(cell => cell.Blocks)
            .OfType<Paragraph>()
            .SelectMany(p => p.Inlines)
            .Any(i => i is InlineImage);
        Assert.True(imageInCell, "the picture did not land inside the table cell");
    }

    // font-weight was decided by scanning the WHOLE style string, so an unrelated declaration could
    // supply the ":600"/"bold" substring and force bold.
    [Theory]
    [InlineData("font-weight:normal;width:600px", false)]   // ":600" came from width
    [InlineData("font-weight:normal;line-height:700%", false)]
    [InlineData("font-weight:bold", true)]
    [InlineData("font-weight:700", true)]
    [InlineData("font-weight:650", true)]                   // numeric compare, not a fixed list
    [InlineData("font-weight:400", false)]
    [InlineData("font-weight:normal", false)]
    public void HtmlFontWeightReadsOnlyItsOwnDeclaration(string style, bool expectBold)
    {
        var doc = HtmlDocumentFormatter.ParseHtml($"<p><span style=\"{style}\">t</span></p>");
        var run = doc.Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines).OfType<Run>()
            .First(r => r.Text == "t");
        Assert.Equal(expectBold, run.FontWeight.Weight >= 600);
    }
}
