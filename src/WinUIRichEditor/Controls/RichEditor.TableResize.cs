using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Phase 5: table column-width resize. Column boundary rects are recorded each render pass (top-level
// tables) and hit-tested on press; dragging an internal edge redistributes the two adjacent columns,
// the outer-right edge grows/shrinks the table. Mirrors the Avalonia original's column resize.
public partial class RichEditor
{
    // Keyed by table identity and replaced wholesale whenever the table draws; kept until the next
    // relayout (ClearRecordedGeometry). See the inline-object rects in RichEditor.BlockSelection.cs for
    // the scheme rationale (draw-time recording instead of a per-frame document-wide geometry pass).
    private readonly Dictionary<TableBlock, List<(int col, Rect rect)>> _columnBoundaries = new();
    private readonly Dictionary<TableBlock, List<(int row, double height, Rect rect)>> _rowBoundaries = new();
    private readonly Dictionary<TableBlock, Rect> _tableRects = new();
    // Doc-space origin of EVERY drawn table — top-level, nested-in-cell and inline. _tableRects only
    // covers top-level tables and _inlineTableRects only inline ones, so nested tables previously had no
    // recorded geometry at all; caret code that needs to locate one (entering it with ↑/↓, stepping out
    // of it) had nothing to work from.
    private readonly Dictionary<TableBlock, Point> _tableOrigins = new();

    // Drops every recorded doc-space rect (inline objects, table outers, resize boundaries). Called from
    // RelayoutToViewport — the funnel for anything that can move content (edit, format, resize, zoom,
    // width change). Scrolling keeps them (doc space); the next draw re-records whatever is visible.
    private void ClearRecordedGeometry()
    {
        _inlineImageRects.Clear();
        _inlineTableRects.Clear();
        _cellImageRects.Clear();
        _columnBoundaries.Clear();
        _rowBoundaries.Clear();
        _tableRects.Clear();
        _tableOrigins.Clear();
    }

    private const double TableBorderGrab = 5;

    private bool _resizingColumn;
    private TableBlock? _resizingColTable;
    private int _resizingColIndex;
    private bool _resizingLastCol;
    private double _colResizeStartX, _initColW, _initNextColW;

    private bool _resizingRow;
    private TableBlock? _resizingRowTable;
    private int _resizingRowIndex;
    private double _rowResizeStartY, _initRowH;

    private const double ColBorderGrab = 4;

    // A resize drag takes its undo snapshot on the FIRST actual move, not on press. Pressing a table
    // border or an image handle and releasing without dragging changes nothing, so it must not clone the
    // whole document into the undo stack, consume an undo step, or flip IsModified — which drives the
    // host's unsaved-changes prompt (the same "no empty undo step for a no-op" rule the key handlers
    // follow). The snapshot still precedes the first mutation because each Resize* pushes before it writes.
    private bool _dragUndoPending;

    private void PushDragUndoOnce()
    {
        if (!_dragUndoPending) return;
        _dragUndoPending = false;
        PushUndo(null);
    }

    // Records each column's right-edge and each row's bottom-edge grab band. Called from DrawNestedTable
    // so it covers EVERY table — top-level, nested-in-cell, and inline — not just the top level.
    private void RecordTableResizeBoundaries(TableBlock tb, double top, in TableLayout tl)
    {
        _tableOrigins[tb] = new Point(tl.ColX.Length > 0 ? tl.ColX[0] : 0, top);
        var cols = new List<(int col, Rect rect)>(tb.Columns);
        for (int c = 0; c < tb.Columns && c + 1 < tl.ColX.Length; c++)
        {
            double x = tl.ColX[c + 1];
            cols.Add((c, new Rect(x - ColBorderGrab, top, 2 * ColBorderGrab, tl.TotalHeight)));
        }
        _columnBoundaries[tb] = cols;

        double left = tl.ColX[0];
        var rows = new List<(int row, double height, Rect rect)>(tb.Rows);
        for (int r = 0; r < tb.Rows && r + 1 < tl.RowY.Length; r++)
        {
            double y = tl.RowY[r + 1];
            double h = tl.RowY[r + 1] - tl.RowY[r];
            rows.Add((r, h, new Rect(left, y - ColBorderGrab, tl.TableWidth, 2 * ColBorderGrab)));
        }
        _rowBoundaries[tb] = rows;
    }

    // The outer rect of a TOP-LEVEL table, for left/top-border block selection (only top-level tables
    // are block-selectable; nested/inline tables are reached/edited through their cells).
    private void RecordTopLevelTableRect(TableBlock tb, double top, in TableLayout tl)
    {
        if (_printMode) return; // print renders must not touch the screen hit-test geometry
        _tableRects[tb] = new Rect(tl.ColX[0], top, tl.TableWidth, tl.TotalHeight);
    }

    private static void EnsureColumnWidths(TableBlock tb)
    {
        while (tb.ColumnWidths.Count < tb.Columns) tb.ColumnWidths.Add(100);
    }

    private static void EnsureRowHeights(TableBlock tb)
    {
        while (tb.RowHeights.Count < tb.Rows) tb.RowHeights.Add(0);
    }

    // Starts a column resize if the press lands on a column boundary. Returns true when it consumed it.
    private bool TryBeginColumnResize(Point pt, PointerRoutedEventArgs e)
    {
        if (IsReadOnly) return false;
        foreach (var (tb, list) in _columnBoundaries)
        foreach (var (col, rect) in list)
            if (rect.Contains(pt))
            {
                EnsureColumnWidths(tb);
                _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
                _resizingColumn = true;
                _resizingColTable = tb;
                _resizingColIndex = col;
                _resizingLastCol = col >= tb.Columns - 1;
                _colResizeStartX = pt.X;
                _initColW = tb.ColumnWidths[col];
                _initNextColW = (col + 1 < tb.ColumnWidths.Count) ? tb.ColumnWidths[col + 1] : 100;
                _canvas.CapturePointer(e.Pointer);
                return true;
            }
        return false;
    }

    // Live column resize during a drag.
    private void ResizeColumn(Point pt)
    {
        if (_resizingColTable is not { } tb) return;
        PushDragUndoOnce(); // snapshot before the first actual write
        const double minW = 20;
        double diff = pt.X - _colResizeStartX;
        EnsureColumnWidths(tb);
        if (_resizingLastCol)
        {
            // Outer-right edge: grow/shrink this column, changing the table's total width.
            tb.ColumnWidths[_resizingColIndex] = Math.Max(minW, _initColW + diff);
        }
        else
        {
            // Internal edge: redistribute between the two adjacent columns, total fixed.
            double minDiff = -(_initColW - minW);
            double maxDiff = _initNextColW - minW;
            diff = Math.Clamp(diff, minDiff, maxDiff);
            tb.ColumnWidths[_resizingColIndex] = _initColW + diff;
            tb.ColumnWidths[_resizingColIndex + 1] = _initNextColW - diff;
        }
        // Targeted invalidation: the resized table's rows reflow, and so do its ancestor tables' host
        // rows (nested/inline). Other tables are untouched — clearing ALL row caches made every pointer
        // move during a drag re-measure every table in the document.
        InvalidateTableMeasure(tb);
        RelayoutToViewport();
    }

    private bool EndColumnResize(PointerRoutedEventArgs e)
    {
        if (!_resizingColumn) return false;
        _resizingColumn = false;
        _resizingColTable = null;
        _dragUndoPending = false; // released without dragging: nothing was pushed, nothing to keep armed
        _canvas.ReleasePointerCapture(e.Pointer);
        RaiseStatusChanged();
        return true;
    }

    private bool OverColumnBoundary(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var list in _columnBoundaries.Values)
            foreach (var (_, rect) in list)
                if (rect.Contains(pt)) return true;
        return false;
    }

    // Starts a row resize if the press lands on a row boundary. Returns true when it consumed it.
    private bool TryBeginRowResize(Point pt, PointerRoutedEventArgs e)
    {
        if (IsReadOnly) return false;
        foreach (var (tb, list) in _rowBoundaries)
        foreach (var (row, height, rect) in list)
            if (rect.Contains(pt))
            {
                EnsureRowHeights(tb);
                _dragUndoPending = true; // pushed on the first move (see PushDragUndoOnce)
                _resizingRow = true;
                _resizingRowTable = tb;
                _resizingRowIndex = row;
                _rowResizeStartY = pt.Y;
                _initRowH = height; // current rendered height (content- or user-driven)
                _canvas.CapturePointer(e.Pointer);
                return true;
            }
        return false;
    }

    // Live row resize during a drag. RowHeights only grows a row past its content; shrinking is bounded
    // by the cell content height (LayoutTable takes the max), so this can't clip text.
    private void ResizeRow(Point pt)
    {
        if (_resizingRowTable is not { } tb) return;
        PushDragUndoOnce(); // snapshot before the first actual write
        EnsureRowHeights(tb);
        double diff = pt.Y - _rowResizeStartY;
        tb.RowHeights[_resizingRowIndex] = Math.Max(20, _initRowH + diff);
        InvalidateTableMeasure(tb); // this table + ancestor hosts only (see ResizeColumn)
        RelayoutToViewport();
    }

    private bool EndRowResize(PointerRoutedEventArgs e)
    {
        if (!_resizingRow) return false;
        _resizingRow = false;
        _resizingRowTable = null;
        _dragUndoPending = false;
        _canvas.ReleasePointerCapture(e.Pointer);
        RaiseStatusChanged();
        return true;
    }

    private bool OverRowBoundary(Point pt)
    {
        if (IsReadOnly) return false;
        foreach (var list in _rowBoundaries.Values)
            foreach (var (_, _, rect) in list)
                if (rect.Contains(pt)) return true;
        return false;
    }

    // True when the point is on a table's outer LEFT or TOP border band (its right/bottom edges are the
    // last column/row resize boundaries, so only left/top select the table as a block).
    private bool OnTableSelectBorder(Point pt, out TableBlock? table)
    {
        foreach (var (tb, o) in _tableRects)
        {
            bool onLeft = Math.Abs(pt.X - o.Left) <= TableBorderGrab && pt.Y >= o.Top - TableBorderGrab && pt.Y <= o.Bottom + TableBorderGrab;
            bool onTop = Math.Abs(pt.Y - o.Top) <= TableBorderGrab && pt.X >= o.Left - TableBorderGrab && pt.X <= o.Right + TableBorderGrab;
            if (onLeft || onTop) { table = tb; return true; }
        }
        table = null;
        return false;
    }

    // Selects a whole table when its left/top border is clicked (Delete then removes it). Editing only:
    // in a read-only viewer the selection chrome would be a dead end (Delete is gated by IsReadOnly), and
    // it would also swallow a click that should place the caret — TrySelectInlineTable already bails out
    // the same way.
    private bool TrySelectTableBlock(Point pt)
    {
        if (IsReadOnly) return false;
        if (!OnTableSelectBorder(pt, out var tb) || tb == null) return false;
        _selectedInline = null;
        _selectedBlock = tb;
        _isSelecting = false;
        CollapseSelectionToCaret();
        RestartBlink();
        InvalidateCanvas();
        RaiseStatusChanged();
        return true;
    }

    // Draws the selection chrome (border) around a selected table.
    private void DrawTableSelectionChrome(CanvasDrawingSession ds, TableBlock tb, double startX, double top, in TableLayout tl)
    {
        if (_printMode || !ReferenceEquals(_selectedBlock, tb)) return;
        var r = new Rect(startX - 1.5, top - 1.5, tl.TableWidth + 3, tl.TotalHeight + 3);
        ds.DrawRectangle(r, BlockSelBorder, 2.5f);
    }
}
