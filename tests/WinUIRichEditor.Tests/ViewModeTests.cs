using System;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>How the editor shows its document (upstream AvaloniaRichEditor PR #68): a bare editor always reflows to its
/// width, whatever paper the document names; pages are drawn in <see cref="RichEditorViewMode.Page"/>, which
/// <see cref="RichEditorView"/> opens in. The page setup stays the document's either way.</summary>
[Collection(UiTests.Collection)]
public class ViewModeTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static bool IsPaged(RichEditor ed) => (bool)typeof(RichEditor).GetProperty("IsPaged", NP)!.GetValue(ed)!;

    private static FlowDocument A4Document() => new()
    {
        PageSetup = new PageSetup { PageSize = RichEditorPageSize.A4 },
        Blocks = { new Paragraph { Inlines = { new Run { Text = "text" } } } },
    };

    // A small editing box given a document saved on A4 showed it as A4 pages, with a desk around them.
    [Fact]
    public void ABareEditor_ShowsAnA4DocumentContinuous_AndKeepsItsPaper() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = A4Document() };

        Assert.Equal(RichEditorViewMode.Continuous, ed.ViewMode);
        Assert.Equal(RichEditorPageSize.A4, ed.PageSize); // the document's paper is still read in…
        Assert.False(IsPaged(ed));                        // …but not drawn
        Assert.Equal(RichEditorPageSize.A4, ed.Document!.PageSetup?.PageSize); // and is saved as it came

        ed.ViewMode = RichEditorViewMode.Page;
        Assert.True(IsPaged(ed));
        ed.ViewMode = RichEditorViewMode.Continuous;
        Assert.False(IsPaged(ed));
        Assert.Equal(RichEditorPageSize.A4, ed.Document!.PageSetup?.PageSize); // a view change records nothing
    });

    // Not only the flag: in page view the document really is laid out on pages — a point of the document lands below
    // the desk's top gap and inside the page's margin — and in the continuous view it lands where it is. (Every
    // paged-intent test before this one passed with pages never drawn at all, so none of them would notice.)
    [Fact]
    public void PageView_PutsTheDocumentOnAPage_ContinuousDoesNot()
    {
        var ed = UiThread.Run(() => new RichEditor { Document = A4Document() });
        UiThread.Host(ed);
        try
        {
            UiThread.Run(() =>
            {
                var toView = typeof(RichEditor).GetMethod("DocToView", NP)!;
                var relayout = typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!;
                var p = new Windows.Foundation.Point(20, 30);

                relayout.Invoke(ed, null);
                var flat = (Windows.Foundation.Point)toView.Invoke(ed, new object[] { p })!;
                Assert.Equal(p.Y, flat.Y, 3);

                ed.ViewMode = RichEditorViewMode.Page;
                var paged = (Windows.Foundation.Point)toView.Invoke(ed, new object[] { p })!;
                Assert.True(paged.Y >= p.Y + ed.PagePadTop, $"paged {paged.Y}, doc {p.Y}, top pad {ed.PagePadTop}");
            });
        }
        finally { UiThread.Run(() => ((Microsoft.UI.Xaml.Controls.Panel)ed.Parent).Children.Remove(ed)); }
    }

    // Page view at a continuous paper still reflows.
    [Fact]
    public void PageView_WithContinuousPaper_StillReflows() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument(), ViewMode = RichEditorViewMode.Page };
        Assert.Equal(RichEditorPageSize.Continuous, ed.PageSize);
        Assert.False(IsPaged(ed));
    });

    // The full host shows pages.
    [Fact]
    public void RichEditorView_OpensInPageView() => UiThread.Run(() =>
    {
        var view = new RichEditorView();
        Assert.Equal(RichEditorViewMode.Page, view.Editor.ViewMode);
    });

    // Paper, orientation and margins are only offered where they show: a continuous editor draws no pages.
    [Fact]
    public void TheToolbarsPaperFields_ComeAndGoWithPageView() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = A4Document() };
        var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        object? Paper() => typeof(RichEditorToolbar).GetField("_paper", NP)!.GetValue(tb);
        object? Margin() => typeof(RichEditorToolbar).GetField("_marginBox", NP)!.GetValue(tb);
        void SyncNow() => typeof(RichEditorToolbar).GetMethod("Sync", NP)!.Invoke(tb, null);

        SyncNow();
        Assert.Null(Paper());
        Assert.Null(Margin());

        ed.ViewMode = RichEditorViewMode.Page;
        SyncNow();
        Assert.NotNull(Paper());
        Assert.NotNull(Margin());

        ed.ViewMode = RichEditorViewMode.Continuous;
        SyncNow();
        Assert.Null(Paper());
    });
}
