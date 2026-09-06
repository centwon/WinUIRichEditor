using System;
using System.Collections.Generic;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

internal readonly struct UndoState
{
    public FlowDocument Document { get; }
    public int CaretGlobalIndex { get; }
    public int CaretOffset { get; }
    /// <summary>Approximate retained size of this snapshot: its element count times a measured
    /// per-element cost (the text and any image bytes are shared with the live document, so they are not
    /// charged). Used to bound total undo memory.</summary>
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
    private const long DefaultMaxBytes = 64L * 1024 * 1024;
    private const int MinSteps = 3;

    private readonly long _maxBytes;

    /// <summary>Creates a history bounded by the default memory budget.</summary>
    public UndoManager() : this(DefaultMaxBytes) { }

    /// <summary>Creates a history with an explicit byte budget. The parameter exists for tests: the real
    /// budget is large enough that filling it needs a document too big to build in one.</summary>
    public UndoManager(long maxBytes) => _maxBytes = maxBytes > 0 ? maxBytes : DefaultMaxBytes;

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
    private void Trim(Stack<UndoState> stack)
    {
        if (stack.Count <= MinSteps) return;
        var arr = stack.ToArray(); // index 0 = newest (top)
        int keep = 0;
        long bytes = 0;
        for (int i = 0; i < arr.Length; i++)
        {
            bytes += arr[i].ApproxBytes;
            bool withinBudget = keep < MaxStackSize && (bytes <= _maxBytes || keep < MinSteps);
            if (!withinBudget) break;
            keep++;
        }
        if (keep >= arr.Length) return; // nothing to drop
        stack.Clear();
        for (int i = keep - 1; i >= 0; i--) stack.Push(arr[i]); // re-push oldest-kept first
    }

    // What one snapshot actually retains, which is NOT what this used to compute.
    //
    // The old estimate charged 2 bytes per character of text. Measured, that is wrong in BOTH
    // directions, because Clone copies the object graph and SHARES the strings: the text already
    // exists, and a snapshot adds only the elements that point at it. UndoBudgetProbeTests measured the
    // same document at 2 and at 60 characters per paragraph and got byte-for-byte the same retention
    // (899 KB per checkpoint, 3000 paragraphs) while the old estimate differed 2.3x between them.
    // Against real retention it undercharged an ordinary document ~1.5x and overcharged a text-heavy
    // one ~25x — so the budget trimmed history that cost nothing and kept history that cost a lot.
    //
    // The cost tracks the ELEMENT COUNT at a stable ~155 bytes per block/inline across every shape
    // measured (155 paragraph-dominated, 120 inline-dominated; 40000 elements retained 6037 KB).
    // Images stay excluded for the original reason: Clone reference-shares their bytes.
    //
    // Re-measure with UndoBudgetProbeTests if the model classes or the runtime change — the constant is
    // an observation, not a rule.
    private const int BytesPerElement = 155;

    internal static int EstimateBytes(FlowDocument doc) // internal: covered directly by the test suite
        => (int)System.Math.Min((long)ElementCount(doc) * BytesPerElement, int.MaxValue);

    // Blocks and inlines at any depth. A cell counts as an element itself, and an inline table's cells
    // hold real content that is deep-cloned with the snapshot — charging a flat placeholder for one made
    // a document whose content lives in inline tables look tiny, which is the exact case the budget
    // exists for. WalkParagraphs already recurses this way.
    internal static int ElementCount(FlowDocument doc)
    {
        int n = 0;
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                n++;
                if (b is Paragraph p)
                {
                    n += p.Inlines.Count;
                    foreach (var inl in p.Inlines)
                        if (inl is InlineTable it)
                            foreach (var row in it.Table.Cells)
                                foreach (var cell in row)
                                { n++; Walk(cell.Blocks); }
                }
                else if (b is TableBlock tb)
                {
                    foreach (var row in tb.Cells)
                        foreach (var cell in row)
                        { n++; Walk(cell.Blocks); }
                }
            }
        }
        Walk(doc.Blocks);
        return n;
    }

    // Document-order paragraph walk shared by GetGlobalIndex / GetPointerFromGlobalIndex. The order and
    // the numbering are BlockWalk's — non-paragraph blocks and each table itself take one index, which is
    // what keeps positions stable across snapshots. Both directions use the SAME walk so a caret parked in
    // any nested structure restores to the matching paragraph after undo/redo (an older walk only saw each
    // top-level cell's first paragraph, so nested carets snapped to the document start).
    // The visitor returns true to stop the walk.
    private static bool WalkParagraphs(IEnumerable<Block> blocks, ref int index, Func<Paragraph, int, bool> visit)
    {
        foreach (var block in BlockWalk.DocumentOrder(blocks))
        {
            if (block is Paragraph p && visit(p, index)) return true;
            index++;
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
