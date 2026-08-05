using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Formatters;

/// <summary>
/// Parses a practical subset of RTF — the "Rich Text Format" both Word and the Korean HWP put on
/// the clipboard — into a <see cref="FlowDocument"/>: paragraphs, bold/italic/underline/strike,
/// font size, foreground colour, embedded images (<c>\pict</c> PNG/JPEG, bytes carried inline), and
/// tables (<c>\trowd</c>…<c>\cell</c>…<c>\row</c>) including merged cells, per-cell shading and
/// tables nested in a cell. Zero external dependencies beyond a code-page provider for CJK text
/// (<c>\'hh</c> bytes are decoded with the document's <c>\ansicpg</c>).
/// <para>A horizontal merge is written GEOMETRICALLY — the merged span is one cell whose <c>\cellx</c>
/// sits at its right edge — because HWP dissolves the flag form (<c>\clmgf</c>/<c>\clmrg</c>) that Word
/// also accepts. Reading handles both. Vertical merge has no geometric encoding and keeps the flags.</para>
/// <para>Known losses: a nested table's column widths come back at the default (they live in the
/// ignorable <c>{\*\nesttableprops}</c> group), and a table whose every row is merged identically reads
/// back as one wide column, because nothing in the file then reveals the underlying grid.</para>
/// <para>RTF has no inline table, so an <see cref="InlineTable"/> is written as a block-level table that
/// splits its host paragraph — other applications see exactly that. Our own ignorable
/// <c>{\*\arinline}</c> marker rides along so this reader puts it back on the text line.</para>
/// </summary>
public static class RtfDocumentFormatter
{
    static RtfDocumentFormatter()
    {
        // CP949 (Korean), Shift-JIS, GB2312 etc. aren't in .NET's default set ??register them so
        // \'hh runs from HWP/Word decode correctly.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
    }

    /// <summary>True if <paramref name="text"/> starts with the RTF signature.</summary>
    public static bool LooksLikeRtf(string? text)
        => text != null && text.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);

    /// <summary>Parses an RTF string into a <see cref="FlowDocument"/> (empty document on failure).
    /// <para>Failure is indistinguishable from a genuinely empty document here. Callers that would
    /// REPLACE open content with the result — loading a file, not pasting a fragment — must use
    /// <see cref="TryParse"/> instead, or a damaged file silently blanks the document and the next
    /// save writes that blank over the original.</para></summary>
    public static FlowDocument Parse(string rtf)
    {
        TryParse(rtf, out var document, out _);
        return document;
    }

    /// <summary>Parses an RTF string, reporting whether it succeeded. Returns <see langword="false"/>
    /// only when the RTF is damaged badly enough to abort the parse; a well-formed but empty document
    /// is a success. On failure <paramref name="document"/> is an empty document (matching
    /// <see cref="Parse"/>) and <paramref name="error"/> describes the fault.
    /// <para>This is the safe entry point for anything that replaces open content — it is the RTF
    /// counterpart of the exception <c>DocumentSerializer.Deserialize</c> throws for damaged JSON.
    /// A parse that aborts part-way reports failure rather than returning what it had read so far:
    /// truncated content that LOOKS like a document is the outcome most likely to be saved over the
    /// original by a host that cannot tell it is incomplete.</para></summary>
    public static bool TryParse(string rtf, out FlowDocument document, out string? error)
    {
        try
        {
            document = RunNormalizer.Compact(new RtfParser(rtf).Run());
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            // Also reported to RichEditorDiagnostics: Parse() discards `error`, so without this a paste
            // that fell back to plain text would be invisible to a host watching only the fault channel.
            RichEditorDiagnostics.Report(ex);
            document = new FlowDocument();
            error = $"{ex.GetType().Name}: {ex.Message}";
            return false;
        }
    }

    /// <summary>Serializes a <see cref="FlowDocument"/> to an RTF string (the inverse of <see cref="Parse"/>):
    /// paragraphs, runs (bold/italic/underline/strike, size, colour, font family), alignment/indent,
    /// headings, lists (as literal markers), tables, and embedded PNG/JPEG images.</summary>
    public static string Write(FlowDocument document) => new RtfWriter().Build(document);
}

// One pass over the RTF char stream. Group state is pushed on '{' and popped on '}', so nested
// formatting restores correctly. Normal text is buffered as bytes and decoded with the document code
// page so multi-byte CJK characters come out whole.
internal sealed class RtfParser
{
    private readonly string _s;
    private int _i;

    private enum Dest { Normal, Skip, ColorTable, Pict, FontTable, FieldInst }

    private struct State
    {
        public bool Bold, Italic, Underline, Strike;
        public double FontSize;   // points; 0 = use the run default
        public int Color;         // index into _colors; -1 = default (black)
        public int Highlight;     // index into _colors (\highlightN); -1 = none
        public int Font;          // index into _fontNames (\fN); -1 = default font
        public string? LinkUrl;   // hyperlink applied to runs inside a \fldrslt group; null = none
        public Dest Dest;
        public int UnicodeSkip;   // chars to swallow after a \uN (set by \ucN)
    }

    private State _st = new() { Color = -1, Highlight = -1, Font = -1, UnicodeSkip = 1 };
    private readonly Stack<State> _stack = new();

    private readonly FlowDocument _doc = new();
    private Paragraph _para = new();
    private readonly StringBuilder _run = new();

    private readonly List<byte> _bytes = new();
    private int _codepage = 1252;
    private Encoding? _enc;
    private Encoding Enc => _enc ??= GetEncoding(_codepage);

    private readonly List<Color> _colors = new();
    private int _ctR, _ctG, _ctB;
    private bool _ctHasColor;

    // \fonttbl: font index -> family name (so \fN runs keep their font — HWP/Word paste otherwise loses
    // every font family), plus per-font \fcharset code pages so CJK bytes (both font NAMES like
    // "맑은 고딕" and run text) decode with the right encoding even when \ansicpg says otherwise.
    private readonly Dictionary<int, string> _fontNames = new();
    private readonly Dictionary<int, int> _fontCodePages = new();
    private readonly Dictionary<int, Encoding> _encCache = new();
    private int _ftblIndex = -1;                      // the \fN entry currently being read
    private readonly List<byte> _ftblBytes = new();   // pending name bytes (code-page text / \'hh)
    private readonly StringBuilder _ftblName = new();

    // \field: the \fldinst instruction is collected here; a HYPERLINK target parsed from it becomes the
    // LinkUrl of the following \fldrslt group's runs (so Word/HWP/own-writer links round-trip).
    private readonly StringBuilder _fldInst = new();
    private string? _pendingFieldUrl;

    private readonly StringBuilder _pictHex = new();
    private string? _pictMime;
    private int _pictWTwips, _pictHTwips;

    // \sl / \slmult state (line spacing; see Apply's "sl"/"slmult" cases).
    private int _slTwips;
    private bool _slMult;

    private void ApplyLineSpacingWords()
    {
        if (_slTwips == 0) { _para.LineSpacing = double.NaN; _para.LineHeight = double.NaN; return; }
        if (_slMult) { _para.LineSpacing = _slTwips / 240.0; _para.LineHeight = double.NaN; }
        else { _para.LineHeight = Math.Abs(_slTwips) / 15.0; _para.LineSpacing = double.NaN; } // "at least" ≈ exact
    }

    // A cell is a TableCell, not just its paragraph: Word writes a table inside a cell as nested rows,
    // and those become real nested tables (which the model has supported since the cell became a block
    // container). Intra-cell \par still becomes a newline within the cell's paragraph.
    private List<List<TableCell>>? _tableRows;
    private List<TableCell>? _curRow;
    private List<int> _curCellx = new();
    private List<int>? _tableCellx;
    private List<List<int>>? _tableRowCellx;

    // Nested tables, keyed by RTF nesting depth (\itap): 2 = a table inside a cell, 3 = one deeper, and
    // so on. _nestRows[d] holds the finished rows at that depth and _nestRow[d] the row being filled;
    // both are consumed when the cell one level up closes. Depth comes from \itap, which is how Word
    // tells the levels apart — every \nestcell looks the same otherwise.
    private readonly Dictionary<int, List<List<TableCell>>> _nestRows = new();
    private readonly Dictionary<int, List<TableCell>> _nestRow = new();
    private int _itap = 1;
    // Paragraphs already closed in the cell being filled, keyed by the depth that cell belongs to: text
    // that preceded a nested table stays with ITS cell instead of being taken by the deeper one.
    private readonly Dictionary<int, List<Block>> _cellPending = new();

    // Merge flags per cell definition (\clmgf/\clmrg horizontal, \clvmgf/\clvmrg vertical). Flags
    // precede their \cellx in the row definition; FinalizeTable turns them back into col/row spans.
    // Shading is a \colortbl index (0 = none), resolved to a colour in FinalizeTable — the colour table
    // is fully read by then, and a row definition can precede it in pathological input.
    private struct CellDef { public bool HFirst, HCont, VFirst, VCont; public CellVerticalAlignment VAlign; public int Shading; }
    private CellDef _pendingCellDef;
    private List<CellDef> _curCellDefs = new();
    private List<List<CellDef>>? _tableRowDefs;

    // Set by our own {\*\arinlineN} marker: the next top-level table was an InlineTable and belongs back
    // on a paragraph's text line.
    private bool _nextTableInline;
    // N = 1 when the table OPENS its host paragraph — nothing preceded it, so the writer emitted no \par
    // (that \par would render as a blank line above the table) and there is no closed host paragraph to
    // reuse. Reattaching to the PREVIOUS paragraph in that case merges two paragraphs and swallows the
    // earlier one — and "a paragraph holding nothing but the table" is the ordinary shape of an inline
    // table, so this is the common case, not an edge one.
    private bool _inlineTableOpensHost;

    public RtfParser(string s) => _s = s;

    public FlowDocument Run()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            // Commit the text collected so far BEFORE descending into a group. A group can switch to a
            // destination we skip ({\*\nesttableprops …}, bookmarks, fields — all normal in Word output),
            // and the closing brace's FlushRun then runs with that destination still active and throws the
            // pending run away. Word documents lost the text preceding any such group.
            if (c == '{') { if (_st.Dest == Dest.Normal) FlushRun(); _stack.Push(_st); _i++; }
            else if (c == '}')
            {
                if (_st.Dest == Dest.Pict) FinalizePict();
                else if (_st.Dest == Dest.FieldInst) FinalizeFieldInst();
                else FlushRun();
                _st = _stack.Count > 0 ? _stack.Pop() : _st; _i++;
            }
            else if (c == '\\') ReadControl();
            else if (c == '\r' || c == '\n') _i++;
            else if (_st.Dest == Dest.ColorTable && c == ';') { CloseColorEntry(); _i++; }
            else if (_st.Dest == Dest.Pict) { if (Uri.IsHexDigit(c)) _pictHex.Append(c); _i++; }
            else if (_st.Dest == Dest.FontTable) { FontTableChar(c); _i++; }
            else if (_st.Dest == Dest.FieldInst) { _fldInst.Append(c); _i++; }
            else { if (_st.Dest == Dest.Normal) AppendByte(c); _i++; }
        }
        EndRow();
        FlushRun();
        FinalizeTable();
        if (_para.Inlines.Count > 0) _doc.Blocks.Add(_para);
        if (_doc.Blocks.Count == 0) _doc.Blocks.Add(new Paragraph());
        return _doc;
    }

    // ---- control word / symbol ----

    private void ReadControl()
    {
        _i++; // past '\'
        if (_i >= _s.Length) return;
        char c = _s[_i];

        if (c == '\'') { ReadHexChar(); return; }
        if (!char.IsLetter(c))
        {
            _i++;
            if (_st.Dest == Dest.Normal)
            {
                if (c == '\\' || c == '{' || c == '}') AppendByte(c);
                else if (c == '~') AppendByte(' ');
            }
            if (c == '*') _st.Dest = Dest.Skip;
            return;
        }

        int start = _i;
        while (_i < _s.Length && char.IsLetter(_s[_i])) _i++;
        string word = _s.Substring(start, _i - start);
        int? param = null;
        if (_i < _s.Length && (_s[_i] == '-' || char.IsDigit(_s[_i])))
        {
            int ns = _i;
            if (_s[_i] == '-') _i++;
            while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
            param = int.Parse(_s.Substring(ns, _i - ns), CultureInfo.InvariantCulture);
        }
        if (_i < _s.Length && _s[_i] == ' ') _i++;

        Apply(word, param);
    }

    private void ReadHexChar()
    {
        _i++; // past '\''
        if (_i + 1 >= _s.Length) return;
        string hex = _s.Substring(_i, 2);
        _i += 2;
        if (!byte.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var b)) return;
        if (_st.Dest == Dest.Normal) _bytes.Add(b);
        else if (_st.Dest == Dest.FontTable) _ftblBytes.Add(b); // CJK font names arrive as \'hh bytes
    }

    private void Apply(string w, int? p)
    {
        switch (w)
        {
            case "ansicpg": _codepage = p ?? 1252; _enc = null; break;

            case "b": SetBold(p != 0); break;
            case "i": SetItalic(p != 0); break;
            case "ul": SetUnderline(p != 0); break;
            case "ulnone": SetUnderline(false); break;
            case "strike": SetStrike(p != 0); break;
            case "fs": FlushRun(); _st.FontSize = (p ?? 24) / 2.0; break;
            case "cf": FlushRun(); _st.Color = p ?? -1; break;
            case "highlight": FlushRun(); _st.Highlight = p ?? -1; break;
            case "plain": FlushRun(); _st.Bold = _st.Italic = _st.Underline = _st.Strike = false; _st.FontSize = 0; _st.Color = -1; _st.Highlight = -1; _st.Font = -1; break;

            // \fN: inside \fonttbl it BEGINS a font definition; in body text it selects that font for
            // the following run. Pending bytes flush first so they decode with the PREVIOUS font's charset.
            case "f":
                if (_st.Dest == Dest.FontTable) { CommitFontEntry(); _ftblIndex = p ?? 0; }
                else if (_st.Dest == Dest.Normal) { FlushRun(); _st.Font = p ?? -1; }
                break;
            case "fcharset":
                if (_st.Dest == Dest.FontTable && _ftblIndex >= 0 && CharsetCodePage(p ?? -1) is { } fcp)
                    _fontCodePages[_ftblIndex] = fcp;
                break;

            case "par": case "sect": EndParagraph(); break;
            case "line": if (_st.Dest == Dest.Normal) _bytes.Add(10); break;
            case "tab": if (_st.Dest == Dest.Normal) _bytes.Add(9); break;
            // \pard resets paragraph formatting; alignment/indent/spacing control words follow it.
            case "pard":
                _para.TextAlignment = TextAlignment.Left; _para.Indent = 0;
                _para.LineSpacing = double.NaN; _para.LineHeight = double.NaN;
                _slTwips = 0; _slMult = false;
                SetItap(1); // \itap is a paragraph property, so a reset drops back to the body level
                break;
            case "ql": _para.TextAlignment = TextAlignment.Left; break;
            case "qc": _para.TextAlignment = TextAlignment.Center; break;
            case "qr": _para.TextAlignment = TextAlignment.Right; break;
            case "qj": _para.TextAlignment = TextAlignment.Justify; break;
            case "li": _para.Indent = Math.Max(0, (p ?? 0) / 15.0); break; // twips -> px

            // Line spacing: \slN (+\slmult1 = proportional, N/240 lines; \slmult0 = absolute twips,
            // negative meaning "exactly"). The two words arrive in either order, so both re-apply.
            case "sl": _slTwips = p ?? 0; ApplyLineSpacingWords(); break;
            case "slmult": _slMult = (p ?? 0) != 0; ApplyLineSpacingWords(); break;

            // Structure control words act only in the body. Word puts a nested table's row definition in
            // {\*\nesttableprops \trowd …\cellx…\nestrow} — an ignorable group we skip — and acting on it
            // restarted the row we were building, discarding the parent cell's accumulated text.
            case "trowd": if (_st.Dest == Dest.Normal) StartRow(); break;
            case "cell": if (_st.Dest == Dest.Normal) EndCell(); break;
            case "row": if (_st.Dest == Dest.Normal) EndRow(); break;
            case "intbl": break;
            case "cellx":
                if (_st.Dest == Dest.Normal)
                {
                    _curCellx.Add(p ?? 0);
                    _curCellDefs.Add(_pendingCellDef);
                    _pendingCellDef = default;
                }
                break;

            case "clmgf": _pendingCellDef.HFirst = true; break;
            case "clmrg": _pendingCellDef.HCont = true; break;
            case "clvmgf": _pendingCellDef.VFirst = true; break;
            case "clvmrg": _pendingCellDef.VCont = true; break;
            case "clvertalt": _pendingCellDef.VAlign = CellVerticalAlignment.Top; break;
            case "clvertalc": _pendingCellDef.VAlign = CellVerticalAlignment.Center; break;
            case "clvertalb": _pendingCellDef.VAlign = CellVerticalAlignment.Bottom; break;
            // The writer has always been able to emit cell shading and Word/HWP honour it, but the reader
            // skipped it — so a cell background exported and reloaded through our OWN format came back
            // colourless.
            case "clcbpat": _pendingCellDef.Shading = p ?? 0; break;

            // A table inside a cell: the model nests, and the writer emits these, so they come back as a
            // real nested TableBlock in the parent cell rather than flattened tab/newline text.
            case "itap": SetItap(p ?? 1); break;
            case "nestcell": if (_st.Dest == Dest.Normal) EndNestedCell(); break;
            case "nestrow": EndNestedRow(); break; // see EndNestedRow: intentionally not destination-gated
            // The fallback copy of a nested table, for readers that can't nest. We flatten instead, so
            // skip it — otherwise its \par landed as a stray line break in the parent cell and its text
            // arrived twice.
            case "nonesttables": _st.Dest = Dest.Skip; break;
            // Our own inline-table marker (see the InlineTable branch in WriteParagraph). It arrives as
            // {\*\arinline}, so \* has already switched this group to Skip — the group is empty and only
            // the flag matters. A parser field rather than pushed/popped group state, because the marker
            // group CLOSES before the table it describes begins.
            case "arinline": _nextTableInline = true; _inlineTableOpensHost = (p ?? 0) != 0; break;

            case "shptxt": _st.Dest = Dest.Normal; break;
            case "sp": case "sn": case "sv": _st.Dest = Dest.Skip; break;

            // \field {\*\fldinst HYPERLINK "url"}{\fldrslt text}: read the instruction (overriding the
            // \* skip, like \shppict) and hand the parsed URL to the result group's runs.
            case "field": _pendingFieldUrl = null; break;
            case "fldinst": _st.Dest = Dest.FieldInst; _fldInst.Clear(); break;
            case "fldrslt": _st.Dest = Dest.Normal; _st.LinkUrl = _pendingFieldUrl; break;

            case "u": EmitUnicode(p ?? 0); break;
            case "uc": _st.UnicodeSkip = p ?? 1; break;

            // Typographic control words Word emits pervasively (smart quotes for every apostrophe,
            // em/en dashes, bullets). Dropping them silently loses characters ("don't" -> "dont").
            case "rquote": AppendChar('’'); break;
            case "lquote": AppendChar('‘'); break;
            case "rdblquote": AppendChar('”'); break;
            case "ldblquote": AppendChar('“'); break;
            case "emdash": AppendChar('—'); break;
            case "endash": AppendChar('–'); break;
            case "bullet": AppendChar('•'); break;
            case "emspace": AppendChar(' '); break;
            case "enspace": AppendChar(' '); break;
            case "qmspace": AppendChar(' '); break;

            case "colortbl": _st.Dest = Dest.ColorTable; _colors.Clear(); _ctR = _ctG = _ctB = 0; _ctHasColor = false; break;
            case "fonttbl": _st.Dest = Dest.FontTable; _ftblIndex = -1; _ftblName.Clear(); _ftblBytes.Clear(); break;
            case "stylesheet": case "info": case "pntext": case "themedata":
            case "datastore": case "xmlnstbl": case "rsidtbl": case "generator": case "listtable":
            case "listoverridetable": case "revtbl":
                _st.Dest = Dest.Skip; break;

            case "red": _ctR = p ?? 0; _ctHasColor = true; break;
            case "green": _ctG = p ?? 0; _ctHasColor = true; break;
            case "blue": _ctB = p ?? 0; _ctHasColor = true; break;

            case "shppict": _st.Dest = Dest.Normal; break;
            case "nonshppict": _st.Dest = Dest.Skip; break;
            case "pict": FlushRun(); _st.Dest = Dest.Pict; _pictHex.Clear(); _pictMime = null; _pictWTwips = _pictHTwips = 0; break;
            case "pngblip": _pictMime = "image/png"; break;
            case "jpegblip": _pictMime = "image/jpeg"; break;
            case "dibitmap": _pictMime = "dib"; break; // raw DIB (no BMP file header); wrapped in FinalizePict
            case "picwgoal": _pictWTwips = p ?? 0; break;
            case "pichgoal": _pictHTwips = p ?? 0; break;

            default: break;
        }
    }

    // ---- fields (hyperlinks) ----

    // Called when a group holding \fldinst text closes. An empty buffer means a NESTED group inside the
    // instruction closed first — keep the already-captured URL instead of clobbering it.
    private void FinalizeFieldInst()
    {
        string inst = _fldInst.ToString().Trim();
        _fldInst.Clear();
        if (inst.Length == 0) return;
        var m = System.Text.RegularExpressions.Regex.Match(inst, "HYPERLINK\\s+(?:\"([^\"]+)\"|(\\S+))",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) _pendingFieldUrl = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
    }

    // ---- font table ----

    private void FontTableChar(char c)
    {
        if (c == ';') { CommitFontEntry(); return; }
        if (c < 256) _ftblBytes.Add((byte)c);
        else { FlushFtblBytes(); _ftblName.Append(c); }
    }

    private void FlushFtblBytes()
    {
        if (_ftblBytes.Count == 0) return;
        var enc = _ftblIndex >= 0 && _fontCodePages.TryGetValue(_ftblIndex, out var cp) ? CachedEnc(cp) : Enc;
        _ftblName.Append(enc.GetString(_ftblBytes.ToArray()));
        _ftblBytes.Clear();
    }

    private void CommitFontEntry()
    {
        FlushFtblBytes();
        if (_ftblIndex >= 0)
        {
            string name = _ftblName.ToString().Trim();
            if (name.Length > 0) _fontNames[_ftblIndex] = RunNormalizer.Intern(name);
        }
        _ftblName.Clear();
        _ftblIndex = -1;
    }

    // Windows \fcharsetN -> code page, for the CJK/legacy sets that matter here. Null = no override.
    private static int? CharsetCodePage(int charset) => charset switch
    {
        0 => 1252,    // ANSI
        128 => 932,   // Shift-JIS
        129 => 949,   // Korean (HWP/Word emit this on Korean fonts)
        130 => 1361,  // Johab
        134 => 936,   // GB2312
        136 => 950,   // Big5
        161 => 1253, 162 => 1254, 163 => 1258, 177 => 1255, 178 => 1256,
        186 => 1257, 204 => 1251, 222 => 874, 238 => 1250,
        _ => null,
    };

    private Encoding CachedEnc(int codepage)
    {
        if (!_encCache.TryGetValue(codepage, out var e)) _encCache[codepage] = e = GetEncoding(codepage);
        return e;
    }

    // The encoding for pending BODY bytes: the current font's \fcharset code page when known (more
    // reliable for CJK than the document \ansicpg), else the document encoding.
    private Encoding RunEnc => _st.Font >= 0 && _fontCodePages.TryGetValue(_st.Font, out var cp) ? CachedEnc(cp) : Enc;

    private void CloseColorEntry()
    {
        _colors.Add(_ctHasColor
            ? Color.FromArgb(255, (byte)_ctR, (byte)_ctG, (byte)_ctB)
            : Color.FromArgb(0, 0, 0, 0)); // leading "auto" entry: zero alpha = "use the run default"
        _ctR = _ctG = _ctB = 0;
        _ctHasColor = false;
    }

    private void SetBold(bool v) { if (v != _st.Bold) FlushRun(); _st.Bold = v; }
    private void SetItalic(bool v) { if (v != _st.Italic) FlushRun(); _st.Italic = v; }
    private void SetUnderline(bool v) { if (v != _st.Underline) FlushRun(); _st.Underline = v; }
    private void SetStrike(bool v) { if (v != _st.Strike) FlushRun(); _st.Strike = v; }

    private void EmitUnicode(int code)
    {
        FlushBytes();
        if (code < 0) code += 65536;
        if (_st.Dest == Dest.Normal)
        {
            if (code >= 0 && code <= 0xFFFF) _run.Append((char)code);
        }
        else if (_st.Dest == Dest.FontTable && code >= 0 && code <= 0xFFFF)
        {
            FlushFtblBytes();
            _ftblName.Append((char)code); // font names may arrive as \uN too
        }
        for (int k = 0; k < _st.UnicodeSkip && _i < _s.Length; k++)
        {
            if (_s[_i] == '\\')
                _i += (_i + 1 < _s.Length && _s[_i + 1] == '\'') ? 4 : 2;
            else if (_s[_i] == '{' || _s[_i] == '}') break;
            else _i++;
        }
    }

    // ---- building ----

    private void AppendByte(char c)
    {
        if (_st.Dest != Dest.Normal) return;
        if (c < 256) _bytes.Add((byte)c);
        else { FlushBytes(); _run.Append(c); }
    }

    // Emits a literal Unicode character produced by a control word (\rquote, \emdash, …). Pending
    // code-page bytes flush first so the character lands in document order.
    private void AppendChar(char ch)
    {
        if (_st.Dest != Dest.Normal) return;
        FlushBytes();
        _run.Append(ch);
    }

    private void FlushBytes()
    {
        if (_bytes.Count == 0) return;
        _run.Append(RunEnc.GetString(_bytes.ToArray()));
        _bytes.Clear();
    }

    private void FlushRun()
    {
        FlushBytes();
        if (_run.Length == 0) return;
        if (_st.Dest != Dest.Normal) { _run.Clear(); return; }
        _para.Inlines.Add(MakeRun(_run.ToString()));
        _run.Clear();
    }

    private Run MakeRun(string text)
    {
        var r = new Run
        {
            Text = text,
            FontWeight = _st.Bold ? FontWeightValues.Bold : FontWeightValues.Normal,
            FontStyle = _st.Italic ? FontStyle.Italic : FontStyle.Normal,
            FontSize = _st.FontSize > 0 ? _st.FontSize : 10, // pt; body default
        };
        var dec = TextDecorationFlags.None;
        if (_st.Underline) dec |= TextDecorationFlags.Underline;
        if (_st.Strike) dec |= TextDecorationFlags.Strikethrough;
        r.TextDecorations = dec;
        if (_st.Font >= 0 && _fontNames.TryGetValue(_st.Font, out var fam)) r.FontFamily = fam;
        r.NavigateUri = _st.LinkUrl; // non-null only inside a \fldrslt with a parsed HYPERLINK
        if (_st.Color >= 0 && _st.Color < _colors.Count)
        {
            var col = _colors[_st.Color];
            if (col.A != 0) r.Foreground = col;
        }
        if (_st.Highlight >= 0 && _st.Highlight < _colors.Count)
        {
            var bg = _colors[_st.Highlight];
            if (bg.A != 0) r.Background = bg;
        }
        return r;
    }

    private void EndParagraph()
    {
        // Inside a cell, \par is a real paragraph break — the model has held several paragraphs per cell
        // since cells became block containers, and folding them into one paragraph with newlines lost
        // that structure on every Word/HWP paste. It also made our own round trip non-idempotent: the
        // count of paragraphs in a cell changed between the first and second cycle, because only a \par
        // that happened to sit next to an \itap transition survived as a break (found by the fuzz).
        if (_curRow != null)
        {
            FlushRun();
            if (_para.Inlines.Count > 0)
            {
                if (!_cellPending.TryGetValue(_itap, out var pending)) _cellPending[_itap] = pending = new List<Block>();
                pending.Add(_para);
                _para = new Paragraph();
            }
            return;
        }
        FlushRun();
        FinalizeTable();
        _doc.Blocks.Add(_para);
        _para = new Paragraph();
    }

    // ---- tables ----

    // Note for the repeated row definition our writer emits (see BuildRowDefinition): the second \trowd
    // arrives after every \cell, so it only clears _curCellx/_curCellDefs, which the repeated \cellx words
    // then refill with the identical values before \row consumes them. _curRow (the cells themselves) is
    // preserved by the ??= below, so the repeat is a no-op on the way back in.
    private void StartRow()
    {
        _tableRows ??= new List<List<TableCell>>();
        _curRow ??= new List<TableCell>();
        _curCellx = new List<int>();
        _curCellDefs = new List<CellDef>();
        _pendingCellDef = default;
        _para = new Paragraph();
    }

    private void EndCell()
    {
        if (_curRow == null) StartRow();
        _curRow!.Add(TakeCell(_itap + 1)); // a top-level cell's nested table lives at \itap 2
    }

    private void EndRow()
    {
        if (_curRow == null) return;
        _tableRows ??= new List<List<TableCell>>();
        _tableRows.Add(_curRow);
        _curRow = null;
        if (_tableCellx == null && _curCellx.Count > 0) _tableCellx = _curCellx;
        // EVERY row's boundaries, not just the first: a horizontally merged cell is one wide \cellx, so
        // the column grid is the union of all rows' boundaries and a row's own list says which grid
        // columns each of its cells spans (see BuildTableFromGeometry).
        (_tableRowCellx ??= new List<List<int>>()).Add(_curCellx);
        (_tableRowDefs ??= new List<List<CellDef>>()).Add(_curCellDefs);
        _curCellDefs = new List<CellDef>();
    }

    // \nestcell ends one cell of the nested row at the current depth. That cell may itself contain the
    // table one level deeper, which is why it goes through the same builder as a top-level cell.
    private void EndNestedCell()
    {
        int depth = Math.Max(2, _itap);
        if (!_nestRow.TryGetValue(depth, out var row)) _nestRow[depth] = row = new List<TableCell>();
        row.Add(TakeCell(depth + 1));
    }

    // \nestrow ends the nested row at the current depth. It arrives inside {\*\nesttableprops …}, which
    // is an ignorable destination, so this one is deliberately NOT gated on the destination — a reader
    // that supports nesting has to act on it there. The row's \cellx widths are in that same group and
    // are not read, so a nested table comes back at the default column width.
    private void EndNestedRow()
    {
        int depth = Math.Max(2, _itap);
        if (!_nestRow.TryGetValue(depth, out var row) || row.Count == 0) return;
        if (!_nestRows.TryGetValue(depth, out var rows)) _nestRows[depth] = rows = new List<List<TableCell>>();
        rows.Add(row);
        _nestRow.Remove(depth);
    }

    // Builds the table from the rows' \cellx GEOMETRY, which is where a horizontal merge lives: a merged
    // span is one cell whose boundary jumps several columns. The column grid is the union of every row's
    // boundaries, and a cell's colspan is the number of grid columns its boundary swallows. This reads
    // both writers' output — ours (geometric) and Word's (one \cellx per column plus \clmgf/\clmrg), the
    // latter because the flags are folded in on top: an \clmrg cell extends the one before it.
    // Returns null when the input carries no boundaries at all, so the caller can fall back.
    private TableBlock? BuildTableFromGeometry(List<List<TableCell>> rows, List<List<int>>? rowCellx,
                                               List<List<CellDef>>? defs)
    {
        if (rowCellx == null || rows.Count == 0) return null;

        var grid = new List<int>();
        foreach (var rc in rowCellx)
            foreach (int b in rc)
                if (b > 0 && !grid.Contains(b)) grid.Add(b);
        if (grid.Count == 0) return null;
        grid.Sort();

        // Per row: where each emitted cell starts in the grid and how many columns it spans.
        var placed = new List<List<(int start, int span, TableCell cell, CellDef def)>>();
        for (int r = 0; r < rows.Count; r++)
        {
            var line = new List<(int, int, TableCell, CellDef)>();
            var bounds = r < rowCellx.Count ? rowCellx[r] : new List<int>();
            var rdefs = defs != null && r < defs.Count ? defs[r] : new List<CellDef>();
            int prev = 0;
            for (int i = 0; i < rows[r].Count; i++)
            {
                var def = i < rdefs.Count ? rdefs[i] : default;
                // Word's flag form: this cell continues the previous one, so widen that instead.
                if (def.HCont && line.Count > 0)
                {
                    var last = line[^1];
                    int extra = i < bounds.Count ? SpanOf(prev, bounds[i]) : 1;
                    line[^1] = (last.Item1, last.Item2 + Math.Max(1, extra), last.Item3, last.Item4);
                    if (i < bounds.Count) prev = bounds[i];
                    continue;
                }
                int start = CountAtOrBelow(prev);
                int span = i < bounds.Count ? SpanOf(prev, bounds[i]) : 1;
                if (start >= grid.Count) break;                     // more cells than the grid describes
                span = Math.Max(1, Math.Min(span, grid.Count - start));
                line.Add((start, span, rows[r][i], def));
                if (i < bounds.Count) prev = bounds[i];
            }
            placed.Add(line);
        }

        int cols = grid.Count;
        var tb = new TableBlock(rows.Count, cols);
        tb.Cells.Clear();
        tb.ColSpans.Clear();
        tb.RowSpans.Clear();
        for (int r = 0; r < rows.Count; r++)
        {
            var cells = new List<TableCell>(cols);
            for (int c = 0; c < cols; c++) cells.Add(new TableCell());
            foreach (var (start, _, cell, _) in placed[r]) cells[start] = cell;
            tb.Cells.Add(cells);
            var cs = new List<int>(cols); var rs = new List<int>(cols);
            for (int c = 0; c < cols; c++) { cs.Add(1); rs.Add(1); }
            tb.ColSpans.Add(cs); tb.RowSpans.Add(rs);
        }
        tb.Rows = rows.Count;
        tb.Columns = cols;

        tb.ColumnWidths.Clear();
        for (int c = 0; c < cols; c++)
        {
            double wpx = (grid[c] - (c == 0 ? 0 : grid[c - 1])) / 15.0;
            tb.ColumnWidths.Add(wpx >= 16 ? wpx : 100);
        }

        // Cell properties, then the spans. Vertical merge has no geometry — it stays on the flags.
        foreach (var line in placed)
            foreach (var (start, _, cell, def) in line)
            {
                cell.VerticalAlignment = def.VAlign;
                if (def.Shading > 0 && def.Shading < _colors.Count)
                {
                    var shade = _colors[def.Shading];
                    if (shade.A != 0) cell.Background = shade;
                }
            }
        for (int r = 0; r < placed.Count; r++)
            foreach (var (start, span, _, def) in placed[r])
            {
                if (def.VCont) continue; // covered from above; its anchor stamps it
                int rspan = 1;
                for (int r2 = r + 1; r2 < placed.Count; r2++)
                {
                    var below = placed[r2].Find(e => e.start == start);
                    if (below.cell == null || !below.def.VCont) break;
                    rspan++;
                }
                if (span > 1 || rspan > 1) tb.SetSpan(r, start, span, rspan);
            }
        return tb;

        // Grid columns fully inside (from, to].
        int SpanOf(int from, int to)
        {
            int n = 0;
            foreach (int b in grid) if (b > from && b <= to) n++;
            return Math.Max(1, n);
        }
        int CountAtOrBelow(int v)
        {
            int n = 0;
            foreach (int b in grid) if (b <= v) n++;
            return n;
        }
    }

    // Builds a rectangular TableBlock from accumulated rows. Shared by the top-level table (FinalizeTable,
    // which then applies the merge flags) and by nested tables, whose \cellx widths live in an ignorable
    // group we don't read — those pass cellx: null and come out at the default column width.
    private static TableBlock? BuildTable(List<List<TableCell>> rows, List<int>? cellx)
    {
        if (rows.Count == 0) return null;
        int cols = 0;
        foreach (var r in rows) if (r.Count > cols) cols = r.Count;
        if (cols == 0) return null;

        var tb = new TableBlock(rows.Count, cols);
        tb.Cells.Clear();
        foreach (var r in rows)
        {
            var cells = new List<TableCell>(cols);
            for (int c = 0; c < cols; c++) cells.Add(c < r.Count ? r[c] : new TableCell());
            tb.Cells.Add(cells);
        }
        tb.Rows = rows.Count;
        tb.Columns = cols;
        if (cellx != null && cellx.Count > 0)
        {
            tb.ColumnWidths.Clear();
            int prev = 0;
            for (int c = 0; c < cols; c++)
            {
                int boundary = c < cellx.Count ? cellx[c] : prev + 1500;
                double wpx = (boundary - prev) / 15.0;
                tb.ColumnWidths.Add(wpx >= 16 ? wpx : 100);
                prev = boundary;
            }
        }
        tb.ColSpans.Clear();
        tb.RowSpans.Clear();
        for (int r = 0; r < rows.Count; r++)
        {
            var cs = new List<int>(cols);
            var rs = new List<int>(cols);
            for (int c = 0; c < cols; c++) { cs.Add(1); rs.Add(1); }
            tb.ColSpans.Add(cs);
            tb.RowSpans.Add(rs);
        }
        return tb;
    }

    // The cell that just ended: the paragraphs it collected, the table nested one level deeper (if any),
    // and the paragraph currently being filled — in the order they appeared.
    private TableCell TakeCell(int childDepth)
    {
        FlushRun();
        var cell = new TableCell(_para);
        _para = new Paragraph();

        // A nested row left open (no \nestrow seen, e.g. truncated input) still counts.
        int saved = _itap;
        _itap = childDepth;
        EndNestedRow();
        _itap = saved;

        int at = 0;
        if (_cellPending.TryGetValue(childDepth - 1, out var pending))
        {
            _cellPending.Remove(childDepth - 1);
            foreach (var b in pending) { b.Parent = cell; cell.Blocks.Insert(at++, b); }
        }
        if (_nestRows.TryGetValue(childDepth, out var rows))
        {
            _nestRows.Remove(childDepth);
            if (BuildTable(rows, null) is { } inner) { inner.Parent = cell; cell.Blocks.Insert(at, inner); }
        }
        return cell;
    }

    // Drops one trailing '\n' from a paragraph, and the run that held it if it becomes empty.
    private static void TrimOneTrailingNewline(Paragraph p)
    {
        for (int i = p.Inlines.Count - 1; i >= 0; i--)
        {
            if (p.Inlines[i] is not Run r) return;              // an object inline ends it; nothing to trim
            if (string.IsNullOrEmpty(r.Text)) continue;          // skip empties and keep looking back
            if (r.Text[^1] != '\n') return;
            r.Text = r.Text[..^1];
            if (r.Text.Length == 0) p.Inlines.RemoveAt(i);
            return;
        }
    }

    // \itap<N> switches nesting depth. Going deeper closes the text collected so far as a paragraph of
    // the cell being filled, so "text, then a nested table" keeps that order instead of the text being
    // swallowed into the nested table's first cell.
    private void SetItap(int depth)
    {
        if (depth == _itap) return;
        if (depth > _itap)
        {
            FlushRun();
            // The writer MUST close this paragraph with \par before descending, or Word glues its text
            // onto the nested table's first cell — so that break is the paragraph boundary itself, not
            // content. Inside a cell \par is otherwise read as a newline, which made the separator come
            // back as text: one more '\n' before the nested table on every save/load cycle.
            TrimOneTrailingNewline(_para);
            if (_para.Inlines.Count > 0)
            {
                // The paragraph belongs to the cell being filled at the CURRENT depth, not the deeper one.
                if (!_cellPending.TryGetValue(_itap, out var pending))
                    _cellPending[_itap] = pending = new List<Block>();
                pending.Add(_para);
                _para = new Paragraph();
            }
        }
        _itap = depth;
    }

    private void FinalizeTable()
    {
        var rows = _tableRows;
        var cellx = _tableCellx;
        var rowCellx = _tableRowCellx;
        var defs = _tableRowDefs;
        _tableRows = null;
        _curRow = null;
        _tableCellx = null;
        _tableRowCellx = null;
        _tableRowDefs = null;
        bool inlineMark = _nextTableInline;
        bool opensHost = _inlineTableOpensHost;
        _nextTableInline = false;
        _inlineTableOpensHost = false;
        // A top-level table is done, so nothing nested inside it can still be pending: every nested row is
        // consumed by TakeCell when the cell one level up closes. Anything left is orphaned by truncated
        // or malformed input — and left in place it was picked up by the NEXT table's first cell, which
        // made a nested table teleport into an unrelated table further down the document.
        _nestRow.Clear();
        _nestRows.Clear();
        _cellPending.Clear();
        _itap = 1;
        if (rows == null) return;
        if ((BuildTableFromGeometry(rows, rowCellx, defs) ?? BuildTable(rows, cellx)) is not { } tb) return;

        // Marked as an inline table by our own writer ({\*\arinline}): put it back on the preceding
        // paragraph's line and continue that paragraph, so "text, table, more text" is one line again
        // instead of three blocks. The host paragraph was closed by the \par the writer emits before it.
        if (inlineMark && (opensHost || (_doc.Blocks.Count > 0 && _doc.Blocks[^1] is Paragraph)))
        {
            // Reuse the paragraph the writer closed just before the table; when the table OPENED its host
            // there is no such paragraph and a fresh one is the host.
            Paragraph host;
            if (opensHost) host = new Paragraph();
            else { host = (Paragraph)_doc.Blocks[^1]; _doc.Blocks.RemoveAt(_doc.Blocks.Count - 1); }
            var it = new InlineTable { Table = tb, Parent = host };
            host.Inlines.Add(it);
            // Whatever the current paragraph has collected is the text that followed the table.
            foreach (var inl in new List<Inline>(_para.Inlines))
            {
                _para.Inlines.Remove(inl);
                inl.Parent = host;
                host.Inlines.Add(inl);
            }
            _para = host; // the caller adds it back
            return;
        }
        _doc.Blocks.Add(tb);
    }

    // ---- images ----

    // Places the accumulated \pict bytes: small (<64px) inline, larger as its own block. Twips ??px is
    // /15. Natural size (when \picwgoal/\pichgoal absent) is read from the encoded header (no GPU decode).
    private void FinalizePict()
    {
        var hex = _pictHex.ToString();
        _pictHex.Clear();
        if (hex.Length < 8) return;
        var bytes = HexToBytes(hex);
        if (bytes == null || bytes.Length == 0) return;

        // \dibitmap carries a header-less DIB — prepend a BITMAPFILEHEADER so it decodes as BMP.
        if (_pictMime == "dib") { bytes = WrapDibAsBmp(bytes); if (bytes == null) return; _pictMime = "image/bmp"; }
        // No recognized blip keyword (\pngblip/\jpegblip): sniff the bytes — some writers omit or use
        // other words but still embed a decodable image. Genuinely unsupported formats (WMF/EMF) fail
        // the sniff and are dropped here (the paste path then prefers the HTML flavor's images).
        else if (_pictMime == null)
        {
            var (sw, sh) = ImageInfo.GetPixelSize(bytes);
            if (sw <= 0 || sh <= 0) return;
            _pictMime = ImageMime.Detect(bytes);
        }

        double w = _pictWTwips > 0 ? _pictWTwips / 15.0 : 0;
        double h = _pictHTwips > 0 ? _pictHTwips / 15.0 : 0;
        if (w <= 0 || h <= 0)
        {
            var (nw, nh) = ImageInfo.GetPixelSize(bytes);
            if (nw <= 0 || nh <= 0) return; // not a decodable/known PNG/JPEG after all
            w = nw; h = nh;
        }
        string mime = ImageMime.Detect(bytes);

        if (w < 64 && h < 64)
        {
            var img = new InlineImage { Width = w, Height = h };
            img.SetImageData(bytes, mime);
            _para.Inlines.Add(img);
        }
        else if (_curRow != null)
        {
            // Inside a table row the document body is the wrong destination: _para is the CELL's
            // paragraph (EndParagraph suppresses flushing while a row is open), so emitting a block here
            // made the picture escape its cell and land in the body, out of document order. Keep it in
            // the cell as an inline image at the same size.
            var cellImg = new InlineImage { Width = w, Height = h };
            cellImg.SetImageData(bytes, mime);
            _para.Inlines.Add(cellImg);
        }
        else
        {
            if (_para.Inlines.Count > 0) { _doc.Blocks.Add(_para); _para = new Paragraph(); }
            var ib = new ImageBlock { Width = w, Height = h };
            ib.SetImageData(bytes, mime);
            _doc.Blocks.Add(ib);
        }
    }

    // Prepends the 14-byte BITMAPFILEHEADER a raw DIB lacks (offset = header + palette), so WIC/our
    // sniffing see a standard BMP. Returns null when the blob doesn't start with a known info header.
    private static byte[]? WrapDibAsBmp(byte[] dib)
    {
        if (dib.Length < 40) return null;
        int hdr = BitConverter.ToInt32(dib, 0);
        if (hdr != 40 && hdr != 108 && hdr != 124) return null; // BITMAPINFO/V4/V5 header sizes
        int bitCount = BitConverter.ToUInt16(dib, 14);
        int compression = BitConverter.ToInt32(dib, 16);
        int clrUsed = BitConverter.ToInt32(dib, 32);
        int palette = bitCount <= 8 ? (clrUsed > 0 ? clrUsed : 1 << bitCount) * 4 : 0;
        int masks = hdr == 40 && compression == 3 ? 12 : 0; // BI_BITFIELDS masks follow a plain header
        int offset = 14 + hdr + masks + palette;
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        return bmp;
    }

    private static byte[]? HexToBytes(string hex)
    {
        if ((hex.Length & 1) != 0) hex = hex.Substring(0, hex.Length - 1);
        // The collector only accepts hex digits (Uri.IsHexDigit), so this can't realistically throw;
        // the catch keeps the old "null on bad input" contract. FromHexString beats the former
        // per-2-chars byte.TryParse loop on multi-megabyte pasted pictures.
        try { return Convert.FromHexString(hex); }
        catch (FormatException ex) { RichEditorDiagnostics.Report(ex); return null; }
    }

    private static Encoding GetEncoding(int codepage)
    {
        try { return Encoding.GetEncoding(codepage); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return Encoding.Latin1; }
    }
}

// Serializes a FlowDocument to RTF ??the inverse of RtfParser, covering the same subset.
internal sealed class RtfWriter
{
    private readonly StringBuilder _body = new();
    private readonly List<string> _fonts = new() { "" };  // \f0 = default
    private readonly List<Color> _colors = new();          // \colortbl entry 0 is "auto"; these are 1-based
    private readonly Dictionary<string, int> _fontIndex = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, int> _colorIndex = new();

    public string Build(FlowDocument doc)
    {
        // Per-ListLevel ordered counters, mirroring the editor's rendering (RichEditor.EnsureBlockLayout):
        // nested ordered lists number 1,2,… per level instead of continuing the parent's count; bullets
        // keep the context alive, non-list blocks reset it.
        var ordCounters = new List<int>();
        foreach (var block in doc.Blocks)
        {
            int ordered = 0;
            if (block is Paragraph { IsListItem: true } p)
            {
                int lvl = Math.Clamp(p.ListLevel, 0, 16);
                if (ordCounters.Count > lvl + 1) ordCounters.RemoveRange(lvl + 1, ordCounters.Count - lvl - 1);
                if (p.ListType == ListKind.Ordered)
                {
                    while (ordCounters.Count <= lvl) ordCounters.Add(0);
                    ordered = ++ordCounters[lvl];
                }
            }
            else ordCounters.Clear();
            WriteBlock(block, ordered);
        }

        var sb = new StringBuilder();
        // \uc1: each \uN Unicode escape is followed by exactly one fallback character (the '?' WriteEscaped
        // emits). It is the default, but readers that saw a different \ucN earlier in their session have
        // been known to carry it over — stating it removes the ambiguity for a two-byte cost.
        sb.Append(@"{\rtf1\ansi\ansicpg1252\uc1\deff0");
        sb.Append(@"{\fonttbl");
        for (int i = 0; i < _fonts.Count; i++)
            sb.Append($@"{{\f{i}\fnil ").Append(EscapeText(_fonts[i].Length == 0 ? "Default" : _fonts[i])).Append(";}");
        sb.Append('}');
        sb.Append(@"{\colortbl;");
        foreach (var c in _colors) sb.Append($@"\red{c.R}\green{c.G}\blue{c.B};");
        sb.Append('}').Append('\n');
        sb.Append(_body);
        sb.Append('}');
        return sb.ToString();
    }

    private void WriteBlock(Block block, int ordered)
    {
        switch (block)
        {
            case Paragraph p: WriteParagraph(p, ordered); break;
            case TableBlock tb: WriteTable(tb); break;
            case ImageBlock ib when ib.RawBytes != null:
                _body.Append(@"\pard ");
                WritePict(ib.RawBytes, ib.MimeType, ib.Width, ib.Height);
                _body.Append(@"\par").Append('\n');
                break;
            case DividerBlock:
                _body.Append(@"\pard\brdrb\brdrs\brdrw10\brsp20 \par").Append('\n');
                break;
        }
    }

    // "\pard" + this paragraph's own properties. Factored out because a paragraph that hosts an inline
    // table is emitted as several RTF paragraphs (see WriteParagraph) and each one must restate them.
    private void WriteParagraphProps(Paragraph p)
    {
        _body.Append(@"\pard");
        // ALWAYS emit the alignment, including \ql for left. In the spec \pard resets alignment to left,
        // but HWP treats \pard as "back to the current defaults" and keeps a previously seen \qr — so a
        // single right-aligned paragraph turned every following one right-aligned on paste. Being explicit
        // costs 3 bytes per paragraph and removes the reader-dependent behaviour entirely.
        switch (p.TextAlignment)
        {
            case TextAlignment.Center: _body.Append(@"\qc"); break;
            case TextAlignment.Right: _body.Append(@"\qr"); break;
            case TextAlignment.Justify: _body.Append(@"\qj"); break;
            default: _body.Append(@"\ql"); break;
        }
        if (p.Indent > 0) _body.Append($@"\li{(int)(p.Indent * 15)}");
        // Line spacing (see the parser's \sl/\slmult cases): proportional = N/240 lines with \slmult1,
        // absolute = negative twips ("exactly") with \slmult0.
        if (!double.IsNaN(p.LineSpacing) && p.LineSpacing > 0)
            _body.Append($@"\sl{(int)Math.Round(p.LineSpacing * 240)}\slmult1");
        else if (!double.IsNaN(p.LineHeight) && p.LineHeight > 0)
            _body.Append($@"\sl-{(int)Math.Round(p.LineHeight * 15)}\slmult0");
        _body.Append(' ');
    }

    private void WriteParagraph(Paragraph p, int ordered)
    {
        WriteParagraphProps(p);

        // Whether anything has been written into the paragraph currently open. Only a paragraph with
        // content needs closing with \par before a table interrupts it; without this an inline table that
        // is the FIRST thing in its host paragraph — the ordinary "글자처럼 취급" shape, where the table is
        // all the paragraph holds — emitted a \par against an empty paragraph and Word/HWP showed a blank
        // line above the table. (The empty paragraph AFTER a table is different: RTF requires one.)
        bool wrote = false;

        if (p.ListType != ListKind.None)
        {
            WriteEscaped(ListMarkers.Text(p.ListType, p.ListMarker, ordered));
            _body.Append(@"\tab ");
            wrote = true;
        }

        bool heading = p.HeadingLevel is >= 1 and <= 6;
        double headingSize = heading ? HeadingSize(p.HeadingLevel) : 0;
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && !string.IsNullOrEmpty(r.Text)) { WriteRun(r, heading, headingSize); wrote = true; }
            else if (inline is InlineImage img && img.RawBytes != null) { WritePict(img.RawBytes, img.MimeType, img.Width, img.Height); wrote = true; }
            else if (inline is InlineTable itbl)
            {
                // An INLINE table ("treat as character") lives in the paragraph's text flow, but RTF has
                // no inline grid: the only way to keep it a TABLE is to close this paragraph, emit real
                // rows, and reopen the paragraph for whatever follows — which is exactly what Word does
                // for a table between two runs of text. Flattening it to tab-separated text (the first
                // attempt) kept the words but lost the grid, which reads as "the table disappeared".
                // A trailing empty paragraph is deliberate: RTF requires a paragraph after a table.
                bool opensHost = !wrote; // nothing preceded it, so no \par closes a host paragraph below
                if (wrote) _body.Append(@"\par").Append('\n');
                // Ours, and deliberately an ignorable destination: {\*\...} groups are skipped by
                // definition, so every other reader still sees exactly the block-level table it saw
                // before. Our own reader takes the marker and puts the table back on the text line, so
                // RTF joins .flow/JSON/HTML in round-tripping an inline table through our save/load.
                // The parameter says whether the table OPENS its host paragraph: without it the reader
                // cannot tell that shape from "text, table, more text" and reattaches the table to the
                // PREVIOUS paragraph, swallowing it.
                _body.Append(opensHost ? @"{\*\arinline1}" : @"{\*\arinline0}");
                WriteTable(itbl.Table);   // ends with \pard\plain
                WriteParagraphProps(p);
                wrote = false;            // the reopened paragraph starts empty again
                continue;
            }
        }
        _body.Append(@"\par").Append('\n');
    }

    private void WriteRun(Run r, bool heading, double headingSize)
    {
        // Hyperlink as a real field: {\field{\*\fldinst{HYPERLINK "url"}}{\fldrslt {...}}} — Word/HWP
        // then keep the target address (the bare \ul form only looked like a link).
        bool link = !string.IsNullOrEmpty(r.NavigateUri);
        if (link)
            _body.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(EscapeText(r.NavigateUri!)).Append("\"}}{\\fldrslt ");
        _body.Append('{');
        if (r.FontWeight.IsBold() || heading) _body.Append(@"\b");
        if (r.FontStyle == FontStyle.Italic) _body.Append(@"\i");
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Underline) || !string.IsNullOrEmpty(r.NavigateUri)) _body.Append(@"\ul");
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)) _body.Append(@"\strike");
        int f = FontIndex(r.FontFamily);
        if (f > 0) _body.Append($@"\f{f}");
        double size = r.FontSize <= 0 ? 10 : r.FontSize; // pt; body default
        if (heading && (r.FontSize <= 0 || Math.Abs(r.FontSize - 10) < 0.01)) size = headingSize;
        _body.Append($@"\fs{(int)Math.Round(size * 2)}"); // \fs is half-points
        int c = ColorIndex(r.Foreground);
        if (c > 0) _body.Append($@"\cf{c}");
        int hb = ColorIndex(r.Background, blackIsDefault: false);
        if (hb > 0) _body.Append($@"\highlight{hb}"); // run highlight, round-trips with the parser
        _body.Append(' ');
        WriteEscaped(r.Text!);
        _body.Append('}');
        if (link) _body.Append("}}");
    }

    // depth 1 = a top-level table (\cell/\row); deeper = a table inside a cell, written as Word does with
    // \itapN + \nestcell/\nestrow so it comes back as a real nested table instead of flattened text.
    private void WriteTable(TableBlock tb, int depth = 1)
    {
        for (int row = 0; row < tb.Rows; row++)
        {
            // The row definition (\trowd + every \cellx) is emitted TWICE at the top level: once before
            // the cell text and once again immediately before \row. The spec allows the single form, but
            // Word writes the repeated form and strict readers — HWP among them — need the definition
            // still in scope at \row. Without the repeat HWP dropped the row structure entirely and
            // pasted the cells as plain lines (the "표가 사라져" report).
            string rowDef = BuildRowDefinition(tb, row);

            if (depth == 1) _body.Append(@"\pard").Append(rowDef);
            for (int col = 0; col < tb.Columns; col++)
            {
                var (ar, ac) = tb.AnchorOf(row, col);
                var (cs, _) = tb.SpanOf(ar, ac);
                // One cell per horizontal span — the row definition emits one \cellx for it, and the two
                // must agree or the reader loses the row. (Vertically covered slots still get their own
                // cell; only the horizontal run collapses.)
                if (col != ac + cs - 1) continue;

                // \itapN = nesting depth; modern readers use it to tell table paragraphs from body text.
                _body.Append(@"\pard\intbl\itap").Append(depth).Append(@"\ql ");
                // Only the merge anchor carries content; a vertically covered slot emits an empty cell.
                if (ar == row && WriteCellContent(tb.Cells[row][ac], depth))
                {
                    // A nested table leaves \itap at ITS depth, so re-declare this cell's own before
                    // closing — otherwise the reader books this cell into the deeper table.
                    _body.Append(@"\pard\intbl\itap").Append(depth).Append(@"\ql ");
                }
                _body.Append(depth == 1 ? @"\cell" : @"\nestcell");
            }
            if (depth == 1)
                _body.Append(rowDef).Append(@"\row").Append('\n');
            else
                // \nesttableprops is ignorable, so a reader that doesn't do nested tables still sees the
                // cell text rather than a corrupt document. \nonesttables is the same fallback for very
                // old readers — our own parser skips it so its \par doesn't land in the parent cell.
                _body.Append(@"{\*\nesttableprops").Append(rowDef).Append(@"\nestrow}{\nonesttables\par}").Append('\n');
        }
        // \plain too: reset the character formatting the last cell left in scope, so body text after the
        // table doesn't inherit it.
        if (depth == 1) _body.Append(@"\pard\plain").Append('\n');
    }

    // Everything a cell can hold: several paragraphs (separated by \par), block images, dividers, and
    // tables — nested ones and the inline tables living in a cell paragraph, both written one \itap
    // deeper. Returns true when it wrote such a table, so the caller can re-declare this cell's depth.
    private bool WriteCellContent(TableCell cell, int depth)
    {
        bool first = true, wroteNested = false;

        // A nested table leaves \itap at ITS depth and consumes the paragraph properties. Anything this
        // cell writes afterwards has to re-open the cell's own level first, or Word books that text into
        // the deeper table and drops it (a paragraph after a nested table vanished entirely).
        void ReopenCell()
        {
            _body.Append(@"\pard\intbl\itap").Append(depth).Append(@"\ql ");
            // A fresh paragraph is now open at this cell's level, so the next block writes straight into
            // it rather than prefixing another \par (which would leave a blank line).
            first = true;
        }
        // Terminate whatever this cell has written so far before descending into a nested table. Without
        // it the preceding paragraph's text is not closed and Word glues it onto the first nested cell.
        void CloseBeforeNested()
        {
            if (!first) _body.Append(@"\par ");
            first = false;
        }

        foreach (var blk in cell.Blocks)
        {
            if (blk is Paragraph cpara)
            {
                if (!first) _body.Append(@"\par ");
                first = false;
                if (cpara.ListType != ListKind.None)
                {
                    WriteEscaped(ListMarkers.Text(cpara.ListType, cpara.ListMarker, 1));
                    _body.Append(@"\tab ");
                }
                bool heading = cpara.HeadingLevel is >= 1 and <= 6;
                double headingSize = heading ? HeadingSize(cpara.HeadingLevel) : 0;
                foreach (var inline in cpara.Inlines)
                {
                    if (inline is InlineTable it)
                    {
                        CloseBeforeNested();
                        WriteTable(it.Table, depth + 1);
                        wroteNested = true;
                        ReopenCell(); // the rest of this paragraph belongs to THIS cell, not the inner table
                    }
                    else if (inline is Run r && !string.IsNullOrEmpty(r.Text)) WriteRun(r, heading, headingSize);
                    else if (inline is InlineImage cii && cii.RawBytes != null)
                        WritePict(cii.RawBytes, cii.MimeType, cii.Width, cii.Height);
                }
            }
            else if (blk is TableBlock nested)
            {
                CloseBeforeNested();
                WriteTable(nested, depth + 1);
                wroteNested = true;
                ReopenCell();
            }
            else if (blk is ImageBlock cib && cib.RawBytes != null)
            {
                if (!first) _body.Append(@"\par ");
                first = false;
                WritePict(cib.RawBytes, cib.MimeType, cib.Width, cib.Height);
            }
            else if (blk is DividerBlock)
            {
                if (!first) _body.Append(@"\par ");
                first = false; // no per-cell rule in this subset; the break keeps blocks separated
            }
        }
        return wroteNested;
    }

    // "\trowd" + the per-cell properties and \cellx boundaries for one row. Built as a string because
    // WriteTable emits it both before the cells and again before \row (see there). Not static: cell
    // shading has to register its colour in the document's colour table (ColorIndex).
    private string BuildRowDefinition(TableBlock tb, int row)
    {
        var d = new StringBuilder();
        // \trgaph = half the gap between cell text and border; \trleft = row origin. Word always writes
        // both, and readers that default them differently otherwise lay the row out at the wrong offset.
        d.Append(@"\trowd\trgaph108\trleft0");
        int x = 0;
        for (int col = 0; col < tb.Columns; col++)
        {
            var (ar, ac) = tb.AnchorOf(row, col);
            var (cs, rs) = tb.SpanOf(ar, ac);

            // HORIZONTAL merge is expressed geometrically: the merged span is ONE cell whose \cellx sits
            // at the far edge, so covered columns contribute width but no \cellx of their own. This is the
            // base RTF table model; Word also accepts the flag form (one \cellx per column with
            // \clmgf/\clmrg), but HWP does not — it dissolved the merge and showed the covered columns as
            // separate empty cells. Word reads the geometric form correctly too, so one form serves both.
            // (Writer rule, re-learned: the target is real readers, not the spec.)
            int wpx = col < tb.ColumnWidths.Count ? (int)tb.ColumnWidths[col] : 100;
            x += wpx * 15;
            // Emit at the LAST column of the horizontal span, so \cellx is the merged cell's right edge
            // and it has swallowed every covered column's width.
            if (col != ac + cs - 1) continue;

            // VERTICAL merge has no geometric encoding — every row still has a cell in this column — so it
            // keeps the flag form, which HWP does honour.
            if (rs > 1) d.Append(row == ar ? @"\clvmgf" : @"\clvmrg");
            switch (tb.Cells[ar][ac].VerticalAlignment)
            {
                case CellVerticalAlignment.Center: d.Append(@"\clvertalc"); break;
                case CellVerticalAlignment.Bottom: d.Append(@"\clvertalb"); break;
            }
            // Cell shading uses the colour table, like text colour. Without it the cell background set
            // in the editor (right-click ▸ 셀 배경색) was dropped by every RTF consumer — including our
            // own reader, so it didn't even survive our save/load.
            // blackIsDefault: false — a black cell background is a real choice, not "unset" (same reason
            // highlights pass it; index 0 means "no shading" here).
            int bg = ColorIndex(tb.Cells[ar][ac].Background, blackIsDefault: false);
            if (bg > 0) d.Append($@"\clcbpat{bg}");
            // Per-cell borders (single line, ~0.5pt) on all four sides — must precede this cell's
            // \cellx. Without them HWP/Word render the table with no visible lines (transparent grid).
            d.Append(@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10");
            d.Append($@"\cellx{x}");
        }
        return d.ToString();
    }

    // {\*\shppict{\pict ...}} ??the modern wrapper our parser un-skips; bytes go out as hex, size in twips.
    private void WritePict(byte[] bytes, string? mime, double w, double h)
    {
        _body.Append(@"{\*\shppict{\pict");
        _body.Append(mime != null && mime.Contains("jpeg", StringComparison.OrdinalIgnoreCase) ? @"\jpegblip" : @"\pngblip");
        // Natural pixel size (\picw/\pich) alongside the display goals: some consumers (HWP notably)
        // ignore or misplace a \pict that only carries goals.
        var (nw, nh) = ImageInfo.GetPixelSize(bytes);
        if (nw > 0 && nh > 0) _body.Append($@"\picw{(int)nw}\pich{(int)nh}");
        if (w > 0) _body.Append($@"\picwgoal{(int)(w * 15)}");
        if (h > 0) _body.Append($@"\pichgoal{(int)(h * 15)}");
        _body.Append(' ');
        // Single-allocation hex: the per-byte ToString("x2") loop allocated one string per byte, and
        // this runs on EVERY copy that contains an image (SetClipboardFromSelection writes RTF).
        _body.Append(Convert.ToHexStringLower(bytes));
        _body.Append("}}");
    }

    private int FontIndex(string? family)
    {
        if (string.IsNullOrEmpty(family)) return 0;
        if (_fontIndex.TryGetValue(family, out var i)) return i;
        i = _fonts.Count;
        _fonts.Add(family);
        _fontIndex[family] = i;
        return i;
    }

    private int ColorIndex(Color? c, bool blackIsDefault = true)
    {
        if (c is not { } col) return 0;
        // Black is the default text colour ??no \cf needed (matches the model default where a null
        // foreground also renders black). Highlights pass blackIsDefault: false — index 0 means
        // "no highlight" there, so a black highlight must get a real colour-table entry.
        if (blackIsDefault && col.R == 0 && col.G == 0 && col.B == 0) return 0;
        uint key = ((uint)col.R << 16) | ((uint)col.G << 8) | col.B;
        if (_colorIndex.TryGetValue(key, out var i)) return i;
        _colors.Add(col);
        i = _colors.Count; // 1-based: \colortbl entry 0 is the auto colour
        _colorIndex[key] = i;
        return i;
    }

    private static double HeadingSize(int level)
        => level switch { 1 => 20, 2 => 16, 3 => 14, 4 => 12, 5 => 11, 6 => 10, _ => 10 };

    private void WriteEscaped(string text) => _body.Append(EscapeText(text));

    private static string EscapeText(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (char ch in text)
        {
            if (ch == '\\' || ch == '{' || ch == '}') sb.Append('\\').Append(ch);
            else if (ch == '\n') sb.Append(@"\line ");
            else if (ch == '\r') { /* skip */ }
            else if (ch < 128) sb.Append(ch);
            else { int code = ch > 0x7FFF ? ch - 0x10000 : ch; sb.Append(@"\u").Append(code.ToString(CultureInfo.InvariantCulture)).Append('?'); }
        }
        return sb.ToString();
    }
}
