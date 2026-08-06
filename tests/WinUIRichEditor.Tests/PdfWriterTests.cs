using System;
using System.IO;
using System.Text;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

// PDF export had NO tests at all — a whole output format whose only check was a human opening the file.
// It is one-way, so there is no round trip to lean on; what can be checked is the structure a reader
// needs, and the part that fails silently is the cross-reference table. A byte offset that is off by one
// produces a file that this project cannot tell apart from a good one, and that a viewer refuses to open.
public class PdfWriterTests
{
    // A page of solid grey, as raw 24-bit RGB — the shape RichEditor.SavePdf hands over.
    private static (int, int, byte[]) Page(int w, int h)
    {
        var rgb = new byte[w * h * 3];
        Array.Fill(rgb, (byte)0x80);
        return (w, h, rgb);
    }

    private static byte[] Write(int pageCount, int w = 8, int h = 4)
    {
        using var ms = new MemoryStream();
        PdfWriter.Write(ms, 595.28, 841.89, pageCount, _ => Page(w, h));
        return ms.ToArray();
    }

    // Latin1 so every byte maps to one char and offsets in the text match offsets in the file — the
    // compressed image bytes are binary and must not be re-encoded.
    private static string AsText(byte[] pdf) => Encoding.Latin1.GetString(pdf);

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void EveryXrefOffsetPointsAtItsObject(int pages)
    {
        var pdf = Write(pages);
        string text = AsText(pdf);

        // "\nxref\n", not "xref\n": the latter also matches the tail of "startxref\n", which sits LATER in
        // the file, so the search would land on that and every offset below would be measured from the
        // wrong place. (It found it the first time this test ran.)
        int xref = text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1;
        Assert.True(xref > 0, "no xref section");
        // Header line: "0 N" — N counts the free entry plus one per object.
        int nl = text.IndexOf('\n', xref + 5);
        var header = text[(xref + 5)..nl].Split(' ');
        Assert.Equal("0", header[0]);
        int size = int.Parse(header[1]);
        // catalog + page tree + (page, contents, image) per page, plus the mandatory free entry at 0.
        Assert.Equal(3 + 3 * pages, size);

        // Entries are fixed-width 20-byte records, which is what lets a reader seek straight to one.
        int entries = nl + 1;
        for (int obj = 0; obj < size; obj++)
        {
            string entry = text.Substring(entries + obj * 20, 20);
            Assert.Equal(20, entry.Length);
            Assert.EndsWith("\n", entry);
            if (obj == 0) { Assert.Equal("0000000000 65535 f \n", entry); continue; }
            long offset = long.Parse(entry[..10]);
            Assert.EndsWith(" 00000 n \n", entry);
            // The claim being checked: object N really begins at the offset the table gives.
            Assert.Equal($"{obj} 0 obj", text.Substring((int)offset, $"{obj} 0 obj".Length));
        }

        // startxref must point at the xref keyword, or a reader cannot find the table at all.
        int sx = text.LastIndexOf("startxref\n", StringComparison.Ordinal);
        long declared = long.Parse(text[(sx + 10)..text.IndexOf('\n', sx + 10)]);
        Assert.Equal(xref, declared);

        Assert.StartsWith("%PDF-1.4\n", text);
        Assert.EndsWith("%%EOF\n", text);
        Assert.Contains($"/Count {pages}", text);
    }

    // Each stream's /Length has to match the bytes actually written, image data included — a stream that
    // declares the wrong length corrupts everything after it.
    [Fact]
    public void EveryStreamLengthMatchesItsBytes()
    {
        string text = AsText(Write(2));
        int at = 0, checked_ = 0;
        while ((at = text.IndexOf("/Length ", at, StringComparison.Ordinal)) >= 0)
        {
            int end = text.IndexOfAny(new[] { ' ', '\n', '/' }, at + 8);
            int declared = int.Parse(text[(at + 8)..end]);
            int streamAt = text.IndexOf("stream\n", at, StringComparison.Ordinal);
            Assert.True(streamAt > 0);
            int dataAt = streamAt + "stream\n".Length;
            Assert.Equal("\nendstream", text.Substring(dataAt + declared, "\nendstream".Length));
            checked_++;
            at = dataAt + declared;
        }
        Assert.Equal(4, checked_); // two pages: content stream + image stream each
    }

    // The page tree must reference page objects that exist. A /Kids entry pointing past /Size is the
    // failure mode of the 3*i arithmetic, and it would not show up in the xref check above.
    [Fact]
    public void PageTreeReferencesRealObjects()
    {
        string text = AsText(Write(3));
        int kids = text.IndexOf("/Kids [", StringComparison.Ordinal);
        string list = text[(kids + 7)..text.IndexOf(']', kids)];
        var refs = list.Split(" 0 R", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(3, refs.Length);
        foreach (var r in refs)
        {
            int num = int.Parse(r.Trim());
            Assert.Contains($"{num} 0 obj\n<< /Type /Page ", text);
        }
    }
}
