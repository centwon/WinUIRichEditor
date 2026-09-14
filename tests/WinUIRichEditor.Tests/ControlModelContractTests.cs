using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text.Json.Nodes;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The model members no test referenced (2026-09-14 re-scan of the public surface): what a file's table
/// spans may be, a page setup that looks default, the pool of a document's pictures, and the value semantics of
/// <see cref="TextPointer"/>. Measured first — a negative span took the editor down on load, spans no merge
/// explained hid cells' text from the editor and every export, <c>"ColSpans":[null]</c> and a null pool entry
/// threw <see cref="NullReferenceException"/> instead of loading, and a Continuous page chosen under an A4 host
/// reopened as A4. AvaloniaRichEditor had the same code in each place.</summary>
[Collection(UiTests.Collection)]
public class ControlModelContractTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private const string OnePixelPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

    // A [paragraph, rows×cols table whose cells read "T<r><c>", paragraph] document, as JSON, with the table's DTO
    // handed to `mutate` — the way a damaged or hand-made file differs from one this library wrote.
    private static string TableJson(int rows, int cols, Action<JsonObject> mutate)
    {
        var tb = new TableBlock(rows, cols);
        foreach (var (r, c, cell) in tb.LogicalCells()) ((Run)cell.Para.Inlines[0]).Text = $"T{r}{c}";
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "before" } } });
        doc.Blocks.Add(tb);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });
        var root = JsonNode.Parse(DocumentSerializer.Serialize(doc))!.AsObject();
        mutate(root["Blocks"]!.AsArray().Select(b => b!.AsObject()).First(b => (string?)b["Type"] == "Table"));
        return root.ToJsonString();
    }

    private static JsonArray Grid(params int[][] rows)
        => new(rows.Select(r => (JsonNode?)new JsonArray(r.Select(v => (JsonNode?)v).ToArray())).ToArray());

    // The grid invariant every consumer assumes: a slot is an anchor (both spans ≥ 1, its merge inside the grid)
    // or covered (both 0), and every slot lies in exactly one anchor's merge.
    private static void AssertConsistent(TableBlock tb)
    {
        int rows = tb.Cells.Count;
        var claims = tb.Cells.Select(row => new int[row.Count]).ToArray();
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < tb.Cells[r].Count; c++)
            {
                int cs = tb.ColSpans[r][c], rs = tb.RowSpans[r][c];
                if (cs == 0 && rs == 0) continue;
                Assert.True(cs >= 1 && rs >= 1, $"({r},{c}) spans {cs},{rs}");
                Assert.True(r + rs <= rows && c + cs <= tb.Cells[r].Count, $"({r},{c}) reaches out of the grid");
                for (int rr = r; rr < r + rs; rr++)
                    for (int cc = c; cc < c + cs; cc++)
                    {
                        if (rr != r || cc != c) Assert.True(tb.ColSpans[rr][cc] == 0 && tb.RowSpans[rr][cc] == 0, $"({rr},{cc}) is inside ({r},{c})'s merge");
                        claims[rr][cc]++;
                    }
            }
        for (int r = 0; r < rows; r++)
            for (int c = 0; c < claims[r].Length; c++)
                Assert.True(claims[r][c] == 1, $"({r},{c}) is in {claims[r][c]} merges");
    }

    private static void Relayout(RichEditor ed) => typeof(RichEditor).GetMethod("RelayoutToViewport", NP)!.Invoke(ed, null);

    // ---- table spans from a file --------------------------------------------------------------------------------

    public static TheoryData<string> SpanGarbage => new()
    {
        "null-row", "colspan-overflow", "rowspan-overflow", "negative", "all-covered", "overlap", "pair-mismatch", "anchor-under-merge",
    };

    private static (int rows, int cols, Action<JsonObject> mutate) Garbage(string name) => name switch
    {
        "null-row" => (2, 2, t => t["ColSpans"] = new JsonArray(null, new JsonArray(1, 1))),
        "colspan-overflow" => (2, 2, t => t["ColSpans"] = Grid(new[] { 5, 1 }, new[] { 1, 1 })),
        "rowspan-overflow" => (2, 2, t => t["RowSpans"] = Grid(new[] { 3, 1 }, new[] { 1, 1 })),
        "negative" => (2, 2, t => t["ColSpans"] = Grid(new[] { -2, 1 }, new[] { 1, 1 })),
        "all-covered" => (2, 2, t => { t["ColSpans"] = Grid(new[] { 0, 0 }, new[] { 0, 0 }); t["RowSpans"] = Grid(new[] { 0, 0 }, new[] { 0, 0 }); }),
        "overlap" => (1, 3, t => { t["ColSpans"] = Grid(new[] { 2, 2, 1 }); t["RowSpans"] = Grid(new[] { 1, 1, 1 }); }),
        "pair-mismatch" => (2, 2, t => t["ColSpans"] = Grid(new[] { 0, 1 }, new[] { 1, 1 })),
        // (0,0) spans the whole 2×2, yet (1,1) claims to be an anchor of its own: the text there must not be hidden.
        _ => (2, 2, t => { t["ColSpans"] = Grid(new[] { 2, 0 }, new[] { 0, 1 }); t["RowSpans"] = Grid(new[] { 2, 0 }, new[] { 0, 1 }); }),
    };

    // Every text a slot that ended up an anchor holds is somewhere a reader can see it — in the editor and in both
    // exports — and the grid is one every consumer can walk. Before: a crash for "negative", NullReferenceException
    // for "null-row", 0 of 4 texts for "all-covered", 3 of 4 for "pair-mismatch".
    [Theory, MemberData(nameof(SpanGarbage))]
    public void SpansFromAFile_LoadAsAConsistentGrid_HidingNoCellsText(string name)
    {
        var (rows, cols, mutate) = Garbage(name);
        string json = TableJson(rows, cols, mutate);

        var doc = DocumentSerializer.Deserialize(json);
        var tb = doc.Blocks.OfType<TableBlock>().Single();
        AssertConsistent(tb);
        var visible = tb.LogicalCells().Select(x => ((Run)x.cell.Para.Inlines[0]).Text!).ToList();
        string html = HtmlDocumentFormatter.ToHtml(doc), rtf = RtfDocumentFormatter.Write(doc);
        foreach (var t in visible) { Assert.Contains(t, html); Assert.Contains(t, rtf); }
        if (name is "all-covered" or "pair-mismatch" or "negative" or "null-row" or "anchor-under-merge")
            Assert.Equal(rows * cols, visible.Count); // nothing in these files says a cell is merged away

        string text = UiThread.Run(() =>
        {
            var ed = new RichEditor();
            ed.LoadJson(json);
            Relayout(ed);
            return ed.GetPlainText();
        });
        foreach (var t in visible) Assert.Contains(t, text);
    }

    // The control: a consistent table — merges across rows and columns — comes back exactly as it was.
    [Fact]
    public void AConsistentGrid_IsLeftExactlyAsItWas()
    {
        var tb = new TableBlock(4, 4);
        tb.MergeCells(0, 0, 1, 1);
        tb.MergeCells(0, 2, 0, 3);
        tb.MergeCells(2, 1, 3, 1);
        string Spans() => string.Join(";", tb.ColSpans.Select(r => string.Join(",", r))) + "|" + string.Join(";", tb.RowSpans.Select(r => string.Join(",", r)));
        string before = Spans();

        tb.EnsureSpanConsistency();

        Assert.Equal(before, Spans());
        AssertConsistent(tb);
    }

    // A host that builds its own model is trusted no more than a file: a negative span in a table nested in a cell
    // and in an inline table took the editor down on its first layout.
    [Fact]
    public void AHostBuiltModelWithBadSpans_DoesNotTakeTheEditorDown() => UiThread.Run(() =>
    {
        var inner = new TableBlock(2, 2);
        inner.ColSpans[0][0] = -3;
        var outer = new TableBlock(1, 1);
        outer.Cells[0][0].Blocks.Clear();
        outer.Cells[0][0].Blocks.Add(inner);
        var inlineTable = new TableBlock(2, 2);
        inlineTable.RowSpans[1][1] = -1;
        var host = new Paragraph { Inlines = { new Run { Text = "x" }, new InlineTable { Table = inlineTable } } };
        var doc = new FlowDocument();
        doc.Blocks.Add(outer);
        doc.Blocks.Add(host);
        doc.Blocks.Add(new Paragraph());

        var ed = new RichEditor { Document = doc };
        Relayout(ed);

        AssertConsistent(inner);
        AssertConsistent(inlineTable);
    });

    // ---- the picture pool ---------------------------------------------------------------------------------------

    [Fact]
    public void ANullPoolEntry_LoadsTheDocument_WithoutThatPicture()
    {
        var root = JsonNode.Parse(TableJson(1, 1, _ => { }))!.AsObject();
        root["Images"] = new JsonObject { ["k"] = null };
        root["Blocks"]![0]!["Inlines"] = new JsonArray(new JsonObject { ["Type"] = "Image", ["ImageRef"] = "k" }, new JsonObject { ["Type"] = "Run", ["Text"] = "kept" });

        var doc = DocumentSerializer.Deserialize(root.ToJsonString());

        var p = (Paragraph)doc.Blocks[0];
        Assert.Null(Assert.IsType<InlineImage>(p.Inlines[0]).RawBytes);
        Assert.Equal("kept", ((Run)p.Inlines[1]).Text);
    }

    // The .flow package reads the pool's MIME types from the same map, and threw on the same null.
    [Fact]
    public void ANullPoolEntry_InAPackage_LoadsThePictureAnyway()
    {
        var doc = new FlowDocument();
        var img = new ImageBlock();
        img.SetImageData(Convert.FromBase64String(OnePixelPng), "image/png");
        doc.Blocks.Add(new Paragraph());
        doc.Blocks.Add(img);
        doc.Blocks.Add(new Paragraph());
        using var ms = new MemoryStream();
        DocumentPackage.Save(doc, ms);

        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            var entry = zip.GetEntry("document.json")!;
            JsonObject root;
            using (var s = entry.Open()) root = JsonNode.Parse(s)!.AsObject();
            foreach (var key in root["Images"]!.AsObject().Select(kv => kv.Key).ToList()) root["Images"]![key] = null;
            entry.Delete();
            using var w = new StreamWriter(zip.CreateEntry("document.json").Open());
            w.Write(root.ToJsonString());
        }
        ms.Position = 0;

        var loaded = DocumentPackage.Load(ms);

        var back = loaded.Blocks.OfType<ImageBlock>().Single();
        Assert.NotNull(back.RawBytes);
        Assert.Equal("image/png", back.MimeType); // detected from the bytes, as for a package with no MIME map
    }

    // ---- page setup --------------------------------------------------------------------------------------------

    // "No page setup" reads back as the HOST's defaults (decided 2026-09-12). A Continuous page chosen under a host
    // that defaults to A4 was stored as "no setup", so the file reopened as A4 — here and in any other A4 host.
    [Fact]
    public void Continuous_ChosenUnderAnA4Host_SurvivesSaveAndReopen() => UiThread.Run(() =>
    {
        var ed = new RichEditor { PageSize = RichEditorPageSize.A4 };
        ed.LoadHtml("<p>x</p>");
        typeof(RichEditor).GetMethod("EditDocumentPageSetup", NP)!.Invoke(ed, new object[]
            { (Action)(() => { ed.PageSize = RichEditorPageSize.Continuous; ed.ShowPageBoundaries = false; }) });
        string json = ed.ToJson();

        ed.LoadJson(json);
        Assert.Equal(RichEditorPageSize.Continuous, ed.PageSize);
        var other = new RichEditor { PageSize = RichEditorPageSize.A4 };
        other.LoadJson(json);
        Assert.Equal(RichEditorPageSize.Continuous, other.PageSize);
    });

    // The control: in a plain host, a plain document is still written without a page setup — old files' bytes stay.
    [Fact]
    public void APlainDocument_InAPlainHost_IsWrittenWithoutAPageSetup() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>x</p>");

        Assert.Null(ed.Document!.PageSetup);
        Assert.DoesNotContain("PageSetup", ed.ToJson());
        Assert.True(new PageSetup().IsDefault);
        Assert.False(new PageSetup { ShowPageNumbers = true }.IsDefault);
    });

    // ---- value semantics and small queries ---------------------------------------------------------------------

    // A position, not an object: two pointers at the same place are equal (affinity is display-only), and equal
    // pointers hash alike — the editor's cell-block marker, which needs IDENTITY, uses ReferenceEquals for that.
    [Fact]
    public void TextPointer_IsEqualByParagraphAndOffset_IgnoringAffinity()
    {
        var p = new Paragraph();
        var q = new Paragraph();
        var a = new TextPointer(p, 3);
        var b = new TextPointer(p, 3) { AtLineEnd = true };

        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals(b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.False(a == new TextPointer(p, 4));
        Assert.False(a == new TextPointer(q, 3));
        Assert.False(a == null);
        Assert.True((TextPointer?)null == null);
        Assert.False(ReferenceEquals(a, b));
    }

    [Fact]
    public void IsListItem_FollowsTheListType()
    {
        Assert.False(new Paragraph().IsListItem);
        Assert.True(new Paragraph { ListType = ListKind.Bullet }.IsListItem);
        Assert.True(new Paragraph { ListType = ListKind.Ordered }.IsListItem);
    }

    // Across paragraphs and a table: "\n" between paragraphs, formatting kept; GetRichRuns leaves pictures out,
    // GetRichInlines keeps one only when the range holds it whole.
    [Fact]
    public void GetRichRuns_AndGetRichInlines_CoverTheRange() => UiThread.Run(() =>
    {
        var img = new InlineImage { Width = 10, Height = 10 };
        var first = new Paragraph { Inlines = { new Run { Text = "ab" }, img, new Run { Text = "cd", FontSize = 20 } } };
        var tb = new TableBlock(1, 1);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "cell";
        var last = new Paragraph { Inlines = { new Run { Text = "ef" } } };
        var doc = new FlowDocument();
        doc.Blocks.Add(first); doc.Blocks.Add(tb); doc.Blocks.Add(last);
        new RichEditor { Document = doc }; // wires Parent pointers

        var runs = new TextRange(new TextPointer(first, 1), new TextPointer(last, 1)).GetRichRuns();
        Assert.Equal(new[] { "b", "cd", "\n", "cell", "\n", "e" }, runs.Select(r => r.Text));
        Assert.Equal(20, runs[1].FontSize);

        var whole = new TextRange(new TextPointer(first, 0), new TextPointer(first, 5)).GetRichInlines();
        Assert.Equal(3, whole.Count);
        Assert.IsType<InlineImage>(whole[1]);
        Assert.NotSame(img, whole[1]); // a copy
        var cut = new TextRange(new TextPointer(first, 3), new TextPointer(first, 5)).GetRichInlines();
        Assert.Equal("cd", Assert.IsType<Run>(Assert.Single(cut)).Text);
    });

    // The async parse applies the same gates as the synchronous one: local files blocked outside %TEMP% (the
    // public paste exemption), data: pictures loaded.
    [Fact]
    public void ParseHtmlAsync_AppliesTheLocalFileGate_AndLoadsDataPictures()
    {
        string outside = Path.Combine(AppContext.BaseDirectory, "parse-async-probe.png");
        File.WriteAllBytes(outside, Convert.FromBase64String(OnePixelPng));
        try
        {
            string html = $"<p><img src=\"{new Uri(outside).AbsoluteUri}\" width=\"5\" height=\"5\"></p>"
                        + $"<p><img src=\"data:image/png;base64,{OnePixelPng}\" width=\"5\" height=\"5\"></p>";
            int Pictures(FlowDocument d) => d.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineImage>().Count(i => i.RawBytes != null)
                                          + d.Blocks.OfType<ImageBlock>().Count(i => i.RawBytes != null);

            var blocked = HtmlDocumentFormatter.ParseHtmlAsync(html, allowLocalFileImages: false, allowRemoteImages: false).GetAwaiter().GetResult();
            var allowed = HtmlDocumentFormatter.ParseHtmlAsync(html, allowLocalFileImages: true, allowRemoteImages: false).GetAwaiter().GetResult();

            Assert.Equal(1, Pictures(blocked)); // the data: picture only
            Assert.Equal(2, Pictures(allowed));
        }
        finally { File.Delete(outside); }
    }
}
