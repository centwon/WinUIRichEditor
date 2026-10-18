using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The audit before publishing 1.3.0 (2026-10-18) of what came in after the 09-29 audit. The fuzz's Shape
/// compares a chosen summary of the model; the lean JSON writer omits fields by default value, so an omission whose
/// default disagrees with the reader's shows only in a field Shape leaves out. These compare EVERY public property
/// of the model by reflection instead.</summary>
[Collection(UiTests.Collection)]
public class PrePublishAuditTests(ITestOutputHelper output)
{
    // ---- a model comparison that knows no format ------------------------------------------------------------

    private static readonly HashSet<string> Skip = new(StringComparer.Ordinal)
    {
        "Parent",   // back-pointers: the walk follows children
        "Image",    // a decoded bitmap (device object); RawBytes is the content
        "TextHash", // a cache
    };

    internal static string? FirstDifference(object? a, object? b, string path = "doc")
    {
        if (a == null || b == null) return a == null && b == null ? null : $"{path}: {Show(a)} vs {Show(b)}";
        var t = a.GetType();
        if (t != b.GetType()) return $"{path}: type {t.Name} vs {b.GetType().Name}";
        if (a is string || t.IsPrimitive || t.IsEnum || a is decimal)
            return Equals(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}";
        if (a is byte[] ba) return ba.AsSpan().SequenceEqual((byte[])b) ? null : $"{path}: bytes differ";
        if (a is IList la)
        {
            var lb = (IList)b;
            if (la.Count != lb.Count) return $"{path}: count {la.Count} vs {lb.Count}";
            for (int i = 0; i < la.Count; i++)
                if (FirstDifference(la[i], lb[i], $"{path}[{i}]") is { } d) return d;
            return null;
        }
        if (t.IsValueType && t.Namespace?.StartsWith("WinUIRichEditor", StringComparison.Ordinal) != true)
            return Equals(a, b) ? null : $"{path}: {Show(a)} vs {Show(b)}"; // Color, FontWeight, …
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (p.GetIndexParameters().Length > 0 || !p.CanRead || Skip.Contains(p.Name)) continue;
            if (FirstDifference(p.GetValue(a), p.GetValue(b), $"{path}.{p.Name}") is { } d) return d;
        }
        return null;
    }

    private static string Show(object? v) => v switch { null => "null", double d => d.ToString("R"), _ => v.ToString() ?? "?" };

    private static FlowDocument Json(FlowDocument d) => DocumentSerializer.Deserialize(DocumentSerializer.Serialize(d));

    private static FlowDocument Flow(FlowDocument d)
    {
        var ms = new System.IO.MemoryStream();
        DocumentPackage.Save(d, ms);
        ms.Position = 0;
        return DocumentPackage.Load(ms);
    }

    // ---- the fuzz's documents, compared whole -----------------------------------------------------------------

    private static readonly MethodInfo SeedDocument = typeof(DocumentFuzzTests).GetMethod("SeedDocument", BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo ApplyRandomOp = typeof(DocumentFuzzTests).GetMethod("ApplyRandomOp", BindingFlags.NonPublic | BindingFlags.Static)!;

    private static readonly int Seeds =
        int.TryParse(Environment.GetEnvironmentVariable("RICHEDITOR_FUZZ_SEEDS"), out int s) && s > 0 ? s : 400;

    // A JSON or .flow round trip (twice) gives back every public property the model has, not only what Shape reads.
    [Fact]
    public void RandomDocuments_KeepEveryModelPropertyThroughTheNativeFormats()
    {
        var failures = new List<string>();
        // One UI-thread body per seed: UiThread.Run gives a body 60 s, and a widened run (RICHEDITOR_FUZZ_SEEDS) is longer.
        for (int seed = 0; seed < Seeds && failures.Count < 8; seed++) UiThread.Run(() =>
        {
            var rng = new Random(seed);
            var doc = (FlowDocument)SeedDocument.Invoke(null, new object[] { rng })!;
            for (int step = 0; step < 40; step++) ApplyRandomOp.Invoke(null, new object[] { doc, rng });
            foreach (var (name, round) in new (string, Func<FlowDocument, FlowDocument>)[] { ("json", Json), ("flow", Flow) })
            {
                try
                {
                    var once = round(doc);
                    var twice = round(once);
                    // The loader merges adjacent runs of one format (RunNormalizer.Compact, by design); the original
                    // is compared in that same form.
                    var expected = RunNormalizer.Compact((FlowDocument)doc.Clone());
                    if (FirstDifference(expected, once) is { } d1) failures.Add($"seed {seed} / {name}: {d1}");
                    else if (FirstDifference(once, twice) is { } d2) failures.Add($"seed {seed} / {name} (second trip): {d2}");
                }
                catch (Exception ex) { failures.Add($"seed {seed} / {name} threw: {ex.GetBaseException().Message}"); }
            }
        });
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    // ---- a selection: what Copy takes is what Delete removes ----------------------------------------------------

    // The letters of a document at any depth (cells, inline tables), as a multiset — whitespace, line breaks and the
    // object placeholder do not count, so the two paths may differ in how they spell structure but not in content.
    private static Dictionary<char, int> Letters(FlowDocument d)
    {
        var bag = new Dictionary<char, int>();
        void Blocks(IEnumerable<Block> bs) { foreach (var b in bs) Block(b); }
        void Block(Block b)
        {
            if (b is Paragraph p)
                foreach (var inl in p.Inlines)
                {
                    if (inl is Run r && r.Text != null)
                        foreach (char c in r.Text)
                            if (!char.IsWhiteSpace(c) && c != '￼') bag[c] = bag.GetValueOrDefault(c) + 1;
                    if (inl is InlineTable it) Block(it.Table);
                }
            else if (b is TableBlock t)
                foreach (var (_, _, cell) in t.LogicalCells()) Blocks(cell.Blocks);
        }
        Blocks(d.Blocks);
        return bag;
    }

    private static string BagDiff(Dictionary<char, int> a, Dictionary<char, int> b)
    {
        var sb = new StringBuilder();
        foreach (char c in a.Keys.Union(b.Keys).OrderBy(c => c))
        {
            int x = a.GetValueOrDefault(c), y = b.GetValueOrDefault(c);
            if (x != y) sb.Append($"'{c}' {x}/{y} ");
        }
        return sb.ToString();
    }

    // Random documents from the fuzz, random endpoints anywhere a caret can be (top level, cells, inline-table cells,
    // tables nested in either): the copy is built and the selection deleted through the editor's own paths, and the
    // letters the delete removed must be exactly the letters the copy holds. The rules for where a range leaves its
    // start paragraph and enters its end one were fixed three times (round 35, 10-15, 10-17), each in one path.
    [Fact]
    public void RandomSelections_DeleteExactlyWhatCopyTakes() => SelectionFuzz(rng =>
    {
        var doc = (FlowDocument)SeedDocument.Invoke(null, new object[] { rng })!;
        for (int step = 0; step < 40; step++) ApplyRandomOp.Invoke(null, new object[] { doc, rng });
        return doc;
    });

    // The upstream peer's generator for the same fuzz (its SelectionPositionOrderTests): inline tables nested three
    // deep, in cells and in cell paragraphs — harsher on the order than the edit fuzz's documents.
    [Fact]
    public void RandomSelectionsInDeepTables_DeleteExactlyWhatCopyTakes() => SelectionFuzz(DeepDocument);

    private static FlowDocument DeepDocument(Random rng)
    {
        int word = 0;
        string Text() => string.Concat(Enumerable.Range(0, rng.Next(0, 4)).Select(_ => (char)('a' + rng.Next(26)) + (word++ % 10).ToString()));
        Paragraph Para(int depth)
        {
            var p = new Paragraph();
            int parts = rng.Next(1, 4);
            for (int i = 0; i < parts; i++)
            {
                if (depth < 3 && rng.Next(5) == 0) p.Inlines.Add(new InlineTable { Table = Table(depth + 1) });
                else p.Inlines.Add(new Run { Text = Text() });
            }
            return p;
        }
        TableBlock Table(int depth)
        {
            var t = new TableBlock(1 + rng.Next(2), 1 + rng.Next(2));
            foreach (var row in t.Cells)
                foreach (var cell in row)
                {
                    cell.Blocks.Clear();
                    int n = 1 + rng.Next(2);
                    for (int i = 0; i < n; i++)
                        cell.Blocks.Add(depth < 3 && rng.Next(6) == 0 ? Table(depth + 1) : Para(depth));
                    if (cell.Blocks[^1] is not Paragraph) cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = Text() } } });
                }
            return t;
        }
        var doc = new FlowDocument();
        int blocks = 2 + rng.Next(4);
        for (int i = 0; i < blocks; i++) doc.Blocks.Add(rng.Next(4) == 0 ? Table(1) : Para(0));
        return doc;
    }

    private void SelectionFuzz(Func<Random, FlowDocument> makeDocument)
    {
        const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
        var T = typeof(WinUIRichEditor.Controls.RichEditor);
        var failures = new List<string>();
        int checkedRanges = 0;
        for (int seed = 0; seed < Seeds && failures.Count < 8; seed++) UiThread.Run(() => // one body per seed (60 s each)
        {
            var rng = new Random(seed);
            var doc = makeDocument(rng);
            // ONE editor for the whole run, a fresh copy of the document per range: editors that are never shown keep
            // their XAML halves until the runtime gets round to them, and a 20,000-seed run making one per range grew past
            // 3.5 GB and slowed to a crawl (one per seed did too).
            var ed = SharedEditor ??= new WinUIRichEditor.Controls.RichEditor();
            if (seed % 1000 == 0 && Environment.GetEnvironmentVariable("RICHEDITOR_FUZZ_PROGRESS") is { } log)
                System.IO.File.AppendAllText(log, $"{DateTime.Now:HH:mm:ss} selection seed {seed}{Environment.NewLine}");
            for (int trial = 0; trial < RangesPerDocument && failures.Count < 8; trial++)
            {
            ed.Document = doc.Clone(); // each range on a fresh copy
            var paras = (List<Paragraph>)T.GetMethod("AllParagraphs", NP)!.Invoke(ed, null)!;
            if (paras.Count == 0) continue;
            TextPointer Pick()
            {
                var p = paras[rng.Next(paras.Count)];
                int len = (int)T.GetMethod("GetParagraphLength", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)!
                    .Invoke(ed, new object[] { p })!;
                return new TextPointer(p, rng.Next(len + 1));
            }
            var a = Pick(); var b = Pick();
            string where = $"seed {seed}/{trial}: {Describe(a)} .. {Describe(b)}";
            try
            {
                T.GetField("_caret", NP)!.SetValue(ed, b);
                T.GetField("_selStart", NP)!.SetValue(ed, a);
                T.GetField("_selEnd", NP)!.SetValue(ed, b);
                // What Copy puts on the clipboard (CopyAsync): a cell-block selection copies the highlighted rectangle,
                // anything else the range.
                var cellBlock = T.GetMethod("CellBlockSelection", NP)!.Invoke(ed, null);
                FlowDocument copy;
                if (cellBlock != null)
                {
                    var ty = cellBlock.GetType();
                    var tb = (TableBlock)ty.GetField("Item1")!.GetValue(cellBlock)!;
                    int r0 = (int)ty.GetField("Item2")!.GetValue(cellBlock)!, c0 = (int)ty.GetField("Item3")!.GetValue(cellBlock)!;
                    int r1 = (int)ty.GetField("Item4")!.GetValue(cellBlock)!, c1 = (int)ty.GetField("Item5")!.GetValue(cellBlock)!;
                    copy = new FlowDocument();
                    copy.Blocks.Add(tb.Extract(r0, c0, r1, c1));
                }
                else copy = (FlowDocument)T.GetMethod("BuildSelectionDocument", NP)!.Invoke(ed, new object[] { new TextRange(a, b) })!;
                var before = Letters(ed.Document!);
                T.GetMethod("DeleteSelection", NP)!.Invoke(ed, null);
                var after = Letters(ed.Document!);
                var removed = new Dictionary<char, int>();
                foreach (var (c, n) in before) if (n - after.GetValueOrDefault(c) is int k && k != 0) removed[c] = k;
                foreach (var (c, n) in after) if (!before.ContainsKey(c)) removed[c] = -n;
                var copied = Letters(copy);
                checkedRanges++;
                if (BagDiff(removed, copied) is { Length: > 0 } diff) failures.Add($"{where}: removed/copied {diff}");
            }
            catch (TargetInvocationException ex) { failures.Add($"{where}: threw {ex.InnerException}"); }
            }
        });
        output.WriteLine($"{checkedRanges} ranges");
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private const int RangesPerDocument = 4; // × Seeds

    private static WinUIRichEditor.Controls.RichEditor? SharedEditor; // UI thread only

    private static string Describe(TextPointer tp)
    {
        var sb = new StringBuilder();
        for (object? cur = tp.Paragraph?.Parent; cur is TextElement te; cur = te.Parent)
            sb.Insert(0, te switch { TableCell => "cell/", TableBlock => "table/", InlineTable => "inline/", _ => "" });
        return sb + $"p@{tp.Offset}";
    }

    // ---- the reported cases ----------------------------------------------------------------------------------

    private const BindingFlags NPI = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type Ed = typeof(WinUIRichEditor.Controls.RichEditor);

    private static (WinUIRichEditor.Controls.RichEditor ed, Paragraph host, TableBlock t) InlineTableLine()
    {
        var t = new TableBlock(2, 2);
        int n = 1;
        foreach (var row in t.Cells) foreach (var c in row) c.Para.Inlines.Add(new Run { Text = "cell" + n++ });
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "ab" }, new InlineTable { Table = t }, new Run { Text = "cd" } } });
        var ed = new WinUIRichEditor.Controls.RichEditor { Document = doc };
        var host = (Paragraph)ed.Document!.Blocks[0];
        return (ed, host, host.Inlines.OfType<InlineTable>().Single().Table);
    }

    private static void Caret(WinUIRichEditor.Controls.RichEditor ed, TextPointer tp)
    {
        Ed.GetField("_caret", NPI)!.SetValue(ed, tp);
        Ed.GetField("_selStart", NPI)!.SetValue(ed, tp);
        Ed.GetField("_selEnd", NPI)!.SetValue(ed, tp);
    }

    private static string Cells(TableBlock t) => string.Join(",", t.Cells.SelectMany(r => r).Select(c => string.Concat(c.Para.Inlines.OfType<Run>().Select(r => r.Text))));

    // One Shift+Left from just after an inline table selects the end of its last cell — nothing. Delete emptied every
    // other cell of the table, because the range was read from the host's text after the table to that cell.
    [Fact]
    public void ShiftLeftIntoAnInlineTable_ThenDelete_KeepsTheOtherCells() => UiThread.Run(() =>
    {
        var (ed, host, t) = InlineTableLine();
        Caret(ed, new TextPointer(host, 3)); // after "ab" and the table
        Ed.GetMethod("MoveCaretLeft", NPI)!.Invoke(ed, new object[] { true });
        Ed.GetMethod("DeleteSelection", NPI)!.Invoke(ed, null);
        Assert.Equal("cell1,cell2,cell3,cell4", Cells(t));
        Assert.Equal("ab\uFFFCcd", string.Concat(host.Inlines.Select(i => i is Run r ? r.Text : "\uFFFC")));
    });

    // Two Shift+Lefts take the last cell's last character: that, and nothing else.
    [Fact]
    public void ShiftLeftTwiceIntoAnInlineTable_ThenDelete_TakesOneCharacter() => UiThread.Run(() =>
    {
        var (ed, host, t) = InlineTableLine();
        Caret(ed, new TextPointer(host, 3));
        Ed.GetMethod("MoveCaretLeft", NPI)!.Invoke(ed, new object[] { true });
        Ed.GetMethod("MoveCaretLeft", NPI)!.Invoke(ed, new object[] { true });
        Ed.GetMethod("DeleteSelection", NPI)!.Invoke(ed, null);
        Assert.Equal("cell1,cell2,cell3,cell", Cells(t));
    });

    // Shift+Right from just before the table into its first cell selects nothing of the host: the copy held "cd".
    [Fact]
    public void ShiftRightIntoAnInlineTable_CopiesNothingOfTheHost() => UiThread.Run(() =>
    {
        var (ed, host, _) = InlineTableLine();
        Caret(ed, new TextPointer(host, 2));
        Ed.GetMethod("MoveCaretRight", NPI)!.Invoke(ed, new object[] { true });
        var range = new TextRange((TextPointer)Ed.GetField("_selStart", NPI)!.GetValue(ed)!, (TextPointer)Ed.GetField("_selEnd", NPI)!.GetValue(ed)!);
        var copy = (FlowDocument)Ed.GetMethod("BuildSelectionDocument", NPI)!.Invoke(ed, new object[] { range })!;
        Assert.Empty(Letters(copy));
        Assert.Equal("", range.GetText().Trim('\n'));
    });

    // A paragraph in no document (a host's own, before it is added) still deletes a range around its inline table — the
    // span rule needs the document only to order paragraphs, and inside one paragraph it needs none (upstream's
    // InlineTableTests caught the first draft returning without deleting).
    [Fact]
    public void ARangeInAParagraphOfNoDocument_StillDeletes() => UiThread.Run(() =>
    {
        var p = new Paragraph { Inlines = { new Run { Text = "AB" }, new InlineTable { Table = new TableBlock(1, 1) }, new Run { Text = "CD" } } };
        new TextRange(new TextPointer(p, 1), new TextPointer(p, 4)).Delete(); // B, the table, C
        Assert.Equal("AD", string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)));
        Assert.DoesNotContain(p.Inlines, i => i is InlineTable);
    });

    // A range over an inline table whole carries it as ONE object (the in-app rich copy), not its cells again after it —
    // as upstream's InlineTableTests.GetRichInlines_KeepsInlineTable.
    [Fact]
    public void RichInlinesOfARangeOverAWholeInlineTable_HoldTheTableOnce() => UiThread.Run(() =>
    {
        var (_, host, _) = InlineTableLine(); // "ab" [table] "cd"
        var inlines = new TextRange(new TextPointer(host, 1), new TextPointer(host, 4)).GetRichInlines(); // b, the table, c
        Assert.Equal(3, inlines.Count);
        Assert.IsType<InlineTable>(inlines[1]);
        Assert.Equal("b", (inlines[0] as Run)?.Text);
        Assert.Equal("c", (inlines[2] as Run)?.Text);
    });

    // The order itself, in both the model's and the editor's comparison: a cell position lies between the table's
    // offset and the next in its host.
    [Fact]
    public void ACellPosition_SortsWhereItsTableIsOnTheHostLine() => UiThread.Run(() =>
    {
        var (ed, host, t) = InlineTableLine();
        var cell = new TextPointer(t.Cells[1][1].Para, 2);
        int Cmp(TextPointer a, TextPointer b) => (int)Ed.GetMethod("ComparePositions", NPI)!.Invoke(ed, new object[] { a, b })!;
        foreach (var (hostOffset, sign) in new[] { (0, 1), (2, 1), (3, -1), (5, -1) })
        {
            Assert.Equal(sign, Math.Sign(cell.CompareTo(new TextPointer(host, hostOffset))));
            Assert.Equal(sign, Math.Sign(Cmp(cell, new TextPointer(host, hostOffset))));
        }
        // and between cells, the table's own order
        Assert.True(new TextPointer(t.Cells[0][0].Para, 5).CompareTo(cell) < 0);
    });

    // Values a host can set that the fuzz never picks: each one at the reader's default for an ABSENT field and at
    // the type's own default, on every kind of block, in a cell and outside one.
    [Fact]
    public void EdgeValues_SurviveJsonTwice() => UiThread.Run(() =>
    {
        var failures = new List<string>();
        foreach (var doc in EdgeDocuments())
        {
            var once = Json(doc.Doc);
            var twice = Json(once);
            if (FirstDifference(RunNormalizer.Compact((FlowDocument)doc.Doc.Clone()), once) is { } d1) failures.Add($"{doc.Name}: {d1}");
            else if (FirstDifference(once, twice) is { } d2) failures.Add($"{doc.Name} (second trip): {d2}");
        }
        foreach (var f in failures) output.WriteLine(f);
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    });

    // Known, decided normalizations a JSON round trip makes — kept out of the comparison so it guards everything else:
    //   · a divider's Indent is not written (a divider draws full width; the margin menu's left margin does nothing);
    //   · a paragraph's NaN top margin (a host's AutoTopMargin; paragraphs have no automatic gap) reads back as 0;
    //   · a run's FontSize <= 0 ("use DefaultFontSize", a host model's choice) reads back as the 10 pt default —
    //     the same in the upstream peer, whose files share the format.
    private static IEnumerable<(string Name, FlowDocument Doc)> EdgeDocuments()
    {
        double[] margins = { 0, 10, -5, 0.25, Block.AutoTopMargin };
        foreach (double v in margins)
        {
            yield return ($"divider mt/mb {v}", Doc(new DividerBlock { MarginTop = v, MarginBottom = double.IsNaN(v) ? 0 : v }));
            yield return ($"table mt/mb {v}", Doc(Table(t => { t.MarginTop = v; t.MarginBottom = double.IsNaN(v) ? 10 : v; t.Indent = double.IsNaN(v) ? 0 : v; })));
            if (!double.IsNaN(v))
                yield return ($"para mt/mb {v}", Doc(new Paragraph { MarginTop = v, MarginBottom = v, MarginRight = v, Indent = v, Inlines = { new Run { Text = "x" } } }));
        }
        foreach (double fs in new[] { 10.0, 9.5, 1638 })
            yield return ($"run fs {fs}", Doc(new Paragraph { Inlines = { new Run { Text = "x", FontSize = fs } } }));
        yield return ("run empty text", Doc(new Paragraph { Inlines = { new Run { Text = "" }, new Run { Text = null } } }));
        yield return ("empty paragraph", Doc(new Paragraph()));
        yield return ("cell para with a fill and a cell fill", Doc(Table(t =>
        {
            var cell = t.Cells[0][0];
            cell.Background = Windows.UI.Color.FromArgb(255, 1, 2, 3);
            cell.Blocks.Clear();
            cell.Blocks.Add(new Paragraph { Background = Windows.UI.Color.FromArgb(255, 4, 5, 6), Inlines = { new Run { Text = "x" } } });
        })));
        yield return ("cell with one empty paragraph and a cell fill", Doc(Table(t => t.Cells[0][0].Background = Windows.UI.Color.FromArgb(255, 1, 2, 3))));
        yield return ("table row heights", Doc(Table(t => { t.RowHeights.Clear(); t.RowHeights.Add(0); t.RowHeights.Add(30); })));
        yield return ("inline table in a cell", Doc(Table(t =>
        {
            var cp = t.Cells[0][0].Para;
            cp.Inlines.Add(new InlineTable { Table = new TableBlock(1, 2) });
        })));
        yield return ("list level, quote, heading", Doc(new Paragraph { ListType = ListKind.Ordered, ListLevel = 3, IsQuote = true, HeadingLevel = 2, LineSpacing = 1.0, LineHeight = 0, Inlines = { new Run { Text = "x" } } }));
    }

    private static TableBlock Table(Action<TableBlock> edit)
    {
        var t = new TableBlock(2, 2);
        edit(t);
        return t;
    }

    private static FlowDocument Doc(Block b)
    {
        var d = new FlowDocument();
        d.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
        d.Blocks.Add(b);
        d.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "below" } } });
        return d;
    }
}
