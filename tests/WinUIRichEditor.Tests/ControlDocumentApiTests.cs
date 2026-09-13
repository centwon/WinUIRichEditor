using System;
using System.Collections;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The document API wrappers — <c>LoadJson</c>/<c>LoadJsonAsync</c>/<c>ToJson</c>/<c>ToJsonAsync</c>,
/// <c>SavePackageAsync</c>/<c>LoadPackageAsync</c>, <c>Clear</c> — and what a load does to the editor.
/// <para>The formatters under these are well covered (round trips, the model fuzz); the wrappers were not
/// called by a single test. Their contract is the one that protects a user's file: input that is not a
/// document of this library is REPORTED and the open document is left exactly as it was — same instance,
/// same text, same undo history, same modified flag — because replacing it with an empty document marks it
/// saved and the next save writes the blank over the original. Measured before this file: a foreign JSON
/// object, <c>{}</c>, and a zip with no document.json (a .docx) all did exactly that.</para>
/// <para>Also pinned: what a load resets (history, modified flag, the format painter, a pending caret
/// style) and where a plain document's page setup comes from (the host's, not the previous document's).
/// Round trips ported from upstream's <c>RichEditorControlTests</c>.</para>
/// </summary>
[Collection(UiTests.Collection)]
public class ControlDocumentApiTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private const BindingFlags NS = BindingFlags.NonPublic | BindingFlags.Static;
    private static readonly Type T = typeof(RichEditor);

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static FlowDocument Doc(params Block[] blocks)
    {
        var d = new FlowDocument();
        foreach (var b in blocks) d.Blocks.Add(b);
        return d;
    }

    private static ImageBlock Image()
    {
        var ib = new ImageBlock { Width = 20, Height = 20 };
        ib.SetImageData(TinyPng, "image/png");
        return ib;
    }

    // An open document with something to lose: an undo step and the modified flag.
    private static RichEditor Open(string text)
    {
        var ed = new RichEditor { Document = Doc(P(text)) };
        ed.FocusDocumentEnd();
        ed.InsertText("!");
        return ed;
    }

    private static void AssertUntouched(RichEditor ed, FlowDocument before, string text)
    {
        Assert.Same(before, ed.Document);
        Assert.Equal(text, ed.GetPlainText().Trim());
        Assert.True(ed.CanUndo, "the open document's undo history was lost");
        Assert.True(ed.IsModified, "the open document was marked saved");
    }

    private static string Shape(RichEditor ed) => DocumentFuzzTests.Shape(ed.Document!);

    // ---- round trips (ported: upstream RichEditorControlTests) --------------------------------------

    [Fact]
    public void ToJson_LoadJson_RoundTrips() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>data <b>bold</b></p><table><tr><td>c</td></tr></table>");

        var ed2 = new RichEditor();
        ed2.LoadJson(ed.ToJson());

        Assert.Equal(Shape(ed), Shape(ed2));
    });

    [Fact]
    public void ToJsonAsync_LoadJsonAsync_RoundTrips_WithAnImage() => UiThread.RunAsync(async () =>
    {
        var ed = new RichEditor { Document = Doc(P("async data"), Image(), P("end")) };

        string json = await ed.ToJsonAsync();
        var ed2 = new RichEditor();
        await ed2.LoadJsonAsync(json);

        Assert.Equal(Shape(ed), Shape(ed2));
        Assert.Equal("image/png", ed2.Document!.Blocks.OfType<ImageBlock>().Single().MimeType);
    });

    // The snapshot is taken at the call, on the UI thread — edits made while encoding runs can't tear it.
    [Fact]
    public void ToJsonAsync_TakesItsSnapshotAtTheCall() => UiThread.RunAsync(async () =>
    {
        var ed = new RichEditor { Document = Doc(P("before")) };
        ed.FocusDocumentEnd();

        var task = ed.ToJsonAsync();
        ed.InsertText("-after");
        string json = await task;

        Assert.Contains("before", json);
        Assert.DoesNotContain("-after", json);
    });

    [Fact]
    public void SavePackageAsync_LoadPackageAsync_RoundTrips_WithAnImage() => UiThread.RunAsync(async () =>
    {
        var ed = new RichEditor { Document = Doc(P("packaged"), Image(), P("end")) };
        var ms = new MemoryStream();

        await ed.SavePackageAsync(ms);
        ms.Position = 0;
        var ed2 = new RichEditor();
        await ed2.LoadPackageAsync(ms);

        Assert.Equal(Shape(ed), Shape(ed2));
    });

    // ---- input that is not a document leaves the open one alone -------------------------------------

    [Theory]
    [InlineData("array", "[]")]
    [InlineData("empty string", "")]
    [InlineData("truncated", "{\"Version\":\"1.0\",\"Blocks\":[")]
    [InlineData("an empty object", "{}")]
    [InlineData("another application's JSON", "{\"name\":\"pkg\",\"version\":\"1.2.3\",\"dependencies\":{}}")]
    public void LoadJson_RejectsWhatIsNotADocument_AndLeavesTheOpenOneAlone(string what, string json) => UiThread.Run(() =>
    {
        var ed = Open("ORIGINAL");
        var before = ed.Document!;

        var ex = Record.Exception(() => ed.LoadJson(json));

        Assert.True(ex is JsonException, $"{what}: expected a JsonException, got {ex?.GetType().Name ?? "none"}");
        AssertUntouched(ed, before, "ORIGINAL!");
    });

    // The one lenient case, and documented: a literal null is an empty document.
    [Fact]
    public void LoadJson_OfALiteralNull_IsAnEmptyDocument() => UiThread.Run(() =>
    {
        var ed = Open("ORIGINAL");

        ed.LoadJson("null");

        Assert.Equal("", ed.GetPlainText().Trim());
    });

    [Fact]
    public void LoadJsonAsync_RejectsAnotherApplicationsJson_AndLeavesTheOpenOneAlone() => UiThread.RunAsync(async () =>
    {
        var ed = Open("ORIGINAL");
        var before = ed.Document!;

        await Assert.ThrowsAsync<JsonException>(() => ed.LoadJsonAsync("{\"name\":\"pkg\"}"));

        AssertUntouched(ed, before, "ORIGINAL!");
    });

    private static byte[] Package(FlowDocument d)
    {
        var ms = new MemoryStream();
        DocumentPackage.Save(d, ms);
        return ms.ToArray();
    }

    private static byte[] Zip(string entry, string content)
    {
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
        using (var s = new StreamWriter(z.CreateEntry(entry).Open()))
            s.Write(content);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("random bytes")]
    [InlineData("a truncated package")]
    [InlineData("an empty stream")]
    [InlineData("a zip with no document.json (a .docx, say)")]
    [InlineData("a package whose document.json is another application's JSON")]
    public void LoadPackageAsync_RejectsWhatIsNotAPackage_AndLeavesTheOpenOneAlone(string what)
    {
        byte[] bytes = what switch
        {
            "random bytes" => new byte[] { 1, 2, 3, 4, 5 },
            "a truncated package" => UiThread.Run(() => Package(Doc(P("PKG"), Image(), P("end")))) is var good ? good.Take(good.Length / 2).ToArray() : null!,
            "an empty stream" => Array.Empty<byte>(),
            "a zip with no document.json (a .docx, say)" => Zip("word/document.xml", "<w:document/>"),
            _ => Zip("document.json", "{\"name\":\"pkg\"}"),
        };
        Type expected = what.Contains("another application's JSON") ? typeof(JsonException) : typeof(InvalidDataException);

        UiThread.RunAsync(async () =>
        {
            var ed = Open("ORIGINAL");
            var before = ed.Document!;

            Exception? ex = null;
            try { await ed.LoadPackageAsync(new MemoryStream(bytes)); }
            catch (Exception e) { ex = e; }

            Assert.True(ex != null && expected.IsInstanceOfType(ex), $"{what}: expected {expected.Name}, got {ex?.GetType().Name ?? "none"}");
            AssertUntouched(ed, before, "ORIGINAL!");
        });
    }

    // ---- what a load resets -------------------------------------------------------------------------

    [Fact]
    public void Loading_ResetsHistoryAndTheModifiedFlag_AndRaisesDocumentChangedOnce() => UiThread.Run(() =>
    {
        var ed = Open("ORIGINAL");
        int changed = 0;
        ed.DocumentChanged += (_, _) => changed++;

        ed.LoadJson(DocumentSerializer.Serialize(Doc(P("NEW"))));

        Assert.Equal("NEW", ed.GetPlainText().Trim());
        Assert.False(ed.CanUndo); // Ctrl+Z must not walk back into the previous file
        Assert.False(ed.IsModified);
        Assert.Equal(1, changed);
    });

    // An armed format painter belongs to the document it was armed in; carried across a load it paints the
    // NEW document's next selection with the OLD one's format.
    [Fact]
    public void Loading_DisarmsTheFormatPainter() => UiThread.Run(() =>
    {
        var ed = Open("source");
        ed.StartFormatPainter();
        Assert.True(ed.IsFormatPainterActive);

        ed.LoadJson(DocumentSerializer.Serialize(Doc(P("NEW"))));

        Assert.False(ed.IsFormatPainterActive);
    });

    // A pending caret style (a toggle at a non-word caret) must not land on the new document's first typing.
    // No caret move after the load: a move would clear it on its own and hide the reset under test.
    [Fact]
    public void Loading_DropsAPendingCaretStyle() => UiThread.Run(() =>
    {
        var ed = Open("abc "); // caret after "!": no word there, so Bold goes pending
        ed.ToggleBold();
        Assert.True(ed.GetCaretFormat().Bold);

        ed.LoadJson(DocumentSerializer.Serialize(Doc(P("NEW"))));
        ed.InsertText("x");

        var x = ed.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First(r => r.Text!.Contains('x'));
        Assert.True(x.FontWeight.Weight < 600, "the old document's pending bold reached the new document");
    });

    [Fact]
    public void Clear_EmptiesTheDocument_AndRaisesDocumentChanged() => UiThread.Run(() =>
    {
        var ed = Open("ORIGINAL");
        int changed = 0;
        ed.DocumentChanged += (_, _) => changed++;

        ed.Clear();

        Assert.Equal("", ed.GetPlainText().Trim());
        Assert.False(ed.CanUndo);
        Assert.Equal(1, changed);
    });

    // ---- page setup: a plain document starts from the HOST's setup ----------------------------------

    private static string A5LandscapeWithHeader()
    {
        var d = Doc(P("A"));
        d.PageSetup = new PageSetup { PageSize = RichEditorPageSize.A5, Orientation = RichEditorPageOrientation.Landscape, Header = "HDR" };
        return DocumentSerializer.Serialize(d);
    }

    private static string Plain() => DocumentSerializer.Serialize(Doc(P("plain B")));

    private static string Page(RichEditor ed) => $"{ed.PageSize}/{ed.PageOrientation}/'{ed.PageHeader}'";

    // Opening an A5-landscape file and then a plain one used to turn the plain one A5 landscape with the
    // first file's header — and saving it wrote them in. Clear (a new document) inherited them too.
    [Fact]
    public void APlainDocument_StartsFromTheHostsSetup_NotThePreviousDocuments() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };
        ed.PageSize = RichEditorPageSize.A4; // the host's choice

        ed.LoadJson(A5LandscapeWithHeader());
        Assert.Equal("A5/Landscape/'HDR'", Page(ed)); // a document's own setup still applies

        ed.LoadJson(Plain());
        Assert.Equal("A4/Portrait/''", Page(ed));
        string saved = ed.ToJson();
        Assert.Contains("A4", saved);
        Assert.DoesNotContain("HDR", saved);

        ed.LoadJson(A5LandscapeWithHeader());
        ed.Clear();
        Assert.Equal("A4/Portrait/''", Page(ed));
    });

    [Fact]
    public void WithoutAHostSetup_APlainDocumentGetsTheDefaults() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };

        ed.LoadJson(A5LandscapeWithHeader());
        ed.LoadJson(Plain());

        Assert.Equal("Continuous/Portrait/''", Page(ed));
    });

    // The host defaults are recorded per property. The DP callback is shared with unrelated properties, and
    // a snapshot of every page DP there would have made the OPEN document's A5 the host default the moment
    // the host touched, say, DefaultFontSize.
    [Fact]
    public void AnUnrelatedHostChange_DoesNotPromoteTheOpenDocumentsSetup() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };
        ed.LoadJson(A5LandscapeWithHeader());

        ed.DefaultFontSize = 12;       // unrelated, same callback
        ed.PageFooter = "HOST FOOTER"; // a page property the host does set

        ed.LoadJson(Plain());
        Assert.Equal("Continuous/Portrait/''", Page(ed));
        Assert.Equal("HOST FOOTER", ed.PageFooter);
    });

    // The toolbar's paper picker edits the OPEN document; it must not become the host's default.
    [Fact]
    public void TheToolbarsPaperPicker_EditsTheDocument_NotTheHostDefault() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument() };
        ed.PageSize = RichEditorPageSize.A4; // host
        var toolbar = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
        var paper = (ComboBox?)typeof(RichEditorToolbar).GetField("_paper", NP)!.GetValue(toolbar);
        Assert.NotNull(paper);
        var sizes = (IList)typeof(RichEditorToolbar).GetField("PaperSizes", NS)!.GetValue(null)!;
        int letter = Enumerable.Range(0, sizes.Count)
            .First(i => (RichEditorPageSize)sizes[i]!.GetType().GetField("Item2")!.GetValue(sizes[i])! == RichEditorPageSize.Letter);

        paper!.SelectedIndex = letter; // what a user's pick does

        Assert.Equal(RichEditorPageSize.Letter, ed.PageSize);
        Assert.Contains("Letter", ed.ToJson()); // the open document carries it
        ed.Clear();
        Assert.Equal(RichEditorPageSize.A4, ed.PageSize); // a new document is back on the host's paper
    });
}
