using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 3a: focus, pointer hit-testing, caret, drag/keyboard selection and a self-contained editing
// core (text insert, backspace/delete, Enter split, caret navigation) for top-level paragraphs.
public partial class RichEditor
{
    private readonly UndoManager _undo = new();
    // Coalescing token: consecutive edits sharing a key (typing, deleting, IME) form one undo group.
    private string? _coalesceKey;

    private TextPointer _caret = new(null, 0);
    private TextPointer _selStart = new(null, 0);
    private TextPointer _selEnd = new(null, 0);
    private bool _isSelecting;
    private bool _hasFocus;
    private bool _caretOn = true;
    private double _desiredCaretX; // remembered x for vertical (up/down) caret movement
    private DispatcherTimer? _blink;

    // Manual multi-click tracking: WinUI pointer events expose no ClickCount, so double/triple-click
    // (word / paragraph selection) is detected from press timing + proximity.
    private DateTime _lastPressTime;
    private Point _lastPressPos;
    private int _clickCount;
    private const double MultiClickMs = 500;
    private const double MultiClickSlop = 6;

    // SelectionFill / CaretColor are now instance-computed from SelectionBrush / CaretBrush
    // (see RichEditor.Appearance.cs).

    // Called from the constructor to wire input once the visual tree exists. Focus + keyboard live on
    // the CanvasVirtualControl (the element actually under the pointer), not the outer ContentControl —
    // otherwise a click focuses the canvas and the outer control's key/char handlers never fire.
    private void SetupInput()
    {
        IsTabStop = false;
        _scroll.IsTabStop = false;
        _canvas.IsTabStop = true;
        _canvas.UseSystemFocusVisuals = false;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.IBeam); // text I-beam by default

        _canvas.PointerPressed += OnCanvasPointerPressed;
        _canvas.PointerMoved += OnCanvasPointerMoved;
        _canvas.PointerReleased += OnCanvasPointerReleased;
        _canvas.PointerWheelChanged += OnCanvasPointerWheel;

        _canvas.KeyDown += OnEditorKeyDown;
        _canvas.CharacterReceived += OnEditorCharacterReceived;
        _canvas.GotFocus += (_, _) => { _hasFocus = true; ImeNotifyFocusEnter(); RestartBlink(); InvalidateCanvas(); };
        _canvas.LostFocus += (_, _) => { _hasFocus = false; ImeNotifyFocusLeave(); StopBlink(); InvalidateCanvas(); };

        // Drag-and-drop of image files onto the editor (inserts each as a block image).
        AllowDrop = true;
        DragOver += OnEditorDragOver;
        Drop += OnEditorDrop;

        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) => { _caretOn = !_caretOn; InvalidateCanvas(); };

        SetupIme();
        SetupContextMenu();
    }

    private void RestartBlink() { _caretOn = true; _blink?.Stop(); if (_hasFocus) _blink?.Start(); }
    private void StopBlink() { _blink?.Stop(); _caretOn = false; }
    private void InvalidateCanvas() => _canvas.Invalidate();

    private bool IsCaretInParagraph(Paragraph p) => ReferenceEquals(_caret.Paragraph, p);
    private bool HasSelection => _selStart.Paragraph != null && _selEnd.Paragraph != null && ComparePositions(_selStart, _selEnd) != 0;

    // Document-order paragraph index, built lazily and dropped on any mutation (MarkTextChanged — every
    // edit path goes through PushUndo) and on relayout. Comparing selection endpoints in different
    // paragraphs otherwise walks the whole document (TextPointer.CompareTo); called per drawn paragraph
    // during a drag selection, that made selection rendering O(visible × document).
    private Dictionary<Paragraph, int>? _paraIndexMap;

    private Dictionary<Paragraph, int> ParaIndexMap()
    {
        if (_paraIndexMap != null) return _paraIndexMap;
        var map = new Dictionary<Paragraph, int>();
        int i = 0;
        foreach (var p in AllParagraphs()) map[p] = i++;
        return _paraIndexMap = map;
    }

    // Fast document-order comparison of two positions. Same relative order as TextPointer.CompareTo
    // (both walks are document-order recursive); a paragraph not in the map sorts first, matching
    // CompareTo's "not found keeps index -1".
    private int ComparePositions(TextPointer a, TextPointer b)
    {
        if (ReferenceEquals(a.Paragraph, b.Paragraph)) return a.Offset.CompareTo(b.Offset);
        var map = ParaIndexMap();
        int ia = a.Paragraph != null && map.TryGetValue(a.Paragraph, out var va) ? va : -1;
        int ib = b.Paragraph != null && map.TryGetValue(b.Paragraph, out var vb) ? vb : -1;
        return ia.CompareTo(ib);
    }

    private static bool KeyDownState(VirtualKey k)
        => (InputKeyboardSource.GetKeyStateForCurrentThread(k) & CoreVirtualKeyStates.Down) != 0;
    private static bool Shift => KeyDownState(VirtualKey.Shift);
    private static bool Ctrl => KeyDownState(VirtualKey.Control);
    private static bool Alt => KeyDownState(VirtualKey.Menu);

    // ---- parent wiring + initial caret (called when Document changes) ------
    private void OnDocumentAssigned()
    {
        ClearObjectSelection();
        _resizingImage = null;
        _resizingInline = null;
        if (Document != null)
        {
            UpdateParents(Document);
            var first = FirstParagraph();
            _caret = new TextPointer(first, 0);
            _selStart = new TextPointer(first, 0);
            _selEnd = new TextPointer(first, 0);
        }
        else { _caret = _selStart = _selEnd = new TextPointer(null, 0); }
    }

    private Paragraph? FirstParagraph() => AllParagraphs().FirstOrDefault();

    private List<Paragraph> AllParagraphs()
        => Document == null ? new() : ParagraphsInBlocks(Document.Blocks).ToList();

    private static IEnumerable<Paragraph> ParagraphsInBlocks(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                yield return p;
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var q in ParagraphsInBlocks(cell.Blocks)) yield return q;
            }
            else if (block is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var q in ParagraphsInBlocks(cell.Blocks)) yield return q;
        }
    }

    private void UpdateParents(FlowDocument doc)
    {
        NormalizeBlockList(doc.Blocks, doc);
        foreach (var block in doc.Blocks) WireBlockParents(block, doc);
    }

    private static void NormalizeBlockList(IList<Block> blocks, object parent)
    {
        if (blocks.Count == 0 || blocks[0] is not Paragraph)
            blocks.Insert(0, new Paragraph { Parent = parent, Inlines = { new Run { Text = "" } } });
        if (blocks[blocks.Count - 1] is not Paragraph)
            blocks.Add(new Paragraph { Parent = parent, Inlines = { new Run { Text = "" } } });
        for (int i = 0; i < blocks.Count - 1; i++)
            if (blocks[i] is not Paragraph && blocks[i + 1] is not Paragraph)
                blocks.Insert(i + 1, new Paragraph { Parent = parent, Inlines = { new Run { Text = "" } } });
        foreach (var b in blocks)
        {
            if (b is TableBlock tb)
                foreach (var row in tb.Cells)
                    foreach (var cell in row)
                        NormalizeBlockList(cell.Blocks, cell);
            // An INLINE table's cells are recursive containers too (rules #4/#5), but they hang off a
            // paragraph's inlines rather than the block list, so this walk used to skip them entirely.
            // The deserializer only injects a paragraph when a cell has ZERO blocks, so a .flow whose
            // inline-table cell holds just an image kept a paragraph-less cell: FirstCellPara/
            // TableCaretParas returned nothing and the caret could never enter it.
            else if (b is Paragraph p)
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                        foreach (var row in it.Table.Cells)
                            foreach (var cell in row)
                                NormalizeBlockList(cell.Blocks, cell);
        }
    }

    private static void WireBlockParents(Block block, object parent)
    {
        block.Parent = parent;
        switch (block)
        {
            case Paragraph p:
                foreach (var inline in p.Inlines)
                {
                    inline.Parent = p;
                    if (inline is InlineTable it) WireBlockParents(it.Table, it);
                }
                break;
            case TableBlock tb:
                for (int r = 0; r < tb.Rows; r++)
                    for (int c = 0; c < tb.Columns; c++)
                    {
                        var cell = tb.Cells[r][c];
                        cell.Parent = tb;
                        foreach (var cb in cell.Blocks) WireBlockParents(cb, cell);
                    }
                break;
        }
    }

    // ---- pointer ----------------------------------------------------------
    // Read-only viewer: link armed at press, launched on RELEASE if the press didn't become a drag —
    // opening on press would make link text unselectable (browser convention: click opens, drag selects).
    private (string uri, Point pos)? _pressLink;

    private void OnCanvasPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _pressLink = null; // every press re-arms; a stale value must never fire on a later release
        _canvas.Focus(FocusState.Pointer);
        // Right button is handled by RightTapped (context menu); don't collapse the selection here.
        if (e.GetCurrentPoint(_canvas).Properties.IsRightButtonPressed) return;
        var ptp = ViewToDoc(e.GetCurrentPoint(_canvas).Position); // doc space (identity unless paged)
        if (TableDrawPointerPressed(ptp, e)) return;  // "draw table" mode: drag from the caret to size it
        if (TryBeginColumnResize(ptp, e)) return;     // table column boundary drag
        if (TryBeginRowResize(ptp, e)) return;        // table row boundary drag
        if (TrySelectTableBlock(ptp)) return;         // table left/top border -> select whole table
        if (TrySelectInlineTable(ptp)) return;        // inline table left/top border -> select it
        if (TryBeginImageInteraction(ptp, e)) return; // image resize handle or selection
        ClearObjectSelection(); // any other press clears an image selection
        var tp = GetPositionFromPoint(ptp);
        if (tp == null) return;

        // Ctrl+click on a hyperlink opens it (Word/browser convention); the caret still moves there.
        if (Ctrl && !Shift && tp.Paragraph != null)
        {
            _caret = tp;
            CollapseSelectionToCaret();
            if (CurrentLinkUri() is { Length: > 0 })
            {
                RestartBlink();
                SyncIme();
                InvalidateCanvas();
                RaiseStatusChanged();
                _ = OpenLinkAtCaretAsync();
                return;
            }
        }

        // Read-only viewer: a PLAIN click on a link opens it (no Ctrl needed — browser convention).
        // Armed here, launched on release only when no drag-selection happened in between.
        if (IsReadOnly && !Shift && tp.Paragraph != null
            && RunAtOffset(tp.Paragraph, tp.Offset > 0 ? tp.Offset - 1 : 0)?.NavigateUri is { Length: > 0 } roUri)
            _pressLink = (roUri, new Point(ptp.X, ptp.Y));

        var now = DateTime.UtcNow;
        bool repeat = (now - _lastPressTime).TotalMilliseconds < MultiClickMs
            && Math.Abs(ptp.X - _lastPressPos.X) + Math.Abs(ptp.Y - _lastPressPos.Y) < MultiClickSlop;
        _clickCount = repeat ? _clickCount + 1 : 1;
        _lastPressTime = now;
        _lastPressPos = new Point(ptp.X, ptp.Y);

        // A single (non-repeat) press strictly inside the existing selection arms text drag & drop —
        // caret/selection stay put; release decides click vs move/copy (RichEditor.DragText.cs).
        // Multi-clicks fall through so a fast triple-click still selects the paragraph.
        if (_clickCount == 1 && !Shift && ArmTextDragAt(tp, new Point(ptp.X, ptp.Y), e)) return;

        _caret = tp;
        _desiredCaretX = ptp.X;
        _coalesceKey = null; // click starts a fresh undo group for subsequent typing
        _pendingCaretStyles = null;
        _canvas.CapturePointer(e.Pointer);

        // Double-click selects the word under the caret; triple-click (or more) selects the paragraph.
        // No drag-select in these modes — keep the word/paragraph selection intact.
        if (_clickCount >= 2 && !Shift && tp.Paragraph != null)
        {
            if (_clickCount == 2)
            {
                var (ws, we) = WordBoundsAt(BuildPlain(tp.Paragraph), tp.Offset);
                _selStart = new TextPointer(tp.Paragraph, ws);
                _selEnd = new TextPointer(tp.Paragraph, we);
                _caret = new TextPointer(tp.Paragraph, we);
            }
            else
            {
                int len = GetParagraphLength(tp.Paragraph);
                _selStart = new TextPointer(tp.Paragraph, 0);
                _selEnd = new TextPointer(tp.Paragraph, len);
                _caret = new TextPointer(tp.Paragraph, len);
            }
            _isSelecting = false;
            RestartBlink();
            SyncIme();
            InvalidateCanvas();
            RaiseStatusChanged();
            return;
        }

        if (Shift) { _selEnd = Clone(tp); }
        else { _selStart = Clone(tp); _selEnd = Clone(tp); }
        _isSelecting = true;
        RestartBlink();
        SyncIme();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    private void OnCanvasPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        var vpos = e.GetCurrentPoint(_canvas).Position;   // canvas (physical) coords
        var pt = ViewToDoc(vpos);                         // doc space (identity unless paged)
        if (TableDrawPointerMoved(pt)) return;        // "draw table" mode: extend the rubber-band
        if (_resizingColumn) { ResizeColumn(pt); return; }
        if (_resizingRow) { ResizeRow(pt); return; }
        if (_resizingImage != null || _resizingInline != null) { TryResizeImage(pt); return; }
        if (_dragTextArmed) { DragTextMoved(pt); return; }
        UpdateHoverCursor(pt);
        if (!_isSelecting) return;
        var tp = GetPositionFromPoint(pt);
        if (tp == null) return;
        _caret = tp;
        _selEnd = Clone(tp);
        UpdateAutoScroll(vpos); // drag past the viewport edge keeps scrolling + extending
        InvalidateCanvas();
    }

    // ---- drag-selection auto-scroll ----------------------------------------
    // While drag-selecting past the viewport's top/bottom edge, scroll repeatedly toward the pointer
    // and keep extending the selection — without this, a selection couldn't grow beyond the visible
    // area (the pointer stops producing new positions once it leaves the viewport band).
    private DispatcherTimer? _autoScroll;
    private double _autoScrollVel;      // signed px per tick, canvas (physical) units
    private Point _lastPointerCanvas;   // last pointer position in canvas coords

    private void UpdateAutoScroll(Point canvasPos)
    {
        _lastPointerCanvas = canvasPos;
        double viewTop = _scroll.VerticalOffset, viewBottom = viewTop + _scroll.ViewportHeight;
        const double band = 24; // edge band that triggers scrolling; speed grows with distance
        double vel = 0;
        if (canvasPos.Y < viewTop + band) vel = -Math.Max(4, (viewTop + band - canvasPos.Y) * 0.5);
        else if (canvasPos.Y > viewBottom - band) vel = Math.Max(4, (canvasPos.Y - (viewBottom - band)) * 0.5);
        _autoScrollVel = vel;
        if (vel != 0)
        {
            if (_autoScroll == null)
            {
                _autoScroll = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _autoScroll.Tick += OnAutoScrollTick;
            }
            _autoScroll.Start();
        }
        else _autoScroll?.Stop();
    }

    private void OnAutoScrollTick(object? sender, object e)
    {
        if (!_isSelecting || _autoScrollVel == 0) { _autoScroll?.Stop(); return; }
        double cur = _scroll.VerticalOffset;
        double target = Math.Clamp(cur + _autoScrollVel, 0, Math.Max(0, _scroll.ScrollableHeight));
        if (Math.Abs(target - cur) < 0.5) { _autoScroll?.Stop(); return; } // reached the end
        _scroll.ChangeView(null, target, null, disableAnimation: true);
        // The pointer is stationary on screen while the canvas scrolls under it, so its CANVAS position
        // advances by the scrolled delta each tick; track that and re-hit to extend the selection.
        _lastPointerCanvas = new Point(_lastPointerCanvas.X, _lastPointerCanvas.Y + (target - cur));
        var tp = GetPositionFromPoint(ViewToDoc(_lastPointerCanvas));
        if (tp != null) { _caret = tp; _selEnd = Clone(tp); InvalidateCanvas(); }
    }

    private void StopAutoScroll() { _autoScroll?.Stop(); _autoScrollVel = 0; }

    // Shows a diagonal resize cursor over the selected image's corner handle, an I-beam elsewhere.
    // ProtectedCursor is set on the editor (this control); it resolves up the tree for the inner canvas.
    private InputSystemCursorShape _cursorShape = InputSystemCursorShape.IBeam;

    private void UpdateHoverCursor(Point pt)
    {
        // Hand cursor over a hyperlink: always in a read-only viewer (plain click opens — browser
        // convention); with Ctrl held when editable (Ctrl+click opens — Word convention).
        if ((Ctrl || IsReadOnly) && LinkAtPoint(pt)) { SetCursorShape(InputSystemCursorShape.Hand); return; }
        if (OverColumnBoundary(pt)) { SetCursorShape(InputSystemCursorShape.SizeWestEast); return; }
        if (OverRowBoundary(pt)) { SetCursorShape(InputSystemCursorShape.SizeNorthSouth); return; }
        if (OnTableSelectBorder(pt, out _) || OverInlineTableBorder(pt)) { SetCursorShape(InputSystemCursorShape.SizeAll); return; }
        bool onHandle = false;
        if (_selectedBlock is ImageBlock selB)
            foreach (var (img, rect) in BlockImageRects())
                if (ReferenceEquals(img, selB) && OnResizeHandle(rect, pt)) { onHandle = true; break; }
        if (!onHandle && _selectedInline is { } selI
            && _inlineImageRects.TryGetValue(selI.img, out var ir) && OnResizeHandle(ir.rect, pt)) onHandle = true;
        SetCursorShape(onHandle ? InputSystemCursorShape.SizeNorthwestSoutheast : InputSystemCursorShape.IBeam);
    }

    // Whether the document position under the point sits on a hyperlinked run.
    private bool LinkAtPoint(Point pt)
        => GetPositionFromPoint(pt) is { Paragraph: { } p } tp
           && RunAtOffset(p, tp.Offset > 0 ? tp.Offset - 1 : 0)?.NavigateUri is { Length: > 0 };

    private void SetCursorShape(InputSystemCursorShape shape)
    {
        if (shape == _cursorShape) return; // avoid re-creating the cursor every pointer move
        _cursorShape = shape;
        ProtectedCursor = InputSystemCursor.Create(shape);
    }

    // Ctrl+wheel zoom (Word/browser convention): ~10% per notch, clamped by SetZoom, which also leaves
    // fit-to-width mode. Handled so the ScrollViewer doesn't scroll instead; a plain wheel falls through.
    private void OnCanvasPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (!Ctrl) return;
        int delta = e.GetCurrentPoint(_canvas).Properties.MouseWheelDelta;
        if (delta == 0) return;
        SetZoom(EffectiveZoom * Math.Pow(1.1, delta / 120.0));
        e.Handled = true;
    }

    private void OnCanvasPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (TableDrawPointerReleased(e)) return;      // "draw table" mode: insert at the caret, dragged size
        if (EndColumnResize(e)) return;
        if (EndRowResize(e)) return;
        if (EndImageResize(e)) return;
        if (_dragTextArmed) { EndTextDrag(e); return; }
        _isSelecting = false;
        StopAutoScroll();
        _canvas.ReleasePointerCapture(e.Pointer);
        if (IsFormatPainterActive) ApplyFormatPainterToSelection();
        // Read-only link click: launch only if the press stayed a CLICK (no selection was dragged out
        // and the pointer didn't travel) — a drag over link text selects, exactly like a browser.
        if (_pressLink is { } pl)
        {
            _pressLink = null;
            var rp = ViewToDoc(e.GetCurrentPoint(_canvas).Position);
            if (!HasSelection && Math.Abs(rp.X - pl.pos.X) + Math.Abs(rp.Y - pl.pos.Y) < MultiClickSlop)
                _ = OpenLinkAtCaretAsync();
        }
        RaiseStatusChanged();
    }

    private static TextPointer Clone(TextPointer t) => new(t.Paragraph, t.Offset);

    // Maps a document-space point to the nearest caret position (descends into table cells). Positions
    // come from the block layout map: a binary search lands on the candidate block, and the nearest-
    // paragraph fallback scans outward from it with a sorted-tops early exit — so a click/drag costs
    // O(log blocks), not a whole-document walk per pointer event.
    private TextPointer? GetPositionFromPoint(Point point)
    {
        if (Document == null) return null;
        double width = _layoutWidth;
        const double listIndent = 10;
        var map = EnsureBlockLayout(width);
        int at = BlockIndexAtY(map, point.Y);

        // Direct hit on the candidate block (the first whose bottom reaches point.Y).
        if (at < map.Count)
        {
            var (block, y, h, _) = map[at];
            if (point.Y >= y && point.Y <= y + h)
            {
                if (block is Paragraph p)
                {
                    double px = ParaLeft(p);
                    double pWidth = Math.Max(10, width - 20 - px - p.MarginRight);
                    var layout = BuildTextLayout(p, pWidth); // hit paragraph: now the layout is needed
                    if (HitInlineTable(p, layout, px, y, point) is { } itp) return itp;
                    int off = HitOffset(layout, p, point.X - px, point.Y - y, out bool ate);
                    return new TextPointer(p, off) { AtLineEnd = ate };
                }
                if (block is TableBlock tb)
                {
                    var tl = LayoutTable(tb, listIndent + tb.Indent, y);
                    foreach (var (r, c, rect) in tl.AnchorRects)
                        if (rect.Contains(point))
                        {
                            var cell = tb.Cells[r][c];
                            var tp = HitTestBlockList(cell.Blocks, rect.X + CellPad,
                                rect.Y + CellPad + CellContentOffsetY(cell, rect),
                                Math.Max(10, rect.Width - 2 * CellPad), point);
                            if (tp != null) return tp;
                        }
                }
            }
        }

        // Nearest paragraph by vertical distance (a point in an inter-paragraph margin / on a non-text
        // block snaps to the closest line). Tops are sorted, so scan outward and stop once the gap
        // already exceeds the best distance.
        Paragraph? nearPara = null;
        double nearDist = double.MaxValue, nearY = 0, nearH = 0;
        for (int i = Math.Min(at, map.Count - 1); i >= 0; i--)
        {
            var (block, y, h, _) = map[i];
            if (nearPara != null && point.Y - (y + h) > nearDist) break;
            if (block is not Paragraph q) continue;
            double d = point.Y < y ? y - point.Y : (point.Y > y + h ? point.Y - (y + h) : 0);
            if (d < nearDist) { nearDist = d; nearPara = q; nearY = y; nearH = h; }
        }
        for (int i = at + 1; i < map.Count; i++)
        {
            var (block, y, h, _) = map[i];
            if (nearPara != null && y - point.Y > nearDist) break;
            if (block is not Paragraph q) continue;
            double d = point.Y < y ? y - point.Y : (point.Y > y + h ? point.Y - (y + h) : 0);
            if (d < nearDist) { nearDist = d; nearPara = q; nearY = y; nearH = h; }
        }
        if (nearPara != null)
        {
            double px = ParaLeft(nearPara);
            double pWidth = Math.Max(10, width - 20 - px - nearPara.MarginRight);
            var nearLayout = BuildTextLayout(nearPara, pWidth); // built lazily, only for the winner
            double ly = Math.Clamp(point.Y - nearY, 0, nearH); // clamp into the paragraph -> top/bottom line
            int noff = HitOffset(nearLayout, nearPara, point.X - px, ly, out bool nate);
            return new TextPointer(nearPara, noff) { AtLineEnd = nate };
        }
        return new TextPointer(FirstParagraph(), 0);
    }

    // Hit-tests a cell's block list (mirrors DrawCellBlockList's advance), returning a caret position
    // in the nearest paragraph. Recurses into nested tables. Returns the closest paragraph even if the
    // point lands between blocks, so a click anywhere in a cell places the caret.
    private TextPointer? HitTestBlockList(System.Collections.Generic.IList<Block> blocks, double ox, double oy, double innerW, Point point)
    {
        double by = 0;
        Paragraph? last = null;
        foreach (var b in blocks)
        {
            double blkY = oy + by;
            switch (b)
            {
                case Paragraph para:
                {
                    // Same list/indent gutter the draw walk applies (CellParaLeft), or hit-testing
                    // lands on the wrong character for every bulleted paragraph in a cell.
                    double pl = CellParaLeft(para);
                    double px = ox + pl;
                    double pw = Math.Max(10, innerW - pl);
                    double h = ParagraphHeight(para, pw); // cheap; layout built only on a hit
                    if (point.Y >= blkY && point.Y <= blkY + h)
                    {
                        var layout = BuildTextLayout(para, pw);
                        if (HitInlineTable(para, layout, px, blkY, point) is { } itp) return itp;
                        int off = HitOffset(layout, para, point.X - px, point.Y - blkY, out bool ate);
                        return new TextPointer(para, off) { AtLineEnd = ate };
                    }
                    last = para; by += h;
                    break;
                }
                case ImageBlock cimg: by += CellImageSize(cimg, innerW).h; break;
                case DividerBlock: by += DividerHeight; break;
                case TableBlock nt:
                {
                    var tl = LayoutTable(nt, ox, blkY);
                    if (point.Y >= blkY && point.Y <= blkY + tl.TotalHeight)
                        foreach (var (r, c, rect) in tl.AnchorRects)
                            if (rect.Contains(point))
                            {
                                var cell = nt.Cells[r][c];
                                var tp = HitTestBlockList(cell.Blocks, rect.X + CellPad,
                                    rect.Y + CellPad + CellContentOffsetY(cell, rect),
                                    Math.Max(10, rect.Width - 2 * CellPad), point);
                                if (tp != null) return tp;
                            }
                    by += tl.TotalHeight;
                    break;
                }
            }
        }
        return last != null ? new TextPointer(last, GetParagraphLength(last)) : null;
    }

    // Descends into an inline table embedded in paragraph p when the point lands on it, returning a
    // caret position inside the hit cell (so inline tables are clickable/editable like block tables).
    // When set, hit-testing treats inline tables as opaque (the U+FFFC char) instead of descending into
    // their cells — used by vertical caret movement so Up/Down past a table lands on the host text.
    private bool _suppressInlineTableHit;

    private TextPointer? HitInlineTable(Paragraph p, CanvasTextLayout layout, double px, double oy, Point point)
    {
        if (_suppressInlineTableHit) return null;
        int off = 0;
        foreach (var inl in p.Inlines)
        {
            if (inl is InlineTable it)
            {
                try
                {
                    var regions = layout.GetCharacterRegions(off, 1);
                    if (regions.Length > 0)
                    {
                        var lb = regions[0].LayoutBounds;
                        double tx = px + lb.X, ty = oy + lb.Y;
                        var tl = LayoutTable(it.Table, tx, ty);
                        if (point.X >= tx && point.X <= tx + tl.TableWidth && point.Y >= ty && point.Y <= ty + tl.TotalHeight)
                            foreach (var (r, c, rect) in tl.AnchorRects)
                                if (rect.Contains(point))
                                {
                                    var cell = it.Table.Cells[r][c];
                                    var tp = HitTestBlockList(cell.Blocks, rect.X + CellPad,
                                        rect.Y + CellPad + CellContentOffsetY(cell, rect),
                                        Math.Max(10, rect.Width - 2 * CellPad), point);
                                    if (tp != null) return tp;
                                }
                    }
                }
                catch { }
            }
            off += InlineLen(inl);
        }
        return null;
    }

    private int HitOffset(CanvasTextLayout layout, Paragraph p, double localX, double localY)
        => HitOffset(layout, p, localX, localY, out _);

    private int HitOffset(CanvasTextLayout layout, Paragraph p, double localX, double localY, out bool atLineEnd)
    {
        atLineEnd = false;
        int len = GetParagraphLength(p);
        if (len == 0) return 0;
        layout.HitTest((float)Math.Max(0, localX), (float)Math.Max(0, localY), out CanvasTextLayoutRegion region);
        int idx = region.CharacterIndex;
        // Trailing half of the glyph -> caret after it. That position may coincide with the next visual
        // line's start (soft-wrap boundary); the affinity flag keeps the caret drawn on THIS line's end.
        if (region.CharacterCount > 0 && localX > region.LayoutBounds.X + region.LayoutBounds.Width / 2)
        {
            idx += region.CharacterCount;
            atLineEnd = true;
        }
        return Math.Clamp(idx, 0, len);
    }

    // ---- keyboard ---------------------------------------------------------
    private void OnEditorCharacterReceived(UIElement sender, CharacterReceivedRoutedEventArgs e)
    {
        // While a CoreTextEditContext composition is active, TextUpdating owns the text; otherwise (incl.
        // plain English and IME-committed characters in WinUI 3 desktop) characters arrive here.
        if (_composing) return;
        if (IsReadOnly || _caret.Paragraph == null) return;
        char ch = e.Character;
        if (ch < ' ' && ch != '\t') return; // control chars handled in KeyDown
        ClearObjectSelection(); // typing dismisses an image selection
        InsertText(ch.ToString());
        e.Handled = true;
    }

    // ---- drag & drop ------------------------------------------------------
    private void OnEditorDragOver(object sender, DragEventArgs e)
    {
        bool ok = !IsReadOnly && AllowImages
            && e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems);
        e.AcceptedOperation = ok
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;
    }

    private async void OnEditorDrop(object sender, DragEventArgs e)
    {
        if (IsReadOnly || !AllowImages
            || !e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems)) return;
        // Insert at the DROP POINT, not wherever the caret happened to be before the drag.
        if (GetPositionFromPoint(ViewToDoc(e.GetPosition(_canvas))) is { Paragraph: not null } dropTp)
        {
            _caret = dropTp;
            CollapseSelectionToCaret();
        }
        var deferral = e.GetDeferral();
        try
        {
            foreach (var item in await e.DataView.GetStorageItemsAsync())
            {
                if (item is not Windows.Storage.StorageFile file) continue;
                try
                {
                    var buffer = await Windows.Storage.FileIO.ReadBufferAsync(file);
                    var bytes = new byte[buffer.Length];
                    using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
                    // Only insert recognized image formats (a non-image file otherwise becomes a broken
                    // 200×200 box). GetPixelSize returns (0,0) for unrecognized headers.
                    var (iw, _) = ImageInfo.GetPixelSize(bytes);
                    if (iw > 0) InsertImageBlock(bytes);
                }
                catch { }
            }
        }
        catch { }
        finally { deferral.Complete(); }
    }

    private void OnEditorKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_composing) return; // let the IME consume keys while a composition is active
        if (e.Key == VirtualKey.Escape && _pendingTableDraw != null) { CancelTableDraw(); e.Handled = true; return; }
        bool shift = Shift, ctrl = Ctrl, alt = Alt;

        // A selected image (block or inline): Delete/Backspace removes it; any other (non-modifier) key clears it.
        if (HasBlockSelection)
        {
            if (e.Key is VirtualKey.Delete or VirtualKey.Back)
            {
                if (!IsReadOnly) DeleteSelectedObject();
                e.Handled = true;
                return;
            }
            // Copy/Cut act on the object itself — must run before the "clear on any other key" below, which
            // would otherwise deselect the object before the clipboard sees it.
            if (ctrl && e.Key == VirtualKey.C) { _ = CopyAsync(); e.Handled = true; return; }
            if (ctrl && e.Key == VirtualKey.X) { if (!IsReadOnly) _ = CutAsync(); e.Handled = true; return; }
            // Block caret: an arrow steps out of the selected top-level object to the adjacent paragraph.
            if (_selectedBlock is { } selBlk
                && e.Key is VirtualKey.Left or VirtualKey.Right or VirtualKey.Up or VirtualKey.Down)
            {
                ExitBlockSelection(selBlk, forward: e.Key is VirtualKey.Right or VirtualKey.Down);
                e.Handled = true;
                return;
            }
            if (e.Key is not (VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu)) { ClearObjectSelection(); InvalidateCanvas(); }
        }

        if (_caret.Paragraph == null) return;

        // Ctrl + arrows/Home/End/Back/Delete: word- and document-level navigation/deletion (handled before
        // the plain-key switch so the modified forms don't fall through to single-character movement).
        if (ctrl)
        {
            switch (e.Key)
            {
                case VirtualKey.Home: GoToDocEdge(true, shift); e.Handled = true; return;
                case VirtualKey.End: GoToDocEdge(false, shift); e.Handled = true; return;
                case VirtualKey.Left: WordMove(false, shift); e.Handled = true; return;
                case VirtualKey.Right: WordMove(true, shift); e.Handled = true; return;
                case VirtualKey.Back: if (!IsReadOnly) { WordDelete(false); e.Handled = true; } return;
                case VirtualKey.Delete: if (!IsReadOnly) { WordDelete(true); e.Handled = true; } return;
                case VirtualKey.Number0:
                case (VirtualKey)0x60: // NumberPad0
                    FitToWidth(); e.Handled = true; return;
            }
        }

        // Command shortcuts (Word-standard) via the central table — a single source shared with the context
        // menu and toolbar hints. All command shortcuts are Ctrl-modified, so this runs before the plain-key
        // navigation switch. Ctrl+nav (word/doc movement) above already returned.
        if (ctrl && RichEditorShortcuts.TryMatch(ctrl, shift, alt, e.Key, out var sid))
        {
            RunShortcut(sid);
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case VirtualKey.Left: MoveCaretLeft(shift); e.Handled = true; break;
            case VirtualKey.Right: MoveCaretRight(shift); e.Handled = true; break;
            case VirtualKey.Up: MoveCaretVertical(false, shift); e.Handled = true; break;
            case VirtualKey.Down: MoveCaretVertical(true, shift); e.Handled = true; break;
            case VirtualKey.Home: MoveToLineEdge(false, shift); e.Handled = true; break;
            case VirtualKey.End: MoveToLineEdge(true, shift); e.Handled = true; break;
            case VirtualKey.PageUp: MovePage(false, shift); e.Handled = true; break;
            case VirtualKey.PageDown: MovePage(true, shift); e.Handled = true; break;
            case VirtualKey.Back: if (!IsReadOnly) { Backspace(); e.Handled = true; } break;
            case VirtualKey.Delete: if (!IsReadOnly) { DeleteForward(); e.Handled = true; } break;
            case VirtualKey.Enter: if (!IsReadOnly) { InsertParagraphBreak(shift); e.Handled = true; } break;
            case VirtualKey.Tab: if (!IsReadOnly) { HandleTab(shift); e.Handled = true; } break;
            case VirtualKey.F3: if (AllowFindReplace && FindAgain(shift)) e.Handled = true; break;
            default: break;
        }
    }

    // Runs a matched command shortcut. Copy / Select All work read-only; everything else is editing and is
    // gated by IsReadOnly. Object-selection Copy/Cut is handled earlier in OnEditorKeyDown.
    private void RunShortcut(ShortcutId id)
    {
        switch (id)
        {
            case ShortcutId.Copy: _ = CopyAsync(); return;
            case ShortcutId.SelectAll: SelectAll(); return;
            case ShortcutId.Find: if (AllowFindReplace) RaiseFindRequested(false); return; // works read-only
        }
        if (IsReadOnly) return;
        switch (id)
        {
            case ShortcutId.FindReplace: if (AllowFindReplace) RaiseFindRequested(true); break;
            case ShortcutId.InsertLink: _ = EditHyperlinkAsync(); break;
            case ShortcutId.Cut: _ = CutAsync(); break;
            case ShortcutId.Paste: _ = PasteAsync(false); break;
            case ShortcutId.PastePlain: _ = PasteAsync(true); break;
            case ShortcutId.Undo: Undo(); break;
            case ShortcutId.Redo: Redo(); break;
            case ShortcutId.Bold: ToggleBold(); break;
            case ShortcutId.Italic: ToggleItalic(); break;
            case ShortcutId.Underline: ToggleUnderline(); break;
            case ShortcutId.Strikethrough: ToggleStrikethrough(); break;
            case ShortcutId.FontLarger: IncreaseFontSize(); break;
            case ShortcutId.FontSmaller: DecreaseFontSize(); break;
            case ShortcutId.IndentIncrease: Indent(20); break;
            case ShortcutId.IndentDecrease: Indent(-20); break;
            case ShortcutId.AlignLeft: SetTextAlignment(Microsoft.UI.Xaml.TextAlignment.Left); break;
            case ShortcutId.AlignCenter: SetTextAlignment(Microsoft.UI.Xaml.TextAlignment.Center); break;
            case ShortcutId.AlignRight: SetTextAlignment(Microsoft.UI.Xaml.TextAlignment.Right); break;
            case ShortcutId.AlignJustify: SetTextAlignment(Microsoft.UI.Xaml.TextAlignment.Justify); break;
            case ShortcutId.Heading1: SetHeading(1); break;
            case ShortcutId.Heading2: SetHeading(2); break;
            case ShortcutId.Heading3: SetHeading(3); break;
            case ShortcutId.Heading4: SetHeading(4); break;
            case ShortcutId.Heading5: SetHeading(5); break;
            case ShortcutId.Heading6: SetHeading(6); break;
            case ShortcutId.BodyText: SetHeading(0); break;
            case ShortcutId.BulletList: ToggleBullet(); break;
            case ShortcutId.NumberedList: ToggleNumbering(); break;
            case ShortcutId.LineSpacingSingle: SetLineSpacing(1.0); break;
            case ShortcutId.LineSpacingOneHalf: SetLineSpacing(1.5); break;
            case ShortcutId.LineSpacingDouble: SetLineSpacing(2.0); break;
        }
    }

    // ---- editing core -----------------------------------------------------
    /// <summary>Inserts plain text at the caret, replacing any selection.</summary>
    public void InsertText(string text)
    {
        if (Document == null || IsReadOnly || _caret.Paragraph == null) return;
        if (text.Contains('\r')) text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        PushUndo(text.Length == 1 && !HasSelection && text != "\n" ? "type" : null);
        if (HasSelection) DeleteSelection();
        int preCaret = _caret.Offset;
        var para = _caret.Paragraph!;
        TryInsertTextCore(para, text, preCaret);
        _caret = new TextPointer(para, preCaret + text.Length);
        ApplyPendingStyles(para, preCaret, text.Length);
        // A typed whitespace completes the token before it — auto-link it if it's a URL.
        if (AutoLinkOnType && text.Length == 1 && (text[0] == ' ' || text[0] == '\t'))
            TryAutoLink(para, preCaret);
        CollapseSelectionToCaret();
        AfterEdit();
    }

    private void TryInsertTextCore(Paragraph p, string text, int localIndex)
    {
        int pos = 0;
        for (int i = 0; i < p.Inlines.Count; i++)
        {
            int len = InlineLen(p.Inlines[i]);
            if (p.Inlines[i] is Run run && localIndex >= pos && localIndex <= pos + len)
            {
                run.Text = (run.Text ?? "").Insert(localIndex - pos, text);
                return;
            }
            if (p.Inlines[i] is not Run && localIndex == pos)
            {
                p.Inlines.Insert(i, new Run { Text = text, Parent = p });
                return;
            }
            pos += len;
        }
        p.Inlines.Add(new Run { Text = text, Parent = p });
    }

    private void DeleteSelection()
    {
        if (!HasSelection) return;
        // A rectangular table-cell block selection: clear the content of every selected cell (keep the
        // grid). The linear TextRange.Delete below would only partially clear the first/last cell and,
        // walking cells row-major, would erase cells outside the selected column band — not the highlight.
        if (CellBlockSelection() is { } cb) { ClearCellRange(cb.tb, cb.r0, cb.c0, cb.r1, cb.c1); return; }
        var range = new TextRange(_selStart, _selEnd);
        range.Delete();
        _caret = new TextPointer(range.Start.Paragraph, range.Start.Offset);
        if (Document != null) UpdateParents(Document);
        CollapseSelectionToCaret();
    }

    // Resets every logical cell whose anchor is inside the rectangle [r0..r1]×[c0..c1] — the same cells the
    // renderer tints — to a single empty paragraph, and lands the caret in the first cleared cell. Matches
    // the visual cell-block selection (unlike the linear TextRange.Delete).
    private void ClearCellRange(TableBlock tb, int r0, int c0, int r1, int c1)
    {
        Paragraph? caretPara = null;
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            if (r < r0 || r > r1 || c < c0 || c > c1) continue;
            var empty = new Paragraph { Inlines = { new Run { Text = "" } } };
            cell.Blocks.Clear();
            cell.Blocks.Add(empty);
            caretPara ??= empty;
        }
        if (Document != null) UpdateParents(Document);
        if (caretPara != null) _caret = new TextPointer(caretPara, 0);
        CollapseSelectionToCaret();
    }

    // PushUndo runs only once an edit is certain — a no-op keypress (Backspace at the document start,
    // Delete at the end) must not leave an empty undo step or fire a phantom TextChanged.
    private void Backspace()
    {
        if (HasSelection) { PushUndo(null); DeleteSelection(); AfterEdit(); return; }
        var p = _caret.Paragraph!;
        if (_caret.Offset > 0)
        {
            PushUndo("del");
            DeleteLocalText(p, _caret.Offset - 1, 1);
            _caret = new TextPointer(p, _caret.Offset - 1);
            CollapseSelectionToCaret();
            AfterEdit();
        }
        else
        {
            MergeWithPrevious();
        }
    }

    private void DeleteForward()
    {
        if (HasSelection) { PushUndo(null); DeleteSelection(); AfterEdit(); return; }
        var p = _caret.Paragraph!;
        int len = GetParagraphLength(p);
        if (_caret.Offset < len)
        {
            PushUndo("del");
            DeleteLocalText(p, _caret.Offset, 1);
            CollapseSelectionToCaret();
            AfterEdit();
        }
        else
        {
            MergeWithNext();
        }
    }

    // Deletes [index, index+length) from a paragraph, spanning runs; collapses to text via TextRange.
    private void DeleteLocalText(Paragraph p, int index, int length)
    {
        if (length <= 0) return;
        new TextRange(new TextPointer(p, index), new TextPointer(p, index + length)).Delete();
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "", Parent = p });
    }

    // The block container that can merge/split paragraph siblings for p: Document.Blocks for a top-level
    // paragraph, the cell's block list for a cell paragraph (rule #3: containers generalize). The container
    // boundary is also the merge boundary — cell paragraphs never merge across cells. Look at the actual
    // adjacent block in the container — NOT AllParagraphs, whose flattened order would pick a preceding
    // table's last cell as "previous".
    private static IList<Block>? MergeContainerOf(Paragraph p) => p.Parent switch
    {
        FlowDocument d => d.Blocks,
        TableCell tc => tc.Blocks,
        _ => null
    };

    private void MergeWithPrevious()
    {
        if (Document == null) return;
        var cur = _caret.Paragraph!;
        if (MergeContainerOf(cur) is not { } container) return;
        int idx = container.IndexOf(cur);
        if (idx <= 0 || container[idx - 1] is not Paragraph prev) return; // prev block is an image/table — no merge
        PushUndo("del"); // after validation; keeps coalescing with the char-delete run
        int joinAt = GetParagraphLength(prev);
        foreach (var inl in cur.Inlines.ToList()) { inl.Parent = prev; prev.Inlines.Add(inl); }
        container.Remove(cur);
        TextRange.CoalesceRuns(prev);
        _caret = new TextPointer(prev, joinAt);
        UpdateParents(Document);
        CollapseSelectionToCaret();
        AfterEdit();
    }

    private void MergeWithNext()
    {
        if (Document == null) return;
        var cur = _caret.Paragraph!;
        if (MergeContainerOf(cur) is not { } container) return;
        int idx = container.IndexOf(cur);
        if (idx < 0 || idx + 1 >= container.Count || container[idx + 1] is not Paragraph next) return;
        PushUndo("del"); // after validation; keeps coalescing with the char-delete run
        int joinAt = GetParagraphLength(cur);
        foreach (var inl in next.Inlines.ToList()) { inl.Parent = cur; cur.Inlines.Add(inl); }
        container.Remove(next);
        TextRange.CoalesceRuns(cur);
        _caret = new TextPointer(cur, joinAt);
        UpdateParents(Document);
        CollapseSelectionToCaret();
        AfterEdit();
    }

    private void InsertParagraphBreak(bool soft)
    {
        if (Document == null || _caret.Paragraph == null) return;
        // A hard break needs a splittable container; validate BEFORE PushUndo so an impossible split
        // doesn't leave an empty undo step / a phantom modified flag (same rule as Backspace).
        if (!soft && !HasSelection && MergeContainerOf(_caret.Paragraph) == null) return;
        PushUndo(null);
        if (HasSelection) DeleteSelection();
        // Enter also completes the token before the caret (Word/HWP auto-link on line commit).
        if (AutoLinkOnType && _caret.Paragraph is { } alp) TryAutoLink(alp, _caret.Offset);
        if (soft)
        {
            TryInsertTextCore(_caret.Paragraph!, "\n", _caret.Offset);
            _caret = new TextPointer(_caret.Paragraph, _caret.Offset + 1);
            CollapseSelectionToCaret();
            AfterEdit();
            return;
        }
        SplitParagraphAtCaret();
        AfterEdit();
    }

    private void SplitParagraphAtCaret()
    {
        var p = _caret.Paragraph!;
        IList<Block>? container = p.Parent switch
        {
            FlowDocument d => d.Blocks,
            TableCell tc => tc.Blocks,
            _ => null
        };
        if (container == null) return;
        int idx = container.IndexOf(p);
        if (idx < 0) return;
        int insertAt = SplitInlinesAt(p, _caret.Offset);
        // Full paragraph-format inheritance (list marker style, line spacing, quote, margins…) — the
        // old hand-picked copy dropped ListMarker (a ◦/"a)" list changed glyph after Enter) and
        // LineSpacing (a 200% paragraph continued at 100%). Heading deliberately resets to body:
        // Enter in a heading starts normal text (Word's "following style" behavior, unchanged).
        var np = p.CloneFormat();
        np.HeadingLevel = 0;
        while (p.Inlines.Count > insertAt)
        {
            var inl = p.Inlines[insertAt];
            p.Inlines.RemoveAt(insertAt);
            inl.Parent = np;
            np.Inlines.Add(inl);
        }
        if (np.Inlines.Count == 0) np.Inlines.Add(new Run { Text = "" });
        if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
        np.Parent = p.Parent;
        container.Insert(idx + 1, np);
        _caret = new TextPointer(np, 0);
        CollapseSelectionToCaret();
        if (Document != null) UpdateParents(Document);
    }

    // Splits the run containing offset so offset lands on an inline boundary; returns that inline index.
    private int SplitInlinesAt(Paragraph p, int offset)
    {
        int pos = 0;
        for (int i = 0; i < p.Inlines.Count; i++)
        {
            int len = InlineLen(p.Inlines[i]);
            if (p.Inlines[i] is Run run)
            {
                if (offset > pos && offset < pos + len)
                {
                    int local = offset - pos;
                    var tail = (Run)run.Clone();
                    tail.Text = run.Text!.Substring(local);
                    tail.Parent = p;
                    run.Text = run.Text!.Substring(0, local);
                    p.Inlines.Insert(i + 1, tail);
                    return i + 1;
                }
                if (offset == pos) return i;
            }
            else if (offset == pos) return i;
            pos += len;
        }
        return p.Inlines.Count;
    }

    // Ctrl+A. In a table cell it selects in STAGES like HWP/Excel: the cell's content first, then the
    // whole (enclosing) table, climbing through any nesting, and finally the whole document; each press
    // advances one stage and the last stays put. Outside a table it selects the whole document at once.
    private void SelectAll()
    {
        if (_caret.Paragraph is { } cp && FindCell(cp) is { } loc)
        {
            var stages = new List<(Paragraph s, int so, Paragraph e, int eo)>();
            void AddRange(IEnumerable<Paragraph> paras)
            {
                Paragraph? first = null, last = null;
                foreach (var q in paras) { first ??= q; last = q; }
                if (first != null && last != null)
                    stages.Add((first, 0, last, GetParagraphLength(last)));
            }

            var (ar, ac) = loc.tb.AnchorOf(loc.r, loc.c);
            AddRange(ParagraphsInBlocks(loc.tb.Cells[ar][ac].Blocks)); // 1: the cell's content
            for (TableBlock? t = loc.tb; t != null; )                  // 2..: each enclosing table
            {
                AddRange(ParagraphsInBlocks(new[] { (Block)t }));
                t = t.Parent is TableCell tc && tc.Parent is TableBlock outer ? outer : null;
            }
            AddRange(AllParagraphs());                                 // last: the whole document

            bool Eq((Paragraph s, int so, Paragraph e, int eo) st)
            {
                TextPointer a = _selStart, b = _selEnd;
                if (ComparePositions(a, b) > 0) (a, b) = (b, a);
                return ReferenceEquals(a.Paragraph, st.s) && a.Offset == st.so
                    && ReferenceEquals(b.Paragraph, st.e) && b.Offset == st.eo;
            }

            // Advance from whichever stage currently matches (the outermost, if several coincide) to the
            // next; clamp at the last so repeated Ctrl+A settles on the document.
            int cur = -1;
            for (int i = 0; i < stages.Count; i++) if (Eq(stages[i])) cur = i;
            var pick = stages[Math.Min(cur + 1, stages.Count - 1)];
            _selStart = new TextPointer(pick.s, pick.so);
            _selEnd = new TextPointer(pick.e, pick.eo);
            _caret = Clone(_selEnd);
            InvalidateCanvas();
            RaiseStatusChanged();
            return;
        }

        var docParas = AllParagraphs();
        if (docParas.Count == 0) return;
        _selStart = new TextPointer(docParas[0], 0);
        _selEnd = new TextPointer(docParas[^1], GetParagraphLength(docParas[^1]));
        _caret = Clone(_selEnd);
        InvalidateCanvas();
        RaiseStatusChanged(); // flush SelectionChanged now, not with the next unrelated input
    }

    private void CollapseSelectionToCaret()
    {
        _selStart = Clone(_caret);
        _selEnd = Clone(_caret);
    }

    // ---- undo / redo ------------------------------------------------------
    // Snapshots the document before an edit. Edits sharing a non-null coalesce key (consecutive typing,
    // deleting, IME) collapse into one undo group; a null key always starts a new group.
    internal void PushUndo(string? coalesceKey = null)
    {
        if (Document == null) return;
        MarkTextChanged(); // a mutation is imminent; flushed as TextChanged from RaiseStatusChanged
        if (coalesceKey != null && coalesceKey == _coalesceKey) return;
        _undo.PushState(Document, _caret);
        _coalesceKey = coalesceKey;
    }

    /// <summary>Undoes the last edit group.</summary>
    public void Undo()
    {
        if (Document == null) return;
        var st = _undo.Undo(Document, _caret);
        if (st != null) ApplyHistoryState(st.Value);
    }

    /// <summary>Redoes the last undone edit group.</summary>
    public void Redo()
    {
        if (Document == null) return;
        var st = _undo.Redo(Document, _caret);
        if (st != null) ApplyHistoryState(st.Value);
    }

    private void ApplyHistoryState(UndoState st)
    {
        Document = st.Document; // fires OnDocumentChanged (wires parents, resets caret to first paragraph)
        var tp = _undo.GetPointerFromGlobalIndex(st.Document, st.CaretGlobalIndex);
        int off = Math.Clamp(st.CaretOffset, 0, GetParagraphLength(tp.Paragraph));
        _caret = new TextPointer(tp.Paragraph, off);
        CollapseSelectionToCaret();
        ClearObjectSelection();
        _coalesceKey = null;
        RelayoutToViewport();
        RestartBlink();
        SyncIme();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    // After a content edit: re-measure and repaint (the changed paragraph reshapes via ParagraphSig).
    private void AfterEdit()
    {
        InvalidateCaretTableMeasure();
        RelayoutToViewport();
        RestartBlink();
        SyncIme();
        ScrollCaretIntoView();
        CheckImageLimit();
        RaiseStatusChanged();
    }

    // Drops cached row heights for every table on the caret's ancestor chain, so editing a cell's
    // content re-measures the row (the per-table rowH cache is otherwise only cleared on document change).
    private void InvalidateCaretTableMeasure()
    {
        object? cur = _caret.Paragraph?.Parent;
        while (cur != null)
        {
            if (cur is TableBlock tb) { _tableRowHeights.Remove(tb); cur = tb.Parent; }
            else if (cur is TableCell tc) cur = tc.Parent;
            else break;
        }
    }

    // ---- caret navigation -------------------------------------------------
    // Caret paragraph order: like AllParagraphs but EXCLUDING inline-table cells — those are entered and
    // exited at the host paragraph's U+FFFC, not in the linear flow. (Block-table cells stay in the flow.)
    private List<Paragraph> CaretParagraphs()
        => Document == null ? new() : CaretParagraphsIn(Document.Blocks).ToList();

    private static IEnumerable<Paragraph> CaretParagraphsIn(IEnumerable<Block> blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p) yield return p; // do not descend into the paragraph's inline tables
            else if (block is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var q in CaretParagraphsIn(cell.Blocks)) yield return q;
        }
    }

    // Whether the paragraph hosts any inline table (its cells may contain the caret paragraph, so the
    // caret/hit walks must build this paragraph's layout to descend; plain paragraphs skip it).
    private static bool HasInlineTable(Paragraph p)
    {
        foreach (var inl in p.Inlines) if (inl is InlineTable) return true;
        return false;
    }

    // The inline table whose U+FFFC sits at `offset` in p (caret right before it), or null.
    private static InlineTable? InlineTableForwardOf(Paragraph p, int offset)
    {
        int pos = 0;
        foreach (var inl in p.Inlines)
        {
            if (inl is InlineTable it && offset == pos) return it;
            pos += InlineLen(inl);
        }
        return null;
    }

    // The inline table whose U+FFFC ends at `offset` in p (caret right after it), or null.
    private static InlineTable? InlineTableBackwardOf(Paragraph p, int offset)
    {
        int pos = 0;
        foreach (var inl in p.Inlines)
        {
            if (inl is InlineTable it && offset == pos + 1) return it;
            pos += InlineLen(inl);
        }
        return null;
    }

    // If p is a paragraph inside an inline-table cell, returns the table, its host paragraph, and the
    // host offset of the table's U+FFFC. Else null.
    private (InlineTable it, Paragraph host, int objOffset)? FindInlineTableHost(Paragraph p)
    {
        if (FindCell(p) is not { } loc || loc.tb.Parent is not InlineTable it || it.Parent is not Paragraph host) return null;
        int pos = 0;
        foreach (var inl in host.Inlines) { if (ReferenceEquals(inl, it)) return (it, host, pos); pos += InlineLen(inl); }
        return null;
    }

    // Caret-navigable paragraphs inside a table's cells, in logical order.
    private static List<Paragraph> TableCaretParas(TableBlock tb)
    {
        var list = new List<Paragraph>();
        foreach (var (_, _, cell) in tb.LogicalCells())
            foreach (var b in cell.Blocks)
                if (b is Paragraph cp) list.Add(cp);
        return list;
    }

    private void MoveCaretLeft(bool shift)
    {
        var p = _caret.Paragraph!;

        // Caret just after an inline table's U+FFFC -> step into the end of its last cell.
        if (InlineTableBackwardOf(p, _caret.Offset) is { } enter && TableCaretParas(enter.Table) is { Count: > 0 } cs)
        {
            var lc = cs[^1];
            _caret = new TextPointer(lc, GetParagraphLength(lc));
            UpdateDesiredX(); ApplyArrowSelection(shift); return;
        }

        if (_caret.Offset > 0) { _caret = new TextPointer(p, _caret.Offset - 1); UpdateDesiredX(); ApplyArrowSelection(shift); return; }

        // Paragraph start. Inside an inline-table cell: prev cell, or exit just before the table.
        if (FindInlineTableHost(p) is { } ctx)
        {
            var cells = TableCaretParas(ctx.it.Table);
            int ci = cells.IndexOf(p);
            if (ci > 0) { var prev = cells[ci - 1]; _caret = new TextPointer(prev, GetParagraphLength(prev)); }
            else _caret = new TextPointer(ctx.host, ctx.objOffset);
            UpdateDesiredX(); ApplyArrowSelection(shift); return;
        }

        // Block caret: the previous top-level block is an image — select it as an object instead of
        // skipping past (keyboard-only access to image selection; another arrow steps out, Delete removes).
        if (!shift && AdjacentTopLevelImage(p, forward: false) is { } prevImg) { SelectBlockObject(prevImg); return; }

        var paras = CaretParagraphs();
        int i = paras.IndexOf(p);
        if (i > 0) _caret = new TextPointer(paras[i - 1], GetParagraphLength(paras[i - 1]));
        UpdateDesiredX();
        ApplyArrowSelection(shift);
    }

    private void MoveCaretRight(bool shift)
    {
        var p = _caret.Paragraph!;
        int len = GetParagraphLength(p);

        // Caret just before an inline table's U+FFFC -> step into the start of its first cell.
        if (InlineTableForwardOf(p, _caret.Offset) is { } enter && TableCaretParas(enter.Table) is { Count: > 0 } cs)
        {
            _caret = new TextPointer(cs[0], 0);
            UpdateDesiredX(); ApplyArrowSelection(shift); return;
        }

        if (_caret.Offset < len) { _caret = new TextPointer(p, _caret.Offset + 1); UpdateDesiredX(); ApplyArrowSelection(shift); return; }

        // Paragraph end. Inside an inline-table cell: next cell, or exit just after the table.
        if (FindInlineTableHost(p) is { } ctx)
        {
            var cells = TableCaretParas(ctx.it.Table);
            int ci = cells.IndexOf(p);
            if (ci >= 0 && ci + 1 < cells.Count) _caret = new TextPointer(cells[ci + 1], 0);
            else _caret = new TextPointer(ctx.host, ctx.objOffset + 1);
            UpdateDesiredX(); ApplyArrowSelection(shift); return;
        }

        // Block caret: the next top-level block is an image — select it as an object (see MoveCaretLeft).
        if (!shift && AdjacentTopLevelImage(p, forward: true) is { } nextImg) { SelectBlockObject(nextImg); return; }

        var paras = CaretParagraphs();
        int i = paras.IndexOf(p);
        if (i >= 0 && i + 1 < paras.Count) _caret = new TextPointer(paras[i + 1], 0);
        UpdateDesiredX();
        ApplyArrowSelection(shift);
    }

    // ---- block caret (keyboard object selection) ----------------------------
    // The ImageBlock directly before/after the caret's TOP-LEVEL paragraph, or null. Tables keep their
    // richer cell-entry navigation; dividers keep the skip-over fallback — only images become a "block
    // caret" stop (they're otherwise unreachable without a mouse).
    private ImageBlock? AdjacentTopLevelImage(Paragraph p, bool forward)
        => AdjacentTopLevelBlock(p, forward) as ImageBlock;

    // The top-level block immediately before/after p, or null when p isn't top-level / has no neighbour.
    private Block? AdjacentTopLevelBlock(Paragraph p, bool forward)
    {
        if (Document == null || p.Parent is not FlowDocument) return null;
        int bi = Document.Blocks.IndexOf(p);
        if (bi < 0) return null;
        int at = forward ? bi + 1 : bi - 1;
        return at >= 0 && at < Document.Blocks.Count ? Document.Blocks[at] : null;
    }

    // The block immediately before/after p among its SIBLINGS — Document.Blocks for a top-level
    // paragraph, the cell's block list for a cell paragraph (the same container generalization as
    // MergeContainerOf). Works at any nesting depth, unlike AdjacentTopLevelBlock.
    private static Block? AdjacentSiblingBlock(Paragraph p, bool forward)
    {
        if (MergeContainerOf(p) is not { } container) return null;
        int bi = container.IndexOf(p);
        if (bi < 0) return null;
        int at = forward ? bi + 1 : bi - 1;
        return at >= 0 && at < container.Count ? container[at] : null;
    }

    // Enters a table from an adjacent sibling paragraph with ↑/↓, landing in the entry row's cell
    // nearest the desired column. The point-based path normally handles this, but its vertical nudge
    // lands in the INTER-BLOCK MARGIN when the paragraph's MarginBottom plus the table's MarginTop
    // exceed it — GetPositionFromPoint then rejects the table (it requires the point inside the block).
    // Both fallbacks that follow would skip the table: AdjacentTopLevelParagraph does so by design, and
    // VerticalInCell's next step jumps to the OUTER table's adjacent row.
    private TextPointer? EnterAdjacentTable(TableBlock tb, bool down)
    {
        if (Document == null || tb.Rows <= 0) return null;
        // Origin: recorded for every drawn table (nested included); the block map covers a top-level
        // table that hasn't been drawn yet.
        Point origin;
        if (_tableOrigins.TryGetValue(tb, out var org)) origin = org;
        else if (_blockLayoutIndex != null && _blockLayoutIndex.TryGetValue(tb, out int bi))
            origin = new Point(10 + tb.Indent, EnsureBlockLayout(_layoutWidth)[bi].top);
        else return null;

        var tl = LayoutTable(tb, origin.X, origin.Y);
        int entryRow = down ? 0 : tb.Rows - 1;

        // The anchor covering the entry row whose column band is nearest the desired X (a row-spanning
        // anchor starting further up still owns that row).
        Rect? bestRect = null;
        TableCell? bestCell = null;
        double bestDist = double.MaxValue;
        foreach (var (r, c, rect) in tl.AnchorRects)
        {
            var (_, rs) = tb.SpanOf(r, c);
            if (entryRow < r || entryRow >= r + Math.Max(1, rs)) continue;
            double d = _desiredCaretX < rect.X ? rect.X - _desiredCaretX
                     : _desiredCaretX > rect.Right ? _desiredCaretX - rect.Right : 0;
            if (d < bestDist) { bestDist = d; bestRect = rect; bestCell = tb.Cells[r][c]; }
        }
        if (bestRect is not { } cellRect || bestCell is not { } cell) return null;

        // Enter on the cell's near LINE at the remembered column: the FIRST paragraph's first line coming
        // down, the LAST paragraph's last line coming up. Point-hitting inside the cell instead is
        // unreliable — when the cell opens with a non-paragraph block (a nested table or an image)
        // HitTestBlockList matches nothing and falls back to the cell's LAST paragraph, dumping the caret
        // at the far end of the cell just entered. Resolving the paragraph ourselves and only then
        // applying _desiredCaretX keeps entry consistent with every other vertical move, which preserves
        // the column (that is why ApplyArrowSelection never rewrites _desiredCaretX).
        if ((down ? FirstCellPara(cell) : LastCellPara(cell)) is { } edgePara)
            return new TextPointer(edgePara, OffsetAtDesiredXInCell(edgePara, cellRect, firstLine: down));

        // The cell has no direct paragraph on that side (it starts/ends with a nested table): fall back
        // to a point hit, which descends into whatever is actually there.
        double loX = cellRect.X + CellPad + 1;
        double x = Math.Clamp(_desiredCaretX, loX, Math.Max(loX, cellRect.Right - CellPad - 1));
        double y = down ? cellRect.Y + CellPad + 2 : cellRect.Bottom - CellPad - 2;
        _suppressInlineTableHit = false;
        try { return GetPositionFromPoint(new Point(x, y)); }
        finally { _suppressInlineTableHit = false; }
    }

    private void SelectBlockObject(Block blk)
    {
        ClearObjectSelection();
        _selectedBlock = blk;
        _isSelecting = false;
        CollapseSelectionToCaret();
        RestartBlink();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    // An arrow while a block object is selected: deselect and land the caret on the adjacent paragraph
    // (its start when moving forward, its end when moving back). NormalizeBlocks guarantees a paragraph
    // borders every non-paragraph block, so the scan always finds one.
    private void ExitBlockSelection(Block blk, bool forward)
    {
        ClearObjectSelection();
        if (Document != null)
        {
            int bi = Document.Blocks.IndexOf(blk);
            if (bi >= 0)
                for (int i = forward ? bi + 1 : bi - 1; i >= 0 && i < Document.Blocks.Count; i += forward ? 1 : -1)
                    if (Document.Blocks[i] is Paragraph q)
                    {
                        _caret = new TextPointer(q, forward ? 0 : GetParagraphLength(q));
                        break;
                    }
        }
        CollapseSelectionToCaret();
        UpdateDesiredX();
        RestartBlink();
        SyncIme();
        ScrollCaretIntoView();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    private void MoveCaretVertical(bool down, bool shift)
    {
        // Inside a table cell the cell's padding (and the tall inline-table box) swallow the small caret
        // nudge, so pure hit-testing gets stuck in the same cell. Navigate cells logically first.
        if (_caret.Paragraph is { } curp && FindCell(curp) is { } loc && VerticalInCell(loc, _caret, down, shift)) return;

        // Navigate the current top-level paragraph's own visual lines first (precise, multi-line safe);
        // only fall through to a cross-paragraph hit when already on its top/bottom line.
        if (TryMoveCaretByLine(down)) { ApplyArrowSelection(shift); return; }

        // Leaving the paragraph into an adjacent sibling TABLE: enter it deterministically. This must
        // run BEFORE the point-based path below, not as its fallback — once the vertical probe lands
        // inside a cell, HitTestBlockList returns that cell's LAST paragraph (at its end) whenever the
        // point matches no block, which happens as soon as the cell opens with a nested table or an
        // image. That result is a different paragraph than the one we started in, so the point path
        // happily accepted it and ↓ dropped the caret at the far end of the cell it had just entered.
        // TryMoveCaretByLine above already consumed any remaining line inside this paragraph.
        if (_caret.Paragraph is { } fromP && AdjacentSiblingBlock(fromP, down) is TableBlock nextTable
            && EnterAdjacentTable(nextTable, down) is { Paragraph: not null } intoTable)
        {
            _caret = intoTable;
            ApplyArrowSelection(shift);
            return;
        }

        if (CaretToDocPoint(_caret) is not { } cp) return;
        var fromPara = _caret.Paragraph;
        // Cross-paragraph: nudge generously past the inter-paragraph margin so the move clears the gap
        // (a small ±2 nudge lands inside the margin and snaps back to the same line).
        double targetY = down ? cp.Y + cp.Height + 14 : cp.Y - 14;
        // From outside an inline table, vertical movement should land on the host text, not dive into a
        // cell; from inside a cell it navigates normally.
        bool inInlineCell = _caret.Paragraph != null && FindInlineTableHost(_caret.Paragraph) != null;
        _suppressInlineTableHit = !inInlineCell;
        TextPointer? tp;
        try { tp = GetPositionFromPoint(new Point(_desiredCaretX, targetY)); }
        finally { _suppressInlineTableHit = false; }
        if (tp?.Paragraph != null && !ReferenceEquals(tp.Paragraph, fromPara))
            _caret = tp;
        // A non-caret block (divider, block image) between paragraphs leaves a gap the nudge can't cross
        // (the hit snaps back to the same paragraph). Step to the adjacent top-level paragraph instead.
        else if (fromPara != null && AdjacentTopLevelParagraph(fromPara, down) is { } adj)
            _caret = new TextPointer(adj, OffsetAtDesiredX(adj, firstLine: down));
        ApplyArrowSelection(shift);
    }

    // The next/previous top-level Paragraph in document order (skips dividers, images, and tables — tables
    // have caret positions reached by the point-hit path, so only no-caret gaps fall back here).
    private Paragraph? AdjacentTopLevelParagraph(Paragraph p, bool down)
    {
        if (Document == null) return null;
        var blocks = Document.Blocks;
        int idx = blocks.IndexOf(p);
        if (idx < 0) return null;
        for (int i = down ? idx + 1 : idx - 1; i >= 0 && i < blocks.Count; i += down ? 1 : -1)
            if (blocks[i] is Paragraph q) return q;
        return null;
    }

    // The offset on a CELL paragraph's first (firstLine) or last visual line nearest the desired caret
    // column. The top-level OffsetAtDesiredX below can't serve here: it derives the wrap width and left
    // origin from the document content box, while a cell paragraph wraps at the cell's inner width and
    // starts at the cell's content left — using the wrong pair would land on the wrong visual line.
    private int OffsetAtDesiredXInCell(Paragraph p, Rect cellRect, bool firstLine)
    {
        double pl = CellParaLeft(p); // list/indent gutter, as every other cell walk applies
        double contentLeft = cellRect.X + CellPad + pl;
        double innerW = Math.Max(10, cellRect.Width - 2 * CellPad - pl);
        var layout = BuildTextLayout(p, innerW);
        Microsoft.Graphics.Canvas.Text.CanvasLineMetrics[] lines;
        try { lines = layout.LineMetrics; } catch { return firstLine ? 0 : GetParagraphLength(p); }
        if (lines.Length == 0) return 0;
        int line = firstLine ? 0 : lines.Length - 1;
        double yTop = 0;
        for (int i = 0; i < line; i++) yTop += lines[i].Height;
        return HitOffset(layout, p, _desiredCaretX - contentLeft, yTop + lines[line].Height / 2);
    }

    // The offset on p's first (firstLine) or last visual line nearest the desired caret column.
    private int OffsetAtDesiredX(Paragraph p, bool firstLine)
    {
        double px = ParaLeft(p);
        double pWidth = Math.Max(10, _layoutWidth - 20 - px - p.MarginRight);
        var layout = BuildTextLayout(p, pWidth);
        Microsoft.Graphics.Canvas.Text.CanvasLineMetrics[] lines;
        try { lines = layout.LineMetrics; } catch { return firstLine ? 0 : GetParagraphLength(p); }
        if (lines.Length == 0) return 0;
        int line = firstLine ? 0 : lines.Length - 1;
        double yTop = 0;
        for (int i = 0; i < line; i++) yTop += lines[i].Height;
        return HitOffset(layout, p, _desiredCaretX - px, yTop + lines[line].Height / 2);
    }

    // Moves the caret to the visual line above/below within the SAME top-level paragraph, keeping the
    // desired x. Returns false when the caret is already on the paragraph's top/bottom line (or it's a
    // single line / a cell paragraph) so the caller crosses to the adjacent paragraph.
    private bool TryMoveCaretByLine(bool down)
    {
        if (_caret.Paragraph is not { } p || Document == null || Document.Blocks.IndexOf(p) < 0) return false;
        double px = ParaLeft(p);
        double pWidth = Math.Max(10, _layoutWidth - 20 - px - p.MarginRight);
        var layout = BuildTextLayout(p, pWidth);
        Microsoft.Graphics.Canvas.Text.CanvasLineMetrics[] lines;
        try { lines = layout.LineMetrics; } catch { return false; }
        if (lines.Length <= 1) return false;

        int len = GetParagraphLength(p);
        int off = Math.Clamp(_caret.Offset, 0, len);
        // Affinity caret at a wrap boundary belongs to the EARLIER line (see MoveToLineEdge) — without
        // the bias, Down after End would skip a line and Up would stay put.
        if (_caret.AtLineEnd && off > 0) off--;
        int cur = 0, acc = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            acc += lines[i].CharacterCount;
            if (off < acc) { cur = i; break; }
            cur = i; // last line if never short-circuited (caret at end)
        }
        int target = down ? cur + 1 : cur - 1;
        if (target < 0 || target >= lines.Length) return false; // top/bottom line -> cross paragraphs

        double yTop = 0;
        for (int i = 0; i < target; i++) yTop += lines[i].Height;
        double localY = yTop + lines[target].Height / 2;
        int noff = HitOffset(layout, p, _desiredCaretX - px, localY, out bool ate);
        _caret = new TextPointer(p, noff) { AtLineEnd = ate };
        return true;
    }

    // Vertical caret navigation when the caret sits in a table cell. Order: (a) another visual line in the
    // SAME cell, (b) the cell in the row above/below (same column), (c) exit past the whole table box.
    // Returns false (caller's point-based path takes over) only when no table geometry is available.
    private bool VerticalInCell((TableBlock tb, int r, int c) loc, TextPointer cur, bool down, bool shift)
    {
        var (tb, r, c) = loc;

        // (a) Move within the same cell (multi-line paragraph or several block paragraphs in the cell).
        if (CaretToDocPoint(cur) is { } cp)
        {
            double ty = cp.Y + (down ? cp.Height + 2 : -2);
            _suppressInlineTableHit = false;
            TextPointer? inner;
            try { inner = GetPositionFromPoint(new Point(_desiredCaretX, ty)); }
            finally { _suppressInlineTableHit = false; }
            // When the caret's very next sibling block inside this cell is a TABLE, the only legitimate
            // in-cell step is another visual line of the SAME paragraph — every other paragraph in the
            // cell lies on the far side of that table. Without this guard a probe that misses the table
            // (its column band doesn't cover _desiredCaretX, e.g. an X inherited from a paragraph
            // outside the table) falls back to the cell's LAST paragraph, which FindCell reports as this
            // same cell — so step (a) accepted it and the caret skipped the nested table entirely.
            bool siblingTable = cur.Paragraph is { } sp0 && AdjacentSiblingBlock(sp0, down) is TableBlock;
            if (inner?.Paragraph != null && FindCell(inner.Paragraph) is { } il
                && ReferenceEquals(il.tb, tb) && il.r == r && il.c == c
                && (!siblingTable || ReferenceEquals(inner.Paragraph, cur.Paragraph))
                && !(ReferenceEquals(inner.Paragraph, cur.Paragraph) && inner.Offset == cur.Offset)
                // Must actually move in the pressed direction. Without this, pressing ↑ from the cell's
                // top line snaps (via the block-list nearest fallback) to the cell's LAST line, trapping
                // the caret instead of letting steps (b)/(c) exit the cell.
                && CaretToDocPoint(inner) is { } ip && (down ? ip.Y > cp.Y + 1 : ip.Y < cp.Y - 1))
            {
                _caret = inner; ApplyArrowSelection(shift); return true;
            }
        }

        // (a2) A sibling block inside the SAME cell is a NESTED table. Step (a) can't reach it: it
        // requires the hit to stay in cell (r,c), but a paragraph inside the nested table reports the
        // NESTED table's cell, so the guard rejects it — and step (b) below then jumps to the outer
        // table's adjacent row, skipping the nested table entirely (both directions).
        if (cur.Paragraph is { } curPara && AdjacentSiblingBlock(curPara, down) is TableBlock nestedTable
            && EnterAdjacentTable(nestedTable, down) is { Paragraph: not null } intoNested)
        {
            _caret = intoNested; ApplyArrowSelection(shift); return true;
        }

        var trect = TableDocRect(tb);

        // (b) Adjacent row, same column. Skip merge-covered slots.
        for (int tr = down ? r + 1 : r - 1; tr >= 0 && tr < tb.Rows; tr += down ? 1 : -1)
        {
            if (tb.IsCovered(tr, c)) continue;
            if (trect is { } rc && CellHitInRow(tb, rc, tr, c, down) is { } hit)
            {
                _caret = hit; ApplyArrowSelection(shift); return true;
            }
            // No geometry (nested table): land at the start/end of the target cell.
            if (trect == null)
            {
                var cell = tb.Cells[tr][c];
                var para = down ? FirstCellPara(cell) : LastCellPara(cell);
                if (para == null) return false;
                _caret = new TextPointer(para, down ? 0 : GetParagraphLength(para));
                ApplyArrowSelection(shift); return true;
            }
        }

        // (c) Top/bottom edge of the table: step out just past the whole box (don't dive back in).
        if (trect is { } outer)
        {
            double ty = down ? outer.Bottom + 6 : outer.Top - 6;
            _suppressInlineTableHit = true;
            TextPointer? outp;
            try { outp = GetPositionFromPoint(new Point(_desiredCaretX, ty)); }
            finally { _suppressInlineTableHit = false; }
            if (outp?.Paragraph != null && !(FindCell(outp.Paragraph) is { } ol && ReferenceEquals(ol.tb, tb)))
            {
                _caret = outp; ApplyArrowSelection(shift); return true;
            }
        }
        return false;
    }

    // Hit-tests the desired column-X inside cell (tr,c) of a table whose doc rect is known, entering from
    // the top (down) or bottom (up) of the cell. Returns the caret position there, or null if absent.
    private TextPointer? CellHitInRow(TableBlock tb, Rect tableRect, int tr, int c, bool down)
    {
        var tl = LayoutTable(tb, tableRect.X, tableRect.Y);
        foreach (var (rr, cc, rect) in tl.AnchorRects)
            if (rr == tr && cc == c)
            {
                double x = Math.Clamp(_desiredCaretX, rect.X + CellPad + 1, rect.X + rect.Width - CellPad - 1);
                double y = down ? rect.Y + CellPad + 2 : rect.Y + rect.Height - CellPad - 2;
                _suppressInlineTableHit = false;
                try { return GetPositionFromPoint(new Point(x, y)); }
                finally { _suppressInlineTableHit = false; }
            }
        return null;
    }

    // The doc-space outer rect of a table whose geometry was recorded this render pass — top-level block
    // tables (block selection rect) and inline tables (their reserved box). Null for nested tables.
    private Rect? TableDocRect(TableBlock tb)
    {
        if (_tableRects.TryGetValue(tb, out var outer)) return outer;
        foreach (var (it, v) in _inlineTableRects) if (ReferenceEquals(it.Table, tb)) return v.rect;
        // NESTED tables are neither block- nor inline-recorded, so this used to return null for them and
        // VerticalInCell's "step out past the whole box" could never fire from inside a nested table.
        // Every drawn table records its origin, so derive the rect from that.
        if (_tableOrigins.TryGetValue(tb, out var org))
        {
            var tl = LayoutTable(tb, org.X, org.Y);
            return new Rect(org.X, org.Y, tl.TableWidth, tl.TotalHeight);
        }
        return null;
    }

    private static Paragraph? FirstCellPara(TableCell cell)
    {
        foreach (var b in cell.Blocks) if (b is Paragraph p) return p;
        return null;
    }

    private static Paragraph? LastCellPara(TableCell cell)
    {
        Paragraph? last = null;
        foreach (var b in cell.Blocks) if (b is Paragraph p) last = p;
        return last;
    }

    // Home/End: move to the start/end of the caret's current VISUAL line (so a wrapped paragraph stops at
    // the wrap, not the paragraph boundary). Uses the same per-line CharacterCount accounting as the
    // up/down navigation; collapses to paragraph start/end when the paragraph is a single visual line.
    private void MoveToLineEdge(bool toEnd, bool shift)
    {
        var p = _caret.Paragraph!;
        int len = GetParagraphLength(p);
        int target = toEnd ? len : 0;

        // ParagraphWrapWidth, not the top-level formula: a cell paragraph wraps at the cell's inner
        // width, and building at the wrong width would misplace the visual-line boundaries.
        var layout = BuildTextLayout(p, ParagraphWrapWidth(p));
        Microsoft.Graphics.Canvas.Text.CanvasLineMetrics[] lines;
        try { lines = layout.LineMetrics; }
        catch { lines = System.Array.Empty<Microsoft.Graphics.Canvas.Text.CanvasLineMetrics>(); }

        bool atLineEnd = false;
        if (lines.Length > 1)
        {
            int off = Math.Clamp(_caret.Offset, 0, len);
            // An affinity caret sits at a wrap boundary but BELONGS to the earlier line — bias the
            // line search back one position so End/Home act on the line the caret is drawn on.
            if (_caret.AtLineEnd && off > 0) off--;
            int cur = 0, acc = 0;
            for (int i = 0; i < lines.Length; i++)
            {
                acc += lines[i].CharacterCount;
                if (off < acc) { cur = i; break; }
                cur = i; // last line when never short-circuited (caret at paragraph end)
            }
            int lineStart = 0;
            for (int i = 0; i < cur; i++) lineStart += lines[i].CharacterCount;
            if (!toEnd) target = lineStart;
            else
            {
                int e = Math.Min(lineStart + lines[cur].CharacterCount, len);
                // Drop the soft-wrap/newline trailing whitespace so End lands before the wrap, not at
                // the visual start of the next line — but only on WRAPPED lines: on the paragraph's
                // last visual line End must reach the true end even past trailing spaces.
                if (cur < lines.Length - 1)
                {
                    string plain = BuildPlain(p);
                    while (e > lineStart && e <= plain.Length && char.IsWhiteSpace(plain[e - 1])) e--;
                }
                target = e;
                // CJK text wraps without whitespace, so End on a wrapped line usually lands exactly on
                // the wrap boundary — the affinity keeps the caret drawn at THIS line's end.
                atLineEnd = cur < lines.Length - 1;
            }
        }

        _caret = new TextPointer(p, Math.Clamp(target, 0, len)) { AtLineEnd = atLineEnd };
        UpdateDesiredX();
        ApplyArrowSelection(shift);
    }

    // ---- word / document / page navigation --------------------------------
    // Word boundaries on plain text: a word is a run of non-whitespace (so a Korean eojeol = one word).
    private static int PrevWord(string text, int offset)
    {
        int i = Math.Clamp(offset, 0, text.Length);
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return i;
    }
    private static int NextWord(string text, int offset)
    {
        int i = Math.Clamp(offset, 0, text.Length);
        while (i < text.Length && !char.IsWhiteSpace(text[i])) i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    // Ctrl+Left/Right: jump by a word within the paragraph; at the paragraph boundary fall back to the
    // single-step move (which crosses paragraphs / table cells).
    private void WordMove(bool forward, bool shift)
    {
        var p = _caret.Paragraph;
        if (p == null) return;
        int off = _caret.Offset;
        if (forward && off < GetParagraphLength(p))
        {
            _caret = new TextPointer(p, NextWord(BuildPlain(p), off));
            UpdateDesiredX(); ApplyArrowSelection(shift);
        }
        else if (!forward && off > 0)
        {
            _caret = new TextPointer(p, PrevWord(BuildPlain(p), off));
            UpdateDesiredX(); ApplyArrowSelection(shift);
        }
        else if (forward) MoveCaretRight(shift);
        else MoveCaretLeft(shift);
    }

    // Ctrl+Backspace/Delete: delete to the next/previous word boundary (never crosses paragraphs). Pushes
    // an undo checkpoint and fires AfterEdit only when something is actually removed.
    private void WordDelete(bool forward)
    {
        if (HasSelection) { PushUndo(null); DeleteSelection(); AfterEdit(); return; }
        var p = _caret.Paragraph;
        if (p == null) return;
        int off = _caret.Offset;
        if (forward && off < GetParagraphLength(p))
        {
            PushUndo(null);
            int no = NextWord(BuildPlain(p), off);
            DeleteLocalText(p, off, no - off);
        }
        else if (!forward && off > 0)
        {
            PushUndo(null);
            int no = PrevWord(BuildPlain(p), off);
            DeleteLocalText(p, no, off - no);
            _caret = new TextPointer(p, no);
        }
        else return; // at a paragraph boundary in the delete direction: nothing to remove
        CollapseSelectionToCaret();
        AfterEdit();
    }

    // Ctrl+Home/End: caret to the very start / end of the document (first / last caret paragraph).
    private void GoToDocEdge(bool start, bool shift)
    {
        var paras = CaretParagraphs();
        if (paras.Count == 0) return;
        var target = start ? paras[0] : paras[^1];
        _caret = new TextPointer(target, start ? 0 : GetParagraphLength(target));
        UpdateDesiredX();
        ApplyArrowSelection(shift);
    }

    // Viewport height in document (pre-zoom) pixels, for PageUp/Down — keeps a little overlap between pages.
    private double PageStep()
    {
        double h = _scroll.ViewportHeight;
        if (h < 1) h = 500;
        double z = EffectiveZoom;
        if (z > 0.01) h /= z;
        return Math.Max(100, h - 40);
    }

    // PageUp/PageDown: move the caret ~one viewport up/down, keeping the desired column; the auto-scroll
    // (ScrollCaretIntoView, via ApplyArrowSelection) follows so the caret stays visible.
    private void MovePage(bool down, bool shift)
    {
        if (CaretToDocPoint(_caret) is not { } cp) return;
        double step = PageStep();
        double ty = down ? cp.Y + step : Math.Max(0, cp.Y - step);
        var tp = GetPositionFromPoint(new Point(_desiredCaretX, ty));
        if (tp?.Paragraph != null) _caret = tp;
        ApplyArrowSelection(shift);
    }

    private void ApplyArrowSelection(bool shift)
    {
        if (shift) _selEnd = Clone(_caret);
        else CollapseSelectionToCaret();
        _coalesceKey = null; // a typing run after navigation is a separate undo group
        _pendingCaretStyles = null; // pending format is position-specific
        RestartBlink();
        SyncIme();
        ScrollCaretIntoView();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    private void UpdateDesiredX()
    {
        if (CaretToDocPoint(_caret) is { } cp) _desiredCaretX = cp.X;
    }

    private int GetParagraphLength(Paragraph? p)
    {
        if (p == null) return 0;
        int len = 0;
        foreach (var inline in p.Inlines) len += InlineLen(inline);
        return len;
    }

    // The top-level Document.Blocks entry containing p, located by walking the PARENT CHAIN (wired by
    // UpdateParents after every structural edit) — O(nesting depth), unlike the old whole-document scan.
    // Chain shapes: p→FlowDocument (top-level), p→TableCell→TableBlock→…→FlowDocument (block table),
    // p→TableCell→TableBlock→InlineTable→hostParagraph→…→FlowDocument (inline table).
    private static Block? TopLevelBlockFor(Paragraph p)
    {
        TextElement? cur = p;
        while (cur != null)
        {
            if (cur.Parent is FlowDocument) return cur as Block;
            cur = cur.Parent as TextElement;
        }
        return null;
    }

    // ---- caret geometry (document space) ----------------------------------
    // The caret's top-level block comes from the parent chain + block layout map (O(depth) + O(1)); the
    // heavy layout is built only for the caret's own paragraph / host cell chain. Previously this
    // advanced from the document start on every caret paint, scroll and IME sync.
    private (double X, double Y, double Height)? CaretToDocPoint(TextPointer tp)
    {
        if (Document == null || tp.Paragraph == null) return null;
        if (TopLevelBlockFor(tp.Paragraph) is not { } top) return null;
        double width = _layoutWidth;
        const double listIndent = 10;
        var map = EnsureBlockLayout(width);
        if (_blockLayoutIndex == null || !_blockLayoutIndex.TryGetValue(top, out int bi)) return null;
        double y = map[bi].top;

        if (top is Paragraph p)
        {
            double px = ParaLeft(p);
            double pWidth = Math.Max(10, width - 20 - px - p.MarginRight);
            if (ReferenceEquals(p, tp.Paragraph))
            {
                var layout = BuildTextLayout(p, pWidth);
                var (cx, cy, ch) = CaretInLayout(layout, p, tp.Offset, tp.AtLineEnd);
                return (px + cx, y + cy, ch);
            }
            // tp lives inside one of p's inline tables (the chain ended at the host paragraph).
            var hostLayout = BuildTextLayout(p, pWidth);
            return CaretInInlineTable(p, hostLayout, px, y, tp);
        }
        if (top is TableBlock tb)
        {
            var tl = LayoutTable(tb, listIndent + tb.Indent, y);
            foreach (var (r, c, rect) in tl.AnchorRects)
            {
                var res = CaretInBlockList(tb.Cells[r][c].Blocks, rect.X + CellPad,
                    rect.Y + CellPad + CellContentOffsetY(tb.Cells[r][c], rect),
                    Math.Max(10, rect.Width - 2 * CellPad), tp);
                if (res != null) return res;
            }
        }
        return null;
    }

    // Finds the caret paragraph inside a cell block list and returns its document-space caret geometry
    // (mirrors DrawCellBlockList's advance). Recurses through nested tables.
    private (double X, double Y, double Height)? CaretInBlockList(System.Collections.Generic.IList<Block> blocks, double ox, double oy, double innerW, TextPointer tp)
    {
        double by = 0;
        foreach (var b in blocks)
        {
            double blkY = oy + by;
            switch (b)
            {
                case Paragraph para:
                {
                    double pl = CellParaLeft(para); // must match the draw/hit-test walks
                    double px = ox + pl;
                    double pw = Math.Max(10, innerW - pl);
                    if (ReferenceEquals(para, tp.Paragraph))
                    {
                        var layout = BuildTextLayout(para, pw);
                        var (cx, cy, ch) = CaretInLayout(layout, para, tp.Offset, tp.AtLineEnd);
                        return (px + cx, blkY + cy, ch);
                    }
                    if (HasInlineTable(para))
                    {
                        var layout = BuildTextLayout(para, pw);
                        if (CaretInInlineTable(para, layout, px, blkY, tp) is { } itres) return itres;
                    }
                    by += ParagraphHeight(para, pw);
                    break;
                }
                case ImageBlock cimg: by += CellImageSize(cimg, innerW).h; break;
                case DividerBlock: by += DividerHeight; break;
                case TableBlock nt:
                {
                    var tl = LayoutTable(nt, ox, blkY);
                    foreach (var (r, c, rect) in tl.AnchorRects)
                    {
                        var res = CaretInBlockList(nt.Cells[r][c].Blocks, rect.X + CellPad,
                            rect.Y + CellPad + CellContentOffsetY(nt.Cells[r][c], rect),
                            Math.Max(10, rect.Width - 2 * CellPad), tp);
                        if (res != null) return res;
                    }
                    by += tl.TotalHeight;
                    break;
                }
            }
        }
        return null;
    }

    // Caret geometry when tp lives inside one of paragraph p's inline-table cells (the offset->geometry
    // dual of HitInlineTable). Lets vertical caret movement read a starting point for a caret parked in
    // an inline-table cell, which CaretToDocPoint's top-level walk would otherwise miss.
    private (double X, double Y, double Height)? CaretInInlineTable(Paragraph p, CanvasTextLayout layout, double px, double oy, TextPointer tp)
    {
        int off = 0;
        foreach (var inl in p.Inlines)
        {
            if (inl is InlineTable it)
            {
                try
                {
                    var regions = layout.GetCharacterRegions(off, 1);
                    if (regions.Length > 0)
                    {
                        var lb = regions[0].LayoutBounds;
                        double tx = px + lb.X, ty = oy + lb.Y;
                        var tl = LayoutTable(it.Table, tx, ty);
                        foreach (var (r, c, rect) in tl.AnchorRects)
                        {
                            var res = CaretInBlockList(it.Table.Cells[r][c].Blocks, rect.X + CellPad,
                                rect.Y + CellPad + CellContentOffsetY(it.Table.Cells[r][c], rect),
                                Math.Max(10, rect.Width - 2 * CellPad), tp);
                            if (res != null) return res;
                        }
                    }
                }
                catch { }
            }
            off += InlineLen(inl);
        }
        return null;
    }

    // ---- selection + caret painting (called from the render walk) ---------
    private void DrawSelectionHighlight(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        if (_printMode || !HasSelection) return;
        // When a table has a cell-block selection, its selection is drawn as the rectangle tint
        // (DrawNestedTable). Suppress the per-paragraph text highlight for EVERY cell of that table —
        // not just the ones inside the rectangle — otherwise the linear _selStart.._selEnd span (which
        // runs row-major and passes through cells in other columns) would highlight adjacent-column cells
        // that aren't actually selected.
        // Table identity is all this needs, so use the O(1) parent-chain lookup — this runs once per
        // DRAWN paragraph while a cell-block selection is active.
        if (_renderCellSel is { } cb && ReferenceEquals(CellTableOf(p), cb.tb)) return;
        TextPointer s = _selStart, e = _selEnd;
        if (ComparePositions(s, e) > 0) (s, e) = (e, s);

        int len = GetParagraphLength(p);
        var pStart = new TextPointer(p, 0);
        var pEnd = new TextPointer(p, len);
        if (ComparePositions(pEnd, s) < 0 || ComparePositions(pStart, e) > 0) return; // paragraph outside selection

        int hlStart = ReferenceEquals(p, s.Paragraph) ? s.Offset : 0;
        int hlEnd = ReferenceEquals(p, e.Paragraph) ? e.Offset : len;
        if (hlEnd <= hlStart) return;

        try
        {
            var regions = layout.GetCharacterRegions(hlStart, hlEnd - hlStart);
            foreach (var r in regions)
            {
                var lb = r.LayoutBounds;
                ds.FillRectangle(new Rect(px + lb.X, oy + lb.Y, lb.Width, lb.Height), SelectionFill);
            }
        }
        catch { }
    }

    private void DrawCaret(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        if (_printMode || !_hasFocus || !_caretOn || IsReadOnly) return;
        if (!IsCaretInParagraph(p)) return;
        var (cx, cy, ch) = CaretInLayout(layout, p, _caret.Offset, _caret.AtLineEnd);
        float x = (float)(px + cx);
        ds.DrawLine(x, (float)(oy + cy), x, (float)(oy + cy + ch), CaretColor, 1.5f);
    }

    private (double X, double Y, double Height) CaretInLayout(CanvasTextLayout layout, Paragraph p, int offset, bool atLineEnd = false)
    {
        int len = GetParagraphLength(p);
        offset = Math.Clamp(offset, 0, len);
        double h = EmptyLineHeight(p);
        double yTop;
        Vector2 pos;
        // Affinity: at a soft-wrap boundary the offset is shared by two visual positions. The trailing
        // side of the PREVIOUS character renders the caret at the end of the earlier line (End key,
        // trailing-half clicks); the default leading side renders it before the next line's first glyph.
        // Off-boundary the two edges coincide, so applying the trailing form there is harmless.
        bool trailing = atLineEnd && offset > 0;
        try { pos = layout.GetCaretPosition(trailing ? offset - 1 : offset, trailing); }
        catch { pos = new Vector2(0, 0); }
        yTop = pos.Y;
        // Caret parked at the very end of a paragraph that ends in a soft break ("…\n"): it belongs on
        // the NEW empty visual line, and GetCaretPosition already puts it there. The region probe below
        // would look at len-1 — the '\n' itself — whose glyph region sits at the END OF THE PREVIOUS
        // line, so taking Y from it drew the caret a line up (after Shift+Enter the caret appeared back
        // on the old line until the next keystroke moved it). Keep GetCaretPosition's Y and the natural
        // empty-line height.
        bool onFreshBreakLine = !trailing && offset == len && EndsWithHardBreak(p);
        if (len > 0 && !onFreshBreakLine)
        {
            try
            {
                int probe = trailing ? offset - 1 : (offset < len ? offset : len - 1);
                var regions = layout.GetCharacterRegions(probe, 1);
                if (regions.Length > 0)
                {
                    var rb = regions[0].LayoutBounds;
                    double textH = PtToPx(RunFontSizeAt(p, probe)) * NaturalLineFactor;
                    // A line dominated by a tall inline object (image/table) gives a giant region height —
                    // keep the caret at normal text height, aligned to the bottom of the line.
                    if (rb.Height > textH * 1.5) { h = textH; yTop = rb.Y + rb.Height - textH; }
                    else { h = rb.Height; yTop = rb.Y; }
                }
            }
            catch { }
        }
        return (pos.X, yTop, h);
    }

    // Whether the paragraph's last character is a soft line break, so the caret at its end sits on a
    // fresh (empty) visual line. Walks back over the inlines instead of building the whole plain string.
    private static bool EndsWithHardBreak(Paragraph p)
    {
        for (int i = p.Inlines.Count - 1; i >= 0; i--)
        {
            if (p.Inlines[i] is Run r)
            {
                if (r.Text is { Length: > 0 } t) return t[^1] == '\n';
                continue; // empty run: keep looking further back
            }
            return false; // an object inline (image/table) is the last thing in the paragraph
        }
        return false;
    }

    // The effective font size (pt) of the run at a logical offset (heading override applied).
    private double RunFontSizeAt(Paragraph p, int offset)
    {
        if (p.HeadingLevel is >= 1 and <= 6) return HeadingFontSize(p.HeadingLevel);
        var r = RunAtOffset(p, offset);
        return r != null && r.FontSize > 0 ? r.FontSize : DefaultFontSize;
    }
}
