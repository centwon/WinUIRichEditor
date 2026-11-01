using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

// The file-by-file audit of 2026-11-01 (with upstream): picture kind through the formats, CSS alpha, what the JSON
// reader let through.
public class Audit4Tests
{
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static Paragraph P(params Inline[] inlines)
    {
        var p = new Paragraph();
        foreach (var i in inlines) p.Inlines.Add(i);
        return p;
    }

    private static FlowDocument Doc(params Block[] blocks)
    {
        var d = new FlowDocument();
        foreach (var b in blocks) d.Blocks.Add(b);
        return d;
    }

    private static Paragraph[] Paras(FlowDocument d) => d.Blocks.OfType<Paragraph>().ToArray();

    // ---- pictures keep inline vs block through HTML and RTF -----------------------------------------

    private static FlowDocument BigInline()
    {
        var img = new InlineImage { Width = 100, Height = 100 };
        img.SetImageData(TinyPng, "image/png");
        return Doc(P(new Run { Text = "a" }, img, new Run { Text = "b" }));
    }

    private static FlowDocument SmallBlock()
    {
        var ib = new ImageBlock { Width = 30, Height = 30 };
        ib.SetImageData(TinyPng, "image/png");
        return Doc(P(new Run { Text = "a" }), ib, P(new Run { Text = "b" }));
    }

    private static FlowDocument Round(string format, FlowDocument d) => format == "html"
        ? HtmlDocumentFormatter.ParseHtml(HtmlDocumentFormatter.ToHtml(d))
        : RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(d));

    private static bool HasInlineImage(FlowDocument d) => Paras(d).Any(p => p.Inlines.OfType<InlineImage>().Any());

    [Theory]
    [InlineData("html")]
    [InlineData("rtf")]
    public void ALargeInlinePicture_StaysInline(string format)
    {
        var back = Round(format, BigInline());
        Assert.True(HasInlineImage(back));
        Assert.DoesNotContain(back.Blocks, b => b is ImageBlock);
    }

    [Theory]
    [InlineData("html")]
    [InlineData("rtf")]
    public void ASmallBlockPicture_StaysABlock(string format)
    {
        var back = Round(format, SmallBlock());
        Assert.Contains(back.Blocks, b => b is ImageBlock);
        Assert.False(HasInlineImage(back));
    }

    // ---- CSS colours: alpha is read; the model's own #AARRGGBB stays as it was -----------------------

    private static Run RunX(string html)
        => HtmlDocumentFormatter.ParseHtml(html).Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).First(r => r.Text == "x");

    [Fact]
    public void Css_EightDigitHex_IsRrggbbaa()
        => Assert.Equal(Windows.UI.Color.FromArgb(0x80, 0xFF, 0, 0),
            RunX("<p><span style=\"color:#ff000080\">x</span></p>").Foreground);

    [Fact]
    public void ModelHex_EightDigits_IsStillAarrggbb()
        => Assert.Equal(Windows.UI.Color.FromArgb(0x80, 0xFF, 0, 0), ColorUtil.Parse("#80FF0000"));

    // ---- JSON reader: unknown enum numbers and dotless future versions ------------------------------

    [Fact]
    public void Json_AnUndefinedEnumNumber_ReadsAsTheDefault()
    {
        var p = P(new Run { Text = "x" });
        p.ListType = ListKind.Bullet;
        var json = DocumentSerializer.Serialize(Doc(p));
        var bad = Regex.Replace(json, "\"ListType\"\\s*:\\s*\"Bullet\"", "\"ListType\":\"7\"");
        Assert.NotEqual(json, bad);
        Assert.Equal(ListKind.None, Paras(DocumentSerializer.Deserialize(bad)).First().ListType);
    }

    [Fact]
    public void Json_ADotlessVersionPastTheLegacyOnes_IsRefused()
    {
        var json = DocumentSerializer.Serialize(Doc(P(new Run { Text = "x" })));
        var future = Regex.Replace(json, "\"Version\"\\s*:\\s*\"[^\"]*\"", "\"Version\":\"3\"");
        Assert.NotEqual(json, future);
        Assert.ThrowsAny<JsonException>(() => DocumentSerializer.Deserialize(future));
        DocumentSerializer.Deserialize(Regex.Replace(json, "\"Version\"\\s*:\\s*\"[^\"]*\"", "\"Version\":\"2\""));
    }
}

// Commands that change nothing leave no undo step.
[Collection(UiTests.Collection)]
public class Audit4ControlTests
{
    private static RichEditor Editor(Paragraph p)
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(p);
        var ed = new RichEditor { Document = doc };
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" })
            typeof(RichEditor).GetField(f, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(ed, new TextPointer(p, 0));
        return ed;
    }

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    [Fact]
    public void SetHeading_BodyOnBody_LeavesNoUndoStep() => UiThread.Run(() =>
    {
        var ed = Editor(P("x"));
        ed.SetHeading(0);
        Assert.False(ed.CanUndo);
    });

    [Fact]
    public void SetListStyle_TheSameStyleAgain_LeavesNoSecondUndoStep() => UiThread.Run(() =>
    {
        var ed = Editor(P("x"));
        ed.SetListStyle(ListMarkerStyle.Square);
        Assert.True(ed.CanUndo);
        ed.SetListStyle(ListMarkerStyle.Square);
        ed.Undo();
        Assert.False(ed.CanUndo);
        Assert.Equal(ListKind.None, ed.Document!.Blocks.OfType<Paragraph>().First().ListType); // undo restores a snapshot
    });
}
