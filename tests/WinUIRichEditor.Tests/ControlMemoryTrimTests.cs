using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>What READING a long document leaves behind (2026-09-27). Every earlier memory guard measured a
/// document that sat at the top of the window; the render path caches what it DRAWS, and paging once to the end
/// draws everything. Measured with MemBaseline's scroll mode, 2,000 paragraphs with 100 photos:
/// <list type="bullet">
/// <item>every picture stayed decoded — Private bytes 183 MB at the top, 501 MB after scrolling to the end and
/// back, and nothing came down while the document stayed open (now 217 MB);</item>
/// <item>every paragraph kept its native layout — 2,000 of them, until the 2048 clear-all (now ~260);</item>
/// <item>taking the editor out of the visual tree released nothing (now 185 MB, and 150 MB once reloaded, from
/// 481 MB).</item>
/// </list>
/// The toolbar's font list is here too: it opened every installed font to read the names.</summary>
[Collection(UiTests.Collection)]
public class ControlMemoryTrimTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags NPS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type T = typeof(RichEditor);

    private static CanvasBitmap Bitmap4x4()
        => CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), new byte[4 * 4 * 4], 4, 4,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

    private static bool IsDisposed(CanvasBitmap bmp)
    {
        try { _ = bmp.SizeInPixels; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    private static readonly Rect Viewport = new(0, 0, 800, 600);

    // A seeded picture drawn at `y` (control DIPs): what a draw pass records.
    private static (byte[] raw, CanvasBitmap bmp) DrawnAt(ImageCache cache, byte seed, double y)
    {
        byte[] raw = [seed, 1, 2, 3];
        var bmp = Bitmap4x4();
        cache.Seed(raw, bmp);
        Assert.Same(bmp, cache.Get(CanvasDevice.GetSharedDevice(), raw, raw, null, 4, 4, new Rect(10, y, 40, 40)));
        return (raw, bmp);
    }

    // ---- ImageCache.TrimOffscreen --------------------------------------------------------------------------

    [Fact]
    public void FarPicturesBeyondTheBudget_GoLeastRecentlyDrawnFirst_AndANearOneStaysWhateverItsAge()
    {
        UiThread.Run(() =>
        {
            var cache = new ImageCache();
            var near = DrawnAt(cache, 1, 0);      // drawn FIRST: the oldest of all, but on screen
            var b = DrawnAt(cache, 2, 5000);
            var c = DrawnAt(cache, 3, 9000);
            var d = DrawnAt(cache, 4, 7000);

            cache.TrimOffscreen(Viewport, keepBytes: 128); // far = b, c, d = 192 bytes: one has to go

            Assert.True(cache.IsCached(near.raw) && !IsDisposed(near.bmp));
            Assert.False(cache.IsCached(b.raw));
            Assert.True(IsDisposed(b.bmp));
            Assert.True(cache.IsCached(c.raw) && cache.IsCached(d.raw));

            cache.TrimOffscreen(Viewport, keepBytes: 0);
            Assert.Equal(1, cache.DecodedCount);
            Assert.True(cache.IsCached(near.raw));
            Assert.Equal(1, cache.SeenCount); // the evicted pictures' positions went with them
        });
    }

    [Fact]
    public void WithinTheBudget_NothingIsReleased()
    {
        UiThread.Run(() =>
        {
            var cache = new ImageCache();
            var a = DrawnAt(cache, 1, 5000);
            var b = DrawnAt(cache, 2, 9000);
            cache.TrimOffscreen(Viewport, keepBytes: 128);
            Assert.True(cache.IsCached(a.raw) && cache.IsCached(b.raw));
        });
    }

    [Fact]
    public void PicturesAnEditRemoved_AreLeftToPrunesOwnBudget()
    {
        UiThread.Run(() =>
        {
            var cache = new ImageCache();
            var a = DrawnAt(cache, 1, 5000);
            cache.Prune(new HashSet<object>(), retainBytes: 1 << 20); // out of the document, kept for undo

            cache.TrimOffscreen(Viewport, keepBytes: 0);

            Assert.True(cache.IsCached(a.raw));
            Assert.Equal(64, cache.RetiredBytes);
        });
    }

    [Fact]
    public void AReleasedPicture_DecodesAgainWhenItIsDrawnAgain_AndTheReleaseIsAnnounced()
    {
        UiThread.Run(() =>
        {
            var cache = new ImageCache();
            int released = 0;
            cache.OnReleased += () => released++;
            var a = DrawnAt(cache, 1, 5000);

            cache.TrimOffscreen(Viewport, keepBytes: 0);
            Assert.Equal(1, released);

            // Scrolled back into view: no bitmap, so the draw shows the placeholder and a decode starts.
            Assert.Null(cache.Get(CanvasDevice.GetSharedDevice(), a.raw, a.raw, null, 4, 4, new Rect(10, 10, 40, 40)));
            Assert.True(cache.IsDecoding(a.raw));
        });
    }

    [Fact]
    public void TheRecordedRect_IsInControlDips_ThroughZoomAndThePageTransform()
    {
        var m = typeof(RichEditor).GetMethod("ControlRect", NPS)!;
        var transform = System.Numerics.Matrix3x2.CreateTranslation(30, 1000) * System.Numerics.Matrix3x2.CreateScale(2);
        var r = (Rect)m.Invoke(null, [transform, new Rect(10, 20, 100, 50)])!;
        Assert.Equal(new Rect(80, 2040, 200, 100), r);
    }

    // ---- the layout cache ---------------------------------------------------------------------------------

    private static FlowDocument Paragraphs(int n)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < n; i++)
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"paragraph {i} with a little text", FontSize = 10 } } });
        return doc;
    }

    private static IDictionary LayoutCache(RichEditor ed) => (IDictionary)T.GetField("_layoutCache", NP)!.GetValue(ed)!;
    private static void TrimLayoutCache(RichEditor ed) => T.GetMethod("TrimLayoutCache", NP)!.Invoke(ed, null);

    [Fact]
    public void TheLayoutCache_IsTrimmedToTheMostRecentlyUsed_AndDisposesWhatItDrops()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = Paragraphs(600) };
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToList();
            var layouts = paras.Select(p => ed.BuildTextLayout(p, 300)).ToList();
            ed.BuildTextLayout(paras[0], 300); // used again: the newest now, though built first

            TrimLayoutCache(ed);

            Assert.Equal(256, ed.LayoutCacheCount);
            var cache = LayoutCache(ed);
            Assert.True(cache.Contains(paras[0]));
            Assert.False(cache.Contains(paras[1]));
            Assert.True(cache.Contains(paras[599]) && cache.Contains(paras[345]));
            Assert.False(cache.Contains(paras[344]));
            Assert.Throws<ObjectDisposedException>(() => layouts[1].LayoutBounds);
            _ = layouts[599].LayoutBounds; // kept ones still work
        });
    }

    [Fact]
    public void TheLayoutCache_IsLeftAloneWithinTheSlack_AndWhileAWalkIsPinned()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = Paragraphs(600) };
            var paras = ed.Document!.Blocks.OfType<Paragraph>().ToList();
            foreach (var p in paras.Take(384)) ed.BuildTextLayout(p, 300);
            TrimLayoutCache(ed);
            Assert.Equal(384, ed.LayoutCacheCount); // LayoutKeep + LayoutTrimSlack: no sort per frame

            foreach (var p in paras.Skip(384)) ed.BuildTextLayout(p, 300);
            var pin = T.GetField("_layoutPinDepth", NP)!;
            pin.SetValue(ed, 1);
            try { TrimLayoutCache(ed); }
            finally { pin.SetValue(ed, 0); }
            Assert.Equal(600, ed.LayoutCacheCount);
        });
    }

    // A hosted editor with `doc`, waited on until it has drawn at least once more.
    private static RichEditor HostedWith(FlowDocument doc)
    {
        var ed = UiThread.Run(() => new RichEditor { Document = doc, PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        WaitForDraw(ed);
        return ed;
    }

    private static void WaitForDraw(RichEditor ed)
    {
        int before = UiThread.Run(() => { T.GetMethod("InvalidateCanvas", NP)!.Invoke(ed, null); return ed.DrawPasses; });
        WaitUntil(() => ed.DrawPasses > before, "a draw pass");
    }

    private static void WaitUntil(Func<bool> condition, string what, int timeoutMs = 8000)
    {
        bool met = false;
        UiThread.RunAsync(async () =>
        {
            var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (!(met = condition()) && DateTime.UtcNow < until) await Task.Delay(25);
        });
        Assert.True(met, $"timed out waiting for {what}");
    }

    private static void Unhost(RichEditor ed)
        => UiThread.Run(() => ((Microsoft.UI.Xaml.Controls.Panel)ed.Parent).Children.Remove(ed));

    [Fact]
    public void ADrawPass_TrimsTheLayoutCacheItFilled()
    {
        var ed = HostedWith(Paragraphs(600));
        try
        {
            UiThread.Run(() =>
            {
                foreach (var p in ed.Document!.Blocks.OfType<Paragraph>()) ed.BuildTextLayout(p, 300);
                Assert.True(ed.LayoutCacheCount >= 600);
            });
            WaitForDraw(ed);
            Assert.Equal(256, UiThread.Run(() => ed.LayoutCacheCount));
        }
        finally { Unhost(ed); }
    }

    // ---- leaving the visual tree ----------------------------------------------------------------------------

    private static ImageCache Cache(RichEditor ed) => (ImageCache)T.GetField("_images", NP)!.GetValue(ed)!;

    // A hosted editor with a picture at the top (drawn by the real draw pass, so its position is recorded)
    // and one 400 paragraphs down (never drawn). Distinct bytes: the cache keys by content. Never decoded —
    // both are seeded before the editor is hosted.
    private static (RichEditor ed, byte[] nearRaw, CanvasBitmap nearBmp, byte[] farRaw, CanvasBitmap farBmp)
        HostedWithTwoPictures(Action<RichEditor>? configure = null)
    {
        byte[] nearRaw = [1, 2, 3], farRaw = [4, 5, 6];
        var doc = Paragraphs(400);
        var nearImg = new ImageBlock { Width = 40, Height = 40 };
        nearImg.SetImageData(nearRaw, "image/png");
        var farImg = new ImageBlock { Width = 40, Height = 40 };
        farImg.SetImageData(farRaw, "image/png");
        doc.Blocks.Insert(0, nearImg);
        doc.Blocks.Add(farImg);

        var ed = UiThread.Run(() => new RichEditor { Document = doc, PageSize = RichEditorPageSize.Continuous });
        CanvasBitmap nearBmp = null!, farBmp = null!;
        UiThread.Run(() =>
        {
            configure?.Invoke(ed);
            nearBmp = Bitmap4x4(); farBmp = Bitmap4x4();
            Cache(ed).Seed(nearRaw, nearBmp);
            Cache(ed).Seed(farRaw, farBmp);
        });
        UiThread.Host(ed);
        WaitForDraw(ed);
        return (ed, nearRaw, nearBmp, farRaw, farBmp);
    }

    [Fact]
    public void ADrawPass_ReleasesPicturesFarFromView_BeyondTheBudget()
    {
        var (ed, nearRaw, nearBmp, farRaw, farBmp) = HostedWithTwoPictures(e => e.ImageOffscreenBytes = 0);
        try
        {
            UiThread.Run(() =>
            {
                Assert.True(Cache(ed).IsCached(nearRaw) && !IsDisposed(nearBmp));
                Assert.False(Cache(ed).IsCached(farRaw));
                Assert.True(IsDisposed(farBmp));
            });
        }
        finally { Unhost(ed); }
    }

    [Fact]
    public void ThePositionADrawRecords_IsWhereItLandsOnTheControl_UnderZoom()
    {
        var (ed, nearRaw, _, _, _) = HostedWithTwoPictures(e => e.Zoom = 2);
        try
        {
            var at = UiThread.Run(() => Cache(ed).SeenAt(nearRaw));
            Assert.NotNull(at);
            Assert.Equal(80, at!.Value.Width, 3);  // 40 DIPs drawn at 200%
            Assert.Equal(80, at.Value.Height, 3);
        }
        finally { Unhost(ed); }
    }

    [Fact]
    public void Unloading_ReleasesTheLayouts_AndThePicturesFarFromView_KeepsTheNearOne_AndTrimsTheDevice()
    {
        var (ed, nearRaw, nearBmp, farRaw, farBmp) = HostedWithTwoPictures();
        int trimsBefore = UiThread.Run(() =>
        {
            Assert.True(ed.LayoutCacheCount > 0);
            Assert.True(Cache(ed).IsCached(nearRaw) && Cache(ed).IsCached(farRaw));
            return ed.DeviceTrims;
        });

        Unhost(ed);
        WaitUntil(() => !ed.IsLoaded && ed.LayoutCacheCount == 0, "the Unloaded release");

        UiThread.Run(() =>
        {
            Assert.True(Cache(ed).IsCached(nearRaw), "the picture in view must survive: coming back shows it at once");
            Assert.False(IsDisposed(nearBmp));
            Assert.False(Cache(ed).IsCached(farRaw));
            Assert.True(IsDisposed(farBmp));
        });
        WaitUntil(() => ed.DeviceTrims > trimsBefore, "the deferred device trim");
    }

    // ---- the font list ----------------------------------------------------------------------------------------

    [Fact]
    public void TheFontList_ReadFromTheFontSetIndex_NamesExactlyWhatAFaceByFaceWalkNames()
    {
        UiThread.Run(() =>
        {
            var choices = new RichEditor().FontFamilyChoices;

            // The old walk, verbatim in effect: every face's family names, in the UI language → the bare
            // language → en-us → the first. Kept here as the oracle, not in the library: it opens every font.
            string locale = System.Globalization.CultureInfo.CurrentUICulture.Name.ToLowerInvariant();
            string lang = locale.Length >= 2 ? locale[..2] : locale;
            var expected = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            using (var set = CanvasFontSet.GetSystemFontSet())
                foreach (var face in set.Fonts)
                    using (face)
                    {
                        var names = face.FamilyNames;
                        string? name = names.TryGetValue(locale, out var exact) ? exact
                            : names.FirstOrDefault(kv => kv.Key.StartsWith(lang, StringComparison.OrdinalIgnoreCase)).Value
                              ?? (names.TryGetValue("en-us", out var en) ? en : names.Values.FirstOrDefault());
                        if (!string.IsNullOrWhiteSpace(name)) expected.Add(name);
                    }

            Assert.True(expected.Count > 10, "no fonts enumerated: the comparison would be vacuous");
            Assert.Equal(expected.ToList(), new SortedSet<string>(choices, StringComparer.OrdinalIgnoreCase).ToList());
        });
    }
}
