using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The formatting commands, one contract at a time.
/// <para>Until this file and <see cref="ControlCommandFuzzTests"/>, not one of the ~25 public formatting
/// commands on <see cref="RichEditor"/> was called by a test — the model fuzz styles runs directly and
/// never passes through them. The fuzz now proves they compose without breaking structure or history;
/// this pins what each one is supposed to DO, which a fuzz cannot know.</para>
/// <para>The selection-scope and undo-step groups are ported from upstream's
/// <c>ParagraphCommandScopeTests</c>; the list-toggle identity pair from upstream's
/// <c>DocumentInvariantFuzzTests</c>. The rest (caret-word targeting, pending caret styles, the format
/// painter, cell rectangles, the size ladder, read-only) had no test on either side.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlFormattingCommandTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);
    private static readonly Color Red = Microsoft.UI.ColorHelper.FromArgb(255, 255, 0, 0);

    // ---- plumbing ---------------------------------------------------------------------------------

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    // Build first, then assign: the Document setter wires Parent and normalizes (a document assembled
    // after assignment has no Parent chain — see ControlUndoTests).
    private static RichEditor Editor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        return new RichEditor { Document = doc };
    }

    private static void Caret(RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
    }

    // A drag from (a, ao) to (b, bo): the caret sits at the drag's end.
    private static void Select(RichEditor ed, Paragraph a, int ao, Paragraph b, int bo)
    {
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(a, ao));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(b, bo));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(b, bo));
    }

    private static Paragraph CaretParagraph(RichEditor ed)
        => ((TextPointer)T.GetField("_caret", NP)!.GetValue(ed)!).Paragraph!;

    private static void Call(RichEditor ed, string name, params object?[] args)
    {
        try { T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    // Undo swaps in a CLONE, so after it any Paragraph reference held by the test is stale: always
    // re-read from the editor's current document.
    private static List<Paragraph> TopParagraphs(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().ToList();

    private static string TextOf(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // The run covering the character AT offset (not the one ending there).
    private static Run RunAt(Paragraph p, int offset)
    {
        int pos = 0;
        foreach (var inl in p.Inlines)
        {
            int len = inl is Run r0 ? (r0.Text?.Length ?? 0) : 1;
            if (inl is Run r && offset >= pos && offset < pos + len) return r;
            pos += len;
        }
        throw new InvalidOperationException($"no run covers offset {offset} of '{TextOf(p)}'");
    }

    private static bool BoldAt(Paragraph p, int offset) => RunAt(p, offset).FontWeight.Weight >= 600;

    // ---- selection scope (ported: upstream ParagraphCommandScopeTests (5)) -------------------------

    private static (RichEditor ed, Paragraph a, Paragraph b, Paragraph c) ThreeSelected()
    {
        var a = P("one");
        var b = P("two");
        var c = P("three");
        var ed = Editor(a, b, c);
        Select(ed, a, 0, c, 5);
        return (ed, a, b, c);
    }

    [Fact]
    public void SetTextAlignment_AppliesToEverySelectedParagraph() => UiThread.Run(() =>
    {
        var (ed, a, b, c) = ThreeSelected();
        ed.SetTextAlignment(TextAlignment.Center);
        Assert.All(new[] { a, b, c }, p => Assert.Equal(TextAlignment.Center, p.TextAlignment));
    });

    [Fact]
    public void SetHeading_AppliesToEverySelectedParagraph() => UiThread.Run(() =>
    {
        var (ed, a, b, c) = ThreeSelected();
        ed.SetHeading(2);
        Assert.All(new[] { a, b, c }, p => Assert.Equal(2, p.HeadingLevel));
    });

    [Fact]
    public void SetLineSpacing_AppliesToEverySelectedParagraph() => UiThread.Run(() =>
    {
        var (ed, a, b, c) = ThreeSelected();
        ed.SetLineSpacing(1.5);
        Assert.All(new[] { a, b, c }, p => Assert.Equal(1.5, p.LineSpacing));
    });

    // Indent is a delta, so each paragraph moves from its own starting value.
    [Fact]
    public void Indent_ShiftsEverySelectedParagraphByTheDelta() => UiThread.Run(() =>
    {
        var (ed, a, b, c) = ThreeSelected();
        b.Indent = 40;
        ed.Indent(20);
        Assert.Equal(20, a.Indent);
        Assert.Equal(60, b.Indent);
        Assert.Equal(20, c.Indent);
    });

    // A mixed selection ends up uniform: on, because not every paragraph is quoted yet.
    [Fact]
    public void ToggleQuote_OnAMixedSelection_TurnsTheWholeSelectionOn() => UiThread.Run(() =>
    {
        var (ed, a, b, c) = ThreeSelected();
        b.IsQuote = true;
        ed.ToggleQuote();
        Assert.All(new[] { a, b, c }, p => Assert.True(p.IsQuote));

        ed.ToggleQuote(); // now all quoted -> the same toggle clears them all
        Assert.All(new[] { a, b, c }, p => Assert.False(p.IsQuote));
    });

    // Cell paragraphs are reachable too — the commands must not be top-level only.
    [Fact]
    public void SetTextAlignment_ReachesParagraphsInsideACell() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 1);
        var p1 = P("one");
        var p2 = P("two");
        tb.Cells[0][0].Blocks.Clear();
        tb.Cells[0][0].Blocks.Add(p1);
        tb.Cells[0][0].Blocks.Add(p2);
        var ed = Editor(tb);
        Select(ed, p1, 0, p2, 3);

        ed.SetTextAlignment(TextAlignment.Right);

        Assert.Equal(TextAlignment.Right, p1.TextAlignment);
        Assert.Equal(TextAlignment.Right, p2.TextAlignment);
    });

    [Fact]
    public void SetTextAlignment_WithNoSelection_TouchesOnlyTheCaretParagraph() => UiThread.Run(() =>
    {
        var (ed, a, b, c) = ThreeSelected();
        Caret(ed, b, 1);
        ed.SetTextAlignment(TextAlignment.Center);
        Assert.Equal(TextAlignment.Left, a.TextAlignment);
        Assert.Equal(TextAlignment.Center, b.TextAlignment);
        Assert.Equal(TextAlignment.Left, c.TextAlignment);
    });

    // ---- a delete that changes nothing leaves no step (ported: upstream (6)) -----------------------

    [Fact]
    public void BackspaceAtDocumentStart_LeavesNoUndoStep() => UiThread.Run(() =>
    {
        var p = P("hello");
        var ed = Editor(p);
        ed.MarkSaved();
        Caret(ed, p, 0);

        Call(ed, "Backspace");

        Assert.False(ed.CanUndo);
        Assert.False(ed.IsModified);
        Assert.Equal("hello", TextOf(p));
    });

    [Fact]
    public void DeleteAtDocumentEnd_LeavesNoUndoStep() => UiThread.Run(() =>
    {
        var p = P("hello");
        var ed = Editor(p);
        ed.MarkSaved();
        Caret(ed, p, 5);

        Call(ed, "DeleteForward");

        Assert.False(ed.CanUndo);
        Assert.False(ed.IsModified);
        Assert.Equal("hello", TextOf(p));
    });

    // Guard for the two above: a Backspace that DOES merge must still checkpoint.
    [Fact]
    public void BackspaceAtAParagraphStart_StillCheckpoints() => UiThread.Run(() =>
    {
        var a = P("one");
        var b = P("two");
        var ed = Editor(a, b);
        ed.MarkSaved();
        Caret(ed, b, 0);

        Call(ed, "Backspace");

        Assert.True(ed.CanUndo);
        Assert.True(ed.IsModified);
        Assert.Equal("onetwo", TextOf(a));
    });

    // Upstream once pushed two identical checkpoints per Enter, so the first Ctrl+Z appeared to do nothing.
    [Fact]
    public void Enter_PushesExactlyOneUndoStep() => UiThread.Run(() =>
    {
        var p = P("abcd");
        var ed = Editor(p);
        Caret(ed, p, 2);

        Call(ed, "InsertParagraphBreak", false);
        Assert.Equal(2, TopParagraphs(ed).Count);

        ed.Undo();

        var paras = TopParagraphs(ed);
        Assert.Single(paras);
        Assert.Equal("abcd", TextOf(paras[0]));
        Assert.False(ed.CanUndo);
    });

    // ---- list toggling keeps inline objects (ported: upstream's fuzz finding) ----------------------
    //
    // Turning a list on splits the paragraph at its hard lines into NEW paragraphs. Inline objects must be
    // MOVED into them, not cloned: a clone replaces the instance the caret, selection chrome and resize
    // handles point at, and the original — with an inline table's cell paragraphs — goes out with the
    // discarded source paragraph. The command fuzz cannot see this (a clone has the same shape), which a
    // falsification run proved: cloning here left all 24 of its seeds green.

    [Fact]
    public void TogglingAListOnAHostParagraph_KeepsTheSameInlineTableInstance() => UiThread.Run(() =>
    {
        var host = P("before");
        var ed = Editor(host);
        Caret(ed, host, 6);
        ed.InsertInlineTable(1, 2);
        var original = host.Inlines.OfType<InlineTable>().Single();
        var cellPara = original.Table.Cells[0][0].Para;

        ed.ToggleBullet();

        var after = TopParagraphs(ed).SelectMany(p => p.Inlines.OfType<InlineTable>()).Single();
        Assert.Same(original, after);
        Assert.Same(after, cellPara.Parent is TableCell tc && tc.Parent is TableBlock tb
            ? TopParagraphs(ed).SelectMany(p => p.Inlines.OfType<InlineTable>()).Single(it => it.Table == tb)
            : null); // the cell paragraph still hangs off the instance that is in the document
    });

    [Fact]
    public void TogglingAListOnAHostParagraph_KeepsTheSameInlineImageInstance() => UiThread.Run(() =>
    {
        var host = P("x");
        var ed = Editor(host);
        Caret(ed, host, 1);
        ed.InsertInlineImage(TinyPng, "image/png");
        var img = host.Inlines.OfType<InlineImage>().Single();

        ed.ToggleBullet();

        Assert.Same(img, TopParagraphs(ed).SelectMany(p => p.Inlines.OfType<InlineImage>()).Single());
    });

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    // ---- what a character command targets ---------------------------------------------------------

    // No selection, caret inside a word: the command takes the word — and only the word.
    [Fact]
    public void ToggleBold_WithTheCaretInsideAWord_BoldsExactlyThatWord() => UiThread.Run(() =>
    {
        var p = P("alpha beta gamma");
        var ed = Editor(p);
        Caret(ed, p, 7); // inside "beta" (6..10)

        ed.ToggleBold();

        Assert.False(BoldAt(p, 5));  // the space before
        Assert.True(BoldAt(p, 6));
        Assert.True(BoldAt(p, 9));
        Assert.False(BoldAt(p, 10)); // the space after
        Assert.True(ed.CanUndo);
    });

    // No selection and no word at the caret: nothing in the document changes — the style waits for the
    // next typed text (Word's behaviour). So there is no undo step and no modified flag yet, but the
    // toolbar must already show it, or the user cannot tell the toggle took.
    [Fact]
    public void ToggleBold_AtACaretOutsideAnyWord_WaitsForTheNextTypedText() => UiThread.Run(() =>
    {
        var p = P("alpha ");
        var ed = Editor(p);
        ed.MarkSaved();
        Caret(ed, p, 6); // after the space

        ed.ToggleBold();

        Assert.False(ed.CanUndo);
        Assert.False(ed.IsModified);
        Assert.False(BoldAt(p, 0));
        Assert.True(ed.GetCaretFormat().Bold); // the pending format previews at the caret

        ed.InsertText("x");

        Assert.True(BoldAt(p, 6));  // the typed text took it
        Assert.False(BoldAt(p, 0)); // and nothing else did
        ed.Undo();
        Assert.Equal("alpha ", TextOf(TopParagraphs(ed)[0])); // typing + its style are one step
    });

    // ---- Word's toggle rule -----------------------------------------------------------------------
    //
    // A toggle turns the format OFF only when every character in the target already has it; otherwise it
    // turns it ON for all of them. The editor used to flip each run on its own, so selecting
    // "plain BOLD plain" and pressing Ctrl+B produced "BOLD plain BOLD" (upstream still does). The three
    // tests below each separate the two rules; the pending one is a guard, since with no text to look at
    // both rules land on the same answer.

    public static TheoryData<string> Toggles => new() { "bold", "italic", "underline", "strike" };

    private static (Action<RichEditor> toggle, Func<Run, bool> has, Action<Run> give) ToggleOf(string name) => name switch
    {
        "bold" => (e => e.ToggleBold(), r => r.FontWeight.Weight >= 600, r => r.FontWeight = FontWeights.Bold),
        "italic" => (e => e.ToggleItalic(), r => r.FontStyle == Windows.UI.Text.FontStyle.Italic,
                     r => r.FontStyle = Windows.UI.Text.FontStyle.Italic),
        "underline" => (e => e.ToggleUnderline(), r => r.TextDecorations.HasFlag(TextDecorationFlags.Underline),
                        r => r.TextDecorations |= TextDecorationFlags.Underline),
        _ => (e => e.ToggleStrikethrough(), r => r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough),
              r => r.TextDecorations |= TextDecorationFlags.Strikethrough),
    };

    [Theory]
    [MemberData(nameof(Toggles))]
    public void AToggle_OnAMixedSelection_TurnsItOnForAll_ThenOffForAll(string name) => UiThread.Run(() =>
    {
        var (toggle, has, give) = ToggleOf(name);
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "plain " });   // 0..6
        var mid = new Run { Text = "MID" };            // 6..9
        give(mid);
        p.Inlines.Add(mid);
        p.Inlines.Add(new Run { Text = " plain" });   // 9..15
        var ed = Editor(p);
        Select(ed, p, 0, p, 15);
        int[] probes = { 0, 5, 6, 8, 9, 14 };

        toggle(ed);
        Assert.All(probes, i => Assert.True(has(RunAt(p, i)), $"{name} missing at {i} after the first press"));

        toggle(ed); // now every character has it, so the same key clears it everywhere
        Assert.All(probes, i => Assert.False(has(RunAt(p, i)), $"{name} still set at {i} after the second press"));
    });

    // The same rule when the selection STARTS formatted. Without this the tests above cannot tell Word's
    // rule from "the first character decides" (another common one), which would clear everything here.
    [Theory]
    [MemberData(nameof(Toggles))]
    public void AToggle_OnASelectionThatStartsFormatted_StillTurnsItOnForAll(string name) => UiThread.Run(() =>
    {
        var (toggle, has, give) = ToggleOf(name);
        var p = new Paragraph();
        var first = new Run { Text = "MID" };          // 0..3
        give(first);
        p.Inlines.Add(first);
        p.Inlines.Add(new Run { Text = " plain" });   // 3..9
        var ed = Editor(p);
        Select(ed, p, 0, p, 9);

        toggle(ed);

        Assert.All(new[] { 0, 2, 3, 8 }, i => Assert.True(has(RunAt(p, i)), $"{name} missing at {i}"));
    });

    // All bold ACROSS paragraphs: one press clears it. The range reader returns a synthetic "\n" run
    // between paragraphs; counted as text, that unformatted separator would make "all bold" unreachable.
    [Fact]
    public void ToggleBold_OnAnAllBoldSelectionAcrossParagraphs_TurnsItOff() => UiThread.Run(() =>
    {
        Paragraph BoldP(string t) { var p = new Paragraph(); p.Inlines.Add(new Run { Text = t, FontWeight = FontWeights.Bold }); return p; }
        var a = BoldP("one");
        var b = BoldP("two");
        var ed = Editor(a, b);
        Select(ed, a, 0, b, 3);

        ed.ToggleBold();

        Assert.False(BoldAt(a, 0));
        Assert.False(BoldAt(b, 2));
    });

    // All bold across a cell rectangle that includes an EMPTY cell: one press clears it. An empty cell has
    // no character to disagree; counting its placeholder run would keep the rectangle "mixed" forever.
    [Fact]
    public void ToggleBold_OnAnAllBoldCellRectangleWithAnEmptyCell_TurnsItOff() => UiThread.Run(() =>
    {
        var tb = new TableBlock(3, 1);
        foreach (var (r, text) in new[] { (0, "a"), (1, "b") })
        {
            var run = (Run)tb.Cells[r][0].Para.Inlines[0];
            run.Text = text;
            run.FontWeight = FontWeights.Bold;
        }
        var ed = Editor(P("top"), tb, P("end"));      // row 2 stays an empty cell
        Select(ed, tb.Cells[0][0].Para, 0, tb.Cells[2][0].Para, 0);

        ed.ToggleBold();

        Assert.False(BoldAt(tb.Cells[0][0].Para, 0));
        Assert.False(BoldAt(tb.Cells[1][0].Para, 0));
    });

    // The caret word is a target too: half a bold word becomes a bold word.
    [Fact]
    public void ToggleBold_OnAHalfBoldWordAtTheCaret_BoldsTheWholeWord() => UiThread.Run(() =>
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "ab" });
        p.Inlines.Add(new Run { Text = "CD", FontWeight = FontWeights.Bold });
        p.Inlines.Add(new Run { Text = " tail" });
        var ed = Editor(p);
        Caret(ed, p, 1);

        ed.ToggleBold();

        Assert.All(new[] { 0, 1, 2, 3 }, i => Assert.True(BoldAt(p, i), $"not bold at {i}"));
        Assert.False(BoldAt(p, 5)); // outside the word
    });

    // A cell rectangle decides over all of its cells together: one bold cell does not make the press clear.
    [Fact]
    public void ToggleBold_OnACellRectangleWithOneBoldCell_BoldsEveryCell() => UiThread.Run(() =>
    {
        var tb = new TableBlock(2, 1);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "a";
        var b = (Run)tb.Cells[1][0].Para.Inlines[0];
        b.Text = "b";
        b.FontWeight = FontWeights.Bold;
        var ed = Editor(P("top"), tb, P("end"));
        Select(ed, tb.Cells[0][0].Para, 0, tb.Cells[1][0].Para, 1);

        ed.ToggleBold();

        Assert.True(BoldAt(tb.Cells[0][0].Para, 0));
        Assert.True(BoldAt(tb.Cells[1][0].Para, 0)); // stays bold rather than flipping off
    });

    // No text to look at: the caret's format decides, and a second press before typing cancels the first.
    [Fact]
    public void ToggleBold_Pending_FollowsTheCaretFormat_AndTwoPressesCancel() => UiThread.Run(() =>
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "BOLD ", FontWeight = FontWeights.Bold });
        var ed = Editor(p);
        Caret(ed, p, 5); // after the bold space: no word at the caret

        ed.ToggleBold();
        Assert.False(ed.GetCaretFormat().Bold); // bold at the caret, so the press turns it off
        ed.ToggleBold();
        Assert.True(ed.GetCaretFormat().Bold);  // and the second press turns it back on

        ed.InsertText("x");
        Assert.True(BoldAt(p, 5));
    });

    [Fact]
    public void ClearFormatting_ResetsEveryCharacterAttribute_AndLeavesTheParagraphAlone() => UiThread.Run(() =>
    {
        var p = new Paragraph { HeadingLevel = 2 };
        p.Inlines.Add(new Run
        {
            Text = "styled",
            FontWeight = FontWeights.Bold,
            FontStyle = Windows.UI.Text.FontStyle.Italic,
            TextDecorations = TextDecorationFlags.Underline | TextDecorationFlags.Strikethrough,
            FontFamily = "Arial",
            Foreground = Red,
            Background = Red,
            NavigateUri = "https://x.test/",
        });
        var ed = Editor(p);
        Select(ed, p, 0, p, 6);

        ed.ClearFormatting();

        var r = RunAt(p, 0);
        Assert.Equal(400, r.FontWeight.Weight);
        Assert.Equal(Windows.UI.Text.FontStyle.Normal, r.FontStyle);
        Assert.Equal(TextDecorationFlags.None, r.TextDecorations);
        Assert.Null(r.FontFamily);
        Assert.Null(r.Foreground);
        Assert.Null(r.Background);
        Assert.Null(r.NavigateUri); // a link is character formatting too
        Assert.Equal(2, p.HeadingLevel); // character-only: the heading stays
    });

    // The larger/smaller commands walk a fixed ladder from the caret's size, and stop at its ends.
    [Fact]
    public void IncreaseAndDecreaseFontSize_WalkTheLadderAndStopAtItsEnds() => UiThread.Run(() =>
    {
        var p = P("alpha");
        var ed = Editor(p);
        Caret(ed, p, 2);

        ed.IncreaseFontSize();
        Assert.Equal(10.5, RunAt(p, 2).FontSize); // 10 -> the next rung, not +1
        ed.IncreaseFontSize();
        Assert.Equal(11, RunAt(p, 2).FontSize);
        ed.DecreaseFontSize();
        ed.DecreaseFontSize();
        ed.DecreaseFontSize();
        Assert.Equal(9, RunAt(p, 2).FontSize);

        ed.SetFontSize(96);
        ed.IncreaseFontSize();
        Assert.Equal(96, RunAt(p, 2).FontSize); // top rung holds
        ed.SetFontSize(8);
        ed.DecreaseFontSize();
        Assert.Equal(8, RunAt(p, 2).FontSize);  // bottom rung holds
    });

    // A host document may leave FontSize unset (≤ 0); such text renders at DefaultFontSize and the caret is
    // sized from it. The caret format reported BodyFontSizePt (10) instead — so a host at 14 saw "10" in
    // the toolbar, and IncreaseFontSize, which steps from the reported size, turned 14pt text into 10.5.
    // Found by a probe while writing the ladder test above; upstream has the same line.
    [Fact]
    public void AnUnsetRunSize_IsReportedAndSteppedFromTheDefaultFontSize() => UiThread.Run(() =>
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "alpha", FontSize = 0 });
        var ed = Editor(p);
        ed.DefaultFontSize = 14;
        Caret(ed, p, 2);

        Assert.Equal(14, ed.GetCaretFormat().FontSize);

        ed.IncreaseFontSize();
        Assert.Equal(16, RunAt(p, 2).FontSize); // the next rung above what is on screen, not above 10
    });

    // In a heading an unstyled run is DRAWN at the heading's size, so that is what the toolbar must show and
    // what "larger" steps from. The caret format reported the raw 10: an H1 drawn at 20 read "10", and
    // IncreaseFontSize stamped 10.5 on it — shrinking the heading. Found by a probe after the fix above, by
    // asking where else the reported size could differ from the drawn one. Upstream has the same code.
    [Fact]
    public void AHeadingsUnstyledRun_IsReportedAndSteppedFromTheHeadingSize() => UiThread.Run(() =>
    {
        var p = new Paragraph { HeadingLevel = 1 };
        p.Inlines.Add(new Run { Text = "Title" });
        var ed = Editor(p);
        Caret(ed, p, 2);

        Assert.Equal(20, ed.GetCaretFormat().FontSize); // an H1 is drawn at 20

        ed.IncreaseFontSize();
        Assert.Equal(24, RunAt(p, 2).FontSize); // the rung above 20 — not 10.5, which shrank it
    });

    // Changing the heading level re-sizes UNSTYLED text (it is drawn at the heading's size), and leaves
    // text the user sized explicitly alone — "larger" stamps an explicit size, which is why a heading
    // change after it keeps that size. Pinned after a live check reported "H1 -> H2 does not change the
    // size": the demo was running a stale library, and the sequence had pressed "larger" first.
    [Fact]
    public void SetHeading_ResizesUnstyledText_AndKeepsAnExplicitSize() => UiThread.Run(() =>
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = "Title" });
        var ed = Editor(p);
        Caret(ed, p, 2);

        ed.SetHeading(1);
        Assert.Equal(20, ed.GetCaretFormat().FontSize);
        ed.SetHeading(2);
        Assert.Equal(16, ed.GetCaretFormat().FontSize); // follows the level

        ed.IncreaseFontSize();                          // explicit 18 now (the ladder's rung above 16)
        ed.SetHeading(1);
        Assert.Equal(18, ed.GetCaretFormat().FontSize); // not the H1 size: explicit wins over the level
        ed.SetHeading(3);
        Assert.Equal(18, ed.GetCaretFormat().FontSize);
    });

    [Fact]
    public void SetHyperlink_TrimsTheUrl_AndAnEmptyOneRemovesTheLink() => UiThread.Run(() =>
    {
        var p = P("go here now");
        var ed = Editor(p);
        Select(ed, p, 3, p, 7);

        ed.SetHyperlink("  https://x.test/  ");

        Assert.Equal("https://x.test/", RunAt(p, 3).NavigateUri);
        Assert.Null(RunAt(p, 2).NavigateUri);
        Assert.Null(RunAt(p, 7).NavigateUri);
        Caret(ed, p, 5);
        Assert.Equal("https://x.test/", ed.CurrentLinkUri());

        Select(ed, p, 3, p, 7);
        ed.SetHyperlink("");
        Assert.Null(RunAt(p, 3).NavigateUri);
    });

    // ---- the format painter -----------------------------------------------------------------------

    // Capture at the caret, then the next selection receives exactly the captured character format, as one
    // undo step, and the painter disarms. The apply half runs on pointer-release, so the method that
    // handler calls is invoked directly.
    [Fact]
    public void FormatPainter_CopiesTheCapturedFormatOntoTheNextSelection_Once() => UiThread.Run(() =>
    {
        var src = new Paragraph();
        src.Inlines.Add(new Run { Text = "SRC", FontWeight = FontWeights.Bold, Foreground = Red });
        var dst = P("target text");
        var ed = Editor(src, dst);
        Caret(ed, src, 1);

        ed.StartFormatPainter();
        Assert.True(ed.IsFormatPainterActive);

        Select(ed, dst, 0, dst, 6);
        Call(ed, "ApplyFormatPainterToSelection");

        Assert.False(ed.IsFormatPainterActive); // one shot
        Assert.True(BoldAt(dst, 0));
        Assert.Equal(Red, RunAt(dst, 5).Foreground);
        Assert.False(BoldAt(dst, 7));           // outside the selection
        Assert.Null(RunAt(dst, 7).Foreground);

        ed.Undo();
        var restored = TopParagraphs(ed)[1];
        Assert.False(BoldAt(restored, 0));
        Assert.Null(RunAt(restored, 0).Foreground);
    });

    [Fact]
    public void StartFormatPainter_Twice_Disarms() => UiThread.Run(() =>
    {
        var p = P("word");
        var ed = Editor(p);
        Caret(ed, p, 2);

        ed.StartFormatPainter();
        ed.StartFormatPainter();

        Assert.False(ed.IsFormatPainterActive);
    });

    // ---- table cell rectangles --------------------------------------------------------------------

    // A drag from one cell to another selects the RECTANGLE between them. The linear path would walk the
    // cells in row-major order and bleed into (0,1), which is outside the highlighted column — the reason
    // ApplyStyleToSelection and SelectedParagraphs both special-case the rectangle.
    [Fact]
    public void ACellRectangleSelection_FormatsExactlyTheCellsItCovers() => UiThread.Run(() =>
    {
        var tb = new TableBlock(2, 2);
        string[,] text = { { "a", "b" }, { "c", "d" } };
        for (int r = 0; r < 2; r++)
            for (int c = 0; c < 2; c++)
                ((Run)tb.Cells[r][c].Para.Inlines[0]).Text = text[r, c];
        var ed = Editor(P("top"), tb, P("end"));
        Select(ed, tb.Cells[0][0].Para, 0, tb.Cells[1][0].Para, 1); // column 0, top to bottom

        ed.ToggleBold();
        ed.SetTextAlignment(TextAlignment.Center);

        foreach (var (r, c, expected) in new[] { (0, 0, true), (1, 0, true), (0, 1, false), (1, 1, false) })
        {
            var para = tb.Cells[r][c].Para;
            Assert.True(expected == BoldAt(para, 0), $"bold at cell ({r},{c})");
            Assert.True(expected == (para.TextAlignment == TextAlignment.Center), $"alignment at cell ({r},{c})");
        }
    });

    // ---- lists ------------------------------------------------------------------------------------

    // A top-level paragraph with soft line breaks becomes one list item per line (each line is an item in
    // every other editor), as a single undo step, with the caret mapped into the item it was on.
    [Fact]
    public void ToggleBullet_OnAParagraphWithSoftBreaks_MakesOneItemPerLine() => UiThread.Run(() =>
    {
        var p = P("one\ntwo");
        var ed = Editor(p);
        Caret(ed, p, 1);

        ed.ToggleBullet();

        var items = TopParagraphs(ed);
        Assert.Equal(new[] { "one", "two" }, items.Select(TextOf));
        Assert.All(items, i => Assert.Equal(ListKind.Bullet, i.ListType));
        Assert.Equal("one", TextOf(CaretParagraph(ed)));

        ed.Undo();
        var back = Assert.Single(TopParagraphs(ed));
        Assert.Equal("one\ntwo", TextOf(back));
        Assert.Equal(ListKind.None, back.ListType);
    });

    // "Off" clears the whole list state. Leaving ListLevel behind indents the paragraph with no marker to
    // explain it (ParaLeft adds the level's indent regardless of ListType).
    [Fact]
    public void RemoveList_ClearsKindLevelAndMarker() => UiThread.Run(() =>
    {
        var p = P("item");
        p.ListType = ListKind.Ordered;
        p.ListLevel = 2;
        p.ListMarker = ListMarkerStyle.LowerRoman;
        var ed = Editor(p);
        Caret(ed, p, 1);

        ed.RemoveList();

        Assert.Equal(ListKind.None, p.ListType);
        Assert.Equal(0, p.ListLevel);
        Assert.Equal(ListMarkerStyle.Default, p.ListMarker);
    });

    // Picking a marker style is never a toggle: choosing the style the list already has keeps the list.
    [Fact]
    public void SetListStyle_NeverTurnsAListOff() => UiThread.Run(() =>
    {
        var p = P("item");
        p.ListType = ListKind.Bullet;
        p.ListMarker = ListMarkerStyle.Disc;
        var ed = Editor(p);
        Caret(ed, p, 1);

        ed.SetListStyle(ListMarkerStyle.Disc);

        var item = Assert.Single(TopParagraphs(ed));
        Assert.Equal(ListKind.Bullet, item.ListType);
        Assert.Equal(ListMarkerStyle.Disc, item.ListMarker);

        ed.SetListStyle(ListMarkerStyle.Decimal); // a number style switches the kind
        Assert.Equal(ListKind.Ordered, Assert.Single(TopParagraphs(ed)).ListType);
    });

    // ---- read-only --------------------------------------------------------------------------------

    // Every formatting command is an edit, so a viewer refuses all of them: no change, no history, no
    // modified flag. One gate per command, and each was only ever checked by reading the code.
    [Fact]
    public void AReadOnlyEditor_RefusesEveryFormattingCommand() => UiThread.Run(() =>
    {
        var p = P("alpha beta");
        p.ListType = ListKind.Bullet;
        var ed = Editor(p, P("second"));
        ed.IsReadOnly = true;
        ed.MarkSaved();
        string before = DocumentFuzzTests.Shape(ed.Document!);

        var commands = new (string name, Action run)[]
        {
            ("ToggleBold", ed.ToggleBold), ("ToggleItalic", ed.ToggleItalic),
            ("ToggleUnderline", ed.ToggleUnderline), ("ToggleStrikethrough", ed.ToggleStrikethrough),
            ("SetFontSize", () => ed.SetFontSize(20)), ("IncreaseFontSize", ed.IncreaseFontSize),
            ("DecreaseFontSize", ed.DecreaseFontSize), ("SetForeground", () => ed.SetForeground(Red)),
            ("SetHighlight", () => ed.SetHighlight(Red)), ("SetRunFontFamily", () => ed.SetRunFontFamily("Arial")),
            ("ClearFormatting", ed.ClearFormatting), ("SetHyperlink", () => ed.SetHyperlink("https://x.test/")),
            ("SetTextAlignment", () => ed.SetTextAlignment(TextAlignment.Center)), ("SetHeading", () => ed.SetHeading(1)),
            ("SetLineSpacing", () => ed.SetLineSpacing(2)), ("SetLineHeight", () => ed.SetLineHeight(30)),
            ("ToggleQuote", ed.ToggleQuote), ("Indent", () => ed.Indent(20)),
            ("ToggleBullet", ed.ToggleBullet), ("ToggleNumbering", ed.ToggleNumbering),
            ("SetListStyle", () => ed.SetListStyle(ListMarkerStyle.Decimal)), ("RemoveList", ed.RemoveList),
            ("StartFormatPainter", ed.StartFormatPainter),
        };

        foreach (var (name, run) in commands)
        {
            // Once with a selection and once with the caret in a word: the two target paths.
            Select(ed, TopParagraphs(ed)[0], 0, TopParagraphs(ed)[1], 3);
            run();
            Caret(ed, TopParagraphs(ed)[0], 2);
            run();

            Assert.True(before == DocumentFuzzTests.Shape(ed.Document!), $"{name} changed a read-only document");
            Assert.False(ed.CanUndo, $"{name} recorded an undo step");
            Assert.False(ed.IsModified, $"{name} set the modified flag");
            Assert.False(ed.IsFormatPainterActive, $"{name} armed the format painter");
        }
    });
}
