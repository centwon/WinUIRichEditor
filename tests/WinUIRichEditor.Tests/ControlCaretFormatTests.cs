using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Windows.UI;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The caret format report — <see cref="RichEditor.GetCaretFormat"/>, what the toolbar shows.
/// <para>Its fields had no test reference beyond bold and size. The contract that makes it checkable: the
/// report is the toolbar's claim about the text the NEXT KEYSTROKE writes, and about what is on screen.
/// Measured before this file (2026-09-12), it broke both ways:</para>
/// <list type="bullet">
/// <item>Next to an image, insertion and report had separate rules and disagreed at 4 caret positions —
/// the report fell back to the paragraph's LAST run, typing wrote a plain new run. One rule now
/// (<c>TypingSource</c>): the nearest text before the caret skipping objects, else after (Word's).</item>
/// <item>The renderer forces a heading's bold and a link's underline and blue; the report said "not bold"
/// over a bold heading, and Ctrl+B/U flipped a hidden flag with no visible change. Now reported as drawn,
/// and a toggle that cannot change what is shown does nothing (user decision).</item>
/// <item>Typing at a link's end extended the link; now it writes plain text (Word; user decision).</item>
/// <item>ClearFormatting in a heading wrote the host's DefaultFontSize as an explicit size: H1 20 → 14.</item>
/// </list></summary>
[Collection(UiTests.Collection)]
public class ControlCaretFormatTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);
    private static readonly Color Red = Microsoft.UI.ColorHelper.FromArgb(255, 255, 0, 0);
    private static readonly Color Yellow = Microsoft.UI.ColorHelper.FromArgb(255, 255, 255, 0);
    private static readonly Color LinkBlue = Microsoft.UI.ColorHelper.FromArgb(255, 0, 0, 255);
    private const string Url = "https://a.example/";

    // ---- plumbing ---------------------------------------------------------------------------------

    private static Run Plain(string t) => new() { Text = t };
    private static Run Bold(string t) => new() { Text = t, FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 } };
    private static Run Ital(string t) => new() { Text = t, FontStyle = Windows.UI.Text.FontStyle.Italic, FontSize = 14, Foreground = Red };
    private static Run Mono(string t) => new() { Text = t, FontFamily = "Consolas", Background = Yellow };
    private static Run Strike(string t) => new() { Text = t, TextDecorations = TextDecorationFlags.Strikethrough, FontSize = 12 };
    private static Run Link(string t) => new() { Text = t, NavigateUri = Url };
    private static InlineImage Img() => new() { Width = 10, Height = 10 };

    private static RichEditor Editor(params Paragraph[] paras)
    {
        var doc = new FlowDocument();
        foreach (var p in paras) doc.Blocks.Add(p);
        return new RichEditor { Document = doc };
    }

    private static Paragraph Para(int heading, params Inline[] inlines)
    {
        var p = new Paragraph { HeadingLevel = heading };
        foreach (var i in inlines) p.Inlines.Add(i);
        return p;
    }

    private static Paragraph Para(params Inline[] inlines) => Para(0, inlines);

    private static List<Paragraph> Paras(RichEditor ed) => ed.Document!.Blocks.OfType<Paragraph>().ToList();

    private static void Caret(RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
    }

    private static void Select(RichEditor ed, Paragraph a, int ao, Paragraph b, int bo)
    {
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(a, ao));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(b, bo));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(b, bo));
    }

    // The run holding the character AT offset.
    private static Run RunAt(Paragraph p, int offset)
    {
        int pos = 0;
        foreach (var inl in p.Inlines)
        {
            int len = inl is Run r0 ? (r0.Text?.Length ?? 0) : 1;
            if (inl is Run r && offset >= pos && offset < pos + len) return r;
            pos += len;
        }
        throw new InvalidOperationException($"no run holds offset {offset}");
    }

    private static int Length(Paragraph p) => p.Inlines.Sum(i => i is Run r ? r.Text?.Length ?? 0 : 1);

    // What a run looks like on screen, written out here rather than borrowed from the product, so the
    // oracle is independent of the rules under test (no headings in the sweep — see the heading tests).
    private static string Look(Run r) => string.Join(" ",
        r.FontWeight.Weight >= 600 ? "bold" : "-",
        r.FontStyle == Windows.UI.Text.FontStyle.Italic ? "italic" : "-",
        r.TextDecorations.HasFlag(TextDecorationFlags.Underline) || !string.IsNullOrEmpty(r.NavigateUri) ? "under" : "-",
        r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough) ? "strike" : "-",
        $"{r.FontSize}pt", r.FontFamily ?? "(family)",
        Hex(r.Foreground ?? (string.IsNullOrEmpty(r.NavigateUri) ? null : LinkBlue)), Hex(r.Background));

    private static string Look(RichEditor.CaretFormat f) => string.Join(" ",
        f.Bold ? "bold" : "-", f.Italic ? "italic" : "-", f.Underline ? "under" : "-", f.Strike ? "strike" : "-",
        $"{f.FontSize}pt", f.FontFamily ?? "(family)", Hex(f.Foreground), Hex(f.Background));

    private static string Hex(Color? c) => c is { } v ? $"#{v.R:X2}{v.G:X2}{v.B:X2}" : "none";

    // ---- the report predicts the keystroke --------------------------------------------------------

    private static readonly (string Name, Func<Paragraph> Make)[] Layouts =
    {
        ("bold italic", () => Para(Bold("BB"), Ital("II"))),
        ("[img] bold italic", () => Para(Img(), Bold("BB"), Ital("II"))),
        ("bold [img]", () => Para(Bold("BB"), Img())),
        ("bold [img] italic", () => Para(Bold("BB"), Img(), Ital("II"))),
        ("bold [img][img] italic", () => Para(Bold("BB"), Img(), Img(), Ital("II"))),
        ("italic [img] bold", () => Para(Ital("II"), Img(), Bold("BB"))),
        ("mono [img] strike", () => Para(Mono("MM"), Img(), Strike("SS"))),
        ("[img] only", () => Para(Img())),
        ("styled empty run", () => Para(Bold(""))),
        ("link plain", () => Para(Link("ab"), Plain(" cd"))),
        ("plain link plain", () => Para(Plain("ab "), Link("cd"), Plain(" ef"))),
        ("[img] link", () => Para(Img(), Link("cd"))),
    };

    // At EVERY caret position of every layout: the format the toolbar reports is the format the character
    // typed there gets. The four positions that failed before are "[img] bold italic" at 0 and 1,
    // "bold [img]" at 3 and "bold [img][img] italic" at 3.
    [Fact]
    public void AtEveryCaretPosition_TheReportIsTheFormatOfTheCharacterTypedThere() => UiThread.Run(() =>
    {
        var wrong = new List<string>();
        foreach (var (name, make) in Layouts)
        {
            int len = Length(make());
            for (int off = 0; off <= len; off++)
            {
                var ed = Editor(make());
                Caret(ed, Paras(ed)[0], off);
                string reported = Look(ed.GetCaretFormat());
                ed.InsertText("Z");
                string typed = Look(RunAt(Paras(ed)[0], off));
                if (reported != typed) wrong.Add($"[{name}] at {off}: reported {reported} | typed {typed}");
            }
        }
        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    });

    // The rule itself, where the sweep only checks that report and typing agree: the nearest text BEFORE
    // the caret, skipping images; with none before, the nearest after. Before, "bold [img]|" typed plain.
    [Theory]
    [InlineData("bold [img]|", 3, true)]         // after a trailing image: the bold before it
    [InlineData("bold [img]|italic", 3, true)]   // before wins over after
    [InlineData("|[img] bold", 0, true)]         // nothing before: the bold after
    [InlineData("[img]|bold", 1, true)]
    [InlineData("italic [img]|bold", 3, false)]
    public void TypingBesideAnImage_TakesTheNearestTextBefore_ElseAfter(string layout, int caret, bool bold) => UiThread.Run(() =>
    {
        var ed = Editor(layout switch
        {
            "bold [img]|" => Para(Bold("BB"), Img()),
            "bold [img]|italic" => Para(Bold("BB"), Img(), Ital("II")),
            "|[img] bold" => Para(Img(), Bold("BB")),
            "[img]|bold" => Para(Img(), Bold("BB")),
            _ => Para(Ital("II"), Img(), Bold("BB")),
        });
        Caret(ed, Paras(ed)[0], caret);
        ed.InsertText("Z");
        var z = RunAt(Paras(ed)[0], caret);
        Assert.Equal(bold, z.FontWeight.Weight >= 600);
        Assert.Equal(!bold, z.FontStyle == Windows.UI.Text.FontStyle.Italic);
    });

    // ---- links ------------------------------------------------------------------------------------

    // Typing continues a link only strictly inside it. At its end or start the text is plain — and the
    // report says so before the keystroke (no underline, no link blue).
    [Theory]
    [InlineData(4, false)] // the link's end
    [InlineData(0, false)] // its start
    [InlineData(2, true)]  // inside
    public void TypingAtALinksEdge_WritesPlainText_InsideContinuesIt(int caret, bool inLink) => UiThread.Run(() =>
    {
        var ed = Editor(Para(Link("link"), Plain(" after")));
        Caret(ed, Paras(ed)[0], caret);
        var f = ed.GetCaretFormat();
        Assert.Equal(inLink, f.Underline);
        Assert.Equal(inLink ? LinkBlue : (Color?)null, f.Foreground);

        ed.InsertText("Z");
        Assert.Equal(inLink ? Url : null, RunAt(Paras(ed)[0], caret).NavigateUri);
        string linked = string.Concat(Paras(ed)[0].Inlines.OfType<Run>().Where(r => r.NavigateUri == Url).Select(r => r.Text));
        Assert.Equal(inLink ? "liZnk" : "link", linked);
    });

    // A link split over two runs (part of it bold) is still one link across the seam.
    [Fact]
    public void TypingAtTheSeamOfASplitLink_ContinuesIt() => UiThread.Run(() =>
    {
        var head = Link("li");
        head.FontWeight = new Windows.UI.Text.FontWeight { Weight = 700 };
        var ed = Editor(Para(head, Link("nk"), Plain(" after")));
        Caret(ed, Paras(ed)[0], 2);
        ed.InsertText("Z");
        Assert.Equal(Url, RunAt(Paras(ed)[0], 2).NavigateUri);
    });

    // Two keystrokes at a link's end: both plain, and the second joins the run the first one made (the
    // rule must not split every keystroke into its own run).
    [Fact]
    public void TypingOnAtALinksEnd_StaysInOnePlainRun() => UiThread.Run(() =>
    {
        var ed = Editor(Para(Link("link")));
        Caret(ed, Paras(ed)[0], 4);
        ed.InsertText("Z");
        ed.InsertText("Y");
        var p = Paras(ed)[0];
        Assert.Same(RunAt(p, 4), RunAt(p, 5));
        Assert.Equal("ZY", RunAt(p, 4).Text);
        Assert.Null(RunAt(p, 4).NavigateUri);
    });

    // CurrentLinkUri read the same fallback: next to an image it answered with a link from the far end of
    // the paragraph, so "open link" offered a link the caret was nowhere near.
    [Fact]
    public void CurrentLinkUri_NextToAnImage_IsNotALinkElsewhereInTheParagraph() => UiThread.Run(() =>
    {
        var ed = Editor(Para(Img(), Plain("text "), Link("link")));
        Caret(ed, Paras(ed)[0], 1);
        Assert.Null(ed.CurrentLinkUri());
        Caret(ed, Paras(ed)[0], 8);
        Assert.Equal(Url, ed.CurrentLinkUri());
    });

    // Ctrl+U inside a link: it is drawn underlined whatever the flag says, so there is nothing to change —
    // no flag flipped behind the screen, no undo step.
    [Fact]
    public void CtrlU_OnALink_DoesNothing() => UiThread.Run(() =>
    {
        var ed = Editor(Para(Link("link"), Plain(" after")));
        Caret(ed, Paras(ed)[0], 2);
        ed.ToggleUnderline();
        Assert.False(ed.CanUndo);
        Assert.Equal(TextDecorationFlags.None, RunAt(Paras(ed)[0], 1).TextDecorations);
        Assert.True(ed.GetCaretFormat().Underline);
    });

    // At a link's end the caret is in the link's WORD, and that word is what Ctrl+U styles — all of it drawn
    // underlined, so nothing to do. The typed-text format (plain there) must not decide it: judged by that,
    // the toggle set a hidden underline flag on the link. (Added upstream first — its PR #28 — where the
    // same perturbation found no test; ported back.)
    [Fact]
    public void CtrlU_AtALinksEnd_LeavesTheLinkAlone() => UiThread.Run(() =>
    {
        var ed = Editor(Para(Link("link"), Plain(" after")));
        Caret(ed, Paras(ed)[0], 4);
        ed.ToggleUnderline();
        Assert.False(ed.CanUndo);
        Assert.Equal(TextDecorationFlags.None, RunAt(Paras(ed)[0], 1).TextDecorations);
    });

    // Across a link and plain text the plain part is what changes: the link counts as underlined (it is
    // shown so), the rest is not, so the toggle turns underline ON.
    [Fact]
    public void CtrlU_OverALinkAndPlainText_UnderlinesThePlainText() => UiThread.Run(() =>
    {
        var ed = Editor(Para(Link("link"), Plain(" after")));
        var p = Paras(ed)[0];
        Select(ed, p, 0, p, 10);
        ed.ToggleUnderline();
        Assert.True(RunAt(Paras(ed)[0], 6).TextDecorations.HasFlag(TextDecorationFlags.Underline));
    });

    // ---- headings ---------------------------------------------------------------------------------

    [Fact]
    public void AHeading_IsReportedBold() => UiThread.Run(() =>
    {
        var ed = Editor(Para(1, Plain("Title")));
        Caret(ed, Paras(ed)[0], 2);
        Assert.True(ed.GetCaretFormat().Bold);
    });

    [Fact]
    public void CtrlB_OnAHeading_DoesNothing() => UiThread.Run(() =>
    {
        var ed = Editor(Para(1, Plain("Title")));
        var p = Paras(ed)[0];
        Select(ed, p, 0, p, 5);
        ed.ToggleBold();
        Assert.False(ed.CanUndo);
        Assert.Equal(400, RunAt(Paras(ed)[0], 1).FontWeight.Weight);
    });

    // With no word at the caret the toggle would arm a pending style for the next keystroke — invisible in
    // a heading, and a hidden bold flag that would surface once the heading is removed. Not armed at all.
    [Fact]
    public void CtrlB_AtAnEmptyCaretInAHeading_ArmsNothing() => UiThread.Run(() =>
    {
        var ed = Editor(Para(1, Plain("Title ")));
        Caret(ed, Paras(ed)[0], 6);
        ed.ToggleBold();
        Assert.Null(T.GetField("_pendingCaretStyles", NP)!.GetValue(ed));
        ed.InsertText("Z");
        Assert.Equal(400, RunAt(Paras(ed)[0], 6).FontWeight.Weight);
    });

    // Heading plus body: the heading counts as bold (shown so), the body is not, so the toggle turns bold
    // ON and the body changes; a second press turns it off again.
    [Fact]
    public void CtrlB_OverAHeadingAndBodyText_TogglesTheBody() => UiThread.Run(() =>
    {
        var ed = Editor(Para(1, Plain("Title")), Para(Plain("text")));
        var ps = Paras(ed);
        Select(ed, ps[0], 0, ps[1], 4);
        ed.ToggleBold();
        Assert.True(RunAt(Paras(ed)[1], 1).FontWeight.Weight >= 600);
        ps = Paras(ed);
        Select(ed, ps[0], 0, ps[1], 4);
        ed.ToggleBold();
        Assert.True(RunAt(Paras(ed)[1], 1).FontWeight.Weight < 600);
    });

    // ---- ClearFormatting --------------------------------------------------------------------------

    // "Unstyled" inside a heading draws at the heading's size. Writing the host's DefaultFontSize made it
    // an explicit size instead: measured 20 → 14 with a host default of 14. Body text still clears to it.
    [Fact]
    public void ClearFormatting_KeepsAHeadingAtItsSize_AndClearsBodyTextToTheHostDefault() => UiThread.Run(() =>
    {
        var ed = Editor(Para(1, Plain("Title")), Para(new Run { Text = "text", FontSize = 12 }));
        ed.DefaultFontSize = 14;
        var ps = Paras(ed);
        Select(ed, ps[0], 0, ps[1], 4);
        ed.ClearFormatting();
        ps = Paras(ed);
        Caret(ed, ps[0], 3);
        Assert.Equal(20, ed.GetCaretFormat().FontSize);
        Caret(ed, ps[1], 3);
        Assert.Equal(14, ed.GetCaretFormat().FontSize);
    });

    // The same rule previewed: a pending ClearFormatting at an empty caret in a heading reports the size
    // the typed text will get. The preview clone had no paragraph, so it reported 14 for text drawn at 20.
    [Fact]
    public void APendingClearFormattingInAHeading_PreviewsTheHeadingSize() => UiThread.Run(() =>
    {
        var ed = Editor(Para(1, Plain("Title ")));
        ed.DefaultFontSize = 14;
        Caret(ed, Paras(ed)[0], 6);
        ed.ClearFormatting();
        Assert.Equal(20, ed.GetCaretFormat().FontSize);
        ed.InsertText("Z");
        Caret(ed, Paras(ed)[0], 7);
        Assert.Equal(20, ed.GetCaretFormat().FontSize);
    });
}
