using System.Linq;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

public class TableFormatTests
{
    private static FlowDocument DocWithTable()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "intro" } } });
        var tb = new TableBlock(2, 2);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = "A1";
        ((Run)tb.Cells[0][1].Para.Inlines[0]).Text = "B1";
        ((Run)tb.Cells[1][0].Para.Inlines[0]).Text = "A2";
        ((Run)tb.Cells[1][1].Para.Inlines[0]).Text = "B2";
        doc.Blocks.Add(tb);
        return doc;
    }

    private static string CellText(TableBlock tb, int r, int c)
        => string.Concat(tb.Cells[r][c].Para.Inlines.OfType<Run>().Select(x => x.Text));

    [Fact]
    public void Json_RoundTrips_Table()
    {
        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(DocWithTable()));
        var tb = back.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Columns);
        Assert.Equal("A1", CellText(tb, 0, 0));
        Assert.Equal("B2", CellText(tb, 1, 1));
    }

    [Fact]
    public void Html_RoundTrips_Table()
    {
        var html = HtmlDocumentFormatter.ToHtml(DocWithTable());
        var back = HtmlDocumentFormatter.ParseHtml(html);
        var tb = back.Blocks.OfType<TableBlock>().Single();
        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Columns);
        Assert.Equal("A1", CellText(tb, 0, 0));
        Assert.Equal("B2", CellText(tb, 1, 1));
    }

    [Fact]
    public void Json_RoundTrips_MergedCells()
    {
        var doc = new FlowDocument();
        var tb = new TableBlock(3, 3);
        tb.MergeCells(0, 0, 1, 1);
        doc.Blocks.Add(tb);

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));
        var bt = back.Blocks.OfType<TableBlock>().Single();
        Assert.True(bt.IsCovered(0, 1));
        Assert.True(bt.IsCovered(1, 1));
        var (cs, rs) = bt.SpanOf(0, 0);
        Assert.Equal(2, cs);
        Assert.Equal(2, rs);
    }
}
