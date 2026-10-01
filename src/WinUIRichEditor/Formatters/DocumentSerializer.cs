using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.UI.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Formatters;

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FlowDocumentDto))]
internal partial class DocumentJsonContext : JsonSerializerContext
{
    // What every document is WRITTEN with. The default encoder escapes everything outside ASCII, so Korean text went
    // out as 정보… — six bytes a character where UTF-8 takes three, and a file no one could read or diff.
    // UnicodeRanges.All leaves letters as they are and still escapes what HTML is sensitive to (< > & ' "). Not
    // indented: indentation was half of every document. (Upstream 2026-10-01.)
    internal static DocumentJsonContext Wire { get; } = new(new JsonSerializerOptions
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    });
}

/// <summary>Serializes and deserializes a <see cref="FlowDocument"/> to/from the library's JSON format.
/// Images are stored as base64 of their original encoded bytes (no re-encoding); bitmaps
/// set without raw bytes fall back to base64 PNG. Uses AOT-safe source-generated JSON.</summary>
public static class DocumentSerializer
{
    /// <summary>Current document-format version written by <see cref="Serialize"/> (a SemVer string).
    /// The reader does not branch on it — it is an informational stamp — and accepts both the new string
    /// form and the legacy integer form.</summary>
    public const string CurrentSchemaVersion = "1.0";

    /// <summary>Serializes <paramref name="document"/> to a JSON string.</summary>
    public static string Serialize(FlowDocument document)
    {
        var (dto, images) = BuildDto(document);
        return SerializeDto(dto, images);
    }

    // Builds the DTO + image pool from the model.
    internal static (FlowDocumentDto Dto, Dictionary<string, (byte[] Bytes, string Mime)> Images) BuildDto(FlowDocument document)
    {
        var images = new Dictionary<string, (byte[] Bytes, string Mime)>();
        var dto = ToDto(document, images);
        return (dto, images);
    }

    // Pure-data half of Serialize: base64-encodes the image pool into the DTO and writes JSON.
    internal static string SerializeDto(FlowDocumentDto dto, Dictionary<string, (byte[] Bytes, string Mime)> images)
    {
        if (images.Count > 0)
        {
            dto.Images = new Dictionary<string, ImagePoolDto>();
            foreach (var (key, img) in images)
                dto.Images[key] = new ImagePoolDto { Data = Convert.ToBase64String(img.Bytes), MimeType = img.Mime };
        }
        return JsonSerializer.Serialize(dto, DocumentJsonContext.Wire.FlowDocumentDto);
    }

    // Builds the wire DTO; image bytes land in `images` keyed by content hash (the dto references
    // them via ImageRef). The caller picks the byte representation: base64 in the JSON pool
    // (Serialize) or zip entries (DocumentPackage).
    internal static FlowDocumentDto ToDto(FlowDocument document, Dictionary<string, (byte[] Bytes, string Mime)> images)
    {
        var blocks = new List<BlockDto>();
        foreach (var block in document.Blocks) blocks.Add(BlockToDto(block, images));
        // The marker only for a document that really carries its heading formats: a host's own model that no
        // editor has converted yet must stay "legacy" in the file, or it would load back with plain headings.
        var dto = new FlowDocumentDto { Version = CurrentSchemaVersion, Blocks = blocks, HeadingFormat = document.HeadingFormatsApplied ? 1 : null };
        // A document with no page setup is written without one, so plain documents keep their original format.
        // One that CARRIES a setup is written even when it looks default: an editor whose host defaults to A4
        // keeps a chosen Continuous on the document, and dropping it here reopened the file as A4 (measured
        // 2026-09-14) — "no setup" means "the host's", which is not "Continuous". The editor itself leaves the
        // setup off only when both the document's and the host's are plain (CapturePageSetupToDocument).
        if (document.PageSetup is { } ps)
            dto.PageSetup = new PageSetupDto
            {
                PageSize = ps.PageSize.ToString(),
                Orientation = ps.Orientation.ToString(),
                ShowPageBoundaries = ps.ShowPageBoundaries,
                Header = string.IsNullOrEmpty(ps.Header) ? null : ps.Header,
                Footer = string.IsNullOrEmpty(ps.Footer) ? null : ps.Footer,
                ShowPageNumbers = ps.ShowPageNumbers,
                MarginLeft = ps.Margin.Left == PageSetup.DefaultMargin.Left ? null : ps.Margin.Left,
                MarginTop = ps.Margin.Top == PageSetup.DefaultMargin.Top ? null : ps.Margin.Top,
                MarginRight = ps.Margin.Right == PageSetup.DefaultMargin.Right ? null : ps.Margin.Right,
                MarginBottom = ps.Margin.Bottom == PageSetup.DefaultMargin.Bottom ? null : ps.Margin.Bottom,
            };
        return dto;
    }

    /// <summary>Deserializes a <see cref="FlowDocument"/> from a JSON string produced by <see cref="Serialize"/>.
    /// Image decoding is deferred to first render.</summary>
    /// <exception cref="JsonException"><paramref name="json"/> is not valid JSON. Malformed input is
    /// reported rather than silently read as an empty document, so a host cannot mistake a damaged file
    /// for an empty one and overwrite it. A literal <c>null</c> is valid JSON and does yield an empty
    /// document.</exception>
    public static FlowDocument Deserialize(string json)
    {
        var (dto, pool) = ParseJson(json);
        return FromDto(dto, pool);
    }

    // The editor's load paths (LoadJson/LoadJsonAsync): Deserialize plus the check that the JSON is a
    // document of this library at all. Any JSON OBJECT used to load — unknown properties are ignored — so
    // opening some other application's .json replaced the open document with an empty one, marked it saved,
    // and the next save wrote the blank over the original (decided 2026-09-12). The public Deserialize keeps
    // its lenient behaviour.
    internal static FlowDocument DeserializeDocument(string json)
    {
        var (dto, pool) = ParseJson(json);
        EnsureDocumentShape(dto);
        return FromDto(dto, pool);
    }

    // Every document this library writes has "Blocks" (ToDto always sets it, even when empty); an object
    // without it is something else. A literal null stays an empty document, as Deserialize documents.
    internal static void EnsureDocumentShape(FlowDocumentDto? dto)
    {
        if (dto != null && dto.Blocks == null)
            throw new JsonException("The JSON is not a WinUIRichEditor document: it has no \"Blocks\".");
        // A newer MAJOR format is one this reader cannot promise to read whole: it would come in with what it does
        // not know turned into empty paragraphs, and a save would write that over the file. Refused like a damaged
        // file, so the host tells the user instead. (Legacy integer versions "1" and "2" predate "1.0" and read.)
        if (dto != null && MajorVersion(dto.Version) is int major && major > SupportedMajorVersion)
            throw new JsonException($"The document is format {dto.Version}, newer than this reader ({CurrentSchemaVersion}).");
    }

    private const int SupportedMajorVersion = 1;

    // The major of a SemVer "M.m" version; null for the legacy integer form, which has no dot.
    private static int? MajorVersion(string? version)
    {
        if (version == null) return null;
        int dot = version.IndexOf('.');
        return dot > 0 && int.TryParse(version.AsSpan(0, dot), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out int major) ? major : null;
    }

    // Thread-free half of Deserialize: JSON parsing + base64 decode only, no model objects.
    internal static (FlowDocumentDto? Dto, Dictionary<string, (byte[] Bytes, string Mime)> Pool) ParseJson(string json)
    {
        // Malformed input is NOT swallowed. Reading a damaged file as an empty document is the worst
        // outcome available: the host cannot tell "this file was empty" from "this file is damaged",
        // shows a blank editor, and a save then overwrites a recoverable file with nothing.
        var dto = JsonSerializer.Deserialize(json, DocumentJsonContext.Default.FlowDocumentDto);
        var pool = new Dictionary<string, (byte[] Bytes, string Mime)>();
        if (dto?.Images != null)
            foreach (var (key, entry) in dto.Images)
            {
                if (entry == null) continue; // `"Images":{"k":null}` threw NullReferenceException out of the load
                var bytes = TryFromBase64(entry.Data);
                if (bytes != null) pool[key] = (bytes, ImageMime.Safe(entry.MimeType ?? "image/png", bytes));
            }
        return (dto, pool);
    }

    // Rebuilds the document from a DTO plus the resolved image pool.
    internal static FlowDocument FromDto(FlowDocumentDto? dto, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        var doc = new FlowDocument { HeadingFormatsApplied = dto?.HeadingFormat >= 1 };
        if (dto?.Blocks != null)
            foreach (var bd in dto.Blocks)
            {
                var b = DtoToBlock(bd, pool);
                if (b != null) doc.Blocks.Add(b);
            }
        if (dto?.PageSetup is { } psd)
            doc.PageSetup = new PageSetup
            {
                PageSize = Enum.TryParse<RichEditorPageSize>(psd.PageSize, out var sz) ? sz : RichEditorPageSize.Continuous,
                Orientation = Enum.TryParse<RichEditorPageOrientation>(psd.Orientation, out var or) ? or : RichEditorPageOrientation.Portrait,
                ShowPageBoundaries = psd.ShowPageBoundaries,
                Header = psd.Header,
                Footer = psd.Footer,
                ShowPageNumbers = psd.ShowPageNumbers,
                Margin = ReadMargin(psd),
            };
        // A file without the HeadingFormat marker (older, or upstream's) is read as it is — the marker's
        // absence stays on the document, and the EDITOR converts it when it receives it (OnDocumentAssigned).
        // Converting here broke the native formats' contract, a document read back exactly as it was saved
        // (DocumentFuzzTests caught it on the first run).
        return RunNormalizer.Compact(doc);
    }

    // ---- model -> dto ----

    private static BlockDto BlockToDto(Block block, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        switch (block)
        {
            case Paragraph p:
                return ParagraphToDto(p, pool);
            case DividerBlock dv:
                return new BlockDto
                {
                    Type = "Divider",
                    MarginTop = double.IsNaN(dv.MarginTop) ? null : dv.MarginTop, // see the table branch
                    MarginBottom = Unless(dv.MarginBottom, 0),
                };
            case ImageBlock img:
                return new BlockDto
                {
                    Type = "Image",
                    ImageRef = PoolImage(pool, img.RawBytes, img.MimeType, img.RawBytes == null ? img.Image : null),
                    Width = NanToNull(img.Width),
                    Height = NanToNull(img.Height),
                    Alt = img.AltText,
                    Indent = Unless(img.Indent, 0),
                    MarginTop = double.IsNaN(img.MarginTop) ? null : img.MarginTop, // see the table branch
                    MarginBottom = Unless(img.MarginBottom, 10)
                };
            case TableBlock tb:
                var td = new BlockDto
                {
                    Type = "Table",
                    // Rows and Columns are not written: every reader rebuilds both from Cells (the widest row
                    // wins over a declared width, and an absent one reads as the narrowest).
                    Indent = Unless(tb.Indent, 0),
                    // NaN is "let the editor choose the gap" (Block.AutoTopMargin) and JSON has no NaN: it goes
                    // out as no field at all, and comes back as NaN on the next read.
                    MarginTop = double.IsNaN(tb.MarginTop) ? null : tb.MarginTop,
                    MarginBottom = Unless(tb.MarginBottom, 10),
                    ColumnWidths = new List<double>(tb.ColumnWidths),
                    RowHeights = tb.RowHeights.Count > 0 ? new List<double>(tb.RowHeights) : null,
                    Cells = new List<List<BlockDto>>()
                };
                foreach (var row in tb.Cells)
                {
                    var rd = new List<BlockDto>();
                    foreach (var cell in row)
                    {
                        // Backward-compatible cell encoding: a plain one-paragraph cell stays the legacy
                        // single block DTO with the cell background on it; a multi-block/non-paragraph cell
                        // is wrapped in a "Cell" DTO whose Blocks carry the full list (recursively).
                        //
                        // The legacy form has ONE Background field standing for two different fills, so it
                        // is only usable when the paragraph has no fill of its own: otherwise the
                        // assignment below overwrites the paragraph's with the cell's, and when the cell
                        // has none that means writing null — the paragraph's fill is erased outright, in
                        // this editor's own save format. A paragraph fill inside a cell arrives whenever
                        // HTML with a styled <p> in a <td> is pasted. The wrapper form keeps the two fills
                        // on separate DTOs, and readers already understand it (so does the upstream peer,
                        // which shares this bug and this encoding), so nothing about the format changes.
                        BlockDto cdto;
                        if (cell.Blocks.Count == 1 && cell.Blocks[0] is Paragraph cpara && cpara.Background == null)
                        {
                            cdto = ParagraphToDto(cpara, pool);
                        }
                        else
                        {
                            cdto = new BlockDto { Type = "Cell", Blocks = new List<BlockDto>() };
                            foreach (var b in cell.Blocks) cdto.Blocks.Add(BlockToDto(b, pool));
                        }
                        cdto.Background = ColorToString(cell.Background);
                        if (cell.VerticalAlignment != CellVerticalAlignment.Top)
                            cdto.VAlign = cell.VerticalAlignment.ToString(); // omit the default, keeping old docs byte-identical
                        rd.Add(cdto);
                    }
                    td.Cells.Add(rd);
                }
                // Only a table with a merge carries the span grids; absent, every reader reads them as all 1s.
                if (HasMerge(tb))
                {
                    td.ColSpans = new List<List<int>>();
                    td.RowSpans = new List<List<int>>();
                    foreach (var row in tb.ColSpans) td.ColSpans.Add(new List<int>(row));
                    foreach (var row in tb.RowSpans) td.RowSpans.Add(new List<int>(row));
                }
                return td;
            default:
                return new BlockDto { Type = "Paragraph", Inlines = new List<InlineDto>() };
        }
    }

    private static BlockDto ParagraphToDto(Paragraph p, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        // A field that holds what a reader assumes when it is absent is not written (Unless): every reader since
        // 1.0 defaults the same way, so they all read this back unchanged (upstream 2026-10-01, checked there with an
        // old tree and here likewise). "Paragraph" and "Run" are the readers' default Type too.
        var d = new BlockDto
        {
            Type = null,
            Inlines = new List<InlineDto>(),
            TextAlignment = p.TextAlignment == TextAlignment.Left ? null : p.TextAlignment.ToString(),
            LineHeight = NanToNull(p.LineHeight),
            LineSpacing = NanToNull(p.LineSpacing),
            // NaN (a host's AutoTopMargin on a paragraph) has no JSON spelling — writing it threw — and a paragraph
            // reads an absent top margin as 0.
            MarginTop = double.IsNaN(p.MarginTop) ? null : Unless(p.MarginTop, 0),
            MarginBottom = Unless(p.MarginBottom, 0),
            MarginRight = Unless(p.MarginRight, 0),
            ListType = p.ListType == ListKind.None ? null : p.ListType.ToString(),
            ListMarker = p.ListMarker == ListMarkerStyle.Default ? null : p.ListMarker.ToString(),
            HeadingLevel = Unless(p.HeadingLevel, 0),
            Background = ColorToString(p.Background),
            Indent = Unless(p.Indent, 0),
            IsQuote = Unless(p.IsQuote, false),
            ListLevel = Unless(p.ListLevel, 0)
        };
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r)
                d.Inlines.Add(new InlineDto
                {
                    Type = null,
                    Text = r.Text,
                    Bold = Unless(r.FontWeight.IsBold(), false),
                    Italic = Unless(r.FontStyle == FontStyle.Italic, false),
                    FontSize = Unless(r.FontSize, 10),
                    Foreground = ColorToString(r.Foreground),
                    Background = ColorToString(r.Background),
                    FontFamily = r.FontFamily,
                    NavigateUri = r.NavigateUri,
                    Underline = Unless(r.TextDecorations.HasFlag(TextDecorationFlags.Underline), false),
                    Strikethrough = Unless(r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough), false)
                });
            else if (inline is InlineImage im)
                d.Inlines.Add(new InlineDto
                {
                    Type = "Image",
                    ImageRef = PoolImage(pool, im.RawBytes, im.MimeType, im.RawBytes == null ? im.Image : null),
                    Width = NanToNull(im.Width),
                    Height = NanToNull(im.Height),
                    Alt = im.AltText
                });
            else if (inline is InlineTable it)
                d.Inlines.Add(new InlineDto { Type = "Table", Table = BlockToDto(it.Table, pool) });
        }
        return d;
    }

    // ---- dto -> model ----

    // Ceiling on a table's declared column count, which pads short rows and so gets allocated. The rows
    // themselves need no cap: they come from the cells present in the file, which the input size bounds.
    // Far beyond any real document, and matched to the HTML importer's own cap.
    private const int MaxTableDimension = 1000;

    // A file is untrusted input (it reaches here by Open and by paste), so a margin that leaves no page to
    // write on is dropped whole rather than per side — half a stated margin is not what the file meant.
    // Paper: what the file itself declares, since the margins are read alongside it.
    private static PageMargins ReadMargin(PageSetupDto psd)
    {
        var d = PageSetup.DefaultMargin;
        var m = new PageMargins(psd.MarginLeft ?? d.Left, psd.MarginTop ?? d.Top,
                                psd.MarginRight ?? d.Right, psd.MarginBottom ?? d.Bottom);
        var size = Enum.TryParse<RichEditorPageSize>(psd.PageSize, out var sz) ? sz : RichEditorPageSize.Continuous;
        var orient = Enum.TryParse<RichEditorPageOrientation>(psd.Orientation, out var or) ? or : RichEditorPageOrientation.Portrait;
        var (w, h) = PageSetup.PaperMillimetres(size, orient);
        return PageSetup.IsUsableMargin(m, w, h) ? m : d;
    }

    private static Block? DtoToBlock(BlockDto? d, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        if (d == null) return null; // a JSON null inside "Blocks" must not take the load down
        switch (d.Type)
        {
            case "Divider":
                return new DividerBlock { MarginTop = d.MarginTop ?? Block.AutoTopMargin, MarginBottom = d.MarginBottom ?? 0 };
            case "Image":
                {
                    var ib = new ImageBlock
                    {
                        Width = d.Width ?? double.NaN,
                        Height = d.Height ?? double.NaN,
                        AltText = d.Alt,
                        Indent = d.Indent ?? 0,
                        MarginTop = d.MarginTop ?? Block.AutoTopMargin, // absent = the editor's own gap
                        MarginBottom = d.MarginBottom ?? 10
                    };
                    if (ResolveImage(d.ImageRef, d.ImageBase64, d.MimeType, pool) is { } img)
                        ib.SetImageData(img.Bytes, img.Mime);
                    return ib;
                }
            case "Table":
                // 1x1, NOT the declared size: everything the constructor builds is cleared on the next
                // three lines and rebuilt from the cells that actually exist, so sizing it from
                // attacker-controlled numbers only ever wasted memory — a file claiming 100000000 rows
                // exhausted it before a single cell was read.
                var tb = new TableBlock(1, 1);
                tb.Indent = d.Indent ?? 0;
                tb.MarginTop = d.MarginTop ?? Block.AutoTopMargin; // absent = the editor's own gap
                tb.MarginBottom = d.MarginBottom ?? 10;
                tb.Cells.Clear();
                tb.ColumnWidths.Clear();
                tb.RowHeights.Clear();
                if (d.ColumnWidths != null) tb.ColumnWidths.AddRange(d.ColumnWidths);
                if (d.RowHeights != null) tb.RowHeights.AddRange(d.RowHeights);
                if (d.Cells != null)
                    foreach (var rowD in d.Cells)
                    {
                        var row = new List<TableCell>();
                        if (rowD == null) { tb.Cells.Add(row); continue; } // JSON null row
                        foreach (var cd in rowD)
                        {
                            TableCell cell;
                            if (cd == null) { row.Add(new TableCell()); continue; } // JSON null cell
                            if (cd.Type == "Cell")
                            {
                                cell = new TableCell { Background = ColorUtil.Parse(cd.Background) };
                                cell.Blocks.Clear();
                                if (cd.Blocks != null)
                                    foreach (var bd in cd.Blocks)
                                        if (DtoToBlock(bd, pool) is { } bb) { bb.Parent = cell; cell.Blocks.Add(bb); }
                                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
                            }
                            else
                            {
                                var para = DtoToParagraph(cd, pool);
                                cell = new TableCell(para) { Background = para.Background };
                                para.Background = null;
                            }
                            if (Enum.TryParse<CellVerticalAlignment>(cd.VAlign, out var va))
                                cell.VerticalAlignment = va;
                            row.Add(cell);
                        }
                        tb.Cells.Add(row);
                    }
                tb.Rows = tb.Cells.Count;
                // No rows is no table, as upstream reads it (round 35) and as the HTML and RTF readers make none —
                // so one file loads as the same document in both editors.
                if (tb.Rows == 0) return null;
                // The declared width pads short rows, so it is allocated too — cap it. The widest row
                // that really exists always wins below, so a legitimate document is unaffected.
                int maxCols = Math.Clamp(d.Columns ?? 0, 1, MaxTableDimension);
                foreach (var row in tb.Cells) if (row.Count > maxCols) maxCols = row.Count;
                // The widest row wins within the import bounds: one wide row padded every other row out to it
                // (upstream round 35; measured here too).
                tb.Columns = TableBlock.ImportColumns(tb.Rows, maxCols);
                foreach (var row in tb.Cells)
                    if (row.Count > tb.Columns) row.RemoveRange(tb.Columns, row.Count - tb.Columns);
                foreach (var row in tb.Cells)
                    while (row.Count < tb.Columns) row.Add(new TableCell());
                tb.ColSpans.Clear();
                tb.RowSpans.Clear();
                for (int r = 0; r < tb.Rows; r++)
                {
                    var cs = new List<int>();
                    var rs = new List<int>();
                    for (int c = 0; c < tb.Columns; c++)
                    {
                        // A JSON null row (`"ColSpans":[null]`) threw NullReferenceException out of the load — not
                        // the JsonException the load paths promise — so it reads as "no spans" like a missing row.
                        cs.Add(d.ColSpans is { } dcs && r < dcs.Count && dcs[r] is { } csr && c < csr.Count ? csr[c] : 1);
                        rs.Add(d.RowSpans is { } drs && r < drs.Count && drs[r] is { } rsr && c < rsr.Count ? rsr[c] : 1);
                    }
                    tb.ColSpans.Add(cs);
                    tb.RowSpans.Add(rs);
                }
                tb.EnsureSpanConsistency(); // the file's span VALUES are data too — see there
                return tb;
            default:
                // A block type this reader does not know — a newer format's — is read as a paragraph (its text, if
                // it has any), and said so: saving the document afterwards writes it back without that block.
                if (d.Type is not null and not "Paragraph") ReportUnknownType("block", d.Type);
                return DtoToParagraph(d, pool);
        }
    }

    private static void ReportUnknownType(string what, string type)
        => RichEditorDiagnostics.Report(new NotSupportedException(
            $"Unknown {what} type \"{type}\" in the document: read as plain text; saving drops what it was."));

    private static Paragraph DtoToParagraph(BlockDto d, Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        var p = new Paragraph
        {
            LineHeight = d.LineHeight ?? double.NaN,
            LineSpacing = d.LineSpacing ?? double.NaN,
            MarginTop = d.MarginTop ?? 0,
            MarginBottom = d.MarginBottom ?? 0,
            MarginRight = d.MarginRight ?? 0,
            HeadingLevel = d.HeadingLevel ?? 0,
            Background = ColorUtil.Parse(d.Background),
            Indent = d.Indent ?? 0,
            IsQuote = d.IsQuote == true,
            // The levels RTF reads too. -1 threw out of the HTML writer — so out of every copy — and a million wrote
            // a million <ul> tags (upstream round 35; measured here too).
            ListLevel = Math.Clamp(d.ListLevel ?? 0, 0, 8),
            ListType = Enum.TryParse<ListKind>(d.ListType, out var lk) ? lk : (d.IsListItem == true ? ListKind.Bullet : ListKind.None),
            ListMarker = Enum.TryParse<ListMarkerStyle>(d.ListMarker, out var lm) ? lm : ListMarkerStyle.Default
        };
        if (Enum.TryParse<TextAlignment>(d.TextAlignment, out var ta)) p.TextAlignment = ta;
        if (d.Inlines != null)
            foreach (var id in d.Inlines)
            {
                if (id == null) continue; // JSON null inside "Inlines"
                if (id.Type == "Image")
                {
                    var im = new InlineImage
                    {
                        Width = id.Width ?? 16,
                        Height = id.Height ?? 16,
                        AltText = id.Alt
                    };
                    if (ResolveImage(id.ImageRef, id.ImageBase64, id.MimeType, pool) is { } img)
                        im.SetImageData(img.Bytes, img.Mime);
                    p.Inlines.Add(im);
                }
                else if (id.Type == "Table" && id.Table != null)
                {
                    if (DtoToBlock(id.Table, pool) is TableBlock itb)
                        p.Inlines.Add(new InlineTable { Table = itb });
                }
                else
                {
                    if (id.Type is not null and not "Run" and not "Table") ReportUnknownType("inline", id.Type);
                    p.Inlines.Add(new Run
                    {
                        Text = id.Text,
                        FontWeight = id.Bold == true ? FontWeightValues.Bold : FontWeightValues.Normal,
                        FontStyle = id.Italic == true ? FontStyle.Italic : FontStyle.Normal,
                        FontSize = id.FontSize is { } fs && fs > 0 ? fs : 10, // pt; body default (absent, or a damaged <= 0)
                        Foreground = ColorUtil.Parse(id.Foreground),
                        Background = ColorUtil.Parse(id.Background),
                        FontFamily = id.FontFamily,
                        // A file is untrusted input: a script link is dropped as the HTML reader drops it (2026-09-24).
                        NavigateUri = id.NavigateUri is { } uri ? HtmlDocumentFormatter.SafeHref(uri) : null,
                        TextDecorations = BuildDecorations(id.Underline == true, id.Strikethrough == true)
                    });
                }
            }
        return p;
    }

    // Every pool key the document's blocks refer to, at any depth (cells, inline tables). Thread-free: the package
    // reader runs it in the background, to read only the picture entries that are used.
    internal static HashSet<string> ImageRefs(FlowDocumentDto? dto)
    {
        var refs = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<BlockDto?>? blocks)
        {
            if (blocks == null) return;
            foreach (var b in blocks)
            {
                if (b == null) continue;
                if (b.ImageRef != null) refs.Add(b.ImageRef);
                Walk(b.Blocks);
                if (b.Cells != null) foreach (var row in b.Cells) Walk(row);
                if (b.Inlines != null)
                    foreach (var i in b.Inlines)
                    {
                        if (i?.ImageRef != null) refs.Add(i.ImageRef);
                        if (i?.Table != null) Walk([i.Table]);
                    }
            }
        }
        Walk(dto?.Blocks);
        return refs;
    }

    // ---- helpers ----

    private static double? NanToNull(double v) => double.IsNaN(v) ? (double?)null : v;

    // `value`, or null (not written) when it is what every reader assumes for an absent field.
    private static T? Unless<T>(T value, T absent) where T : struct
        => EqualityComparer<T>.Default.Equals(value, absent) ? null : value;

    private static bool HasMerge(TableBlock tb)
    {
        foreach (var row in tb.ColSpans) foreach (int v in row) if (v != 1) return true;
        foreach (var row in tb.RowSpans) foreach (int v in row) if (v != 1) return true;
        return false;
    }

    private static TextDecorationFlags BuildDecorations(bool underline, bool strikethrough)
    {
        var d = TextDecorationFlags.None;
        if (underline) d |= TextDecorationFlags.Underline;
        if (strikethrough) d |= TextDecorationFlags.Strikethrough;
        return d;
    }

    // The JSON storage form for a color is "#AARRGGBB" (matches Color.ToString()); ColorUtil.Parse reads it back.
    private static string? ColorToString(Windows.UI.Color? c) => c.HasValue ? ColorUtil.ToHexArgb(c.Value) : null;

    // Adds image bytes to the document pool (deduplicated by content hash) and returns the pool key,
    // or null when there is no image data. RawBytes are stored as-is; a bitmap without bytes is PNG-encoded.
    private static string? PoolImage(Dictionary<string, (byte[] Bytes, string Mime)> pool, byte[]? rawBytes, string? mimeType, CanvasBitmap? bmp)
    {
        byte[]? bytes = rawBytes ?? ImageEncoder.ToPngBytes(bmp);
        if (bytes == null) return null;
        string mime = rawBytes != null ? (mimeType ?? "image/png") : "image/png";
        string key = Convert.ToHexString(SHA256.HashData(bytes));
        if (!pool.ContainsKey(key)) pool[key] = (bytes, mime);
        return key;
    }

    private static (byte[] Bytes, string Mime)? ResolveImage(string? imageRef, string? inlineBase64, string? mimeType,
        Dictionary<string, (byte[] Bytes, string Mime)> pool)
    {
        if (imageRef != null && pool.TryGetValue(imageRef, out var pooled)) return pooled;
        var bytes = TryFromBase64(inlineBase64);
        return bytes != null ? (bytes, ImageMime.Safe(mimeType ?? "image/png", bytes)) : null;
    }

    private static byte[]? TryFromBase64(string? value)
    {
        if (string.IsNullOrEmpty(value)) return null;
        try { return Convert.FromBase64String(value); }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return null; }
    }
}

internal class FlowDocumentDto
{
    [JsonConverter(typeof(SchemaVersionConverter))]
    public string Version { get; set; } = "1";
    // Null by default so a READ can tell "no Blocks property" (not a document of this library) from "no
    // blocks" — ToDto always sets it, so every document this library writes has it (see EnsureDocumentShape).
    public List<BlockDto>? Blocks { get; set; }
    public Dictionary<string, ImagePoolDto>? Images { get; set; }
    // Optional page setup; absent for plain (Continuous) documents, so the format is unchanged for them.
    public PageSetupDto? PageSetup { get; set; }
    // 1 = heading formatting is on the runs (bold and the heading size, written when the heading was set).
    // Absent in files written before 2026-09-12 and by upstream, whose renderers forced both; the EDITOR then
    // writes them onto the runs once when it receives the document (HeadingStyle.Materialize) — the reader
    // leaves the model as it was saved. Upstream ignores unknown properties.
    public int? HeadingFormat { get; set; }
}

// Enums are serialized as their names (matching the rest of the format), so unknown future values degrade
// gracefully to defaults on read. The original AvaloniaRichEditor ignores this object (content-only format).
internal class PageSetupDto
{
    public string? PageSize { get; set; }
    public string? Orientation { get; set; }
    public bool ShowPageBoundaries { get; set; } = true;
    public string? Header { get; set; }
    public string? Footer { get; set; }
    public bool ShowPageNumbers { get; set; }
    // Page margins in MILLIMETRES, one per side. Omitted when they are the default, so a document that never
    // touched them keeps its bytes; a reader that predates them (or a file that omits one side) falls back to
    // the default for that side. Same fields and unit as upstream.
    public double? MarginLeft { get; set; }
    public double? MarginTop { get; set; }
    public double? MarginRight { get; set; }
    public double? MarginBottom { get; set; }
}

internal class ImagePoolDto
{
    public string? Data { get; set; } // base64 of the original encoded bytes
    public string? MimeType { get; set; }
}

internal class BlockDto
{
    public string? Type { get; set; } = "Paragraph"; // written as null (absent) for a paragraph

    // Paragraph
    public List<InlineDto>? Inlines { get; set; }
    public string? TextAlignment { get; set; }
    public double? LineHeight { get; set; }
    public double? LineSpacing { get; set; }
    public double? MarginTop { get; set; }
    public double? MarginBottom { get; set; }
    public double? MarginRight { get; set; }
    public bool? IsListItem { get; set; } // legacy (read fallback); replaced by ListType
    public string? ListType { get; set; }
    public string? ListMarker { get; set; }
    public int? HeadingLevel { get; set; }
    public string? Background { get; set; }
    public double? Indent { get; set; }
    public bool? IsQuote { get; set; }
    public int? ListLevel { get; set; }

    // Image block
    public string? ImageRef { get; set; }
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? Alt { get; set; } // accessibility description; omitted when null (format unchanged)

    // Table block
    public int? Rows { get; set; } // not written: readers rebuild it from Cells
    public int? Columns { get; set; } // not written: readers rebuild it from Cells (old files: a floor on the width)
    public List<double>? ColumnWidths { get; set; }
    public List<double>? RowHeights { get; set; }
    public List<List<BlockDto>>? Cells { get; set; }
    public List<List<int>>? ColSpans { get; set; }
    public List<List<int>>? RowSpans { get; set; }

    // Table cell (Type == "Cell"): the cell's block list.
    public List<BlockDto>? Blocks { get; set; }
    // Table cell vertical alignment ("Center"/"Bottom"; null = Top). On both cell encodings.
    public string? VAlign { get; set; }
}

internal class InlineDto
{
    public string? Type { get; set; } = "Run"; // written as null (absent) for a run

    // Run
    public string? Text { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public double? FontSize { get; set; } // pt; absent = the 10 pt body default
    public string? Foreground { get; set; }
    public string? Background { get; set; }
    public string? FontFamily { get; set; }
    public string? NavigateUri { get; set; }
    public bool? Underline { get; set; }
    public bool? Strikethrough { get; set; }

    // Inline image
    public string? ImageRef { get; set; }
    public string? ImageBase64 { get; set; }
    public string? MimeType { get; set; }
    public double? Width { get; set; }
    public double? Height { get; set; }
    public string? Alt { get; set; } // accessibility description; omitted when null

    // Inline table (Type == "Table"): the wrapped grid serialized as a block table DTO.
    public BlockDto? Table { get; set; }
}

// Reads the document-format version as the legacy integer form (1, 2) or the current SemVer string ("1.0").
// Always writes the string form.
internal sealed class SchemaVersionConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString() ?? "1",
            JsonTokenType.Number => reader.TryGetInt64(out var n)
                ? n.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "1"
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value);
}
