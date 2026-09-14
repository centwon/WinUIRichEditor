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

    // ---- the table menu — the same items in the same order as AvaloniaRichEditor's (user decision, 2026-09-14) --

    private static string[] Labels(MenuFlyout m) => m.Items.Select(i => i is MenuFlyoutSeparator ? "—" : TextOf(i)).ToArray();

    // Written out identically in AvaloniaRichEditor's ContextMenuConvergenceTests; "—" is a separator.
    private static string[] TableMenuLabels(bool inlineToggle)
    {
        var l = new List<string> { Loc("Cut"), Loc("Copy"), Loc("Paste"), Loc("Delete"), "—",
            Loc("SelectCell"), "—",
            Loc("InsertRowAbove"), Loc("InsertRowBelow"), Loc("DeleteRow"), "—",
            Loc("InsertColumnLeft"), Loc("InsertColumnRight"), Loc("DeleteColumn"), "—",
            Loc("MergeCells"), Loc("UnmergeCells"), "—",
            Loc("CellVerticalAlign"), Loc("CellBackground"), Loc("Margin") };
        if (inlineToggle) l.Add(Loc("InlineWithText"));
        l.Add("—");
        l.Add(Loc("DeleteTable"));
        return l.ToArray();
    }

    private static (RichEditor ed, TableBlock tb) TableEditor(bool nested)
    {
        var tb = new TableBlock(2, 2);
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
        if (nested)
        {
            var outer = new TableBlock(1, 1);
            outer.Cells[0][0].Blocks.Insert(0, tb);
            doc.Blocks.Add(outer);
        }
        else doc.Blocks.Add(tb);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below" } } });
        return (new RichEditor { Document = doc }, tb);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheTableMenu_IsTheSameItemsInTheSameOrder_AsUpstreams(bool nested) => UiThread.Run(() =>
    {
        var (ed, tb) = TableEditor(nested);
        Assert.Equal(TableMenuLabels(inlineToggle: !nested), Labels(ed.BuildTableMenu(tb, 0, 0, () => { }, () => { })));
    });

    private static void F5(RichEditor ed)
        => typeof(RichEditor).GetMethod("TryCellBlockKey", NP)!.Invoke(ed, new object[] { Windows.System.VirtualKey.F5, false, false, false });

    // Editing inside a cell, 셀 선택 is right in the text menu — just above the "Table" submenu, not only inside it
    // (user decision, 2026-09-14). The submenu then starts with the rows. Written the same way upstream.
    [Fact]
    public void InACell_TheTextMenuOffersSelectCell_RightAboveTheTableSubmenu() => UiThread.Run(() =>
    {
        var (ed, tb) = TableEditor(nested: false);
        Caret(ed, tb.Cells[1][1].Blocks.OfType<Paragraph>().First(), 0);

        var menu = TextMenu(ed);
        Assert.Equal(new[] { "—", Loc("SelectCell"), Loc("TableOps") }, Labels(menu)[^3..]);
        Assert.True(menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == Loc("SelectCell")).IsEnabled);
        var sub = menu.Items.OfType<MenuFlyoutSubItem>().Single(s => s.Text == Loc("TableOps"));
        Assert.Equal(Loc("InsertRowAbove"), TextOf(sub.Items[0]));
    });

    // The two items that went: a list is turned off by its own toggle, and a cell is copied with F5 then Copy.
    [Fact]
    public void TheMenus_HaveNoRemoveList_AndNoCopyCell() => UiThread.Run(() =>
    {
        var (ed, tb) = TableEditor(nested: false);
        ed.ShowFormattingMenu = true;
        Caret(ed, tb.Cells[0][0].Blocks.OfType<Paragraph>().First(), 0);
        ed.ToggleBullet();

        var texts = Walk(TextMenu(ed).Items).Select(TextOf).ToList();
        Assert.Contains(Loc("TableOps"), texts); // the cell's "Table" submenu is in there
        Assert.DoesNotContain(texts, t => t is "목록 제거" or "Remove List" or "셀 복사" or "Copy Cell");
    });

    // 셀 배경 goes on a ONE-cell block — SelectedCellRange does not see one, so it painted the clicked cell.
    [Fact]
    public void CellBackground_TakesTheOneCellBlock_NotTheClickedCell() => UiThread.Run(() =>
    {
        var (ed, tb) = TableEditor(nested: false);
        Caret(ed, tb.Cells[0][1].Blocks.OfType<Paragraph>().First(), 0);
        F5(ed);
        Assert.Same(tb.Cells[0][1], Assert.Single(ed.CellBackgroundTargets(tb, 1, 1)));

        Caret(ed, tb.Cells[0][0].Blocks.OfType<Paragraph>().First(), 0); // no block: the clicked cell
        Assert.Same(tb.Cells[1][1], Assert.Single(ed.CellBackgroundTargets(tb, 1, 1)));
    });

    // A viewer's inline-table menu is Copy and nothing else; an editor's is the table menu.
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

        if (readOnly) Assert.Equal(new[] { Loc("Copy") }, labels);
        else Assert.Equal(TableMenuLabels(inlineToggle: true).Where(l => l != "—"), labels);
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

            var menu = ed.BuildContextMenuAt(new Windows.Foundation.Point(20, 2040));
            Assert.Equal(TableMenuLabels(inlineToggle: true), Labels(menu)); // the table menu (it was a short object menu)
            Assert.Same(tb, typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed));
            // Held whole, the table names no cell: the cell items are greyed (user decision, 2026-09-14).
            foreach (var key in new[] { "SelectCell", "InsertRowAbove", "InsertRowBelow", "DeleteRow", "InsertColumnLeft",
                                        "UnmergeCells", "CellVerticalAlign", "CellBackground" })
                Assert.False(menu.Items.Single(i => TextOf(i) == Loc(key)).IsEnabled, key);
            Assert.True(menu.Items.Single(i => TextOf(i) == Loc("DeleteTable")).IsEnabled);

            typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.SetValue(ed, null);
            Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == Loc("Copy")));
            var clip = (FlowDocument?)typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.GetValue(ed);
            Assert.IsType<TableBlock>(Assert.Single(clip!.Blocks));
        });
    }

    // A right-click on a cell block opens the table menu and leaves the block selected — it opened the text menu
    // (user decision, 2026-09-14: the menu fits what was clicked).
    [Fact]
    public void ARightClick_OnACellBlock_OpensTheTableMenu_AndKeepsTheBlock()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(2, 2);
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
            doc.Blocks.Add(tb);
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below" } } });
            ed.IsReadOnly = false;
            ed.Document = doc;
            typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            Caret(ed, tb.Cells[0][0].Blocks.OfType<Paragraph>().First(), 0);
            F5(ed);

            var menu = ed.BuildContextMenuAt(PointAt(ed, tb.Cells[1][1].Blocks.OfType<Paragraph>().First(), 0));

            Assert.Equal(TableMenuLabels(inlineToggle: true), Labels(menu));
            Assert.NotNull(typeof(RichEditor).GetMethod("CellBlockSelection", NP)!.Invoke(ed, null));
        });
    }

    // The border band reaches into the grid, so a right-click on it can be over an edge cell. That cell must not become
    // the menu's cell — the table is held whole and names no cell, so the cell items stay greyed (user decision,
    // 2026-09-14). The border test above seeds its rect far below the content, where no cell is under the pointer, so
    // it cannot tell "no cell" from "the cell under the pointer"; this one puts the band over a real cell.
    [Fact]
    public void RightClickingTheBorderBandOverACell_StillNamesNoCell()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(2, 2);
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
            doc.Blocks.Add(tb);
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below" } } });
            ed.IsReadOnly = false;
            ed.Document = doc;
            typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            var cellPara = tb.Cells[1][0].Blocks.OfType<Paragraph>().First();
            var cellPt = PointAt(ed, cellPara, 0);
            var rects = (IDictionary<TableBlock, Windows.Foundation.Rect>)typeof(RichEditor).GetField("_tableRects", NP)!.GetValue(ed)!;
            rects[tb] = new Windows.Foundation.Rect(cellPt.X - 2, cellPt.Y - 30, 200, 80); // left edge just left of the cell's text
            var band = new Windows.Foundation.Point(cellPt.X - 1, cellPt.Y);

            // Guard: the band point really is over that cell — without it this test would prove nothing.
            var under = (TextPointer?)typeof(RichEditor).GetMethod("GetPositionFromPoint", NP)!.Invoke(ed, new object[] { band });
            Assert.Same(cellPara, under?.Paragraph);

            var menu = ed.BuildContextMenuAt(band);

            Assert.Same(tb, typeof(RichEditor).GetField("_selectedBlock", NP)!.GetValue(ed)); // the border: held whole
            foreach (var key in new[] { "SelectCell", "InsertRowAbove", "UnmergeCells", "CellVerticalAlign", "CellBackground" })
                Assert.False(menu.Items.Single(i => TextOf(i) == Loc(key)).IsEnabled, key);
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

            var menu = ed.BuildContextMenuAt(both);
            Assert.Equal(TableMenuLabels(inlineToggle: false), Labels(menu)); // a table in a cell: no 글자처럼 취급
            Assert.Same(inner, Selected());
            typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.SetValue(ed, null);
            Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == Loc("Copy")));
            var copied = Assert.IsType<TableBlock>(Assert.Single(((FlowDocument?)typeof(RichEditor).GetField("_internalClipboardDoc", NP)!.GetValue(ed))!.Blocks));
            Assert.Equal((2, 2), (copied.Rows, copied.Columns)); // the inner table — the outer is 1×2

            typeof(RichEditor).GetMethod("ExitBlockSelection", NP)!.Invoke(ed, new object[] { inner, true });
            Assert.Same(after, CaretNow().Paragraph); // the next paragraph IN the cell

            Caret(ed, inner.Cells[1][1].Blocks.OfType<Paragraph>().First(), 0); // the caret inside the table about to go
            typeof(RichEditor).GetField("_selectedBlock", NP)!.SetValue(ed, inner);
            Invoke(menu.Items.OfType<MenuFlyoutItem>().Single(i => i.Text == Loc("DeleteTable")));
            Assert.DoesNotContain(inner, outer.Cells[0][0].Blocks);
            Assert.Same(outer.Cells[0][0], CaretNow().Paragraph!.Parent);
        });
    }
}
