using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The two defects the upstream peer's height oracle found (AvaloniaRichEditor PR #58, 2026-09-30: an edited
/// editor must measure what a fresh editor measures for the same document), measured here.</summary>
[Collection(UiTests.Collection)]
public class LayoutCacheBackportTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    // NaN and 1.5 differ only in a double's top bits, and FNV's xor-multiply carries a difference only upward, so
    // two such changes in one edit could cancel in the signature and the cached layout and height were kept.
    [Fact]
    public void TwoWideChangesInOneEdit_NeverCancelInTheSignature()
    {
        var sig = T.GetMethod("ParagraphSig", BindingFlags.NonPublic | BindingFlags.Static)!;
        var cancelled = new List<string>();
        UiThread.Run(() =>
        {
            var tb = new TableBlock(1, 1);
            var cell = tb.Cells[0][0].Para;
            cell.Inlines.Add(new Run { Text = "cell" });
            var host = new Paragraph { Inlines = { new Run { Text = "ab" }, new InlineTable { Table = tb }, new Run { Text = "cdef" } } };
            foreach (var v in new[] { 1.0, 1.5, 2.0, 2.5, 3.0, 1.6, 0.8 })
                foreach (var heading in new[] { 0, 1, 2, 3 })
                {
                    host.HeadingLevel = heading;
                    host.LineSpacing = cell.LineSpacing = double.NaN;
                    long before = (long)sig.Invoke(null, [host])!;
                    host.LineSpacing = cell.LineSpacing = v;
                    if ((long)sig.Invoke(null, [host])! == before) cancelled.Add($"{v}/h{heading}");
                }
        });
        Assert.Empty(cancelled);
    }

    // Bullets take a gutter from the wrap width, so toggling a list changes the height. Oracle: a fresh editor
    // measuring the same document.
    [Fact]
    public void TogglingAList_ChangesTheMeasuredHeight_ToWhatAFreshEditorMeasures()
    {
        static FlowDocument Doc() => new() { Blocks = { new Paragraph { Inlines = { new Run { Text = string.Concat(Enumerable.Repeat("word ", 400)) } } } } };
        static double Height(RichEditor ed)
        {
            T.GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            return (double)T.GetField("_measuredHeight", NP)!.GetValue(ed)!;
        }

        // Continuous view wraps to viewport ÷ zoom: at zoom 1 the lines are ~1,260 wide and a bullet's gutter does not
        // change how many there are. Zoom 4 narrows them to ~300, as the upstream test measures.
        var ed = UiThread.Run(() => new RichEditor { Document = Doc(), PageSize = RichEditorPageSize.Continuous, Zoom = 4 });
        UiThread.Host(ed);
        var fresh = UiThread.Run(() => new RichEditor { PageSize = RichEditorPageSize.Continuous, Zoom = 4 });
        UiThread.Host(fresh);
        UiThread.Run(() =>
        {
            double plain = Height(ed);
            T.GetMethod("SelectAll", NP, Type.EmptyTypes)!.Invoke(ed, null);
            ed.ToggleBullet();
            double bulleted = (double)T.GetField("_measuredHeight", NP)!.GetValue(ed)!;
            Assert.True(bulleted > plain, $"height {plain} -> {bulleted}: the gutter narrowed the lines, so there are more of them");

            var copy = Doc();
            ((Paragraph)copy.Blocks[0]).ListType = ListKind.Bullet;
            fresh.Document = copy;
            Assert.Equal(Height(fresh), bulleted, 2);

            ed.ToggleBullet();
            Assert.Equal(plain, (double)T.GetField("_measuredHeight", NP)!.GetValue(ed)!, 2);
        });
    }
}
