using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Zoom: the coordinate axis the engine owns, and the one nothing tested.
/// <para>This editor scales inside the engine rather than under a XAML transform, and that is not a
/// style choice — Win2D draws into an immediate-mode raster surface, so a transform above the control
/// would magnify already-rasterized output and blur the text at 200/300%. Keeping glyphs crisp means the
/// scale has to reach the drawing session, which means every piece of engine math has to fold the factor
/// in: <c>EffectiveZoom</c> appears in 15 places and the view↔doc conversion in 16 more.</para>
/// <para>That is the price of the design, and until this file it was paid without a receipt: no test in
/// the repo so much as mentioned <c>Zoom</c>. The Avalonia peer has the opposite arrangement (a
/// LayoutTransformControl, with the framework doing the coordinate math) and so has nothing to test
/// here — which is exactly why the two are NOT converging on one mechanism.</para>
/// <para>What is asserted is the invariant a user feels: <b>clicking a glyph selects that glyph, at any
/// zoom</b>. The pointer path is view point → <c>ViewToDoc</c> → <c>GetPositionFromPoint</c>, so the
/// tests drive that composition rather than any one half of it.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlZoomTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly MethodInfo ViewToDocM = T.GetMethod("ViewToDoc", NP)!;
    private static readonly MethodInfo DocToViewM = T.GetMethod("DocToView", NP)!;
    private static readonly MethodInfo ToDocPoint = T.GetMethod("CaretToDocPoint", NP)!;
    private static readonly MethodInfo HitTest = T.GetMethod("GetPositionFromPoint", NP)!;
    private static readonly MethodInfo Relayout = T.GetMethod("RelayoutToViewport", NP)!;
    private static readonly FieldInfo CaretField = T.GetField("_caret", NP)!;

    private static Point ViewToDoc(RichEditor ed, Point p) => (Point)ViewToDocM.Invoke(ed, new object?[] { p })!;
    private static Point DocToView(RichEditor ed, Point p) => (Point)DocToViewM.Invoke(ed, new object?[] { p })!;
    private static TextPointer? HitAt(RichEditor ed, Point docPoint) => (TextPointer?)HitTest.Invoke(ed, new object?[] { docPoint });

    private static void SetCaret(RichEditor ed, Paragraph p, int offset)
        => CaretField.SetValue(ed, new TextPointer(p, offset));

    // (X, Y, Height, LineTop, LineBottom) in DOCUMENT space — pre-zoom, like everything the layout does.
    private static (double X, double Y, double Height) CaretDocPoint(RichEditor ed)
    {
        object? boxed = ToDocPoint.Invoke(ed, new object?[] { (TextPointer)CaretField.GetValue(ed)! });
        Assert.NotNull(boxed);
        var ty = boxed!.GetType();
        double F(string n) => (double)ty.GetField(n)!.GetValue(boxed)!;
        return (F("Item1"), F("Item2"), F("Item3"));
    }

    // ---- hosting ------------------------------------------------------------------------------------

    // Coordinates come from a real layout, so the control has to be in a window (measuring outside a
    // visual tree overflows the stack — see UiThread). One editor, hosted once, document and zoom swapped
    // per test, and the zoom is always put back: it is a DP on a shared instance.
    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor
        {
            Document = new FlowDocument(),
            PageSize = RichEditorPageSize.Continuous,
        });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void Hosted(FlowDocument doc, double zoom, RichEditorPageSize size, Action<RichEditor> body)
    {
        var editor = Shared.Value;
        UiThread.Run(() =>
        {
            try
            {
                editor.PageSize = size;
                editor.Document = doc;
                editor.SetZoom(zoom);
                Relayout.Invoke(editor, null);
                body(editor);
            }
            finally
            {
                editor.SetZoom(1.0);
                editor.PageSize = RichEditorPageSize.Continuous;
            }
        });
    }

    private static FlowDocument Doc(int paragraphs = 6)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < paragraphs; i++)
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = $"paragraph {i} with enough text to measure against" });
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static List<Paragraph> Paragraphs(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().ToList();

    // ---- the conversion is invertible at every factor ------------------------------------------------

    // The cheap invariant underneath everything else: whatever DocToView produces, ViewToDoc must undo.
    // A missing factor on either side shows up here first, and it is checked at several points because a
    // conversion that only works at the origin is a conversion that does not work.
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void ViewAndDocumentSpace_RoundTrip_AtEveryZoom(double zoom)
    {
        Hosted(Doc(), zoom, RichEditorPageSize.Continuous, ed =>
        {
            foreach (var p in new[] { new Point(0, 0), new Point(37, 12), new Point(120, 240), new Point(300, 61) })
            {
                var back = ViewToDoc(ed, DocToView(ed, p));
                Assert.True(Math.Abs(back.X - p.X) < 0.01 && Math.Abs(back.Y - p.Y) < 0.01,
                    $"at {zoom:0.0}x, doc {p.X},{p.Y} -> view -> doc {back.X:0.###},{back.Y:0.###}");
            }
        });
    }

    // Paged mode stacks pages on a desk with gaps and centres them, so the conversion is no longer a bare
    // multiply — the page transform composes with the zoom. Both directions still have to invert.
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    public void ViewAndDocumentSpace_RoundTrip_InPagedMode(double zoom)
    {
        Hosted(Doc(120), zoom, RichEditorPageSize.A4, ed =>
        {
            foreach (var p in new[] { new Point(10, 5), new Point(80, 400), new Point(200, 1500) })
            {
                var back = ViewToDoc(ed, DocToView(ed, p));
                Assert.True(Math.Abs(back.X - p.X) < 0.01 && Math.Abs(back.Y - p.Y) < 0.01,
                    $"paged at {zoom:0.0}x, doc {p.X},{p.Y} -> view -> doc {back.X:0.###},{back.Y:0.###}");
            }
        });
    }

    // ---- what the user actually does -----------------------------------------------------------------

    // Click where a glyph is drawn and the caret lands on that glyph — at any zoom. This drives the whole
    // pointer composition (view point -> ViewToDoc -> GetPositionFromPoint), which is where a dropped
    // factor would actually reach a user.
    //
    // ⚠ The offset is asserted, not just the paragraph: hit-testing falls back to a nearby paragraph when
    // it misses, and a paragraph-only assertion passes on that fallback for the wrong reason (the caret
    // round's AdjacentTopLevelParagraph trap). The probe also aims at the caret's TOP + 1px rather than
    // its middle, because a line box has slack that swallows small drift.
    [Theory]
    [InlineData(0.5)]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void ClickingWhereAGlyphIsDrawn_LandsOnIt_AtEveryZoom(double zoom)
    {
        Hosted(Doc(), zoom, RichEditorPageSize.Continuous, ed =>
        {
            var target = Paragraphs(ed)[3];
            const int offset = 9;
            SetCaret(ed, target, offset);
            var doc = CaretDocPoint(ed);

            // Where that glyph is on the physical canvas, then back through the pointer path.
            var view = DocToView(ed, new Point(doc.X + 1, doc.Y + 1));
            var landed = HitAt(ed, ViewToDoc(ed, view));

            Assert.NotNull(landed);
            Assert.Same(target, landed!.Paragraph);
            Assert.True(Math.Abs(landed.Offset - offset) <= 1,
                $"at {zoom:0.0}x the click landed on offset {landed.Offset}, expected ~{offset}");
        });
    }

    // The caret's position on the canvas has to scale with the zoom — the document coordinate does not
    // change (layout is zoom-independent), only where it is painted.
    [Fact]
    public void TheCaretsCanvasPosition_ScalesWithTheZoom()
    {
        var doc = Doc();
        double y1 = 0, y2 = 0, docY1 = 0, docY2 = 0;

        Hosted(doc, 1.0, RichEditorPageSize.Continuous, ed =>
        {
            SetCaret(ed, Paragraphs(ed)[3], 4);
            var d = CaretDocPoint(ed);
            docY1 = d.Y;
            y1 = DocToView(ed, new Point(d.X, d.Y)).Y;
        });
        Hosted(doc, 2.0, RichEditorPageSize.Continuous, ed =>
        {
            SetCaret(ed, Paragraphs(ed)[3], 4);
            var d = CaretDocPoint(ed);
            docY2 = d.Y;
            y2 = DocToView(ed, new Point(d.X, d.Y)).Y;
        });

        Assert.True(Math.Abs(docY1 - docY2) < 0.01, $"the DOCUMENT position moved with zoom: {docY1} -> {docY2}");
        Assert.True(Math.Abs(y2 - y1 * 2) < 0.5, $"the canvas position did not double: {y1} -> {y2}");
    }

    // ---- the zoom property itself --------------------------------------------------------------------

    [Theory]
    [InlineData(99.0, 5.0)]
    [InlineData(0.01, 0.25)]
    [InlineData(1.75, 1.75)]
    public void SetZoom_ClampsToTheSupportedRange(double asked, double expected)
    {
        Hosted(Doc(), 1.0, RichEditorPageSize.Continuous, ed =>
        {
            ed.SetZoom(asked);
            Assert.Equal(expected, ed.Zoom, 3);
        });
    }

    // Fit-to-width is a MODE, not a one-off zoom: it keeps re-fitting as the view resizes, and an explicit
    // zoom is what leaves it. Both halves matter — a SetZoom that forgot to clear the flag would have the
    // next resize silently undo the user's zoom.
    [Fact]
    public void FitToWidth_IsAMode_AndAnExplicitZoomLeavesIt()
    {
        Hosted(Doc(40), 1.0, RichEditorPageSize.A4, ed =>
        {
            ed.FitToWidth();
            Assert.True(ed.IsFitWidth);

            ed.SetZoom(1.5);

            Assert.False(ed.IsFitWidth);
            Assert.Equal(1.5, ed.Zoom, 3);
        });
    }

    // Continuous mode reflows to the viewport already, so fitting is 1.0 there — the paged case is the one
    // that computes a factor from the paper width.
    [Fact]
    public void FitToWidth_InContinuousMode_IsOneToOne()
    {
        Hosted(Doc(), 2.0, RichEditorPageSize.Continuous, ed =>
        {
            ed.FitToWidth();
            Assert.Equal(1.0, ed.Zoom, 3);
        });
    }
}
