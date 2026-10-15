using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

// Data-loss fixes added for 1.3.0 (2026-10-15) that go through the editor.
[Collection(UiTests.Collection)]
public class DataLossFixesEditorTests
{
    private static FlowDocument Copied(RichEditor ed, TextPointer from, TextPointer to)
        => (FlowDocument)typeof(RichEditor).GetMethod("BuildSelectionDocument", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(ed, new object[] { new TextRange(from, to) })!;

    private static Paragraph Para(string text, ListKind list = ListKind.None)
        => new() { ListType = list, Inlines = { new Run { Text = text } } };

    private static string TextOf(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // Several paragraphs copied from inside one table cell went as bare runs: a bulleted list pasted as plain lines.
    [Fact]
    public void Copy_OfSeveralParagraphsInOneCell_KeepsTheirParagraphFormat() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 1);
        var cell = tb.Cells[0][0];
        cell.Blocks.Clear();
        cell.Blocks.Add(Para("one", ListKind.Bullet));
        cell.Blocks.Add(Para("two", ListKind.Bullet));
        var doc = new FlowDocument();
        doc.Blocks.Add(Para("above"));
        doc.Blocks.Add(tb);
        doc.Blocks.Add(Para("below"));
        var ed = new RichEditor { Document = doc };
        var paras = ((TableBlock)ed.Document!.Blocks.First(b => b is TableBlock)).Cells[0][0].Blocks.OfType<Paragraph>().ToList();

        var copy = Copied(ed, new TextPointer(paras[0], 1), new TextPointer(paras[1], 2));

        var got = copy.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(new[] { "ne", "tw" }, got.Select(TextOf));
        Assert.All(got, p => Assert.Equal(ListKind.Bullet, p.ListType));
    });

    // A selection across two paragraphs of an inline table's cell was cut at the first.
    [Fact]
    public void Copy_AcrossTwoParagraphsOfAnInlineTableCell_TakesBoth() => UiThread.Run(() =>
    {
        var t = new TableBlock(1, 1);
        var cell = t.Cells[0][0];
        cell.Blocks.Clear();
        cell.Blocks.Add(Para("first"));
        cell.Blocks.Add(Para("second"));
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "a" }, new InlineTable { Table = t }, new Run { Text = "b" } } });
        var ed = new RichEditor { Document = doc };
        var host = ed.Document!.Blocks.OfType<Paragraph>().First(p => p.Inlines.OfType<InlineTable>().Any());
        var paras = host.Inlines.OfType<InlineTable>().Single().Table.Cells[0][0].Blocks.OfType<Paragraph>().ToList();

        var copy = Copied(ed, new TextPointer(paras[0], 2), new TextPointer(paras[1], 3));

        Assert.Equal(new[] { "rst", "sec" }, copy.Blocks.OfType<Paragraph>().Select(TextOf));
    });
}
