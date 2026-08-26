using System;
using Windows.UI;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Guards for two optimisations whose failure mode is silent.
/// <para>The first: measurement layouts are now built WITHOUT colour, underline and strikethrough
/// (<c>CreateLayout(forMeasure: true)</c>) because DirectWrite draws with those and does not lay out with
/// them. If that is ever untrue, every measured height in the document drifts away from what is drawn —
/// caret geometry, hit-testing and pagination all read from measured heights — and no round-trip, fuzz or
/// pixel test in this repo would report it, because the model is not wrong and the page still renders.</para>
/// <para>The second: the per-paragraph caches must not keep paragraphs the document has dropped alive.
/// They were <c>Dictionary&lt;Paragraph, …&gt;</c>, i.e. strong references with no removal path — measured
/// before the fix, a removed paragraph survived a full GC.</para></summary>
[Collection(UiTests.Collection)]
public class LayoutCostTests
{
    // Every decoration CreateLayout skips when measuring, on runs of several sizes and weights, plus a
    // hyperlink (which forces the implicit blue + underline branch).
    private static Paragraph Decorated() => new()
    {
        Inlines =
        {
            new Run { Text = "plain text and then ", FontSize = 10 },
            new Run { Text = "underlined ", FontSize = 14, TextDecorations = TextDecorationFlags.Underline },
            new Run { Text = "struck ", FontSize = 18, TextDecorations = TextDecorationFlags.Strikethrough },
            new Run { Text = "both ", FontSize = 9, TextDecorations = TextDecorationFlags.Underline | TextDecorationFlags.Strikethrough },
            new Run { Text = "coloured ", FontSize = 24, Foreground = Color.FromArgb(255, 200, 30, 30) },
            new Run { Text = "a link that is long enough to wrap the line at least once ", FontSize = 10, NavigateUri = "https://example.com" },
        },
    };

    [Fact]
    public void MeasureLayoutMatchesDrawLayout()
    {
        UiThread.Run(() =>
        {
            var p = Decorated();
            var ed = new RichEditor();
            // Narrow enough that the paragraph wraps: a decoration that changed an advance would change
            // where the wrap falls, which shows up as a different height, not just a different width.
            foreach (double w in new[] { 120.0, 300.0, 698.0 })
            {
                var (measure, draw) = ed.MeasureBothWays(p, w);
                Assert.True(Math.Abs(measure.Height - draw.Height) < 0.001,
                    $"width {w}: measured height {measure.Height} != drawn height {draw.Height}");
                Assert.True(Math.Abs(measure.Width - draw.Width) < 0.001,
                    $"width {w}: measured width {measure.Width} != drawn width {draw.Width}");
            }
        });
    }

    // Separated and never inlined so no stack slot of the caller can keep the paragraph alive.
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference DropOneParagraph(RichEditor ed, FlowDocument doc, int index)
    {
        var weak = new WeakReference(doc.Blocks[index]);
        doc.Blocks.RemoveAt(index);
        // One edit, so the editor does what every real edit does: rebuild the block layout map and the
        // paragraph index without the removed paragraph. Both are whole-document maps that hold their
        // blocks strongly BY DESIGN — they are rebuilt on every relayout, so they never hold a stale
        // one. Without this step the test would be measuring those, not the side caches.
        ed.InsertText("x");
        return weak;
    }

    [Fact]
    public void ADroppedParagraphIsNotPinnedByTheCaches()
    {
        bool alive = UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            for (int i = 0; i < 400; i++)
                doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"paragraph {i}", FontSize = 10 } } });

            var ed = new RichEditor { Document = doc };
            _ = ed.GetStatus();                              // fills the stats cache, for every paragraph
            using (var page = ed.RenderPrintPage(0)) { }     // fills the height and line caches, likewise

            // Index 350 is far past the first page, so it is in the three SIDE caches (which measure the
            // whole document to size the scrollbar and to paginate) but not in the heavy layout cache —
            // that one is bounded and disposes native resources explicitly, so it stays a Dictionary and
            // only ever holds a viewport's worth. This is exactly the population that used to accumulate.
            var weak = DropOneParagraph(ed, doc, 350);

            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            return weak.IsAlive;
        });

        Assert.False(alive, "a paragraph removed from the document is still reachable — a per-paragraph cache is pinning it");
    }
}
