using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The upstream peer's last audit before its 1.3.0 (AvaloniaRichEditor PR #70, 2026-10-17), measured here
/// before porting. Its tab-separated paste bound has no counterpart: this port does not paste TSV as a table.</summary>
[Collection(UiTests.Collection)]
public class Release13BackportTests(ITestOutputHelper output)
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    private static void SetCaret(RichEditor ed, TextPointer caret, TextPointer? anchor = null)
    {
        T.GetField("_caret", NP)!.SetValue(ed, caret);
        T.GetField("_selStart", NP)!.SetValue(ed, anchor ?? caret);
        T.GetField("_selEnd", NP)!.SetValue(ed, caret);
    }

    private static TextPointer Caret(RichEditor ed) => (TextPointer)T.GetField("_caret", NP)!.GetValue(ed)!;

    private static void WaitForDraw(RichEditor ed)
    {
        bool drew = false;
        UiThread.RunAsync(async () =>
        {
            int before = ed.DrawPasses;
            T.GetMethod("InvalidateCanvas", NP)!.Invoke(ed, null);
            for (int i = 0; i < 2400 && ed.DrawPasses == before; i++) await Task.Delay(25);
            drew = ed.DrawPasses > before;
        });
        Assert.True(drew, "no draw pass");
    }

    // ---- a selection from a line's own text into its inline table -------------------------------------------

    // Round 35 fixed a selection reaching an inline table from the line ABOVE. One that starts in the table's own
    // line, before the table, deleted from its start to the END of the line: the table and the text after it.
    [Fact]
    public void DeletingFromALinesTextIntoItsOwnInlineTable_KeepsTheTableAndTheTextAfterIt() => UiThread.Run(() =>
    {
        var t = new TableBlock(1, 1);
        var cell = t.Cells[0][0].Para;
        cell.Inlines.Add(new Run { Text = "cellText" });
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "abc" }, new InlineTable { Table = t }, new Run { Text = "def" } } });
        var ed = new RichEditor { Document = doc };
        var line = (Paragraph)ed.Document!.Blocks[0];
        SetCaret(ed, new TextPointer(cell, 4), anchor: new TextPointer(line, 1));

        T.GetMethod("DeleteSelection", NP)!.Invoke(ed, null);

        string text = ed.GetPlainText();
        output.WriteLine(text.Replace("\r\n", "\\n"));
        Assert.Single(line.Inlines.OfType<InlineTable>());
        Assert.StartsWith("a", text);
        Assert.Contains("Text", text); // the rest of the cell
        Assert.Contains("def", text);  // the line's text after the table
        Assert.DoesNotContain("bc", text);
    });

    // The same rule for copying: the selection's text stops at the table, not the line's end.
    [Fact]
    public void TextOfASelectionFromALineIntoItsOwnInlineTable_StopsAtTheTable() => UiThread.Run(() =>
    {
        var t = new TableBlock(1, 1);
        var cell = t.Cells[0][0].Para;
        cell.Inlines.Add(new Run { Text = "cellText" });
        var line = new Paragraph { Inlines = { new Run { Text = "abc" }, new InlineTable { Table = t }, new Run { Text = "def" } } };
        var doc = new FlowDocument();
        doc.Blocks.Add(line);
        var ed = new RichEditor { Document = doc };
        line = (Paragraph)ed.Document!.Blocks[0];
        cell = line.Inlines.OfType<InlineTable>().Single().Table.Cells[0][0].Para;

        string text = new TextRange(new TextPointer(line, 1), new TextPointer(cell, 4)).GetText();

        Assert.DoesNotContain("def", text);
        Assert.StartsWith("bc", text);
    });

    // ---- sizes a file states, summed past the range of a double ---------------------------------------------

    // Each value is finite — the reader refuses 1e400 since 2026-10-15 — but two of them add up to infinity, and an
    // infinite canvas height is an exception out of the layout pass.
    [Theory]
    [InlineData("{\"Blocks\":[{\"Type\":\"Image\",\"Height\":1e308},{\"Type\":\"Image\",\"Height\":1e308}]}")]
    [InlineData("{\"Blocks\":[{\"MarginTop\":1e308,\"Inlines\":[{\"Text\":\"a\"}]},{\"MarginTop\":1e308,\"Inlines\":[{\"Text\":\"b\"}]}]}")]
    [InlineData("{\"Blocks\":[{\"Inlines\":[{\"Text\":\"a\",\"FontSize\":1e308}]}]}")]
    // Port only: far from infinite, but past the 2^24 DIP the compositor draws — the process died at the first frame.
    [InlineData("{\"Blocks\":[{\"MarginTop\":2e7,\"Inlines\":[{\"Text\":\"a\"}]}]}")]
    [InlineData("{\"Blocks\":[{\"Type\":\"Image\",\"Height\":1e8}]}")]
    // Port only: a fixed line height keeps the line spacing small, so the font size alone reaches DirectWrite (as
    // infinity once it is a float) — and an inline picture's stated size the same way.
    [InlineData("{\"Blocks\":[{\"LineHeight\":20,\"Inlines\":[{\"Text\":\"a\",\"FontSize\":1e308}]}]}")]
    [InlineData("{\"Blocks\":[{\"LineHeight\":20,\"Inlines\":[{\"Text\":\"a\"},{\"Type\":\"Image\",\"Width\":1e308,\"Height\":1e308}]}]}")]
    public void HugeButFiniteSizesInAFile_DoNotBreakTheLayoutPass(string json)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            ed.LoadJson(json);
            T.GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            Assert.True(double.IsFinite((double)T.GetField("_measuredHeight", NP)!.GetValue(ed)!));
        });
        WaitForDraw(ed);
        UiThread.Run(() => ed.Document = new FlowDocument());
    }

    // Paged, the same sizes go through the page breaker, which walks the content a page at a time.
    [Theory]
    [InlineData("{\"Blocks\":[{\"Type\":\"Image\",\"Height\":1e308},{\"Type\":\"Image\",\"Height\":1e308}]}")]
    [InlineData("{\"Blocks\":[{\"MarginTop\":1e308,\"Inlines\":[{\"Text\":\"a\"}]},{\"MarginTop\":1e308,\"Inlines\":[{\"Text\":\"b\"}]}]}")]
    [InlineData("{\"Blocks\":[{\"Inlines\":[{\"Text\":\"a\",\"FontSize\":1e308}]}]}")]
    [InlineData("{\"Blocks\":[{\"MarginTop\":2e7,\"Inlines\":[{\"Text\":\"a\"}]}]}")]
    public void HugeButFiniteSizesInAFile_DoNotBreakThePagedLayout(string json)
    {
        var ed = Shared.Value;
        try
        {
            UiThread.Run(() =>
            {
                ed.LoadJson(json);
                ed.PageSize = RichEditorPageSize.A4;
                ed.ViewMode = RichEditorViewMode.Page;
                T.GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            });
            WaitForDraw(ed);
        }
        finally { UiThread.Run(() => { ed.ViewMode = RichEditorViewMode.Continuous; ed.PageSize = RichEditorPageSize.Continuous; ed.Document = new FlowDocument(); }); }
    }

    // Pasted HTML reaches the same sizes without any file: double.TryParse reads "Infinity" and "1e308".
    private const string Png1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    [Theory]
    [InlineData("<p>a<img src=\"data:image/png;base64," + Png1x1 + "\" width=\"Infinity\" height=\"Infinity\">b</p>", true)]
    [InlineData("<img src=\"data:image/png;base64," + Png1x1 + "\" width=\"1e308\" height=\"1e308\"><img src=\"data:image/png;base64," + Png1x1 + "\" width=\"1e308\" height=\"1e308\">", true)]
    [InlineData("<p style=\"line-height:Infinity\">a</p>", false)]
    [InlineData("<p data-are-m=\"1e308,1e308,0\">a</p><p data-are-m=\"1e308,1e308,0\">b</p>", false)]
    public void HugeOrInfiniteSizesInHtml_DoNotBreakLayoutOrRendering(string html, bool paste)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            if (paste)
            {
                ed.Clear();
                ed.InsertHtml(html);
                Assert.True(ed.CanUndo); // precondition: the HTML really went in
            }
            else ed.LoadHtml(html);
            T.GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            Assert.True(double.IsFinite((double)T.GetField("_measuredHeight", NP)!.GetValue(ed)!));
        });
        WaitForDraw(ed);
        UiThread.Run(() => ed.Document = new FlowDocument());
    }

    // ---- ←/→ inside a table nested in an inline table's cell -------------------------------------------------

    // ←/→ find the next paragraph in one of two orders: the inline table's own (for a paragraph DIRECTLY in its cell)
    // or the document's caret order, which leaves inline tables out. A paragraph in a block table nested in an inline
    // table's cell is in neither, so the caret stopped at its edge.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrowKeys_LeaveATableNestedInAnInlineTablesCell(bool right) => UiThread.Run(() =>
    {
        var nested = new TableBlock(1, 1);
        var inner = nested.Cells[0][0].Para;
        inner.Inlines.Add(new Run { Text = "x" });
        var outer = new TableBlock(1, 1);
        var cell = outer.Cells[0][0];
        cell.Blocks.Clear();
        cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "before" } } });
        cell.Blocks.Add(nested);
        cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "ab" }, new InlineTable { Table = outer }, new Run { Text = "cd" } } });
        var ed = new RichEditor { Document = doc };
        SetCaret(ed, new TextPointer(inner, right ? 1 : 0));

        T.GetMethod(right ? "MoveCaretRight" : "MoveCaretLeft", NP)!.Invoke(ed, new object[] { false });

        var at = Caret(ed);
        Assert.NotSame(inner, at.Paragraph);
        Assert.Equal(right ? "after" : "before", at.Paragraph?.Inlines.OfType<Run>().FirstOrDefault()?.Text);
    });

    // And the cell's own paragraphs step INTO the nested table rather than over it.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ArrowKeys_EnterATableNestedInAnInlineTablesCell(bool right) => UiThread.Run(() =>
    {
        var nested = new TableBlock(1, 1);
        var inner = nested.Cells[0][0].Para;
        inner.Inlines.Add(new Run { Text = "x" });
        var outer = new TableBlock(1, 1);
        var cell = outer.Cells[0][0];
        cell.Blocks.Clear();
        var before = new Paragraph { Inlines = { new Run { Text = "before" } } };
        var after = new Paragraph { Inlines = { new Run { Text = "after" } } };
        cell.Blocks.Add(before);
        cell.Blocks.Add(nested);
        cell.Blocks.Add(after);
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "ab" }, new InlineTable { Table = outer }, new Run { Text = "cd" } } });
        var ed = new RichEditor { Document = doc };
        SetCaret(ed, right ? new TextPointer(before, 6) : new TextPointer(after, 0));

        T.GetMethod(right ? "MoveCaretRight" : "MoveCaretLeft", NP)!.Invoke(ed, new object[] { false });

        Assert.Same(inner, Caret(ed).Paragraph);
    });

    // ---- a table of more than the layout cache's cap ---------------------------------------------------------

    // Upstream: a prune that ran on every build past 10,000 layouts walked the whole document for nothing — 20,000
    // cells took 77 s to draw. Here the cap is 2,048 with a clear-all; measured as a whole draw of a big table.
    [Fact]
    public void ATableOfTwentyThousandCells_DrawsInSeconds()
    {
        var ed = Shared.Value;
        var sw = Stopwatch.StartNew();
        UiThread.Run(() =>
        {
            var tb = new TableBlock(100, 200);
            foreach (var row in tb.Cells) foreach (var c in row) c.Para.Inlines.Add(new Run { Text = "c" });
            var doc = new FlowDocument();
            doc.Blocks.Add(tb);
            ed.Document = doc;
        });
        WaitForDraw(ed);
        sw.Stop();
        output.WriteLine($"{sw.Elapsed.TotalSeconds:0.0} s, {UiThread.Run(() => ed.LayoutBuilds)} builds");
        UiThread.Run(() => ed.Document = new FlowDocument());
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), $"{sw.Elapsed.TotalSeconds:0.0} s");
    }

    // ---- a .flow whose document.json inflates without bound -----------------------------------------------------

    // Round 35 bounded the picture entries a package inflates (256 MB); the document itself was read whole, so a
    // small .flow inflated to gigabytes. Bounded the same (upstream user decision, 2026-10-17).
    [Fact]
    public void AFlowWhoseDocumentInflatesPastTheBound_IsRefusedLikeADamagedOne()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry("document.json", CompressionLevel.Optimal).Open())
        {
            s.Write("{\"Blocks\":[]}"u8);
            var spaces = new byte[1 << 20];
            Array.Fill(spaces, (byte)' ');
            for (long w = 0; w <= 256L * 1024 * 1024; w += spaces.Length) s.Write(spaces);
        }
        Assert.True(ms.Length < 2 * 1024 * 1024, $"{ms.Length} bytes"); // precondition: a small file
        ms.Position = 0;

        Assert.Throws<InvalidDataException>(() => DocumentPackage.Load(ms));
    }

    // ---- a BMP header claiming int.MinValue pixels -----------------------------------------------------------

    // Width and height are signed in a BMP header (negative height = top-down), so the reader takes the absolute
    // value — and Math.Abs(int.MinValue) throws.
    [Fact]
    public void ABmpHeaderOfIntMinValue_IsReadAsASize_NotAnException()
    {
        var b = new byte[64];
        b[0] = (byte)'B'; b[1] = (byte)'M';
        BitConverter.GetBytes(int.MinValue).CopyTo(b, 18);
        BitConverter.GetBytes(int.MinValue).CopyTo(b, 22);
        var (w, h) = ImageInfo.GetPixelSize(b);
        Assert.True(w >= 0 && h >= 0);
    }
}
