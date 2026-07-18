using System;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Two-step table insert: the grid picker (toolbar/context menu) chooses rows × columns, then this "draw
// table" mode lets the user drag from the caret to set the table's width and height. The table inserts at
// the caret (rows×cols stay fixed by the pick); a plain click with no drag uses the default size. The
// rubber-band is anchored at the caret and shows the picked grid sized by the drag.
public partial class RichEditor
{
    private (int rows, int cols)? _pendingTableDraw;
    private Point? _tableDrawStart;   // doc space — anchored at the caret
    private Point? _tableDrawCurrent; // doc space — follows the pointer

    /// <summary>Arms "draw table" mode with the picked <paramref name="rows"/>×<paramref name="cols"/>: the
    /// next drag from the caret sets the table's width/height (a click with no drag uses the default size).</summary>
    internal void BeginTableDraw(int rows, int cols)
    {
        if (IsReadOnly || !AllowTables || rows < 1 || cols < 1) return;
        _pendingTableDraw = (rows, cols);
        _tableDrawStart = null;
        _tableDrawCurrent = null;
        SetCursorShape(InputSystemCursorShape.Cross);
    }

    private void CancelTableDraw()
    {
        if (_pendingTableDraw == null) return;
        _pendingTableDraw = null;
        _tableDrawStart = null;
        _tableDrawCurrent = null;
        SetCursorShape(InputSystemCursorShape.IBeam);
        InvalidateCanvas();
    }

    private Point ClampDocPoint(Point p)
        => new(Math.Clamp(p.X, 0, Math.Max(0, _layoutWidth)), Math.Clamp(p.Y, 0, Math.Max(0, _measuredHeight)));

    // Pointer hooks, called from the main handlers. Return true when draw mode consumed the event.
    private bool TableDrawPointerPressed(Point docPt, PointerRoutedEventArgs e)
    {
        if (_pendingTableDraw == null) return false;
        // Anchor at the caret (where the table will land); the dragged opposite corner sets the size.
        _tableDrawStart = CaretToDocPoint(_caret) is { } cp ? new Point(cp.X, cp.Y) : ClampDocPoint(docPt);
        _tableDrawCurrent = ClampDocPoint(docPt);
        _canvas.CapturePointer(e.Pointer);
        InvalidateCanvas();
        return true;
    }

    private bool TableDrawPointerMoved(Point docPt)
    {
        if (_pendingTableDraw == null) return false; // not armed
        if (_tableDrawStart != null) { _tableDrawCurrent = ClampDocPoint(docPt); InvalidateCanvas(); }
        return true; // consume all moves while armed (keep the cross cursor, skip hover/selection)
    }

    private bool TableDrawPointerReleased(PointerRoutedEventArgs e)
    {
        if (_pendingTableDraw is not { } pd || _tableDrawStart is not { } start) return false;
        var end = _tableDrawCurrent ?? start;
        _canvas.ReleasePointerCapture(e.Pointer);
        var rect = new Rect(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
                            Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y));
        _pendingTableDraw = null;
        _tableDrawStart = null;
        _tableDrawCurrent = null;
        SetCursorShape(InputSystemCursorShape.IBeam);
        // Insert at the existing caret (the drag only sizes the table — it does not move the caret).
        if (rect.Width >= 20 && rect.Height >= 16) InsertTableDrawn(pd.rows, pd.cols, rect.Width, rect.Height);
        else InsertTable(pd.rows, pd.cols); // click with no real drag -> the picked default size
        return true;
    }

    // Inserts a block table (at the caret) sized to the drawn rectangle: equal columns across totalWidth,
    // equal minimum row heights across totalHeight.
    private void InsertTableDrawn(int rows, int cols, double totalWidth, double totalHeight)
    {
        if (Document == null || IsReadOnly || !AllowTables || !CaretCanHostBlock) return;
        PushUndo(null);
        var tb = new TableBlock(rows, cols);
        double w = Math.Max(20, totalWidth / cols);
        for (int c = 0; c < tb.ColumnWidths.Count; c++) tb.ColumnWidths[c] = w;
        double rh = Math.Max(16, totalHeight / rows);
        tb.RowHeights.Clear();
        for (int r = 0; r < rows; r++) tb.RowHeights.Add(rh);
        InsertBlockAtCaret(tb);
        AfterEdit();
    }

    private static readonly Color RubberFill = Color.FromArgb(40, 0, 120, 215);
    private static readonly Color RubberStroke = Color.FromArgb(220, 0, 120, 215);
    private static readonly Color RubberGrid = Color.FromArgb(140, 0, 120, 215);
    private static readonly CanvasTextFormat RubberLabel = new() { FontSize = 12, WordWrapping = CanvasWordWrapping.NoWrap };

    // Draws the draw-table rubber-band + picked-grid preview (document space; the session carries the zoom).
    private void DrawTableDrawRubberBand(CanvasDrawingSession ds)
    {
        if (_pendingTableDraw is not { } pd || _tableDrawStart is not { } s || _tableDrawCurrent is not { } c) return;
        var rect = new Rect(Math.Min(s.X, c.X), Math.Min(s.Y, c.Y), Math.Abs(c.X - s.X), Math.Abs(c.Y - s.Y));
        ds.FillRectangle(rect, RubberFill);
        using var dash = new CanvasStrokeStyle { DashStyle = CanvasDashStyle.Dash };
        ds.DrawRectangle(rect, RubberStroke, 1.5f, dash);

        if (rect.Width >= 20 && rect.Height >= 16)
        {
            for (int col = 1; col < pd.cols; col++)
            {
                float x = (float)(rect.X + rect.Width * col / pd.cols);
                ds.DrawLine(x, (float)rect.Y, x, (float)(rect.Y + rect.Height), RubberGrid, 1f);
            }
            for (int row = 1; row < pd.rows; row++)
            {
                float y = (float)(rect.Y + rect.Height * row / pd.rows);
                ds.DrawLine((float)rect.X, y, (float)(rect.X + rect.Width), y, RubberGrid, 1f);
            }
        }
        ds.DrawText($"{pd.rows} × {pd.cols}", (float)(c.X + 8), (float)(c.Y + 4), RubberStroke, RubberLabel);
    }
}
