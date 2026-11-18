using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Two places where the saved JSON differed from upstream (AvaloniaRichEditor PR #92) for the same edit.</summary>
[Collection(UiTests.Collection)]
public class Upstream92ParityTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    // A selected inline picture deleted with Delete left "he" + "llo" as two runs; upstream joins them.
    [Fact]
    public void DeletingASelectedInlinePicture_JoinsTheRunsBesideIt() => UiThread.Run(() =>
    {
        var img = new InlineImage { Width = 10, Height = 10 };
        var p = new Paragraph { Inlines = { new Run { Text = "he" }, img, new Run { Text = "llo" } } };
        var ed = new RichEditor { Document = new FlowDocument { Blocks = { p } } };
        T.GetField("_selectedInline", NP)!.SetValue(ed, ((Paragraph, InlineImage)?)(p, img));

        T.GetMethod("DeleteSelectedInline", NP)!.Invoke(ed, null);

        var run = Assert.IsType<Run>(Assert.Single(p.Inlines));
        Assert.Equal("hello", run.Text);
    });

    // A copied cell block of a table with no set row heights wrote "RowHeights":[0,0]; upstream writes none.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtractingCellsWithNoSetRowHeights_WritesNoRowHeights(bool explicitZeros)
    {
        var tb = new TableBlock(2, 2);
        if (explicitZeros) { tb.RowHeights.Add(0); tb.RowHeights.Add(0); }

        var sub = tb.Extract(0, 0, 1, 1);

        Assert.Empty(sub.RowHeights);
        Assert.DoesNotContain("RowHeights", DocumentSerializer.Serialize(new FlowDocument { Blocks = { new Paragraph(), sub, new Paragraph() } }));
    }

    [Fact]
    public void ExtractingCellsWithASetRowHeight_KeepsTheRowHeights()
    {
        var tb = new TableBlock(2, 2);
        tb.RowHeights.Add(0); tb.RowHeights.Add(40);

        Assert.Equal(new[] { 0.0, 40.0 }, tb.Extract(0, 0, 1, 1).RowHeights);
    }
}
