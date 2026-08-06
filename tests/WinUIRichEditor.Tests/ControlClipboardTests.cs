using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Saves whatever was on the clipboard and puts the text back afterwards.
/// <para>These tests drive the REAL system clipboard, because that is the thing under test — the copy
/// path builds a <c>DataPackage</c> and hands it to Windows, and the paste path reads back whatever
/// Windows returns, including its own normalizations (LF becomes CRLF, which the internal-snapshot guard
/// depends on). A fake would test the fake.</para>
/// <para>⚠ Only the TEXT flavour is restored. Windows exposes no way to snapshot an arbitrary
/// DataPackage and put it back, so running this suite still costs you any richer clipboard content you
/// were holding.</para></summary>
public sealed class ClipboardGuard : IDisposable
{
    private readonly string? _saved;

    public ClipboardGuard()
        => _saved = UiThread.Run(() =>
        {
            try
            {
                var view = Clipboard.GetContent();
                return view.Contains(StandardDataFormats.Text)
                    ? view.GetTextAsync().AsTask().GetAwaiter().GetResult()
                    : null;
            }
            catch { return null; }
        });

    public void Dispose()
        => UiThread.Run(() =>
        {
            try
            {
                if (_saved == null) { Clipboard.Clear(); return; }
                var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                dp.SetText(_saved);
                Clipboard.SetContent(dp);
            }
            catch { /* restoring is best effort */ }
        });
}

/// <summary>The clipboard path: what a copy actually puts on the system clipboard, and which flavour a
/// paste picks up. Interoperability with Word and HWP rides entirely on this and had no automated
/// coverage — the RTF flavour exists because without it a copied table pastes into HWP as plain text,
/// and that was found by a person trying it.</summary>
[Collection(UiTests.Collection)]
public class ControlClipboardTests : IClassFixture<ClipboardGuard>
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    // Build the document FIRST and assign it: the Document setter is what wires Parent on every block.
    // Adding to Document.Blocks by hand leaves Parent null, and InsertDocumentAtCaret then silently takes
    // its plain-text fallback — a paste that does nothing, looking exactly like a product defect.
    private static RichEditor NewEditor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph());
        foreach (var b in blocks) doc.Blocks.Add(b);
        var ed = new RichEditor { Document = doc };
        var first = (Paragraph)ed.Document!.Blocks.First(b => b is Paragraph);
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(first, 0));
        return ed;
    }

    private static TableBlock Table2x2()
    {
        var tb = new TableBlock(2, 2);
        foreach (var (r, c, cell) in tb.LogicalCells())
            ((Run)cell.Para.Inlines[0]).Text = $"c{r}{c}";
        return tb;
    }

    private static void CopyBlock(RichEditor ed, Block block, string expect = "c00")
    {
        UiThread.RunAsync(() => (Task)T.GetMethod("CopyBlockToClipboard", NP)!.Invoke(ed, new object?[] { block })!);
        Settle(expect);
    }

    // Clipboard.SetContent hands the data to Windows and returns; a read that follows immediately can
    // still see the PREVIOUS contents. Flush() commits it (that is what makes copied data outlive the
    // owning process), and the retry covers the moment in between.
    //
    // It waits for the text WE just copied, not merely for "some text": the previous test left text
    // behind, so a presence check returns instantly while the new content is still in flight. That
    // narrowed the failures from two runs in six to one in eight and no further — the remaining one was
    // this exact hole. It is a race in the test, not in the copy path; a real user is slower than this.
    private static void Settle(string expect)
    {
        UiThread.Run(() => { try { Clipboard.Flush(); } catch { /* nothing to flush */ } });

        for (int attempt = 0; attempt < 80; attempt++)
        {
            if (UiThread.Run(() =>
            {
                try
                {
                    var view = Clipboard.GetContent();
                    return view.Contains(StandardDataFormats.Text)
                        && view.GetTextAsync().AsTask().GetAwaiter().GetResult().Contains(expect, StringComparison.Ordinal);
                }
                catch { return false; }
            }))
                return;
            System.Threading.Thread.Sleep(25);
        }
        throw new TimeoutException($"the clipboard never came to hold the copied text ('{expect}')");
    }

    private static string Flat(RichEditor ed)
        => string.Join(" ", ed.GetPlainText().Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    // ---- what a copy puts on the clipboard -----------------------------------------------------------

    // The RTF flavour is not decoration. HWP (and Word) import table structure from RTF far more reliably
    // than from HTML, and without it a copied table arrives there as plain text — which is how its
    // absence was originally noticed, by hand.
    [Fact]
    public void CopyingATable_PutsText_Html_AndRtf_OnTheClipboard()
    {
        var table = Table2x2();
        RichEditor ed = UiThread.Run(() => NewEditor(table));
        CopyBlock(ed, table);

        UiThread.Run(() =>
        {
            var view = Clipboard.GetContent();
            Assert.True(view.Contains(StandardDataFormats.Text), "no plain text flavour");
            Assert.True(view.Contains(StandardDataFormats.Html), "no CF_HTML flavour");
            Assert.True(view.Contains(StandardDataFormats.Rtf), "no RTF flavour — HWP/Word would get plain text");

            // Tab-separated, so Excel and Notepad get something meaningful.
            string text = view.GetTextAsync().AsTask().GetAwaiter().GetResult();
            Assert.Contains("c00\tc01", text);

            string rtf = view.GetRtfAsync().AsTask().GetAwaiter().GetResult();
            Assert.Contains(@"\trowd", rtf);      // a real RTF table, not just the text

            string fragment = HtmlFormatHelper.GetStaticFragment(
                view.GetHtmlFormatAsync().AsTask().GetAwaiter().GetResult());
            Assert.Contains("<table", fragment);
        });
    }

    // Plain text goes out with the platform newline. LF-only shows as ONE line in Notepad and native text
    // boxes — and the internal-snapshot guard compares what Windows hands back, which is CRLF, so getting
    // this wrong silently costs the high-fidelity in-app paste as well.
    [Fact]
    public void CopiedPlainText_UsesPlatformNewlines()
    {
        var table = Table2x2();
        RichEditor ed = UiThread.Run(() => NewEditor(table));
        CopyBlock(ed, table);

        string text = UiThread.Run(() =>
            Clipboard.GetContent().GetTextAsync().AsTask().GetAwaiter().GetResult());

        Assert.Contains("\r\n", text);
        Assert.DoesNotContain("\n\n", text.Replace("\r\n", "\n\n").Replace("\n\n", "\r\n")); // no bare LF left
    }

    // ---- what a paste picks up ------------------------------------------------------------------------

    // Into a DIFFERENT editor there is no internal snapshot, so this exercises the external path — the
    // same one Word and HWP content takes.
    [Fact]
    public void PastingIntoAnotherEditor_RebuildsTheTable_FromTheExternalFlavours()
    {
        var table = Table2x2();
        RichEditor source = UiThread.Run(() => NewEditor(table));
        CopyBlock(source, table);

        RichEditor target = UiThread.Run(() => NewEditor());
        UiThread.RunAsync(() => target.PasteAsync());

        UiThread.Run(() =>
        {
            Assert.Single(target.Document!.Blocks.OfType<TableBlock>());
            Assert.Equal("c00 c01 c10 c11", Flat(target));
        });
    }

    // Into the SAME editor the internal rich snapshot wins, which is the loss-free path.
    [Fact]
    public void PastingIntoTheSameEditor_UsesTheInternalSnapshot()
    {
        var table = Table2x2();
        RichEditor ed = UiThread.Run(() => NewEditor(table));
        CopyBlock(ed, table);

        UiThread.Run(() => Assert.NotNull(T.GetField("_internalClipboardDoc", NP)!.GetValue(ed)));
        UiThread.RunAsync(() => ed.PasteAsync());

        UiThread.Run(() =>
        {
            // The original table plus the pasted one.
            Assert.Equal(2, ed.Document!.Blocks.OfType<TableBlock>().Count());
            Assert.Equal("c00 c01 c10 c11 c00 c01 c10 c11", Flat(ed));
        });
    }

    // Ctrl+Shift+V. Every rich flavour is skipped even though all three are sitting on the clipboard.
    [Fact]
    public void PasteAsPlainText_SkipsEveryRichFlavour()
    {
        var table = Table2x2();
        RichEditor source = UiThread.Run(() => NewEditor(table));
        CopyBlock(source, table);

        RichEditor target = UiThread.Run(() => NewEditor());
        UiThread.RunAsync(() => target.PasteAsync(plainOnly: true));

        UiThread.Run(() =>
        {
            Assert.Empty(target.Document!.Blocks.OfType<TableBlock>());
            Assert.Contains("c00", Flat(target));   // the text still arrives
        });
    }

    // A copy of an image clears the internal snapshot: leaving it would let the PREVIOUS text copy hijack
    // the next paste, because the snapshot is chosen on a text match the image copy never invalidates.
    [Fact]
    public void CopyingAnImage_ClearsTheStaleInternalSnapshot()
    {
        byte[] png = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

        var table = Table2x2();
        RichEditor ed = UiThread.Run(() => NewEditor(table));
        CopyBlock(ed, table);
        UiThread.Run(() => Assert.NotNull(T.GetField("_internalClipboardDoc", NP)!.GetValue(ed)));

        UiThread.RunAsync(() => ed.CopyImageToClipboardAsync(png, null, false, 32, 32));

        UiThread.Run(() => Assert.Null(T.GetField("_internalClipboardDoc", NP)!.GetValue(ed)));
    }

    // ---- TSV detection (pure, no clipboard) -----------------------------------------------------------

    // The rule: every non-empty line must contain a tab, AND at least one line must have two or more
    // non-empty cells. The second half is what keeps tab-INDENTED prose from becoming a table — one cell
    // per line, however many lines. A single row of two cells is a legitimate 1x2 grid and does count.
    [Theory]
    [InlineData("a\tb\nc\td", true)]
    [InlineData("a\tb\tc\nd\te\tf", true)]
    [InlineData("one line\twith a tab", true)]        // 1x2 is still a grid
    [InlineData("a\tb\n\nc\td", true)]                // blank lines are skipped, not disqualifying
    [InlineData("just some prose", false)]            // no tab at all
    [InlineData("\tindented\n\tprose", false)]        // tab-indented prose: one cell per line
    [InlineData("a\tb\nno tab here", false)]          // one line without a tab disqualifies the lot
    [InlineData("", false)]
    public void LooksTabular_RecognizesSpreadsheetText(string text, bool expected)
        => Assert.Equal(expected, RichEditor.LooksTabular(text));
}
