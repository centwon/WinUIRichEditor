using System;
using System.Collections.Generic;
using System.Linq;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Character/paragraph formatting commands (toolbar surface), list toggling with hard-line splitting.
// Ported from the Avalonia original with type swaps (IBrush→Color, FontWeight struct, TextDecorationFlags).
public partial class RichEditor
{
    // Word-style pending format: a toggle made at an empty caret applies to the next typed text.
    private List<Action<Run>>? _pendingCaretStyles;

    // ---- format painter ---------------------------------------------------
    // Character-format snapshot captured from the source selection; non-null while armed.
    private (FontWeight w, FontStyle st, TextDecorationFlags dec, double size, string? family, Color? fg, Color? bg)? _painterFmt;

    /// <summary>True while the format painter is armed (the next selection receives the captured format).</summary>
    public bool IsFormatPainterActive => _painterFmt != null;

    /// <summary>Captures character formatting from the caret/selection and arms the format painter: the
    /// next selection receives that formatting. Calling again while armed cancels (toggle).</summary>
    public void StartFormatPainter()
    {
        if (IsReadOnly) return;
        if (_painterFmt != null) { CancelFormatPainter(); return; }
        var p = HasSelection ? _selStart.Paragraph : _caret.Paragraph;
        if (p == null) return;
        int off = HasSelection ? _selStart.Offset : _caret.Offset;
        var src = RunAtOffset(p, off) ?? RunAtOffset(p, Math.Max(0, off - 1));
        if (src == null) return;
        _painterFmt = (src.FontWeight, src.FontStyle, src.TextDecorations, src.FontSize, src.FontFamily, src.Foreground, src.Background);
        RaiseStatusChanged();
    }

    /// <summary>Disarms the format painter without applying.</summary>
    public void CancelFormatPainter()
    {
        if (_painterFmt == null) return;
        _painterFmt = null;
        RaiseStatusChanged();
    }

    // Applies the captured format to the current selection (called on pointer-release while armed).
    private void ApplyFormatPainterToSelection()
    {
        if (_painterFmt is not { } f || !HasSelection) return;
        ApplyStyleToSelection(r =>
        {
            r.FontWeight = f.w; r.FontStyle = f.st; r.TextDecorations = f.dec;
            r.FontSize = f.size; r.FontFamily = f.family; r.Foreground = f.fg; r.Background = f.bg;
        });
        CancelFormatPainter();
    }

    /// <summary>Toggles bold on the current selection (or the caret word).</summary>
    public void ToggleBold() => ApplyStyleToSelection(r => r.FontWeight = r.FontWeight.IsBold() ? FontWeights.Normal : FontWeights.Bold);
    /// <summary>Toggles italic on the current selection (or the caret word).</summary>
    public void ToggleItalic() => ApplyStyleToSelection(r => r.FontStyle = r.FontStyle == FontStyle.Italic ? FontStyle.Normal : FontStyle.Italic);
    /// <summary>Toggles underline.</summary>
    public void ToggleUnderline() => ApplyStyleToSelection(r => r.TextDecorations ^= TextDecorationFlags.Underline);
    /// <summary>Toggles strikethrough.</summary>
    public void ToggleStrikethrough() => ApplyStyleToSelection(r => r.TextDecorations ^= TextDecorationFlags.Strikethrough);
    /// <summary>Sets the font size (pt) of the current selection (or the caret word).</summary>
    public void SetFontSize(double size) => ApplyStyleToSelection(r => r.FontSize = size);

    // Standard point-size ladder for the 크게/작게 (larger/smaller) commands.
    private static readonly double[] FontSizeLadder =
        { 8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 40, 48, 56, 72, 96 };

    /// <summary>Bumps the font size to the next larger step on the standard ladder (based on the caret size).</summary>
    public void IncreaseFontSize() => StepFontSize(+1);
    /// <summary>Drops the font size to the next smaller step on the standard ladder (based on the caret size).</summary>
    public void DecreaseFontSize() => StepFontSize(-1);

    private void StepFontSize(int dir)
    {
        double cur = GetCaretFormat().FontSize;
        double target;
        if (dir > 0)
        {
            target = FontSizeLadder[^1];
            foreach (var v in FontSizeLadder) if (v > cur + 0.01) { target = v; break; }
        }
        else
        {
            target = FontSizeLadder[0];
            for (int i = FontSizeLadder.Length - 1; i >= 0; i--) if (FontSizeLadder[i] < cur - 0.01) { target = FontSizeLadder[i]; break; }
        }
        SetFontSize(target);
    }
    /// <summary>Sets the foreground color; pass <see langword="null"/> to clear back to the automatic
    /// default (<see cref="TextForeground"/> — matters for dark-theme hosts, where a stamped explicit
    /// black would vanish against the background).</summary>
    public void SetForeground(Color? color) => ApplyStyleToSelection(r => r.Foreground = color);
    /// <summary>Sets the font family.</summary>
    public void SetRunFontFamily(string family) => ApplyStyleToSelection(r => r.FontFamily = family);
    /// <summary>Sets the highlight (background) color; pass <see langword="null"/> to clear.</summary>
    public void SetHighlight(Color? color) => ApplyStyleToSelection(r => r.Background = color);

    /// <summary>Clears character formatting on the current selection (or caret word).</summary>
    public void ClearFormatting() => ApplyStyleToSelection(r =>
    {
        r.FontWeight = FontWeights.Normal;
        r.FontStyle = FontStyle.Normal;
        r.FontSize = DefaultFontSize;
        r.Foreground = null;
        r.Background = null;
        r.FontFamily = null;
        r.TextDecorations = TextDecorationFlags.None;
        r.NavigateUri = null;
    });

    /// <summary>Sets the text alignment of the selected paragraphs (or the caret paragraph). Applies to
    /// every paragraph in a table-cell block selection.</summary>
    public void SetTextAlignment(TextAlignment align)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) p.TextAlignment = align;
        AfterFormat();
    }

    /// <summary>Sets the heading level (1–6 = h1–h6, 0 = body) of the selected paragraphs (or the caret
    /// paragraph). Applies to every paragraph in a table-cell block selection.</summary>
    public void SetHeading(int level)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) p.HeadingLevel = level;
        AfterFormat();
    }

    /// <summary>Sets a proportional line spacing (e.g. 1.0 single, 1.5, 2.0) on the paragraphs in the
    /// selection; scales with font size. Pass <see cref="double.NaN"/> to clear back to natural spacing.</summary>
    public void SetLineSpacing(double spacing)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) { p.LineSpacing = spacing; p.LineHeight = double.NaN; }
        AfterFormat();
    }

    /// <summary>Sets an absolute line height in DIPs on the paragraphs in the selection. Pass
    /// <see cref="double.NaN"/> to clear back to natural spacing.</summary>
    public void SetLineHeight(double height)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) { p.LineHeight = height; p.LineSpacing = double.NaN; }
        AfterFormat();
    }

    // The paragraphs the current selection intersects (or the caret paragraph when collapsed). A
    // rectangular table-cell block selection yields every paragraph in the selected cells (the same cells
    // the renderer tints), not the row-major linear span — so paragraph formatting matches the highlight.
    private System.Collections.Generic.IEnumerable<Paragraph> SelectedParagraphs()
    {
        if (CellBlockSelection() is { } cb)
        {
            foreach (var (r, c, cell) in cb.tb.LogicalCells())
                if (r >= cb.r0 && r <= cb.r1 && c >= cb.c0 && c <= cb.c1)
                    foreach (var p in ParagraphsInBlocks(cell.Blocks))
                        yield return p;
            yield break;
        }
        if (!HasSelection) { if (_caret.Paragraph != null) yield return _caret.Paragraph; yield break; }
        TextPointer s = _selStart, e = _selEnd;
        if (s.CompareTo(e) > 0) (s, e) = (e, s);
        bool inRange = false;
        foreach (var p in AllParagraphs())
        {
            if (ReferenceEquals(p, s.Paragraph)) inRange = true;
            if (inRange) yield return p;
            if (ReferenceEquals(p, e.Paragraph)) break;
        }
    }

    /// <summary>Toggles blockquote styling on the selected paragraphs (or the caret paragraph). When the
    /// selection mixes quoted and unquoted paragraphs, the toggle turns them all on (only clears when every
    /// paragraph is already quoted). Applies across a table-cell block selection.</summary>
    public void ToggleQuote()
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        var paras = SelectedParagraphs().ToList();
        if (paras.Count == 0) return;
        PushUndo(null);
        bool allQuote = paras.All(p => p.IsQuote);
        foreach (var p in paras) p.IsQuote = !allQuote;
        AfterFormat();
    }

    /// <summary>Adjusts the indent of the selected paragraphs (or the caret paragraph) by
    /// <paramref name="delta"/> px (clamped 0–400). Applies across a table-cell block selection.</summary>
    public void Indent(double delta)
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs()) p.Indent = Math.Clamp(p.Indent + delta, 0, 400);
        AfterFormat();
    }

    /// <summary>Toggles a bullet list on the selected paragraphs.</summary>
    public void ToggleBullet() => SetListType(ListKind.Bullet);
    /// <summary>Toggles a numbered list on the selected paragraphs.</summary>
    public void ToggleNumbering() => SetListType(ListKind.Ordered);

    /// <summary>Applies a specific list marker style to the selected paragraphs, turning the matching list
    /// kind on (bullet glyphs vs number formats). Unlike the toggles, a style pick never turns the list off.</summary>
    public void SetListStyle(ListMarkerStyle style) => SetListType(ListMarkerStyleKind(style), style);

    /// <summary>Removes any list attribute — bullet or numbering, including the marker style and nesting
    /// level — from the selected paragraphs (or the caret paragraph). The explicit counterpart to the
    /// toggles, which only turn off the matching kind of the caret paragraph. Applies across a
    /// table-cell block selection.</summary>
    public void RemoveList()
    {
        if (_caret.Paragraph == null || IsReadOnly) return;
        PushUndo(null);
        foreach (var p in SelectedParagraphs())
        {
            p.ListType = ListKind.None;
            p.ListMarker = ListMarkerStyle.Default;
            p.ListLevel = 0;
        }
        AfterFormat();
    }

    // The list kind a marker style belongs to (number formats -> Ordered, everything else -> Bullet).
    private static ListKind ListMarkerStyleKind(ListMarkerStyle s) => s switch
    {
        ListMarkerStyle.Decimal or ListMarkerStyle.DecimalParen or ListMarkerStyle.LowerAlpha
            or ListMarkerStyle.UpperAlpha or ListMarkerStyle.LowerRoman => ListKind.Ordered,
        _ => ListKind.Bullet,
    };

    private void SetListType(ListKind kind, ListMarkerStyle? marker = null)
    {
        if (_caret.Paragraph == null || Document == null || IsReadOnly) return;
        PushUndo(null);
        bool turningOff = marker == null && _caret.Paragraph.ListType == kind;
        void ApplyMarker(Paragraph par) { if (marker.HasValue) par.ListMarker = marker.Value; }

        // Table-cell block selection: set the list flag directly on every selected cell paragraph. The
        // top-level newline-splitting/reindexing below only applies to Document.Blocks paragraphs; a cell
        // paragraph's soft line breaks stay one list item. Toggle is group-based (turn all off only when
        // every selected paragraph already has this list kind).
        if (CellBlockSelection() != null)
        {
            var cellParas = SelectedParagraphs().ToList();
            bool off = marker == null && cellParas.Count > 0 && cellParas.All(p => p.ListType == kind);
            foreach (var p in cellParas) { p.ListType = off ? ListKind.None : kind; ApplyMarker(p); }
            UpdateParents(Document);
            AfterFormat();
            return;
        }

        var targets = SelectedTopLevelParagraphs();
        if (targets.Count == 0)
        {
            _caret.Paragraph.ListType = turningOff ? ListKind.None : kind;
            ApplyMarker(_caret.Paragraph);
            AfterFormat();
            return;
        }
        if (turningOff)
        {
            foreach (var tp in targets) tp.ListType = ListKind.None;
            UpdateParents(Document);
            AfterFormat();
            return;
        }

        var ssP = _selStart.Paragraph; int ssO = _selStart.Offset;
        var seP = _selEnd.Paragraph; int seO = _selEnd.Offset;
        var cpP = _caret.Paragraph; int cpO = _caret.Offset;
        TextPointer? nSs = null, nSe = null, nCp = null;

        (Paragraph, int) MapInto(List<Paragraph> items, Paragraph tp, int off)
        {
            string plain = BuildPlain(tp);
            int line = 0, lineStart = 0, lim = Math.Min(off, plain.Length);
            for (int i = 0; i < lim; i++) if (plain[i] == '\n') { line++; lineStart = i + 1; }
            var it = items[Math.Min(line, items.Count - 1)];
            return (it, Math.Min(off - lineStart, GetParagraphLength(it)));
        }

        foreach (var tp in targets.OrderByDescending(t => Document.Blocks.IndexOf(t)))
        {
            int idx = Document.Blocks.IndexOf(tp);
            if (idx < 0) { tp.ListType = kind; ApplyMarker(tp); continue; }
            var items = SplitByNewlines(tp);
            foreach (var it in items) { it.ListType = kind; ApplyMarker(it); it.Parent = Document; }
            Document.Blocks.RemoveAt(idx);
            for (int k = 0; k < items.Count; k++) Document.Blocks.Insert(idx + k, items[k]);
            if (tp == ssP) { var (p2, o2) = MapInto(items, tp, ssO); nSs = new TextPointer(p2, o2); }
            if (tp == seP) { var (p2, o2) = MapInto(items, tp, seO); nSe = new TextPointer(p2, o2); }
            if (tp == cpP) { var (p2, o2) = MapInto(items, tp, cpO); nCp = new TextPointer(p2, o2); }
        }
        if (nSs != null) _selStart = nSs;
        if (nSe != null) _selEnd = nSe;
        if (nCp != null) _caret = nCp;
        UpdateParents(Document);
        AfterFormat();
    }

    private List<Paragraph> SelectedTopLevelParagraphs()
    {
        var result = new List<Paragraph>();
        if (Document == null) return result;
        var all = AllParagraphs();
        int si = _selStart.Paragraph != null ? all.IndexOf(_selStart.Paragraph) : -1;
        int ei = _selEnd.Paragraph != null ? all.IndexOf(_selEnd.Paragraph) : -1;
        if (si < 0 || ei < 0)
        {
            if (_caret.Paragraph != null && Document.Blocks.Contains(_caret.Paragraph))
                result.Add(_caret.Paragraph);
            return result;
        }
        if (si > ei) (si, ei) = (ei, si);
        for (int i = si; i <= ei; i++)
            if (Document.Blocks.Contains(all[i])) result.Add(all[i]);
        return result;
    }

    private List<Paragraph> SplitByNewlines(Paragraph p)
    {
        var result = new List<Paragraph>();
        Paragraph NewPara() => new Paragraph
        {
            ListType = p.ListType,
            ListLevel = p.ListLevel,
            Indent = p.Indent,
            TextAlignment = p.TextAlignment,
            Background = p.Background
        };
        var cur = NewPara();
        foreach (var inl in p.Inlines)
        {
            if (inl is Run run && run.Text != null && run.Text.Contains('\n'))
            {
                var parts = run.Text.Split('\n');
                for (int k = 0; k < parts.Length; k++)
                {
                    if (k > 0) { result.Add(cur); cur = NewPara(); }
                    if (parts[k].Length > 0)
                    {
                        var nr = (Run)run.Clone();
                        nr.Text = parts[k];
                        nr.Parent = cur;
                        cur.Inlines.Add(nr);
                    }
                }
            }
            else
            {
                var c = (Inline)inl.Clone();
                c.Parent = cur;
                cur.Inlines.Add(c);
            }
        }
        result.Add(cur);
        foreach (var pp in result)
            if (pp.Inlines.Count == 0) pp.Inlines.Add(new Run { Text = "" });
        return result;
    }

    private void ApplyStyleToSelection(Action<Run> styleAction)
    {
        if (IsReadOnly) return;
        // A rectangular table-cell block selection (drag across cells): style every selected cell in
        // full. The linear TextRange path below only partially styles the first/last cell (from/to the
        // drag offsets) and, because it walks cells in row-major order, bleeds into cells outside the
        // selected column band — so multi-cell formatting wouldn't match the highlighted rectangle.
        if (CellBlockSelection() is { } cb)
        {
            PushUndo(null);
            ApplyStyleToCellRange(cb.tb, cb.r0, cb.c0, cb.r1, cb.c1, styleAction);
            AfterFormat();
            return;
        }
        if (HasSelection)
        {
            PushUndo(null);
            new TextRange(_selStart, _selEnd).ApplyPropertyValue(styleAction);
        }
        else if (_caret.Paragraph is { } p)
        {
            string plain = BuildPlain(p);
            int off = Math.Clamp(_caret.Offset, 0, plain.Length);
            static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
            bool inWord = (off < plain.Length && IsWord(plain[off])) || (off > 0 && IsWord(plain[off - 1]));
            if (inWord)
            {
                var (ws, we) = WordBoundsAt(plain, off);
                PushUndo(null);
                new TextRange(new TextPointer(p, ws), new TextPointer(p, we)).ApplyPropertyValue(styleAction);
            }
            else
            {
                // Pending: applies to the next typed text (the undo checkpoint comes with that typing).
                (_pendingCaretStyles ??= new List<Action<Run>>()).Add(styleAction);
            }
        }
        AfterFormat();
    }

    // Applies a character style to every logical cell whose anchor lies inside the selected rectangle
    // [r0..r1]×[c0..c1] — the same cells the renderer tints (DrawNestedTable) — styling each cell's
    // paragraphs in full (nested content included). Empty placeholder runs are styled directly so text
    // typed next in a selected empty cell inherits the format.
    private void ApplyStyleToCellRange(TableBlock tb, int r0, int c0, int r1, int c1, Action<Run> styleAction)
    {
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            if (r < r0 || r > r1 || c < c0 || c > c1) continue;
            foreach (var p in ParagraphsInBlocks(cell.Blocks))
            {
                int len = GetParagraphLength(p);
                if (len > 0)
                    new TextRange(new TextPointer(p, 0), new TextPointer(p, len)).ApplyPropertyValue(styleAction);
                else
                    foreach (var inl in p.Inlines) if (inl is Run run) styleAction(run);
            }
        }
    }

    private static (int start, int end) WordBoundsAt(string s, int off)
    {
        static bool IsWord(char ch) => char.IsLetterOrDigit(ch) || ch == '_';
        int a = Math.Clamp(off, 0, s.Length), b = a;
        while (a > 0 && IsWord(s[a - 1])) a--;
        while (b < s.Length && IsWord(s[b])) b++;
        return (a, b);
    }

    // Applies any pending caret styles to the just-inserted [start, start+len) range. During an IME
    // composition the styles must survive (each TextUpdating replaces the composed range, so they are
    // re-applied per update and only cleared when the composition commits) — pass clear: false there.
    private void ApplyPendingStyles(Paragraph p, int start, int len, bool clear = true)
    {
        if (_pendingCaretStyles is not { Count: > 0 } pend || len <= 0) return;
        if (clear) _pendingCaretStyles = null;
        new TextRange(new TextPointer(p, start), new TextPointer(p, start + len))
            .ApplyPropertyValue(r => { foreach (var a in pend) a(r); });
    }

    // After a formatting change: re-measure (size/heading/list change heights) and repaint.
    private void AfterFormat()
    {
        _coalesceKey = null;
        InvalidateCaretTableMeasure();
        RelayoutToViewport();
        SyncIme();
        RestartBlink();
        RaiseStatusChanged();
    }
}
