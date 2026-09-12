using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The caret's size inside a heading.
/// <para>The caret is sized from the font size at the caret. Inside a heading that size used to be the
/// heading's, whatever the run said — while the renderer draws an explicitly sized run at its OWN size
/// (only unstyled runs take the heading size). Measured before the fix, on an H1: an explicit 36pt run got
/// a 32.0 caret where the same run in body text gets 57.6, and an explicit 12pt run got 32.0 — taller than
/// its own 21.3 line box. The caret now uses the renderer's rule (<c>DrawnRunSize</c>), the one the caret
/// format already shares.</para>
/// <para>Judged against a REFERENCE — the same run in an ordinary paragraph — rather than a formula, so the
/// test says "sized like the text it sits in" and survives any change to how caret height is computed.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlCaretHeadingTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    // Caret geometry comes from a real layout: hosted, one editor for the class (see ControlCaretTests).
    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static Paragraph Para(int heading, double size)
    {
        var p = new Paragraph { HeadingLevel = heading };
        p.Inlines.Add(new Run { Text = "Mxg Mxg", FontSize = size });
        return p;
    }

    // (caret height, line box height) at offset 2 of each paragraph.
    private static (double caret, double lineBox)[] Measure(RichEditor ed, params Paragraph[] paras)
    {
        var doc = new FlowDocument();
        foreach (var p in paras) doc.Blocks.Add(p);
        ed.Document = doc;
        typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
        return ed.Document!.Blocks.OfType<Paragraph>().Select(p =>
        {
            var box = typeof(RichEditor).GetMethod("CaretToDocPoint", NP)!.Invoke(ed, new object[] { new TextPointer(p, 2) })!;
            var ty = box.GetType();
            double F(string n) => (double)ty.GetField(n)!.GetValue(box)!;
            return (F("Item3"), F("Item5") - F("Item4"));
        }).ToArray();
    }

    [Theory]
    [InlineData(36.0)] // explicit, larger than the H1 size: the caret was too SHORT
    [InlineData(12.0)] // explicit, smaller: the caret was taller than its own line box
    public void TheCaretOnAnExplicitlySizedRunInAHeading_IsSizedLikeThatRun(double size)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            var m = Measure(ed, Para(0, size), Para(1, size));
            var (body, heading) = (m[0], m[1]);

            Assert.True(Math.Abs(heading.caret - body.caret) < 0.5,
                $"{size}pt in an H1: caret {heading.caret:0.0}, the same run in body text {body.caret:0.0}");
            Assert.True(heading.caret <= heading.lineBox + 0.01,
                $"{size}pt in an H1: the caret ({heading.caret:0.0}) is taller than its line box ({heading.lineBox:0.0})");
        });
    }

    // The case that was already right, pinned so the fix cannot trade it away: an UNSTYLED run in an H1 is
    // drawn at the heading size (20pt), and so is its caret.
    [Fact]
    public void TheCaretOnAnUnstyledRunInAHeading_IsSizedLikeTheHeading()
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            var m = Measure(ed, Para(0, 20), Para(1, 10)); // 10 = the model's body default: "unstyled"
            Assert.True(Math.Abs(m[1].caret - m[0].caret) < 0.5,
                $"unstyled H1: caret {m[1].caret:0.0}, a 20pt body run {m[0].caret:0.0}");
        });
    }
}
