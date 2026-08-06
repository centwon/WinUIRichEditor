using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: block-image selection chrome (border + bottom-right resize handle), drag-resize (aspect
// locked), and delete. A top-level block image is selected by clicking it; the selection is cleared by
// any other press or key. Mirrors the Avalonia original's _selectedBlock / image-resize behavior.
public partial class RichEditor
{
    private Block? _selectedBlock;          // currently selected block (top-level ImageBlock or TableBlock)
    private (Paragraph p, InlineImage img)? _selectedInline; // currently selected inline image
    private (Paragraph host, InlineTable it)? _selectedInlineTable; // currently selected inline table
    private ImageBlock? _resizingImage;     // block image being drag-resized, if any
    private InlineImage? _resizingInline;   // inline image being drag-resized, if any
    private double _imageAspect;            // width/height captured at resize start (aspect lock)
    private double _resizeStartX;           // pointer x at resize start
    private double _resizeStartW;           // image width at resize start

    // Inline-object rects in document space, recorded whenever an object draws (keyed by object
    // identity, so partial-region redraws just overwrite) and kept until the next relayout drops them
    // (ClearRecordedGeometry — positions may have moved). Doc-space rects survive scrolling. This
    // replaces the old rebuild-document-wide-every-draw scheme, which forced the render walk to lay out
    // every atomic-inline paragraph and table per frame. Interaction needs visibility, so an object is
    // always drawn (and thus recorded) before it can be clicked.
    private readonly Dictionary<InlineImage, (Paragraph p, Rect rect)> _inlineImageRects = new();
    private readonly Dictionary<InlineTable, (Paragraph host, Rect rect)> _inlineTableRects = new();
    // Rendered rects of block images inside table CELLS. Top-level block images come from the block
    // layout map (BlockImageRects); a cell image is not in that map at all, so without this registry it
    // could not be clicked, selected, resized or deleted — at any nesting depth. The recorded rect is the
    // size the image was DRAWN at: a cell scales a picture down to fit its width (CellImageSize), so the
    // declared Width is normally several times larger (insertion caps to the document width, not the
    // cell's). That is also why the drag below is measured against this rect and not against Width —
    // seeding it from the declared size puts every realistic drag inside the range that still clamps to
    // the same drawn width, so the handle looks dead.
    private readonly Dictionary<ImageBlock, Rect> _cellImageRects = new();

    private const double ResizeHandleSize = 11;
    private const double ResizeGrab = 14;   // grab radius around the corner

    private static readonly Color BlockSelBorder = Color.FromArgb(255, 0, 120, 215);
    private static readonly Color BlockHandleFill = Color.FromArgb(255, 0, 120, 215);

    /// <summary>True when a block/inline image or an inline table is selected as an object.</summary>
    public bool HasBlockSelection => _selectedBlock != null || _selectedInline != null || _selectedInlineTable != null;

    private void ClearObjectSelection() { _selectedBlock = null; _selectedInline = null; _selectedInlineTable = null; }

    // Records an inline table's rect for hit-testing and draws its selection chrome if selected.
    private void TrackInlineTable(CanvasDrawingSession ds, Paragraph host, InlineTable it, Rect rect)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _inlineTableRects[it] = (host, rect);
        if (_selectedInlineTable is { } s && ReferenceEquals(s.it, it))
            ds.DrawRectangle(new Rect(rect.X - 1.5, rect.Y - 1.5, rect.Width + 3, rect.Height + 3), BlockSelBorder, 2.5f);
    }

    // Selects a whole inline table when its left/top border is clicked (a click inside descends into a
    // cell instead, via HitInlineTable). The grab band reuses the block-table border width.
    private bool TrySelectInlineTable(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var (it, v) in _inlineTableRects)
            if (OnEdgeBorder(v.rect, pt))
            {
                ClearObjectSelection();
                _selectedInlineTable = (v.host, it);
                _isSelecting = false;
                CollapseSelectionToCaret();
                RestartBlink();
                InvalidateCanvas();
                RaiseStatusChanged();
                return true;
            }
        return false;
    }

    private bool OverInlineTableBorder(Point pt)
    {
        foreach (var v in _inlineTableRects.Values)
            if (OnEdgeBorder(v.rect, pt)) return true;
        return false;
    }

    // True when pt is on a rect's outer LEFT or TOP border band (shared by inline-table selection).
    private static bool OnEdgeBorder(Rect o, Point pt)
    {
        const double grab = 5;
        bool onLeft = Math.Abs(pt.X - o.Left) <= grab && pt.Y >= o.Top - grab && pt.Y <= o.Bottom + grab;
        bool onTop = Math.Abs(pt.Y - o.Top) <= grab && pt.X >= o.Left - grab && pt.X <= o.Right + grab;
        return onLeft || onTop;
    }

    // Removes the selected inline table from its host paragraph, dropping the caret where it sat.
    private void DeleteSelectedInlineTable()
    {
        if (Document == null || _selectedInlineTable is not { } sel) return;
        PushUndo(null);
        var (host, it) = sel;
        _selectedInlineTable = null;
        int idx = host.Inlines.IndexOf(it);
        if (idx >= 0)
        {
            int off = 0;
            for (int i = 0; i < idx; i++) off += InlineLen(host.Inlines[i]);
            host.Inlines.RemoveAt(idx);
            if (host.Inlines.Count == 0) host.Inlines.Add(new Run { Text = "" });
            _caret = new TextPointer(host, Math.Min(off, GetParagraphLength(host)));
            CollapseSelectionToCaret();
        }
        UpdateParents(Document);
        AfterEdit();
    }

    // Begins a block-image interaction at the press point: a drag on the selected image's corner handle
    // starts a resize; a click inside any block image selects it. Returns true when it consumed the press.
    // What a pointer press on the canvas lands on. Split out of TryBeginImageInteraction because that
    // method needs a PointerRoutedEventArgs — a WinRT type with no public constructor — while the ORDER
    // of these tests is a contract that has already cost a defect once.
    internal enum PointerTarget { None, SelectedBlockImageHandle, SelectedInlineImageHandle, CellImage, InlineImage, BlockImage }

    /// <summary>Priority order for a pointer press, and the reasons it is this order:
    /// <list type="bullet">
    /// <item>EVERY resize-handle test runs before EVERY selection click. A handle's grab band extends
    /// OUTSIDE its own rect, so it can land inside a neighbouring object; if selection went first the
    /// neighbour would swallow the grab and the handle would be unusable.</item>
    /// <item>Handles only exist while editing, so read-only skips straight to selection.</item>
    /// <item>A cell-hosted image is tested before a top-level one: it is drawn inside a table that also
    /// covers the point, so the innermost target has to win.</item>
    /// <item>An inline image beats a block image — it sits within text, where the block hit-test is
    /// coarser.</item>
    /// </list></summary>
    internal static PointerTarget ChoosePointerTarget(
        bool isReadOnly,
        bool onSelectedBlockImageHandle, bool onSelectedInlineImageHandle,
        bool inCellImage, bool inInlineImage, bool inBlockImage)
    {
        if (!isReadOnly && onSelectedBlockImageHandle) return PointerTarget.SelectedBlockImageHandle;
        if (!isReadOnly && onSelectedInlineImageHandle) return PointerTarget.SelectedInlineImageHandle;
        if (inCellImage) return PointerTarget.CellImage;
        if (inInlineImage) return PointerTarget.InlineImage;
        if (inBlockImage) return PointerTarget.BlockImage;
        return PointerTarget.None;
    }

    private bool TryBeginImageInteraction(Point pt, PointerRoutedEventArgs e)
    {
        // Hit-test everything first, then let ChoosePointerTarget decide. The drag is seeded from the
        // rect the handle was DRAWN at, for both registries — see _cellImageRects for why.
        Rect? blockHandleRect = null;
        if (_selectedBlock is ImageBlock selB)
            foreach (var rect in BlockImageHandleRects(selB))
                if (OnResizeHandle(rect, pt)) { blockHandleRect = rect; break; }

        (Paragraph p, InlineImage img)? selInline = null;
        Rect inlineHandleRect = default;
        if (_selectedInline is { } selI
            && _inlineImageRects.TryGetValue(selI.img, out var selRect) && OnResizeHandle(selRect.rect, pt))
        { selInline = selI; inlineHandleRect = selRect.rect; }

        ImageBlock? cellImage = null;
        foreach (var (img, rect) in _cellImageRects)
            if (rect.Contains(pt)) { cellImage = img; break; }

        (Paragraph p, InlineImage img)? inlineImage = null;
        foreach (var (img, v) in _inlineImageRects)
            if (v.rect.Contains(pt)) { inlineImage = (v.p, img); break; }

        ImageBlock? blockImage = null;
        foreach (var (img, rect) in BlockImageRects())
            if (rect.Contains(pt)) { blockImage = img; break; }

        void SelectObject(ImageBlock? block, (Paragraph p, InlineImage img)? inline)
        {
            _selectedBlock = block;
            _selectedInline = inline;
            _isSelecting = false;
            CollapseSelectionToCaret();
            RestartBlink();
            InvalidateCanvas();
            RaiseStatusChanged();
        }

        switch (ChoosePointerTarget(IsReadOnly, blockHandleRect != null, selInline != null,
                                    cellImage != null, inlineImage != null, blockImage != null))
        {
            case PointerTarget.SelectedBlockImageHandle:
            {
                var rect = blockHandleRect!.Value;
                _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
                _resizingImage = (ImageBlock)_selectedBlock!;
                _imageAspect = rect.Height > 0 ? rect.Width / rect.Height : 1;
                _resizeStartX = pt.X;
                _resizeStartW = rect.Width;
                _canvas.CapturePointer(e.Pointer);
                return true;
            }
            case PointerTarget.SelectedInlineImageHandle:
            {
                var img = selInline!.Value.img;
                _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
                _resizingInline = img;
                _imageAspect = img.Height > 0 ? img.Width / img.Height : 1;
                _resizeStartX = pt.X;
                _resizeStartW = img.Width > 0 ? img.Width : inlineHandleRect.Width;
                _canvas.CapturePointer(e.Pointer);
                return true;
            }
            case PointerTarget.CellImage:  SelectObject(cellImage, null); return true;
            case PointerTarget.InlineImage: SelectObject(null, inlineImage); return true;
            case PointerTarget.BlockImage: SelectObject(blockImage, null); return true;
            default: return false;
        }
    }

    // Live image resize during a pointer drag (aspect-locked). Returns true while a resize is active.
    private bool TryResizeImage(Point pt)
    {
        if (_resizingImage != null)
        {
            PushDragUndoOnce(); // snapshot before the first actual write
            double dx = pt.X - _resizeStartX;
            double newW = Math.Clamp(_resizeStartW + dx, 24, Math.Max(24, _layoutWidth - 40));
            _resizingImage.Width = newW;
            _resizingImage.Height = _imageAspect > 0 ? newW / _imageAspect : _resizingImage.Height;
            // A block image in a cell sizes that cell, and so the row. Evict the enclosing table chain
            // the way the column/row drags do, or the row only grows after the NEXT edit.
            if (_resizingImage.Parent is TableCell rc && rc.Parent is TableBlock rtb) InvalidateTableMeasure(rtb);
            RelayoutToViewport();
            return true;
        }
        if (_resizingInline != null)
        {
            PushDragUndoOnce(); // snapshot before the first actual write
            double dx = pt.X - _resizeStartX;
            double newW = Math.Clamp(_resizeStartW + dx, 16, Math.Max(16, _layoutWidth - 40));
            _resizingInline.Width = newW;
            _resizingInline.Height = _imageAspect > 0 ? newW / _imageAspect : _resizingInline.Height;
            _tableRowHeights.Clear(); // an inline image may live in a table cell — re-measure rows
            RelayoutToViewport();
            return true;
        }
        return false;
    }

    private bool EndImageResize(PointerRoutedEventArgs e)
    {
        if (_resizingImage == null && _resizingInline == null) return false;
        _resizingImage = null;
        _resizingInline = null;
        _dragUndoPending = false; // released without dragging: nothing was pushed
        _canvas.ReleasePointerCapture(e.Pointer);
        RaiseStatusChanged();
        return true;
    }

    private static bool OnResizeHandle(Rect rect, Point pt)
        => Math.Abs(pt.X - rect.Right) <= ResizeGrab && Math.Abs(pt.Y - rect.Bottom) <= ResizeGrab;

    // Deletes whichever image object is selected (inline takes priority).
    private void DeleteSelectedObject()
    {
        if (_selectedInlineTable != null) DeleteSelectedInlineTable();
        else if (_selectedInline != null) DeleteSelectedInline();
        else if (_selectedBlock != null) DeleteSelectedBlock();
    }

    // Removes the selected inline image from its host paragraph, dropping the caret where it sat.
    private void DeleteSelectedInline()
    {
        if (Document == null || _selectedInline is not { } sel) return;
        PushUndo(null);
        var (p, img) = sel;
        _selectedInline = null;
        int idx = p.Inlines.IndexOf(img);
        if (idx >= 0)
        {
            int off = 0;
            for (int i = 0; i < idx; i++) off += InlineLen(p.Inlines[i]);
            p.Inlines.RemoveAt(idx);
            if (p.Inlines.Count == 0) p.Inlines.Add(new Run { Text = "" });
            _caret = new TextPointer(p, Math.Min(off, GetParagraphLength(p)));
            CollapseSelectionToCaret();
        }
        UpdateParents(Document);
        AfterEdit();
    }

    // Deletes the selected block image (top-level), placing the caret on the nearest paragraph.
    private void DeleteSelectedBlock()
    {
        if (Document == null || _selectedBlock is not { } blk) return;
        PushUndo(null);
        _selectedBlock = null;
        int idx = Document.Blocks.IndexOf(blk);
        if (idx < 0) { RemoveBlockAnywhere(blk); UpdateParents(Document); AfterEdit(); return; }
        Document.Blocks.RemoveAt(idx);
        UpdateParents(Document); // re-normalizes so a paragraph borders the gap

        var blocks = Document.Blocks;
        Paragraph? target = null;
        for (int i = Math.Min(idx, blocks.Count - 1); i >= 0 && i < blocks.Count; i++)
            if (blocks[i] is Paragraph pp) { target = pp; break; }
        target ??= FirstParagraph();
        if (target != null) { _caret = new TextPointer(target, 0); CollapseSelectionToCaret(); }
        AfterEdit();
    }

    // The drawn rect(s) a given block image's resize handle can sit on: the block layout map for a
    // top-level image, the cell registry for one inside a table cell (which is not in that map).
    private IEnumerable<Rect> BlockImageHandleRects(ImageBlock target)
    {
        if (_cellImageRects.TryGetValue(target, out var cellRect)) yield return cellRect;
        foreach (var (img, rect) in BlockImageRects())
            if (ReferenceEquals(img, target)) yield return rect;
    }

    // Block image rects in document space, straight off the block layout map (no advance walk).
    private IEnumerable<(ImageBlock img, Rect rect)> BlockImageRects()
    {
        if (Document == null) yield break;
        foreach (var (block, y, h, _) in EnsureBlockLayout(_layoutWidth))
            if (block is ImageBlock img)
                yield return (img, new Rect(10 + img.Indent, y, BlockImageDims(img).w, h));
    }

    // Draws the selection border + bottom-right resize handle over a selected block image.
    private void DrawBlockImageChrome(CanvasDrawingSession ds, ImageBlock img, Rect rect)
    {
        if (_printMode || !ReferenceEquals(_selectedBlock, img)) return;
        DrawSelectionChrome(ds, rect);
    }

    // Records a cell-hosted block image's DRAWN rect for hit-testing and draws its selection chrome if
    // selected. Called from the cell block walk, which is the only place that knows the scaled size.
    private void TrackCellImage(CanvasDrawingSession ds, ImageBlock img, Rect rect)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _cellImageRects[img] = rect;
        if (ReferenceEquals(_selectedBlock, img)) DrawSelectionChrome(ds, rect);
    }

    // Records an inline image's rect for hit-testing and draws its selection chrome if selected.
    // Called from DrawInlineObjects for each inline image (top-level and table-cell paragraphs).
    private void TrackInlineImage(CanvasDrawingSession ds, Paragraph p, InlineImage img, Rect rect)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _inlineImageRects[img] = (p, rect);
        if (_selectedInline is { } s && ReferenceEquals(s.img, img)) DrawSelectionChrome(ds, rect);
    }

    private void DrawSelectionChrome(CanvasDrawingSession ds, Rect rect)
    {
        ds.DrawRectangle(rect, BlockSelBorder, 2.5f);
        var h = new Rect(rect.Right - ResizeHandleSize / 2, rect.Bottom - ResizeHandleSize / 2, ResizeHandleSize, ResizeHandleSize);
        ds.FillRectangle(h, BlockHandleFill);
        ds.DrawRectangle(h, Colors.White, 1.5f);
    }
}
