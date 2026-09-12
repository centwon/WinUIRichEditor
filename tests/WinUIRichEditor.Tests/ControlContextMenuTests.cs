using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>What the right-click menus CONTAIN — the decision of which menu opens is pinned in
/// <see cref="ExtractedGateTests"/>. The builders are split from the showing (ShowAt needs a live
/// RightTappedRoutedEventArgs position), so the flyout can be inspected without opening it.
/// <para>Three defects found by comparing against the AvaloniaRichEditor peer (2026-09-12):</para>
/// <list type="bullet">
/// <item>The full 글자 모양 group used plain items, so it could not show that bold was already on —
/// the slim menu (and upstream's full one) could.</item>
/// <item>The "표" submenu header alone missed the compact menu font size.</item>
/// <item>A viewer right-clicking an inline table's border got an EMPTY flyout: the object wins the
/// right-click even when read-only, and every item was gated on !IsReadOnly.</item>
/// </list></summary>
[Collection(UiTests.Collection)]
public class ControlContextMenuTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static string Loc(string k) => RichEditorLocalization.GetString(k);

    private static void Caret(RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            typeof(RichEditor).GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
    }

    private static MenuFlyout TextMenu(RichEditor ed)
    {
        var menu = new MenuFlyout();
        ed.BuildTextMenu(menu, hasSel: false, linkUri: null);
        return menu;
    }

    private static IEnumerable<MenuFlyoutItemBase> Walk(IEnumerable<MenuFlyoutItemBase> items)
    {
        foreach (var i in items)
        {
            yield return i;
            if (i is MenuFlyoutSubItem sub)
                foreach (var c in Walk(sub.Items)) yield return c;
        }
    }

    private static string TextOf(MenuFlyoutItemBase i) => i switch
    {
        MenuFlyoutItem mi => mi.Text,          // Toggle/Radio items derive from MenuFlyoutItem
        MenuFlyoutSubItem sub => sub.Text,
        _ => "",
    };

    // The four toggles reflect the caret — checked where the caret's run is bold, unchecked where it is
    // not (the contrast is what makes this more than "IsChecked happens to be true") — and keep the
    // icons they carried as plain items.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFullCharacterGroup_ChecksTheCaretsFormat_AndKeepsItsIcons(bool bold) => UiThread.Run(() =>
    {
        var run = new Run { Text = "abcd" };
        if (bold) run.FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 };
        var p = new Paragraph { Inlines = { run } };
        var doc = new FlowDocument();
        doc.Blocks.Add(p);
        var ed = new RichEditor { Document = doc, ShowFormattingMenu = true };
        Caret(ed, p, 2);

        var group = TextMenu(ed).Items.OfType<MenuFlyoutSubItem>().Single(s => s.Text == Loc("CharacterFormat"));
        var toggles = group.Items.OfType<ToggleMenuFlyoutItem>().ToList();

        Assert.Equal(new[] { Loc("Bold"), Loc("Italic"), Loc("Underline"), Loc("Strikethrough") },
                     toggles.Select(t => t.Text).ToArray());
        Assert.Equal(bold, toggles[0].IsChecked);
        Assert.False(toggles[1].IsChecked);
        Assert.All(toggles, t => Assert.NotNull(t.Icon));
    });

    // Every item of the text menu — the "표" submenu included, which is where the one miss was — sits at
    // the same compact font size. Asserted against the menu's own first item rather than a copied
    // constant, so it catches the next item that forgets FontSize, wherever it is added.
    [Fact]
    public void EveryItem_InTheCellTextMenu_UsesTheSameFontSize() => UiThread.Run(() =>
    {
        var tb = new TableBlock(2, 2);
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
        doc.Blocks.Add(tb);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below" } } });
        var ed = new RichEditor { Document = doc, ShowFormattingMenu = true };
        Caret(ed, tb.Cells[0][0].Blocks.OfType<Paragraph>().First(), 0);

        var items = Walk(TextMenu(ed).Items).Where(i => i is not MenuFlyoutSeparator).ToList();
        Assert.Contains(items, i => TextOf(i) == Loc("TableOps")); // the caret really is in a cell
        double size = items[0].FontSize;
        Assert.All(items, i => Assert.True(i.FontSize == size, $"'{TextOf(i)}' is {i.FontSize}px, the menu is {size}px"));
    });

    // A viewer's inline-table menu is Copy and nothing else; an editor's adds the structure verbs.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheInlineTableMenu_IsNeverEmpty_AndAViewerGetsOnlyCopy(bool readOnly) => UiThread.Run(() =>
    {
        var it = new InlineTable { Table = new TableBlock(1, 1) };
        var host = new Paragraph { Inlines = { new Run { Text = "a" }, it } };
        var doc = new FlowDocument();
        doc.Blocks.Add(host);
        var ed = new RichEditor { Document = doc, IsReadOnly = readOnly };

        var labels = ed.BuildInlineTableMenu(host, it).Items
            .Where(i => i is not MenuFlyoutSeparator).Select(TextOf).ToList();

        Assert.Equal(Loc("Copy"), labels.First());
        if (readOnly) Assert.Single(labels);
        else Assert.Contains(Loc("DeleteTable"), labels);
    });
}
