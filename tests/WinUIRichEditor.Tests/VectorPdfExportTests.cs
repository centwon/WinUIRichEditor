using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>SavePdf's vector path (2026-09-12).
/// <para>SavePdf wrote one bitmap per page: a PDF with no text in it. It now draws the pages for Windows'
/// "Microsoft Print to PDF" printer through Direct2D's print control, with the job output captured in memory
/// — no dialog. A probe showed the whole PDF is in the stream when the print control closes, with subset
/// fonts and a ToUnicode map and no images. Where the printer is missing SavePdf falls back to the bitmaps;
/// these tests skip on such a machine rather than pass vacuously.</para></summary>
[Collection(UiTests.Collection)]
public class VectorPdfExportTests
{
    private const string Text = "Editor text bold 한글 문장 cell A";

    private static RichEditor Editor(int paragraphs = 1)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < paragraphs; i++)
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = i == 0 ? Text : $"line {i}", FontSize = 14 });
            doc.Blocks.Add(p);
        }
        return new RichEditor { Document = doc, PageSize = RichEditorPageSize.A4 };
    }

    [System.Runtime.InteropServices.DllImport("winspool.drv", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern bool OpenPrinter(string name, out IntPtr handle, IntPtr defaults);

    [System.Runtime.InteropServices.DllImport("winspool.drv")]
    private static extern bool ClosePrinter(IntPtr handle);

    // Skip only where the printer is genuinely absent — checked apart from the code under test. Skipping on a
    // null result instead would turn "the vector path broke" into a green skip on every machine that has it.
    private static byte[] VectorOrSkip(RichEditor ed)
    {
        if (!OpenPrinter(WindowsPdfPrinter.PrinterName, out var handle, IntPtr.Zero))
            Assert.Skip($"\"{WindowsPdfPrinter.PrinterName}\" is not installed on this machine");
        ClosePrinter(handle);
        var pdf = WindowsPdfPrinter.TryWrite(ed);
        Assert.True(pdf != null, $"\"{WindowsPdfPrinter.PrinterName}\" is installed, but the vector path gave nothing");
        return pdf!;
    }

    // Every stream's text — inflated when it is Flate, as-is when not: Microsoft Print to PDF writes its
    // ToUnicode maps uncompressed (reading only Flate streams found none). No xref walk: it may pack objects
    // into object streams.
    private static List<string> Streams(byte[] pdf)
    {
        string raw = Encoding.Latin1.GetString(pdf);
        var result = new List<string>();
        foreach (Match m in Regex.Matches(raw, @"stream\r?\n"))
        {
            int start = m.Index + m.Length;
            int end = raw.IndexOf("endstream", start, StringComparison.Ordinal);
            if (end < 0) break;
            try
            {
                using var z = new ZLibStream(new MemoryStream(pdf, start, end - start), CompressionMode.Decompress);
                using var r = new StreamReader(z, Encoding.Latin1);
                result.Add(r.ReadToEnd());
            }
            catch (InvalidDataException) { result.Add(raw.Substring(start, end - start)); }
        }
        return result;
    }

    // What a viewer's search and copy can read back: every ToUnicode destination.
    private static HashSet<char> Searchable(byte[] pdf)
    {
        var chars = new HashSet<char>();
        foreach (var s in Streams(pdf).Where(s => s.Contains("begincmap")))
        {
            foreach (Match block in Regex.Matches(s, @"beginbfchar(.*?)endbfchar", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<[0-9A-Fa-f]+>\s*<([0-9A-Fa-f]+)>"))
                    for (int k = 0; k + 4 <= m.Groups[1].Value.Length; k += 4)
                        chars.Add((char)int.Parse(m.Groups[1].Value.Substring(k, 4), NumberStyles.HexNumber));
            foreach (Match block in Regex.Matches(s, @"beginbfrange(.*?)endbfrange", RegexOptions.Singleline))
                foreach (Match m in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>\s*<([0-9A-Fa-f]+)>"))
                {
                    int lo = int.Parse(m.Groups[1].Value, NumberStyles.HexNumber), hi = int.Parse(m.Groups[2].Value, NumberStyles.HexNumber);
                    int dst = int.Parse(m.Groups[3].Value.Substring(0, 4), NumberStyles.HexNumber);
                    for (int k = 0; k <= hi - lo; k++) chars.Add((char)(dst + k));
                }
        }
        return chars;
    }

    // Text, not pictures: no image objects, embedded TrueType fonts, and every character of the document —
    // Latin and Hangul — readable back through ToUnicode.
    [Fact]
    public void TheVectorPdf_HoldsText_ThatAViewerCanSearch() => UiThread.Run(() =>
    {
        var pdf = VectorOrSkip(Editor());
        string raw = Encoding.Latin1.GetString(pdf);
        Assert.StartsWith("%PDF-", raw);
        Assert.DoesNotContain("/Subtype /Image", raw);
        Assert.Contains("/FontFile2", raw);
        var searchable = Searchable(pdf);
        var missing = Text.Where(c => c != ' ' && !searchable.Contains(c)).ToList();
        Assert.True(missing.Count == 0, "not searchable: " + string.Concat(missing));
    });

    // The PDF page is the editor's paper: A4 in DIPs (794 × 1123) is 595.5 × 842.25 points.
    [Fact]
    public void TheVectorPdf_PageIsThePaper() => UiThread.Run(() =>
    {
        var ed = Editor();
        var pdf = VectorOrSkip(ed);
        var paper = ed.GetPaperPixelSize();
        var box = Regex.Match(Encoding.Latin1.GetString(pdf), @"/MediaBox\s*\[\s*([\d.]+)\s+([\d.]+)\s+([\d.]+)\s+([\d.]+)\s*\]");
        Assert.True(box.Success, "no MediaBox");
        Assert.Equal(paper.Width * 0.75, double.Parse(box.Groups[3].Value, CultureInfo.InvariantCulture), 1);
        Assert.Equal(paper.Height * 0.75, double.Parse(box.Groups[4].Value, CultureInfo.InvariantCulture), 1);
    });

    // Paging carries over: as many PDF pages as the print layout has.
    [Fact]
    public void TheVectorPdf_HasEveryPage() => UiThread.Run(() =>
    {
        var ed = Editor(paragraphs: 120);
        int pages = ed.GetPrintPageCount();
        Assert.True(pages >= 2, $"the fixture should span pages, got {pages}");
        var pdf = VectorOrSkip(ed);
        Assert.Equal(pages, Regex.Matches(Encoding.Latin1.GetString(pdf), @"/Type\s*/Page\b(?!s)").Count);
    });

    // SavePdf takes the vector path when it can — the bitmap pages are only the fallback.
    [Fact]
    public void SavePdf_WritesTheVectorPdf() => UiThread.Run(() =>
    {
        var ed = Editor();
        VectorOrSkip(ed);
        using var ms = new MemoryStream();
        ed.SavePdf(ms);
        string raw = Encoding.Latin1.GetString(ms.ToArray());
        Assert.Contains("/FontFile2", raw);
        Assert.DoesNotContain("/Subtype /Image", raw);
    });

    // No such printer (or no spooler): nothing, so SavePdf falls back — never a half-written file.
    [Fact]
    public void AMissingPrinter_GivesNothing() => UiThread.Run(() =>
    {
        Assert.Null(WindowsPdfPrinter.TryWrite(Editor(), "No Such Printer 7c1f"));
    });
}
