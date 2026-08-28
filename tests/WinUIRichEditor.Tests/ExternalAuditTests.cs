using System;
using System.Linq;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Regressions for the defects confirmed in the 2026-08-26 audit round (an external report's
/// claims, verified one by one against the code — most of them did not survive that check; these are
/// the ones that did).
/// <para>Everything here is headless: pure gates in the style of <c>ExtractedGateTests</c>, plus model
/// and formatter assertions. The two confirmed defects that are NOT here — the right-click hit test for
/// a cell image and the UI Automation notifications — need pointer injection and an attached assistive
/// client respectively, neither of which this process can synthesize.</para></summary>
public class ExternalAuditTests
{
    // ---- column resize: the bounds could invert, and Math.Clamp throws on min > max --------------

    [Theory]
    [InlineData(15, 15)]   // both below the floor — a nested table's own minimum is 15
    [InlineData(10, 100)]  // only the left one
    [InlineData(100, 16)]  // only the right one (RTF import's floor)
    [InlineData(5, 5)]     // an HTML <td width="5"> pair
    [InlineData(0, 0)]     // degenerate
    public void ColumnResize_SurvivesSubMinimumColumns(double left, double right)
    {
        foreach (double drag in new[] { -1000d, -30, -1, 0, 1, 30, 1000 })
        {
            double d = RichEditor.ClampColumnDelta(drag, left, right, 20);
            // The total is fixed by construction; the invariant that must hold is that neither side
            // goes negative — a negative column width would corrupt every geometry walk downstream.
            Assert.True(left + d >= 0, $"left {left}+{d} < 0");
            Assert.True(right - d >= 0, $"right {right}-{d} < 0");
        }
    }

    [Fact]
    public void ColumnResize_KeepsTheFloorWhenItFits()
    {
        // Unchanged behaviour for the normal case: 100|100 with a 20px floor may move ±80.
        Assert.Equal(80, RichEditor.ClampColumnDelta(500, 100, 100, 20));
        Assert.Equal(-80, RichEditor.ClampColumnDelta(-500, 100, 100, 20));
        Assert.Equal(12, RichEditor.ClampColumnDelta(12, 100, 100, 20));
    }

    // ---- Extract on an empty grid ---------------------------------------------------------------

    [Fact]
    public void ExtractFromAnEmptyTable_ReturnsAnEmptyTable()
    {
        // Math.Clamp(0, 0, Rows - 1) threw ArgumentException ("'0' cannot be greater than -1").
        var sub = new TableBlock(0, 0).Extract(0, 0, 0, 0);
        Assert.Equal(0, sub.Rows);
    }

    // ---- caret/delete steps over surrogate pairs ------------------------------------------------

    [Fact]
    public void StepOffset_TreatsASurrogatePairAsOnePosition()
    {
        const string s = "a\U0001F600b"; // a, emoji (2 chars), b  => length 4
        Assert.Equal(4, s.Length);

        Assert.Equal(1, RichEditor.StepOffset(s, 0, forward: true));
        Assert.Equal(3, RichEditor.StepOffset(s, 1, forward: true)); // over the pair, not into it
        Assert.Equal(4, RichEditor.StepOffset(s, 3, forward: true));
        Assert.Equal(4, RichEditor.StepOffset(s, 4, forward: true)); // clamped at the end

        Assert.Equal(3, RichEditor.StepOffset(s, 4, forward: false));
        Assert.Equal(1, RichEditor.StepOffset(s, 3, forward: false)); // over the pair
        Assert.Equal(0, RichEditor.StepOffset(s, 1, forward: false));
        Assert.Equal(0, RichEditor.StepOffset(s, 0, forward: false)); // clamped at the start
    }

    [Fact]
    public void StepOffset_LeavesEveryOtherCharacterAlone()
    {
        const string s = "ab￼c"; // U+FFFC is the inline-object placeholder: one char, one position
        for (int i = 0; i < s.Length; i++) Assert.Equal(i + 1, RichEditor.StepOffset(s, i, forward: true));
        for (int i = 1; i <= s.Length; i++) Assert.Equal(i - 1, RichEditor.StepOffset(s, i, forward: false));
    }

    // ---- RTF: a malformed numeric parameter used to discard the whole document -------------------

    [Theory]
    [InlineData(@"{\rtf1\ansi\fs-x hello}")]
    [InlineData(@"{\rtf1\ansi\li- hello}")]
    [InlineData(@"{\rtf1\ansi\b-\i0 hello}")]
    public void RtfWithASignThatIsNotAParameter_KeepsTheText(string rtf)
    {
        // A '-' not followed by a digit is not a parameter: the control word ends there and the sign is
        // literal text (Word reads these as text too). Consuming it produced the substring "-", and
        // int.Parse("-") threw out of a token loop with no per-word recovery — so this brace-balanced,
        // complete, perfectly readable RTF came back EMPTY and every character after the sign was lost.
        Assert.Contains("hello", PlainText(RtfDocumentFormatter.Parse(rtf)));
        Assert.True(RtfDocumentFormatter.TryParse(rtf, out var doc, out string? err), $"TryParse: {err}");
        Assert.Contains("hello", PlainText(doc));
    }

    // An over-wide parameter is a malformed TOKEN, not a damaged DOCUMENT.
    //
    // This assertion used to be the opposite — overflow threw, and that throw was the damage signal
    // TryParse/LoadRtf were built on. Converged with the upstream peer, whose reasoning wins: the spec
    // caps parameters at 32 bits so nothing valid is lost, and refusing to OPEN a complete, readable,
    // brace-balanced file over one out-of-range number is a false positive. It also contradicted the
    // fix directly above, which exists precisely so one bad token cannot cost the whole document.
    // Real damage is truncation, and UnclosedGroups still catches that (see the theory below).
    [Fact]
    public void RtfWithAnOverflowingParameter_IsNotDamage()
    {
        Assert.True(RtfDocumentFormatter.TryParse(
            @"{\rtf1\ansi\fs99999999999999999999 x\par}", out var doc, out string? error));
        Assert.Null(error);
        Assert.Contains("x", PlainText(doc));
    }

    // ...and the signal that still means damage. Truncation is the failure TryParse was created for:
    // it does not throw, so a half-copied file used to look like a clean parse of a shorter document.
    [Theory]
    [InlineData(@"{\rtf1\ansi {\*\broken")]
    [InlineData(@"{\rtf1\ansi\trowd\cellx1000 a\cell")]
    [InlineData(@"{\rtf1\ansi hello there")]
    public void TruncatedRtf_IsStillDamage(string truncated)
    {
        Assert.False(RtfDocumentFormatter.TryParse(truncated, out _, out string? error));
        Assert.Contains("truncated", error!);
    }

    // ---- colours: CSS percentage channels --------------------------------------------------------

    [Fact]
    public void PercentageRgbColours_Parse()
    {
        var red = ColorUtil.Parse("rgb(100%, 0%, 0%)");
        Assert.NotNull(red);
        Assert.Equal(255, red!.Value.R);
        Assert.Equal(0, red.Value.G);

        var half = ColorUtil.Parse("rgba(0, 0, 0, 50%)");
        Assert.NotNull(half);
        Assert.InRange(half!.Value.A, 126, 129);

        // The integer forms must keep behaving exactly as before.
        var plain = ColorUtil.Parse("rgb(255, 0, 0)");
        Assert.Equal(red, plain);
        var fractional = ColorUtil.Parse("rgba(0, 0, 0, 0.5)");
        Assert.NotNull(fractional);
        Assert.InRange(fractional!.Value.A, 126, 129);
    }

    private static string PlainText(FlowDocument doc)
        => string.Concat(doc.Blocks.OfType<Paragraph>()
            .SelectMany(p => p.Inlines.OfType<Run>().Select(r => r.Text)));
}
