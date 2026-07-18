using System.Linq;
using Windows.UI.Text;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

public class TextRangeTests
{
    private static Paragraph Para(string text) => new() { Inlines = { new Run { Text = text } } };
    private static string Plain(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    [Fact]
    public void GetText_WithinParagraph()
    {
        var p = Para("Hello world");
        var range = new TextRange(new TextPointer(p, 0), new TextPointer(p, 5));
        Assert.Equal("Hello", range.GetText());
    }

    [Fact]
    public void Delete_RemovesSpan()
    {
        var p = Para("Hello world");
        new TextRange(new TextPointer(p, 5), new TextPointer(p, 11)).Delete();
        Assert.Equal("Hello", Plain(p));
    }

    [Fact]
    public void ApplyPropertyValue_BoldsSubrange()
    {
        var p = Para("Hello world");
        new TextRange(new TextPointer(p, 0), new TextPointer(p, 5))
            .ApplyPropertyValue(r => r.FontWeight = new FontWeight { Weight = 700 });

        // The "Hello" portion should now be bold; "world" should not.
        int pos = 0;
        bool helloBold = false, worldPlain = true;
        foreach (var run in p.Inlines.OfType<Run>())
        {
            int len = run.Text!.Length;
            if (pos < 5 && run.FontWeight.IsBold()) helloBold = true;
            if (pos >= 5 && run.FontWeight.IsBold()) worldPlain = false;
            pos += len;
        }
        Assert.True(helloBold);
        Assert.True(worldPlain);
    }

    [Fact]
    public void IsEmpty_WhenCollapsed()
    {
        var p = Para("abc");
        Assert.True(new TextRange(new TextPointer(p, 1), new TextPointer(p, 1)).IsEmpty);
        Assert.False(new TextRange(new TextPointer(p, 0), new TextPointer(p, 1)).IsEmpty);
    }

    [Fact]
    public void CoalesceRuns_MergesIdenticalAdjacent()
    {
        var p = new Paragraph
        {
            Inlines =
            {
                new Run { Text = "ab" },
                new Run { Text = "cd" }, // same formatting -> should merge
            },
        };
        TextRange.CoalesceRuns(p);
        Assert.Single(p.Inlines);
        Assert.Equal("abcd", ((Run)p.Inlines[0]).Text);
    }
}
