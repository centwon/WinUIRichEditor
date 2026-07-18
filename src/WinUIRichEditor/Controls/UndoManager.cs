using System;
using System.Collections.Generic;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

internal readonly struct UndoState
{
    public FlowDocument Document { get; }
    public int CaretGlobalIndex { get; }
    public int CaretOffset { get; }
    /// <summary>Approximate retained size of this snapshot's text (image bytes are reference-shared by
    /// Clone, so they're excluded). Used to bound total undo memory.</summary>
    public int ApproxBytes { get; }

    public UndoState(FlowDocument document, int caretGlobalIndex, int caretOffset, int approxBytes)
    {
        Document = document;
        CaretGlobalIndex = caretGlobalIndex;
        CaretOffset = caretOffset;
        ApproxBytes = approxBytes;
    }
}

// Snapshot-based undo/redo: each entry is a deep clone of the document plus a paragraph-granular caret
// index. Ported verbatim from the Avalonia original (pure model logic).
internal class UndoManager
{
    private readonly Stack<UndoState> _undoStack = new();
    private readonly Stack<UndoState> _redoStack = new();

    public bool CanUndo => _undoStack.Count > 0;
    public bool CanRedo => _redoStack.Count > 0;

    public void Clear()
    {
        _undoStack.Clear();
        _redoStack.Clear();
    }

    // History is bounded by BOTH a step count and an approximate memory budget. Small documents keep the
    // full MaxStackSize steps; large documents (each snapshot is a full deep clone) are trimmed sooner so
    // undo history can't dominate memory — while always keeping at least MinSteps so undo stays useful.
    private const int MaxStackSize = 50;
    private const long MaxBytes = 64L * 1024 * 1024; // ~64MB of text snapshots
    private const int MinSteps = 3;

    public void PushState(FlowDocument? currentDoc, TextPointer? currentCaret)
    {
        if (currentDoc == null || currentCaret == null || currentCaret.Paragraph == null) return;

        int caretGlobal = GetGlobalIndex(currentDoc, currentCaret);
        var clonedDoc = currentDoc.Clone();

        _undoStack.Push(new UndoState(clonedDoc, caretGlobal, currentCaret.Offset, EstimateBytes(clonedDoc)));
        Trim(_undoStack);
        _redoStack.Clear();
    }

    public UndoState? Undo(FlowDocument currentDoc, TextPointer currentCaret)
    {
        if (_undoStack.Count == 0) return null;
        int caretGlobal = GetGlobalIndex(currentDoc, currentCaret);
        var clone = currentDoc.Clone();
        _redoStack.Push(new UndoState(clone, caretGlobal, currentCaret.Offset, EstimateBytes(clone)));
        Trim(_redoStack);
        return _undoStack.Pop();
    }

    public UndoState? Redo(FlowDocument currentDoc, TextPointer currentCaret)
    {
        if (_redoStack.Count == 0) return null;
        int caretGlobal = GetGlobalIndex(currentDoc, currentCaret);
        var clone = currentDoc.Clone();
        _undoStack.Push(new UndoState(clone, caretGlobal, currentCaret.Offset, EstimateBytes(clone)));
        Trim(_undoStack);
        return _redoStack.Pop();
    }

    // Keeps the newest states within both the count and byte budgets (but never fewer than MinSteps).
    private static void Trim(Stack<UndoState> stack)
    {
        if (stack.Count <= MinSteps) return;
        var arr = stack.ToArray(); // index 0 = newest (top)
        int keep = 0;
        long bytes = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            bytes += arr[i].ApproxBytes;
            bool withinBudget = keep < MaxStackSize && (bytes <= MaxBytes || keep < MinSteps);
            if (!withinBudget) break;
            keep++;
        }
        if (keep >= arr.Length) return; // nothing to drop
        stack.Clear();
        for (int i = keep - 1; i >= 0; i--) stack.Push(arr[i]); // re-push oldest-kept first
    }

    // Approximate retained text size of a snapshot (UTF-16 chars + small per-element overhead). Image
    // RawBytes are excluded because Clone reference-shares them, so they don't grow with history depth.
    private static int EstimateBytes(FlowDocument doc)
    {
        long total = 0;
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                total += 48; // per-block overhead
                if (b is Paragraph p)
                {
                    foreach (var inl in p.Inlines)
                    {
                        if (inl is Run r) total += 40 + (long)(r.Text?.Length ?? 0) * 2;
                        else total += 24; // inline image/table placeholder (image bytes are shared)
                    }
                }
                else if (b is TableBlock tb)
                {
                    foreach (var row in tb.Cells)
                        foreach (var cell in row)
                            Walk(cell.Blocks);
                }
            }
        }
        Walk(doc.Blocks);
        return (int)System.Math.Min(total, int.MaxValue);
    }

    // Document-order paragraph walk shared by GetGlobalIndex / GetPointerFromGlobalIndex — fully
    // recursive through table cells (logical/anchor cells), nested tables and inline tables, mirroring
    // the editor's caret paragraph order. Both directions use the SAME walk so a caret parked in any
    // nested structure restores to the matching paragraph after undo/redo (the old walk only saw each
    // top-level cell's first paragraph, so nested carets snapped to the document start). Non-paragraph
    // blocks and each table itself still take one index, keeping positions stable across snapshots.
    // The visitor returns true to stop the walk.
    private static bool WalkParagraphs(IEnumerable<Block> blocks, ref int index, Func<Paragraph, int, bool> visit)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph p)
            {
                if (visit(p, index)) return true;
                index++;
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            if (WalkParagraphs(cell.Blocks, ref index, visit)) return true;
            }
            else if (block is TableBlock tb)
            {
                index++;
                foreach (var (_, _, cell) in tb.LogicalCells())
                    if (WalkParagraphs(cell.Blocks, ref index, visit)) return true;
            }
            else index++;
        }
        return false;
    }

    public TextPointer GetPointerFromGlobalIndex(FlowDocument doc, int index)
    {
        if (doc.Blocks.Count == 0) return new TextPointer(null, 0);
        Paragraph? found = null, lastPara = null;
        int i = 0;
        WalkParagraphs(doc.Blocks, ref i, (p, idx) =>
        {
            lastPara = p;
            if (idx == index) { found = p; return true; }
            return false;
        });
        return new TextPointer(found ?? lastPara, 0);
    }

    private int GetGlobalIndex(FlowDocument doc, TextPointer pointer)
    {
        int result = 0, i = 0;
        bool found = WalkParagraphs(doc.Blocks, ref i, (p, idx) =>
        {
            if (ReferenceEquals(p, pointer.Paragraph)) { result = idx; return true; }
            return false;
        });
        return found ? result : 0;
    }
}
