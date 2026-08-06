using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The context menu and the IME bridge — the last two control-layer areas with no coverage.
/// <para>Neither can be driven end to end: the menu's read-only and hyperlink gates live inside
/// <c>OnCanvasRightTapped</c>, which needs a <c>RightTappedRoutedEventArgs</c>, and the IME's real work
/// happens in <c>TextUpdating</c>, which needs a <c>CoreTextTextUpdatingEventArgs</c>. Both are WinRT
/// event args with no public constructor. What IS reachable is the builder the menu path ends in and the
/// state the IME bridge exposes to the system, and that is where the failures that reach users live —
/// a menu item with no label, or an IME buffer that disagrees with the document about offsets.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlMenuAndImeTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    // Build the document first, then assign it — the Document setter is what wires Parent.
    private static RichEditor NewEditor(params Inline[] inlines)
    {
        var doc = new FlowDocument();
        var p = new Paragraph();
        foreach (var i in inlines) p.Inlines.Add(i);
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "hello world" });
        doc.Blocks.Add(p);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "second paragraph" } } });

        var ed = new RichEditor { Document = doc };
        SetCaret(ed, First(ed), 0);
        return ed;
    }

    private static Paragraph First(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().First();
    private static Paragraph Second(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().ElementAt(1);

    private static void SetCaret(RichEditor ed, Paragraph p, int offset)
    {
        var tp = new TextPointer(p, offset);
        T.GetField("_caret", NP)!.SetValue(ed, tp);
        T.GetField("_selStart", NP)!.SetValue(ed, tp);
        T.GetField("_selEnd", NP)!.SetValue(ed, tp);
    }

    private static void SetSelection(RichEditor ed, Paragraph p, int from, int to)
    {
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(p, from));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(p, to));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(p, to));
    }

    // ---- the context menu ---------------------------------------------------------------------------

    private static MenuFlyout BuildTextMenu(RichEditor ed, bool hasSel, string? linkUri)
    {
        var menu = new MenuFlyout();
        T.GetMethod("BuildTextMenu", NP)!.Invoke(ed, new object?[] { menu, hasSel, linkUri });
        return menu;
    }

    // Flattened labels, submenus included.
    private static IEnumerable<(string Label, bool Enabled)> Entries(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var it in items)
        {
            switch (it)
            {
                case MenuFlyoutSubItem sub:
                    yield return (sub.Text, sub.IsEnabled);
                    foreach (var x in Entries(sub.Items)) yield return x;
                    break;
                case MenuFlyoutSeparator:
                    break;
                case MenuFlyoutItem mi:
                    yield return (mi.Text, mi.IsEnabled);
                    break;
                default:
                    yield return (it.GetType().Name, it.IsEnabled);
                    break;
            }
        }
    }

    // The clipboard verbs follow the selection. Without one there is nothing to cut, copy or delete, and
    // an enabled item that does nothing is worse than an absent one.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TextMenu_ClipboardVerbs_FollowTheSelection(bool hasSel)
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            var entries = Entries(BuildTextMenu(ed, hasSel, null).Items).ToList();

            foreach (var key in new[] { "Cut", "Copy", "Delete" })
            {
                string label = RichEditorLocalization.GetString(key);
                var found = entries.FirstOrDefault(e => e.Label == label);
                Assert.True(found.Label != null, $"the menu has no '{label}' item");
                Assert.Equal(hasSel, found.Enabled);
            }

            // Paste does not depend on the selection — there may be something on the clipboard either way.
            Assert.Contains(entries, e => e.Label == RichEditorLocalization.GetString("Paste"));
        });
    }

    // ShowFormattingMenu defaults to FALSE — the slim menu is the default, and a host with its own
    // toolbar opts into the rich groups. Asserting both directions is the point: comparing the default
    // against an explicit `false` proves nothing, which is exactly the mistake that produced the first
    // draft of this test.
    [Fact]
    public void TextMenu_FormattingGroups_AppearOnlyWhenAskedFor()
    {
        UiThread.Run(() =>
        {
            var slim = NewEditor();
            Assert.False(slim.ShowFormattingMenu); // the default this test depends on
            int slimCount = Entries(BuildTextMenu(slim, false, null).Items).Count();

            var rich = NewEditor();
            rich.ShowFormattingMenu = true;
            int richCount = Entries(BuildTextMenu(rich, false, null).Items).Count();

            Assert.True(richCount > slimCount,
                $"ShowFormattingMenu=true did not add anything ({slimCount} -> {richCount})");
        });
    }

    // Right-clicking a hyperlink offers open/edit/remove instead of "insert link"; the two sets are
    // mutually exclusive, so a caret on a link must never be offered the insert verb.
    [Fact]
    public void TextMenu_OnALink_OffersLinkVerbs_NotInsertLink()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            var onLink = Entries(BuildTextMenu(ed, false, "https://example.com/x").Items).Select(e => e.Label).ToList();
            var offLink = Entries(BuildTextMenu(ed, false, null).Items).Select(e => e.Label).ToList();

            Assert.Contains(RichEditorLocalization.GetString("OpenLink"), onLink);
            Assert.Contains(RichEditorLocalization.GetString("RemoveLink"), onLink);
            Assert.DoesNotContain(RichEditorLocalization.GetString("InsertLink"), onLink);

            Assert.Contains(RichEditorLocalization.GetString("InsertLink"), offLink);
            Assert.DoesNotContain(RichEditorLocalization.GetString("RemoveLink"), offLink);
        });
    }

    // A menu item with a blank label is invisible in the UI, and a missing localization key produces
    // exactly that. Nothing else in the suite would notice.
    [Fact]
    public void EveryTextMenuItem_HasAVisibleLabel()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            ed.ShowFormattingMenu = true; // the widest menu, so the check covers the most items

            var blank = Entries(BuildTextMenu(ed, true, "https://example.com/x").Items)
                .Where(e => string.IsNullOrWhiteSpace(e.Label))
                .ToList();

            Assert.Empty(blank);
        });
    }

    [Fact]
    public void LinkMenu_OffersOpenEditRemove()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            var menu = new MenuFlyout();
            T.GetMethod("BuildLinkMenu", NP)!.Invoke(ed, new object?[] { menu, "https://example.com/x" });

            var labels = Entries(menu.Items).Select(e => e.Label).ToList();
            Assert.Contains(RichEditorLocalization.GetString("OpenLink"), labels);
            Assert.Contains(RichEditorLocalization.GetString("EditLink"), labels);
            Assert.Contains(RichEditorLocalization.GetString("RemoveLink"), labels);
            Assert.DoesNotContain(labels, string.IsNullOrWhiteSpace);
        });
    }

    // ---- the IME bridge -----------------------------------------------------------------------------

    private static string CaretParagraphText(RichEditor ed)
        => (string)T.GetMethod("CaretParagraphText", NP)!.Invoke(ed, null)!;

    private static void SyncIme(RichEditor ed) => T.GetMethod("SyncIme", NP)!.Invoke(ed, null);

    private static object? Field(RichEditor ed, string name) => T.GetField(name, NP)!.GetValue(ed);

    // The IME is handed the caret paragraph as a flat string and answers in OFFSETS into it. The offset
    // model's rule is that an inline object counts as exactly one character (U+FFFC), so the text the IME
    // sees has to contain that placeholder — if an image contributed zero characters, or its own width,
    // every IME offset after it would land somewhere else in the document.
    [Fact]
    public void TextHandedToTheIme_CountsAnInlineObjectAsOneCharacter()
    {
        UiThread.Run(() =>
        {
            var image = new InlineImage { Width = 10, Height = 10 };
            var ed = NewEditor(new Run { Text = "ab" }, image, new Run { Text = "cd" });

            string text = CaretParagraphText(ed);

            Assert.Equal(5, text.Length);          // a b <obj> c d
            Assert.Equal('￼', text[2]);
            Assert.Equal("ab￼cd", text);
        });
    }

    // The IME's buffer is the caret paragraph. When the caret moves to a different one the buffer has to
    // follow, or the IME composes against text that is no longer where the caret is.
    [Fact]
    public void ImeBuffer_FollowsTheCaretToAnotherParagraph()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();

            SetCaret(ed, First(ed), 0);
            SyncIme(ed);
            Assert.Equal("hello world", Field(ed, "_imeBufferText"));
            Assert.Equal(11, Field(ed, "_imeBufferLen"));
            Assert.Same(First(ed), Field(ed, "_imeBufferPara"));

            SetCaret(ed, Second(ed), 0);
            SyncIme(ed);
            Assert.Equal("second paragraph", Field(ed, "_imeBufferText"));
            Assert.Equal(16, Field(ed, "_imeBufferLen"));
            Assert.Same(Second(ed), Field(ed, "_imeBufferPara"));
        });
    }

    // A same-LENGTH edit — find/replace, a formatting-driven text swap — is what a length-only check
    // misses, leaving the IME composing against text the document no longer has.
    //
    // What this must NOT assert is `_imeBufferText`: that field is assigned on every SyncIme regardless
    // of which branch ran, so it tracks the document either way and proves nothing. (Measured — the first
    // version of this test passed against a deliberately length-only check.) The branch that reports the
    // change to the IME also resets `_imeRangeDelta`, and the branch that skips it does not, so that
    // field is the one signal that actually distinguishes them from out here.
    [Fact]
    public void ImeBuffer_ReportsASameLengthEdit_ToTheIme()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            SyncIme(ed);
            Assert.Equal("hello world", Field(ed, "_imeBufferText"));

            T.GetField("_imeRangeDelta", NP)!.SetValue(ed, 5); // a value only the reporting branch clears
            ((Run)First(ed).Inlines[0]).Text = "HELLO WORLD";  // same length, different text
            SyncIme(ed);

            Assert.Equal(0, Field(ed, "_imeRangeDelta"));
            Assert.Equal("HELLO WORLD", Field(ed, "_imeBufferText"));
        });
    }

    // Losing focus mid-composition must end it. A composition left "active" makes the editor ignore every
    // subsequent CharacterReceived — the gate that routes plain typing is `_composing`, not `_imeEnabled` —
    // so typing would silently stop working after an alt-tab.
    [Fact]
    public void LosingFocus_EndsAnActiveComposition()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            T.GetField("_composing", NP)!.SetValue(ed, true);
            T.GetField("_composRange", NP)!.SetValue(ed, (ValueTuple<int, int>?)(0, 3));

            T.GetMethod("ImeNotifyFocusLeave", NP)!.Invoke(ed, null);

            Assert.False((bool)Field(ed, "_composing")!);
            Assert.Null(Field(ed, "_composRange"));
        });
    }

    // What the IME is told about the selection has to match what the document has, or a composition
    // replaces the wrong span.
    [Fact]
    public void SelectionHandedToTheIme_MatchesTheDocument()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor();
            SetSelection(ed, First(ed), 2, 7);

            // CoreTextRange is a public WinRT struct with FIELDS, not properties — reflecting for
            // properties returns null and the cast is both simpler and checked by the compiler.
            var range = (Windows.UI.Text.Core.CoreTextRange)
                T.GetMethod("CaretSelectionRange", NP)!.Invoke(ed, null)!;

            Assert.Equal(2, range.StartCaretPosition);
            Assert.Equal(7, range.EndCaretPosition);
        });
    }
}
