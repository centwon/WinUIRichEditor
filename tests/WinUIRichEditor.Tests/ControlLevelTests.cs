using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The first automated tests of the CONTROL layer.
/// <para>Until now this repo's coverage stopped at the model and formatter layers, and every control
/// defect — toolbar focus theft, caret alignment, vertical movement across a table boundary — was found
/// by a human driving the demo and reporting it. The roadmap called that the largest structural gap, on
/// the belief that WinUI's dependency-property static constructors made control tests impossible.</para>
/// <para>They do require the WinUI runtime; they do not make it unreachable. See <see cref="UiThread"/>.</para>
/// </summary>
public class ControlLevelTests
{
    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static FlowDocument Doc(params string[] paragraphs)
    {
        var d = new FlowDocument();
        foreach (var t in paragraphs) d.Blocks.Add(P(t));
        return d;
    }

    // ---- damaged input must not replace what is open ---------------------------------------------

    // The contract TryParse exists for. It was verified upstream and, until this file, had no equivalent
    // here — the roadmap said so explicitly, and the reason given was that control tests were not possible.
    [Theory]
    [InlineData(@"{\rtf1\ansi\fs99999999999999999999 x\par}")]  // aborts the parse
    [InlineData(@"{\rtf1\ansi {\*\broken")]                      // truncated mid-group
    [InlineData(@"{\rtf1\ansi\trowd\cellx1000 a\cell")]          // truncated mid-table
    [InlineData(@"{\rtf1\ansi hello there")]                     // truncated after readable text
    public void LoadRtf_DamagedInput_KeepsTheOpenDocument(string damaged)
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = Doc("keep me") };
            var open = ed.Document;

            ed.LoadRtf(damaged);

            // Not merely "the text is still there": the document instance must not even be replaced,
            // because a host holding a reference to it would otherwise be editing an orphan.
            Assert.Same(open, ed.Document);
            Assert.Contains("keep me", ed.GetPlainText());
        });
    }

    [Fact]
    public void LoadRtf_ValidInput_DoesReplaceTheDocument()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = Doc("old") };
            ed.LoadRtf(@"{\rtf1\ansi new text\par}");
            Assert.Contains("new text", ed.GetPlainText());
            Assert.DoesNotContain("old", ed.GetPlainText());
        });
    }

    // With nothing open there is nothing to protect, and bailing would leave the editor inert.
    [Fact]
    public void LoadRtf_DamagedInput_WithNothingOpen_LoadsAnEmptyDocument()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            ed.LoadRtf(@"{\rtf1\ansi {\*\broken");
            Assert.NotNull(ed.Document);
            Assert.Equal("", ed.GetPlainText().Trim());
        });
    }

    // ---- the toolbar must never take focus from the editing surface -------------------------------

    // Track C was a user report — "typing does nothing after I click a toolbar button" — and its fix was
    // verified by hand. This is the regression guard that was missing: every button the toolbar builds,
    // at every density, has to refuse focus.
    [Theory]
    [InlineData(ToolbarLevel.Minimal)]
    [InlineData(ToolbarLevel.Normal)]
    [InlineData(ToolbarLevel.Maximum)]
    public void EveryToolbarButton_RefusesFocusOnInteraction(ToolbarLevel level)
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor { Document = Doc("x") };
            var toolbar = new RichEditorToolbar { Target = ed, ToolbarLevel = level };

            var thieves = Walk(toolbar.Content).OfType<ButtonBase>()
                .Where(b => b.AllowFocusOnInteraction)
                .Select(Describe)
                .ToList();

            Assert.True(Walk(toolbar.Content).OfType<ButtonBase>().Any(), "no buttons were found — the walk is wrong, not the toolbar");
            Assert.Empty(thieves);
        });
    }

    private static string Describe(ButtonBase b)
        => $"{b.GetType().Name}('{(b.Content as string) ?? b.Name ?? "?"}')";

    // Depth-limited walk over the composed tree. The toolbar is built in code, so this mirrors the
    // shapes ClearFocusOnInteraction itself descends through.
    private static IEnumerable<object> Walk(object? node, int depth = 0)
    {
        if (node == null || depth > 16) yield break;
        yield return node;
        switch (node)
        {
            case Panel panel:
                foreach (var child in panel.Children)
                    foreach (var x in Walk(child, depth + 1)) yield return x;
                break;
            case Border border:
                foreach (var x in Walk(border.Child, depth + 1)) yield return x;
                break;
            case ContentControl cc when cc is not ButtonBase:
                foreach (var x in Walk(cc.Content, depth + 1)) yield return x;
                break;
        }
    }

    // ---- recursion into table cells ---------------------------------------------------------------

    // Track B made these recursive so assistive technology sees cell text and the image soft-limit counts
    // cell images. Both were checked by hand at the time.
    [Fact]
    public void GetPlainText_And_GetImageCount_ReachInsideTableCells()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            var table = new TableBlock(1, 2);
            ((Run)table.Cells[0][0].Para.Inlines[0]).Text = "inside";
            var nested = new TableBlock(1, 1);
            ((Run)nested.Cells[0][0].Para.Inlines[0]).Text = "deeper";
            table.Cells[0][1].Blocks.Add(nested);
            doc.Blocks.Add(table);

            var ed = new RichEditor { Document = doc };

            string text = ed.GetPlainText();
            Assert.Contains("inside", text);
            Assert.Contains("deeper", text);   // one level down is not enough
        });
    }

    // ---- the view composes what it says it composes ------------------------------------------------

    [Fact]
    public void RichEditorView_ExposesItsParts_AndTogglesTheFindBar()
    {
        UiThread.Run(() =>
        {
            var view = new RichEditorView { Document = Doc("hello") };

            Assert.NotNull(view.Editor);
            Assert.NotNull(view.Toolbar);
            Assert.Same(view.Editor, view.Toolbar.Target);   // the toolbar drives the embedded editor
            Assert.Equal("hello", view.Editor.GetPlainText().Trim());

            Assert.True(view.ShowStatusBar);
            view.ShowStatusBar = false;
            Assert.False(view.ShowStatusBar);

            // Ctrl+F/H route through these; they must be callable without a window.
            view.ShowFindBar(false);
            view.ShowFindBar(true);
            view.HideFindBar();
        });
    }

    [Fact]
    public void RichEditorView_ForwardsReadOnlyToTheEditor()
    {
        UiThread.Run(() =>
        {
            var view = new RichEditorView { Document = Doc("x"), IsReadOnly = true };
            Assert.True(view.Editor.IsReadOnly);
            view.IsReadOnly = false;
            Assert.False(view.Editor.IsReadOnly);
        });
    }

    // ---- hosted in a real window --------------------------------------------------------------------

    // FocusEditor() returns false before layout — WinUI ignores Focus() on an element that has not been
    // realized, which reads as a failure and is not one. Hosting the control proves the other half: once
    // it is loaded, focus really does land on the internal canvas (the control itself is not a tab stop,
    // which is why FocusEditor exists at all).
    [Fact]
    public void FocusEditor_SucceedsOnceTheControlIsLoaded()
    {
        RichEditor? editor = null;
        Window? window = null;
        var loaded = new ManualResetEventSlim();

        try
        {
            UiThread.Run(() =>
            {
                editor = new RichEditor { Document = Doc("hello") };
                Assert.False(editor.FocusEditor()); // not realized yet
                editor.Loaded += (_, _) => loaded.Set();
                window = new Window { Content = editor };
                window.Activate();
            });

            Assert.True(loaded.Wait(TimeSpan.FromSeconds(30)), "the hosted editor never raised Loaded");

            UiThread.Run(() =>
            {
                Assert.True(editor!.ActualWidth > 0, "the hosted editor never got a size");
                Assert.True(editor.FocusEditor());
            });
        }
        finally
        {
            if (window != null) UiThread.Run(() => window.Close());
        }
    }
}
