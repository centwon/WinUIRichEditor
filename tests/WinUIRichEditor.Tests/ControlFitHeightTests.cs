using System;
using System.Linq;
using WinUIRichEditor.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Height follows the content (2026-11-08): no
/// property — like WPF's TextBox, an editor given unbounded height (a StackPanel) is as tall as its text, and with
/// MaxHeight it grows up to that and then its own ScrollViewer scrolls. Upstream's FitHeightTests is the same check with
/// the host's scroller (Avalonia's editor does not scroll itself).</summary>
[Collection(UiTests.Collection)]
public class ControlFitHeightTests
{
    private static string Paras(int n) => string.Concat(Enumerable.Range(1, n).Select(i => $"<p>line {i}</p>"));

    // Layout passes and the SizeChanged-driven relayout they trigger; a few rounds settle it.
    private static void Settle(FrameworkElement root)
    {
        for (int i = 0; i < 6; i++)
        {
            UiThread.Run(root.UpdateLayout);
            System.Threading.Thread.Sleep(30);
        }
    }

    private static ScrollViewer InnerScroller(RichEditor ed) => (ScrollViewer)ed.Content;

    [Fact]
    public void InAStackPanel_TheEditorIsAsTallAsItsText_AndGrowsWithIt()
    {
        RichEditor ed = null!;
        StackPanel panel = null!;
        UiThread.Run(() =>
        {
            ed = new RichEditor();
            ed.LoadHtml(Paras(2));
            panel = new StackPanel { Width = 600, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(ed);
        });
        UiThread.Host(panel);
        Settle(panel);

        double two = UiThread.Run(() => ed.ActualHeight);
        Assert.InRange(two, 20, 200); // two lines

        UiThread.Run(() => ed.LoadHtml(Paras(10)));
        Settle(panel);
        double ten = UiThread.Run(() => ed.ActualHeight);
        Assert.True(ten - two > 100, $"10 lines {ten} vs 2 lines {two}"); // eight more lines (both controls keep 40 px under the text)
        Assert.Equal(0, UiThread.Run(() => InnerScroller(ed).ScrollableHeight), 1); // nothing to scroll: it grew instead
        UiThread.Run(() => panel.Visibility = Visibility.Collapsed);
    }

    // Found 2026-11-08: grown past a host page's viewport, the editor only scrolled its own (empty) scroller,
    // so the caret at the end of the text went off-screen; upstream's BringIntoView reached the host's.
    [Fact]
    public void GrownPastTheHostsViewport_TheHostScrollsToTheCaret()
    {
        RichEditor ed = null!;
        ScrollViewer outer = null!;
        UiThread.Run(() =>
        {
            ed = new RichEditor();
            ed.LoadHtml(Paras(40));
            var panel = new StackPanel();
            panel.Children.Add(ed);
            outer = new ScrollViewer
            {
                Content = panel, Width = 600, Height = 300,
                VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left,
            };
        });
        UiThread.Host(outer);
        Settle(outer);
        Assert.Equal(0, UiThread.Run(() => outer.VerticalOffset), 1);

        UiThread.Run(ed.FocusDocumentEnd);
        Settle(outer);
        double offset = UiThread.Run(() => outer.VerticalOffset);
        double height = UiThread.Run(() => ed.ActualHeight);
        Assert.True(offset > height - 300 - 100, $"host offset {offset}, editor {height}"); // scrolled to the end
        UiThread.Run(() => outer.Visibility = Visibility.Collapsed);
    }

    [Fact]
    public void WithMaxHeight_TheEditorGrowsToTheCap_ThenScrollsItself()
    {
        RichEditor ed = null!;
        StackPanel panel = null!;
        UiThread.Run(() =>
        {
            ed = new RichEditor { MaxHeight = 200 };
            ed.LoadHtml(Paras(2));
            panel = new StackPanel { Width = 600, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left };
            panel.Children.Add(ed);
        });
        UiThread.Host(panel);
        Settle(panel);

        double shortH = UiThread.Run(() => ed.ActualHeight);
        Assert.True(shortH < 200, $"short text: {shortH}");

        UiThread.Run(() => ed.LoadHtml(Paras(40)));
        Settle(panel);
        Assert.Equal(200, UiThread.Run(() => ed.ActualHeight), 1);
        Assert.True(UiThread.Run(() => InnerScroller(ed).ScrollableHeight) > 100);
        UiThread.Run(() => panel.Visibility = Visibility.Collapsed);
    }
}
