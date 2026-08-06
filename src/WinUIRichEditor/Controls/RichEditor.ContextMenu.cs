using System;
using Windows.Foundation;
using Windows.UI;
using Windows.ApplicationModel.DataTransfer;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Right-click context menus (per-object: text / link / inline-image / image / table), organized into
// Character-format and Paragraph submenus and carrying RichEditorIcons (Segoe Fluent FontIcons), to match
// the AvaloniaRichEditor original. Built in code (no XAML).
public partial class RichEditor
{
    private void SetupContextMenu() => _canvas.RightTapped += OnCanvasRightTapped;

    private static string Loc(string k) => RichEditorLocalization.GetString(k);

    // Context-menu item font size, in DIPs (px) — WinUI's FontSize unit. 12px gives a compact, HWP-like menu,
    // deliberately smaller than the WinUI/Windows 11 default (ControlContentThemeFontSize = 14px, which reads
    // large like Explorer's menu). FontSize is not an inherited DP, so every menu item sets this explicitly.
    private const double MenuFontSize = 12;

    // Built-in icon for a menu slot (host override > Segoe Fluent FontIcon). Only IconElements fit the
    // MenuFlyoutItem.Icon slot, so a non-icon host element is ignored.
    private static IconElement? MenuIcon(RichEditorIcon icon)
        => (RichEditorIcons.TryCreate(icon) ?? ToolbarIcons.Create(icon)) as IconElement;

    private static MenuFlyoutItem Mi(string text, Action act, bool enabled = true, RichEditorIcon? icon = null, string? accel = null)
    {
        var mi = new MenuFlyoutItem { Text = text, IsEnabled = enabled, FontSize = MenuFontSize };
        if (icon is { } k && MenuIcon(k) is { } ic) mi.Icon = ic;
        // Display-only shortcut hint. The editor handles keys itself (OnKeyDown), so this must not be a real
        // KeyboardAccelerator (that would double-fire and, with the flyout closed, never trigger anyway).
        if (accel != null) mi.KeyboardAcceleratorTextOverride = accel;
        mi.Click += (_, _) => act();
        return mi;
    }

    private static MenuFlyoutSubItem Sub(string text, RichEditorIcon? icon, params MenuFlyoutItemBase[] children)
    {
        var sub = new MenuFlyoutSubItem { Text = text, FontSize = MenuFontSize };
        if (icon is { } k && MenuIcon(k) is { } ic) sub.Icon = ic;
        foreach (var c in children) sub.Items.Add(c);
        return sub;
    }

    private static MenuFlyoutSeparator Sep() => new();

    private Point _ctxMenuPos; // where the last context menu opened (for the table-insert grid flyout)

    // Which context menu a right-click opens. Split out of OnCanvasRightTapped because the decision is
    // the part worth pinning and the handler is the part that cannot be tested — it needs a
    // RightTappedRoutedEventArgs, which has no public constructor. The handler now does the hit-testing
    // and the acting; this decides, and it decides from plain values.
    internal enum ContextMenuKind { BlockImage, InlineImage, InlineTable, ReadOnlyText, Link, Text }

    /// The priority order, which is a contract and not an accident:
    /// an object under the pointer wins over any text menu (you right-clicked the object, not the line);
    /// read-only wins over everything textual (a viewer offers copy, never edit); and the concise link
    /// menu only replaces the full one when there is no selection — with a selection the user is acting
    /// on the selection, not on the link under the pointer.
    internal static ContextMenuKind ChooseContextMenu(
        bool onBlockImage, bool onInlineImage, bool onInlineTableEdge,
        bool isReadOnly, bool hasSelection, string? linkUri)
    {
        if (onBlockImage) return ContextMenuKind.BlockImage;
        if (onInlineImage) return ContextMenuKind.InlineImage;
        if (onInlineTableEdge) return ContextMenuKind.InlineTable;
        if (isReadOnly) return ContextMenuKind.ReadOnlyText;
        if (!hasSelection && !string.IsNullOrEmpty(linkUri)) return ContextMenuKind.Link;
        return ContextMenuKind.Text;
    }

    private void OnCanvasRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        _canvas.Focus(Microsoft.UI.Xaml.FocusState.Pointer);
        CancelTableDraw(); // a right-click abandons an armed table-draw
        var pos = e.GetPosition(_canvas);             // view space — where the menu opens
        _ctxMenuPos = pos;
        var ipt = ViewToDoc(new Point(pos.X, pos.Y)); // doc space — for hit-testing

        // Hit-test the per-object targets first; the decision below turns these into a menu choice.
        ImageBlock? hitBlockImage = null;
        (Paragraph p, InlineImage img)? hitInlineImage = null;
        (Paragraph host, InlineTable it)? hitInlineTable = null;

        foreach (var (img, rect) in BlockImageRects())
            if (rect.Contains(ipt)) { hitBlockImage = img; break; }
        if (hitBlockImage == null)
            foreach (var (img, v) in _inlineImageRects)
                if (v.rect.Contains(ipt)) { hitInlineImage = (v.p, img); break; }
        if (hitBlockImage == null && hitInlineImage == null)
            foreach (var (it, v) in _inlineTableRects)
                if (OnEdgeBorder(v.rect, ipt)) { hitInlineTable = (v.host, it); break; }

        // Right-clicking outside an existing selection moves the caret there first (Word/VS behavior).
        // Only when no object was hit — those select the object instead.
        bool onObject = hitBlockImage != null || hitInlineImage != null || hitInlineTable != null;
        if (!onObject && !HasSelection && GetPositionFromPoint(ipt) is { } tp)
        {
            _caret = tp; CollapseSelectionToCaret(); InvalidateCanvas();
        }

        // The caret may have just moved, so read the selection and the link AFTER it.
        bool hasSel = HasSelection;
        string? linkUri = onObject ? null : CurrentLinkUri();
        var kind = ChooseContextMenu(hitBlockImage != null, hitInlineImage != null, hitInlineTable != null,
                                     IsReadOnly, hasSel, linkUri);

        var menu = new MenuFlyout();
        switch (kind)
        {
            case ContextMenuKind.BlockImage:
                _selectedInline = null; _selectedBlock = hitBlockImage; CollapseSelectionToCaret(); InvalidateCanvas();
                ShowImageMenu(pos, hitBlockImage, null); e.Handled = true; return;

            case ContextMenuKind.InlineImage:
                _selectedBlock = null; _selectedInline = hitInlineImage; CollapseSelectionToCaret(); InvalidateCanvas();
                ShowImageMenu(pos, null, hitInlineImage!.Value.img); e.Handled = true; return;

            case ContextMenuKind.InlineTable:
                ClearObjectSelection(); _selectedInlineTable = hitInlineTable; CollapseSelectionToCaret(); InvalidateCanvas();
                ShowInlineTableMenu(pos, hitInlineTable!.Value.host, hitInlineTable.Value.it); e.Handled = true; return;

            case ContextMenuKind.ReadOnlyText: // a viewer: copy + select-all only
                menu.Items.Add(Mi(Loc("Copy"), () => _ = CopyAsync(), hasSel, RichEditorIcon.Copy, "Ctrl+C"));
                menu.Items.Add(Mi(Loc("SelectAll"), SelectAll, true, RichEditorIcon.SelectAll, "Ctrl+A"));
                break;

            case ContextMenuKind.Link:
                BuildLinkMenu(menu, linkUri!);
                break;

            default:
                BuildTextMenu(menu, hasSel, linkUri);
                break;
        }

        menu.ShowAt(_canvas, pos);
        e.Handled = true;
    }

    // The caret-position text menu, shared by top-level paragraphs and table cells.
    // HWP-style arrangement: edit block → separator → 글자 모양 / 문단 모양 / 목록 / 제목 grouping submenus →
    // hyperlink → select/undo → inserts. Flattened vs. the old layout — alignment and margin now live directly
    // under 문단 모양 (no extra nesting level), and list/heading are promoted to top level. Windows-standard
    // clipboard terms (잘라내기/복사/붙여넣기) are kept; only the layout changed.
    private void BuildTextMenu(MenuFlyout menu, bool hasSel, string? linkUri)
    {
        void Item(MenuFlyoutItemBase i) => menu.Items.Add(i);
        var fmt = GetCaretFormat(); // current caret state → radio/check reflection

        // ── Edit (clipboard) ──
        Item(Mi(Loc("Cut"), () => _ = CutAsync(), hasSel, RichEditorIcon.Cut, "Ctrl+X"));
        Item(Mi(Loc("Copy"), () => _ = CopyAsync(), hasSel, RichEditorIcon.Copy, "Ctrl+C"));
        Item(Mi(Loc("Paste"), () => _ = PasteAsync(), true, RichEditorIcon.Paste, "Ctrl+V"));
        Item(Mi(Loc("Delete"), () => { if (HasSelection) { PushUndo(null); DeleteSelection(); AfterEdit(); } }, hasSel, RichEditorIcon.Delete, "Del"));
        Item(Sep());

        if (ShowFormattingMenu)
        {
            // ── 글자 모양 (character shape) ── quick toggles + font size larger/smaller + clear.
            // Precise pickers (specific size, text color, highlight, font family) live on the toolbar.
            // Always enabled: without a selection ApplyStyleToSelection targets the caret word, or arms a
            // pending format for the next typed text — same as the slim menu and the keyboard shortcuts.
            Item(Sub(Loc("CharacterFormat"), RichEditorIcon.CharacterFormat,
                Mi(Loc("Bold"), ToggleBold, true, RichEditorIcon.Bold, "Ctrl+B"),
                Mi(Loc("Italic"), ToggleItalic, true, RichEditorIcon.Italic, "Ctrl+I"),
                Mi(Loc("Underline"), ToggleUnderline, true, RichEditorIcon.Underline, "Ctrl+U"),
                Mi(Loc("Strikethrough"), ToggleStrikethrough, true, RichEditorIcon.Strikethrough, "Ctrl+Shift+X"),
                Sep(),
                Mi(Loc("FontSizeIncrease"), IncreaseFontSize, true, RichEditorIcon.FontSizeIncrease, "Ctrl+Shift+."),
                Mi(Loc("FontSizeDecrease"), DecreaseFontSize, true, RichEditorIcon.FontSizeDecrease, "Ctrl+Shift+,"),
                Sep(),
                Mi(Loc("ClearFormatting"), ClearFormatting, true, RichEditorIcon.ClearFormatting)));

            // ── 문단 모양 (paragraph shape) — alignment radios + indent + margin, flattened ──
            Item(BuildParagraphFormatSub(fmt));

            // ── 목록 (list) — promoted to top level ──
            Item(Sub(Loc("List"), RichEditorIcon.BulletList,
                BulletToggle(Loc("BulletList"), fmt.List == ListKind.Bullet, ToggleBullet, RichEditorShortcuts.Display(ShortcutId.BulletList)),
                // Style submenus list STYLES only. Removal has exactly two doors — the toggle above
                // (which now clears the whole list state) and the labelled "목록 제거" below — instead
                // of the three it used to have (a "없음" first item in each of these two submenus).
                Sub(Loc("BulletStyle"), null,
                    Mi("•", () => SetListStyle(ListMarkerStyle.Disc)), Mi("◦", () => SetListStyle(ListMarkerStyle.Circle)),
                    Mi("▪", () => SetListStyle(ListMarkerStyle.Square)), Mi("–", () => SetListStyle(ListMarkerStyle.Dash))),
                BulletToggle(Loc("NumberedList"), fmt.List == ListKind.Ordered, ToggleNumbering, RichEditorShortcuts.Display(ShortcutId.NumberedList)),
                Sub(Loc("NumberStyle"), null,
                    Mi("1.", () => SetListStyle(ListMarkerStyle.Decimal)), Mi("1)", () => SetListStyle(ListMarkerStyle.DecimalParen)),
                    Mi("a)", () => SetListStyle(ListMarkerStyle.LowerAlpha)), Mi("A)", () => SetListStyle(ListMarkerStyle.UpperAlpha)),
                    Mi("i)", () => SetListStyle(ListMarkerStyle.LowerRoman))),
                Sep(),
                Mi(Loc("RemoveList"), RemoveList, fmt.List != ListKind.None),
                Sep(),
                BulletToggle(Loc("Quote"), fmt.Quote, ToggleQuote)));

            // ── 제목 (heading / style) — promoted to top level, radio-checked ──
            Item(Sub(Loc("Heading"), null,
                HeadingItem(Loc("Heading1"), 1, fmt.Heading), HeadingItem(Loc("Heading2"), 2, fmt.Heading),
                HeadingItem(Loc("Heading3"), 3, fmt.Heading), HeadingItem(Loc("Heading4"), 4, fmt.Heading),
                HeadingItem(Loc("Heading5"), 5, fmt.Heading), HeadingItem(Loc("Heading6"), 6, fmt.Heading),
                Sep(),
                HeadingItem(Loc("BodyText"), 0, fmt.Heading)));
        }
        else
        {
            // Slim (default): just the quick character toggles, checked to reflect the caret. Full formatting
            // lives on the toolbar; a standalone host can opt into the rich groups via ShowFormattingMenu.
            Item(BulletToggle(Loc("Bold"), fmt.Bold, ToggleBold, "Ctrl+B"));
            Item(BulletToggle(Loc("Italic"), fmt.Italic, ToggleItalic, "Ctrl+I"));
            Item(BulletToggle(Loc("Underline"), fmt.Underline, ToggleUnderline, "Ctrl+U"));
        }

        Item(Sep());

        // ── Hyperlink ──
        if (!string.IsNullOrEmpty(linkUri))
        {
            Item(Mi(Loc("OpenLink"), () => _ = OpenLinkAtCaretAsync(), true, RichEditorIcon.OpenLink));
            Item(Mi(Loc("EditLink"), () => _ = EditHyperlinkAsync(), true, RichEditorIcon.EditLink, RichEditorShortcuts.Display(ShortcutId.InsertLink)));
            Item(Mi(Loc("RemoveLink"), () => SetHyperlink(null), true, RichEditorIcon.RemoveLink));
        }
        else
            // Enabled without a selection too: the link applies to the caret word (ApplyStyleToSelection).
            Item(Mi(Loc("InsertLink"), () => _ = EditHyperlinkAsync(), true, RichEditorIcon.InsertLink, RichEditorShortcuts.Display(ShortcutId.InsertLink)));

        Item(Sep());
        Item(Mi(Loc("SelectAll"), SelectAll, true, RichEditorIcon.SelectAll, "Ctrl+A"));
        Item(Mi(Loc("Undo"), Undo, CanUndo, RichEditorIcon.Undo, "Ctrl+Z"));
        Item(Mi(Loc("Redo"), Redo, CanRedo, RichEditorIcon.Redo, "Ctrl+Y"));

        // Inserts (gated by feature flags)
        if (AllowTables || AllowImages) Item(Sep());
        if (AllowTables) Item(Mi(Loc("InsertTable"), () => ShowInsertTableGrid(_ctxMenuPos), true, RichEditorIcon.InsertTable));
        if (AllowTables || AllowImages) Item(Mi(Loc("InsertDivider"), InsertDivider, true, RichEditorIcon.InsertDivider));

        // Inside a cell: the table-structure operations in a "Table" submenu.
        var cellLoc = _caret.Paragraph != null ? FindCell(_caret.Paragraph) : null;
        if (cellLoc is { } loc)
        {
            Item(Sep());
            menu.Items.Add(BuildTableSubmenu(loc.tb, loc.r, loc.c));
        }
    }

    // 문단 모양 submenu: alignment as a radio group (current value checked) + indent +/- + per-side margin.
    // Flattens the old 문단 ▸ 정렬 ▸ / 여백 nesting into one level, HWP-style.
    private MenuFlyoutSubItem BuildParagraphFormatSub(CaretFormat fmt)
    {
        RadioMenuFlyoutItem Align(string key, TextAlignment a, ShortcutId sc)
        {
            var ri = new RadioMenuFlyoutItem { Text = Loc(key), GroupName = "ctxAlign", IsChecked = fmt.Align == a, FontSize = MenuFontSize };
            ri.KeyboardAcceleratorTextOverride = RichEditorShortcuts.Display(sc);
            ri.Click += (_, _) => SetTextAlignment(a);
            return ri;
        }
        var sub = new MenuFlyoutSubItem { Text = Loc("ParagraphFormat"), FontSize = MenuFontSize };
        sub.Items.Add(Align("AlignLeft", TextAlignment.Left, ShortcutId.AlignLeft));
        sub.Items.Add(Align("AlignCenter", TextAlignment.Center, ShortcutId.AlignCenter));
        sub.Items.Add(Align("AlignRight", TextAlignment.Right, ShortcutId.AlignRight));
        sub.Items.Add(Align("AlignJustify", TextAlignment.Justify, ShortcutId.AlignJustify));
        sub.Items.Add(Sep());
        sub.Items.Add(Mi(Loc("IndentIncrease"), () => Indent(20), true, RichEditorIcon.IndentIncrease, RichEditorShortcuts.Display(ShortcutId.IndentIncrease)));
        sub.Items.Add(Mi(Loc("IndentDecrease"), () => Indent(-20), true, RichEditorIcon.IndentDecrease, RichEditorShortcuts.Display(ShortcutId.IndentDecrease)));
        // Margin (top-level paragraphs only — cell paragraphs lay out inside the cell).
        if (_caret.Paragraph is { } mp && Document != null && Document.Blocks.IndexOf(mp) >= 0)
        {
            sub.Items.Add(Sep());
            sub.Items.Add(MarginMenu(mp));
        }
        return sub;
    }

    // A checkable toggle item for a list/quote state (checked reflects the caret paragraph).
    private static ToggleMenuFlyoutItem BulletToggle(string text, bool isChecked, Action act, string? accel = null)
    {
        var t = new ToggleMenuFlyoutItem { Text = text, IsChecked = isChecked, FontSize = MenuFontSize };
        if (accel != null) t.KeyboardAcceleratorTextOverride = accel;
        t.Click += (_, _) => act();
        return t;
    }

    // A heading level as a radio item within the 제목 group (current level checked).
    private RadioMenuFlyoutItem HeadingItem(string text, int level, int current)
    {
        var ri = new RadioMenuFlyoutItem { Text = text, GroupName = "ctxHeading", IsChecked = current == level, FontSize = MenuFontSize };
        // Heading1..6 are consecutive enum values; level 0 = body text.
        var sc = level == 0 ? ShortcutId.BodyText : (ShortcutId)((int)ShortcutId.Heading1 + level - 1);
        ri.KeyboardAcceleratorTextOverride = RichEditorShortcuts.Display(sc);
        ri.Click += (_, _) => SetHeading(level);
        return ri;
    }


    // The same 8×10 grid picker the toolbar button uses (hover to choose rows×columns, click to insert at
    // the caret). WinUI menus can't host a grid, so the menu item opens this as a separate flyout.
    private void ShowInsertTableGrid(Point pos)
    {
        if (!AllowTables || IsReadOnly) return;
        const int rows = 8, cols = 10;
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft, FlyoutPresenterStyle = RichEditorToolbar.TightFlyoutPresenter() };
        var root = new StackPanel { Spacing = 4, Padding = new Thickness(2) };
        var label = new TextBlock { Text = "1 × 1", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
        var grid = new Grid();
        for (int c = 0; c < cols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < rows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var idle = new SolidColorBrush(Color.FromArgb(255, 0xE4, 0xE4, 0xE4));
        var hot = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xA0, 0xE0));
        var border = new SolidColorBrush(Color.FromArgb(255, 0xAA, 0xAA, 0xAA));
        var cells = new Border[rows, cols];

        void Highlight(int rr, int cc)
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                    cells[r, c].Background = (r <= rr && c <= cc) ? hot : idle;
            label.Text = $"{rr + 1} × {cc + 1}";
        }

        for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                var cell = new Border
                {
                    Width = 16, Height = 16, Margin = new Thickness(1),
                    Background = idle, BorderBrush = border, BorderThickness = new Thickness(0.5),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                int rr = r, cc = c;
                cell.PointerEntered += (_, _) => Highlight(rr, cc);
                // Pick rows×cols here; then drag from the caret on the document to set the table's size.
                cell.Tapped += (_, _) => { BeginTableDraw(rr + 1, cc + 1); flyout.Hide(); };
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }
        Highlight(0, 0);
        root.Children.Add(grid);
        root.Children.Add(label);
        flyout.Content = root;
        flyout.ShowAt(_canvas, new FlyoutShowOptions { Position = pos });
    }

    // Per-side margin presets for a block (radio-checked at the current value). Left maps to Block.Indent;
    // right exists for paragraphs only (nothing flows around images/tables).
    private MenuFlyoutSubItem MarginMenu(Block target)
    {
        MenuFlyoutSubItem Side(string label, Func<double> get, Action<double> set)
        {
            var s = new MenuFlyoutSubItem { Text = label, FontSize = MenuFontSize };
            foreach (double v in new[] { 0d, 5, 10, 20, 30 })
            {
                double vv = v;
                var ri = new RadioMenuFlyoutItem { Text = $"{vv:0} px", GroupName = label, IsChecked = Math.Abs(get() - vv) < 0.5, FontSize = MenuFontSize };
                ri.Click += (_, _) => { if (Document != null) PushUndo(null); set(vv); AfterFormat(); };
                s.Items.Add(ri);
            }
            return s;
        }
        var sub = new MenuFlyoutSubItem { Text = Loc("Margin"), FontSize = MenuFontSize };
        sub.Items.Add(Side(Loc("MarginTop"), () => target.MarginTop, v => target.MarginTop = v));
        sub.Items.Add(Side(Loc("MarginBottom"), () => target.MarginBottom, v => target.MarginBottom = v));
        sub.Items.Add(Side(Loc("MarginLeft"), () => target.Indent, v => target.Indent = v));
        if (target is Paragraph mp) sub.Items.Add(Side(Loc("MarginRight"), () => mp.MarginRight, v => mp.MarginRight = v));
        return sub;
    }

    // Cell vertical-alignment radio submenu for the anchor cell at (r,c) — Top/Center/Bottom, checked
    // at the current value. Row heights are unchanged (alignment redistributes slack), so a repaint
    // plus the undo checkpoint is all that's needed.
    private MenuFlyoutSubItem BuildCellVAlignSub(TableBlock tb, int r, int c)
    {
        var (ar, ac) = tb.AnchorOf(r, c);
        var cell = tb.Cells[ar][ac];
        var sub = new MenuFlyoutSubItem { Text = Loc("CellVerticalAlign"), FontSize = MenuFontSize };
        void AddOpt(string key, CellVerticalAlignment va)
        {
            var ri = new RadioMenuFlyoutItem
            { Text = Loc(key), GroupName = "ctxCellVAlign", IsChecked = cell.VerticalAlignment == va, FontSize = MenuFontSize };
            ri.Click += (_, _) =>
            {
                if (Document == null || IsReadOnly || cell.VerticalAlignment == va) return;
                PushUndo(null);
                cell.VerticalAlignment = va;
                InvalidateCanvas();
                RaiseStatusChanged();
            };
            sub.Items.Add(ri);
        }
        AddOpt("VAlignTop", CellVerticalAlignment.Top);
        AddOpt("VAlignCenter", CellVerticalAlignment.Center);
        AddOpt("VAlignBottom", CellVerticalAlignment.Bottom);
        return sub;
    }

    // Concise menu for a right-clicked hyperlink: link actions + copy address, no formatting clutter.
    private void BuildLinkMenu(MenuFlyout menu, string uri)
    {
        menu.Items.Add(Mi(Loc("OpenLink"), () => _ = OpenLinkAtCaretAsync(), true, RichEditorIcon.OpenLink));
        if (!IsReadOnly)
        {
            menu.Items.Add(Sep()); // navigation vs. edit
            menu.Items.Add(Mi(Loc("EditLink"), () => _ = EditHyperlinkAsync(), true, RichEditorIcon.EditLink));
            menu.Items.Add(Mi(Loc("RemoveLink"), () => SetHyperlink(null), true, RichEditorIcon.RemoveLink));
        }
        menu.Items.Add(Mi(Loc("CopyLink"), () =>
        {
            var dp = new DataPackage();
            dp.SetText(uri);
            Clipboard.SetContent(dp);
        }, true, RichEditorIcon.CopyLink));
        menu.Items.Add(Sep());
        menu.Items.Add(Mi(Loc("Copy"), () => _ = CopyAsync(), HasSelection, RichEditorIcon.Copy, "Ctrl+C"));
    }

    // Image context menu: size presets (fraction of natural), replace, save, margin, block↔inline, delete.
    private void ShowImageMenu(Point pos, ImageBlock? block, InlineImage? inline)
    {
        string L(string k) => RichEditorLocalization.GetString(k);
        var menu = new MenuFlyout();
        void Add(string text, Action act, bool enabled = true, RichEditorIcon? icon = null) => menu.Items.Add(Mi(text, act, enabled, icon));
        void Original() { if (block != null) ResetBlockImageNatural(block); else if (inline != null) ResetInlineImageNatural(inline); }
        void Scale(double f) { if (block != null) ScaleBlockImage(block, f); else if (inline != null) ScaleInlineImage(inline, f); }

        byte[]? raw = block?.RawBytes ?? inline?.RawBytes;
        string? mime = block?.MimeType ?? inline?.MimeType;
        var bmp = block?.Image ?? inline?.Image;
        bool hasNatural = NaturalImageSize(raw) != null;

        // ── Edit ── Copy the image to the system clipboard (works in-app and into other apps). Pass
        // inline-ness and the displayed size so an in-app paste restores them (see CopyImageToClipboardAsync).
        bool isInline = inline != null;
        double iw = block?.Width ?? inline?.Width ?? 0;
        double ih = block?.Height ?? inline?.Height ?? 0;
        menu.Items.Add(Mi(L("Copy"), () => _ = CopyImageToClipboardAsync(raw, bmp, isInline, iw, ih), raw != null || bmp != null, RichEditorIcon.Copy, "Ctrl+C"));

        // ── 개체 모양 (object shape): size preset, 글자처럼 취급, margin ──
        if (!IsReadOnly)
        {
            menu.Items.Add(Sep());
            menu.Items.Add(Sub(L("ImageSize"), null,
                Mi(L("OriginalSize"), Original, hasNatural),
                Mi(L("HalfSize"), () => Scale(1.0 / 2)),
                Mi(L("ThirdSize"), () => Scale(1.0 / 3)),
                Mi(L("QuarterSize"), () => Scale(1.0 / 4))));
            // "Treat as character": checked while the image is inline; toggling converts block↔inline.
            var inlineToggle = new ToggleMenuFlyoutItem { Text = L("InlineWithText"), IsChecked = inline != null, FontSize = MenuFontSize };
            inlineToggle.Click += (_, _) =>
            {
                if (block != null) ConvertImageBlockToInline(block);
                else if (inline != null) ConvertInlineImageToBlock(inline.Parent as Paragraph ?? _caret.Paragraph!, inline);
            };
            menu.Items.Add(inlineToggle);
            if (block != null) menu.Items.Add(MarginMenu(block));
            // Accessibility description (HTML alt). Round-trips through JSON/.flow and HTML.
            menu.Items.Add(Mi(L("AltText"), () => _ = EditImageAltTextAsync(block, inline)));
        }

        // ── File ops: replace / save ──
        bool canReplace = !IsReadOnly && ImageReplacePicker != null;
        bool canSave = ImageSaveHandler != null;
        if (canReplace || canSave)
        {
            menu.Items.Add(Sep());
            if (canReplace) Add(L("ReplaceImage"), () => _ = ReplaceImageBytesAsync((object?)block ?? inline!), true, RichEditorIcon.ReplaceImage);
            if (canSave) Add(L("SaveImageAs"), () => _ = SaveImageBytesAsync(raw, mime), raw != null, RichEditorIcon.SaveImageAs);
        }

        // ── Delete ──
        if (!IsReadOnly)
        {
            menu.Items.Add(Sep());
            Add(L("Delete"), DeleteSelectedObject, true, RichEditorIcon.Delete);
        }
        menu.ShowAt(_canvas, pos);
    }

    // Cell-background palette flyout (the toolbar's 40 swatches + "none"). Applies to the selected
    // rectangular cell block when the selection spans cells of this table, else to the cell at (r,c).
    private void ShowCellBackgroundPalette(TableBlock tb, int r, int c)
    {
        if (IsReadOnly || Document == null) return;
        var flyout = new Flyout { FlyoutPresenterStyle = RichEditorToolbar.TightFlyoutPresenter() };

        void Apply(Color? color)
        {
            flyout.Hide();
            PushUndo(null);
            if (SelectedCellRange(tb) is { } rg)
            {
                foreach (var (rr, cc, cell) in tb.LogicalCells())
                    if (rr >= rg.r0 && rr <= rg.r1 && cc >= rg.c0 && cc <= rg.c1)
                        cell.Background = color;
            }
            else
            {
                var (ar, ac) = tb.AnchorOf(r, c);
                tb.Cells[ar][ac].Background = color;
            }
            InvalidateCanvas();
            RaiseStatusChanged();
        }

        var grid = new ToolbarWrapPanel { Width = 8 * 24, HorizontalSpacing = 0, VerticalSpacing = 0 };
        var borderBrush = new SolidColorBrush(Color.FromArgb(255, 0x80, 0x80, 0x80));
        foreach (var hex in RichEditorToolbar.Palette)
        {
            if (ColorUtil.Parse(hex) is not { } color) continue;
            var sw = new Button
            {
                Background = new SolidColorBrush(color),
                Width = 22, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0),
                BorderThickness = new Thickness(1), BorderBrush = borderBrush,
            };
            sw.Click += (_, _) => Apply(color);
            grid.Children.Add(sw);
        }

        var none = new Button { Content = Loc("HighlightNone"), HorizontalAlignment = HorizontalAlignment.Stretch };
        none.Click += (_, _) => Apply(null);

        var panel = new StackPanel { Spacing = 6, Width = 200, Padding = new Thickness(4) };
        panel.Children.Add(grid);
        panel.Children.Add(none);
        flyout.Content = panel;
        flyout.ShowAt(_canvas, new FlyoutShowOptions { Position = _ctxMenuPos });
    }

    // Menu for a whole inline table selected by its border: block↔inline toggle + delete.
    private void ShowInlineTableMenu(Point pos, Paragraph host, InlineTable it)
    {
        var menu = new MenuFlyout();
        if (!IsReadOnly)
        {
            // 표 모양: 글자처럼 취급 (checked while inline) → separator → 표 삭제.
            var t = new ToggleMenuFlyoutItem { Text = Loc("InlineWithText"), IsChecked = true, FontSize = MenuFontSize };
            t.Click += (_, _) => ConvertInlineTableToBlock(host, it);
            menu.Items.Add(t);
            menu.Items.Add(Sep());
            menu.Items.Add(Mi(Loc("DeleteTable"), DeleteSelectedInlineTable, true, RichEditorIcon.DeleteTable));
        }
        menu.ShowAt(_canvas, pos);
    }

    // The table-structure operations, shown as a "Table" submenu when editing inside a cell.
    private MenuFlyoutSubItem BuildTableSubmenu(TableBlock tb, int r, int c)
    {
        var sub = new MenuFlyoutSubItem { Text = Loc("TableOps") };
        void Add(string text, Action act, bool enabled = true, RichEditorIcon? icon = null) => sub.Items.Add(Mi(text, act, enabled, icon));

        // ── Edit ── Copy the current cell as a 1×1 sub-table (a whole merged cell copies its content), so it
        // pastes back as a cell — not just text. A multi-cell rectangle uses the ordinary Copy (Ctrl+C) path.
        Add(Loc("CopyCell"), () => _ = CopyCell(tb, r, c), r >= 0 && c >= 0, RichEditorIcon.Copy);
        sub.Items.Add(Sep());
        // ── Rows ──
        Add(Loc("InsertRowAbove"), () => TableInsertRow(tb, r), r >= 0, RichEditorIcon.InsertRowAbove);
        Add(Loc("InsertRowBelow"), () => TableInsertRow(tb, r + 1), r >= 0, RichEditorIcon.InsertRowBelow);
        Add(Loc("DeleteRow"), () => TableDeleteRow(tb, r), r >= 0 && tb.Rows > 1, RichEditorIcon.DeleteRow);
        sub.Items.Add(Sep());
        // ── Columns ──
        Add(Loc("InsertColumnLeft"), () => TableInsertColumn(tb, c), c >= 0, RichEditorIcon.InsertColumnLeft);
        Add(Loc("InsertColumnRight"), () => TableInsertColumn(tb, c + 1), c >= 0, RichEditorIcon.InsertColumnRight);
        Add(Loc("DeleteColumn"), () => TableDeleteColumn(tb, c), c >= 0 && tb.Columns > 1, RichEditorIcon.DeleteColumn);
        sub.Items.Add(Sep());
        // ── Merge / split ──
        bool canMerge = SelectedCellRange(tb) is { } rg && IsCleanRect(tb, rg.r0, rg.c0, rg.r1, rg.c1);
        Add(Loc("MergeCells"), () => TableMergeSelected(tb), canMerge, RichEditorIcon.MergeCells);
        bool canUnmerge = r >= 0 && c >= 0 && (tb.SpanOf(r, c).cs > 1 || tb.SpanOf(r, c).rs > 1);
        Add(Loc("UnmergeCells"), () => TableUnmergeCell(tb, r, c), canUnmerge, RichEditorIcon.UnmergeCells);
        sub.Items.Add(Sep());
        // ── 표 모양 (table shape): cell vertical alignment + background + margin + 글자처럼 취급 ──
        if (r >= 0 && c >= 0) sub.Items.Add(BuildCellVAlignSub(tb, r, c));
        // Cell background: model + every serializer already round-trip TableCell.Background; this
        // palette (shared with the toolbar pickers) is the missing edit surface. Applies to the
        // selected cell rectangle when one exists, else the current cell.
        if (r >= 0 && c >= 0)
            Add(Loc("CellBackground"), () => ShowCellBackgroundPalette(tb, r, c));
        sub.Items.Add(MarginMenu(tb));
        if (tb.Parent is FlowDocument)
        {
            var t = new ToggleMenuFlyoutItem { Text = Loc("InlineWithText"), IsChecked = false, FontSize = MenuFontSize };
            t.Click += (_, _) => ConvertTableBlockToInline(tb);
            sub.Items.Add(t);
        }
        else if (tb.Parent is InlineTable it && it.Parent is Paragraph host)
        {
            var t = new ToggleMenuFlyoutItem { Text = Loc("InlineWithText"), IsChecked = true, FontSize = MenuFontSize };
            t.Click += (_, _) => ConvertInlineTableToBlock(host, it);
            sub.Items.Add(t);
        }
        Add(Loc("DeleteTable"), () => DeleteTable(tb), true, RichEditorIcon.DeleteTable);
        return sub;
    }
}
