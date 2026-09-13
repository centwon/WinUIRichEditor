using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
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

    // ---- the whole right-click, from a point ------------------------------------------------------

    // Hit-testing needs a real layout: hosted, one editor for the class (see ControlCaretTests).
    private static readonly Lazy<RichEditor> Hosted = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // The middle of the caret box at p:offset, in document space — which is view space here (continuous
    // page, no zoom, not scrolled).
    private static Windows.Foundation.Point PointAt(RichEditor ed, Paragraph p, int offset)
    {
        var box = typeof(RichEditor).GetMethod("CaretToDocPoint", NP)!.Invoke(ed, new object[] { new TextPointer(p, offset) })!;
        var ty = box.GetType();
        double F(string n) => (double)ty.GetField(n)!.GetValue(box)!;
        return new(F("Item1"), F("Item2") + F("Item3") / 2);
    }

    private static void Invoke(MenuFlyoutItem item)
        => ((Microsoft.UI.Xaml.Automation.Provider.IInvokeProvider)new Microsoft.UI.Xaml.Automation.Peers.MenuFlyoutItemAutomationPeer(item)).Invoke();

    // A viewer right-clicking a table gets Copy and Select All, and Copy — enabled with nothing selected —
    // takes the table, as right-clicking an image takes the image. Outside a table the same menu's Copy is
    // greyed out with nothing selected (the contrast that makes the table case more than "always enabled").
    [Fact]
    public void AViewerRightClickingATable_GetsCopyAndSelectAll_AndCopyTakesTheTable()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(2, 2);
            for (int r = 0; r < 2; r++)
                for (int c = 0; c < 2; c++)
                {
                    tb.Cells[r][c].Blocks.Clear();
                    tb.Cells[r][c].Blocks.Add(new Paragraph { Inlines = { new Run { Text = $"cell {r}{c}" } } });
                }
            var above = new Paragraph { Inlines = { new Run { Text = "above the table" } } };
            var doc = new FlowDocument();
            doc.Blocks.Add(above);
            doc.Blocks.Add(tb);
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below the table" } } });
            ed.Document = doc;
            ed.IsReadOnly = true;
            try
            {
                typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);

                object? Selected() => typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed);

                var items = ed.BuildContextMenuAt(PointAt(ed, (Paragraph)tb.Cells[1][0].Blocks[0], 2))
                    .Items.OfType<MenuFlyoutItem>().ToList();
                Assert.Equal(new[] { Loc("Copy"), Loc("SelectAll") }, items.Select(i => i.Text).ToArray());
                Assert.True(items[0].IsEnabled, "Copy is greyed out on a table with nothing selected");
                Assert.Same(tb, Selected()); // shown selected: the viewer sees what Copy takes
                // The hosted editor is shared by the class: an earlier test's copy could satisfy the check below.
                typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.SetValue(ed, null);
                Invoke(items[0]);
                var clip = (FlowDocument?)typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.GetValue(ed);
                Assert.IsType<TableBlock>(Assert.Single(clip!.Blocks));

                var outside = ed.BuildContextMenuAt(PointAt(ed, above, 2)).Items.OfType<MenuFlyoutItem>().ToList();
                Assert.False(outside[0].IsEnabled);
                Assert.Null(Selected()); // the table's selection does not linger past a right-click elsewhere
                Invoke(outside[1]);
                Assert.True((bool)typeof(RichEditor).GetProperty("HasSelection", NP | BindingFlags.Public)!.GetValue(ed)!);
            }
            finally { ed.IsReadOnly = false; }
        });
    }

    // A viewer's table border shows the move cursor, a click there selects the table — as in the editor —
    // and a right-click there takes the table. The first two were edit-only; a live check asked for a sign
    // that the TABLE is the target. The border rect is seeded into the registry the renderer fills (a test
    // host does not paint), since what changed is the gate in front of it, not the geometry — and seeded far
    // below the content, so the right-click can only find the table through its border (the caret lands in
    // the last paragraph there). A point inside the rect is the contrast: I-beam, no selection.
    [Fact]
    public void AViewersTableBorder_ShowsTheMoveCursor_AndAClickSelectsTheTable()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(2, 2);
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
            doc.Blocks.Add(tb);
            ed.Document = doc;
            ed.IsReadOnly = true;
            try
            {
                typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
                var rects = (IDictionary<TableBlock, Windows.Foundation.Rect>)typeof(RichEditor).GetField("_tableRects", NP)!.GetValue(ed)!;
                rects[tb] = new Windows.Foundation.Rect(20, 2000, 200, 80);
                var border = new Windows.Foundation.Point(20, 2040);
                var inside = new Windows.Foundation.Point(120, 2040);

                object Cursor(Windows.Foundation.Point pt)
                {
                    typeof(RichEditor).GetMethod("UpdateHoverCursor", NP)!.Invoke(ed, new object[] { pt });
                    return typeof(RichEditor).GetField("_cursorShape", NP)!.GetValue(ed)!;
                }
                bool Select(Windows.Foundation.Point pt) => (bool)typeof(RichEditor).GetMethod("TrySelectTableBlock", NP)!.Invoke(ed, new object[] { pt })!;

                Assert.Equal(Microsoft.UI.Input.InputSystemCursorShape.SizeAll, Cursor(border));
                Assert.NotEqual(Microsoft.UI.Input.InputSystemCursorShape.SizeAll, Cursor(inside));
                Assert.False(Select(inside));
                Assert.True(Select(border));
                Assert.Same(tb, typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed));

                typeof(RichEditor).GetField("_selectedBlock", NP)!.SetValue(ed, null);
                var copy = ed.BuildContextMenuAt(border).Items.OfType<MenuFlyoutItem>().First();
                Assert.True(copy.IsEnabled, "a right-click on the border did not find the table");
                Assert.Same(tb, typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed));
            }
            finally { ed.IsReadOnly = false; }
        });
    }

    // In the editor a right-click on a table's border is a right-click on the TABLE: it is selected as an
    // object and its own menu opens — Copy and Cut take it, then 글자처럼 취급 and 표 삭제. It opened the text
    // menu with Copy greyed out, although a click on the same band selects the table (live check, 2026-09-13).
    // The rect is seeded far below the content (see the viewer test above), so only the border finds it.
    [Fact]
    public void InTheEditor_RightClickingATablesBorder_OpensTheTablesMenu_AndCopyTakesIt()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(2, 2);
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
            doc.Blocks.Add(tb);
            ed.Document = doc;
            typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            var rects = (IDictionary<TableBlock, Windows.Foundation.Rect>)typeof(RichEditor).GetField("_tableRects", NP)!.GetValue(ed)!;
            rects[tb] = new Windows.Foundation.Rect(20, 2000, 200, 80);

            var items = ed.BuildContextMenuAt(new Windows.Foundation.Point(20, 2040)).Items.OfType<MenuFlyoutItem>().ToList();
            Assert.Equal(new[] { Loc("Copy"), Loc("Cut"), Loc("InlineWithText"), Loc("DeleteTable") },
                         items.Select(i => i.Text).ToArray());
            Assert.Same(tb, typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed));

            typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.SetValue(ed, null);
            Invoke(items[0]);
            var clip = (FlowDocument?)typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.GetValue(ed);
            Assert.IsType<TableBlock>(Assert.Single(clip!.Blocks));
        });
    }

    // An inline table copies AS an inline table: the clipboard holds one paragraph with it, which paste puts at
    // the caret — inside a line of text — not a block table splitting the paragraph (user decision, 2026-09-13).
    // The paste half is InsertDocumentAtCaret, the in-app paste path (the OS clipboard is not needed for it).
    [Fact]
    public void AnInlineTable_CopiesAsAnInlineTable_AndPastesInlineAtTheCaret() => UiThread.Run(() =>
    {
        var it = new InlineTable { Table = new TableBlock(2, 2) };
        var host = new Paragraph { Inlines = { new Run { Text = "a" }, it } };
        var target = new Paragraph { Inlines = { new Run { Text = "xy" } } };
        var doc = new FlowDocument();
        doc.Blocks.Add(host);
        doc.Blocks.Add(target);
        var ed = new RichEditor { Document = doc };
        typeof(RichEditor).GetField("_selectedInlineTable", NP)!.SetValue(ed, ((Paragraph, InlineTable)?)(host, it));
        typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.SetValue(ed, null);

        _ = ed.CopyAsync();

        var clip = (FlowDocument)typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.GetValue(ed)!;
        var line = Assert.IsType<Paragraph>(Assert.Single(clip.Blocks)); // a line, not a block table
        var copied = Assert.IsType<InlineTable>(Assert.Single(line.Inlines));
        Assert.Equal((2, 2), (copied.Table.Rows, copied.Table.Columns));

        Caret(ed, target, 1); // between "x" and "y"
        typeof(RichEditor).GetMethod("InsertDocumentAtCaret", NP)!.Invoke(ed, new object[] { clip.Clone() });

        Assert.Empty(ed.Document!.Blocks.OfType<TableBlock>());  // no block table appeared
        Assert.Collection(target.Inlines,
            i => Assert.Equal("x", Assert.IsType<Run>(i).Text),
            i => Assert.IsType<InlineTable>(i),
            i => Assert.Equal("y", Assert.IsType<Run>(i).Text));
    });

    // A table nested in a cell has a border too (2026-09-13 — it had none): a right-click there selects it as
    // an object and opens the table's menu — without 글자처럼 취급, which converts top-level tables only — and
    // Copy takes it. Where its band overlaps its outer table's (a cell's padding apart), the INNER table wins.
    // Now that it can be selected, an arrow steps out of it within its cell, and deleting it leaves the caret
    // in that cell — not inside the removed table, where it was.
    [Fact]
    public void ATableInACell_HasABorder_TheInnerWins_AndLeavingOrDeletingItKeepsTheCaretInItsCell()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var inner = new TableBlock(2, 2);
            var outer = new TableBlock(1, 2);
            var after = new Paragraph { Inlines = { new Run { Text = "after" } } };
            outer.Cells[0][0].Blocks.Clear();
            outer.Cells[0][0].Blocks.Add(inner);
            outer.Cells[0][0].Blocks.Add(after);
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
            doc.Blocks.Add(outer);
            ed.Document = doc;
            typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            var top = (IDictionary<TableBlock, Windows.Foundation.Rect>)typeof(RichEditor).GetField("_tableRects", NP)!.GetValue(ed)!;
            top[outer] = new Windows.Foundation.Rect(20, 3000, 300, 100);
            var nested = (List<(TableBlock tb, Windows.Foundation.Rect rect)>)typeof(RichEditor).GetField("_cellTableRects", NP)!.GetValue(ed)!;
            nested.Add((inner, new Windows.Foundation.Rect(25, 3005, 100, 50)));
            var both = new Windows.Foundation.Point(23, 3020); // 3 from the outer's left edge, 2 from the inner's

            object? Selected() => typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed);
            TextPointer CaretNow() => (TextPointer)typeof(RichEditor).GetField("_caret", NP)!.GetValue(ed)!;

            var items = ed.BuildContextMenuAt(both).Items.OfType<MenuFlyoutItem>().ToList();
            Assert.Equal(new[] { Loc("Copy"), Loc("Cut"), Loc("DeleteTable") }, items.Select(i => i.Text).ToArray());
            Assert.Same(inner, Selected());
            typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.SetValue(ed, null);
            Invoke(items[0]);
            var copied = Assert.IsType<TableBlock>(Assert.Single(((FlowDocument?)typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.GetValue(ed))!.Blocks));
            Assert.Equal((2, 2), (copied.Rows, copied.Columns)); // the inner table — the outer is 1×2

            typeof(RichEditor).GetMethod("ExitBlockSelection", NP)!.Invoke(ed, new object[] { inner, true });
            Assert.Same(after, CaretNow().Paragraph); // the next paragraph IN the cell

            Caret(ed, inner.Cells[1][1].Blocks.OfType<Paragraph>().First(), 0); // the caret inside the table about to go
            typeof(RichEditor).GetField("_selectedBlock", NP)!.SetValue(ed, inner);
            Invoke(items[2]);
            Assert.DoesNotContain(inner, outer.Cells[0][0].Blocks);
            Assert.Same(outer.Cells[0][0], CaretNow().Paragraph!.Parent);
        });
    }
}
