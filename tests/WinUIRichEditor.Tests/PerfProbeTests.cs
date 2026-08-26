using System;
using System.Diagnostics;
using System.Text;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>A measurement harness, not an assertion suite. Skipped unless <c>RICHEDITOR_PERF=1</c>
/// (same idiom as <c>RICHEDITOR_FUZZ_SEEDS</c>): timings on a live desktop are not a pass/fail signal,
/// but "정독이 아니라 측정" is this repo's rule for perf work, so the recipe lives here rather than in a
/// throwaway scratch file.
/// <para>Run: <c>$env:RICHEDITOR_PERF=1; dotnet test tests\WinUIRichEditor.Tests -v n</c></para></summary>
[Collection(UiTests.Collection)]
public class PerfProbeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("RICHEDITOR_PERF") == "1";

    // A document big enough that O(document) work per keystroke is visible: mixed formatting, a few
    // lists (the list path builds extra text), and a table.
    private static FlowDocument BigDocument(int paragraphs)
    {
        var doc = new FlowDocument();
        var rnd = new Random(1234);
        for (int i = 0; i < paragraphs; i++)
        {
            var p = new Paragraph();
            int runs = 1 + rnd.Next(3);
            for (int r = 0; r < runs; r++)
            {
                var sb = new StringBuilder();
                int words = 4 + rnd.Next(10);
                for (int w = 0; w < words; w++) sb.Append("word").Append(rnd.Next(1000)).Append(' ');
                // Real documents carry colour, links and decorations; a probe of plain black text would
                // not exercise the drawing-only attributes that measurement now skips.
                p.Inlines.Add(new Run
                {
                    Text = sb.ToString(),
                    FontFamily = (r % 3 == 0) ? "Segoe UI" : null,
                    FontSize = 10,
                    FontWeight = (r % 4 == 0) ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
                    Foreground = (i % 3 == 0) ? Windows.UI.Color.FromArgb(255, 40, 40, 160) : null,
                    NavigateUri = (i % 11 == 0 && r == 0) ? "https://example.com/some/page" : null,
                    TextDecorations = (i % 5 == 0) ? TextDecorationFlags.Underline
                                    : (i % 9 == 0) ? TextDecorationFlags.Strikethrough
                                    : TextDecorationFlags.None,
                });
            }
            if (i % 7 == 0) { p.ListType = ListKind.Bullet; p.ListLevel = i % 3; }
            if (i % 23 == 0) p.HeadingLevel = 2;
            doc.Blocks.Add(p);
        }
        var tb = new TableBlock(4, 4);
        foreach (var row in tb.Cells)
            foreach (var cell in row)
            {
                cell.Blocks.Clear();
                cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "cell text here", FontSize = 10 } } } );
            }
        doc.Blocks.Insert(paragraphs / 2, tb);
        return doc;
    }

    // Best-of-3 rounds. A live desktop makes single runs noisy enough to invent or hide a 20% change;
    // the minimum is the round least disturbed by whatever else the machine was doing.
    private static double Ms(Action a, int iterations)
    {
        double best = double.MaxValue;
        for (int round = 0; round < 3; round++)
        {
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) a();
            sw.Stop();
            best = Math.Min(best, sw.Elapsed.TotalMilliseconds / iterations);
        }
        return best;
    }

    [Fact]
    public void Probe()
    {
        if (!Enabled) return;

        var lines = new System.Collections.Generic.List<string>();
        foreach (int n in new[] { 200, 2000 })
        {
            var report = UiThread.Run(() =>
            {
                var sb = new StringBuilder();
                var doc = BigDocument(n);

                var ed = new RichEditor();
                double assign = Ms(() => { ed.Document = null; ed.Document = doc; }, 3);

                // Warm: one status + one page render so lazy caches exist.
                ed.Document = doc;
                _ = ed.GetStatus();
                using (var warm = ed.RenderPrintPage(0)) { }

                // Per-keystroke: what the editor does for one typed character, plus the status read a
                // host status bar performs on every StatusChanged.
                double type = Ms(() => { ed.InsertText("x"); _ = ed.GetStatus(); }, 200);
                double typeOnly = Ms(() => ed.InsertText("y"), 200);
                double statusOnly = Ms(() => _ = ed.GetStatus(), 200);
                double pageCount = Ms(() => _ = ed.GetPrintPageCount(), 20);

                // Draw: one page's worth of the render walk (the same DrawContentWalk the canvas runs).
                double draw = Ms(() => { using var rt = ed.RenderPrintPage(0); }, 20);

                long managed = GC.GetTotalMemory(true);

                sb.Append($"paras={n}  assign={assign:F2}ms  type+status={type:F3}ms  type={typeOnly:F3}ms  status={statusOnly:F3}ms  pageCount={pageCount:F2}ms  renderPage={draw:F2}ms  managed={managed / 1024 / 1024}MB");
                return sb.ToString();
            });

            lines.Add(report);
        }
        // The heavy layout cache should stay viewport-sized however long the document is (retention of
        // dropped paragraphs is pinned separately, by LayoutCostTests).
        lines.Add(UiThread.Run(() =>
        {
            var doc = BigDocument(300);
            var ed = new RichEditor { Document = doc };
            _ = ed.GetStatus();
            using (var w = ed.RenderPrintPage(0)) { }
            return $"300-paragraph doc: blocks={doc.Blocks.Count}, heavy layout cache holds {ed.LayoutCacheCount} entries";
        }));

        lines.AddRange(CloneCostReport());

        string ws = $"process working set = {Environment.WorkingSet / 1024 / 1024}MB";

        lines.Add(ws);
        // Also to a file: the test runner's captured output is easy to lose in build noise.
        System.IO.File.WriteAllLines(
            Environment.GetEnvironmentVariable("RICHEDITOR_PERF_REPORT") ?? "perf-report.txt", lines);
    }

    // ---- FlowDocument.Clone: the undo-checkpoint path ---------------------------------------------
    //
    // Kept because this is where an audit claim was settled with a number instead of an argument, and
    // because the shape is easy to reintroduce. TableBlock.Clone/Extract once asked the public
    // constructor for a full rows×cols grid — a TableCell holding a Paragraph holding a Run, per slot —
    // and discarded every bit of it. Clone runs at every undo checkpoint (UndoManager.PushState).
    //
    // BYTES ARE THE SIGNAL HERE, NOT MILLISECONDS. Allocation is deterministic and comparable across
    // runs; the timings are not — when this was measured, the unchanged no-table control drifted 16%
    // between two runs minutes apart, which is the same "the machine got slower, not the code" trap the
    // roadmap records. Compare the KB column against a previous report; treat ms as texture only.
    //
    // Measured when the waste was removed (Release):
    //   prose 2000, no table            610.4 ->  610.4 KB   (untouched: the control)
    //   prose 2000 + one 4x4 table      630.8 ->  623.1 KB   (~1%: why general probes never saw it)
    //   prose 200 + 20x (10x10)        2605.8 -> 1576.1 KB   (-40%)
    //   one 50x20 table                1245.6 ->  739.0 KB   (-41%)
    // Each drop matched the separately-priced discarded construction to within ~1%.
    private static System.Collections.Generic.List<string> CloneCostReport()
    {
        var lines = new System.Collections.Generic.List<string>();

        FlowDocument Prose(int paragraphs)
        {
            var doc = new FlowDocument();
            var rnd = new Random(1234);
            for (int i = 0; i < paragraphs; i++)
            {
                var p = new Paragraph();
                var sb = new StringBuilder();
                for (int w = 0; w < 10; w++) sb.Append("word").Append(rnd.Next(1000)).Append(' ');
                p.Inlines.Add(new Run { Text = sb.ToString(), FontSize = 10 });
                doc.Blocks.Add(p);
            }
            return doc;
        }

        TableBlock Table(int rows, int cols)
        {
            var tb = new TableBlock(rows, cols);
            foreach (var row in tb.Cells)
                foreach (var cell in row)
                {
                    cell.Blocks.Clear();
                    cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "c", FontSize = 10 } } });
                }
            return tb;
        }

        void Row(string name, FlowDocument doc)
        {
            const int iterations = 20;
            doc.Clone();                                   // warm: JIT + first touch
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            long before = GC.GetAllocatedBytesForCurrentThread();
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < iterations; i++) doc.Clone();
            sw.Stop();
            long bytes = (GC.GetAllocatedBytesForCurrentThread() - before) / iterations;
            lines.Add($"clone  {name,-30} {sw.Elapsed.TotalMilliseconds / iterations,7:F3}ms  {bytes / 1024.0,9:F1}KB");
        }

        Row("prose 2000, no table", Prose(2000));

        var withSmall = Prose(2000);
        withSmall.Blocks.Add(Table(4, 4));
        Row("prose 2000 + 4x4", withSmall);

        var heavy = Prose(200);
        for (int t = 0; t < 20; t++) heavy.Blocks.Add(Table(10, 10));
        Row("prose 200 + 20x(10x10)", heavy);

        var big = new FlowDocument();
        big.Blocks.Add(Table(50, 20));
        Row("one 50x20 table", big);

        return lines;
    }
}
