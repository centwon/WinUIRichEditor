using System;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The host surface of the toolbar, the view and the editor's appearance — members no test referenced
/// (2026-09-14 re-scan). Measured first: the toolbar's image button was enabled with nothing to pick with and did
/// nothing when clicked, and a font list curated at run time never reached the toolbar (no change signal; upstream
/// listens for it). The rest is pinned as it was: host items across every rebuild, the page and file sections,
/// Print appearing with its handler, the view's forwarders, the find-bar gate, and how brushes resolve.</summary>
[Collection(UiTests.Collection)]
public class ControlHostSurfaceTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type TB = typeof(RichEditorToolbar);
    private static readonly Type ED = typeof(RichEditor);

    private static object? Call(object target, string name, params object?[] args)
    {
        try { return target.GetType().GetMethod(name, NP)!.Invoke(target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static T? Field<T>(object target, string name) where T : class => target.GetType().GetField(name, NP)!.GetValue(target) as T;
    private static Panel Strip(RichEditorToolbar tb) => Field<Panel>(tb, "_strip")!;

    private static RichEditor Editor(string html = "<p>abcd</p>")
    {
        var ed = new RichEditor();
        ed.LoadHtml(html);
        return ed;
    }

    // ---- the toolbar ---------------------------------------------------------------------------------------------

    // A host's own buttons sit first and last in the strip, once, through every rebuild the toolbar does on its own —
    // a section toggled, the density changed, the language switched. A WinUI element with a parent cannot be added
    // again (0x800F1000), so each rebuild has to let go of them first.
    [Fact]
    public void HostItems_StayFirstAndLast_ThroughEveryRebuild() => UiThread.Run(() =>
    {
        var tb = new RichEditorToolbar { Target = Editor(), ToolbarLevel = ToolbarLevel.Maximum };
        var lead = new Button { Content = "lead" };
        var trail = new Button { Content = "trail" };
        tb.LeadingItems.Add(lead);
        tb.TrailingItems.Add(trail);

        void AssertPlaced(string after)
        {
            var kids = Strip(tb).Children;
            Assert.True(ReferenceEquals(kids.First(), lead), $"lead is not first after {after}");
            Assert.True(ReferenceEquals(kids.Last(), trail), $"trail is not last after {after}");
            Assert.Equal(1, kids.Count(k => ReferenceEquals(k, lead)));
        }

        AssertPlaced("adding them");
        tb.ShowFileActions = false; AssertPlaced("hiding the file actions");
        tb.ShowPageControls = false; AssertPlaced("hiding the page controls");
        tb.ToolbarLevel = ToolbarLevel.Minimal; AssertPlaced("a density change");
        Call(tb, "RebuildForLanguage"); AssertPlaced("a language change");

        tb.LeadingItems.Remove(lead);
        Assert.DoesNotContain(lead, Strip(tb).Children);
    });

    [Fact]
    public void PageControls_AndFileActions_ComeAndGoWithTheirSwitches() => UiThread.Run(() =>
    {
        var tb = new RichEditorToolbar { Target = Editor(), ToolbarLevel = ToolbarLevel.Maximum };
        Assert.NotNull(Field<ComboBox>(tb, "_zoom"));
        Assert.NotNull(Field<Button>(tb, "_exportBtn"));

        tb.ShowPageControls = false;
        tb.ShowFileActions = false;
        Assert.Null(Field<ComboBox>(tb, "_zoom"));
        Assert.Null(Field<Button>(tb, "_exportBtn"));

        tb.ShowPageControls = true;
        tb.ShowFileActions = true;
        Assert.NotNull(Field<ComboBox>(tb, "_zoom"));
        Assert.NotNull(Field<Button>(tb, "_exportBtn"));
    });

    // Printing is the host's, so Print shows only while someone handles it — through the view too.
    [Fact]
    public void Print_ShowsOnlyWhileAHandlerIsAttached() => UiThread.Run(() =>
    {
        var view = new RichEditorView { Document = new FlowDocument() };
        Button Print() => Field<Button>(view.Toolbar, "_printBtn")!;
        Assert.Equal(Visibility.Collapsed, Print().Visibility);

        int clicks = 0;
        EventHandler h = (_, _) => clicks++;
        view.PrintRequested += h;
        Assert.Equal(Visibility.Visible, Print().Visibility);

        view.PrintRequested -= h;
        Assert.Equal(Visibility.Collapsed, Print().Visibility);
    });

    // The button did nothing, enabled, with neither a host picker nor a window handle: PickAndInsertImageAsync
    // returned silently. Now either one enables it — the handle through the editor's own picker, as upstream falls
    // back to its own.
    [Fact]
    public void TheImageButton_IsEnabledOnlyWithSomethingToPickWith() => UiThread.Run(() =>
    {
        var tb = new RichEditorToolbar { Target = Editor(), ToolbarLevel = ToolbarLevel.Maximum };
        Button Image() => Field<Button>(tb, "_imageBtn")!;
        Assert.False(Image().IsEnabled);

        tb.WindowHandle = 1;
        Assert.True(Image().IsEnabled);
        tb.WindowHandle = 0;
        Assert.False(Image().IsEnabled);

        tb.ImagePicker = () => Task.FromResult<byte[]?>(null);
        Assert.True(Image().IsEnabled);
    });

    [Fact]
    public void TheImageButton_InsertsWhatTheHostsPickerReturns()
    {
        const string png = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";
        int images = -1;
        UiThread.RunAsync(async () =>
        {
            var ed = Editor();
            var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum,
                                             ImagePicker = () => Task.FromResult<byte[]?>(Convert.FromBase64String(png)) };
            await (Task)Call(tb, "PickAndInsertImageAsync")!;
            images = ed.Document!.Blocks.OfType<ImageBlock>().Count();
        });
        Assert.Equal(1, images);
    }

    // No signal left the editor when the host set a new font list, so the toolbar kept the old one. It rebuilds on
    // the change now — and only then: an ordinary status change (the control) keeps the same combo.
    [Fact]
    public void ANewFontList_ReachesTheToolbar_AndOnlyThatRebuildsIt() => UiThread.Run(() =>
    {
        var ed = Editor();
        var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        var before = Field<ComboBox>(tb, "_font")!;

        ed.InsertText("x");
        Assert.Same(before, Field<ComboBox>(tb, "_font"));

        ed.FontFamilyChoices = new[] { "Arial", "Georgia" };
        var after = Field<ComboBox>(tb, "_font")!;
        Assert.NotSame(before, after);
        Call(tb, "PopulateFontList");
        Assert.Equal(new[] { "Arial", "Georgia" }, after.Items.Cast<ComboBoxItem>().Select(i => (string)i.Content));

        // A toolbar moved to another editor stops listening to the first one.
        tb.Target = Editor();
        var moved = Field<ComboBox>(tb, "_font");
        ed.FontFamilyChoices = new[] { "Verdana" };
        Assert.Same(moved, Field<ComboBox>(tb, "_font"));
    });

    // ---- the view ------------------------------------------------------------------------------------------------

    [Fact]
    public void TheView_ForwardsItsSurface() => UiThread.Run(() =>
    {
        var view = new RichEditorView { Document = new FlowDocument() };
        Func<Task<byte[]?>> picker = () => Task.FromResult<byte[]?>(null);

        view.ImagePicker = picker;
        view.WindowHandle = 42;
        view.ShowFileActions = false;
        view.ShowCaretWhenReadOnly = true;

        Assert.Same(picker, view.Toolbar.ImagePicker);
        Assert.Equal((nint)42, view.Toolbar.WindowHandle);
        Assert.False(view.Toolbar.ShowFileActions);
        Assert.True(view.Editor.ShowCaretWhenReadOnly);
        Assert.Equal(view.Toolbar.WindowHandle, view.WindowHandle);
    });

    // ZoomFactor is the editor's zoom, clamped the same way.
    [Fact]
    public void ZoomFactor_IsTheEditorsZoom_Clamped() => UiThread.Run(() =>
    {
        var view = new RichEditorView { Document = new FlowDocument() };
        view.ZoomFactor = 1.5;
        Assert.Equal(1.5, view.Editor.Zoom);
        view.ZoomFactor = 9;
        Assert.Equal(5.0, view.ZoomFactor);
        view.ZoomFactor = 0.01;
        Assert.Equal(0.25, view.ZoomFactor);
    });

    // A host with its own find UI turns the built-in bar off; find being disabled keeps it closed too.
    [Fact]
    public void TheBuiltInFindBar_OpensOnlyWhenAllowed() => UiThread.Run(() =>
    {
        Visibility Bar(RichEditorView v) => Field<Border>(v, "_findBarHost")!.Visibility;

        var off = new RichEditorView { Document = new FlowDocument(), ShowBuiltInFindBar = false };
        off.ShowFindBar(false);
        Assert.Equal(Visibility.Collapsed, Bar(off));

        var noFind = new RichEditorView { Document = new FlowDocument() };
        noFind.Editor.AllowFindReplace = false;
        noFind.ShowFindBar(false);
        Assert.Equal(Visibility.Collapsed, Bar(noFind));

        var on = new RichEditorView { Document = new FlowDocument() };
        on.ShowFindBar(false);
        Assert.Equal(Visibility.Visible, Bar(on));
    });

    // ---- the editor's appearance and small commands --------------------------------------------------------------

    // Win2D draws with a colour, so a solid brush gives its colour and anything else the default — never a throw.
    [Fact]
    public void Brushes_ResolveToTheirColour_OrTheDefault() => UiThread.Run(() =>
    {
        var ed = Editor();
        Windows.UI.Color Get(string name) => (Windows.UI.Color)ED.GetProperty(name, NP)!.GetValue(ed)!;
        var red = Windows.UI.Color.FromArgb(255, 200, 0, 0);

        ed.TextForeground = new SolidColorBrush(red);
        ed.CaretBrush = new SolidColorBrush(red);
        ed.SelectionBrush = new SolidColorBrush(red);
        Assert.Equal(red, Get("EffectiveTextColor"));
        Assert.Equal(red, Get("CaretColor"));
        Assert.Equal(red, Get("SelectionFill"));

        ed.TextForeground = new LinearGradientBrush();
        Assert.Equal(Colors.Black, Get("EffectiveTextColor"));

        var canvas = ED.GetField("_canvas", NP)!.GetValue(ed)!;
        Windows.UI.Color Clear() => (Windows.UI.Color)canvas.GetType().GetProperty("ClearColor")!.GetValue(canvas)!;
        ed.CanvasBackground = new SolidColorBrush(red);
        Assert.Equal(red, Clear());
        ed.CanvasBackground = null;
        Assert.Equal(Colors.Transparent, Clear());
    });

    // Cut = copy + delete as one undo step, the cut text on the in-app clipboard; a viewer cuts nothing.
    [Fact]
    public void Cut_CopiesAndDeletes_AsOneUndoStep_AndNotInAViewer() => UiThread.Run(() =>
    {
        var ed = Editor();
        var p = (Paragraph)ed.Document!.Blocks[0];
        ED.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(p, 1));
        ED.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(p, 3));
        ED.GetField("_caret", NP)!.SetValue(ed, new TextPointer(p, 3));

        ed.CutAsync().GetAwaiter().GetResult();
        Assert.Equal("ad", ed.GetPlainText().Trim());
        var clip = (FlowDocument)ED.GetField("_internalClipboardDoc", NP)!.GetValue(ed)!;
        Assert.Equal("bc", string.Concat(clip.Blocks.OfType<Paragraph>().SelectMany(q => q.Inlines).OfType<Run>().Select(r => r.Text)));
        ed.Undo();
        Assert.Equal("abcd", ed.GetPlainText().Trim());

        var viewer = Editor();
        viewer.IsReadOnly = true;
        var vp = (Paragraph)viewer.Document!.Blocks[0];
        ED.GetField("_selStart", NP)!.SetValue(viewer, new TextPointer(vp, 1));
        ED.GetField("_selEnd", NP)!.SetValue(viewer, new TextPointer(vp, 3));
        viewer.CutAsync().GetAwaiter().GetResult();
        Assert.Equal("abcd", viewer.GetPlainText().Trim());
    });

    // Outside a window there is nowhere to show the dialog: a quiet no-op, not a throw from a fire-and-forget call.
    [Fact]
    public void EditHyperlink_OutsideAWindow_DoesNothing() => UiThread.Run(() =>
    {
        var ed = Editor();
        ed.EditHyperlinkAsync().GetAwaiter().GetResult();
        Assert.False(ed.IsModified);
    });

    // The original's names still work: SetFontFamily is SetRunFontFamily, ToRtf is the RTF writer.
    [Fact]
    public void TheCompatibilityNames_DoWhatTheirTargetsDo() => UiThread.Run(() =>
    {
        var ed = Editor();
        var p = (Paragraph)ed.Document!.Blocks[0];
        ED.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(p, 0));
        ED.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(p, 4));
        ED.GetField("_caret", NP)!.SetValue(ed, new TextPointer(p, 4));

        ed.SetFontFamily("Georgia");

        Assert.All(p.Inlines.OfType<Run>(), r => Assert.Equal("Georgia", r.FontFamily));
        Assert.Equal(RtfDocumentFormatter.Write(ed.Document!), ed.ToRtf());
    });
}
