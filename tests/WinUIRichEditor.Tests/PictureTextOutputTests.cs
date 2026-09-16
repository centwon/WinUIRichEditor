using System;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Pictures written into HTML (base64) and RTF (hex) — the output and what producing it allocates.
/// <para>Both writers now stream a picture's bytes into a pre-sized builder a slice at a time instead of making
/// strings of the whole payload (2026-09-16). Measured before with <see cref="CopyAllocationProbeTests"/>: copying one
/// 10 MB picture allocated 166 MB for the HTML and 160 MB for the RTF; after, 54 and 80.</para>
/// <para>The encoding tests use a picture spanning several slices with a length that is not a multiple of 3, so a
/// slice boundary that emitted base64 padding, or dropped or doubled a byte, changes the output.</para></summary>
public class PictureTextOutputTests
{
    private static byte[] Picture(int length, int seed)
    {
        var raw = new byte[length];
        new Random(seed).NextBytes(raw);
        raw[0] = 0xFF; raw[1] = 0xD8; raw[2] = 0xFF;
        return raw;
    }

    private static FlowDocument Doc(byte[] blockPicture, byte[] inlinePicture, byte[] cellPicture)
    {
        var doc = new FlowDocument();
        var inline = new InlineImage { Width = 20, Height = 10 };
        inline.SetImageData(inlinePicture, "image/jpeg");
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "a" }, inline } });
        var block = new ImageBlock { Width = 400, Height = 300 };
        block.SetImageData(blockPicture, "image/jpeg");
        doc.Blocks.Add(block);
        var table = new TableBlock(1, 1);
        var cellImage = new ImageBlock { Width = 40, Height = 30 };
        cellImage.SetImageData(cellPicture, "image/jpeg");
        table.Cells[0][0].Blocks.Add(cellImage);
        doc.Blocks.Add(table);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "b" } } });
        return doc;
    }

    [Fact]
    public void Html_CarriesEveryPictureAsItsExactBase64()
    {
        byte[] a = Picture(300_001, 1), b = Picture(100_000, 2), c = Picture(49_000, 3);
        string html = HtmlDocumentFormatter.ToHtml(Doc(a, b, c));

        foreach (var pic in new[] { a, b, c })
            Assert.Contains($"src=\"data:image/jpeg;base64,{Convert.ToBase64String(pic)}\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public void Rtf_CarriesEveryPictureAsItsExactHex()
    {
        byte[] a = Picture(300_001, 4), b = Picture(100_000, 5), c = Picture(49_000, 6);
        string rtf = RtfDocumentFormatter.Write(Doc(a, b, c));

        foreach (var pic in new[] { a, b, c })
            Assert.Contains(" " + Convert.ToHexStringLower(pic) + "}}", rtf, StringComparison.Ordinal);
        Assert.StartsWith(@"{\rtf1", rtf, StringComparison.Ordinal);
        Assert.EndsWith("}", rtf, StringComparison.Ordinal);
    }

    [Fact]
    public void AnHtmlAndRtfRoundTrip_KeepsThePictureBytes()
    {
        byte[] a = Picture(200_000, 7), b = Picture(70_001, 8), c = Picture(5_000, 9);
        foreach (var back in new[]
                 {
                     HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(Doc(a, b, c))),
                     RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(Doc(a, b, c))),
                 })
        {
            var found = new System.Collections.Generic.List<byte[]>(BlockWalk.PictureBytes(back.Blocks));
            Assert.Contains(found, x => x.AsSpan().SequenceEqual(a));
            Assert.Contains(found, x => x.AsSpan().SequenceEqual(b));
            Assert.Contains(found, x => x.AsSpan().SequenceEqual(c));
        }
    }

    // A regression guard on the allocation itself. The bound is 2.5 payloads: a builder and the string made from it are two;
    // any whole-payload string made on the way (the old writers made four) crosses it.
    [Fact]
    public void WritingAPicture_AllocatesItsTextAboutTwice_NotSixTimes()
    {
        const int size = 2 * 1024 * 1024;
        var doc = new FlowDocument();
        var img = new ImageBlock { Width = 100, Height = 100 };
        img.SetImageData(Picture(size, 10), "image/jpeg");
        doc.Blocks.Add(img);

        static long Allocated(Action body)
        {
            body(); // warm-up: JIT and one-time statics are not what is measured
            long before = GC.GetAllocatedBytesForCurrentThread();
            body();
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }

        long base64Bytes = (size + 2) / 3 * 4 * 2L, hexBytes = size * 2 * 2L;
        long html = Allocated(() => HtmlDocumentFormatter.ToHtml(doc));
        long rtf = Allocated(() => RtfDocumentFormatter.Write(doc));

        Assert.True(html < base64Bytes * 5 / 2, $"HTML allocated {html / 1e6:F1} MB for a {base64Bytes / 1e6:F1} MB base64 payload");
        Assert.True(rtf < hexBytes * 5 / 2, $"RTF allocated {rtf / 1e6:F1} MB for a {hexBytes / 1e6:F1} MB hex payload");
    }
}
