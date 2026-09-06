using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Undo and redo as the editor exposes them: what counts as one step, where the caret lands,
/// and what wipes the history.
/// <para><see cref="UndoHistoryTests"/> covers the stacks themselves; this covers the wiring around
/// them, which is where the user-visible contract lives. Neither had any coverage — <c>Undo()</c> and
/// <c>Redo()</c> were not called by a single test in this repo, so grouping ("one Ctrl+Z should take
/// back the word I just typed, not one letter") and caret restoration were verified only by hand.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlUndoTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    // Build the document first, then assign it — the Document setter is what wires Parent and resets the
    // caret. (A document assembled after assignment has no Parent chain, which has produced test failures
    // that looked exactly like product defects.)
    private static RichEditor NewEditor(params string[] paragraphs)
    {
        var doc = new FlowDocument();
        foreach (var t in paragraphs) doc.Blocks.Add(P(t));
        return new RichEditor { Document = doc };
    }

    private static void SetCaret(RichEditor ed, Paragraph p, int offset = 0)
    {
        var tp = new TextPointer(p, offset);
        T.GetField("_caret", NP)!.SetValue(ed, tp);
        T.GetField("_selStart", NP)!.SetValue(ed, tp);
        T.GetField("_selEnd", NP)!.SetValue(ed, tp);
    }

    private static Paragraph? CaretParagraph(RichEditor ed)
        => ((TextPointer)T.GetField("_caret", NP)!.GetValue(ed)!).Paragraph;

    private static string TextOf(Paragraph? p)
        => p == null ? "<null>" : string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // ---- what counts as one step -------------------------------------------------------------------

    // Typing is coalesced: consecutive single characters share an undo group, so one Ctrl+Z takes back
    // the run of typing rather than one letter. This is the behaviour a user feels most directly, and
    // the only thing enforcing it is a string key compared inside PushUndo.
    [Fact]
    public void ConsecutiveTypedCharacters_CollapseIntoOneStep()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);

            ed.InsertText("a");
            ed.InsertText("b");
            ed.InsertText("c");
            Assert.Equal("startabc", ed.GetPlainText());

            ed.Undo();

            Assert.Equal("start", ed.GetPlainText());
            Assert.False(ed.CanUndo); // one group, not three
        });
    }

    // A paste (or any multi-character insert) is its own group — it is one user action, and it must not
    // be swallowed into the typing before it.
    [Fact]
    public void AMultiCharacterInsert_StartsItsOwnStep()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);

            ed.InsertText("a");
            ed.InsertText("b");
            ed.InsertText("XY");
            Assert.Equal("startabXY", ed.GetPlainText());

            ed.Undo();
            Assert.Equal("startab", ed.GetPlainText()); // the insert, without the typing before it

            ed.Undo();
            Assert.Equal("start", ed.GetPlainText());
            Assert.False(ed.CanUndo);
        });
    }

    [Fact]
    public void Redo_ReappliesWhatUndoTookBack()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);
            ed.InsertText("XY");

            ed.Undo();
            Assert.Equal("start", ed.GetPlainText());
            Assert.True(ed.CanRedo);

            ed.Redo();

            Assert.Equal("startXY", ed.GetPlainText());
            Assert.False(ed.CanRedo);
        });
    }

    [Fact]
    public void EditingAfterAnUndo_DropsTheRedo()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);
            ed.InsertText("XY");
            ed.Undo();
            Assert.True(ed.CanRedo);

            ed.InsertText("ZZ");

            Assert.False(ed.CanRedo);
            Assert.Equal("startZZ", ed.GetPlainText());
        });
    }

    // ---- where the caret lands ----------------------------------------------------------------------

    // Undo swaps in a CLONE of the document, so the caret cannot be restored by reference — it is
    // restored by the paragraph's number in document order. That numbering used to stop at each cell's
    // first paragraph, which dropped a caret edited deeper at the very start of the document. The
    // manager carries the fix; nothing here checked that the control still gets it right end to end.
    [Fact]
    public void Undo_PutsTheCaretBackInTheParagraphThatWasEdited_EvenInsideACell()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(P("top"));
            var table = new TableBlock(1, 1);
            table.Cells[0][0].Blocks.Clear();
            table.Cells[0][0].Blocks.Add(P("cell-first"));
            table.Cells[0][0].Blocks.Add(P("cell-second"));
            doc.Blocks.Add(table);
            doc.Blocks.Add(P("last"));

            var ed = new RichEditor { Document = doc };
            var target = table.Cells[0][0].Blocks.OfType<Paragraph>().ElementAt(1);
            SetCaret(ed, target, 0);

            ed.InsertText("Z");
            Assert.Contains("Zcell-second", ed.GetPlainText()); // the edit really landed there

            ed.Undo();

            Assert.DoesNotContain("Zcell-second", ed.GetPlainText());
            // Not "the caret is somewhere valid": it has to be the restored copy of the paragraph that
            // was edited. Landing on "top" is exactly the old defect, and it is a valid pointer.
            Assert.Equal("cell-second", TextOf(CaretParagraph(ed)));
        });
    }

    // ---- what wipes the history ---------------------------------------------------------------------

    // Opening a file resets the history. Otherwise Ctrl+Z would walk backwards out of the document just
    // opened and into the previous one — the reason LoadDocument exists as a shared path at all.
    [Fact]
    public void LoadingADocument_ForgetsTheHistoryOfThePreviousOne()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);
            ed.InsertText("XY");
            Assert.True(ed.CanUndo);

            ed.LoadRtf(@"{\rtf1\ansi a fresh file\par}");

            Assert.False(ed.CanUndo);
            Assert.False(ed.CanRedo);
            ed.Undo(); // and a stray Ctrl+Z must not resurrect the old document
            Assert.Contains("a fresh file", ed.GetPlainText());
        });
    }

    [Fact]
    public void Clear_ForgetsTheHistoryToo()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);
            ed.InsertText("XY");

            ed.Clear();

            Assert.False(ed.CanUndo);
            ed.Undo();
            Assert.Equal("", ed.GetPlainText().Trim());
        });
    }

    // A rejected load must leave the history it did not replace. LoadRtf keeps the open document when the
    // input is damaged; keeping the document but dropping its undo history would be a half-done rescue.
    [Fact]
    public void ARejectedLoad_KeepsTheHistoryOfWhatIsStillOpen()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);
            ed.InsertText("XY");

            ed.LoadRtf(@"{\rtf1\ansi truncated"); // unclosed group = damaged

            Assert.Equal("startXY", ed.GetPlainText());
            Assert.True(ed.CanUndo);
            ed.Undo();
            Assert.Equal("start", ed.GetPlainText());
        });
    }

    // ---- nothing to undo ----------------------------------------------------------------------------

    [Fact]
    public void UndoAndRedo_WithNothingRecorded_DoNothing()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("untouched");

            ed.Undo();
            ed.Redo();

            Assert.Equal("untouched", ed.GetPlainText());
            Assert.False(ed.CanUndo);
            Assert.False(ed.CanRedo);
        });
    }

    // A read-only editor refuses the edit, so there must be no checkpoint either — a history of edits
    // that never happened would make Ctrl+Z change the document a viewer is not allowed to change.
    [Fact]
    public void AReadOnlyEditor_RecordsNothingToUndo()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("start");
            SetCaret(ed, ed.Document!.Blocks.OfType<Paragraph>().First(), 5);
            ed.IsReadOnly = true;

            ed.InsertText("XY");

            Assert.Equal("start", ed.GetPlainText());
            Assert.False(ed.CanUndo);
        });
    }
}
