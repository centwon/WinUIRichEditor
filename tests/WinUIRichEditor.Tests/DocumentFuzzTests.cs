using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Windows.UI;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

// Randomized edit-sequence fuzz over the document model.
//
// Why this exists: every other detection method in this codebase has been exhausted, and the defects
// that kept surviving them were not "unread code" but UNTRIED COMBINATIONS — a table between two texts
// passed while a table alone in its paragraph failed; one round trip passed while two grew a separator;
// 100% line spacing passed while 300% broke caret movement. Reading finds none of those, because each
// piece is individually correct. A fuzz walks combinations no hand-written case enumerates, and checks
// invariants after EVERY step so a failure points at the operation that broke them rather than at the
// end state. (The Avalonia original found a real defect this way — a list toggle that cloned inline
// objects and detached them — that nothing else had caught.)
//
// Scope: the model layer, which is headless. The control (RichEditor) needs the WinUI runtime, so the
// editing commands are out of reach; what IS reachable is the document model, TextRange/TextPointer,
// RunNormalizer, and all four formatters — which is where structural corruption would land anyway.
public class DocumentFuzzTests
{
    // 400 seeds is the CI budget — about three seconds, and the reason it is not 24 any more: EVERY
    // defect the 2026-08-06 audit found lived past seed 24 (nested list items came back reordered at
    // 53, a table could be left with no reachable cells at 1559). Seeds are cheap; the old budget was
    // the binding constraint on what this fuzz could see.
    //
    // Widen further with RICHEDITOR_FUZZ_SEEDS, so an audit run needs no source edit (an
    // edit-and-revert dance is how a widened run silently becomes the committed default).
    //
    // Clean to 20000 seeds as of 2026-08-06. Nothing is known-failing; a failure here is a new defect.
    private static readonly int Seeds =
        int.TryParse(Environment.GetEnvironmentVariable("RICHEDITOR_FUZZ_SEEDS"), out int s) && s > 0 ? s : 400;
    private const int StepsPerSeed = 300;

    [Fact]
    public void RandomEditSequences_KeepTheDocumentStructurallyValid()
    {
        var failures = new List<string>();
        for (int seed = 0; seed < Seeds; seed++)
        {
            var rng = new Random(seed);
            var doc = SeedDocument(rng);
            string lastOp = "(seed)";
            try
            {
                CheckInvariants(doc);
                for (int step = 0; step < StepsPerSeed; step++)
                {
                    lastOp = ApplyRandomOp(doc, rng);
                    CheckInvariants(doc);
                }
            }
            catch (Exception ex)
            {
                failures.Add(
                    $"seed {seed} broke after '{lastOp}': {ex.GetType().Name}: {ex.Message}\n{Dump(doc)}\n--- origin ---\n{ex.StackTrace}");
            }
        }
        if (failures.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"{failures.Count} failure(s) over {Seeds} seeds:\n\n" + string.Join("\n\n", failures.Take(6)) +
                (failures.Count > 6 ? $"\n\n… and {failures.Count - 6} more" : ""));
    }

    // The same sequences, then through every export format twice. A single round trip hides anything
    // that ACCUMULATES; comparing cycle 1 against cycle 2 is what catches a separator turning into
    // content (RTF gained a newline before a nested table, HTML a space after an inline table).
    [Fact]
    public void RandomDocuments_RoundTripIdempotentlyThroughEveryFormat()
    {
        // Collect instead of throwing on the first seed. A widened audit run exists to survey the space,
        // and stopping at seed 26 hid whatever seeds 27..399 had to say — the failure detail below is
        // still the first one's, but the tally tells you whether you are looking at one defect or five.
        var failures = new List<string>();
        for (int seed = 0; seed < Seeds; seed++)
        {
            var rng = new Random(seed);
            var doc = SeedDocument(rng);
            for (int step = 0; step < 40; step++) ApplyRandomOp(doc, rng);

            foreach (var (name, round) in RoundTrips())
            {
                FlowDocument once, twice;
                try
                {
                    once = round(doc);
                    twice = round(once);
                }
                catch (Exception ex)
                {
                    failures.Add($"seed {seed} / {name} threw: {ex}\n{Dump(doc)}");
                    continue;
                }
                try
                {
                    CheckInvariants(once);
                    CheckInvariants(twice);
                }
                catch (Exception ex)
                {
                    failures.Add($"seed {seed} / {name} broke an invariant: {ex.Message}");
                    continue;
                }
                string a = Shape(once), b = Shape(twice);
                if (a != b)
                    failures.Add($"seed {seed} / {name} is not idempotent — cycle 2 differs from cycle 1.\n" + Diff(a, b));
            }
        }
        if (failures.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"{failures.Count} failure(s) over {Seeds} seeds:\n\n" + string.Join("\n\n", failures.Take(12)) +
                (failures.Count > 12 ? $"\n\n… and {failures.Count - 12} more" : ""));
    }

    // The two NATIVE formats must be lossless, and idempotency cannot tell you whether they are: a
    // format that every cycle drops equally is idempotent and gone. JSON and .flow are this editor's own
    // save formats — a document saved and reopened has to come back identical, formatting included — so
    // here the comparison is against the ORIGINAL rather than against the previous cycle. (HTML and RTF
    // are excluded on purpose: they are interchange formats with documented losses, so only the
    // idempotency test above applies to them.)
    [Fact]
    public void RandomDocuments_SurviveTheNativeFormatsWithoutLoss()
    {
        var failures = new List<string>();
        for (int seed = 0; seed < Seeds; seed++)
        {
            var rng = new Random(seed);
            var doc = SeedDocument(rng);
            for (int step = 0; step < 40; step++) ApplyRandomOp(doc, rng);
            string before = Shape(doc);

            foreach (var (name, round) in RoundTrips())
            {
                if (name is not ("json" or "flow")) continue;
                string after;
                try { after = Shape(round(doc)); }
                catch (Exception ex) { failures.Add($"seed {seed} / {name} threw: {ex}"); continue; }
                if (before != after)
                    failures.Add($"seed {seed} / {name} lost or changed something.\n" + Diff(before, after));
            }
        }
        if (failures.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"{failures.Count} failure(s) over {Seeds} seeds:\n\n" + string.Join("\n\n", failures.Take(12)) +
                (failures.Count > 12 ? $"\n\n… and {failures.Count - 12} more" : ""));
    }

    private static (string, Func<FlowDocument, FlowDocument>)[] RoundTrips() => new (string, Func<FlowDocument, FlowDocument>)[]
    {
        ("json", d => DocumentSerializer.Deserialize(DocumentSerializer.Serialize(d))),
        ("flow", d =>
        {
            using var ms = new System.IO.MemoryStream();
            DocumentPackage.Save(d, ms);
            ms.Position = 0;
            return DocumentPackage.Load(ms);
        }),
        ("html", d => HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(d))),
        ("rtf",  d => RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(d))),
    };

    // ---- document generation -------------------------------------------------------------------

    private static FlowDocument SeedDocument(Random rng)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("시작"));
        int n = rng.Next(1, 4);
        for (int i = 0; i < n; i++) doc.Blocks.Add(rng.Next(3) == 0 ? NewTable(rng) : Para("문단 " + i));
        doc.Blocks.Add(Para("끝"));
        return doc;
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    // A 2×2 PNG. Real bytes, because every format carries images differently (base64 in JSON/.flow, a
    // data: URI in HTML, hex \pict in RTF) and a placeholder would exercise none of it. Kept tiny so
    // widening the seed count stays affordable.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static ImageBlock CellImage(Random rng)
    {
        var ib = new ImageBlock { Width = 20 + rng.Next(40), Height = 20 + rng.Next(40) };
        ib.SetImageData(TinyPng, "image/png");
        return ib;
    }

    private static TableBlock NewTable(Random rng)
    {
        var tb = new TableBlock(rng.Next(1, 4), rng.Next(1, 4));
        foreach (var (r, c, cell) in tb.LogicalCells())
            ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c}";
        return tb;
    }

    // ---- operations ----------------------------------------------------------------------------

    private static string ApplyRandomOp(FlowDocument doc, Random rng)
    {
        var paras = AllParagraphs(doc).ToList();
        var tables = AllTables(doc).ToList();

        // 23 ops, and `default` is still one of them (RunNormalizer.Compact) — a new case must WIDEN this
        // bound, not take the last number, or the default branch silently stops running.
        switch (rng.Next(23))
        {
            case 18:
                doc.Blocks.Insert(rng.Next(doc.Blocks.Count + 1), new DividerBlock());
                return "insert divider";
            case 19:
                // An EMPTY paragraph is a blank line the author typed, and it is a different shape from
                // a paragraph with text: HTML drops elements that produce no inline, so it needed a
                // marker to survive at all.
                doc.Blocks.Insert(rng.Next(doc.Blocks.Count + 1), new Paragraph());
                return "insert blank paragraph";
            case 20:
            {
                // A block image. RTF spells one as `\pard <pict>\par`, and reading that \par as content
                // grew a blank paragraph under every picture on every cycle.
                var ib = new ImageBlock { Width = 80 + rng.Next(60), Height = 40 + rng.Next(60) };
                ib.SetImageData(TinyPng, "image/png");
                doc.Blocks.Insert(rng.Next(doc.Blocks.Count + 1), ib);
                return "insert block image";
            }
            case 21:
            {
                var p = Pick(paras, rng); if (p == null) return "inline image (skipped)";
                var im = new InlineImage { Width = 12 + rng.Next(20), Height = 12 + rng.Next(20) };
                im.SetImageData(TinyPng, "image/png");
                p.Inlines.Insert(rng.Next(p.Inlines.Count + 1), im);
                return "insert inline image";
            }
            case 0:
                doc.Blocks.Insert(rng.Next(doc.Blocks.Count + 1), Para("새 문단"));
                return "insert paragraph";
            case 1:
                if (doc.Blocks.Count > 1) { doc.Blocks.RemoveAt(rng.Next(doc.Blocks.Count)); return "remove top block"; }
                return "remove top block (skipped)";
            case 2:
            {
                var p = Pick(paras, rng); if (p == null) return "add run (skipped)";
                p.Inlines.Insert(rng.Next(p.Inlines.Count + 1), new Run { Text = RandomText(rng) });
                return "add run";
            }
            case 3:
            {
                var p = Pick(paras, rng); if (p == null) return "style run (skipped)";
                foreach (var r in p.Inlines.OfType<Run>())
                {
                    if (rng.Next(2) == 0) r.FontWeight = FontWeightValues.Bold;
                    if (rng.Next(3) == 0) r.Foreground = Color.FromArgb(255, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                    if (rng.Next(4) == 0) r.Background = Color.FromArgb(255, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                    if (rng.Next(4) == 0) r.NavigateUri = "https://example.com/" + rng.Next(100);
                    // The rest of the character format. Italic, the decorations and an explicit family/size
                    // were never generated, so nothing downstream of them was fuzzed at all — and a
                    // fractional size is the one that has bitten this codebase before (10.5pt never
                    // matched the toolbar's combo because an int round-tripped where a double was meant).
                    if (rng.Next(3) == 0) r.FontStyle = Windows.UI.Text.FontStyle.Italic;
                    if (rng.Next(3) == 0) r.TextDecorations |= (TextDecorationFlags)(1 + rng.Next(3));
                    if (rng.Next(4) == 0) r.FontFamily = rng.Next(2) == 0 ? "Segoe UI" : "맑은 고딕";
                    if (rng.Next(4) == 0) r.FontSize = 8 + rng.Next(20) + (rng.Next(2) == 0 ? 0.5 : 0);
                }
                return "style runs";
            }
            case 4:
            {
                var p = Pick(paras, rng); if (p == null) return "paragraph format (skipped)";
                p.HeadingLevel = rng.Next(7);
                p.IsQuote = rng.Next(3) == 0;
                p.Indent = rng.Next(3) * 20;
                p.ListType = (ListKind)rng.Next(3);
                p.ListLevel = rng.Next(3);
                if (rng.Next(3) == 0) p.LineSpacing = 1.0 + rng.Next(4) * 0.5; // 1.0 … 2.5
                // Also never generated: alignment, the marker style that refines ListType, the paragraph
                // fill, the right margin and an absolute line height. Each has its own writer/reader pair
                // in all four formats.
                if (rng.Next(3) == 0) p.TextAlignment = (Microsoft.UI.Xaml.TextAlignment)rng.Next(4);
                if (rng.Next(3) == 0) p.ListMarker = (ListMarkerStyle)rng.Next(10);
                if (rng.Next(4) == 0) p.Background = Color.FromArgb(255, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                if (rng.Next(5) == 0) p.MarginRight = rng.Next(3) * 15;
                if (rng.Next(6) == 0) p.LineHeight = 12 + rng.Next(20);
                return "paragraph format";
            }
            case 22:
            {
                // Alt text — the accessibility description, which round-trips through JSON/.flow and HTML
                // and has no RTF spelling at all. Never generated before this audit.
                var imgs = AllImages(doc).ToList();
                if (imgs.Count == 0) return "alt text (skipped)";
                var target = imgs[rng.Next(imgs.Count)];
                string alt = "설명 " + rng.Next(50);
                if (target is ImageBlock ib2) ib2.AltText = alt; else ((InlineImage)target).AltText = alt;
                return "alt text";
            }
            case 5:
            {
                var tb = Pick(tables, rng); if (tb == null) return "insert row (skipped)";
                tb.InsertRow(rng.Next(tb.Rows + 1));
                return "InsertRow";
            }
            case 6:
            {
                var tb = Pick(tables, rng); if (tb == null || tb.Rows <= 1) return "delete row (skipped)";
                tb.DeleteRow(rng.Next(tb.Rows));
                return "DeleteRow";
            }
            case 7:
            {
                var tb = Pick(tables, rng); if (tb == null) return "insert column (skipped)";
                tb.InsertColumn(rng.Next(tb.Columns + 1));
                return "InsertColumn";
            }
            case 8:
            {
                var tb = Pick(tables, rng); if (tb == null || tb.Columns <= 1) return "delete column (skipped)";
                tb.DeleteColumn(rng.Next(tb.Columns));
                return "DeleteColumn";
            }
            case 9:
            {
                var tb = Pick(tables, rng); if (tb == null) return "merge (skipped)";
                int r0 = rng.Next(tb.Rows), c0 = rng.Next(tb.Columns);
                int r1 = Math.Min(tb.Rows - 1, r0 + rng.Next(3)), c1 = Math.Min(tb.Columns - 1, c0 + rng.Next(3));
                tb.MergeCells(r0, c0, r1, c1);
                return $"MergeCells({r0},{c0},{r1},{c1})";
            }
            case 10:
            {
                var tb = Pick(tables, rng); if (tb == null) return "unmerge (skipped)";
                tb.UnmergeCell(rng.Next(tb.Rows), rng.Next(tb.Columns));
                return "UnmergeCell";
            }
            case 11:
            {
                var tb = Pick(tables, rng); if (tb == null) return "cell block (skipped)";
                // Materialize once. Counting one enumeration and indexing another turned "this table has
                // no reachable cells" into an ArgumentOutOfRangeException from inside LINQ, which reads
                // like a harness bug and buries the real finding; CheckTable now asserts that condition
                // directly, and this just stays out of its way.
                var cells = tb.LogicalCells().Select(x => x.cell).ToList();
                if (cells.Count == 0) return "add block to cell (skipped)";
                var cell = cells[rng.Next(cells.Count)];
                cell.Blocks.Add(rng.Next(4) switch
                {
                    0 => Para("셀 문단"),
                    1 => NewTable(rng),          // a nested table
                    2 => new DividerBlock(),
                    _ => CellImage(rng),
                });
                return "add block to cell";
            }
            case 12:
            {
                var tb = Pick(tables, rng); if (tb == null) return "cell style (skipped)";
                foreach (var (_, _, cell) in tb.LogicalCells())
                {
                    if (rng.Next(3) == 0) cell.Background = Color.FromArgb(255, (byte)rng.Next(256), (byte)rng.Next(256), (byte)rng.Next(256));
                    if (rng.Next(4) == 0) cell.VerticalAlignment = (CellVerticalAlignment)rng.Next(3);
                }
                return "cell style";
            }
            case 13:
            {
                // An inline table — the shape that has produced three separate defects this session.
                var p = Pick(paras, rng); if (p == null) return "inline table (skipped)";
                var it = new InlineTable { Table = NewTable(rng) };
                // Deliberately sometimes at index 0 (the table ALONE opens its paragraph) and sometimes
                // between runs: those two shapes take different writer/reader paths.
                p.Inlines.Insert(rng.Next(2) == 0 ? 0 : rng.Next(p.Inlines.Count + 1), it);
                return "insert inline table";
            }
            case 14:
            {
                var p = Pick(paras, rng); if (p == null) return "soft break (skipped)";
                p.Inlines.Add(new Run { Text = "a\nb" }); // hard line inside a paragraph
                return "soft break run";
            }
            case 15:
            {
                if (paras.Count < 2) return "range delete (skipped)";
                var a = paras[rng.Next(paras.Count)];
                var b = paras[rng.Next(paras.Count)];
                var range = new TextRange(new TextPointer(a, 0), new TextPointer(b, TextLen(b)));
                try { range.Delete(); } catch (Exception e) { throw new InvalidOperationException("TextRange.Delete threw", e); }
                return "TextRange.Delete";
            }
            case 16:
            {
                var p = Pick(paras, rng); if (p == null) return "coalesce (skipped)";
                TextRange.CoalesceRuns(p);
                return "CoalesceRuns";
            }
            default:
                RunNormalizer.Compact(doc);
                return "RunNormalizer.Compact";
        }
    }

    private static string RandomText(Random rng)
    {
        const string alphabet = "가나다abc 123";
        var sb = new StringBuilder();
        int n = rng.Next(1, 8);
        for (int i = 0; i < n; i++) sb.Append(alphabet[rng.Next(alphabet.Length)]);
        return sb.ToString();
    }

    private static T? Pick<T>(List<T> xs, Random rng) where T : class => xs.Count == 0 ? null : xs[rng.Next(xs.Count)];

    // ---- invariants ----------------------------------------------------------------------------

    private static void CheckInvariants(FlowDocument doc)
    {
        foreach (var b in doc.Blocks)
        {
            if (b == null) throw new InvalidOperationException("null block at the document's top level");
            CheckBlock(b);
        }
    }

    private static void CheckBlock(Block b)
    {
        switch (b)
        {
            case Paragraph p:
                foreach (var inl in p.Inlines)
                {
                    if (inl == null) throw new InvalidOperationException("null inline");
                    if (inl is InlineTable it)
                    {
                        if (it.Table == null) throw new InvalidOperationException("InlineTable with a null Table");
                        CheckTable(it.Table);
                    }
                }
                break;
            case TableBlock tb:
                CheckTable(tb);
                break;
        }
    }

    private static void CheckTable(TableBlock tb)
    {
        if (tb.Rows != tb.Cells.Count)
            throw new InvalidOperationException($"Rows={tb.Rows} but Cells.Count={tb.Cells.Count}");
        if (tb.ColSpans.Count != tb.Rows || tb.RowSpans.Count != tb.Rows)
            throw new InvalidOperationException("span grids do not have one row each");
        for (int r = 0; r < tb.Rows; r++)
        {
            if (tb.Cells[r].Count != tb.Columns)
                throw new InvalidOperationException($"row {r} has {tb.Cells[r].Count} cells, expected {tb.Columns}");
            if (tb.ColSpans[r].Count != tb.Columns || tb.RowSpans[r].Count != tb.Columns)
                throw new InvalidOperationException($"row {r} span widths disagree with Columns");
            for (int c = 0; c < tb.Columns; c++)
            {
                if (tb.Cells[r][c] == null) throw new InvalidOperationException($"null cell at {r},{c}");
                // The model's documented convention: an anchor reports (>=1, >=1) and a covered slot
                // reports exactly (0,0). Pin both directions — a covered cell with a stale non-zero span
                // is exactly how a merge grid silently goes wrong.
                var (cs, rs) = tb.SpanOf(r, c);
                if (tb.IsCovered(r, c))
                {
                    if (cs != 0 || rs != 0)
                        throw new InvalidOperationException($"covered slot {r},{c} reports span ({cs},{rs}), expected (0,0)");
                    // A covered slot must be covered BY SOMETHING. Skipping this (the earlier version
                    // just continued here) let an ORPHANED cell pass every check: still flagged covered
                    // after its anchor was deleted or shrunk away, so LogicalCells never yields it, its
                    // content is unreachable from every command, and a whole table could end up with no
                    // logical cells at all. The other direction — an anchor's reach — is checked below.
                    var (car, cac) = tb.AnchorOf(r, c);
                    if (car < 0 || cac < 0 || car >= tb.Rows || cac >= tb.Columns)
                        throw new InvalidOperationException($"covered slot {r},{c} has off-grid anchor ({car},{cac})");
                    if (car == r && cac == c)
                        throw new InvalidOperationException($"covered slot {r},{c} is its own anchor");
                    if (tb.IsCovered(car, cac))
                        throw new InvalidOperationException($"covered slot {r},{c} resolves to ({car},{cac}), which is itself covered");
                    var (acs, ars) = tb.SpanOf(car, cac);
                    if (r < car || r >= car + ars || c < cac || c >= cac + acs)
                        throw new InvalidOperationException(
                            $"covered slot {r},{c} claims anchor ({car},{cac}), whose span ({acs},{ars}) does not reach it");
                    continue;
                }
                if (cs < 1 || rs < 1) throw new InvalidOperationException($"anchor {r},{c} reports span ({cs},{rs})");
                if (c + cs > tb.Columns || r + rs > tb.Rows)
                    throw new InvalidOperationException($"span at {r},{c} = ({cs},{rs}) runs past the grid");
                // An anchor must not be covered by anyone else, and a covered slot must resolve to a
                // real anchor — the two directions of the same relation.
                var (ar, ac) = tb.AnchorOf(r, c);
                if (ar < 0 || ac < 0 || ar >= tb.Rows || ac >= tb.Columns)
                    throw new InvalidOperationException($"AnchorOf({r},{c}) = ({ar},{ac}) is off-grid");
                if (tb.IsCovered(ar, ac))
                    throw new InvalidOperationException($"anchor ({ar},{ac}) of ({r},{c}) is itself covered");
            }
            foreach (var cell in tb.Cells[r])
                foreach (var cb in cell.Blocks)
                {
                    if (cb == null) throw new InvalidOperationException("null block inside a cell");
                    CheckBlock(cb);
                }
        }
        // LogicalCells must visit each anchor exactly once and nothing else.
        var seen = new HashSet<(int, int)>();
        foreach (var (r, c, _) in tb.LogicalCells())
        {
            if (tb.IsCovered(r, c)) throw new InvalidOperationException($"LogicalCells yielded covered slot {r},{c}");
            if (!seen.Add((r, c))) throw new InvalidOperationException($"LogicalCells yielded {r},{c} twice");
        }
    }

    // ---- shape (format-independent structural signature) ---------------------------------------

    private static string Shape(FlowDocument doc)
    {
        var sb = new StringBuilder();
        foreach (var b in doc.Blocks) ShapeBlock(sb, b);
        return sb.ToString();
    }

    private static void ShapeBlock(StringBuilder sb, Block b)
    {
        switch (b)
        {
            case Paragraph p:
                sb.Append("P").Append(ParaFmt(p)).Append('[');
                ShapeInlines(sb, p);
                sb.Append(']');
                break;
            case TableBlock tb: sb.Append("T"); ShapeTable(sb, tb); break;
            case ImageBlock ib: sb.Append("IMGBLK").Append(ImgFmt(ib.Width, ib.Height, ib.AltText)); break;
            case DividerBlock: sb.Append("HR"); break;
        }
    }

    // A paragraph's inlines, canonical with respect to RUN BOUNDARIES. Where one run ends and the next
    // begins is not part of the document's content when both carry the same formatting, and the loaders
    // coalesce such neighbours on purpose (RunNormalizer exists to do it). A signature that recorded the
    // boundaries reported every one of those as a difference — 12 of them on the first wide run — which
    // is exactly how a real finding gets buried. Empty runs are skipped for the same reason: they carry
    // no content, and a loader is free to drop them.
    private static void ShapeInlines(StringBuilder sb, Paragraph p)
    {
        string text = "", fmt = "";
        bool pending = false;
        void Flush()
        {
            if (!pending) return;
            sb.Append(text).Append(fmt);
            pending = false; text = "";
        }

        foreach (var inl in p.Inlines)
        {
            if (inl is Run r)
            {
                if (string.IsNullOrEmpty(r.Text)) continue;
                string f = RunFmt(r);
                if (pending && f == fmt) { text += r.Text; continue; }
                Flush();
                text = r.Text!; fmt = f; pending = true;
            }
            else if (inl is InlineTable it) { Flush(); sb.Append("<IT"); ShapeTable(sb, it.Table); sb.Append('>'); }
            else if (inl is InlineImage im) { Flush(); sb.Append("<IMG").Append(ImgFmt(im.Width, im.Height, im.AltText)).Append('>'); }
        }
        Flush();
    }

    // ---- formatting signatures -----------------------------------------------------------------
    //
    // The fuzz has been STYLING documents since it was written (case 3 sets weight, colours and link
    // targets; case 4 sets heading/quote/indent/list/spacing; case 12 sets cell fills and alignment) and
    // then comparing shapes that recorded NONE of it. That is the same hole the 2026-08-06 image/divider
    // round found from the other side — generation without observation is silently 0% coverage — so these
    // signatures close the pair: what the ops produce is now what the comparison reads.
    //
    // Only non-defaults are emitted, so an unstyled document's signature is empty and the shapes stay
    // readable. Doubles are rounded, because a format that legitimately quantizes (RTF half-points,
    // twips) settles in cycle 1 and this test compares cycle 1 against cycle 2 — the failures it reports
    // are formats that keep MOVING, not formats a writer cannot express.
    private static string Num(double d) => double.IsNaN(d) ? "nan" : Math.Round(d, 2).ToString(System.Globalization.CultureInfo.InvariantCulture);

    private static string Col(Color? c) => c is { } k ? $"#{k.A:X2}{k.R:X2}{k.G:X2}{k.B:X2}" : "";

    private static string Wrap(List<string> parts) => parts.Count == 0 ? "" : "{" + string.Join(",", parts) + "}";

    private static string RunFmt(Run r)
    {
        var parts = new List<string>();
        if (r.FontWeight.Weight != 400) parts.Add("w" + r.FontWeight.Weight);
        if (r.FontStyle != Windows.UI.Text.FontStyle.Normal) parts.Add("i" + r.FontStyle);
        if (r.TextDecorations != TextDecorationFlags.None) parts.Add("d" + r.TextDecorations);
        if (r.Foreground != null) parts.Add("fg" + Col(r.Foreground));
        if (r.Background != null) parts.Add("bg" + Col(r.Background));
        if (r.FontFamily != null) parts.Add("ff" + r.FontFamily);
        if (Math.Abs(r.FontSize - 10) > 0.005) parts.Add("fs" + Num(r.FontSize));
        if (!string.IsNullOrEmpty(r.NavigateUri)) parts.Add("a" + r.NavigateUri);
        return Wrap(parts);
    }

    private static string ParaFmt(Paragraph p)
    {
        var parts = new List<string>();
        if (p.HeadingLevel != 0) parts.Add("h" + p.HeadingLevel);
        if (p.IsQuote) parts.Add("q");
        if (p.ListType != ListKind.None) parts.Add("l" + p.ListType + "/" + p.ListLevel + "/" + p.ListMarker);
        if (p.TextAlignment != Microsoft.UI.Xaml.TextAlignment.Left) parts.Add("al" + p.TextAlignment);
        if (p.Background != null) parts.Add("bg" + Col(p.Background));
        if (p.Indent != 0) parts.Add("in" + Num(p.Indent));
        if (p.MarginRight != 0) parts.Add("mr" + Num(p.MarginRight));
        if (!double.IsNaN(p.LineSpacing)) parts.Add("ls" + Num(p.LineSpacing));
        if (!double.IsNaN(p.LineHeight)) parts.Add("lh" + Num(p.LineHeight));
        return Wrap(parts);
    }

    private static string CellFmt(TableCell cell)
    {
        var parts = new List<string>();
        if (cell.Background != null) parts.Add("bg" + Col(cell.Background));
        if (cell.VerticalAlignment != CellVerticalAlignment.Top) parts.Add("va" + cell.VerticalAlignment);
        return Wrap(parts);
    }

    private static string ImgFmt(double w, double h, string? alt)
    {
        var parts = new List<string> { Num(w) + "x" + Num(h) };
        if (!string.IsNullOrEmpty(alt)) parts.Add("alt" + alt);
        return Wrap(parts);
    }

    private static void ShapeTable(StringBuilder sb, TableBlock tb)
    {
        sb.Append('(').Append(tb.Rows).Append('x').Append(tb.Columns);
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            var (cs, rs) = tb.SpanOf(r, c);
            sb.Append('|').Append(r).Append(',').Append(c);
            if (cs > 1 || rs > 1) sb.Append('s').Append(cs).Append('x').Append(rs);
            sb.Append(CellFmt(cell)).Append(':');
            foreach (var cb in cell.Blocks) ShapeBlock(sb, cb);
        }
        sb.Append(')');
    }

    private static string Trunc(string s) => s.Length <= 900 ? s : s[..900] + "…";

    // Only the neighbourhood of the FIRST divergence — a whole fuzzed document's shape is unreadable,
    // and the first difference is where the non-idempotent step actually happened.
    private static string Diff(string a, string b)
    {
        int i = 0;
        while (i < a.Length && i < b.Length && a[i] == b[i]) i++;
        int from = Math.Max(0, i - 60);
        string Win(string s) => from >= s.Length ? "(end)" : s[from..Math.Min(s.Length, i + 60)].Replace("\n", "\\n");
        return $"first difference at {i}\n  once : …{Win(a)}…\n  twice: …{Win(b)}…";
    }

    private static string Dump(FlowDocument doc) => "doc: " + Trunc(Shape(doc));

    // ---- walkers -------------------------------------------------------------------------------

    private static IEnumerable<Paragraph> AllParagraphs(FlowDocument doc) => ParasIn(doc.Blocks);

    private static IEnumerable<Paragraph> ParasIn(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is Paragraph p)
            {
                yield return p;
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var q in ParasIn(cell.Blocks)) yield return q;
            }
            else if (b is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var q in ParasIn(cell.Blocks)) yield return q;
        }
    }

    // Every picture in the document, block or inline, at any nesting depth — returned as the base
    // TextElement because the two types share no image interface.
    private static IEnumerable<TextElement> AllImages(FlowDocument doc) => ImagesIn(doc.Blocks);

    private static IEnumerable<TextElement> ImagesIn(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is ImageBlock ib) yield return ib;
            else if (b is Paragraph p)
            {
                foreach (var inl in p.Inlines)
                {
                    if (inl is InlineImage im) yield return im;
                    else if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var x in ImagesIn(cell.Blocks)) yield return x;
                }
            }
            else if (b is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var x in ImagesIn(cell.Blocks)) yield return x;
        }
    }

    private static IEnumerable<TableBlock> AllTables(FlowDocument doc) => TablesIn(doc.Blocks);

    private static IEnumerable<TableBlock> TablesIn(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is TableBlock tb)
            {
                yield return tb;
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var t in TablesIn(cell.Blocks)) yield return t;
            }
            else if (b is Paragraph p)
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                    {
                        yield return it.Table;
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var t in TablesIn(cell.Blocks)) yield return t;
                    }
        }
    }

    private static int TextLen(Paragraph p)
    {
        int n = 0;
        foreach (var inl in p.Inlines) n += inl is Run r ? (r.Text?.Length ?? 0) : 1;
        return n;
    }
}
