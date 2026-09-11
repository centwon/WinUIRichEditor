using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Random sequences of EDITOR COMMANDS, checked after every step.
/// <para><see cref="DocumentFuzzTests"/> edits the model directly and never goes through
/// <see cref="RichEditor"/> — it was written when the control needed a runtime the tests could not
/// start. So the command layer (selection → run splitting, cell ranges, paragraph scope, list toggling
/// with hard-line splitting, pending caret styles, the format painter) had never run in combination with
/// anything, and ~25 public formatting commands were not called by a single test. Upstream's fuzz drives
/// the editor's commands; this is that axis, on the <see cref="UiThread"/> harness.</para>
/// <para>Three oracles after each step:</para>
/// <list type="number">
/// <item><b>Structure</b> — the model invariants <see cref="DocumentFuzzTests"/> checks, plus the
/// control's own: block lists normalized (core rule #5), every Parent pointing at the container that
/// holds it, and caret/selection naming a reachable paragraph at an in-range offset.</item>
/// <item><b>History</b> — a step that changed the document must be exactly one undo step: Undo brings
/// back the shape from before it, Redo the shape after it. A command that mutates without a checkpoint,
/// or checkpoints halfway through, or whose state Clone does not carry, fails here.</item>
/// <item><b>Change tracking</b> — the same step must leave <see cref="RichEditor.IsModified"/> set and
/// must have raised <see cref="RichEditor.TextChanged"/>. The host's "save changes?" prompt is built on
/// these two, so a mutation that slips past them is a data-loss path.</item>
/// </list>
/// <para>The history check runs Undo/Redo after every mutating step, and that resets the typing
/// coalesce key — so each step here is its own undo group by construction. Coalescing itself is pinned
/// by <see cref="ControlUndoTests"/>.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlCommandFuzzTests
{
    // A control-level step costs a relayout plus, for mutating steps, two document clones — far more than
    // a model step, so the CI budget is smaller than DocumentFuzzTests'. Widen with
    // RICHEDITOR_CMD_FUZZ_SEEDS for an audit run (a separate variable, so widening the model fuzz to
    // 20000 does not also make this one run for an hour).
    private static readonly int Seeds =
        int.TryParse(Environment.GetEnvironmentVariable("RICHEDITOR_CMD_FUZZ_SEEDS"), out int s) && s > 0 ? s : 24;
    private const int StepsPerSeed = 120;

    // RICHEDITOR_CMD_FUZZ_ONLY=746,1035 reruns exactly those seeds: a 2000-seed audit run takes ~8
    // minutes, one failing seed about a second.
    private static IEnumerable<int> SeedsToRun()
    {
        string? only = Environment.GetEnvironmentVariable("RICHEDITOR_CMD_FUZZ_ONLY");
        if (!string.IsNullOrWhiteSpace(only))
            return only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(int.Parse).ToList();
        return Enumerable.Range(0, Seeds);
    }

    // Which check failed matters as much as what it found: "the caret points at a paragraph not in the
    // document" right after a command is that command's defect, and the same message after the history
    // walk's Undo is the history's.
    private static void Check(RichEditor ed, string phase)
    {
        try { CheckEditor(ed); }
        catch (InvalidOperationException ex) { throw new InvalidOperationException($"[{phase}] {ex.Message}", ex); }
    }

    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type T = typeof(RichEditor);

    private static readonly FieldInfo CaretF = T.GetField("_caret", NP)!;
    private static readonly FieldInfo SelStartF = T.GetField("_selStart", NP)!;
    private static readonly FieldInfo SelEndF = T.GetField("_selEnd", NP)!;
    private static readonly MethodInfo FindCellM = T.GetMethod("FindCell", NS)!;

    // ---- hosting ----------------------------------------------------------------------------------

    // Vertical movement and line edges need a real CanvasTextLayout, so the editor is hosted — one editor
    // for the run, with its Document replaced per seed (see ControlCaretTests for why not one per test).
    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor
        {
            Document = new FlowDocument(),
            PageSize = RichEditorPageSize.Continuous,
        });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    [Fact]
    public void RandomCommandSequences_KeepStructureHistoryAndChangeTracking()
    {
        var ed = Shared.Value;
        var failures = new List<string>();
        var kinds = new Dictionary<string, int>();

        int textChanged = 0;
        void OnTextChanged(object? s, EventArgs e) => textChanged++;
        UiThread.Run(() => ed.TextChanged += OnTextChanged);
        try
        {
            // One marshalled body PER SEED. UiThread gives each body 60 s, and running every seed inside
            // one body put a widened audit run straight into that limit (400 seeds took 51 s, 2000 died
            // at 60 s with a TimeoutException that said nothing about the editor).
            foreach (int seed in SeedsToRun())
            {
                UiThread.Run(() =>
                {
                    var rng = new Random(seed);
                    var log = new List<string>();
                    string step = "(seed)";
                    try
                    {
                        ed.Document = SeedDocument(rng);
                        Call(ed, "RelayoutToViewport");
                        Check(ed, "seed document");

                        for (int i = 0; i < StepsPerSeed; i++)
                        {
                            string before = DocumentFuzzTests.Shape(ed.Document!);
                            ed.MarkSaved();
                            textChanged = 0;

                            string op = ApplyRandomCommand(ed, rng);
                            step = $"step {i}: {op}";
                            log.Add(step);
                            Check(ed, "after the step");

                            string after = DocumentFuzzTests.Shape(ed.Document!);
                            if (after == before) continue;

                            if (!ed.IsModified)
                                throw new InvalidOperationException("the document changed but IsModified is false");
                            if (textChanged == 0)
                                throw new InvalidOperationException("the document changed but TextChanged was not raised");

                            // The step's inverse takes the document back to `before`, and the step's own
                            // direction brings it to `after` again. For an ordinary edit the inverse is Undo;
                            // for a step that WAS an Undo it is Redo (and vice versa) — undoing once more
                            // there would walk past this step into the one before it.
                            Action inverse = op == "undo" ? ed.Redo : ed.Undo;
                            Action again = op == "undo" ? ed.Undo : ed.Redo;

                            inverse();
                            string undone = DocumentFuzzTests.Shape(ed.Document!);
                            if (undone != before)
                                throw new InvalidOperationException(
                                    "the inverse did not restore the state before this step.\n" + DocumentFuzzTests.Diff(before, undone));
                            Check(ed, $"after the inverse ({(op == "undo" ? "redo" : "undo")})");

                            again();
                            string redone = DocumentFuzzTests.Shape(ed.Document!);
                            if (redone != after)
                                throw new InvalidOperationException(
                                    "repeating the step did not bring it back.\n" + DocumentFuzzTests.Diff(after, redone));
                            Check(ed, $"after repeating it ({(op == "undo" ? "undo" : "redo")})");
                        }
                    }
                    catch (Exception ex)
                    {
                        // Tally by (command, first line of the message): a widened run reports dozens of
                        // seeds, and what matters first is how many DISTINCT defects that is.
                        string op = step.Contains(": ") ? step[(step.IndexOf(": ") + 2)..] : step;
                        string kind = $"{op} → {ex.GetType().Name}: {ex.Message.Split('\n')[0]}";
                        kinds[kind] = kinds.TryGetValue(kind, out int k) ? k + 1 : 1;
                        failures.Add($"seed {seed} broke at '{step}': {ex.GetType().Name}: {ex.Message}\n" +
                                     $"  history: {string.Join(" -> ", log.TakeLast(10))}\n" +
                                     $"  doc: {Trunc(ed.Document == null ? "<null>" : DocumentFuzzTests.Shape(ed.Document))}\n" +
                                     $"--- origin ---\n{ex.StackTrace}");
                    }
                });
            }
        }
        finally
        {
            UiThread.Run(() =>
            {
                ed.TextChanged -= OnTextChanged;
                ed.Document = new FlowDocument(); // leave the shared editor clean for whoever hosts next
            });
        }

        if (failures.Count > 0)
            throw new Xunit.Sdk.XunitException(
                $"{failures.Count} failure(s) over {SeedsToRun().Count()} seeds, by kind:\n" +
                string.Join("\n", kinds.OrderByDescending(kv => kv.Value).Select(kv => $"  {kv.Value,3} × {kv.Key}")) +
                "\n\n" + string.Join("\n\n", failures.Take(6)) +
                (failures.Count > 6 ? $"\n\n… and {failures.Count - 6} more" : ""));
    }

    // ---- document generation ----------------------------------------------------------------------

    private static FlowDocument SeedDocument(Random rng)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("첫 문단 alpha beta"));
        int n = rng.Next(1, 4);
        for (int i = 0; i < n; i++)
            doc.Blocks.Add(rng.Next(3) == 0 ? NewTable(rng) : Para($"문단 {i} gamma delta"));
        doc.Blocks.Add(Para("끝 epsilon"));
        return doc; // assigned through the Document setter, which wires Parent and normalizes
    }

    private static Paragraph Para(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static TableBlock NewTable(Random rng)
    {
        var tb = new TableBlock(rng.Next(1, 4), rng.Next(1, 4));
        foreach (var (r, c, cell) in tb.LogicalCells())
            ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c} word";
        return tb;
    }

    // A 2×2 PNG — real bytes, so image insertion goes through the real decode/measure path.
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static readonly Color?[] Colors =
    {
        Microsoft.UI.ColorHelper.FromArgb(255, 255, 0, 0),
        Microsoft.UI.ColorHelper.FromArgb(255, 0, 128, 0),
        Microsoft.UI.ColorHelper.FromArgb(128, 0, 0, 255),
        null,
    };

    private static readonly TextAlignment[] Alignments =
        { TextAlignment.Left, TextAlignment.Center, TextAlignment.Right, TextAlignment.Justify };

    // ---- commands ---------------------------------------------------------------------------------
    //
    // Public commands are called directly. Keys, table structure and the format painter's apply step have
    // no public entry point (they hang off KeyRoutedEventArgs, the context menu and pointer-release, none
    // of which a test can synthesize), so the private methods those handlers call are invoked instead —
    // the same code the user reaches, minus only the event dispatch.

    private static string ApplyRandomCommand(RichEditor ed, Random rng)
    {
        switch (rng.Next(40))
        {
            // -- caret and selection (no mutation; they set up what the next command acts on)
            case 0: return PlaceCaret(ed, rng);
            case 1: return SelectRange(ed, rng);
            case 2: Call(ed, "MoveCaretLeft", rng.Next(2) == 0); return "left";
            case 3: Call(ed, "MoveCaretRight", rng.Next(2) == 0); return "right";
            case 4: Call(ed, "MoveCaretVertical", rng.Next(2) == 0, rng.Next(2) == 0); return "up/down";
            case 5: Call(ed, "MoveToLineEdge", rng.Next(2) == 0, rng.Next(2) == 0); return "home/end";
            case 6: Call(ed, "WordMove", rng.Next(2) == 0, rng.Next(2) == 0); return "word-move";
            case 7: Call(ed, "SelectAll"); return "select-all";

            // -- typing and deleting
            case 8: ed.InsertText(new[] { "abc", "한글", "a", " ", "x y" }[rng.Next(5)]); return "type";
            // The URL alone: a space typed by a LATER step (case 8) is what auto-links it. Typing both here
            // would be two undo groups in one step, which the history oracle rightly counts as one too many.
            case 9: ed.InsertText("www.example.com"); return "type-url";
            case 10: Call(ed, "Backspace"); return "backspace";
            case 11: Call(ed, "DeleteForward"); return "delete";
            case 12: Call(ed, "WordDelete", rng.Next(2) == 0); return "word-delete";
            case 13: Call(ed, "InsertParagraphBreak", rng.Next(3) == 0); return "enter";
            case 14: Call(ed, "HandleTab", rng.Next(2) == 0); return "tab";

            // -- character formatting
            case 15: ed.ToggleBold(); return "bold";
            case 16: ed.ToggleItalic(); return "italic";
            case 17: ed.ToggleUnderline(); return "underline";
            case 18: ed.ToggleStrikethrough(); return "strike";
            case 19: ed.SetFontSize(new[] { 8.0, 12.0, 36.0 }[rng.Next(3)]); return "font-size";
            case 20: if (rng.Next(2) == 0) ed.IncreaseFontSize(); else ed.DecreaseFontSize(); return "font-step";
            case 21: ed.SetRunFontFamily(rng.Next(2) == 0 ? "Arial" : "맑은 고딕"); return "font-family";
            case 22: ed.SetForeground(Colors[rng.Next(Colors.Length)]); return "foreground";
            case 23: ed.SetHighlight(Colors[rng.Next(Colors.Length)]); return "highlight";
            case 24: ed.ClearFormatting(); return "clear-formatting";
            case 25: ed.SetHyperlink(rng.Next(3) == 0 ? null : "https://example.com/"); return "hyperlink";
            case 26: return FormatPainter(ed, rng);

            // -- paragraph formatting
            case 27: ed.SetHeading(rng.Next(0, 7)); return "heading";
            case 28: ed.SetTextAlignment(Alignments[rng.Next(Alignments.Length)]); return "align";
            case 29:
                if (rng.Next(2) == 0) ed.SetLineSpacing(new[] { 1.0, 1.5, 2.0, double.NaN }[rng.Next(4)]);
                else ed.SetLineHeight(new[] { 20.0, 40.0, double.NaN }[rng.Next(3)]);
                return "line-spacing";
            case 30: ed.ToggleQuote(); return "quote";
            case 31: ed.Indent(rng.Next(2) == 0 ? 20 : -20); return "indent";
            case 32:
                switch (rng.Next(4))
                {
                    case 0: ed.ToggleBullet(); return "bullet";
                    case 1: ed.ToggleNumbering(); return "numbering";
                    case 2: ed.SetListStyle((ListMarkerStyle)rng.Next(0, 10)); return "list-style";
                    default: ed.RemoveList(); return "remove-list";
                }

            // -- structure
            case 33: ed.InsertTable(rng.Next(1, 3), rng.Next(1, 3)); return "insert-table";
            case 34: ed.InsertInlineTable(1, rng.Next(1, 3)); return "insert-inline-table";
            case 35: ed.InsertDivider(); return "insert-divider";
            case 36:
                if (rng.Next(2) == 0) ed.InsertImageBlock(TinyPng, "image/png");
                else ed.InsertInlineImage(TinyPng, "image/png");
                return "insert-image";
            case 37: return TableCommand(ed, rng);

            // -- history, driven as a user would (on top of the per-step check)
            case 38: ed.Undo(); return "undo";
            default: ed.Redo(); return "redo";
        }
    }

    // A caret anywhere a click could put it: any paragraph the document reaches, any offset in it.
    private static string PlaceCaret(RichEditor ed, Random rng)
    {
        var paras = Paragraphs(ed.Document!.Blocks).ToList();
        var p = paras[rng.Next(paras.Count)];
        var tp = new TextPointer(p, rng.Next(Len(p) + 1));
        CaretF.SetValue(ed, tp);
        SelStartF.SetValue(ed, new TextPointer(p, tp.Offset));
        SelEndF.SetValue(ed, new TextPointer(p, tp.Offset));
        return "place-caret";
    }

    // A selection a drag could make: both ends in the same block list (siblings, or the same cell), or
    // in two cells of the same table — which is the rectangular cell-block selection. Arbitrary pairs
    // across nesting levels are not generated; a drag cannot produce them, and a failure there would
    // report a state no user reaches.
    private static string SelectRange(RichEditor ed, Random rng)
    {
        var groups = new List<List<Paragraph>>();
        CollectGroups(ed.Document!.Blocks, groups);
        var g = groups[rng.Next(groups.Count)];
        var a = g[rng.Next(g.Count)];
        var b = g[rng.Next(g.Count)];
        var s = new TextPointer(a, rng.Next(Len(a) + 1));
        var e = new TextPointer(b, rng.Next(Len(b) + 1));
        SelStartF.SetValue(ed, s);
        SelEndF.SetValue(ed, e);
        CaretF.SetValue(ed, new TextPointer(e.Paragraph, e.Offset)); // the caret sits at the drag's end
        return "select-range";
    }

    // Each block list's own paragraphs form one group, and each table contributes one more group made of
    // the first paragraph of every logical cell (so a selection can span cells of ONE table).
    private static void CollectGroups(IEnumerable<Block> blocks, List<List<Paragraph>> groups)
    {
        var mine = new List<Paragraph>();
        foreach (var b in blocks)
        {
            if (b is Paragraph p)
            {
                mine.Add(p);
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it) CollectTableGroups(it.Table, groups);
            }
            else if (b is TableBlock tb) CollectTableGroups(tb, groups);
        }
        if (mine.Count > 0) groups.Add(mine);
    }

    private static void CollectTableGroups(TableBlock tb, List<List<Paragraph>> groups)
    {
        var cellFirsts = new List<Paragraph>();
        foreach (var (_, _, cell) in tb.LogicalCells())
        {
            if (cell.Blocks.OfType<Paragraph>().FirstOrDefault() is { } fp) cellFirsts.Add(fp);
            CollectGroups(cell.Blocks, groups);
        }
        if (cellFirsts.Count > 1) groups.Add(cellFirsts);
    }

    // Arm from the current caret/selection, move to a random selection, apply — what capture-then-drag
    // does, with pointer-release replaced by the method it calls.
    private static string FormatPainter(RichEditor ed, Random rng)
    {
        ed.StartFormatPainter();
        if (!ed.IsFormatPainterActive) return "format-painter(not armed)";
        SelectRange(ed, rng);
        Call(ed, "ApplyFormatPainterToSelection");
        if (ed.IsFormatPainterActive) ed.CancelFormatPainter(); // armed but no selection to paint: disarm
        return "format-painter";
    }

    // The context menu's table commands, against the caret's own table.
    private static string TableCommand(RichEditor ed, Random rng)
    {
        var caret = (TextPointer)CaretF.GetValue(ed)!;
        if (caret.Paragraph == null) return "table(no caret)";
        if (FindCellM.Invoke(null, new object[] { caret.Paragraph }) is not { } loc) return "table(not in a table)";
        var t = loc.GetType();
        var tb = (TableBlock)t.GetField("Item1")!.GetValue(loc)!;
        int r = (int)t.GetField("Item2")!.GetValue(loc)!;
        int c = (int)t.GetField("Item3")!.GetValue(loc)!;

        switch (rng.Next(7))
        {
            // The menu's own indices: above/left = r/c, below/right = past the whole (possibly merged) cell.
            case 0: Call(ed, "TableInsertRow", tb, rng.Next(2) == 0 ? r : RichEditor.RowBelowIndex(tb, r, c)); return "table-insert-row";
            case 1: Call(ed, "TableInsertColumn", tb, rng.Next(2) == 0 ? c : RichEditor.ColumnRightIndex(tb, r, c)); return "table-insert-column";
            case 2: Call(ed, "TableDeleteRow", tb, r); return "table-delete-row";
            case 3: Call(ed, "TableDeleteColumn", tb, c); return "table-delete-column";
            case 4:
            {
                // Select from this cell to another of the same table, then merge through the menu's own
                // entry point — SelectedCellRange and the IsCleanRect gate included. The drag's end is a
                // LOGICAL cell: a covered slot is not drawn and not hit-testable, so no drag ends in one.
                // (Picking from Cells[][] did, and when IsCleanRect then refused the merge, the caret this
                // harness had placed was left in a covered cell — a failure no user can reach.)
                // Same two draws as before, mapped to the owning anchor, so a failing seed keeps
                // reproducing across this change (a different draw count would re-route every later step).
                var (ar, ac) = tb.AnchorOf(rng.Next(r, tb.Rows), rng.Next(c, tb.Columns));
                var other = tb.Cells[ar][ac];
                SelStartF.SetValue(ed, new TextPointer(caret.Paragraph, 0));
                var op = other.Blocks.OfType<Paragraph>().First();
                SelEndF.SetValue(ed, new TextPointer(op, 0));
                CaretF.SetValue(ed, new TextPointer(op, 0));
                Call(ed, "TableMergeSelected", tb);
                return "table-merge";
            }
            case 5:
            {
                var (cs, rs) = tb.SpanOf(r, c);
                if (cs <= 1 && rs <= 1) return "table-unmerge(not merged)";
                Call(ed, "TableUnmergeCell", tb, r, c);
                return "table-unmerge";
            }
            default: Call(ed, "DeleteTable", tb); return "table-delete";
        }
    }

    private static void Call(RichEditor ed, string name, params object?[] args)
    {
        var m = T.GetMethod(name, NP) ?? throw new MissingMethodException(T.Name, name);
        try { m.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); // report the product's own stack
        }
    }

    // ---- invariants -------------------------------------------------------------------------------

    private static void CheckEditor(RichEditor ed)
    {
        var doc = ed.Document ?? throw new InvalidOperationException("Document is null");
        DocumentFuzzTests.CheckInvariants(doc);
        CheckContainer(doc.Blocks, doc, "document");

        var reachable = new HashSet<Paragraph>(Paragraphs(doc.Blocks), ReferenceEqualityComparer.Instance);
        foreach (var (name, f) in new[] { ("caret", CaretF), ("selStart", SelStartF), ("selEnd", SelEndF) })
        {
            if (f.GetValue(ed) is not TextPointer tp || tp.Paragraph == null) continue;
            if (!reachable.Contains(tp.Paragraph))
                throw new InvalidOperationException(
                    $"{name} points at a paragraph not in the document (text '{Text(tp.Paragraph)}', " +
                    $"Parent={tp.Paragraph.Parent?.GetType().Name ?? "<null>"}{CellDiagnosis(doc, tp.Paragraph)})");
            int len = Len(tp.Paragraph);
            if (tp.Offset < 0 || tp.Offset > len)
                throw new InvalidOperationException($"{name} offset {tp.Offset} is outside its paragraph (length {len})");
        }
    }

    // Core rule #5 (NormalizeBlocks) — a block list starts and ends with a paragraph and never has two
    // non-paragraph blocks side by side, which is what lets the caret reach both sides of every object —
    // and the Parent chain, which hit-testing, FindCell and every table command resolve through.
    private static void CheckContainer(List<Block> blocks, object owner, string where)
    {
        if (blocks.Count == 0) throw new InvalidOperationException($"{where}: empty block list");
        if (blocks[0] is not Paragraph)
            throw new InvalidOperationException($"{where}: first block is {blocks[0].GetType().Name}, not a Paragraph");
        if (blocks[^1] is not Paragraph)
            throw new InvalidOperationException($"{where}: last block is {blocks[^1].GetType().Name}, not a Paragraph");
        for (int i = 0; i + 1 < blocks.Count; i++)
            if (blocks[i] is not Paragraph && blocks[i + 1] is not Paragraph)
                throw new InvalidOperationException(
                    $"{where}: {blocks[i].GetType().Name} and {blocks[i + 1].GetType().Name} are adjacent at {i}");

        foreach (var b in blocks)
        {
            if (!ReferenceEquals(b.Parent, owner))
                throw new InvalidOperationException($"{where}: a {b.GetType().Name}'s Parent is not the list that holds it");
            if (b is Paragraph p)
            {
                foreach (var inl in p.Inlines)
                {
                    if (!ReferenceEquals(inl.Parent, p))
                        throw new InvalidOperationException($"{where}: a {inl.GetType().Name}'s Parent is not its paragraph");
                    if (inl is InlineTable it) CheckTable(it.Table, $"{where} > inline table");
                }
            }
            else if (b is TableBlock tb) CheckTable(tb, where);
        }
    }

    private static void CheckTable(TableBlock tb, string where)
    {
        foreach (var (r, c, cell) in tb.LogicalCells())
        {
            if (!ReferenceEquals(cell.Parent, tb))
                throw new InvalidOperationException($"{where}: cell({r},{c}).Parent is not its table");
            CheckContainer(cell.Blocks, cell, $"{where} > cell({r},{c})");
        }
    }

    // For a pointer into a cell the document cannot reach: WHICH slot of WHICH table. The two ways to get
    // there look identical from outside — a covered slot of a live table (the grid says nobody may stand
    // there) or any slot of a table that is no longer in the document (a stale object) — and they are
    // different defects.
    private static string CellDiagnosis(FlowDocument doc, Paragraph p)
    {
        if (p.Parent is not TableCell tc || tc.Parent is not TableBlock tb) return "";
        int sr = -1, sc = -1;
        for (int r = 0; r < tb.Rows && sr < 0; r++)
            for (int c = 0; c < tb.Columns; c++)
                if (ReferenceEquals(tb.Cells[r][c], tc)) { sr = r; sc = c; break; }
        bool covered = sr >= 0 && tb.IsCovered(sr, sc);
        bool tableLive = Tables(doc.Blocks).Contains(tb);
        return $"; slot ({sr},{sc}) of a {tb.Rows}x{tb.Columns} table, covered={covered}, table in document={tableLive}";
    }

    private static IEnumerable<TableBlock> Tables(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is TableBlock tb)
            {
                yield return tb;
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var t in Tables(cell.Blocks)) yield return t;
            }
            else if (b is Paragraph p)
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                    {
                        yield return it.Table;
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var t in Tables(cell.Blocks)) yield return t;
                    }
        }
    }

    // ---- walkers ----------------------------------------------------------------------------------

    private static IEnumerable<Paragraph> Paragraphs(IEnumerable<Block> blocks)
    {
        foreach (var b in blocks)
        {
            if (b is Paragraph p)
            {
                yield return p;
                foreach (var inl in p.Inlines)
                    if (inl is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells())
                            foreach (var q in Paragraphs(cell.Blocks)) yield return q;
            }
            else if (b is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells())
                    foreach (var q in Paragraphs(cell.Blocks)) yield return q;
        }
    }

    private static int Len(Paragraph p)
    {
        int n = 0;
        foreach (var inl in p.Inlines) n += inl is Run r ? (r.Text?.Length ?? 0) : 1;
        return n;
    }

    private static string Text(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private static string Trunc(string s) => s.Length <= 700 ? s : s[..700] + "…";
}
