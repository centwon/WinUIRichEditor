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
/// Parses a practical subset of RTF ??the "Rich Text Format" both Word and the Korean HWP put on
/// the clipboard ??into a <see cref="FlowDocument"/>: paragraphs, bold/italic/underline/strike,
/// font size, foreground colour, embedded images (<c>\pict</c> PNG/JPEG, bytes carried inline), and
/// simple tables (<c>\trowd??cell??row</c>). Zero external dependencies beyond a code-page provider
/// for CJK text (<c>\'hh</c> bytes are decoded with the document's <c>\ansicpg</c>).
/// </summary>
public static class RtfDocumentFormatter
{
    static RtfDocumentFormatter()
    {
        // CP949 (Korean), Shift-JIS, GB2312 etc. aren't in .NET's default set ??register them so
        // \'hh runs from HWP/Word decode correctly.
        try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); } catch { }
    }

    /// <summary>True if <paramref name="text"/> starts with the RTF signature.</summary>
    public static bool LooksLikeRtf(string? text)
        => text != null && text.TrimStart().StartsWith(@"{\rtf", StringComparison.Ordinal);

    /// <summary>Parses an RTF string into a <see cref="FlowDocument"/> (empty document on failure).</summary>
    public static FlowDocument Parse(string rtf)
    {
        try { return RunNormalizer.Compact(new RtfParser(rtf).Run()); }
        catch { return new FlowDocument(); }
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

    private List<List<Paragraph>>? _tableRows;
    private List<Paragraph>? _curRow;
    private List<int> _curCellx = new();
    private List<int>? _tableCellx;

    // Merge flags per cell definition (\clmgf/\clmrg horizontal, \clvmgf/\clvmrg vertical). Flags
    // precede their \cellx in the row definition; FinalizeTable turns them back into col/row spans.
    private struct CellDef { public bool HFirst, HCont, VFirst, VCont; public CellVerticalAlignment VAlign; }
    private CellDef _pendingCellDef;
    private List<CellDef> _curCellDefs = new();
    private List<List<CellDef>>? _tableRowDefs;

    public RtfParser(string s) => _s = s;

    public FlowDocument Run()
    {
        while (_i < _s.Length)
        {
            char c = _s[_i];
            if (c == '{') { _stack.Push(_st); _i++; }
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

            case "trowd": StartRow(); break;
            case "cell": EndCell(); break;
            case "row": EndRow(); break;
            case "intbl": break;
            case "cellx": _curCellx.Add(p ?? 0); _curCellDefs.Add(_pendingCellDef); _pendingCellDef = default; break;

            case "clmgf": _pendingCellDef.HFirst = true; break;
            case "clmrg": _pendingCellDef.HCont = true; break;
            case "clvmgf": _pendingCellDef.VFirst = true; break;
            case "clvmrg": _pendingCellDef.VCont = true; break;
            case "clvertalt": _pendingCellDef.VAlign = CellVerticalAlignment.Top; break;
            case "clvertalc": _pendingCellDef.VAlign = CellVerticalAlignment.Center; break;
            case "clvertalb": _pendingCellDef.VAlign = CellVerticalAlignment.Bottom; break;

            case "nestcell": if (_st.Dest == Dest.Normal) _bytes.Add(9); break;
            case "nestrow": if (_st.Dest == Dest.Normal) _bytes.Add(10); break;

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
        if (_curRow != null) { _bytes.Add(10); return; }
        FlushRun();
        FinalizeTable();
        _doc.Blocks.Add(_para);
        _para = new Paragraph();
    }

    // ---- tables ----

    private void StartRow()
    {
        _tableRows ??= new List<List<Paragraph>>();
        _curRow ??= new List<Paragraph>();
        _curCellx = new List<int>();
        _curCellDefs = new List<CellDef>();
        _pendingCellDef = default;
        _para = new Paragraph();
    }

    private void EndCell()
    {
        if (_curRow == null) StartRow();
        FlushRun();
        _curRow!.Add(_para);
        _para = new Paragraph();
    }

    private void EndRow()
    {
        if (_curRow == null) return;
        _tableRows ??= new List<List<Paragraph>>();
        _tableRows.Add(_curRow);
        _curRow = null;
        if (_tableCellx == null && _curCellx.Count > 0) _tableCellx = _curCellx;
        (_tableRowDefs ??= new List<List<CellDef>>()).Add(_curCellDefs);
        _curCellDefs = new List<CellDef>();
    }

    private void FinalizeTable()
    {
        var rows = _tableRows;
        var cellx = _tableCellx;
        var defs = _tableRowDefs;
        _tableRows = null;
        _curRow = null;
        _tableCellx = null;
        _tableRowDefs = null;
        if (rows == null || rows.Count == 0) return;

        int cols = 0;
        foreach (var r in rows) if (r.Count > cols) cols = r.Count;
        if (cols == 0) return;

        var tb = new TableBlock(rows.Count, cols);
        tb.Cells.Clear();
        foreach (var r in rows)
        {
            var cells = new List<TableCell>(cols);
            for (int c = 0; c < cols; c++) cells.Add(c < r.Count ? new TableCell(r[c]) : new TableCell());
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

        // Rebuild merges from the row definitions' \clmgf/\clmrg/\clvmgf/\clvmrg flags: an anchor is a
        // slot that is no continuation; its colspan = following \clmrg run in the row, its rowspan =
        // the \clvmrg run straight down. SetSpan stamps the covered slots.
        if (defs != null)
        {
            for (int r = 0; r < rows.Count && r < defs.Count; r++)
            {
                var rowDefs = defs[r];
                for (int c = 0; c < cols && c < rowDefs.Count; c++)
                {
                    var d = rowDefs[c];
                    tb.Cells[r][c].VerticalAlignment = d.VAlign;
                    if (d.HCont || d.VCont) continue; // covered slot; its anchor stamps it
                    int cspan = 1, rspan = 1;
                    while (c + cspan < rowDefs.Count && rowDefs[c + cspan].HCont) cspan++;
                    for (int r2 = r + 1; r2 < rows.Count && r2 < defs.Count; r2++)
                    {
                        if (c >= defs[r2].Count || !defs[r2][c].VCont) break;
                        rspan++;
                    }
                    if (cspan > 1 || rspan > 1) tb.SetSpan(r, c, cspan, rspan);
                }
            }
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
        catch (FormatException) { return null; }
    }

    private static Encoding GetEncoding(int codepage)
    {
        try { return Encoding.GetEncoding(codepage); }
        catch { return Encoding.Latin1; }
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
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0");
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

    private void WriteParagraph(Paragraph p, int ordered)
    {
        _body.Append(@"\pard");
        switch (p.TextAlignment)
        {
            case TextAlignment.Center: _body.Append(@"\qc"); break;
            case TextAlignment.Right: _body.Append(@"\qr"); break;
            case TextAlignment.Justify: _body.Append(@"\qj"); break;
        }
        if (p.Indent > 0) _body.Append($@"\li{(int)(p.Indent * 15)}");
        // Line spacing (see the parser's \sl/\slmult cases): proportional = N/240 lines with \slmult1,
        // absolute = negative twips ("exactly") with \slmult0.
        if (!double.IsNaN(p.LineSpacing) && p.LineSpacing > 0)
            _body.Append($@"\sl{(int)Math.Round(p.LineSpacing * 240)}\slmult1");
        else if (!double.IsNaN(p.LineHeight) && p.LineHeight > 0)
            _body.Append($@"\sl-{(int)Math.Round(p.LineHeight * 15)}\slmult0");
        _body.Append(' ');

        if (p.ListType != ListKind.None)
        {
            WriteEscaped(ListMarkers.Text(p.ListType, p.ListMarker, ordered));
            _body.Append(@"\tab ");
        }

        bool heading = p.HeadingLevel is >= 1 and <= 6;
        double headingSize = heading ? HeadingSize(p.HeadingLevel) : 0;
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && !string.IsNullOrEmpty(r.Text)) WriteRun(r, heading, headingSize);
            else if (inline is InlineImage img && img.RawBytes != null) WritePict(img.RawBytes, img.MimeType, img.Width, img.Height);
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

    private void WriteTable(TableBlock tb)
    {
        for (int row = 0; row < tb.Rows; row++)
        {
            _body.Append(@"\trowd");
            int x = 0;
            for (int col = 0; col < tb.Columns; col++)
            {
                int wpx = col < tb.ColumnWidths.Count ? (int)tb.ColumnWidths[col] : 100;
                x += wpx * 15;
                // Merge flags must precede this cell's \cellx: \clmgf/\clmrg for the horizontal run,
                // \clvmgf/\clvmrg for the vertical one (Word emits both on doubly-merged slots).
                // Without them, a merged table pastes into Word/HWP with the merge dissolved.
                var (ar, ac) = tb.AnchorOf(row, col);
                var (cs, rs) = tb.SpanOf(ar, ac);
                if (cs > 1) _body.Append(col == ac ? @"\clmgf" : @"\clmrg");
                if (rs > 1) _body.Append(row == ar ? @"\clvmgf" : @"\clvmrg");
                switch (tb.Cells[ar][ac].VerticalAlignment)
                {
                    case CellVerticalAlignment.Center: _body.Append(@"\clvertalc"); break;
                    case CellVerticalAlignment.Bottom: _body.Append(@"\clvertalb"); break;
                }
                // Per-cell borders (single line, ~0.5pt) on all four sides — must precede this cell's
                // \cellx. Without them HWP/Word render the table with no visible lines (transparent grid).
                _body.Append(@"\clbrdrt\brdrs\brdrw10\clbrdrl\brdrs\brdrw10\clbrdrb\brdrs\brdrw10\clbrdrr\brdrs\brdrw10");
                _body.Append($@"\cellx{x}");
            }
            for (int col = 0; col < tb.Columns; col++)
            {
                _body.Append(@"\pard\intbl ");
                // Only the merge anchor carries content; covered slots emit an empty \cell.
                var (ar, ac) = tb.AnchorOf(row, col);
                if (ar == row && ac == col)
                {
                    var cell = tb.Cells[row][col];
                    bool firstBlk = true;
                    foreach (var cblk in cell.Blocks)
                    {
                        switch (cblk)
                        {
                            case Paragraph cpara:
                                if (!firstBlk) _body.Append(@"\par ");
                                firstBlk = false;
                                foreach (var inline in cpara.Inlines)
                                {
                                    if (inline is Run r && !string.IsNullOrEmpty(r.Text)) WriteRun(r, false, 0);
                                    else if (inline is InlineImage cii && cii.RawBytes != null)
                                        WritePict(cii.RawBytes, cii.MimeType, cii.Width, cii.Height);
                                }
                                break;
                            case ImageBlock cib when cib.RawBytes != null:
                                if (!firstBlk) _body.Append(@"\par ");
                                firstBlk = false;
                                WritePict(cib.RawBytes, cib.MimeType, cib.Width, cib.Height);
                                break;
                            case TableBlock nested:
                                if (!firstBlk) _body.Append(@"\par ");
                                firstBlk = false;
                                WriteNestedTableAsText(nested);
                                break;
                            case DividerBlock:
                                if (!firstBlk) _body.Append(@"\par ");
                                firstBlk = false;
                                break; // no per-cell rule in this subset; the break keeps blocks separated
                        }
                    }
                }
                _body.Append(@"\cell");
            }
            _body.Append(@"\row").Append('\n');
        }
        _body.Append(@"\pard").Append('\n');
    }

    // A table nested inside a cell flattens to text (tab between cells, \line between rows). True RTF
    // nesting (\itapN + \nestcell/\nestrow) is outside this writer's practical subset — and hard to get
    // Word-valid blind — but dropping the content entirely was worse. Content, not structure, survives.
    private void WriteNestedTableAsText(TableBlock tb)
    {
        for (int r = 0; r < tb.Rows; r++)
        {
            if (r > 0) _body.Append(@"\line ");
            bool firstCell = true;
            for (int c = 0; c < tb.Columns; c++)
            {
                if (tb.IsCovered(r, c)) continue;
                if (!firstCell) _body.Append(@"\tab ");
                firstCell = false;
                foreach (var blk in tb.Cells[r][c].Blocks)
                    if (blk is Paragraph p)
                        foreach (var inline in p.Inlines)
                            if (inline is Run run && !string.IsNullOrEmpty(run.Text)) WriteRun(run, false, 0);
            }
        }
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
