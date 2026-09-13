using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The capability flags: <c>AllowImages</c>, <c>AllowTables</c>, <c>AllowRichPaste</c>,
/// <c>AllowFindReplace</c>, the two privacy flags <c>AllowRemoteImagesOnPaste</c> and
/// <c>AllowLocalFileImages</c>, and the <c>MaxRecommendedImages</c> soft limit.
/// <para>About 63 branches in the control read these flags and not one test referenced any of them. A gate
/// that stops gating has no symptom — the feature just keeps working — so this is exactly the kind of
/// axis nothing else would ever notice. The two privacy flags are the heaviest: they promise that pasting
/// a web page makes no network request and that HTML reads no local files, and those promises are checked
/// here by COUNTING — connections to a loopback server, images that came out of a file — with the flag on
/// as the control that proves each probe can see anything at all.</para>
/// <para>Ported from upstream: <c>FeatureFlagTests</c> (8) and <c>ImageLimitTests</c> (5). The image
/// entry points, both privacy gates and the toolbar reflection had no test on either side.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlFeatureFlagTests : IClassFixture<ClipboardGuard>
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    // ---- plumbing ---------------------------------------------------------------------------------

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    // Built first, then assigned (the Document setter wires Parent — see ControlUndoTests), caret at the
    // end of the document the way FocusDocumentEnd leaves it.
    private static RichEditor Editor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        var ed = new RichEditor { Document = doc };
        ed.FocusDocumentEnd();
        return ed;
    }

    private static void Call(object target, string name, params object?[] args)
    {
        var m = target.GetType().GetMethod(name, NP) ?? throw new MissingMethodException(target.GetType().Name, name);
        try { m.Invoke(target, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private static int Images(FlowDocument doc) => Count(doc.Blocks, b => b is ImageBlock, i => i is InlineImage);
    private static int Tables(FlowDocument doc) => Count(doc.Blocks, b => b is TableBlock, i => i is InlineTable);

    // Every block and inline at any depth — cells of block tables and of inline tables included.
    private static int Count(IEnumerable<Block> blocks, Func<Block, bool> block, Func<Inline, bool> inline)
    {
        int n = 0;
        foreach (var b in blocks)
        {
            if (block(b)) n++;
            if (b is Paragraph p)
                foreach (var i in p.Inlines)
                {
                    if (inline(i)) n++;
                    if (i is InlineTable it)
                        foreach (var (_, _, cell) in it.Table.LogicalCells()) n += Count(cell.Blocks, block, inline);
                }
            else if (b is TableBlock tb)
                foreach (var (_, _, cell) in tb.LogicalCells()) n += Count(cell.Blocks, block, inline);
        }
        return n;
    }

    // Turns off the rich-content flags while staying editable (upstream's former "Basic" preset).
    private static void MakeBasic(RichEditor ed)
    {
        ed.AllowImages = false;
        ed.AllowTables = false;
        ed.AllowRichPaste = false;
        ed.AllowFindReplace = false;
    }

    // ---- ported: upstream FeatureFlagTests ---------------------------------------------------------

    [Fact]
    public void Default_HasAllFlagsEnabled_AndIsEditable() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        Assert.True(ed.AllowImages);
        Assert.True(ed.AllowTables);
        Assert.True(ed.AllowRichPaste);
        Assert.True(ed.AllowFindReplace);
        Assert.True(ed.AllowRemoteImagesOnPaste);
        Assert.True(ed.AllowLocalFileImages);
        Assert.Equal(50, ed.MaxRecommendedImages);
        Assert.False(ed.IsReadOnly);
    });

    [Fact]
    public void ClearingRichFlags_KeepsEditable() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        MakeBasic(ed);
        Assert.False(ed.AllowImages);
        Assert.False(ed.AllowTables);
        Assert.False(ed.AllowRichPaste);
        Assert.False(ed.AllowFindReplace);
        Assert.False(ed.IsReadOnly);
    });

    [Fact]
    public void AllowTablesFalse_BlocksBothTableInserts() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        MakeBasic(ed);

        ed.InsertTable(2, 2);
        ed.InsertInlineTable(1, 2); // the port has a second entry point upstream's test didn't cover

        Assert.Equal(0, Tables(ed.Document!));
        Assert.False(ed.CanUndo); // refused before the checkpoint, not undone after it
    });

    [Fact]
    public void ClearedFlags_StillAllowTextInput() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        MakeBasic(ed);

        ed.InsertText("XY");

        Assert.Equal("abcXY", ed.GetPlainText().Trim());
    });

    [Fact]
    public void AllowFindReplaceFalse_DisablesFindAndReplace() => UiThread.Run(() =>
    {
        var ed = Editor(P("hello hello"));
        MakeBasic(ed);

        Assert.False(ed.FindNext("hello", matchCase: false));
        Assert.False(ed.FindPrev("hello", matchCase: false));
        Assert.False(ed.ReplaceNext("hello", "x", matchCase: false));
        Assert.Equal(0, ed.ReplaceAll("hello", "x", matchCase: false));
        Assert.Equal("hello hello", ed.GetPlainText().Trim());
    });

    [Fact]
    public void IndividualFlag_TakesEffect() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        MakeBasic(ed);          // tables off
        ed.AllowTables = true;  // ...then just tables back on

        ed.InsertTable(2, 2);

        Assert.Equal(1, Tables(ed.Document!));
    });

    [Fact]
    public void ReadOnly_ClearsUndoHistory() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        ed.InsertTable(2, 2);
        Assert.True(ed.CanUndo);

        ed.IsReadOnly = true;

        Assert.False(ed.CanUndo);
    });

    [Fact]
    public void ReadOnly_BlocksInsertHtml() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        ed.IsReadOnly = true;

        ed.InsertHtml("<p>injected</p>");

        Assert.DoesNotContain("injected", ed.GetPlainText());
    });

    // ---- AllowImages: every public way in ----------------------------------------------------------

    // InsertInlineImage checked the flag; its block twin InsertImageBlock (and the InsertImageBytes alias
    // that forwards to it) did not — a host that turned images off still got them through the API. The
    // paste path was covered only because its CALLER checks the flag first. Upstream gates InsertImageBytes.
    [Theory]
    [InlineData("InsertImageBlock")]
    [InlineData("InsertImageBytes")]
    [InlineData("InsertInlineImage")]
    public void AllowImagesFalse_RefusesEveryImageInsert(string entry) => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        ed.AllowImages = false;

        switch (entry)
        {
            case "InsertImageBlock": ed.InsertImageBlock(TinyPng, "image/png"); break;
            case "InsertImageBytes": ed.InsertImageBytes(TinyPng); break;
            default: ed.InsertInlineImage(TinyPng, "image/png"); break;
        }

        Assert.Equal(0, Images(ed.Document!));
        Assert.False(ed.CanUndo, $"{entry} left an undo step for an insert it should have refused");
    });

    // The control for the theory above: with the flag on, each entry point really does insert — otherwise
    // "no image" would pass for an entry point that is simply broken.
    [Theory]
    [InlineData("InsertImageBlock")]
    [InlineData("InsertImageBytes")]
    [InlineData("InsertInlineImage")]
    public void AllowImagesTrue_EachImageInsertWorks(string entry) => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));

        switch (entry)
        {
            case "InsertImageBlock": ed.InsertImageBlock(TinyPng, "image/png"); break;
            case "InsertImageBytes": ed.InsertImageBytes(TinyPng); break;
            default: ed.InsertInlineImage(TinyPng, "image/png"); break;
        }

        Assert.Equal(1, Images(ed.Document!));
    });

    // ---- AllowImages / AllowTables on INCOMING content (paste, InsertHtml) --------------------------
    //
    // The rich paste paths checked only AllowRichPaste, so a host that turned images or tables off still
    // received them from any web page or Word document pasted in (measured: all four shapes below came
    // through). Decided 2026-09-12: pictures are dropped, tables are unwrapped into their cells' text.
    // Each of the four splice sites — InsertHtml, HTML paste, RTF paste, in-app snapshot paste — has its
    // own test, so a site that forgets the adaptation is caught on its own.

    private const string PngUri = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=";

    private static string Flat(RichEditor ed) => ed.GetPlainText().Replace("\r", "").Replace("\n", "|");

    [Theory]
    [InlineData("inline", "<p>AAA<img src=\"{0}\" width=\"16\" height=\"16\">BBB</p>")]
    [InlineData("block", "<p>AAA</p><img src=\"{0}\" width=\"300\" height=\"300\"><p>BBB</p>")]
    [InlineData("in a cell", "<p>AAA</p><table><tr><td>BBB<img src=\"{0}\" width=\"16\" height=\"16\"></td></tr></table>")]
    public void InsertHtml_WithImagesOff_DropsThePictures_AndKeepsTheText(string shape, string html) => UiThread.Run(() =>
    {
        var ed = Editor(P("start"));
        ed.AllowImages = false;

        ed.InsertHtml(string.Format(html, PngUri));

        Assert.True(Images(ed.Document!) == 0, $"a picture ({shape}) came through with AllowImages off");
        Assert.Contains("AAA", Flat(ed));
        Assert.Contains("BBB", Flat(ed));
    });

    [Fact]
    public void InsertHtml_WithTablesOff_UnwrapsTheTable_IntoItsCellsText() => UiThread.Run(() =>
    {
        var ed = Editor(P("start"));
        ed.AllowTables = false;

        ed.InsertHtml("<p>AAA</p><table><tr><td>C1</td><td>C2</td></tr><tr><td>C3</td><td></td></tr></table><p>BBB</p>");

        Assert.Equal(0, Tables(ed.Document!));
        string text = Flat(ed);
        int a = text.IndexOf("AAA"), c1 = text.IndexOf("C1"), c2 = text.IndexOf("C2"), c3 = text.IndexOf("C3"), b = text.IndexOf("BBB");
        Assert.True(a >= 0 && a < c1 && c1 < c2 && c2 < c3 && c3 < b, $"reading order lost: '{text}'");
        Assert.DoesNotContain("||", text.Substring(c1, b - c1)); // the empty cell added no blank line
    });

    [Fact]
    public void InsertHtml_WithBothOff_AnImageInACell_LeavesJustTheCellsText() => UiThread.Run(() =>
    {
        var ed = Editor(P("start"));
        ed.AllowImages = false;
        ed.AllowTables = false;

        ed.InsertHtml($"<table><tr><td>XXX<img src=\"{PngUri}\" width=\"16\" height=\"16\"></td></tr></table>");

        Assert.Equal(0, Images(ed.Document!));
        Assert.Equal(0, Tables(ed.Document!));
        Assert.Contains("XXX", Flat(ed));
    });

    // Content the flags remove entirely inserts nothing: no empty undo step, no modified flag. Both shapes:
    // a large picture parses as a BLOCK (removing it empties the block list, which the old "no blocks"
    // check already caught), a small one as an INLINE in a paragraph (removing it leaves a blank
    // paragraph — only the `emptied` check stops that; the first version tested just the block shape and
    // a falsification run showed the check could be deleted with the test still green).
    [Theory]
    [InlineData("block", 300)]
    [InlineData("inline", 16)]
    public void InsertHtml_OfOnlyAPicture_WithImagesOff_LeavesNoTrace(string shape, int size) => UiThread.Run(() =>
    {
        var ed = Editor(P("start"));
        ed.AllowImages = false;
        ed.MarkSaved();

        ed.InsertHtml($"<p><img src=\"{PngUri}\" width=\"{size}\" height=\"{size}\"></p>");
        Assert.True(!ed.CanUndo, $"{shape}: an undo step was left behind");

        Assert.False(ed.CanUndo);
        Assert.False(ed.IsModified);
        Assert.Equal("start", ed.GetPlainText().Trim());
    });

    // The controls: with the flags on nothing is adapted, and OPENING a document is never adapted — the
    // flags restrict what can be added, not what an existing document may contain.
    [Fact]
    public void InsertHtml_WithTheFlagsOn_KeepsPicturesAndTables() => UiThread.Run(() =>
    {
        var ed = Editor(P("start"));

        ed.InsertHtml($"<p>AAA</p><img src=\"{PngUri}\" width=\"300\" height=\"300\"><table><tr><td>C1</td></tr></table><p>BBB</p>");

        Assert.Equal(1, Images(ed.Document!));
        Assert.Equal(1, Tables(ed.Document!));
    });

    [Fact]
    public void LoadHtml_WithTheFlagsOff_StillOpensPicturesAndTables() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument(), AllowImages = false, AllowTables = false };

        ed.LoadHtml($"<p>AAA</p><img src=\"{PngUri}\" width=\"300\" height=\"300\"><table><tr><td>C1</td></tr></table>");

        Assert.Equal(1, Images(ed.Document!));
        Assert.Equal(1, Tables(ed.Document!));
    });

    [Fact]
    public void PastingHtml_WithBothOff_DropsPicturesAndUnwrapsTables()
    {
        PutHtmlOnClipboard($"<p><b>viahtml</b></p><table><tr><td>C1</td></tr></table><img src=\"{PngUri}\" width=\"300\" height=\"300\">",
                           plainText: "viatext");
        string text = "";
        int images = -1, tables = -1;
        UiThread.RunAsync(async () =>
        {
            var ed = Editor(P("start"));
            ed.AllowImages = false;
            ed.AllowTables = false;
            await ed.PasteAsync();
            text = Flat(ed); images = Images(ed.Document!); tables = Tables(ed.Document!);
        });

        Assert.Contains("viahtml", text);
        Assert.DoesNotContain("viatext", text);
        Assert.Contains("C1", text);
        Assert.Equal(0, images);
        Assert.Equal(0, tables);
    }

    [Fact]
    public void PastingRtf_WithTablesOff_UnwrapsTheTable()
    {
        const string rtf = @"{\rtf1\ansi \trowd\cellx1500\cellx3000 R1\cell R2\cell\row \pard viartf\par}";
        PutOnClipboard(dp => { dp.SetRtf(rtf); dp.SetText("viatext"); },
                       view => view.Contains(StandardDataFormats.Rtf));
        string text = "";
        int tables = -1;
        UiThread.RunAsync(async () =>
        {
            var ed = Editor(P("start"));
            ed.AllowTables = false;
            await ed.PasteAsync();
            text = Flat(ed); tables = Tables(ed.Document!);
        });

        Assert.Contains("viartf", text);         // the RTF flavour was the one pasted
        Assert.DoesNotContain("viatext", text);
        Assert.Contains("R1", text);
        Assert.Contains("R2", text);
        Assert.Equal(0, tables);
    }

    // The in-app snapshot (copy in this editor, paste back): it carries the editor's own objects, an inline
    // table and an inline picture here. With the flags then off, the pasted COPY is adapted — the
    // originals are untouched (they are existing content, not an insert).
    [Fact]
    public void PastingAnInAppCopy_WithBothOff_AdaptsTheCopy_AndLeavesTheOriginals()
    {
        RichEditor? ed = null;
        string? copied = null;
        UiThread.Run(() =>
        {
            ed = Editor(P("AAA"));
            ed.InsertInlineTable(1, 2);
            var it = ed.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<InlineTable>()).Single();
            ((Run)it.Table.Cells[0][0].Para.Inlines[0]).Text = "T1";
            ((Run)it.Table.Cells[0][1].Para.Inlines[0]).Text = "T2";
            ed.InsertInlineImage(TinyPng, "image/png");
            var host = ed.Document.Blocks.OfType<Paragraph>().First(p => p.Inlines.OfType<InlineTable>().Any());
            int len = host.Inlines.Sum(i => i is Run r ? (r.Text?.Length ?? 0) : 1);
            T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(host, 0));
            T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(host, len));
            T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(host, len));
        });
        for (int attempt = 0; attempt < 10 && copied == null; attempt++)
        {
            try
            {
                UiThread.RunAsync(() => ed!.CopyAsync());
                string? want = UiThread.Run(() => (string?)T.GetField("_internalClipboardText", NP)!.GetValue(ed));
                for (int i = 0; i < 20; i++)
                {
                    string? got = UiThread.Run(() =>
                    {
                        var v = Clipboard.GetContent();
                        return v.Contains(StandardDataFormats.Text) ? v.GetTextAsync().AsTask().GetAwaiter().GetResult() : null;
                    });
                    if (want != null && got == want) { copied = got; break; }
                    Thread.Sleep(50);
                }
            }
            catch { Thread.Sleep(100); }
        }
        Assert.NotNull(copied);

        int images = -1, tables = -1;
        bool flattened = false;
        UiThread.RunAsync(async () =>
        {
            ed!.AllowImages = false;
            ed.AllowTables = false;
            ed.FocusDocumentEnd();
            await ed.PasteAsync();
            images = Images(ed.Document!);
            tables = Tables(ed.Document!);
            flattened = ed.Document!.Blocks.OfType<Paragraph>()
                .SelectMany(p => p.Inlines.OfType<Run>()).Any(r => r.Text != null && r.Text.Contains("T1 T2"));
        });

        Assert.Equal(1, images);   // the original picture only — the copy's was dropped
        Assert.Equal(1, tables);   // the original inline table only — the copy's was unwrapped...
        Assert.True(flattened, "...into its cells' text, in place");
    }

    // The general clipboard writer behind PutHtmlOnClipboard: retried until the flavour reads back.
    private static void PutOnClipboard(Action<DataPackage> fill, Func<DataPackageView, bool> landed)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                UiThread.Run(() =>
                {
                    var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                    fill(dp);
                    Clipboard.SetContent(dp);
                    Clipboard.Flush();
                });
                if (UiThread.Run(() => landed(Clipboard.GetContent()))) return;
            }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the clipboard could not be written in 10 attempts", last);
    }

    // ---- AllowFindReplace: the shortcuts ------------------------------------------------------------

    // Ctrl+F / Ctrl+H ask the host to open its find UI through FindRequested. With the flag off, the
    // shortcuts must not ask. RunShortcut is what the key handler calls once a shortcut matched.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheFindShortcuts_AskForTheFindUi_OnlyWhenAllowed(bool allowed) => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        ed.AllowFindReplace = allowed;
        int asked = 0;
        ed.FindRequested += (_, _) => asked++;
        var shortcutType = T.Assembly.GetType("WinUIRichEditor.Controls.ShortcutId")!;

        Call(ed, "RunShortcut", Enum.Parse(shortcutType, "Find"));
        Call(ed, "RunShortcut", Enum.Parse(shortcutType, "FindReplace"));

        Assert.Equal(allowed ? 2 : 0, asked);
    });

    // ---- AllowRemoteImagesOnPaste: the network, counted ---------------------------------------------

    // A loopback endpoint that counts the connections made to it and answers each with a PNG. Counting is
    // the point: "no image appeared" would also pass for a fetch that was made and then failed.
    private sealed class PixelServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private int _hits;

        public PixelServer()
        {
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/pixel.png";
        public int Hits => Volatile.Read(ref _hits);

        private async Task ServeAsync()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync().ConfigureAwait(false);
                    Interlocked.Increment(ref _hits);
                    var stream = client.GetStream();
                    var buffer = new byte[8192];
                    await stream.ReadAsync(buffer).ConfigureAwait(false); // the request itself is irrelevant
                    var head = System.Text.Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {TinyPng.Length}\r\nConnection: close\r\n\r\n");
                    await stream.WriteAsync(head).ConfigureAwait(false);
                    await stream.WriteAsync(TinyPng).ConfigureAwait(false);
                }
            }
            catch { /* the listener was stopped */ }
        }

        public void Dispose() => _listener.Stop();
    }

    // LoadHtmlAsync is the entry that downloads remote images, and it takes the same flag as paste.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowRemoteImagesOnPaste_DecidesWhetherLoadHtmlAsyncGoesToTheNetwork(bool allowed)
    {
        using var server = new PixelServer();
        int images = -1;
        UiThread.RunAsync(async () =>
        {
            var ed = new RichEditor { Document = new FlowDocument(), AllowRemoteImagesOnPaste = allowed };
            await ed.LoadHtmlAsync($"<p>x<img src=\"{server.Url}\"></p>");
            images = Images(ed.Document!);
        });

        if (allowed)
        {
            Assert.True(server.Hits >= 1, "control: with the flag on the image must be fetched, or this probe sees nothing");
            Assert.Equal(1, images);
        }
        else
        {
            Assert.Equal(0, server.Hits);
            Assert.Equal(0, images);
        }
    }

    // The case the flag exists for: pasting a web page (a tracking pixel is exactly this) through the real
    // clipboard and the real paste path.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowRemoteImagesOnPaste_DecidesWhetherPastingHtmlGoesToTheNetwork(bool allowed)
    {
        using var server = new PixelServer();
        PutHtmlOnClipboard($"<p><b>viahtml</b><img src=\"{server.Url}\"></p>", plainText: "viatext");
        string text = "";
        int images = -1;
        UiThread.RunAsync(async () =>
        {
            var ed = Editor(P("start"));
            ed.AllowRemoteImagesOnPaste = allowed;
            await ed.PasteAsync();
            text = ed.GetPlainText();
            images = Images(ed.Document!);
        });

        Assert.Contains("viahtml", text);        // the HTML flavour was the one pasted...
        Assert.DoesNotContain("viatext", text);  // ...not the plain-text fallback
        if (allowed)
        {
            Assert.True(server.Hits >= 1, "control: with the flag on the pasted image must be fetched");
            Assert.Equal(1, images);
        }
        else
        {
            Assert.Equal(0, server.Hits);
            Assert.Equal(0, images);
        }
    }

    // The clipboard is a lock shared with other processes; the whole write is retried until the HTML we
    // just put there reads back (see ControlClipboardTests for why a single attempt is not enough).
    private static void PutHtmlOnClipboard(string fragment, string plainText)
    {
        Exception? last = null;
        for (int attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                UiThread.Run(() =>
                {
                    var dp = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
                    dp.SetHtmlFormat(HtmlFormatHelper.CreateHtmlFormat(fragment));
                    dp.SetText(plainText);
                    Clipboard.SetContent(dp);
                    Clipboard.Flush();
                });
                bool ok = UiThread.Run(() =>
                {
                    var view = Clipboard.GetContent();
                    return view.Contains(StandardDataFormats.Html)
                        && view.GetHtmlFormatAsync().AsTask().GetAwaiter().GetResult().Contains(fragment);
                });
                if (ok) return;
            }
            catch (Exception ex) { last = ex; }
            Thread.Sleep(100);
        }
        throw new InvalidOperationException("the HTML could not be put on the clipboard in 10 attempts", last);
    }

    // ---- AllowLocalFileImages: local files, counted -------------------------------------------------

    private static string WritePng(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, TinyPng);
        return path;
    }

    private static bool UnderTemp(string path)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllowLocalFileImages_DecidesWhetherHtmlReadsALocalFile(bool allowed) => UiThread.Run(() =>
    {
        string path = WritePng(AppContext.BaseDirectory, "flag-probe-local.png");
        Assert.False(UnderTemp(path), "the probe file must live OUTSIDE %TEMP% (temp has its own rule)");
        var ed = Editor(P("abc"));
        ed.AllowLocalFileImages = allowed;

        ed.InsertHtml($"<p>x<img src=\"{new Uri(path).AbsoluteUri}\"></p>");

        Assert.Equal(allowed ? 1 : 0, Images(ed.Document!));
    });

    // The paste path reads Word/HWP's clipboard pictures from %TEMP% even though it blocks other local
    // files — the copy itself just wrote them there. That exception is for PASTE. A host that sets
    // AllowLocalFileImages=false for LoadHtml/InsertHtml is told "file:// sources are skipped"; reading
    // whatever sits under %TEMP% (other applications' temp files included) breaks that promise.
    // Upstream has no temp exception at all.
    [Fact]
    public void AllowLocalFileImagesFalse_ReadsNothingUnderTempEither() => UiThread.Run(() =>
    {
        string path = WritePng(Path.Combine(Path.GetTempPath(), "WinUIRichEditor.Tests"), "flag-probe-temp.png");
        Assert.True(UnderTemp(path));
        var ed = Editor(P("abc"));
        ed.AllowLocalFileImages = false;

        ed.InsertHtml($"<p>x<img src=\"{new Uri(path).AbsoluteUri}\"></p>");

        Assert.Equal(0, Images(ed.Document!));
    });

    // ---- the toolbar follows the flags --------------------------------------------------------------

    private static Button? Btn(RichEditorToolbar tb, string field)
        => (Button?)typeof(RichEditorToolbar).GetField(field, NP)!.GetValue(tb);

    // A host that turns a capability off at run time must not keep showing its button. The toolbar only
    // re-read the flags on the editor's StatusChanged, which a flag change does not raise — so the button
    // stayed until the next keystroke. Upstream subscribes to the property change itself.
    [Theory]
    [InlineData("AllowTables", "_tableBtn")]
    [InlineData("AllowImages", "_imageBtn")]
    [InlineData("AllowFindReplace", "_findBtn")]
    public void TurningAFlagOff_HidesItsToolbarButton_Immediately(string flag, string button) => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        var toolbar = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        var btn = Btn(toolbar, button);
        Assert.NotNull(btn);
        Assert.Equal(Visibility.Visible, btn!.Visibility);

        T.GetProperty(flag)!.SetValue(ed, false);

        Assert.Equal(Visibility.Collapsed, btn.Visibility);
    });

    // The divider belongs to the insert group: shown while tables OR images are allowed, like the context
    // menu's divider item (and upstream's toolbar). The toolbar never touched its visibility at all.
    [Fact]
    public void TheDividerButton_HidesWhenNeitherTablesNorImagesAreAllowed() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        var toolbar = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        var divider = Btn(toolbar, "_dividerBtn");
        Assert.NotNull(divider);

        ed.AllowTables = false;
        ed.AllowImages = false;
        ed.InsertText("x"); // a status change, so this does not depend on the immediate-reflection fix
        Assert.Equal(Visibility.Collapsed, divider!.Visibility);

        ed.AllowImages = true;
        ed.InsertText("y");
        Assert.Equal(Visibility.Visible, divider.Visibility);
    });

    // ---- MaxRecommendedImages (ported: upstream ImageLimitTests) ------------------------------------

    private static FlowDocument DocWithImages(int blockImages, int inlineImages = 0, int tableCellImages = 0)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < blockImages; i++) doc.Blocks.Add(new ImageBlock());
        if (inlineImages > 0)
        {
            var p = new Paragraph();
            for (int i = 0; i < inlineImages; i++) p.Inlines.Add(new InlineImage());
            doc.Blocks.Add(p);
        }
        if (tableCellImages > 0)
        {
            var t = new TableBlock(1, 1);
            for (int i = 0; i < tableCellImages; i++) t.Cells[0][0].Para.Inlines.Add(new InlineImage());
            doc.Blocks.Add(t);
        }
        return doc;
    }

    [Fact]
    public void GetImageCount_CountsBlockInlineAndTableCellImages() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = DocWithImages(blockImages: 2, inlineImages: 3, tableCellImages: 1) };
        Assert.Equal(6, ed.GetImageCount());
    });

    [Fact]
    public void ExceedingTheLimit_RaisesTheWarningOnce() => UiThread.Run(() =>
    {
        var ed = new RichEditor { MaxRecommendedImages = 2, Document = DocWithImages(3) };
        int fired = 0;
        ed.RecommendedImageLimitExceeded += (_, _) => fired++;

        Call(ed, "CheckImageLimit");
        Call(ed, "CheckImageLimit"); // still over: no second warning
        Assert.Equal(1, fired);
    });

    [Fact]
    public void DroppingBackToTheLimit_RearmsTheWarning() => UiThread.Run(() =>
    {
        var ed = new RichEditor { MaxRecommendedImages = 2, Document = DocWithImages(3) };
        int fired = 0;
        ed.RecommendedImageLimitExceeded += (_, _) => fired++;

        Call(ed, "CheckImageLimit");
        ed.Document!.Blocks.Remove(ed.Document.Blocks.First(b => b is ImageBlock)); // back to 2 == limit
        Call(ed, "CheckImageLimit");
        ed.Document.Blocks.Add(new ImageBlock()); // over again
        Call(ed, "CheckImageLimit");
        Assert.Equal(2, fired);
    });

    [Fact]
    public void AtOrUnderTheLimit_DoesNotWarn() => UiThread.Run(() =>
    {
        var ed = new RichEditor { MaxRecommendedImages = 3, Document = DocWithImages(3) };
        int fired = 0;
        ed.RecommendedImageLimitExceeded += (_, _) => fired++;

        Call(ed, "CheckImageLimit");
        Assert.Equal(0, fired);
    });

    [Fact]
    public void AZeroLimit_DisablesTheWarning() => UiThread.Run(() =>
    {
        var ed = new RichEditor { MaxRecommendedImages = 0, Document = DocWithImages(100) };
        int fired = 0;
        ed.RecommendedImageLimitExceeded += (_, _) => fired++;

        Call(ed, "CheckImageLimit");
        Assert.Equal(0, fired);
    });

    // The ported tests call CheckImageLimit directly. This one goes through the product: an ordinary
    // insert's AfterEdit is what runs the check.
    [Fact]
    public void InsertingPastTheLimit_RaisesTheWarningThroughTheEditPath() => UiThread.Run(() =>
    {
        var ed = Editor(P("abc"));
        ed.MaxRecommendedImages = 1;
        int fired = 0;
        ed.RecommendedImageLimitExceeded += (_, _) => fired++;

        ed.InsertInlineImage(TinyPng, "image/png");
        Assert.Equal(0, fired); // at the limit
        ed.InsertInlineImage(TinyPng, "image/png");
        Assert.Equal(1, fired); // past it
    });
}
