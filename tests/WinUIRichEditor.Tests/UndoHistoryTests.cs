using System;
using System.Collections.Generic;
using System.Linq;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The undo history itself: what it restores, how deep it goes, and what bounds it.
/// <para>Why this file exists: <see cref="UndoManager"/> had no coverage at all. The suite called
/// <c>EstimateBytes</c> and nothing else — <c>Undo</c>, <c>Redo</c> and <c>PushState</c> were never
/// invoked by any test in the repo, at either layer. That is the whole of the editor's history model,
/// and it is a state machine: two stacks, a byte budget, and a caret identified by its number in
/// document paragraph order rather than by reference (the snapshot is a clone, so a reference could
/// not survive).</para>
/// <para>These run headless. <see cref="UndoManager"/> takes a document and a pointer and nothing
/// else, so the WinUI runtime is not needed here — only <see cref="ControlUndoTests"/> needs it.</para>
/// </summary>
public class UndoHistoryTests
{
    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static string TextOf(Paragraph? p)
        => p == null ? "<null>" : string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private static FlowDocument Doc(params string[] paragraphs)
    {
        var d = new FlowDocument();
        foreach (var t in paragraphs) d.Blocks.Add(P(t));
        return d;
    }

    private static TextPointer CaretOn(FlowDocument doc, string paragraphText, int offset = 0)
    {
        var p = AllParagraphs(doc).FirstOrDefault(x => TextOf(x) == paragraphText);
        Assert.NotNull(p); // a typo in the fixture would otherwise silently test the fallback path
        return new TextPointer(p!, offset);
    }

    // Document order at every depth — the same walk the manager numbers by (BlockWalk owns it).
    private static List<Paragraph> AllParagraphs(FlowDocument doc)
    {
        var list = new List<Paragraph>();
        void Walk(IEnumerable<Block> blocks)
        {
            foreach (var b in blocks)
            {
                if (b is Paragraph p)
                {
                    list.Add(p);
                    foreach (var it in p.Inlines.OfType<InlineTable>())
                        foreach (var row in it.Table.Cells)
                            foreach (var cell in row)
                                Walk(cell.Blocks);
                }
                else if (b is TableBlock tb)
                {
                    foreach (var row in tb.Cells)
                        foreach (var cell in row)
                            Walk(cell.Blocks);
                }
            }
        }
        Walk(doc.Blocks);
        return list;
    }

    // ---- the round trip ----------------------------------------------------------------------------

    // The base contract, and the one nothing asserted: an edit can be taken back, and taking it back can
    // itself be taken back. The manager never mutates the live document — it hands back a snapshot and
    // the control swaps it in — so "restores" here means the returned state carries the old content.
    [Fact]
    public void UndoThenRedo_RoundTripsTheDocument()
    {
        var doc = Doc("before");
        var undo = new UndoManager();

        undo.PushState(doc, CaretOn(doc, "before"));
        Assert.True(undo.CanUndo);
        Assert.False(undo.CanRedo);

        var edited = Doc("after"); // stands in for the edit the control would have applied in place

        var undone = undo.Undo(edited, CaretOn(edited, "after"));
        Assert.NotNull(undone);
        Assert.Equal("before", TextOf(AllParagraphs(undone!.Value.Document).Single()));
        Assert.True(undo.CanRedo);

        var redone = undo.Redo(undone.Value.Document, CaretOn(undone.Value.Document, "before"));
        Assert.NotNull(redone);
        Assert.Equal("after", TextOf(AllParagraphs(redone!.Value.Document).Single()));
        Assert.False(undo.CanRedo);
        Assert.True(undo.CanUndo);
    }

    // Editing after an undo abandons the branch that was undone. Without this a redo would resurrect
    // content the user has since replaced.
    [Fact]
    public void ANewEdit_DropsTheRedoBranch()
    {
        var doc = Doc("v1");
        var undo = new UndoManager();
        undo.PushState(doc, CaretOn(doc, "v1"));

        var v2 = Doc("v2");
        undo.Undo(v2, CaretOn(v2, "v2"));
        Assert.True(undo.CanRedo);

        undo.PushState(v2, CaretOn(v2, "v2"));

        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void WithAnEmptyHistory_UndoAndRedoReportNothing()
    {
        var doc = Doc("only");
        var undo = new UndoManager();

        Assert.Null(undo.Undo(doc, CaretOn(doc, "only")));
        Assert.Null(undo.Redo(doc, CaretOn(doc, "only")));
        Assert.False(undo.CanUndo);
        Assert.False(undo.CanRedo);
    }

    [Fact]
    public void Clear_ForgetsBothDirections()
    {
        var doc = Doc("a");
        var undo = new UndoManager();
        undo.PushState(doc, CaretOn(doc, "a"));
        var edited = Doc("b");
        undo.Undo(edited, CaretOn(edited, "b"));
        Assert.True(undo.CanRedo);

        undo.Clear();

        Assert.False(undo.CanUndo);
        Assert.False(undo.CanRedo);
    }

    // ---- the caret survives at every depth ----------------------------------------------------------

    // The manager's own comment records the defect this pins: an older walk saw only each cell's FIRST
    // paragraph, so a caret anywhere deeper was never numbered and undo dropped it at the start of the
    // document. Upstream fixed the same thing and covers it with five tests; this repo covered it with
    // none. Every paragraph of a mixed document is checked, because the failure is per-position — the
    // top-level ones kept working the whole time, which is why it went unnoticed.
    [Theory]
    [InlineData("top")]
    [InlineData("cell-first")]
    [InlineData("cell-second")]   // the position the old walk could not number
    [InlineData("nested")]
    [InlineData("inline-cell")]
    [InlineData("last")]
    public void TheCaretIndex_ResolvesBackToTheSameParagraph_AtEveryDepth(string where)
    {
        var doc = MixedDocument();
        var undo = new UndoManager();

        undo.PushState(doc, CaretOn(doc, where, offset: 1));

        // Push snapshots the CLONE, so what comes back is a different Paragraph instance. Identify it
        // the only way that survives a clone: by its text at that position in document order.
        var edited = Doc("replaced");
        var state = undo.Undo(edited, CaretOn(edited, "replaced"))!.Value;
        var restored = undo.GetPointerFromGlobalIndex(state.Document, state.CaretGlobalIndex);

        Assert.Equal(where, TextOf(restored.Paragraph));
        Assert.Equal(1, state.CaretOffset);
    }

    // A structure with one paragraph at each depth the walk has to number. The texts are the fixture's
    // identity, so they must stay unique.
    private static FlowDocument MixedDocument()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(P("top"));

        var table = new TableBlock(1, 2);
        table.Cells[0][0].Blocks.Clear();
        table.Cells[0][0].Blocks.Add(P("cell-first"));
        table.Cells[0][0].Blocks.Add(P("cell-second"));

        var nested = new TableBlock(1, 1);
        nested.Cells[0][0].Blocks.Clear();
        nested.Cells[0][0].Blocks.Add(P("nested"));
        table.Cells[0][1].Blocks.Clear();
        table.Cells[0][1].Blocks.Add(nested);
        doc.Blocks.Add(table);

        var hostPara = new Paragraph();
        var inline = new InlineTable { Table = new TableBlock(1, 1) };
        inline.Table.Cells[0][0].Blocks.Clear();
        inline.Table.Cells[0][0].Blocks.Add(P("inline-cell"));
        hostPara.Inlines.Add(inline);
        doc.Blocks.Add(hostPara);

        doc.Blocks.Add(P("last"));
        return doc;
    }

    // An index that no longer exists (the document shrank under the history) must land somewhere real
    // rather than throw or hand back a pointer into nothing.
    [Fact]
    public void AnOutOfRangeCaretIndex_FallsBackToTheLastParagraph()
    {
        var doc = Doc("one", "two");

        var landed = new UndoManager().GetPointerFromGlobalIndex(doc, 99);

        Assert.Equal("two", TextOf(landed.Paragraph));
    }

    [Fact]
    public void AnEmptyDocument_HasNoParagraphToRestoreTo()
    {
        var landed = new UndoManager().GetPointerFromGlobalIndex(new FlowDocument(), 0);

        Assert.Null(landed.Paragraph);
    }

    // ---- the history is bounded -----------------------------------------------------------------

    // Fifty steps is the count bound. Snapshots are full document clones, so an unbounded stack would
    // hold every version of the document the session ever had.
    [Fact]
    public void TheHistory_KeepsAtMostFiftySteps()
    {
        var undo = new UndoManager();
        var doc = Doc("x");
        for (int i = 0; i < 60; i++) undo.PushState(doc, CaretOn(doc, "x"));

        int depth = 0;
        while (undo.Undo(doc, CaretOn(doc, "x")) != null) depth++;

        Assert.Equal(50, depth);
    }

    // The byte budget, and the floor that keeps undo useful under it. Snapshots of ~24MB blow the 64MB
    // budget after two, but the manager must still keep three steps — a large document that trimmed to
    // one would leave the user with an undo key that undoes almost nothing.
    //
    // The text is one big string and Clone shares the reference, so this costs 24MB once, not per step.
    //
    // ⚠ SIX pushes, not four, and the number is load-bearing. Trim returns early while the stack is at
    // or below the floor, so a four-push run lands on three steps whether or not the budget loop honours
    // the floor at all — the first version of this test asserted the right number for the wrong reason
    // and stayed green with the floor deleted. Six pushes make the loop the only thing keeping the third
    // step (without the floor it trims to two).
    [Fact]
    public void TheByteBudget_TrimsTheHistory_ButNeverBelowThreeSteps()
    {
        var big = Doc(new string('x', 12_000_000)); // ~24MB by EstimateBytes' count
        Assert.True(UndoManager.EstimateBytes(big) > 20 * 1024 * 1024);

        var undo = new UndoManager();
        var caret = CaretOn(big, TextOf(AllParagraphs(big).Single()));
        for (int i = 0; i < 6; i++) undo.PushState(big, caret);

        int depth = 0;
        while (undo.Undo(big, caret) != null) depth++;

        Assert.Equal(3, depth);
    }

    // The budget must not trim a small document at all — the count bound is what applies there.
    [Fact]
    public void ASmallDocument_KeepsItsFullHistory()
    {
        var undo = new UndoManager();
        var doc = Doc("small");
        for (int i = 0; i < 10; i++) undo.PushState(doc, CaretOn(doc, "small"));

        int depth = 0;
        while (undo.Undo(doc, CaretOn(doc, "small")) != null) depth++;

        Assert.Equal(10, depth);
    }

    // A caret with no paragraph is what an editor with no document has. Snapshotting it would push a
    // state nothing could restore to.
    [Fact]
    public void APointerWithoutAParagraph_IsNotSnapshotted()
    {
        var undo = new UndoManager();

        undo.PushState(Doc("a"), new TextPointer(null, 0));
        undo.PushState(null, new TextPointer(P("a"), 0));

        Assert.False(undo.CanUndo);
    }
}
