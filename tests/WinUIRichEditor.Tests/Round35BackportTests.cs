using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Upstream round 35 (AvaloniaRichEditor PR #59/#61, 2026-10-01), measured here before porting: most were
/// red in this port too. The ones already right here are kept as guards: InsertTable with a bad size, a pasted
/// paragraph holding an inline table, an empty inline table from JSON, RTF nesting depth (this reader does not build
/// deep nested tables) and RTF vertical merges (stamped with SetSpan, not MergeCells). The picture bomb is no memory
/// risk here: WIC scales while it decodes (40000x40000 peaked at 69 MB, though it took 8.6 s of CPU).</summary>
[Collection(UiTests.Collection)]
public class Round35BackportTests(ITestOutputHelper output)
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    private static IEnumerable<TableBlock> AllTables(FlowDocument doc)
    {
        foreach (var b in doc.Blocks)
            if (b is TableBlock t) yield return t;
            else if (b is Paragraph p) foreach (var it in p.Inlines.OfType<InlineTable>()) yield return it.Table;
    }

    private static int TableDepth(FlowDocument doc)
    {
        int levels = 0;
        for (Block? b = doc.Blocks.OfType<TableBlock>().FirstOrDefault(); b is TableBlock t; b = t.Cells[0][0].Blocks.OfType<TableBlock>().FirstOrDefault())
            levels++;
        return levels;
    }

    // A StackOverflowException takes the process down. 2,000 nested <div>s (11 KB) did it here too.
    [Theory]
    [InlineData("div", 3000)]
    [InlineData("span", 3000)]
    [InlineData("div", 20000)] // where HtmlAgilityPack's own recursive subtree removal overflowed (~3 s)
    public void DeeplyNestedHtml_ParsesKeepingItsText(string tag, int depth)
    {
        var doc = HtmlDocumentFormatter.ParseHtml(string.Concat(Enumerable.Repeat($"<{tag}>", depth)) + "deep text" + string.Concat(Enumerable.Repeat($"</{tag}>", depth)));
        Assert.Contains("deep text", string.Concat(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text)));
    }

    // ---- model / formatters -----------------------------------------------------------------------------

    [Fact]
    public void H1_DeletingIntoAnInlineTableCell_KeepsTheHostsTextAfterTheTable() => UiThread.Run(() =>
    {
        var t = new TableBlock(1, 1);
        var cell = t.Cells[0][0].Para;
        cell.Inlines.Add(new Run { Text = "cellText" });
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "hello" } } });
        doc.Blocks.Add(new Paragraph { Inlines = { new InlineTable { Table = t }, new Run { Text = " after" } } });
        var ed = new RichEditor { Document = doc };
        var p0 = (Paragraph)ed.Document!.Blocks[0];
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(cell, 4));
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(p0, 2));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(cell, 4));
        T.GetMethod("DeleteSelection", NP)!.Invoke(ed, null);
        string text = ed.GetPlainText();
        output.WriteLine(text.Replace("\r\n", "\\n"));
        Assert.Contains("after", text);
        Assert.Contains("Text", text);
    });

    [Fact]
    public void H2b_RtfTablesNestedDeep_AreBounded()
    {
        const int depth = 400;
        var sb = new StringBuilder(@"{\rtf1\ansi ");
        for (int d = 1; d <= depth; d++) sb.Append(@"\pard\intbl\itap").Append(d).Append(' ');
        sb.Append("deep");
        for (int d = depth; d >= 2; d--)
            sb.Append(@"\nestcell{\*\nesttableprops\trowd\itap").Append(d).Append(@"\cellx1000\nestrow}{\nonesttables\par}\pard\intbl\itap").Append(d - 1).Append(' ');
        sb.Append(@"\cell\trowd\cellx2000\row\pard after\par}");
        int levels = TableDepth(RtfDocumentFormatter.Parse(sb.ToString()));
        output.WriteLine($"{levels} levels");
        Assert.True(levels <= 64, $"{levels} levels");
    }

    [Fact]
    public void H2b_HtmlTablesNestedDeep_AreBounded()
    {
        var doc = HtmlDocumentFormatter.ParseHtml(string.Concat(Enumerable.Repeat("<table><tr><td>", 300)) + "deep" + string.Concat(Enumerable.Repeat("</td></tr></table>", 300)));
        int levels = TableDepth(doc);
        output.WriteLine($"{levels} levels");
        Assert.True(levels <= 64, $"{levels} levels");
    }

    [Fact]
    public void H4_AMimeTypeFromAFile_CannotInjectIntoExportedHtml()
    {
        string json = "{\"Blocks\":[{\"Type\":\"Image\",\"ImageRef\":\"k\"}],\"Images\":{\"k\":{\"Data\":\"" + Png1x1 +
                      "\",\"MimeType\":\"image/png\\\" onerror=\\\"alert(1)\"}}}";
        var doc = DocumentSerializer.Deserialize(json);
        // The model does not carry it either: MimeType is public, and a host may write it out itself.
        Assert.Equal("image/png", doc.Blocks.OfType<ImageBlock>().Single().MimeType);
        string html = HtmlDocumentFormatter.ToHtml(doc);
        output.WriteLine(html.Substring(0, Math.Min(200, html.Length)));
        Assert.DoesNotContain("onerror", html);
    }

    [Fact]
    public void H4_AMimeTypeSetByAHost_CannotInjectEither()
    {
        var ib = new ImageBlock();
        ib.SetImageData(Convert.FromBase64String(Png1x1), "image/png\" onerror=\"alert(1)");
        var doc = new FlowDocument();
        doc.Blocks.Add(ib);
        Assert.DoesNotContain("onerror", HtmlDocumentFormatter.ToHtml(doc));
    }

    [Fact]
    public void M2_Json_WideRowPadsEveryRow()
    {
        var sb = new StringBuilder("{\"Blocks\":[{\"Type\":\"Table\",\"Cells\":[");
        sb.Append('[').Append(string.Join(",", Enumerable.Repeat("{}", 1500))).Append(']');
        for (int i = 0; i < 3; i++) sb.Append(",[{}]");
        sb.Append("]}]}");
        var tb = DocumentSerializer.Deserialize(sb.ToString()).Blocks.OfType<TableBlock>().Single();
        output.WriteLine($"{tb.Columns} cols");
        Assert.True(tb.Columns <= 1000);
    }

    [Fact]
    public void M2b_Rtf_CellBoundaries()
    {
        var sb = new StringBuilder(@"{\rtf1\ansi ");
        for (int r = 0; r < 3; r++)
        {
            sb.Append(@"\trowd");
            if (r == 0) for (int c = 1; c <= 1500; c++) sb.Append(@"\cellx").Append(c * 20);
            else sb.Append(@"\cellx100");
            sb.Append(@"\pard\intbl x\cell\row ");
        }
        sb.Append(@"\pard after\par}");
        var tb = RtfDocumentFormatter.Parse(sb.ToString()).Blocks.OfType<TableBlock>().Single();
        output.WriteLine($"{tb.Columns} cols");
        Assert.True(tb.Columns <= 1000);
    }

    [Fact]
    public void M2c_Html_ManyRowsUnderAWideOne()
    {
        var html = new StringBuilder("<table><tr><td colspan=\"1000\">wide</td></tr>");
        for (int i = 0; i < 2000; i++) html.Append("<tr><td>r</td></tr>");
        html.Append("</table>");
        var tb = HtmlDocumentFormatter.ParseHtml(html.ToString()).Blocks.OfType<TableBlock>().Single();
        output.WriteLine($"{tb.Rows} x {tb.Columns}");
        Assert.True((long)tb.Rows * tb.Columns <= 250_000);
    }

    [Fact]
    public void M3_APackage_ReadsOnlyUsedPictures()
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            string json = "{\"Blocks\":[{\"Type\":\"Image\",\"ImageRef\":\"used\"}],\"Images\":{\"used\":{},\"unused\":{}}}";
            using (var s = zip.CreateEntry("document.json").Open()) s.Write(Encoding.UTF8.GetBytes(json));
            using (var s = zip.CreateEntry("images/used").Open()) s.Write(Convert.FromBase64String(Png1x1));
            using (var s = zip.CreateEntry("images/unused", CompressionLevel.Optimal).Open())
            {
                var zeros = new byte[1 << 20];
                for (int i = 0; i < 64; i++) s.Write(zeros);
            }
        }
        ms.Position = 0;
        var (_, pool) = DocumentPackage.ReadPackage(ms);
        Assert.False(pool.ContainsKey("unused"));
    }

    [Fact]
    public void M4_Html_ARowWiderThanTheCap()
    {
        Exception? ex = Record.Exception(() => HtmlDocumentFormatter.ParseHtml("<table><tr>" + string.Concat(Enumerable.Repeat("<td>x</td>", 1001)) + "</tr></table>"));
        Assert.Null(ex);
    }

    [Fact]
    public void M6_ScriptAndStyleInsideAParagraph()
    {
        var doc = HtmlDocumentFormatter.ParseHtml("<div>text<script>var secret=1;</script><style>.a{color:red}</style> more</div>");
        string all = string.Concat(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
        output.WriteLine(all);
        Assert.DoesNotContain("secret", all);
        Assert.DoesNotContain("color", all);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void M7_AListLevelFromAFile(int level)
    {
        var doc = DocumentSerializer.Deserialize("{\"Blocks\":[{\"Type\":\"Paragraph\",\"ListType\":\"Bullet\",\"ListLevel\":" + level + ",\"Inlines\":[{\"Text\":\"item\"}]}]}");
        Assert.InRange(((Paragraph)doc.Blocks[0]).ListLevel, 0, 8); // the reader bounds it, not only the writer
        string? html = null;
        Assert.Null(Record.Exception(() => html = HtmlDocumentFormatter.ToHtml(doc)));
        Assert.True(html!.Length < 2000, $"{html.Length}");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(1_000_000)]
    public void M7_AListLevelSetByAHost(int level)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { ListType = ListKind.Bullet, ListLevel = level, Inlines = { new Run { Text = "item" } } });
        string? html = null;
        Assert.Null(Record.Exception(() => html = HtmlDocumentFormatter.ToHtml(doc)));
        Assert.True(html!.Length < 2000, $"{html.Length}");
    }

    [Fact]
    public void L1_JpegFillBytes()
    {
        var b = new byte[] { 0xFF, 0xD8, 0xFF, 0xFF, 0xFF, 0xC0, 0x00, 0x11, 0x08, 0xC3, 0x50, 0xEA, 0x60, 0x03, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        Assert.Equal((60000.0, 50000.0), ImageInfo.GetPixelSize(b));
    }

    [Fact]
    public void L2_MergingIntoACellThatStartsWithANestedTable()
    {
        var tb = new TableBlock(1, 2);
        var anchor = tb.Cells[0][0];
        var nested = new TableBlock(1, 1);
        anchor.Blocks.Insert(0, nested);
        tb.Cells[0][1].Para.Inlines.Add(new Run { Text = "moved" });
        tb.MergeCells(0, 0, 0, 1);
        static bool Holds(TableCell c) => c.Blocks.OfType<Paragraph>().Any(p => p.Inlines.OfType<Run>().Any(r => r.Text == "moved"));
        Assert.True(Holds(anchor) && !Holds(nested.Cells[0][0]));
    }

    [Fact]
    public void L3_FontNameWithASemicolon()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "x", FontFamily = "A;B" } } });
        string rtf = RtfDocumentFormatter.Write(doc);
        string fonttbl = rtf.Substring(rtf.IndexOf(@"{\fonttbl", StringComparison.Ordinal));
        fonttbl = fonttbl.Substring(0, fonttbl.IndexOf(@"{\colortbl", StringComparison.Ordinal));
        output.WriteLine(fonttbl);
        Assert.DoesNotContain("A;B", fonttbl);
    }

    [Fact]
    public void L4_ManyVerticalMergesInRtf()
    {
        const int n = 200;
        var sb = new StringBuilder(@"{\rtf1\ansi ");
        for (int r = 0; r < n; r++)
        {
            sb.Append(@"\trowd");
            for (int c = 1; c <= n; c++) sb.Append(r % 2 == 0 ? @"\clvmgf" : @"\clvmrg").Append(@"\cellx").Append(c * 100);
            for (int c = 0; c < n; c++) sb.Append(@"\pard\intbl x\cell");
            sb.Append(@"\row ");
        }
        sb.Append(@"\pard end\par}");
        var sw = Stopwatch.StartNew();
        RtfDocumentFormatter.Parse(sb.ToString());
        output.WriteLine($"{sw.ElapsedMilliseconds} ms");
        Assert.True(sw.Elapsed.TotalSeconds < 10, $"{sw.Elapsed.TotalSeconds:F1} s");
    }

    // ---- control ------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(-1, 2)]
    [InlineData(0, 3)]
    public void M8_InsertTableNonPositive(int rows, int cols) => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };
        Exception? ex = Record.Exception(() => ed.InsertTable(rows, cols));
        output.WriteLine(ex?.GetType().Name ?? "no exception");
        Assert.Null(ex);
        Assert.Empty(ed.Document!.Blocks.OfType<TableBlock>());
    });

    [Fact]
    public void M9_PastingOneParagraphWithAnInlineTable() => UiThread.Run(() =>
    {
        var src = new FlowDocument();
        var t = new TableBlock(1, 1);
        t.Cells[0][0].Para.Inlines.Add(new Run { Text = "cell" });
        src.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "ab" }, new InlineTable { Table = t }, new Run { Text = "cd" } } });
        var ed = new RichEditor { Document = new FlowDocument() };
        ed.InsertHtml(HtmlDocumentFormatter.ToHtml(src));
        Assert.Single(AllTables(ed.Document!));
    });

    [Fact]
    public void M10_AssigningANewDocument_StartsANewHistory() => UiThread.Run(() =>
    {
        var first = new FlowDocument();
        first.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "OLD FILE" } } });
        var ed = new RichEditor { Document = first };
        var p = (Paragraph)ed.Document!.Blocks[0];
        var tp = new TextPointer(p, 8);
        T.GetField("_caret", NP)!.SetValue(ed, tp); T.GetField("_selStart", NP)!.SetValue(ed, tp); T.GetField("_selEnd", NP)!.SetValue(ed, tp);
        ed.InsertText("!");
        Assert.True(ed.CanUndo); // precondition
        var second = new FlowDocument();
        second.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "NEW FILE" } } });
        ed.Document = second;
        output.WriteLine($"CanUndo after assignment: {ed.CanUndo}");
        ed.Undo();
        Assert.DoesNotContain("OLD FILE", ed.GetPlainText());
    });

    [Fact]
    public void M11_AnInlineTableWithNoRows_FromJson() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadJson("{\"Blocks\":[{\"Type\":\"Paragraph\",\"Inlines\":[{\"Type\":\"Table\",\"Table\":{\"Type\":\"Table\",\"Cells\":[]}},{\"Text\":\"after\"}]}]}");
        output.WriteLine($"tables: {AllTables(ed.Document!).Count()} rows {string.Join(",", AllTables(ed.Document!).Select(t => t.Rows))}");
        var p = (Paragraph)ed.Document!.Blocks[0];
        var tp = new TextPointer(p, 0);
        T.GetField("_caret", NP)!.SetValue(ed, tp); T.GetField("_selStart", NP)!.SetValue(ed, tp); T.GetField("_selEnd", NP)!.SetValue(ed, tp);
        var right = T.GetMethod("MoveCaretRight", NP)!;
        Exception? ex = Record.Exception(() => { for (int i = 0; i < 6; i++) right.Invoke(ed, [false]); });
        output.WriteLine(ex?.InnerException?.GetType().Name ?? ex?.GetType().Name ?? "no exception");
        Assert.Null(ex);
    });
}
