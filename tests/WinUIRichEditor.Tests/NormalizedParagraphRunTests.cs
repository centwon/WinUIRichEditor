using System.Linq;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

// RTF has no empty run to write, so a blank line read back with none, where the editor's own empty paragraphs hold one:
// the next save differed from the last, and the upstream control wrote the same document differently (2026-11-08).
public class NormalizedParagraphRunTests
{
    [Fact]
    public void ABlankRtfLine_ReadsWithOneEmptyRun()
    {
        var doc = RtfDocumentFormatter.Parse(@"{\rtf1 \pard a\par \pard\par \pard b\par}");
        var blank = doc.Blocks.OfType<Paragraph>().ElementAt(1);
        var run = Assert.IsType<Run>(Assert.Single(blank.Inlines));
        Assert.Equal("", run.Text);
    }
}
