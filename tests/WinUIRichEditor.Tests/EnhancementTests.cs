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
    public void Json_MalformedInput_Throws()
    {
        // A damaged file is REPORTED, not read as an empty document. Swallowing it is the worst outcome
        // available: the host cannot tell "this file was empty" from "this file is damaged", shows a
        // blank editor, and the next save overwrites a recoverable file with nothing.
        Assert.Throws<System.Text.Json.JsonException>(() => DocumentSerializer.Deserialize("{ not valid json !!"));
    }

    [Fact]
    public void Json_LiteralNull_IsEmptyDocument()
    {
        // A literal null IS valid JSON, so it stays a (empty) document rather than an error.
        var doc = DocumentSerializer.Deserialize("null");
        Assert.NotNull(doc);
        Assert.Empty(doc.Blocks);
    }

    [Fact]
    public void FlowPackage_Corrupt_Throws()
    {
        // Same contract on the .flow path: DocumentPackage.Load used to swallow a corrupt zip.
        using var ms = new System.IO.MemoryStream(new byte[] { 0x50, 0x4B, 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Throws<System.IO.InvalidDataException>(() => DocumentPackage.Load(ms));
    }

    [Fact]
    public void Json_NullEntries_AreSkippedNotThrown()
    {
        // JSON nulls inside Blocks / Cells / Inlines are hostile-but-plausible input (any pasted or
        // picked file is untrusted). They used to take the load down with a NullReferenceException.
        var doc = DocumentSerializer.Deserialize(
            """{"Blocks":[null,{"Type":"Paragraph","Inlines":[null,{"Type":"Run","Text":"ok"}]},""" +
            """{"Type":"Table","Rows":1,"Columns":1,"Cells":[null,[null]]}]}""");
        Assert.NotNull(doc);
        var p = Assert.IsType<Paragraph>(doc.Blocks[0]);           // the null block was skipped
        Assert.Equal("ok", ((Run)p.Inlines.Single()).Text);        // the null inline was skipped
        Assert.IsType<TableBlock>(doc.Blocks[1]);                  // the null row/cell did not throw
    }

    [Fact]
    public void Json_HugeDeclaredTable_DoesNotAllocateIt()
    {
        // The declared Rows/Columns used to size the TableBlock constructor, whose whole result is
        // discarded three lines later in favour of the cells that actually exist — so a file claiming
        // 100,000,000 columns exhausted memory before a single cell was read.
        var doc = DocumentSerializer.Deserialize(
            """{"Blocks":[{"Type":"Table","Rows":100000000,"Columns":100000000,"Cells":[[{"Type":"Run","Text":"x"}]]}]}""");
        var tb = Assert.IsType<TableBlock>(doc.Blocks[0]);
        Assert.Equal(1, tb.Rows);
        Assert.True(tb.Columns <= 1000, $"declared column count must be capped, got {tb.Columns}");
    }

    [Fact]
    public void Html_HugeColspan_DoesNotExhaustMemory()
    {
        // rowspan was bounded by the rows that exist; colspan had no ceiling at all, and both the
        // occupancy grid and the TableBlock are allocated from it. One pasted (or merely buggy) table
        // hung the application.
        var doc = HtmlDocumentFormatter.ParseHtml("""<table><tr><td colspan="100000000">x</td></tr></table>""");
        var tb = Assert.IsType<TableBlock>(doc.Blocks.First(b => b is TableBlock));
        Assert.True(tb.Columns <= 1000, $"colspan must be capped, got {tb.Columns}");
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

        // CopyFormatFrom (paste-into-empty-paragraph) shares CloneFormat's field list: onto a
        // fully-formatted paragraph it must reproduce the same field set.
        var onto = new Paragraph { HeadingLevel = 5, ListType = ListKind.Ordered, Indent = 99, IsQuote = true };
        onto.Inlines.Add(new Run { Text = "keep" });
        onto.CopyFormatFrom(src);
        Assert.Equal(src.ListType, onto.ListType);
        Assert.Equal(src.ListMarker, onto.ListMarker);
        Assert.Equal(src.HeadingLevel, onto.HeadingLevel);
        Assert.Equal(src.LineSpacing, onto.LineSpacing);
        Assert.Equal(src.Indent, onto.Indent);
        Assert.Equal(src.IsQuote, onto.IsQuote);
        Assert.Single(onto.Inlines);                  // inlines untouched
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

    // ---- 6th review (2026-07-25) regressions ------------------------------------------------------

    // The RTF writer handled Run and InlineImage inside a paragraph but had no branch for InlineTable, so
    // an inline ("treat as character") table was dropped without a trace. That is real content loss:
    // SetClipboardFromSelection writes RTF on EVERY copy, and Word/HWP prefer it over CF_HTML — so
    // pasting such a paragraph into those apps lost the table. RTF has no inline grid, so a TOP-LEVEL
    // host paragraph is split around real \trowd rows (what Word does for a table between two runs).
    // Other applications see exactly that block table; OUR reader also sees the {\*\arinline} marker
    // (an ignorable group everyone else skips) and puts the table back on its text line.
    [Fact]
    public void RtfExportKeepsInlineTableAsRealTable()
    {
        var doc = new FlowDocument();
        var host = new Paragraph();
        var it = new InlineTable { Table = new TableBlock(1, 2) };
        ((Run)it.Table.Cells[0][0].Para.Inlines[0]).Text = "A1";
        ((Run)it.Table.Cells[0][1].Para.Inlines[0]).Text = "B1";
        host.Inlines.Add(new Run { Text = "before" });
        host.Inlines.Add(it);
        host.Inlines.Add(new Run { Text = "after" });
        doc.Blocks.Add(host);

        string rtf = RtfDocumentFormatter.Write(doc);
        Assert.Contains(@"\trowd", rtf, System.StringComparison.Ordinal); // a REAL row, not flattened text

        var back = RtfDocumentFormatter.Parse(rtf);
        // Restored to the text line, so the paragraph is whole again instead of split into three blocks.
        var hostBack = back.Blocks.OfType<Paragraph>().Single(p => p.Inlines.OfType<InlineTable>().Any());
        var tbl = hostBack.Inlines.OfType<InlineTable>().Single().Table;
        Assert.Equal(1, tbl.Rows);
        Assert.Equal(2, tbl.Columns);
        Assert.Equal("A1", string.Concat(tbl.Cells[0][0].Para.Inlines.OfType<Run>().Select(r => r.Text)));
        Assert.Equal("B1", string.Concat(tbl.Cells[0][1].Para.Inlines.OfType<Run>().Select(r => r.Text)));
        // The host paragraph's own text survives, in order, around the table on that one line.
        string hostText = string.Concat(hostBack.Inlines.OfType<Run>().Select(r => r.Text));
        Assert.Contains("before", hostText, System.StringComparison.Ordinal);
        Assert.Contains("after", hostText, System.StringComparison.Ordinal);
        Assert.True(hostText.IndexOf("before", System.StringComparison.Ordinal)
                    < hostText.IndexOf("after", System.StringComparison.Ordinal));
    }

    // An inline table that is the FIRST thing in its host paragraph (the ordinary "treat as character"
    // shape — the paragraph holds nothing else) must not be preceded by a \par: that closes an EMPTY
    // paragraph and Word/HWP render a blank line above the table. The empty paragraph AFTER the table is
    // a different thing — RTF requires one — so only the leading side is suppressed.
    [Fact]
    public void RtfInlineTableAloneHasNoLeadingEmptyParagraph()
    {
        var doc = new FlowDocument();
        var host = new Paragraph();
        var it = new InlineTable { Table = new TableBlock(1, 1) };
        ((Run)it.Table.Cells[0][0].Para.Inlines[0]).Text = "X";
        host.Inlines.Add(it);              // nothing before the table
        doc.Blocks.Add(host);

        string rtf = RtfDocumentFormatter.Write(doc);
        // \par as a whole control word (not the \pard prefix) must first appear AFTER the row starts.
        var par = System.Text.RegularExpressions.Regex.Match(rtf, @"\\par(?![a-zA-Z])");
        int rowIdx = rtf.IndexOf(@"\trowd", System.StringComparison.Ordinal);
        Assert.True(par.Success, "expected a trailing \\par after the table");
        Assert.True(rowIdx >= 0 && rowIdx < par.Index,
            $"a \\par at {par.Index} precedes \\trowd at {rowIdx} — that is the spurious empty paragraph");
    }

    // Inside a CELL the same promotion isn't available (real nesting needs \itap2 + \nestcell/\nestrow,
    // outside this writer's subset), so an inline table there still flattens — content over structure,
    // the same rule a nested TableBlock in a cell follows. It must not vanish.
    [Fact]
    public void RtfExportFlattensInlineTableInsideCell()
    {
        var doc = new FlowDocument();
        var outer = new TableBlock(1, 1);
        var cellPara = outer.Cells[0][0].Para;
        cellPara.Inlines.Clear();
        var it = new InlineTable { Table = new TableBlock(1, 2) };
        ((Run)it.Table.Cells[0][0].Para.Inlines[0]).Text = "C1";
        ((Run)it.Table.Cells[0][1].Para.Inlines[0]).Text = "D1";
        cellPara.Inlines.Add(it);
        doc.Blocks.Add(outer);

        string rtf = RtfDocumentFormatter.Write(doc);
        Assert.Contains("C1", rtf, System.StringComparison.Ordinal);
        Assert.Contains("D1", rtf, System.StringComparison.Ordinal);
    }

    // Load-time compaction walked block tables' cells but not an INLINE table's, so a document whose text
    // lives in one kept its per-word run fragmentation (the same blind spot the undo byte budget had).
    [Fact]
    public void RunNormalizerCompactsInsideInlineTable()
    {
        var doc = new FlowDocument();
        var host = new Paragraph();
        var it = new InlineTable { Table = new TableBlock(1, 1) };
        var cellPara = it.Table.Cells[0][0].Para;
        cellPara.Inlines.Clear();
        // Three adjacent identically-formatted runs — the Word/Docs per-word <span> shape.
        cellPara.Inlines.Add(new Run { Text = "one ", FontFamily = string.Concat("Ari", "al") });
        cellPara.Inlines.Add(new Run { Text = "two ", FontFamily = string.Concat("Ari", "al") });
        cellPara.Inlines.Add(new Run { Text = "three", FontFamily = string.Concat("Ari", "al") });
        host.Inlines.Add(it);
        doc.Blocks.Add(host);

        RunNormalizer.Compact(doc);

        var runs = cellPara.Inlines.OfType<Run>().ToList();
        Assert.Single(runs);
        Assert.Equal("one two three", runs[0].Text);
        // Font families are interned to one shared instance across the document.
        Assert.Same(RunNormalizer.Intern("Arial"), runs[0].FontFamily);
    }

    // HWP dropped the row structure of an exported table and pasted the cells as plain lines, even though
    // \trowd/\cellx/\cell/\row were all present: strict readers need the row definition still in scope AT
    // \row, which is why Word writes it twice. Assert the repeat, and that the round-trip survives it
    // (our own parser sees a second \trowd after the cells and must treat it as a no-op).
    [Fact]
    public void RtfTableRepeatsRowDefinitionBeforeRow()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(2, 2);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "A1";
        ((Run)tb.Cells[0][1].Para.Inlines[0]).Text = "B1";
        ((Run)tb.Cells[1][0].Para.Inlines[0]).Text = "A2";
        ((Run)tb.Cells[1][1].Para.Inlines[0]).Text = "B2";
        doc.Blocks.Add(tb);

        string rtf = RtfDocumentFormatter.Write(doc);
        // 2 rows × (definition before the cells + definition before \row) = 4 \trowd.
        Assert.Equal(4, System.Text.RegularExpressions.Regex.Matches(rtf, @"\\trowd").Count);
        // \row is preceded by the LAST \cellx of the repeated definition, not by a \cell.
        Assert.Contains(@"\cellx3000\row", rtf, System.StringComparison.Ordinal);

        var back = RtfDocumentFormatter.Parse(rtf).Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, back.Rows);
        Assert.Equal(2, back.Columns);
        Assert.Equal("B2", string.Concat(back.Cells[1][1].Para.Inlines.OfType<Run>().Select(r => r.Text)));
    }

    // TextRange's TopLevelBlockOf looked exactly ONE level deep (a top-level table's own cells), so a
    // paragraph inside a NESTED (or inline) table read as "not in this document" and Delete() skipped
    // removing the top-level blocks the selection spanned — here the "MID" paragraph survived a
    // selection that ran straight through it.
    [Fact]
    public void TextRangeDeleteSpansOutOfNestedTable()
    {
        var doc = new FlowDocument();
        var a = new Paragraph { Inlines = { new Run { Text = "A" } } };
        var outer = new TableBlock(1, 1);
        var nested = new TableBlock(1, 1);
        var n = nested.Cells[0][0].Para;
        ((Run)n.Inlines[0]).Text = "N";
        outer.Cells[0][0].Blocks.Clear();
        outer.Cells[0][0].Blocks.Add(nested);
        var mid = new Paragraph { Inlines = { new Run { Text = "MID" } } };
        var z = new Paragraph { Inlines = { new Run { Text = "Z" } } };
        doc.Blocks.Add(a); doc.Blocks.Add(outer); doc.Blocks.Add(mid); doc.Blocks.Add(z);

        // Parent chain as UpdateParents wires it — TextRange reaches the document through it.
        a.Parent = doc; outer.Parent = doc; mid.Parent = doc; z.Parent = doc;
        outer.Cells[0][0].Parent = outer;
        nested.Parent = outer.Cells[0][0];
        nested.Cells[0][0].Parent = nested;
        n.Parent = nested.Cells[0][0];
        foreach (var inl in n.Inlines) inl.Parent = n;
        foreach (var inl in z.Inlines) inl.Parent = z;

        // Select from the end of the nested cell's text out to the start of the last top-level paragraph.
        new TextRange(new TextPointer(n, 1), new TextPointer(z, 0)).Delete();

        Assert.DoesNotContain(mid, doc.Blocks);   // the block the selection ran through is gone
        Assert.Contains(outer, doc.Blocks);       // endpoints' own blocks survive
        Assert.Contains(z, doc.Blocks);
        Assert.Equal("N", string.Concat(n.Inlines.OfType<Run>().Select(r => r.Text)));
        Assert.Equal("Z", string.Concat(z.Inlines.OfType<Run>().Select(r => r.Text)));
    }

    // \pard resets alignment to left per the spec, but HWP carries a previously seen \qr forward — one
    // right-aligned paragraph turned every following one right-aligned on paste. The writer now states
    // the alignment on every paragraph, \ql included.
    [Fact]
    public void RtfWritesExplicitAlignmentOnEveryParagraph()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { TextAlignment = Microsoft.UI.Xaml.TextAlignment.Right, Inlines = { new Run { Text = "right" } } });
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "left" } } });

        string rtf = RtfDocumentFormatter.Write(doc);
        Assert.Contains(@"\pard\qr", rtf, System.StringComparison.Ordinal);
        Assert.Contains(@"\pard\ql", rtf, System.StringComparison.Ordinal);

        // And the explicit \ql still round-trips as Left (the parser maps it directly).
        var paras = RtfDocumentFormatter.Parse(rtf).Blocks.OfType<Paragraph>()
            .Where(p => p.Inlines.OfType<Run>().Any(r => !string.IsNullOrEmpty(r.Text))).ToList();
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Right, paras[0].TextAlignment);
        Assert.Equal(Microsoft.UI.Xaml.TextAlignment.Left, paras[1].TextAlignment);
    }

    // ---- RTF table model: real nesting + geometric horizontal merge ---------------------------------

    [Fact]
    public void Rtf_NestedTableInACell_SurvivesAsARealTable()
    {
        // Both directions used to flatten a table inside a cell to tab/newline text: the grid was lost
        // ("the table disappeared" to a user, even though the words were there).
        var inner = new TableBlock(2, 2);
        ((Run)inner.Cells[0][0].Para.Inlines[0]).Text = "중첩1";
        ((Run)inner.Cells[0][1].Para.Inlines[0]).Text = "중첩2";
        ((Run)inner.Cells[1][0].Para.Inlines[0]).Text = "중첩3";
        ((Run)inner.Cells[1][1].Para.Inlines[0]).Text = "중첩4";

        var outer = new TableBlock(1, 1);
        outer.Cells[0][0].Blocks.Clear();
        outer.Cells[0][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = "앞 문단" } } });
        outer.Cells[0][0].Blocks.Add(inner);
        outer.Cells[0][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = "뒤 문단" } } });

        var doc = new FlowDocument();
        doc.Blocks.Add(outer);

        string rtf = RtfDocumentFormatter.Write(doc);
        Assert.Contains(@"\nestcell", rtf, System.StringComparison.Ordinal);
        Assert.Contains(@"\itap2", rtf, System.StringComparison.Ordinal);

        var back = RtfDocumentFormatter.Parse(rtf);
        var outBack = Assert.IsType<TableBlock>(back.Blocks.First(b => b is TableBlock));
        var cell = outBack.Cells[0][0];
        var innerBack = cell.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, innerBack.Rows);
        Assert.Equal(2, innerBack.Columns);
        Assert.Equal("중첩1", string.Concat(innerBack.Cells[0][0].Para.Inlines.OfType<Run>().Select(r => r.Text)));
        Assert.Equal("중첩4", string.Concat(innerBack.Cells[1][1].Para.Inlines.OfType<Run>().Select(r => r.Text)));

        // The parent cell's own paragraphs keep their order AROUND the nested table — without the
        // per-depth pending list the deeper table swallows the text that preceded it.
        var blocks = cell.Blocks.ToList();
        int iInner = blocks.IndexOf(innerBack);
        string Before = string.Concat(blocks.Take(iInner).OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
        string After = string.Concat(blocks.Skip(iInner + 1).OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
        Assert.Contains("앞 문단", Before, System.StringComparison.Ordinal);
        Assert.Contains("뒤 문단", After, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Rtf_OrphanedNestedRows_DoNotLeakIntoTheNextTable()
    {
        // The nested-table accumulators are keyed by \itap depth and consumed when the cell one level up
        // closes. Truncated or malformed input can leave a depth-2 row that no cell ever collects — and
        // without clearing it at the end of the top-level table, the NEXT table's first cell picked it up:
        // a nested table teleporting into an unrelated table further down the document.
        var doc = RtfDocumentFormatter.Parse(
            @"{\rtf1\ansi\trowd\cellx4000\intbl\itap2 잔여\nestcell\nestrow\row\pard 사이\par" +
            @"\trowd\cellx4000\intbl\itap1 정상\cell\row\pard}");

        var tables = doc.Blocks.OfType<TableBlock>().ToList();
        var last = tables[^1];
        foreach (var (_, _, cell) in last.LogicalCells())
            Assert.Empty(cell.Blocks.OfType<TableBlock>());
    }

    [Fact]
    public void Rtf_HorizontalMerge_UsesTheGeometricForm()
    {
        // HWP dissolved the merge when it was expressed as Word's flag form (one \cellx per column with
        // \clmgf/\clmrg). The base RTF model — one cell whose \cellx sits at the merged right edge — is
        // what both readers honour, so that is what we write.
        // A merged header row over an ordinary row — the shape a real document has, and the one the
        // column grid can be recovered from (the unmerged row is what reveals the true columns).
        var tb = new TableBlock(2, 2);
        tb.SetSpan(0, 0, 2, 1);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "머리글";
        ((Run)tb.Cells[1][0].Para.Inlines[0]).Text = "좌";
        ((Run)tb.Cells[1][1].Para.Inlines[0]).Text = "우";
        var doc = new FlowDocument();
        doc.Blocks.Add(tb);

        string rtf = RtfDocumentFormatter.Write(doc);
        Assert.DoesNotContain(@"\clmrg", rtf, System.StringComparison.Ordinal);

        var back = RtfDocumentFormatter.Parse(rtf);
        var tb2 = Assert.IsType<TableBlock>(back.Blocks.First(b => b is TableBlock));
        Assert.Equal(2, tb2.Rows);
        Assert.Equal(2, tb2.Columns);
        Assert.Equal((2, 1), tb2.SpanOf(0, 0));                      // the merge survived
        Assert.Equal("머리글", string.Concat(tb2.Cells[0][0].Para.Inlines.OfType<Run>().Select(r => r.Text)));
        Assert.Equal("우", string.Concat(tb2.Cells[1][1].Para.Inlines.OfType<Run>().Select(r => r.Text)));
    }

    [Fact]
    public void Rtf_FullyMergedRow_CollapsesToOneColumn()
    {
        // Documented limit of the geometric form: when EVERY row is merged the same way, nothing in the
        // file reveals the underlying grid, so it reads back as a single wide column. That renders
        // identically — the merged cell has the same total width — but the model is not identical, so
        // pin the behaviour rather than let it look like a silent bug later.
        var tb = new TableBlock(1, 2);
        tb.SetSpan(0, 0, 2, 1);
        var doc = new FlowDocument();
        doc.Blocks.Add(tb);

        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(doc));
        var tb2 = Assert.IsType<TableBlock>(back.Blocks.First(b => b is TableBlock));
        Assert.Equal(1, tb2.Columns);
    }

    [Fact]
    public void Rtf_WordFlagFormMerge_StillImports()
    {
        // Word writes the OTHER form, and pasting from Word has to keep working: one \cellx per column,
        // \clmgf on the anchor and \clmrg on the continuation.
        var doc = RtfDocumentFormatter.Parse(
            @"{\rtf1\ansi\trowd\clmgf\cellx4000\clmrg\cellx8000\intbl merged\cell\cell\row\pard}");
        var tb = Assert.IsType<TableBlock>(doc.Blocks.First(b => b is TableBlock));
        Assert.Equal(2, tb.Columns);
        Assert.Equal((2, 1), tb.SpanOf(0, 0));
    }

    // ---- inline-table fidelity through the export formats (trackD) ----------------------------------
    // An InlineTable ("treat as character") has no equivalent in HTML or RTF, so it goes out as a
    // block-level <table> / \trowd row set. Without a marker it came BACK as a block table, permanently
    // splitting its host paragraph — one save/load turned one line into three blocks.

    private static Paragraph InlineTableHost()
    {
        var inner = new TableBlock(1, 2);
        inner.Cells[0][0].Blocks.Clear();
        inner.Cells[0][0].Blocks.Add(new Paragraph { Inlines = { new Run { Text = "A" } } });
        inner.Cells[0][1].Blocks.Clear();
        inner.Cells[0][1].Blocks.Add(new Paragraph { Inlines = { new Run { Text = "B" } } });
        var host = new Paragraph();
        host.Inlines.Add(new Run { Text = "before" });
        host.Inlines.Add(new InlineTable { Table = inner });
        host.Inlines.Add(new Run { Text = "after" });
        return host;
    }

    [Fact]
    public void Html_InlineTable_StaysInlineThroughRoundTrip()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(InlineTableHost());

        string html = HtmlDocumentFormatter.ToHtml(doc);
        Assert.Contains("data-are-inline", html, System.StringComparison.Ordinal);
        // Sized to its own columns, not width:100% — otherwise browsers and Word lay it out as a
        // full-width band on its own line.
        Assert.Contains("display:inline-table", html, System.StringComparison.Ordinal);

        var back = HtmlDocumentFormatter.ParseHtml(html);
        var host = Assert.IsType<Paragraph>(back.Blocks[0]);
        Assert.Single(host.Inlines.OfType<InlineTable>());
        Assert.DoesNotContain(back.Blocks.Skip(1), b => b is TableBlock); // did NOT become a block table
        string text = string.Concat(host.Inlines.OfType<Run>().Select(r => r.Text));
        Assert.Contains("before", text, System.StringComparison.Ordinal);
        Assert.Contains("after", text, System.StringComparison.Ordinal);
    }

    [Fact]
    public void Html_InlineTable_DoesNotGrowASpacePerRoundTrip()
    {
        // The pretty-printing newline after </table> parsed back as a whitespace text node, so each
        // save/load inserted one more space after every inline table.
        var doc = new FlowDocument();
        doc.Blocks.Add(InlineTableHost());

        string Text(FlowDocument d) => string.Concat(
            d.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));

        var once = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(doc));
        var twice = HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(once));
        Assert.Equal(Text(once), Text(twice));
    }

    [Fact]
    public void Rtf_InlineTable_StaysInlineThroughRoundTrip()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(InlineTableHost());

        string rtf = RtfDocumentFormatter.Write(doc);
        // The marker is an ignorable destination, so other readers skip it and still see a block table.
        Assert.Contains(@"{\*\arinline}", rtf, System.StringComparison.Ordinal);
        Assert.Contains(@"\trowd", rtf, System.StringComparison.Ordinal); // a real table, not flattened text

        var back = RtfDocumentFormatter.Parse(rtf);
        var host = back.Blocks.OfType<Paragraph>().FirstOrDefault(p => p.Inlines.OfType<InlineTable>().Any());
        Assert.NotNull(host);
        Assert.Single(host!.Inlines.OfType<InlineTable>());
    }

    [Fact]
    public void Rtf_CellBackground_RoundTrips()
    {
        // The editor can set a cell background (right-click ▸ cell colour) and the model/JSON/HTML carry
        // it, but RTF wrote no \clcbpat and read none — so it was dropped by Word, HWP and our own reader.
        var tb = new TableBlock(1, 1);
        tb.Cells[0][0].Background = Color.FromArgb(255, 0xFF, 0xC0, 0x40);
        var doc = new FlowDocument();
        doc.Blocks.Add(tb);

        string rtf = RtfDocumentFormatter.Write(doc);
        Assert.Contains(@"\clcbpat", rtf, System.StringComparison.Ordinal);

        var back = RtfDocumentFormatter.Parse(rtf);
        var tb2 = Assert.IsType<TableBlock>(back.Blocks.First(b => b is TableBlock));
        var bg = tb2.Cells[0][0].Background;
        Assert.NotNull(bg);
        Assert.Equal((0xFF, 0xC0, 0x40), (bg!.Value.R, bg.Value.G, bg.Value.B));
    }

    // Word writes ignorable groups routinely — bookmarks, fields, a nested table's {\*\nesttableprops}.
    // The pending run was flushed at the group's CLOSING brace, by which point the skipped destination
    // was active and FlushRun threw it away: ordinary Word documents imported with text missing.
    [Fact]
    public void RtfImport_KeepsTextBeforeAnIgnorableGroup()
    {
        var doc = RtfDocumentFormatter.Parse(
            @"{\rtf1\ansi before{\*\bkmkstart mark}{\*\bkmkend mark}after\par}");
        Assert.Contains("before", PlainText(doc), System.StringComparison.Ordinal);
        Assert.Contains("after", PlainText(doc), System.StringComparison.Ordinal);
    }

    // \trowd / \cell / \row / \cellx used to act regardless of destination, and Word puts a nested
    // table's row definition inside {\*\nesttableprops \trowd …} — so the half-built parent cell was
    // restarted and its accumulated text discarded mid-row.
    [Fact]
    public void RtfImport_IgnoresRowDefinitionsInsideASkippedGroup()
    {
        var doc = RtfDocumentFormatter.Parse(
            @"{\rtf1\ansi\trowd\cellx4000\cellx8000\intbl keep me{\*\nesttableprops\trowd\cellx2000\nestrow}" +
            @"\cell second\cell\row\pard after\par}");
        var tb = Assert.IsType<TableBlock>(doc.Blocks.First(b => b is TableBlock));
        string cell = string.Concat(tb.Cells[0][0].Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
        Assert.Contains("keep me", cell, System.StringComparison.Ordinal);
    }

    // {\nonesttables …} is the flattened fallback copy of a nested table, for readers that can't nest.
    // We flatten ourselves, so it must be skipped — its \par used to land as a stray break in the
    // parent cell and its text arrived a second time.
    [Fact]
    public void RtfImport_SkipsTheNoNestTablesFallback()
    {
        var doc = RtfDocumentFormatter.Parse(
            @"{\rtf1\ansi real{\nonesttables fallback\par}\par}");
        string text = PlainText(doc);
        Assert.Contains("real", text, System.StringComparison.Ordinal);
        Assert.DoesNotContain("fallback", text, System.StringComparison.Ordinal);
    }
}
