using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The toolbar's vector icons and the vector print path (2026-09-12).
/// <para>Icons: the toolbar drew Segoe symbol-font glyphs, with text stand-ins where the font had none
/// (⇥ ⇤ indent, "1." numbered list, 🖌 format painter, ▦ table). It now draws the upstream peer's pictures as
/// paths — WinUI has no code-side path parser, so <c>PathMarkup</c> is ours. B/I/U/S are styled letters, as
/// upstream.</para>
/// <para>Print: pages used to reach the print dialog as 150 DPI bitmaps, so a PDF printer's output was an
/// image with no text in it. The dialog now draws each page with the editor's own renderer straight into the
/// printer's session — which needs the page drawer to compose with the caller's (fit-to-paper) transform.
/// The dialog itself has no automated check; this pins the drawing it relies on.</para></summary>
[Collection(UiTests.Collection)]
public class ToolbarVectorIconAndPrintTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static readonly RichEditorIcon[] VectorSlots =
    {
        RichEditorIcon.FormatPainter, RichEditorIcon.BulletList, RichEditorIcon.NumberedList, RichEditorIcon.LineSpacing,
        RichEditorIcon.IndentIncrease, RichEditorIcon.IndentDecrease, RichEditorIcon.InsertTable, RichEditorIcon.InsertImage,
        RichEditorIcon.InsertDivider, RichEditorIcon.Undo, RichEditorIcon.Redo, RichEditorIcon.Highlight,
        RichEditorIcon.Export, RichEditorIcon.Import, RichEditorIcon.Print, RichEditorIcon.Find, RichEditorIcon.ClearFormatting,
    };

    private static IEnumerable<DependencyObject> Walk(object? root)
    {
        if (root is not DependencyObject d) yield break;
        yield return d;
        switch (d)
        {
            case Panel p: foreach (var c in p.Children) foreach (var x in Walk(c)) yield return x; break;
            case Border b: foreach (var x in Walk(b.Child)) yield return x; break;
            case ContentControl cc: foreach (var x in Walk(cc.Content)) yield return x; break;
            case Viewbox v: foreach (var x in Walk(v.Child)) yield return x; break;
        }
    }

    // ---- icons ------------------------------------------------------------------------------------

    // Every toolbar slot that has a picture builds one — each layer's path data parses into a figure with
    // segments — and the letters do not (they are styled text).
    [Fact]
    public void EveryPictureSlotBuildsItsPaths_AndTheLettersAreText() => UiThread.Run(() =>
    {
        foreach (var slot in VectorSlots)
        {
            var box = Assert.IsType<Viewbox>(ToolbarIcons.CreateVector(slot));
            var paths = ((Canvas)box.Child).Children.OfType<Microsoft.UI.Xaml.Shapes.Path>().ToList();
            Assert.True(paths.Count > 0, $"{slot}: no paths");
            foreach (var path in paths)
            {
                var g = Assert.IsType<PathGeometry>(path.Data);
                Assert.True(g.Figures.Sum(f => f.Segments.Count) > 0, $"{slot}: a layer with no segments");
            }
        }
        foreach (var letter in new[] { RichEditorIcon.Bold, RichEditorIcon.Italic, RichEditorIcon.Underline, RichEditorIcon.Strikethrough })
            Assert.Null(ToolbarIcons.CreateVector(letter));
    });

    // The parser on the shapes the icons use: a relative move after an absolute one, arcs drawing a dot,
    // H/V from the current point, line-tos implied after a move, a quadratic, and Z returning to the start.
    [Fact]
    public void PathMarkup_ReadsTheIconDialect() => UiThread.Run(() =>
    {
        var dot = PathMarkup.Parse("M4.5 7 m-1.4 0 a1.4 1.4 0 1 0 2.8 0 a1.4 1.4 0 1 0 -2.8 0 Z");
        var ring = dot.Figures[^1];
        Assert.Equal(3.1, ring.StartPoint.X, 6);
        Assert.Equal(7, ring.StartPoint.Y, 6);
        var arc = Assert.IsType<ArcSegment>(ring.Segments[0]);
        Assert.Equal(5.9, arc.Point.X, 6);
        Assert.Equal(SweepDirection.Counterclockwise, arc.SweepDirection);
        Assert.Equal(3.1, ((ArcSegment)ring.Segments[1]).Point.X, 6);
        Assert.True(ring.IsClosed);

        var tray = PathMarkup.Parse("M5 14 V19 H19 V14").Figures.Single();
        Assert.Equal(new[] { (5.0, 19.0), (19.0, 19.0), (19.0, 14.0) },
            tray.Segments.Cast<LineSegment>().Select(s => (s.Point.X, s.Point.Y)));

        var implied = PathMarkup.Parse("M3 16 9 11 13 15").Figures.Single();
        Assert.Equal(2, implied.Segments.Count);

        var q = Assert.IsType<QuadraticBezierSegment>(PathMarkup.Parse("M2.7 11 Q3 10.2 3.9 10.2").Figures.Single().Segments.Single());
        Assert.Equal(3.9, q.Point2.X, 6);

        Assert.Throws<FormatException>(() => PathMarkup.Parse("M1 2 C3 4 5 6 7 8"));
    });

    // In the built toolbar no picture slot falls back to text any more; the only text faces left are the
    // letters, and U and S carry their decoration.
    [Fact]
    public void TheToolbarShowsNoTextStandIns_AndTheLettersShowWhatTheyDo() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };
        var toolbar = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        var textFaces = Walk(toolbar.Content).OfType<ButtonBase>()
            .Select(b => b.Content).OfType<TextBlock>().Select(t => t.Text).ToList();
        Assert.True(textFaces.All(t => t is "B" or "I" or "U" or "S"), "text faces: " + string.Join(" ", textFaces));

        TextBlock Face(string field) => (TextBlock)((ToggleButton)typeof(RichEditorToolbar).GetField(field, NP)!.GetValue(toolbar)!).Content;
        Assert.Equal(Windows.UI.Text.TextDecorations.Underline, Face("_underline").TextDecorations);
        Assert.Equal(Windows.UI.Text.TextDecorations.Strikethrough, Face("_strike").TextDecorations);
    });

    // A path is drawn in fixed ink and does not follow the button's disabled foreground the way a FontIcon
    // did, so it is dimmed with the button: undo with no history, then with some.
    [Fact]
    public void AVectorIconDimsWithItsDisabledButton() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };
        var toolbar = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        var undo = (Button)typeof(RichEditorToolbar).GetField("_undo", NP)!.GetValue(toolbar)!;
        var icon = Assert.IsType<Viewbox>(undo.Content);
        Assert.False(undo.IsEnabled);
        Assert.True(icon.Opacity < 1);

        ed.InsertText("x");
        Assert.True(undo.IsEnabled);
        Assert.Equal(1, icon.Opacity);
    });

    // ---- print ------------------------------------------------------------------------------------

    // The bounds of everything not white, in pixels.
    private static (int L, int T, int R, int B) Ink(CanvasRenderTarget rt)
    {
        var px = rt.GetPixelBytes();
        int w = (int)rt.SizeInPixels.Width, h = (int)rt.SizeInPixels.Height;
        int l = w, t = h, r = -1, b = -1;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                if (px[i] > 200 && px[i + 1] > 200 && px[i + 2] > 200) continue;
                l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
            }
        return (l, t, r, b);
    }

    // The print dialog's page drawer draws the page PDF export draws, and draws it where the caller's
    // transform puts it — the dialog sets a fit-to-paper transform first. Drawing under an offset must move
    // the whole page by exactly that offset; a drawer that replaced the transform instead of composing with it
    // prints every page unscaled at the paper's corner.
    [Fact]
    public void ThePrintPageDrawer_DrawsTheExportedPage_UnderTheCallersTransform() => UiThread.Run(() =>
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "Printed as text, not pixels", FontSize = 18 });
        var doc = new FlowDocument();
        doc.Blocks.Add(p);
        var ed = new RichEditor { Document = doc };

        using var exported = ed.RenderPrintPage(0, 96);
        var expected = Ink(exported);
        Assert.True(expected.R > expected.L, "the reference page drew nothing");

        var paper = ed.GetPaperPixelSize();
        using var shifted = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (float)paper.Width + 100, (float)paper.Height + 50, 96);
        using (var ds = shifted.CreateDrawingSession())
        {
            ds.Clear(Colors.White);
            ds.Transform = Matrix3x2.CreateTranslation(100, 50);
            int pages = 0;
            ed.WithPrintPages((count, draw) => { pages = count; draw(ds, 0); });
            Assert.Equal(1, pages);
        }
        Assert.Equal((expected.L + 100, expected.T + 50, expected.R + 100, expected.B + 50), Ink(shifted));
    });
}
