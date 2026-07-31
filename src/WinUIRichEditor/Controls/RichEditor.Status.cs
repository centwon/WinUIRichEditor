using System;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Caret-format + document-status API for toolbar/status-bar reflection. StatusChanged fires after
// edits, formatting changes, and caret/selection moves so chrome can refresh.
public partial class RichEditor
{
    /// <summary>Raised after the caret moves, the selection changes, or the document is edited, so a
    /// toolbar/status bar can refresh its state. Coarse signal — prefer <see cref="TextChanged"/> or
    /// <see cref="SelectionChanged"/> for new code.</summary>
    public event EventHandler? StatusChanged;

    /// <summary>Raised after the document's text, structure, or formatting is modified.</summary>
    public event EventHandler? TextChanged;

    /// <summary>Raised when the caret position or the selected range changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Raised when the <see cref="Document"/> is replaced with a different instance.</summary>
    public event EventHandler? DocumentChanged;

    // Set by any mutation (PushUndo) or a wholesale Document swap; cleared when flushed as TextChanged.
    private bool _textChangedPending;
    private void MarkTextChanged()
    {
        _textChangedPending = true;
        _paraIndexMap = null;    // a mutation may reorder/add/remove paragraphs (see ComparePositions)
        InvalidateBlockLayout(); // block heights/positions may change (see EnsureBlockLayout)
        SetModified(true);
    }

    /// <summary>True when the document has been modified since it was loaded (or since
    /// <see cref="MarkSaved"/>). Undo/redo count as modifications — the flag is a "needs saving" hint,
    /// not a content diff against the saved state.</summary>
    public bool IsModified { get; private set; }

    /// <summary>Raised when <see cref="IsModified"/> changes.</summary>
    public event EventHandler? IsModifiedChanged;

    /// <summary>Clears the modified flag; call after persisting the document.</summary>
    public void MarkSaved() => SetModified(false);

    private void SetModified(bool value)
    {
        if (IsModified == value) return;
        IsModified = value;
        IsModifiedChanged?.Invoke(this, EventArgs.Empty);
    }

    // Last-seen caret/selection, so SelectionChanged fires only on real movement (not on every repaint).
    private (Paragraph? cp, int co, Paragraph? ss, int so, Paragraph? se, int eo) _selSnapshot;
    private bool SelectionMovedSinceLastSnapshot()
    {
        var now = (_caret.Paragraph, _caret.Offset, _selStart.Paragraph, _selStart.Offset,
                   _selEnd.Paragraph, _selEnd.Offset);
        if (now == _selSnapshot) return false;
        _selSnapshot = now;
        return true;
    }

    // The notification hub. Always fires StatusChanged; additionally flushes a pending TextChanged and,
    // when the caret/selection actually moved, SelectionChanged. Callers reach this off the render stack
    // (input handlers, AfterEdit), so handlers see the already-mutated document.
    private void RaiseStatusChanged()
    {
        StatusChanged?.Invoke(this, EventArgs.Empty);
        if (_textChangedPending)
        {
            _textChangedPending = false;
            TextChanged?.Invoke(this, EventArgs.Empty);
        }
        if (SelectionMovedSinceLastSnapshot())
            SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    // Fired from the Document DP callback when the whole document instance is replaced.
    private void RaiseDocumentChanged() => DocumentChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Snapshot of the formatting at the caret position, for toolbar state reflection.</summary>
    public readonly record struct CaretFormat(bool Bold, bool Italic, bool Underline, bool Strike,
        double FontSize, string? FontFamily, TextAlignment Align, ListKind List, int Heading,
        Color? Foreground = null, Color? Background = null, bool Quote = false, double LineSpacing = double.NaN,
        ListMarkerStyle ListMarker = ListMarkerStyle.Default);

    // The Run covering logical offset within p (the run just before a boundary offset), or null.
    private static Run? RunAtOffset(Paragraph p, int offset)
    {
        int pos = 0;
        Run? last = null;
        foreach (var inl in p.Inlines)
        {
            int len = InlineLen(inl);
            if (inl is Run r)
            {
                last = r;
                if (offset >= pos && offset < pos + len) return r;
            }
            pos += len;
        }
        return last;
    }

    /// <summary>Returns the formatting snapshot at the current caret position for toolbar state display.</summary>
    public CaretFormat GetCaretFormat()
    {
        var p = _caret.Paragraph;
        Run? run = null;
        if (p != null)
        {
            run = RunAtOffset(p, _caret.Offset > 0 ? _caret.Offset - 1 : 0);
            if (run == null) foreach (var inl in p.Inlines) if (inl is Run r0) { run = r0; break; }
        }
        // A pending caret format (toggle at an empty position) previews on a clone so the document stays untouched.
        if (_pendingCaretStyles is { Count: > 0 } pend)
        {
            var probe = run != null ? (Run)run.Clone() : new Run();
            foreach (var a in pend) a(probe);
            run = probe;
        }
        bool underline = run != null && (run.TextDecorations.HasFlag(TextDecorationFlags.Underline) || !string.IsNullOrEmpty(run.NavigateUri));
        bool strike = run != null && run.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough);
        return new CaretFormat(
            run?.FontWeight.IsBold() ?? false,
            run?.FontStyle == FontStyle.Italic,
            underline,
            strike,
            run != null && run.FontSize > 0 ? run.FontSize : BodyFontSizePt,
            run?.FontFamily,
            p?.TextAlignment ?? TextAlignment.Left,
            p?.ListType ?? ListKind.None,
            p?.HeadingLevel ?? 0,
            run?.Foreground,
            run?.Background,
            p?.IsQuote ?? false,
            p?.LineSpacing ?? double.NaN,
            p?.ListMarker ?? ListMarkerStyle.Default);
    }

    // ---- automation text bridge (TextPattern in RichEditorAutomationPeer) --------------------------
    // The document as one flat string (paragraphs joined with '\n', matching GetPlainText's logical
    // form before CRLF normalization) plus global-offset <-> (paragraph, local offset) conversion, so
    // the automation ITextRangeProvider can work in simple integer ranges.

    internal string AutomationGetText()
    {
        var sb = new System.Text.StringBuilder();
        bool first = true;
        foreach (var p in AllParagraphs())
        {
            if (!first) sb.Append('\n');
            first = false;
            sb.Append(BuildPlain(p));
        }
        return sb.ToString();
    }

    // The current selection (collapsed caret when none) as global offsets into AutomationGetText().
    internal (int start, int end) AutomationGetSelection()
    {
        int s = AutomationOffsetOf(_selStart), e = AutomationOffsetOf(_selEnd);
        if (s < 0 || e < 0) { int c = Math.Max(0, AutomationOffsetOf(_caret)); return (c, c); }
        return s <= e ? (s, e) : (e, s);
    }

    private int AutomationOffsetOf(TextPointer tp)
    {
        if (tp.Paragraph == null) return -1;
        int pos = 0;
        foreach (var p in AllParagraphs())
        {
            int len = GetParagraphLength(p);
            if (ReferenceEquals(p, tp.Paragraph)) return pos + Math.Clamp(tp.Offset, 0, len);
            pos += len + 1; // '\n'
        }
        return -1;
    }

    private TextPointer AutomationPointerAt(int offset)
    {
        int pos = 0;
        Paragraph? last = null;
        foreach (var p in AllParagraphs())
        {
            int len = GetParagraphLength(p);
            if (offset <= pos + len) return new TextPointer(p, Math.Clamp(offset - pos, 0, len));
            pos += len + 1;
            last = p;
        }
        return new TextPointer(last, last != null ? GetParagraphLength(last) : 0);
    }

    // Selects [start, end) (global offsets) and scrolls the caret end into view (assistive tech drives this).
    internal void AutomationSelect(int start, int end)
    {
        if (Document == null) return;
        if (end < start) (start, end) = (end, start);
        _selStart = AutomationPointerAt(start);
        _selEnd = AutomationPointerAt(end);
        _caret = new TextPointer(_selEnd.Paragraph, _selEnd.Offset);
        ScrollCaretIntoView();
        InvalidateCanvas();
        RaiseStatusChanged();
    }

    // ---- automation geometry (TextPattern bounding rects / point hit) ------------------------------

    // The document-space origin of a paragraph's text layout — top-level or nested in (inline-)table
    // cells — derived from the caret geometry at offset 0: doc caret − in-layout caret = layout origin.
    // Reuses CaretToDocPoint's full descent instead of duplicating the cell walk.
    private (double x, double y)? ParagraphDocOrigin(Paragraph p)
    {
        if (CaretToDocPoint(new TextPointer(p, 0)) is not { } c) return null;
        var layout = BuildTextLayout(p, ParagraphWrapWidth(p));
        var (cx, cy, _, _, _) = CaretInLayout(layout, p, 0, false);
        return (c.X - cx, c.Y - cy);
    }

    /// <summary>Physical-screen bounding rectangles (flattened L,T,W,H quads) for a global-offset range
    /// of the automation text — one rect per rendered line fragment. Capped so a document-wide range
    /// doesn't build layouts for thousands of paragraphs.</summary>
    internal double[] AutomationBoundingRects(int start, int end)
    {
        if (Document == null || end <= start) return System.Array.Empty<double>();
        var result = new System.Collections.Generic.List<double>();
        const int maxRects = 256;
        int pos = 0;
        try
        {
            foreach (var p in AllParagraphs())
            {
                int len = GetParagraphLength(p);
                int ls = System.Math.Max(start - pos, 0), le = System.Math.Min(end - pos, len);
                if (le > ls && ParagraphDocOrigin(p) is { } o)
                {
                    var layout = BuildTextLayout(p, ParagraphWrapWidth(p));
                    foreach (var r in layout.GetCharacterRegions(ls, le - ls))
                    {
                        var lb = r.LayoutBounds;
                        var sr = DocRectToScreen(new Windows.Foundation.Rect(o.x + lb.X, o.y + lb.Y, lb.Width, lb.Height));
                        result.Add(sr.X); result.Add(sr.Y); result.Add(sr.Width); result.Add(sr.Height);
                        if (result.Count >= maxRects * 4) return result.ToArray();
                    }
                }
                pos += len + 1; // '\n'
                if (pos > end) break;
            }
        }
        catch { /* partial rects are still useful to assistive tech */ }
        return result.ToArray();
    }

    /// <summary>The automation-text offset nearest a physical screen point (RangeFromPoint).</summary>
    internal int AutomationOffsetAtScreenPoint(Windows.Foundation.Point screen)
    {
        try
        {
            if (ScreenPointToView(screen) is { } view
                && GetPositionFromPoint(ViewToDoc(view)) is { Paragraph: not null } tp)
                return System.Math.Max(0, AutomationOffsetOf(tp));
        }
        catch { }
        return 0;
    }

    private static bool IsSepChar(char ch) => ch == ' ' || ch == '\n' || ch == '\t' || ch == '\r';

    // Per-paragraph character/word/hard-line statistics, cached by content signature. GetStatus runs on
    // every StatusChanged (i.e. per keystroke via the status bar), and rescanning every character made
    // typing O(document chars); with this cache only the edited paragraph rescans. Words never span
    // paragraphs (the separator '\n' breaks them), so per-paragraph counts sum exactly.
    private readonly System.Collections.Generic.Dictionary<Paragraph, (long sig, int chars, int words, int breaks)> _statsCache = new();

    private (int chars, int words, int breaks) ParagraphStats(Paragraph p)
    {
        long sig = ParagraphSig(p);
        if (_statsCache.TryGetValue(p, out var hit) && hit.sig == sig)
            return (hit.chars, hit.words, hit.breaks);

        int chars = 0, words = 0, breaks = 0;
        bool inWord = false;
        void Consume(char ch)
        {
            if (ch == '\n') { breaks++; inWord = false; }
            else { chars++; if (IsSepChar(ch)) inWord = false; else { if (!inWord) words++; inWord = true; } }
        }
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && r.Text is { } t) { foreach (char ch in t) Consume(ch); }
            else if (inline is not Run) Consume(ObjChar);
        }

        if (_statsCache.Count > 100000) _statsCache.Clear(); // guard against pathological growth
        _statsCache[p] = (sig, chars, words, breaks);
        return (chars, words, breaks);
    }

    /// <summary>Returns document statistics: total character count, word count, and the caret's
    /// 1-based (line, column) position. Inline images count as one character.</summary>
    public (int chars, int words, int line, int col) GetStatus()
    {
        var doc = Document;
        if (doc == null) return (0, 0, 1, 1);

        int chars = 0, words = 0;
        int capLine = 1, capCol = 1;
        bool captured = false, firstPara = true;
        int lineAtParaStart = 1; // the global line number of the current paragraph's first hard line

        // Lazy enumeration — this runs on every StatusChanged (per keystroke via the status bar), and
        // AllParagraphs() materialized a fresh List of the whole document each time.
        foreach (var p in ParagraphsInBlocks(doc.Blocks))
        {
            if (!firstPara) lineAtParaStart++; // the '\n' separator between paragraphs
            firstPara = false;

            var st = ParagraphStats(p);

            // Only the CARET paragraph is scanned character-by-character (for line/col); all others
            // contribute their cached totals.
            if (!captured && ReferenceEquals(p, _caret.Paragraph))
            {
                int caretOff = Math.Clamp(_caret.Offset, 0, GetParagraphLength(p));
                int local = 0, lineInPara = 0, colChars = 0;
                bool done = false;
                void Scan(char ch)
                {
                    if (local == caretOff) { done = true; return; }
                    if (ch == '\n') { lineInPara++; colChars = 0; } else colChars++;
                    local++;
                }
                foreach (var inline in p.Inlines)
                {
                    if (inline is Run r && r.Text is { } t) { foreach (char ch in t) { Scan(ch); if (done) break; } }
                    else if (inline is not Run) Scan(ObjChar);
                    if (done) break;
                }
                capLine = lineAtParaStart + lineInPara;
                capCol = colChars + 1;
                captured = true;
            }

            chars += st.chars;
            words += st.words;
            lineAtParaStart += st.breaks;
        }

        return (chars, words, captured ? capLine : 1, captured ? capCol : 1);
    }
}
