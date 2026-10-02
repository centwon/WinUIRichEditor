using System;
using System.Linq;
using System.Numerics;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>"Every full redraw moves all the text half a pixel" (2026-10-02, 150 %, View page, short document).
/// <para>A <c>CanvasVirtualControl</c> session draws into a slot of XAML's shared atlas surface and Win2D folds the
/// slot's pixel offset into the device context's transform. In DIP units that offset becomes a float DIP value —
/// the two slots a redraw alternated between were y = 2 px (1.3333334 DIP, inexact) and y = 288 px (192 DIP) —
/// and Direct2D's baseline snapping turned the float error into text drawn a fraction of a pixel apart. The
/// screen pass now draws in pixel units (<see cref="RichEditor.UseExactPixelOrigin"/>), where Win2D keeps the
/// offset in exact pixels.</para>
/// <para>The atlas can't be reached from a test, so these draw into an offscreen target with the very transform
/// the device context had in each slot (read back from it while measuring) and compare the two strips.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class RenderPixelOriginTests
{
    private const float Dpi = 144;       // 150 %
    private const int SlotA = 2, SlotB = 288; // the two atlas rows, in pixels
    private const int StripW = 600, StripH = 280;

    // The View demo's opening: a heading and body lines at the default 160 % — baselines at .2 and .6 px.
    private static FlowDocument ShortDocument()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { HeadingLevel = 1, Inlines = { new Run { Text = "RichEditorView" } } });
        foreach (var text in new[] { "드롭인 호스트 컨트롤: 툴바 + 에디터", "툴바로 굵게/기울임/밑줄을 시험하세요.", "Tab 이동", "더블클릭 단어 선택" })
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = text } } });
        return doc;
    }

    // Draws the document's content walk twice into one 144-DPI target, once per slot, `setup` building each
    // slot's session state, and returns how many pixels differ between the two strips.
    private static int SlotDifference(Action<CanvasDrawingSession, int> setup)
    {
        return UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = ShortDocument(), PageSize = RichEditorPageSize.Continuous };
            using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (StripW + 30) / 1.5f, (SlotB + StripH) / 1.5f, Dpi);
            foreach (int slot in new[] { SlotA, SlotB })
            {
                using var ds = rt.CreateDrawingSession();
                setup(ds, slot);
                var draw = typeof(RichEditor).GetMethod("DrawDocument", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                draw.Invoke(ed, [ds, new Windows.Foundation.Rect(0, 0, StripW / 1.5, StripH / 1.5)]);
            }
            var px = rt.GetPixelBytes();
            int stride = (int)rt.SizeInPixels.Width * 4, diff = 0, ink = 0;
            for (int y = 0; y < StripH - 2; y++)
                for (int x = 0; x < StripW * 4; x++)
                {
                    byte a = px[(SlotA + y) * stride + x], b = px[(SlotB + y) * stride + x];
                    if (a != b) diff++;
                    if (a != 0) ink++;
                }
            Assert.True(ink > 1000, $"the strip drew nothing ({ink} non-zero bytes) — the comparison would be vacuous");
            return diff;
        });
    }

    // The hazard itself, as the screen pass met it: DIP units, the slot's offset as Win2D computed it.
    [Fact]
    public void InDipUnits_TheTwoAtlasSlotsDrawTheTextDifferently()
    {
        int diff = SlotDifference((ds, slot) => ds.Transform = Matrix3x2.CreateTranslation(1 / 1.5f, slot / 1.5f));
        Assert.True(diff > 0, "the two slots drew the same pixels in DIP units — the hazard this guards no longer reproduces");
    }

    // The fix: pixel units, the slot's offset in whole pixels (what Win2D's transform holds then), and the two
    // slots are pixel-identical.
    [Fact]
    public void WithAnExactPixelOrigin_TheTwoAtlasSlotsDrawTheSamePixels()
    {
        int diff = SlotDifference((ds, slot) =>
        {
            RichEditor.UseExactPixelOrigin(ds, 1);
            ds.Transform *= Matrix3x2.CreateTranslation(1, slot);
        });
        Assert.Equal(0, diff);
    }

    // A real screen pass goes through it.
    [Fact]
    public void TheScreenDrawPass_DrawsInPixelUnits()
    {
        var ed = UiThread.Run(() => new RichEditor { Document = ShortDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        bool drew = false;
        UiThread.RunAsync(async () =>
        {
            int before = ed.DrawPasses;
            typeof(RichEditor).GetMethod("InvalidateCanvas", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(ed, null);
            for (int i = 0; i < 320 && ed.DrawPasses == before; i++) await System.Threading.Tasks.Task.Delay(25);
            drew = ed.DrawPasses > before;
        });
        Assert.True(drew, "no draw pass");
        Assert.Equal(CanvasUnits.Pixels, UiThread.Run(() => ed.LastDrawUnits));
        UiThread.Run(() => ((Microsoft.UI.Xaml.Controls.Panel)ed.Parent).Children.Remove(ed));
    }

    // What reads the session's scale keeps seeing control DIPs: zoom × DPI in pixel units is the same map as the
    // zoom alone in DIP units.
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.75)]
    public void SessionToControlDips_IsTheSameInBothUnits(double zoom)
    {
        UiThread.Run(() =>
        {
            using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 10, 10, Dpi);
            using var ds = rt.CreateDrawingSession();
            ds.Transform = Matrix3x2.CreateScale((float)zoom);
            var dips = RichEditor.SessionToControlDips(ds);
            RichEditor.UseExactPixelOrigin(ds, zoom);
            Assert.Equal(Dpi, ds.Dpi); // the DPI is still reported in pixel units — SessionToControlDips relies on it
            var pixels = RichEditor.SessionToControlDips(ds);
            Assert.Equal(dips.M11, pixels.M11, 5);
            Assert.Equal(dips.M22, pixels.M22, 5);
            Assert.Equal(dips.M31, pixels.M31, 5);
            Assert.Equal(dips.M32, pixels.M32, 5);
        });
    }
}
