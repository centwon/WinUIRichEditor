using System;
using System.Collections.Generic;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;


namespace WinUIRichEditor.Tests;

/// <summary>Where <see cref="UndoManager"/>'s per-element constant comes from: what a checkpoint
/// actually retains, measured.
/// <para>This answered a question the code had been guessing at. The budget used to charge 2 bytes per
/// character, and the measurement below shows text length does not cost a checkpoint ANYTHING — Clone
/// shares the strings — while the element count predicts retention within a few percent across every
/// document shape tried. Both repos had the same wrong model.</para>
/// <para>A measurement harness, not an assertion suite: skipped unless <c>RICHEDITOR_PERF=1</c>, the
/// same gate <see cref="PerfProbeTests"/> uses, because it allocates hundreds of MB and a GC number on
/// a shared machine is not something to fail a build on. The deterministic half of the contract —
/// element count moves the estimate, text length does not — is guarded in <c>EnhancementTests</c>.</para>
/// <para>Run: <c>$env:RICHEDITOR_PERF=1; dotnet test tests\WinUIRichEditor.Tests --filter UndoBudgetProbe
/// --logger "console;verbosity=detailed"</c></para></summary>
public class UndoBudgetProbeTests
{
    private static bool Enabled => Environment.GetEnvironmentVariable("RICHEDITOR_PERF") == "1";

    private readonly ITestOutputHelper _out;
    public UndoBudgetProbeTests(ITestOutputHelper output) => _out = output;

    private static FlowDocument Doc(int paragraphs, int charsPerParagraph, int runsPerParagraph)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < paragraphs; i++)
        {
            var p = new Paragraph();
            for (int r = 0; r < runsPerParagraph; r++)
                p.Inlines.Add(new Run { Text = new string('x', charsPerParagraph) });
            doc.Blocks.Add(p);
        }
        return doc;
    }

    private static int ElementCount(FlowDocument doc)
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
                                    Walk(cell.Blocks);
                }
                else if (b is TableBlock tb)
                {
                    foreach (var row in tb.Cells)
                        foreach (var cell in row) { n++; Walk(cell.Blocks); }
                }
            }
        }
        Walk(doc.Blocks);
        return n;
    }

    private static long Heap()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        return GC.GetTotalMemory(forceFullCollection: true);
    }

    [Theory]
    [InlineData(3000, 60, 1)]
    [InlineData(300, 4000, 1)]
    [InlineData(3000, 2, 1)]     // same elements as the first, almost no text
    [InlineData(3000, 60, 5)]    // same paragraphs, five times the inlines
    [InlineData(20000, 60, 1)]
    public void WhatACheckpointRetains(int paragraphs, int chars, int runs)
    {
        if (!Enabled) return;
        var doc = Doc(paragraphs, chars, runs);
        int elements = ElementCount(doc);
        long estimate = UndoManager.EstimateBytes(doc);

        long before = Heap();
        var undo = new UndoManager();
        var caret = new TextPointer((Paragraph)doc.Blocks[0], 0);
        // Ten, not fifty: the byte budget would trim a fifty-deep history and the point here is to
        // measure what ONE checkpoint costs, before any policy applies.
        for (int i = 0; i < 10; i++) undo.PushState(doc, caret);
        long after = Heap();
        GC.KeepAlive(undo);

        double per = (after - before) / 10.0;
        _out.WriteLine(
            $"{paragraphs}p x {chars}ch x {runs}run | elements {elements,7} | " +
            $"EstimateBytes {estimate / 1024.0 / 1024.0,6:F2} MB | retained/checkpoint {per / 1024.0,8:F0} KB | " +
            $"bytes/element {per / elements,6:F0}");
    }
}
