using System.Collections.Generic;
using System.Linq;
using WinUIRichEditor.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

// Lightness (2026-11-09): a RichEditorView took about 271 ms to construct, nearly all of it the toolbar — the strip
// was built three times (constructor, ToolbarLevel, Target; ~65 ms each) and the colour palettes and the table grid with
// every build. Those pickers' panels are made when first opened now. (The upstream toolbar also builds no strip without a
// target; here that hung this harness's UI thread, so the strip is still built — 2026-11-09.)
[Collection(UiTests.Collection)]
public class ToolbarLazyBuildTests
{
    // The picker flyouts (Flyout, not the MenuFlyout menus) hung on the strip's buttons.
    private static List<Flyout> Pickers(object? node, List<Flyout>? into = null)
    {
        into ??= new List<Flyout>();
        switch (node)
        {
            case Button { Flyout: Flyout f } b: into.Add(f); Pickers(b.Content, into); break;
            case Panel p: foreach (var c in p.Children) Pickers(c, into); break;
            case Border bd: Pickers(bd.Child, into); break;
            case ContentControl cc: Pickers(cc.Content, into); break;
        }
        return into;
    }

    // Colour ×2 and the table grid start empty: their panels are made on Flyout.Opening (ContentOnFirstOpen). Opening one is
    // left to the real app — a toolbar put in this harness's shared window never loads (it hangs the UI thread), so ShowAt cannot be driven here.
    [Fact]
    public void PickerPanels_AreNotBuiltWithTheStrip() => UiThread.Run(() =>
    {
        var tb = new RichEditorToolbar { ToolbarLevel = ToolbarLevel.Normal, Target = new RichEditor() };
        var pickers = Pickers(tb.Content);
        Assert.Equal(3, pickers.Count);
        Assert.All(pickers, f => Assert.Null(f.Content));
    });
}
