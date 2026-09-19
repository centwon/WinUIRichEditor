using System;
using System.Reflection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.System;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The find UI's two live-check findings (2026-09-19): the toolbar's Find button showed on an editor that
/// nothing answers Ctrl+F for (a bare editor + toolbar) and did nothing, and F3 from the find bar's query box — where
/// the focus stays after Enter — never reached the editor. The boxes' keys are pressed through FindBoxKey /
/// ReplaceBoxKey (KeyRoutedEventArgs has no public constructor).</summary>
[Collection(UiTests.Collection)]
public class FindUiTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T? Field<T>(object o, string name) where T : class => o.GetType().GetField(name, NP)!.GetValue(o) as T;

    private static FlowDocument Doc() => new()
    {
        Blocks = { new Paragraph { Inlines = { new Run { Text = "apple banana apple cherry apple" } } } },
    };

    private static int SelStart(RichEditor ed) => ((TextPointer)typeof(RichEditor).GetField("_selStart", NP)!.GetValue(ed)!).Offset;

    // Like Print: the button exists only while something answers it.
    [Fact]
    public void TheToolbarsFindButton_ShowsOnlyWhileSomethingHandlesFind()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
            Visibility Find() => Field<Button>(tb, "_findBtn")!.Visibility;
            Assert.Equal(Visibility.Collapsed, Find()); // a bare editor: Ctrl+F opens nothing

            EventHandler<bool> h = (_, _) => { };
            ed.FindRequested += h;
            Assert.Equal(Visibility.Visible, Find());
            ed.FindRequested -= h;
            Assert.Equal(Visibility.Collapsed, Find());
        });
    }

    // The view answers it with its bar, so its toolbar has the button.
    [Fact]
    public void AViewsToolbar_HasTheFindButton()
    {
        UiThread.Run(() =>
        {
            var view = new RichEditorView();
            Assert.Equal(Visibility.Visible, Field<Button>(view.Toolbar, "_findBtn")!.Visibility);
        });
    }

    [Fact]
    public void TheQueryBox_Enter_F3_ShiftF3_Escape()
    {
        UiThread.Run(() =>
        {
            var view = new RichEditorView { Document = Doc() };
            view.Editor.RaiseFindRequested(false); // what Ctrl+F does
            var host = Field<Border>(view, "_findBarHost")!;
            Assert.Equal(Visibility.Visible, host.Visibility);

            Field<TextBox>(view, "_findBox")!.Text = "apple";
            Assert.True(view.FindBoxKey(VirtualKey.Enter, shift: false));
            int first = SelStart(view.Editor);

            Assert.True(view.FindBoxKey(VirtualKey.F3, shift: false));
            int second = SelStart(view.Editor);
            Assert.True(second > first, $"F3 in the query box did not move on ({first} -> {second})");

            Assert.True(view.FindBoxKey(VirtualKey.F3, shift: true));
            Assert.Equal(first, SelStart(view.Editor));

            Assert.True(view.FindBoxKey(VirtualKey.Escape, shift: false));
            Assert.Equal(Visibility.Collapsed, host.Visibility);
        });
    }
}
