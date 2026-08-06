using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Caret geometry and vertical movement — the two areas with the worst defect history in this
/// repo and, until now, no automated verification at all.
/// <para>Both were rewritten once (caret placed on the line's BASELINE instead of its line box) and that
/// single change spawned three defects: downward movement, upward movement, and the initial alignment.
/// All three were invisible at 100% line spacing, where the line box and the text height happen to
/// coincide — a user found them at 200/300%. These tests exercise every rule at all three.</para>
/// <para>The methods are private, so they are reached by reflection, the same way the upstream peer's
/// fuzz drives its private table commands. The roadmap's plan to extract caret geometry as pure
/// functions is what eventually replaces this; until then the choice is reflection or no coverage.</para>
/// </summary>
public class ControlCaretTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    // The one constant that ties the two halves of caret placement together: where DirectWrite is told to
    // put the baseline, and where the glyph top is worked back from. If these ever disagree the caret
    // drifts off the text — which is what a hard-coded pair of rules did before.
    private const double BaselineFraction = 0.8;

    // ---- reflection plumbing ----------------------------------------------------------------------

    private static readonly FieldInfo CaretField = T.GetField("_caret", NP)!;
    private static readonly MethodInfo ToDocPoint = T.GetMethod("CaretToDocPoint", NP)!;
    private static readonly MethodInfo MoveVertical = T.GetMethod("MoveCaretVertical", NP)!;
    private static readonly MethodInfo Relayout = T.GetMethod("RelayoutToViewport", NP)!;

    private readonly record struct Geometry(double X, double Y, double Height, double LineTop, double LineBottom)
    {
        public double LineBoxHeight => LineBottom - LineTop;
    }

    private static void SetCaret(RichEditor ed, Paragraph p, int offset = 0)
        => CaretField.SetValue(ed, new TextPointer(p, offset));

    private static TextPointer GetCaret(RichEditor ed) => (TextPointer)CaretField.GetValue(ed)!;

    private static Geometry CaretGeometry(RichEditor ed)
    {
        object? boxed = ToDocPoint.Invoke(ed, new object?[] { GetCaret(ed) });
        Assert.NotNull(boxed);
        var ty = boxed!.GetType();
        double F(string n) => (double)ty.GetField(n)!.GetValue(boxed)!;
        return new Geometry(F("Item1"), F("Item2"), F("Item3"), F("Item4"), F("Item5"));
    }

    private static void Move(RichEditor ed, bool down) => MoveVertical.Invoke(ed, new object?[] { down, false });

    // ---- hosting ------------------------------------------------------------------------------------

    // Caret geometry comes from a real CanvasTextLayout, so the control has to be laid out for real —
    // and laying one out outside a visual tree overflows the stack (see UiThread). A window it is.
    //
    // ONE editor, hosted once, with its Document swapped per test. Two reasons, both learned the hard
    // way: closing a window ends the Application's message loop and takes the runtime with it, and
    // repeatedly swapping a live window's Content races — a swap occasionally never loads, and which
    // test paid for it moved around between runs. Replacing the Document is what a host application does
    // anyway, and it resets the editor's interaction state.
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

    private static void Hosted(FlowDocument doc, Action<RichEditor> body)
    {
        var editor = Shared.Value;
        UiThread.Run(() =>
        {
            editor.Document = doc;
            Relayout.Invoke(editor, null);
            body(editor);
        });
    }

    private static FlowDocument Lines(double spacing, params string[] texts)
    {
        var doc = new FlowDocument();
        foreach (var text in texts)
        {
            var p = new Paragraph { LineSpacing = spacing };
            if (text.Length > 0) p.Inlines.Add(new Run { Text = text });
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static List<Paragraph> Paragraphs(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().ToList();

    // ---- the caret is sized to the TEXT, not to the line box ----------------------------------------

    // A line box is taller than the glyphs it holds, and at 300% spacing it is three times taller. The
    // caret must not grow with it: it marks where you type, and a caret twice the height of the text was
    // the visible symptom users reported.
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void CaretHeight_IsTheTextHeight_AtEveryLineSpacing(double spacing)
    {
        Hosted(Lines(spacing, "first line", "second line", "third line"), ed =>
        {
            var paras = Paragraphs(ed);
            SetCaret(ed, paras[1]);
            var g = CaretGeometry(ed);

            // The line box really does grow with the spacing — otherwise this test proves nothing.
            if (spacing > 1.0)
                Assert.True(g.LineBoxHeight > 20, $"line box did not grow at {spacing:0.0}x (was {g.LineBoxHeight:0.0})");

            // 10pt body text: 10 * 4/3 px * 1.2 natural line factor = 16px.
            Assert.Equal(16.0, g.Height, 1);
            Assert.True(g.Height < g.LineBoxHeight + 0.01, "the caret is taller than its own line box");
        });
    }

    // Under CUSTOM (uniform) spacing the control tells DirectWrite to put the baseline at
    // BaselineFraction of the line box and works the glyph top back from the same constant. Deriving the
    // expected Y that way reproduces the rule exactly, so a change that reintroduces a second rule — or a
    // different constant on one side — fails here instead of drifting quietly at 200% and up.
    //
    // Natural spacing is deliberately NOT included: there the baseline is the FONT's own, not a fraction
    // of the box, so this formula does not apply (it lands ~0.3px off, which is the measured difference
    // between the two, not a defect). The rules that must hold everywhere are asserted separately below.
    [Theory]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void Caret_SitsOnTheFractionalBaseline_UnderCustomSpacing(double spacing)
    {
        Hosted(Lines(spacing, "first line", "second line", "third line"), ed =>
        {
            SetCaret(ed, Paragraphs(ed)[1]);
            var g = CaretGeometry(ed);

            double baseline = g.LineTop + g.LineBoxHeight * BaselineFraction;
            Assert.Equal(baseline - g.Height * BaselineFraction, g.Y, 1);
        });
    }

    // What has to hold at EVERY spacing: the caret lies within its own line box. "Drifted off the text"
    // is what the two-rules-for-one-thing version produced, and at 300% it was far enough out to break
    // vertical movement as well.
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void Caret_StaysInsideItsLineBox_AtEveryLineSpacing(double spacing)
    {
        Hosted(Lines(spacing, "first line", "second line", "third line"), ed =>
        {
            SetCaret(ed, Paragraphs(ed)[1]);
            var g = CaretGeometry(ed);

            Assert.InRange(g.Y, g.LineTop - 0.5, g.LineBottom);
            Assert.InRange(g.Y + g.Height, g.LineTop, g.LineBottom + 0.5);
        });
    }

    // An empty paragraph has no glyphs to measure, so it took a different path and produced a caret the
    // height of the whole line box. A blank line's caret has to match one on a line with text.
    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void Caret_OnABlankLine_MatchesOneOnALineWithText(double spacing)
    {
        Hosted(Lines(spacing, "has text", "", "has text too"), ed =>
        {
            var paras = Paragraphs(ed);

            SetCaret(ed, paras[0]);
            var withText = CaretGeometry(ed);
            SetCaret(ed, paras[1]);
            var blank = CaretGeometry(ed);

            Assert.Equal(withText.Height, blank.Height, 1);
            // Same rule, so the same offset from the top of its own line box.
            Assert.Equal(withText.Y - withText.LineTop, blank.Y - blank.LineTop, 1);
        });
    }

    // ---- vertical movement crosses the LINE, not the caret --------------------------------------------

    // The contract the roadmap states: a vertical probe must clear the LINE (LineTop upward,
    // LineBottom downward), not the caret. When it used the caret instead, the probe landed back inside
    // the same line box — the gap is 0.8x(line box - text height) upward, about 26px at 300%, so the move
    // silently failed and fell through to the next strategy. At 100% all three values coincide, which is
    // why nothing showed until a user tried 300%.
    [Theory]
    [InlineData(1.0)]
    [InlineData(2.0)]
    [InlineData(3.0)]
    public void VerticalMovement_CrossesParagraphs_AtEveryLineSpacing(double spacing)
    {
        Hosted(Lines(spacing, "first line", "second line", "third line"), ed =>
        {
            var paras = Paragraphs(ed);

            SetCaret(ed, paras[1]);
            Move(ed, down: false);
            Assert.Same(paras[0], GetCaret(ed).Paragraph);

            SetCaret(ed, paras[1]);
            Move(ed, down: true);
            Assert.Same(paras[2], GetCaret(ed).Paragraph);
        });
    }

    // The probe distance has to be measured from the line, so assert the geometry the movement relies on
    // rather than only its outcome: the caret sits strictly inside its line box, and the slack above it
    // grows with the spacing. That slack is precisely what a caret-based probe failed to clear.
    [Fact]
    public void TheGapAboveTheCaret_GrowsWithLineSpacing()
    {
        double SlackAbove(double spacing)
        {
            double slack = 0;
            Hosted(Lines(spacing, "first line", "second line", "third line"), ed =>
            {
                SetCaret(ed, Paragraphs(ed)[1]);
                var g = CaretGeometry(ed);
                slack = g.Y - g.LineTop;
            });
            return slack;
        }

        double at100 = SlackAbove(1.0), at300 = SlackAbove(3.0);
        Assert.True(at300 > at100 + 10,
            $"the gap a vertical probe must clear barely changed ({at100:0.0} -> {at300:0.0}); " +
            "if that were so the 300% regression could not have happened, so this test is measuring the wrong thing");
    }

    // ---- the case the 300% regression was actually reported as ----------------------------------------

    // "Press ↑ on the bottom line of a table cell and the caret jumps out of the table."
    //
    // In-cell movement probes only 2px past the line (LineTop/LineBottom), and the caret sits
    // BaselineFraction x (line box - text height) below the top of its box — about 26px at 300%. Probing
    // from the caret instead of the line therefore lands back inside the same box, step (a) fails, and
    // the caret falls through to the "leave the table" step. At 100% the three values coincide, so this
    // only ever showed at large spacing.
    //
    // This is the ONLY test here that catches that regression, and it took three attempts to write one
    // that does — the earlier shapes passed against a deliberately broken probe:
    //   · between top-level paragraphs, AdjacentTopLevelParagraph rescues the move;
    //   · between two paragraphs inside one cell, crossing the paragraph boundary clears the gap anyway.
    // Only moving between the wrapped LINES of a single cell paragraph has nothing to fall back on, and
    // there the broken probe leaves the caret exactly where it was — at 300% and not at 100%, which is
    // the signature of the original report.
    [Theory]
    [InlineData(1.0)]
    [InlineData(3.0)]
    public void VerticalMovement_FromTheBottomLineOfACell_MovesUpALine(double spacing)
    {
        var doc = new FlowDocument();
        var table = new TableBlock(1, 1);
        var cell = table.Cells[0][0];
        cell.Blocks.Clear();
        // ONE paragraph, long enough to wrap — the shape it was reported as. Two separate paragraphs in
        // the cell do NOT reproduce it: that crosses a paragraph boundary, which the probe clears even
        // when it starts from the wrong place. (Measured — the first version of this test passed against
        // a deliberately broken probe, which is the only reason the difference is documented here.)
        var wrapped = new Paragraph { LineSpacing = spacing };
        wrapped.Inlines.Add(new Run { Text = string.Join(" ", Enumerable.Repeat("wrapping cell text", 10)) });
        cell.Blocks.Add(wrapped);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above the table" } } });
        doc.Blocks.Add(table);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below the table" } } });

        Hosted(doc, ed =>
        {
            int end = string.Concat(wrapped.Inlines.OfType<Run>().Select(r => r.Text)).Length;

            SetCaret(ed, wrapped, end);          // the caret on the cell's LAST line
            SyncDesiredX(ed);
            var before = CaretGeometry(ed);
            Assert.True(before.LineTop > 0, "the cell paragraph did not wrap, so there is no line to move up from");

            Move(ed, down: false);

            var landed = GetCaret(ed);
            var after = CaretGeometry(ed);
            Assert.Same(wrapped, landed.Paragraph);
            Assert.True(after.Y < before.Y - 1,
                $"the caret did not move up a line at {spacing:0.0}x spacing " +
                $"(offset {end} -> {landed.Offset}, Y {before.Y:0.0} -> {after.Y:0.0})");
        });
    }

    // The probe uses the caret's remembered X, which a real move keeps up to date. Setting the caret by
    // hand skips that, so refresh it or the probe hunts at whatever column the previous test left behind.
    private static void SyncDesiredX(RichEditor ed)
        => T.GetField("_desiredCaretX", NP)!.SetValue(ed, CaretGeometry(ed).X);

    private static string Text(Paragraph? p)
        => p == null ? "<null>" : string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // Moving within a MULTI-LINE paragraph must step one visual line, not jump to the next paragraph —
    // the line-by-line path runs before the cross-paragraph probe for exactly this reason.
    [Fact]
    public void VerticalMovement_WithinAWrappedParagraph_StaysInIt()
    {
        string longText = string.Join(" ", Enumerable.Repeat("wrapping text that keeps going", 12));
        Hosted(Lines(1.0, "above", longText, "below"), ed =>
        {
            var paras = Paragraphs(ed);
            SetCaret(ed, paras[1], 0);
            var before = CaretGeometry(ed);

            Move(ed, down: true);

            Assert.Same(paras[1], GetCaret(ed).Paragraph);          // still the same paragraph
            var after = CaretGeometry(ed);
            Assert.True(after.Y > before.Y, "the caret did not move down a line");
        });
    }
}
