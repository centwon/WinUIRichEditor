using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The host link API, same names as upstream (its PR #87): <c>HasSelection</c>, <c>GetHyperlinkAt</c>
/// and <c>TrySetHyperlinkAt</c>, so a host builds its own link UI (a toolbar button, a menu on a clicked link).
/// The point a host passes is in the editor's own space; the editor puts its canvas inside a scroller with a
/// top margin, and the page view adds the page stack, so the point has to be mapped before the hit test.</summary>
[Collection(UiTests.Collection)]
public class ControlHyperlinkApiTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);
    private const string Url = "https://example.com/";

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static object? Call(RichEditor ed, string name, params object?[] args)
    {
        try { return T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    // A link followed by plain text, in the given view.
    private static void Hosted(bool paged, Action<RichEditor> body)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            ed.PageSize = paged ? RichEditorPageSize.A4 : RichEditorPageSize.Continuous;
            ed.ViewMode = paged ? RichEditorViewMode.Page : RichEditorViewMode.Continuous;
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = "link", NavigateUri = Url });
            p.Inlines.Add(new Run { Text = " and plain text" });
            var doc = new FlowDocument();
            doc.Blocks.Add(p);
            ed.Document = doc;
            ed.IsReadOnly = false;
            Call(ed, "RelayoutToViewport");
            try { body(ed); }
            finally { ed.IsReadOnly = false; ed.PageSize = RichEditorPageSize.Continuous; ed.ViewMode = RichEditorViewMode.Continuous; }
        });
    }

    private static Paragraph First(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().First();
    private static Run RunOf(RichEditor ed, string text) => First(ed).Inlines.OfType<Run>().Single(r => r.Text == text);

    private static void Select(RichEditor ed, int from, int to)
    {
        var p = First(ed);
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(p, from));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(p, to));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(p, to));
    }

    // A point on the link as a host sees it (e.GetCurrentPoint(editor).Position): found in document space with
    // the internal hit test, then taken to the canvas (DocToView) and from the canvas to the editor. It sits just
    // above the link line's bottom edge, so a point left unmapped by the canvas's top margin falls off the line.
    private static Point PointOnLink(RichEditor ed)
    {
        bool On(double x, double y) => Call(ed, "LinkRunAtPoint", new Point(x, y)) != null;
        for (double y = 0; y < 400; y += 1)
            for (double x = 0; x < 300; x += 2)
                if (On(x, y))
                {
                    x += 4;
                    while (On(x, y + 1)) y++;
                    var view = (Point)Call(ed, "DocToView", new Point(x, y - 1))!;
                    var canvas = (UIElement)T.GetField("_canvas", NP)!.GetValue(ed)!;
                    return canvas.TransformToVisual(ed).TransformPoint(view);
                }
        throw new Xunit.Sdk.XunitException("no link found");
    }

    [Fact]
    public void HasSelection_IsFalseForACaret_AndTrueForARange() => Hosted(false, ed =>
    {
        Select(ed, 6, 6);
        Assert.False(ed.HasSelection);
        Select(ed, 5, 8);
        Assert.True(ed.HasSelection);
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetHyperlinkAt_TakesAPointInTheEditorsOwnSpace(bool paged) => Hosted(paged, ed =>
    {
        Assert.Equal(Url, ed.GetHyperlinkAt(PointOnLink(ed)));
        Assert.Null(ed.GetHyperlinkAt(new Point(5, 600)));
    });

    [Fact]
    public void TrySetHyperlinkAt_ChangesOnlyTheClickedLink_KeepsTheSelection_AsOneUndoStep() => Hosted(true, ed =>
    {
        Select(ed, 9, 14); // "plain"
        var point = PointOnLink(ed);

        Assert.True(ed.TrySetHyperlinkAt(point, "  https://example.org/ "));

        Assert.Equal("https://example.org/", RunOf(ed, "link").NavigateUri); // trimmed, like SetHyperlink
        Assert.All(First(ed).Inlines.OfType<Run>().Where(r => r.Text != "link"), r => Assert.Null(r.NavigateUri));
        Assert.True(ed.HasSelection); // the host's selection is left alone

        ed.Undo();
        Assert.Equal(Url, RunOf(ed, "link").NavigateUri);
    });

    [Fact]
    public void TrySetHyperlinkAt_BlankRemovesTheLink_AndReportsNoLinkOrReadOnly() => Hosted(false, ed =>
    {
        var point = PointOnLink(ed);

        Assert.False(ed.TrySetHyperlinkAt(new Point(5, 600), "https://example.org/")); // no link there

        ed.IsReadOnly = true;
        Assert.False(ed.TrySetHyperlinkAt(point, null));
        Assert.Equal(Url, RunOf(ed, "link").NavigateUri);

        ed.IsReadOnly = false;
        Assert.True(ed.TrySetHyperlinkAt(point, " "));
        Assert.DoesNotContain(First(ed).Inlines.OfType<Run>(), r => !string.IsNullOrEmpty(r.NavigateUri));
        Assert.Null(ed.GetHyperlinkAt(point));
    });

    [Fact]
    public void TrySetHyperlinkAt_TheSameAddress_PushesNoUndoStep() => Hosted(false, ed =>
    {
        Assert.False(ed.CanUndo); // a fresh document
        Assert.True(ed.TrySetHyperlinkAt(PointOnLink(ed), Url));
        Assert.False(ed.CanUndo);
    });
}
