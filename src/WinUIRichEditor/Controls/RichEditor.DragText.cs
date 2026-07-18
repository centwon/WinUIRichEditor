using System;
using Windows.Foundation;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Input;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Drag & drop of the selected text within the editor (Word convention): pressing inside the selection
// arms a drag instead of starting a new selection; moving past the click slop shows a grey drop-preview
// caret; releasing moves the content there (Ctrl held = copy). A press that never becomes a drag falls
// back to the ordinary click (collapse the caret at the press point) on release.
public partial class RichEditor
{
    private bool _dragTextArmed;        // pressed inside the selection; may become a drag or stay a click
    private bool _dragTextActive;       // past the slop: drop preview showing, release performs the drop
    private Point _dragTextStart;       // press point, doc space
    private TextPointer? _dropPreview;  // current drop position while dragging (drawn by DrawDropPreview)

    // Whether tp sits STRICTLY inside the current selection (boundary clicks place the caret instead,
    // matching Word). Cell-block selections are excluded — their "content" is a grid rectangle, not a
    // linear range this move understands.
    private bool ArmTextDragAt(TextPointer tp, Point docPt, PointerRoutedEventArgs e)
    {
        if (IsReadOnly || !HasSelection || CellBlockSelection() != null) return false;
        TextPointer s = _selStart, e2 = _selEnd;
        if (ComparePositions(s, e2) > 0) (s, e2) = (e2, s);
        if (!(ComparePositions(s, tp) < 0 && ComparePositions(tp, e2) < 0)) return false;

        _dragTextArmed = true;
        _dragTextActive = false;
        _dragTextStart = docPt;
        _dropPreview = null;
        _canvas.CapturePointer(e.Pointer);
        return true;
    }

    // Pointer move while armed: past the slop the drag activates and the drop preview follows the pointer.
    private void DragTextMoved(Point docPt)
    {
        if (!_dragTextActive)
        {
            if (Math.Abs(docPt.X - _dragTextStart.X) + Math.Abs(docPt.Y - _dragTextStart.Y) < MultiClickSlop) return;
            _dragTextActive = true;
            SetCursorShape(InputSystemCursorShape.Arrow);
        }
        _dropPreview = GetPositionFromPoint(docPt);
        InvalidateCanvas();
    }

    // Pointer release: a real drag performs the move/copy; an unmoved press is the deferred plain click.
    private void EndTextDrag(PointerRoutedEventArgs e)
    {
        bool wasDrag = _dragTextActive;
        var drop = _dropPreview;
        _dragTextArmed = false;
        _dragTextActive = false;
        _dropPreview = null;
        _canvas.ReleasePointerCapture(e.Pointer);
        SetCursorShape(InputSystemCursorShape.IBeam);

        if (!wasDrag)
        {
            // The press deferred the usual click handling (so the selection survived a potential drag);
            // do it now: caret to the click point, selection collapsed.
            var pt = ViewToDoc(e.GetCurrentPoint(_canvas).Position);
            if (GetPositionFromPoint(pt) is { Paragraph: not null } tp)
            {
                _caret = tp;
                _desiredCaretX = pt.X;
                _coalesceKey = null;
                _pendingCaretStyles = null;
                CollapseSelectionToCaret();
                RestartBlink();
                SyncIme();
                RaiseStatusChanged();
            }
            InvalidateCanvas();
            return;
        }

        if (drop?.Paragraph == null) { InvalidateCanvas(); return; }
        PerformTextDrop(drop, copy: Ctrl);
    }

    // Moves (or, with copy, duplicates) the selected content to `drop`. The move deletes the selection
    // first; a zero-length MARKER run planted at the drop point survives that deletion (empty runs are
    // neither removed by DeleteInParagraph nor merged by CoalesceRuns while their format is unique, and
    // paragraph merges move inlines rather than discard them), so the drop position is recovered after
    // the offsets shift. One undo step restores both halves.
    private void PerformTextDrop(TextPointer drop, bool copy)
    {
        if (Document == null || !HasSelection) { InvalidateCanvas(); return; }
        TextPointer s = _selStart, e = _selEnd;
        if (ComparePositions(s, e) > 0) (s, e) = (e, s);

        // Dropping into the dragged range is a no-op (Word behavior).
        if (ComparePositions(s, drop) <= 0 && ComparePositions(drop, e) <= 0) { InvalidateCanvas(); return; }

        var content = BuildSelectionDocument(new TextRange(s, e)); // cloned; independent of the edits below
        PushUndo(null);

        if (copy)
        {
            _caret = new TextPointer(drop.Paragraph, drop.Offset);
            CollapseSelectionToCaret();
            InsertDocumentAtCaret(content);
            AfterEdit();
            return;
        }

        var markerPara = drop.Paragraph!;
        var marker = new Run { Text = "", NavigateUri = "winuirich:drop-marker" }; // unique format → never coalesced
        int mIdx = SplitInlinesAt(markerPara, Math.Clamp(drop.Offset, 0, GetParagraphLength(markerPara)));
        marker.Parent = markerPara;
        markerPara.Inlines.Insert(mIdx, marker);

        new TextRange(_selStart, _selEnd).Delete();
        UpdateParents(Document);

        // Relocate the marker (its paragraph may have been merged into the selection-start paragraph).
        Paragraph? host = null;
        int hostOff = 0;
        foreach (var p in AllParagraphs())
        {
            int off = 0;
            foreach (var inl in p.Inlines)
            {
                if (ReferenceEquals(inl, marker)) { host = p; hostOff = off; break; }
                off += InlineLen(inl);
            }
            if (host != null) break;
        }
        if (host == null) { AfterEdit(); return; } // defensive: content deleted; undo restores everything
        host.Inlines.Remove(marker);
        if (host.Inlines.Count == 0) host.Inlines.Add(new Run { Text = "", Parent = host });

        _caret = new TextPointer(host, hostOff);
        CollapseSelectionToCaret();
        InsertDocumentAtCaret(content);
        AfterEdit();
    }
}
