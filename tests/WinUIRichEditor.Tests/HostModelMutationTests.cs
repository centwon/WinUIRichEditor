using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>A host that edits the model it assigned — Document.Blocks / Paragraph.Inlines / Run.Text — instead of going
/// through the editor's commands. The model raises no change notifications (Inlines and a cell's Blocks are plain
/// lists), so the host tells the editor with NotifyDocumentChanged (upstream AvaloniaRichEditor PR #67). Measured here
/// without it (2026-10-17) before porting — the same three as upstream's.</summary>
[Collection(UiTests.Collection)]
public class HostModelMutationTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;

    private static string TextOf(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    private static Paragraph Para(string text) => new() { Inlines = { new Run { Text = text } } };

    // What a keystroke does: the typing path, which coalesces consecutive characters into one undo step.
    private static void Type(RichEditor ed, string text)
    {
        foreach (char c in text) typeof(RichEditor).GetMethod("TypeText", NP)!.Invoke(ed, new object[] { c.ToString() });
    }

    private static void Enter(RichEditor ed) => typeof(RichEditor).GetMethod("InsertParagraphBreak", NP)!.Invoke(ed, new object[] { false });

    [Fact]
    public void CaretParagraphRemoved_ThenTyping_TextIsInTheDocument() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("one"));
        doc.Blocks.Add(Para("two"));
        var ed = new RichEditor { Document = doc };
        ed.FocusDocumentEnd();

        doc.Blocks.RemoveAt(1);
        ed.NotifyDocumentChanged();
        ed.InsertText("X");

        Assert.Equal("oneX", ed.GetPlainText());
    });

    [Fact]
    public void ParagraphAppended_ThenEnterAtItsEnd_SplitsIt() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("one"));
        var ed = new RichEditor { Document = doc };

        doc.Blocks.Add(Para("two"));
        ed.NotifyDocumentChanged();
        ed.FocusDocumentEnd();
        Enter(ed);
        ed.InsertText("three");

        Assert.Equal(new[] { "one", "two", "three" }, ed.Document!.Blocks.OfType<Paragraph>().Select(TextOf).ToArray());
    });

    [Fact]
    public void TableAppended_ThenTypingAtTheEnd_TextGoesAfterTheTable() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("one"));
        var ed = new RichEditor { Document = doc };

        doc.Blocks.Add(new TableBlock(1, 1));
        ed.NotifyDocumentChanged();
        ed.FocusDocumentEnd();
        ed.InsertText("after");

        var last = Assert.IsType<Paragraph>(ed.Document!.Blocks.Last());
        Assert.Equal("after", TextOf(last));
    });

    // A paragraph that is still there keeps the caret, its offset clamped to the paragraph's new length.
    [Fact]
    public void CaretParagraphShortened_CaretStaysInItAtItsNewEnd() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        var run = new Run { Text = "hello world" };
        doc.Blocks.Add(Para("first"));
        doc.Blocks.Add(new Paragraph { Inlines = { run } });
        var ed = new RichEditor { Document = doc };
        ed.FocusDocumentEnd(); // offset 11

        run.Text = "hi";
        ed.NotifyDocumentChanged();
        ed.InsertText("!");

        Assert.Equal("first", TextOf((Paragraph)ed.Document!.Blocks[0]));
        Assert.Equal("hi!", TextOf((Paragraph)ed.Document!.Blocks[1]));
    });

    // The user types, the host edits, the user types again: undo takes back only the second typing, so the host's
    // edit survives one Ctrl+Z (the notification ends the coalescing run that would have swallowed it).
    [Fact]
    public void TypingRunAcrossAHostEdit_UndoKeepsTheHostEdit() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("one"));
        var ed = new RichEditor { Document = doc };
        ed.FocusDocumentEnd();
        Type(ed, "a");

        ed.Document!.Blocks.Add(Para("host"));
        ed.NotifyDocumentChanged(); // the caret stays after "a": no caret move to end the run
        Type(ed, "b");
        ed.Undo();

        // Undo swaps a snapshot in, so read the editor's document.
        Assert.Equal(new[] { "onea", "host" }, ed.Document!.Blocks.OfType<Paragraph>().Select(TextOf).ToArray());
    });

    // A one-character InsertText joined the typing run before it (it took the keystroke's coalescing key), so Ctrl+Z
    // after a host's insert took the user's typing with it. Each call is its own step, as upstream's.
    [Fact]
    public void InsertText_IsItsOwnUndoStep_EvenOneCharacterAfterTyping() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("one"));
        var ed = new RichEditor { Document = doc };
        ed.FocusDocumentEnd();

        Type(ed, "ab");
        ed.InsertText("c");
        ed.InsertText("d");
        ed.Undo();
        Assert.Equal("oneabc", ed.GetPlainText());
        ed.Undo();
        Assert.Equal("oneab", ed.GetPlainText());
        ed.Undo();
        Assert.Equal("one", ed.GetPlainText());
    });

    // And typing after a host's InsertText starts a step of its own.
    [Fact]
    public void TypingAfterInsertText_IsAStepOfItsOwn() => UiThread.Run(() =>
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("one"));
        var ed = new RichEditor { Document = doc };
        ed.FocusDocumentEnd();

        ed.InsertText("X");
        Type(ed, "yz");
        ed.Undo();

        Assert.Equal("oneX", ed.GetPlainText());
    });
}
