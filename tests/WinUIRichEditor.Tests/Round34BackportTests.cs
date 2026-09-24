using System;
using System.Diagnostics;
using System.Linq;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Upstream round 34 (AvaloniaRichEditor PR #53, 2026-09-23), measured here before porting: each case
/// was red in this port first, or is recorded as already right.</summary>
public class Round34BackportTests
{
    // ---- security: HTML ingestion ------------------------------------------------------------------

    // A file: image on ANOTHER machine is a UNC path; File.Exists on it opens an SMB connection that offers the
    // user's NTLM credentials to that host. This port blocks local files on paste by default (temp only), but a
    // host that allows local file images read network shares too. 192.0.2.1 is TEST-NET-1: an attempt shows up
    // as the connect timeout.
    [Theory]
    [InlineData("file://192.0.2.1/share/pic.png")]
    [InlineData("ms-clipboard-file://192.0.2.1/share/pic.png")]
    public void AnImageOnANetworkShare_IsNeverOpened(string src)
    {
        var sw = Stopwatch.StartNew();
        var doc = HtmlDocumentFormatter.ParseHtml($"<p>x<img src=\"{src}\" width=\"100\" height=\"100\"/></p>",
            allowLocalFileImages: true);
        sw.Stop();

        Assert.True(sw.ElapsedMilliseconds < 1000, $"the parse waited {sw.ElapsedMilliseconds} ms on the network share");
        Assert.DoesNotContain(doc.Blocks, b => b is ImageBlock);
    }

    // Script links were kept and written back out into exported and clipboard HTML (upstream decision: drop on read).
    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData(" javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,&lt;script&gt;alert(1)&lt;/script&gt;")]
    public void AScriptLink_IsNotCarriedIntoTheDocument(string href)
    {
        var doc = HtmlDocumentFormatter.ParseHtml($"<p><a href=\"{href}\">click</a></p>");

        var run = doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First(r => r.Text == "click");
        Assert.Null(run.NavigateUri);
    }

    [Theory]
    [InlineData("https://example.com/a?b=1")]
    [InlineData("http://example.com")]
    [InlineData("mailto:someone@example.com")]
    public void AWebLink_IsKept(string href)
    {
        var doc = HtmlDocumentFormatter.ParseHtml($"<p><a href=\"{href}\">click</a></p>");

        var run = doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First(r => r.Text == "click");
        Assert.Equal(href, run.NavigateUri);
    }

    // ---- the same rule on the other ways in (2026-09-24) ---------------------------------------------------
    // Round 34 dropped script links in the HTML reader only. An RTF HYPERLINK field (RTF is a clipboard flavour,
    // so a paste) and a JSON/.flow file carried them in untouched, and the HTML writer sent them back out in
    // exported and clipboard HTML. A host's SetHyperlink reaches the writer too, so it is the backstop.

    private static Run Clicked(FlowDocument doc)
        => doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First(r => r.Text?.Contains("click") == true);

    private static FlowDocument Linked(string href)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "click", NavigateUri = href } } });
        return doc;
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,x")]
    public void AScriptLinkInAnRtfField_IsNotCarriedIntoTheDocument(string href)
    {
        var doc = RtfDocumentFormatter.Parse(
            $@"{{\rtf1\ansi {{\field{{\*\fldinst HYPERLINK ""{href}""}}{{\fldrslt click}}}}\par}}");

        Assert.Null(Clicked(doc).NavigateUri); // the text stays, the link goes
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("vbscript:msgbox(1)")]
    public void AScriptLinkInAJsonFile_IsNotCarriedIntoTheDocument(string href)
    {
        var doc = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(Linked(href)));

        Assert.Null(Clicked(doc).NavigateUri);
    }

    [Fact]
    public void AScriptLinkSetByTheHost_IsNotWrittenToHtml()
    {
        string html = HtmlDocumentFormatter.ToHtml(Linked("javascript:alert(1)"));

        Assert.DoesNotContain("javascript", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("click", html);
    }

    // The other half, so the guards cannot pass by dropping every link.
    [Fact]
    public void AWebLink_SurvivesRtfJsonAndTheHtmlWriter()
    {
        const string url = "https://example.com/a?b=1";

        var rtf = RtfDocumentFormatter.Parse(
            $@"{{\rtf1\ansi {{\field{{\*\fldinst HYPERLINK ""{url}""}}{{\fldrslt click}}}}\par}}");
        var json = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(Linked(url)));

        Assert.Equal(url, Clicked(rtf).NavigateUri);
        Assert.Equal(url, Clicked(json).NavigateUri);
        Assert.Contains("href=\"https://example.com/a?b=1\"", HtmlDocumentFormatter.ToHtml(Linked(url)).Replace("&amp;", "&"));
    }
}

/// <summary>Round 34's control-level cases, measured in this port (see <see cref="Round34BackportTests"/>).</summary>
[Collection(UiTests.Collection)]
public class Round34BackportControlTests
{
    private const System.Reflection.BindingFlags NP = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static Paragraph P(string text) => new() { Inlines = { new Run { Text = text } } };

    private static Controls.RichEditor Editor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        return new Controls.RichEditor { Document = doc };
    }

    private static void Caret(Controls.RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            typeof(Controls.RichEditor).GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
    }

    private static string Text(Block b) => b is Paragraph p ? string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text)) : b.GetType().Name;

    // A command that changes nothing left an undo step that undid nothing (and flagged the document modified).
    [Theory]
    [InlineData("outdent")]
    [InlineData("align")]
    [InlineData("spacing")]
    [InlineData("height")]
    [InlineData("removelist")]
    [InlineData("replaceall")]
    public void ACommandThatChangesNothing_LeavesNoUndoStep(string cmd) => UiThread.Run(() =>
    {
        var p = P("hello world");
        var ed = Editor(p);
        Caret(ed, p, 2);
        ed.MarkSaved();
        Assert.False(ed.CanUndo); // precondition

        switch (cmd)
        {
            case "outdent": ed.Indent(-20); break;
            case "align": ed.SetTextAlignment(p.TextAlignment); break;
            case "spacing": ed.SetLineSpacing(p.LineSpacing); break;
            case "height": ed.SetLineHeight(p.LineHeight); break;
            case "removelist": ed.RemoveList(); break;
            case "replaceall": Assert.Equal(0, ed.ReplaceAll("absent", "x", false)); break;
        }

        Assert.False(ed.CanUndo);
        Assert.False(ed.IsModified);
    });

    [Fact]
    public void ACommandThatChangesSomething_StillLeavesAnUndoStep() => UiThread.Run(() =>
    {
        var p = P("hello world");
        var ed = Editor(p);
        Caret(ed, p, 2);
        ed.SetTextAlignment(Microsoft.UI.Xaml.TextAlignment.Center);
        Assert.True(ed.CanUndo);
    });

    // Upstream decision (2026-09-23): a block goes where the caret is — in the middle the paragraph splits, at its
    // start the block goes before it, at its end after it. This port always split, leaving an EMPTY paragraph ahead
    // of a block inserted at a paragraph's start, and behind one inserted at its end.
    [Theory]
    [InlineData(4, new[] { "top", "abcd", "DividerBlock", "efgh", "next" })]
    [InlineData(0, new[] { "top", "DividerBlock", "abcdefgh", "next" })]
    [InlineData(8, new[] { "top", "abcdefgh", "DividerBlock", "next" })]
    public void ADividerGoesWhereTheCaretIs(int offset, string[] expected) => UiThread.Run(() =>
    {
        var p = P("abcdefgh");
        // Paragraphs on both sides, so NormalizeBlocks has nothing to add and an extra empty one is the defect.
        var ed = Editor(P("top"), p, P("next"));
        Caret(ed, p, offset);

        ed.InsertDivider();

        Assert.Equal(expected, ed.Document!.Blocks.Select(Text).ToArray());
    });

    // Upstream decision (2026-09-23): Tab in a table's last cell adds a row to THAT table; it jumped into the next
    // table below unless the table was the document's last.
    [Fact]
    public void TabInTheLastCell_OfAnEarlierTable_AddsARow() => UiThread.Run(() =>
    {
        var first = new TableBlock(2, 2);
        var second = new TableBlock(2, 2);
        var ed = Editor(P("a"), first, P("between"), second);
        Caret(ed, first.Cells[1][1].Para, 0);

        typeof(Controls.RichEditor).GetMethod("HandleTab", NP)!.Invoke(ed, new object[] { false });

        Assert.Equal(3, first.Rows);
        Assert.Equal(2, second.Rows);
    });

    // Ctrl+drag CREATES a table or picture, which AllowTables / AllowImages forbid.
    [Fact]
    public void CopyDraggingATable_WithTablesOff_AddsNoTable() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 1);
        var target = P("drop here");
        var ed = Editor(P("top"), tb, target);
        ed.AllowTables = false;

        ed.DropObject(tb, new TextPointer(target, 9), copy: true);

        Assert.Single(ed.Document!.Blocks.OfType<TableBlock>());
    });

    [Fact]
    public void MoveDraggingATable_WithTablesOff_StillMovesIt() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 1);
        var target = P("drop here");
        var ed = Editor(P("top"), tb, target);
        ed.AllowTables = false;

        Assert.True(ed.DropObject(tb, new TextPointer(target, 9), copy: false));
        Assert.Single(ed.Document!.Blocks.OfType<TableBlock>());
    });

    private static readonly byte[] Png = System.Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");

    private static TextPointer CaretOf(Controls.RichEditor ed) => (TextPointer)typeof(Controls.RichEditor).GetField("_caret", NP)!.GetValue(ed)!;

    // By the document's own paragraph walk: a detached subtree keeps its Parent links, so climbing them from the
    // caret would still "reach" the document (that made the first draft of the test below pass vacuously).
    private static bool InDocument(Controls.RichEditor ed, Paragraph? p)
        => p != null && ((System.Collections.Generic.IEnumerable<Paragraph>)typeof(Controls.RichEditor)
            .GetMethod("AllParagraphs", NP)!.Invoke(ed, null)!).Contains(p);

    // Making an inline picture a block took its character out but left a caret that sat after it one past the
    // paragraph's end (upstream round 34).
    [Fact]
    public void MakingAnInlinePictureABlock_KeepsTheCaretInsideItsParagraph() => UiThread.Run(() =>
    {
        var img = new InlineImage { Width = 16, Height = 16 };
        img.SetImageData(Png, "image/png");
        var p = new Paragraph { Inlines = { new Run { Text = "ab" }, img } };
        var ed = Editor(p, P("next"));
        Caret(ed, p, 3); // after the picture

        ed.ConvertInlineImageToBlock(p, img);

        Assert.DoesNotContain(p.Inlines, i => i is InlineImage); // precondition: it went
        var c = CaretOf(ed);
        Assert.True(!ReferenceEquals(c.Paragraph, p) || c.Offset <= 2, $"caret at {c.Offset} in a 2-character paragraph");
    });

    // Unchecking "treat as character" re-creates the inline table as a block (a clone); the caret in the inline
    // original's cell was left there, in a paragraph no longer in the document (upstream round 34).
    [Fact]
    public void InlineTableToBlock_LeavesTheCaretInTheDocument() => UiThread.Run(() =>
    {
        var inner = new TableBlock(2, 2);
        var host = new Paragraph { Inlines = { new Run { Text = "before " }, new InlineTable { Table = inner }, new Run { Text = " after" } } };
        var ed = Editor(P("top"), host);
        var it = host.Inlines.OfType<InlineTable>().Single();
        Caret(ed, inner.Cells[0][0].Para, 0);

        ed.ConvertInlineTableToBlock(host, it);

        Assert.Contains(ed.Document!.Blocks, b => b is TableBlock); // precondition
        Assert.True(InDocument(ed, CaretOf(ed).Paragraph), "the caret is in a paragraph no longer in the document");
    });

    // Picking the margin already in force left an undo step that undid nothing (upstream round 34).
    [Fact]
    public void AMarginAlreadyInForce_LeavesNoUndoStep() => UiThread.Run(() =>
    {
        var p = P("text");
        var ed = Editor(p);
        ed.MarkSaved();
        Func<double> get = () => p.MarginBottom;
        Action<double> set = v => p.MarginBottom = v;

        typeof(Controls.RichEditor).GetMethod("PickMargin", NP)!.Invoke(ed, new object[] { get, set, p.MarginBottom });

        Assert.False(ed.CanUndo);
    });

    // "Original size" on a picture already at its natural size left an undo step that undid nothing.
    [Fact]
    public void OriginalSize_OnAPictureAtItsNaturalSize_LeavesNoUndoStep() => UiThread.Run(() =>
    {
        var img = new ImageBlock { Width = 1, Height = 1 }; // the PNG is 1x1
        img.SetImageData(Png, "image/png");
        var ed = Editor(P("top"), img);
        ed.MarkSaved();

        typeof(Controls.RichEditor).GetMethod("ResetBlockImageNatural", NP)!.Invoke(ed, new object[] { img });

        Assert.False(ed.CanUndo);
    });

    private static readonly Lazy<Controls.RichEditor> Hosted = new(() =>
    {
        var ed = UiThread.Run(() => new Controls.RichEditor { Document = new FlowDocument(), PageSize = Controls.RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, System.Threading.LazyThreadSafetyMode.ExecutionAndPublication);

    // A table held by its border stayed selected after a right-click on text elsewhere: the caret moved to the
    // text, but the table kept its selection (and Delete then removed it). The read-only branch already dropped
    // it ("a table shown selected earlier must not linger"); the editing branches did not (upstream round 34).
    [Fact]
    public void RightClickingText_DropsATableHeldByItsBorder()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(1, 2);
            var above = P("above the table");
            var doc = new FlowDocument();
            doc.Blocks.Add(above);
            doc.Blocks.Add(tb);
            ed.Document = doc;
            typeof(Controls.RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);
            typeof(Controls.RichEditor).GetField("_selectedBlock", NP)!.SetValue(ed, tb);
            Assert.True(ed.HasBlockSelection); // precondition

            ed.BuildContextMenuAt(new Windows.Foundation.Point(30, 8)); // on "above the table"

            Assert.Same(above, CaretOf(ed).Paragraph); // precondition: the right-click landed on the text
            Assert.False(ed.HasBlockSelection);
        });
    }
}
