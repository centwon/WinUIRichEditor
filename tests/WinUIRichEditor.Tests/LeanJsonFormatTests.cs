using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Windows.UI.Text;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The JSON writer leaves out what every reader assumes (upstream AvaloniaRichEditor PR #62, 2026-10-01).
/// The schema is unchanged; a run spent ~90 bytes on <c>"Italic":false,…</c>, Korean went out as <c>\uXXXX</c>, and
/// every document was indented. The fixtures are the SAME kitchen-sink document as the upstream tree wrote it before
/// (verbose) and after (lean) — the two repositories share this format, so reading both alike is what keeps them
/// interchangeable.</summary>
[Collection(UiTests.Collection)]
public class LeanJsonFormatTests
{
    private static string Fixture(string name)
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = Path.Combine(dir.FullName, "Fixtures", name);
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException(name);
    }

    private static FlowDocument OneParagraph(params Inline[] inlines)
    {
        var doc = new FlowDocument();
        var p = new Paragraph();
        foreach (var i in inlines) p.Inlines.Add(i);
        doc.Blocks.Add(p);
        return doc;
    }

    [Fact]
    public void APlainRun_WritesItsText_AndNothingElse()
    {
        string json = DocumentSerializer.Serialize(OneParagraph(new Run { Text = "안녕" }));
        Assert.Equal("{\"Version\":\"1.0\",\"Blocks\":[{\"Inlines\":[{\"Text\":\"안녕\"}]}]}", json);
        var p = (Paragraph)DocumentSerializer.Deserialize(json).Blocks[0];
        var r = (Run)p.Inlines[0];
        Assert.Equal(10.0, r.FontSize);
        Assert.False(r.FontWeight.IsBold());
        Assert.Equal(FontStyle.Normal, r.FontStyle);
        Assert.Equal(TextDecorationFlags.None, r.TextDecorations);
        Assert.Equal((0, ListKind.None, TextAlignment.Left, 0.0, false), (p.HeadingLevel, p.ListType, p.TextAlignment, p.Indent, p.IsQuote));
    }

    [Fact]
    public void KoreanIsWrittenAsItIs_AndHtmlSensitiveCharactersAreNot()
    {
        string json = DocumentSerializer.Serialize(OneParagraph(new Run { Text = "정보공시 </script> & 'x'" }));
        Assert.Contains("정보공시", json);
        Assert.DoesNotContain("\\uC815", json);
        Assert.DoesNotContain("</script>", json);
        Assert.Equal("정보공시 </script> & 'x'", ((Run)((Paragraph)DocumentSerializer.Deserialize(json).Blocks[0]).Inlines[0]).Text);
    }

    [Fact]
    public void WhatDiffersFromTheDefault_IsWritten()
    {
        var doc = OneParagraph(new Run { Text = "b", FontWeight = FontWeightValues.Bold, FontStyle = FontStyle.Italic, FontSize = 12 });
        var p = (Paragraph)doc.Blocks[0];
        p.HeadingLevel = 2; p.ListType = ListKind.Ordered; p.ListLevel = 1; p.IsQuote = true; p.Indent = 20;
        p.TextAlignment = TextAlignment.Center; p.MarginRight = 5; p.MarginTop = 3;
        string json = DocumentSerializer.Serialize(doc);
        foreach (var field in new[] { "\"Bold\":true", "\"Italic\":true", "\"FontSize\":12", "\"HeadingLevel\":2", "\"ListType\":\"Ordered\"",
                                      "\"ListLevel\":1", "\"IsQuote\":true", "\"Indent\":20", "\"TextAlignment\":\"Center\"", "\"MarginRight\":5", "\"MarginTop\":3" })
            Assert.Contains(field, json);
        Assert.Equal(json, DocumentSerializer.Serialize(DocumentSerializer.Deserialize(json)));
    }

    [Fact]
    public void BlockMargins_AreLeftOutOnlyAtTheirOwnDefault()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "p" } } });
        doc.Blocks.Add(new TableBlock(1, 1));
        doc.Blocks.Add(new TableBlock(1, 1) { MarginBottom = 0 });
        doc.Blocks.Add(new DividerBlock { MarginBottom = 10 });
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        Assert.Equal(10, back.Blocks.OfType<TableBlock>().First().MarginBottom);
        Assert.Equal(0, back.Blocks.OfType<TableBlock>().Last().MarginBottom);
        Assert.Equal(10, back.Blocks.OfType<DividerBlock>().Single().MarginBottom);
    }

    [Fact]
    public void OnlyAMergedTable_CarriesItsSpanGrids()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new TableBlock(2, 2));
        Assert.DoesNotContain("ColSpans", DocumentSerializer.Serialize(doc));
        ((TableBlock)doc.Blocks[0]).MergeCells(0, 0, 0, 1);
        string merged = DocumentSerializer.Serialize(doc);
        Assert.Contains("\"ColSpans\"", merged);
        Assert.Equal((2, 1), DocumentSerializer.Deserialize(merged).Blocks.OfType<TableBlock>().Single().SpanOf(0, 0));
    }

    // Both forms of one document, as the upstream tree wrote them before and after the change.
    [Fact]
    public void TheVerboseAndTheLeanForm_ReadAlike()
    {
        string verbose = Fixture("format-1.0-verbose-kitchen-sink.json"), lean = Fixture("format-1.0-lean-kitchen-sink.json");
        Assert.Contains("\"Italic\": false", verbose); // precondition: the old, verbose form
        Assert.DoesNotContain("\"Italic\":false", lean);
        Assert.Equal(DocumentSerializer.Serialize(DocumentSerializer.Deserialize(verbose)),
                     DocumentSerializer.Serialize(DocumentSerializer.Deserialize(lean)));
        // And byte-equal to upstream's own output: both join equal runs on load, so one document is one file.
        Assert.Equal(lean, DocumentSerializer.Serialize(DocumentSerializer.Deserialize(lean)));
    }

    // The interchange contract with upstream: one document using every field, in the canonical form. Upstream
    // (AvaloniaRichEditor) holds the same file and the same test, so a change to what either editor writes — a
    // field's order, a colour's spelling, a default — fails in the repository that made it.
    [Fact]
    public void TheInterchangeDocument_ReadsAndWritesBackByteForByte()
    {
        string canonical = Fixture("format-1.0-interchange.json");
        Assert.Equal(canonical, DocumentSerializer.Serialize(DocumentSerializer.Deserialize(canonical)));
    }

    // A table with no rows is no table, in both editors (upstream drops it too).
    [Fact]
    public void ATableWithNoRows_IsNotRead()
    {
        var doc = DocumentSerializer.Deserialize("{\"Version\":\"1.0\",\"Blocks\":[{\"Inlines\":[{\"Text\":\"a\"},{\"Type\":\"Table\",\"Table\":{\"Type\":\"Table\",\"Cells\":[]}}]},{\"Type\":\"Table\",\"Cells\":[]}]}");
        Assert.Empty(doc.Blocks.OfType<TableBlock>());
        Assert.Empty(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineTable>());
    }

    [Fact]
    public void APackagesDocumentJson_IsLeanToo()
    {
        using var ms = new MemoryStream();
        DocumentPackage.Save(DocumentSerializer.Deserialize(Fixture("format-1.0-lean-kitchen-sink.json")), ms);
        ms.Position = 0;
        using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
        using var reader = new StreamReader(zip.GetEntry("document.json")!.Open());
        string json = reader.ReadToEnd();
        Assert.DoesNotContain("\"Italic\":false", json);
        Assert.DoesNotContain("\n", json);
    }

    [Fact]
    public void AParagraphWithAnAutoTopMargin_Serializes()
    {
        var doc = OneParagraph(new Run { Text = "x" });
        ((Paragraph)doc.Blocks[0]).MarginTop = Block.AutoTopMargin;
        string? json = null;
        Assert.Null(Record.Exception(() => json = DocumentSerializer.Serialize(doc)));
        Assert.Equal(0, ((Paragraph)DocumentSerializer.Deserialize(json!).Blocks[0]).MarginTop);
    }

    // ---- versions ------------------------------------------------------------------------------------------

    [Fact]
    public void ANewerMajorFormat_IsRefusedByTheLoadingPaths() => UiThread.Run(() =>
    {
        const string newer = "{\"Version\":\"2.0\",\"Blocks\":[{\"Inlines\":[{\"Text\":\"x\"}]}]}";
        var ed = new RichEditor();
        ed.LoadJson("{\"Version\":\"1.0\",\"Blocks\":[{\"Inlines\":[{\"Text\":\"kept\"}]}]}");
        Assert.Throws<JsonException>(() => ed.LoadJson(newer));
        Assert.Contains("kept", ed.GetPlainText()); // the open document is left alone
        Assert.Equal("x", string.Concat(((Paragraph)DocumentSerializer.Deserialize(newer).Blocks[0]).Inlines.OfType<Run>().Select(r => r.Text)));
    });

    [Theory]
    [InlineData("\"1.0\"")]
    [InlineData("\"1.9\"")]
    [InlineData("1")]
    [InlineData("2")]
    public void OlderAndSameMajorFormats_Load(string version) => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadJson("{\"Version\":" + version + ",\"Blocks\":[{\"Inlines\":[{\"Text\":\"x\"}]}]}");
        Assert.Contains("x", ed.GetPlainText());
    });

    [Fact]
    public void AnUnknownBlockType_IsReported()
    {
        RichEditorDiagnostics.Reset();
        Exception? seen = null;
        void Handler(object? s, RichEditorFaultEventArgs e) { if (e.Exception is NotSupportedException) seen = e.Exception; }
        RichEditorDiagnostics.Fault += Handler;
        try
        {
            var doc = DocumentSerializer.Deserialize("{\"Version\":\"1.4\",\"Blocks\":[{\"Type\":\"Chart\",\"Inlines\":[{\"Text\":\"caption\"}]}]}");
            Assert.Equal("caption", string.Concat(((Paragraph)doc.Blocks[0]).Inlines.OfType<Run>().Select(r => r.Text)));
        }
        finally { RichEditorDiagnostics.Fault -= Handler; }
        Assert.NotNull(seen);
        Assert.Contains("Chart", seen!.Message);
    }
}
