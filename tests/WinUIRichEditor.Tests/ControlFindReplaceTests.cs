using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Find and replace.
/// <para>Six public methods — <c>FindNext</c>, <c>FindPrev</c>, <c>FindAgain</c>, <c>ReplaceNext</c>,
/// <c>ReplaceAll</c>, <c>GetFindMatchPosition</c> — and until this file not one of them was called by a
/// test in this repo. The 2026-08-26 audit judged two find/replace items by READING the code and
/// rejected both; one of the rejections turned on wrap-around semantics that nothing pinned, and the
/// "optimization" it rejected would have been a silent regression. Those semantics are pinned here.</para>
/// <para>Upstream's coverage of the same surface is thin but real (highlight/current-match, reaching an
/// inline table cell, ReplaceAll at depth, the feature-flag gate); those cases are mirrored so the two
/// peers can be compared on the same claims.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlFindReplaceTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static RichEditor NewEditor(params string[] paragraphs)
    {
        var doc = new FlowDocument();
        foreach (var t in paragraphs) doc.Blocks.Add(P(t));
        return new RichEditor { Document = doc };
    }

    private static TextPointer Sel(RichEditor ed, string field)
        => (TextPointer)T.GetField(field, NP)!.GetValue(ed)!;

    private static string SelectedText(RichEditor ed)
        => new TextRange(Sel(ed, "_selStart"), Sel(ed, "_selEnd")).GetText();

    private static string TextOf(Paragraph? p)
        => p == null ? "<null>" : string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // ---- what a find selects ------------------------------------------------------------------------

    [Fact]
    public void FindNext_SelectsTheFirstMatchAndReportsItAsTheCurrentOne()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("alpha beta beta");

            Assert.True(ed.FindNext("beta", matchCase: false));

            Assert.Equal("beta", SelectedText(ed));
            Assert.Equal(6, Sel(ed, "_selStart").Offset); // the FIRST one, not just any
            var (current, total) = ed.GetFindMatchPosition();
            Assert.Equal(2, total);
            Assert.Equal(1, current);
        });
    }

    // Repeating a find must advance, which is what anchoring the search at the selection END buys. An
    // anchor at the selection start would find the same match forever.
    [Fact]
    public void FindNext_Repeated_AdvancesToTheNextMatch()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("beta and beta");

            Assert.True(ed.FindNext("beta", matchCase: false));
            Assert.Equal(0, Sel(ed, "_selStart").Offset);

            Assert.True(ed.FindNext("beta", matchCase: false));

            Assert.Equal(9, Sel(ed, "_selStart").Offset);
            Assert.Equal(2, ed.GetFindMatchPosition().current);
        });
    }

    // The wrap must re-scan the paragraph the caret is IN, from its beginning — the match it wraps to is
    // usually behind the caret in that same paragraph. The audit report proposed skipping that paragraph
    // on the wrap pass (`pi < fromPi`); this is the case that would have broken, and with a single
    // paragraph there is nothing else for the wrap to find.
    [Fact]
    public void FindNext_WrapsBackToAMatchBehindTheCaret_InTheSameParagraph()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("only one hit here");

            Assert.True(ed.FindNext("only", matchCase: false)); // selection now sits past the match
            Assert.Equal(4, Sel(ed, "_selEnd").Offset);

            Assert.True(ed.FindNext("only", matchCase: false)); // nothing ahead: must wrap and re-find it

            Assert.Equal(0, Sel(ed, "_selStart").Offset);
            Assert.Equal((1, 1), ed.GetFindMatchPosition());
        });
    }

    [Fact]
    public void FindPrev_WalksBackwardsAndWrapsToTheLastMatch()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("hit one", "middle", "hit two");

            Assert.True(ed.FindPrev("hit", matchCase: false)); // from the document start: wraps to the last
            Assert.Equal("hit two", TextOf(Sel(ed, "_selStart").Paragraph));

            Assert.True(ed.FindPrev("hit", matchCase: false));

            Assert.Equal("hit one", TextOf(Sel(ed, "_selStart").Paragraph));
        });
    }

    [Fact]
    public void MatchCase_IsHonoured()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("Beta beta");

            Assert.True(ed.FindNext("beta", matchCase: true));
            Assert.Equal(5, Sel(ed, "_selStart").Offset); // skipped the capitalized one
            Assert.False(ed.FindNext("BETA", matchCase: true));
        });
    }

    // F3 with no previous query is not a search. FindAgain is the only entry point that can be reached
    // with no query at all, and it has to say so rather than search for "".
    [Fact]
    public void FindAgain_WithoutAPreviousQuery_FindsNothing()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("anything");

            Assert.False(ed.FindAgain(backwards: false));

            Assert.True(ed.FindNext("any", matchCase: false));
            Assert.Equal("any", ed.LastFindQuery);
            Assert.True(ed.FindAgain(backwards: false)); // wraps back onto the same single match
        });
    }

    // Find has to reach text at any depth — a match inside a cell is a match. (Mirrors upstream's
    // FindNext_ReachesTextInsideAnInlineTableCell.)
    [Fact]
    public void FindNext_ReachesTextInsideATableCell()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(P("outside"));
            var table = new TableBlock(1, 1);
            table.Cells[0][0].Blocks.Clear();
            table.Cells[0][0].Blocks.Add(P("buried treasure"));
            doc.Blocks.Add(table);
            var ed = new RichEditor { Document = doc };

            Assert.True(ed.FindNext("treasure", matchCase: false));

            Assert.Equal("buried treasure", TextOf(Sel(ed, "_selStart").Paragraph));
            Assert.Equal("treasure", SelectedText(ed));
        });
    }

    // ---- replacing ----------------------------------------------------------------------------------

    [Fact]
    public void ReplaceAll_ReplacesEveryOccurrenceAndReportsHowMany()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("one cat", "two cats here", "no felines");

            int count = ed.ReplaceAll("cat", "dog", matchCase: false);

            Assert.Equal(2, count);
            Assert.Contains("one dog", ed.GetPlainText());
            Assert.Contains("two dogs here", ed.GetPlainText());
        });
    }

    // Depth again, this time for the write path. (Mirrors upstream's ReplaceAll_ReachesEveryDepth.)
    [Fact]
    public void ReplaceAll_ReachesInsideCells()
    {
        UiThread.Run(() =>
        {
            var doc = new FlowDocument();
            doc.Blocks.Add(P("x0 outside"));
            var table = new TableBlock(1, 1);
            table.Cells[0][0].Blocks.Clear();
            var cellPara = P("x0 inside");
            table.Cells[0][0].Blocks.Add(cellPara);
            doc.Blocks.Add(table);
            var ed = new RichEditor { Document = doc };

            Assert.Equal(2, ed.ReplaceAll("x0", "x9", matchCase: true));

            Assert.Equal("x9 inside", TextOf(cellPara));
        });
    }

    // A replacement that CONTAINS the query is the shape that turns a replace-all into a hang: each
    // rewrite re-creates something the next scan can match. The loop survives it only because the search
    // resumes past what it just wrote. (The 1,000,000 cap would eventually stop a runaway, but with a
    // document grown past anything usable.)
    [Fact]
    public void ReplaceAll_WhenTheReplacementContainsTheQuery_StillTerminates()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("aaa");

            int count = ed.ReplaceAll("a", "aa", matchCase: true);

            Assert.Equal(3, count);
            Assert.Equal("aaaaaa", ed.GetPlainText());
        });
    }

    // One replace-all is one user action, so it is one checkpoint — not one per occurrence.
    [Fact]
    public void ReplaceAll_IsASingleUndoStep()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("cat", "cat", "cat");
            Assert.Equal(3, ed.ReplaceAll("cat", "dog", matchCase: false));

            ed.Undo();

            Assert.Equal("cat\r\ncat\r\ncat", ed.GetPlainText());
            Assert.False(ed.CanUndo);
        });
    }

    [Fact]
    public void ReplaceAll_WithNoMatch_ChangesNothing()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("nothing to see");

            Assert.Equal(0, ed.ReplaceAll("absent", "x", matchCase: false));

            Assert.Equal("nothing to see", ed.GetPlainText());
        });
    }

    // ReplaceNext replaces only what is actually selected, then moves on. Called with no selection it is
    // a find — that is what makes the find bar's "Replace" button safe to press first.
    [Fact]
    public void ReplaceNext_ReplacesTheSelectedMatchThenAdvances()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("cat and cat");

            Assert.True(ed.ReplaceNext("cat", "dog", matchCase: false)); // nothing selected yet: finds
            Assert.Equal("cat and cat", ed.GetPlainText());
            Assert.Equal(0, Sel(ed, "_selStart").Offset);

            Assert.True(ed.ReplaceNext("cat", "dog", matchCase: false)); // now the selection matches

            Assert.Equal("dog and cat", ed.GetPlainText());
            Assert.Equal(8, Sel(ed, "_selStart").Offset); // advanced to the remaining one
        });
    }

    // ---- the gates ----------------------------------------------------------------------------------

    // (Mirrors upstream's AllowFindReplaceFalse_DisablesFindReplace.)
    [Fact]
    public void AllowFindReplaceFalse_DisablesBothHalves()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("hello hello");
            ed.AllowFindReplace = false;

            Assert.False(ed.FindNext("hello", matchCase: false));
            Assert.False(ed.FindPrev("hello", matchCase: false));
            Assert.Equal(0, ed.ReplaceAll("hello", "x", matchCase: false));
            Assert.False(ed.ReplaceNext("hello", "x", matchCase: false));
            Assert.Equal("hello hello", ed.GetPlainText());
        });
    }

    // A viewer must still be able to FIND. Read-only gates the write half only — gating both would make
    // the find bar useless in exactly the mode people read long documents in.
    [Fact]
    public void AReadOnlyEditor_CanFindButNotReplace()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("cat and cat");
            ed.IsReadOnly = true;

            Assert.True(ed.FindNext("cat", matchCase: false));
            Assert.Equal(0, ed.ReplaceAll("cat", "dog", matchCase: false));
            Assert.False(ed.ReplaceNext("cat", "dog", matchCase: false));
            Assert.Equal("cat and cat", ed.GetPlainText());
        });
    }

    // An empty query is not a search at any entry point. The audit rejected a "빈 검색어가 글자 수만큼
    // 루프" finding on the grounds that the empty string is normalized to null before it can reach the
    // scan; this is that normalization, asserted instead of read.
    [Fact]
    public void AnEmptyQuery_IsNeverASearch()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("some text");

            Assert.False(ed.FindNext("", matchCase: false));
            Assert.Equal(0, ed.ReplaceAll("", "x", matchCase: false));

            ed.SetFindHighlight("", matchCase: false);
            Assert.Equal((0, 0), ed.GetFindMatchPosition());
            Assert.Equal("some text", ed.GetPlainText());
        });
    }

    // The n/m counter is driven by the highlight, not by the last find, so closing the find bar stops it
    // reporting. (Mirrors upstream's highlight test.)
    [Fact]
    public void TheMatchCounter_FollowsTheHighlight()
    {
        UiThread.Run(() =>
        {
            var ed = NewEditor("hello hello", "hello again");

            ed.SetFindHighlight("hello", matchCase: false);
            Assert.Equal(3, ed.GetFindMatchPosition().total);
            Assert.Equal(0, ed.GetFindMatchPosition().current); // nothing selected: on no match

            ed.ClearFindHighlight();

            Assert.Equal((0, 0), ed.GetFindMatchPosition());
        });
    }
}
