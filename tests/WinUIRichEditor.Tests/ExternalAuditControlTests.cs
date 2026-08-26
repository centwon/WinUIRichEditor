using System;
using System.Linq;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The half of the 2026-08-26 audit round's regressions that needs the WinUI runtime —
/// a Win2D device for a bitmap, or the control itself for an edit command.</summary>
[Collection(UiTests.Collection)]
public class ExternalAuditControlTests
{
    private static readonly BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static void SetCaret(RichEditor ed, Paragraph p, int offset)
        => T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(p, offset));

    private static void Invoke(RichEditor ed, string method)
        => T.GetMethod(method, NP)!.Invoke(ed, Array.Empty<object>());

    private static byte[] Png4x4() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAQAAAAECAYAAACp8Z5+AAAAF0lEQVQIHWP8z8Dwn4EIwESEGrCi4a8QAFsUAwXqtvJZAAAAAElFTkSuQmCC");

    // ---- an image held as a decoded bitmap must reach RTF too -------------------------------------

    // ImageBlock.Image / InlineImage.Image are public setters that deliberately clear RawBytes ("setting
    // a bitmap directly discards the raw bytes — serialization then falls back to PNG-encoding it").
    // DocumentSerializer and the HTML writer both honour that fallback. The RTF writer's four image
    // sites tested `RawBytes != null` and wrote nothing otherwise, so such a picture disappeared from
    // .rtf saves AND from every clipboard copy — the RTF flavour being the one Word and HWP prefer.
    [Fact]
    public void ARawBytelessBitmap_SurvivesEveryWriter()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var block = new ImageBlock { Width = 40, Height = 40, Image = Bitmap() };
            var host = new Paragraph();
            host.Inlines.Add(new InlineImage { Width = 16, Height = 16, Image = Bitmap() });
            doc.Blocks.Add(block);
            doc.Blocks.Add(host);

            var cellDoc = new FlowDocument();
            var tb = new TableBlock(1, 1);
            tb.Cells[0][0].Blocks.Clear();
            tb.Cells[0][0].Blocks.Add(new ImageBlock { Width = 40, Height = 40, Image = Bitmap() });
            var cellHost = new Paragraph();
            cellHost.Inlines.Add(new InlineImage { Width = 16, Height = 16, Image = Bitmap() });
            tb.Cells[0][0].Blocks.Add(cellHost);
            cellDoc.Blocks.Add(tb);

            // Two pictures at the top level, two more inside a cell — all four writer branches.
            Assert.Equal(2, Occurrences(RtfDocumentFormatter.Write(doc), @"\pict"));
            Assert.Equal(2, Occurrences(RtfDocumentFormatter.Write(cellDoc), @"\pict"));

            // The control group: the writers that were already right stay right.
            Assert.Equal(2, Occurrences(HtmlDocumentFormatter.ToHtml(doc), "<img"));
        });
    }

    private static CanvasBitmap Bitmap()
    {
        var pixels = new byte[4 * 4 * 4];
        for (int i = 0; i < pixels.Length; i++) pixels[i] = 255;
        return CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), pixels, 4, 4,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
    }

    private static int Occurrences(string haystack, string needle)
    {
        int n = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { n++; i += needle.Length; }
        return n;
    }

    // ---- Backspace / Delete must not split a surrogate pair ---------------------------------------

    [Fact]
    public void Backspace_OverAnEmoji_LeavesNoLoneSurrogate()
    {
        UiThread.Run(() =>
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = "a\U0001F600" });
            var doc = new FlowDocument();
            doc.Blocks.Add(p);
            var ed = new RichEditor { Document = doc };

            var live = (Paragraph)ed.Document!.Blocks[0];
            SetCaret(ed, live, 3); // past the emoji
            Invoke(ed, "Backspace");

            string text = ed.GetPlainText();
            Assert.DoesNotContain(text, c => char.IsSurrogate(c));
            Assert.StartsWith("a", text);
        });
    }

    [Fact]
    public void DeleteForward_OverAnEmoji_LeavesNoLoneSurrogate()
    {
        UiThread.Run(() =>
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = "\U0001F600b" });
            var doc = new FlowDocument();
            doc.Blocks.Add(p);
            var ed = new RichEditor { Document = doc };

            SetCaret(ed, (Paragraph)ed.Document!.Blocks[0], 0);
            Invoke(ed, "DeleteForward");

            string text = ed.GetPlainText();
            Assert.DoesNotContain(text, c => char.IsSurrogate(c));
            Assert.StartsWith("b", text);
        });
    }

    // ---- "make this a block" must work inside a table cell ---------------------------------------

    // Both conversions resolved their insertion point with Document.Blocks.IndexOf(host), which is -1
    // for a paragraph inside a cell, so the command returned silently. The right-click that offers it
    // does fire on cell content — inline objects inside cells are registered by the same paragraph walk
    // the top level uses — and a cell is a legitimate block container (rules #3/#4).
    [Fact]
    public void ConvertingAnInlineImageInsideACell_PutsTheBlockInThatCell()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var tb = new TableBlock(1, 1);
            doc.Blocks.Add(tb);
            var ed = new RichEditor { Document = doc };

            var cell = ((TableBlock)ed.Document!.Blocks.OfType<TableBlock>().First()).Cells[0][0];
            var img = new InlineImage { Width = 16, Height = 16 };
            img.SetImageData(Png4x4(), "image/png");
            cell.Para.Inlines.Add(img);

            int topLevelBefore = ed.Document.Blocks.OfType<ImageBlock>().Count();
            ed.ConvertInlineImageToBlock(cell.Para, img);

            Assert.Contains(cell.Blocks, b => b is ImageBlock);
            Assert.Equal(topLevelBefore, ed.Document.Blocks.OfType<ImageBlock>().Count());
        });
    }

    // The other direction, and the one a human found first: "글자처럼 취급" on a block image inside a
    // cell did nothing at all. Fixing only inline→block left exactly the "same family, remaining half of
    // the pair" this repo has been caught by before.
    [Fact]
    public void ConvertingACellBlockImageToInline_KeepsItInThatCell()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var tb = new TableBlock(1, 1);
            var img = new ImageBlock { Width = 300, Height = 300 };
            img.SetImageData(Png4x4(), "image/png");
            tb.Cells[0][0].Blocks.Add(img);
            doc.Blocks.Add(tb);
            var ed = new RichEditor { Document = doc };

            var cell = ed.Document!.Blocks.OfType<TableBlock>().First().Cells[0][0];
            var live = cell.Blocks.OfType<ImageBlock>().Single();
            ed.ConvertImageBlockToInline(live);

            Assert.DoesNotContain(cell.Blocks, b => b is ImageBlock);
            var inline = cell.Blocks.OfType<Paragraph>()
                .SelectMany(p => p.Inlines).OfType<InlineImage>().Single();
            // ...and sized to the CELL, not to the document: a 240px cap inside a 100px column overflows.
            Assert.True(inline.Width <= 100, $"inline width {inline.Width} does not fit the 100px column");
        });
    }

    // Round trip: the pair must be each other's inverse inside a cell, not just individually reachable.
    [Fact]
    public void ACellImage_SurvivesABlockInlineBlockRoundTrip()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var tb = new TableBlock(1, 1);
            var img = new ImageBlock { Width = 60, Height = 60 };
            img.SetImageData(Png4x4(), "image/png");
            tb.Cells[0][0].Blocks.Add(img);
            doc.Blocks.Add(tb);
            var ed = new RichEditor { Document = doc };

            var cell = ed.Document!.Blocks.OfType<TableBlock>().First().Cells[0][0];
            ed.ConvertImageBlockToInline(cell.Blocks.OfType<ImageBlock>().Single());

            var host = cell.Blocks.OfType<Paragraph>().First(p => p.Inlines.OfType<InlineImage>().Any());
            ed.ConvertInlineImageToBlock(host, host.Inlines.OfType<InlineImage>().Single());

            Assert.Single(cell.Blocks.OfType<ImageBlock>());
            Assert.Empty(ed.Document.Blocks.OfType<ImageBlock>()); // never escapes to the top level
        });
    }

    [Fact]
    public void ConvertingAnInlineTableInsideACell_PutsTheTableInThatCell()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var outer = new TableBlock(1, 1);
            doc.Blocks.Add(outer);
            var ed = new RichEditor { Document = doc };

            var cell = ed.Document!.Blocks.OfType<TableBlock>().First().Cells[0][0];
            var it = new InlineTable { Table = new TableBlock(2, 2) };
            cell.Para.Inlines.Add(it);

            ed.ConvertInlineTableToBlock(cell.Para, it);

            Assert.Contains(cell.Blocks, b => b is TableBlock);
            Assert.Single(ed.Document.Blocks.OfType<TableBlock>()); // no stray table at the top level
        });
    }
}
