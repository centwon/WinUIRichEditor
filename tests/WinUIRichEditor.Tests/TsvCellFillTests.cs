using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>TSV pasted with the caret in a table cell fills the table and adds rows, but never columns: the columns
/// past the table's right edge are dropped, as upstream (AvaloniaRichEditor PR #92) does.</summary>
[Collection(UiTests.Collection)]
public class TsvCellFillTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    [Fact]
    public void TsvPaste_InCell_WiderThanTheTable_DropsTheExtraColumns() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 2);
        var ed = new RichEditor { Document = new FlowDocument { Blocks = { new Paragraph(), tb, new Paragraph() } } };
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(tb.Cells[0][0].Para, 0));

        T.GetMethod("InsertTableFromTsv", NP)!.Invoke(ed, new object[] { "A\tB\tExtra\nC\tD\tExtra" });

        Assert.Equal(2, tb.Columns);
        Assert.Equal(2, tb.Rows);
        Assert.Equal(new[] { "A", "B", "C", "D" },
            new[] { tb.Cells[0][0], tb.Cells[0][1], tb.Cells[1][0], tb.Cells[1][1] }
                .Select(c => string.Concat(c.Para.Inlines.OfType<Run>().Select(r => r.Text))).ToArray());
    });
}
