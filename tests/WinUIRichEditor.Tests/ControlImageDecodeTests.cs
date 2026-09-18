using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Pictures are decoded to the size they are drawn at, not their source size (2026-09-16).
/// Measured before (MemBaseline images mode): six 4000x3000 photos shown 240px wide cost ~280 MB of decoded
/// pixels; 1000x750 sources at the same display size cost ~22 MB — the cost followed the SOURCE.</summary>
[Collection(UiTests.Collection)]
public class ControlImageDecodeTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // A solid-colour, bottom-up 24-bit BMP: trivially built by hand, decoded by WIC like any photo.
    internal static byte[] SolidBmp(int w, int h, byte r, byte g, byte b)
    {
        int stride = (w * 3 + 3) & ~3, size = 54 + stride * h;
        var bytes = new byte[size];
        bytes[0] = (byte)'B'; bytes[1] = (byte)'M';
        BitConverter.GetBytes(size).CopyTo(bytes, 2);
        BitConverter.GetBytes(54).CopyTo(bytes, 10);
        BitConverter.GetBytes(40).CopyTo(bytes, 14);
        BitConverter.GetBytes(w).CopyTo(bytes, 18);
        BitConverter.GetBytes(h).CopyTo(bytes, 22);
        BitConverter.GetBytes((short)1).CopyTo(bytes, 26);
        BitConverter.GetBytes((short)24).CopyTo(bytes, 28);
        BitConverter.GetBytes(stride * h).CopyTo(bytes, 34);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int o = 54 + y * stride + x * 3;
                bytes[o] = b; bytes[o + 1] = g; bytes[o + 2] = r;
            }
        return bytes;
    }

    private static int CountRed(CanvasRenderTarget rt)
    {
        var px = rt.GetPixelBytes();
        int n = 0;
        for (int i = 0; i < px.Length; i += 4)
            if (px[i + 2] > 200 && px[i + 1] < 60 && px[i] < 60) n++;
        return n;
    }

    private static ImageCache Cache(RichEditor ed) => (ImageCache)typeof(RichEditor).GetField("_images", NP)!.GetValue(ed)!;

    // One ImageBlock of the given source, displayed at w×h DIPs; returns the RawBytes the loaded document holds.
    // ⚠ Every test uses a source of its OWN size. The cache is keyed by content and the editor is shared, so
    // two tests loading identical pictures share one entry — whichever runs first decides what the other sees
    // (CI's order made the print test find a bitmap an earlier test had decoded: green locally, red there).
    private static byte[] LoadPicture(RichEditor ed, byte[] source, double w, double h)
    {
        var img = new ImageBlock { Width = w, Height = h };
        img.SetImageData(source, "image/bmp");
        var doc = new FlowDocument();
        doc.Blocks.Add(img);
        ed.LoadJson(DocumentSerializer.Serialize(doc));
        return ed.Document!.Blocks.OfType<ImageBlock>().Single().RawBytes!;
    }

    // A screen draw into a 96-DPI target under `scale` (standing in for zoom × display scaling).
    private static void Draw(RichEditor ed, float scale = 1)
    {
        typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
        using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 1200, 1200, 96);
        using var ds = rt.CreateDrawingSession();
        ds.Transform = System.Numerics.Matrix3x2.CreateScale(scale);
        typeof(RichEditor).GetMethod("DrawDocument", NP)!.Invoke(ed, [ds, new Windows.Foundation.Rect(0, 0, 1200, 1200)]);
    }

    private static async System.Threading.Tasks.Task Decoded(RichEditor ed, byte[] raw)
    {
        for (int i = 0; i < 200 && Cache(ed).IsDecoding(raw); i++) await System.Threading.Tasks.Task.Delay(25);
        Assert.False(Cache(ed).IsDecoding(raw), "decode did not finish");
    }

    [Fact]
    public void APictureIsDecodedAtTheSizeItIsDrawn_NotItsSourceSize()
    {
        var ed = Shared.Value;
        byte[] raw = null!;
        UiThread.RunAsync(async () =>
        {
            raw = LoadPicture(ed, SolidBmp(2000, 1500, 255, 0, 0), 200, 150);
            Draw(ed);
            await Decoded(ed, raw);

            var px = Cache(ed).CachedBitmap(raw)!.SizeInPixels;
            // 200×150 drawn, plus the cache's headroom — against the 2000×1500 (11.4 MB) a source decode holds.
            Assert.InRange((int)px.Width, 200, (int)Math.Ceiling(200 * ImageCache.Headroom) + 1);
            Assert.InRange((int)px.Height, 150, (int)Math.Ceiling(150 * ImageCache.Headroom) + 1);
        });
    }

    [Fact]
    public void DrawingAPictureLarger_DecodesItAgainSharper_AndKeepsTheOldOneUntilThen()
    {
        var ed = Shared.Value;
        UiThread.RunAsync(async () =>
        {
            var raw = LoadPicture(ed, SolidBmp(2001, 1500, 255, 0, 0), 200, 150);
            Draw(ed);
            await Decoded(ed, raw);
            var small = Cache(ed).CachedBitmap(raw)!;

            Draw(ed, scale: 3); // zoomed in: 600×450 device pixels
            Assert.True(Cache(ed).IsDecoding(raw), "a larger draw must start a sharper decode");
            Assert.Same(small, Cache(ed).CachedBitmap(raw)); // still drawing the one it has meanwhile
            await Decoded(ed, raw);

            var px = Cache(ed).CachedBitmap(raw)!.SizeInPixels;
            Assert.True(px.Width >= 600 && px.Height >= 450, $"after zooming in: {px.Width}x{px.Height}");

            Draw(ed); // zoomed back out: the sharper bitmap serves, nothing re-decodes
            Assert.False(Cache(ed).IsDecoding(raw));
        });
    }

    // A picture resized out of its source's proportions (a handle drag, an <img width height> from HTML) is
    // drawn STRETCHED into its rect. Decoding to fit INSIDE that rect kept the aspect and came out short on one
    // axis — 400×100 drawn from a 4:3 source decoded to 167×125 — so every draw saw "smaller than needed" on
    // the long axis, started the same decode again, and OnReady's repaint drew again: a decode loop for as
    // long as the picture was on screen, and a blurry picture throughout.
    [Theory]
    [InlineData(400, 100)]
    [InlineData(100, 400)]
    public void AStretchedPicture_IsDecodedOnce_AndSharpOnBothAxes(double w, double h)
    {
        var ed = Shared.Value;
        UiThread.RunAsync(async () =>
        {
            var raw = LoadPicture(ed, SolidBmp(2002 + (int)w, 1500, 255, 0, 0), w, h);
            Draw(ed);
            await Decoded(ed, raw);
            var px = Cache(ed).CachedBitmap(raw)!.SizeInPixels;
            Assert.True(px.Width >= w && px.Height >= h, $"{w}x{h} drawn from a {px.Width}x{px.Height} bitmap");

            Draw(ed);
            Assert.False(Cache(ed).IsDecoding(raw), "the same draw must not decode the picture again");
        });
    }

    [Fact]
    public void APictureIsNeverDecodedAboveItsSource_AndThenStopsAskingForMore()
    {
        var ed = Shared.Value;
        UiThread.RunAsync(async () =>
        {
            var raw = LoadPicture(ed, SolidBmp(64, 48, 255, 0, 0), 400, 300);
            Draw(ed);
            await Decoded(ed, raw);
            var px = Cache(ed).CachedBitmap(raw)!.SizeInPixels;
            Assert.Equal((64u, 48u), (px.Width, px.Height));

            Draw(ed, scale: 2);
            Assert.False(Cache(ed).IsDecoding(raw), "a source-size bitmap can't get sharper; re-decoding it is waste");
        });
    }

    [Fact]
    public void PrintingAPictureNeverDrawnOnScreen_PrintsThePicture_NotTheLoadingPlaceholder()
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            // Loaded but never drawn — what a host does when it opens a file and prints it at once.
            var raw = LoadPicture(ed, SolidBmp(66, 50, 255, 0, 0), 200, 150);

            using var page = ed.RenderPrintPage(0, 96);
            int red = CountRed(page);
            Assert.True(red > 200 * 150 / 2, $"red pixels on the page: {red} (a placeholder has none)");
            // The print's own decode is not left behind in the screen cache.
            Assert.False(Cache(ed).IsCached(raw));
            Assert.False(Cache(ed).IsDecoding(raw));
        });
    }

    [Fact]
    public void CoverBox_KeepsTheAspect_CoversBothAxes_AndNeverEnlarges()
    {
        Assert.Equal((200, 150), ImageDecoder.CoverBox(4000, 3000, 200, 150)); // the source's own proportions
        Assert.Equal((1334, 1000), ImageDecoder.CoverBox(4000, 3000, 200, 1000)); // stretched tall: height decides
        Assert.Equal((1000, 750), ImageDecoder.CoverBox(4000, 3000, 1000, 75)); // stretched wide: width decides
        Assert.Equal((64, 48), ImageDecoder.CoverBox(64, 48, 4000, 3000));
        Assert.Equal((4000, 3), ImageDecoder.CoverBox(4000, 3, 10, 10)); // covering needs more than the source has
        Assert.Equal((2, 1), ImageDecoder.CoverBox(4000, 3000, 1, 0)); // a degenerate box still decodes something
    }

    [Fact]
    public void TheDecoder_ScalesDownAndKeepsTheColour_AndRejectsGarbage()
    {
        var d = ImageDecoder.Decode(SolidBmp(400, 300, 255, 0, 0), 100, 75)!.Value;
        Assert.Equal((100, 75, 400, 300), (d.Width, d.Height, d.NaturalWidth, d.NaturalHeight));
        Assert.Equal(100 * 75 * 4, d.Pixels.Length);
        int mid = (37 * 100 + 50) * 4; // BGRA
        Assert.Equal((0, 0, 255, 255), (d.Pixels[mid], d.Pixels[mid + 1], d.Pixels[mid + 2], d.Pixels[mid + 3]));

        Assert.Null(ImageDecoder.Decode([1, 2, 3, 4, 5], 10, 10));
    }
}
