using System.Linq;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

// Upstream second review (AvaloniaRichEditor, 2026-11-01). The editor-level fixes (no-op dialog OKs, waits re-checked,
// Replace line endings) have no test here: this suite has no headless editor to drive them.
public class ReviewFixes20261101bTests
{
    // A line break between \uN and its fallback was counted as the fallback, and the real "?" went into the text.
    [Fact]
    public void UnicodeFallback_AfterALineBreak_IsSkipped()
    {
        var doc = RtfDocumentFormatter.Parse("{\\rtf1\\ansi a\\u1234\r\n?b\\par}");
        string text = string.Concat(doc.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines.OfType<Run>()).Select(r => r.Text));
        Assert.Equal("a\u04D2b", text);
    }
}
