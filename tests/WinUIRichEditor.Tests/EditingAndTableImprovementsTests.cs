using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Regression tests for PR #84 (the port of upstream AvaloniaRichEditor PR #92): cell merge order,
/// multi-paragraph copy inside one cell, TSV paste, the 32-level table nesting ceiling and non-finite numbers /
/// font names on save. Each was red against the code before #84.</summary>
[Collection(UiTests.Collection)]
public class EditingAndTableImprovementsTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static void Caret(RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
    }

    private static void InsertTsv(RichEditor ed, string tsv)
        => T.GetMethod("InsertTableFromTsv", NP)!.Invoke(ed, new object[] { tsv });

    private static string Text(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // ---- merge ----

    [Fact]
    public void MergeCells_MultiParagraphCells_KeepsEveryParagraphInCellOrder()
    {
        var tb = new TableBlock(1, 2);
        foreach (var (cell, a, b) in new[] { (tb.Cells[0][0], "A1", "A2"), (tb.Cells[0][1], "B1", "B2") })
        {
            cell.Blocks.Clear();
            cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = a } }, Parent = cell });
            cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = b } }, Parent = cell });
        }

        tb.MergeCells(0, 0, 0, 1);

        Assert.Equal(new[] { "A1", "A2", "B1", "B2" }, tb.Cells[0][0].Blocks.OfType<Paragraph>().Select(Text).ToArray());
    }

    // ---- copy ----

    [Fact]
    public void Copy_SelectionInOneCell_KeepsTheCellsBlocksAndListFormat() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 2);
        var cell = tb.Cells[0][0];
        cell.Blocks.Clear();
        var p1 = new Paragraph { ListType = ListKind.Bullet, Inlines = { new Run { Text = "First" } }, Parent = cell };
        var p2 = new Paragraph { ListType = ListKind.Bullet, Inlines = { new Run { Text = "Second" } }, Parent = cell };
        cell.Blocks.Add(p1);
        cell.Blocks.Add(new DividerBlock { Parent = cell }); // flattening to paragraphs dropped this
        cell.Blocks.Add(p2);
        var ed = new RichEditor { Document = new FlowDocument { Blocks = { new Paragraph(), tb, new Paragraph() } } };

        var copied = (FlowDocument)T.GetMethod("BuildSelectionDocument", NP)!
            .Invoke(ed, new object[] { new TextRange(new TextPointer(p1, 0), new TextPointer(p2, 6)) })!;

        Assert.Equal(new[] { typeof(Paragraph), typeof(DividerBlock), typeof(Paragraph) }, copied.Blocks.Select(b => b.GetType()).ToArray());
        var paras = copied.Blocks.OfType<Paragraph>().ToList();
        Assert.Equal(new[] { "First", "Second" }, paras.Select(Text).ToArray());
        Assert.All(paras, p => Assert.Equal(ListKind.Bullet, p.ListType));
    });

    // ---- TSV paste ----

    [Fact]
    public void TsvPaste_BlankLinesBetweenRows_AreNotRows() => UiThread.Run(() =>
    {
        var p = new Paragraph { Inlines = { new Run { Text = "" } } };
        var ed = new RichEditor { Document = new FlowDocument { Blocks = { p } } };
        Caret(ed, p, 0);

        InsertTsv(ed, "a\tb\n\nc\td\n");

        var tb = Assert.Single(ed.Document!.Blocks.OfType<TableBlock>());
        Assert.Equal(2, tb.Rows);
        Assert.Equal(2, tb.Columns);
    });

    [Fact]
    public void TsvPaste_CaretInTableCell_FillsTheCellsAndAddsRows() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 2);
        var ed = new RichEditor { Document = new FlowDocument { Blocks = { new Paragraph(), tb, new Paragraph() } } };
        Caret(ed, tb.Cells[0][0].Para, 0);

        InsertTsv(ed, "A\tB\nC\tD");

        Assert.Single(ed.Document!.Blocks.OfType<TableBlock>());
        Assert.Empty(tb.Cells[0][0].Blocks.OfType<TableBlock>());
        Assert.Equal(2, tb.Rows);
        Assert.Equal(new[] { "A", "B", "C", "D" },
            new[] { tb.Cells[0][0], tb.Cells[0][1], tb.Cells[1][0], tb.Cells[1][1] }.Select(c => Text(c.Para)).ToArray());
    });

    // ---- 32-level nesting ceiling ----

    [Fact]
    public void InsertTable_At32Levels_DoesNothingAndPushesNoUndo() => UiThread.Run(() =>
    {
        var root = new TableBlock(1, 1);
        var deepest = root;
        for (int i = 1; i < TableNesting.Max; i++)
        {
            var next = new TableBlock(1, 1);
            deepest.Cells[0][0].Blocks.Clear();
            deepest.Cells[0][0].Blocks.Add(next);
            deepest = next;
        }
        var ed = new RichEditor { Document = new FlowDocument { Blocks = { new Paragraph(), root, new Paragraph() } } };
        ed.NotifyDocumentChanged();
        Caret(ed, deepest.Cells[0][0].Para, 0);
        Assert.False(ed.CanUndo);

        ed.InsertTable(2, 2);

        Assert.False(ed.CanUndo);
        Assert.Empty(deepest.Cells[0][0].Blocks.OfType<TableBlock>());
    });

    [Fact]
    public void HtmlImport_TablesNestedPast32Levels_AreCappedAt32()
    {
        const int depth = 40;
        string html = string.Concat(Enumerable.Repeat("<table><tr><td>", depth)) + "deep" + string.Concat(Enumerable.Repeat("</td></tr></table>", depth));

        var doc = HtmlDocumentFormatter.ParseHtml(html);

        int levels = 0;
        for (Block? b = doc.Blocks.OfType<TableBlock>().FirstOrDefault(); b is TableBlock t; b = t.Cells[0][0].Blocks.OfType<TableBlock>().FirstOrDefault())
            levels++;
        Assert.InRange(levels, 1, TableNesting.Max);
    }

    // ---- save ----

    [Fact]
    public void Serialize_NonFiniteNumbers_DoesNotThrowAndReadsBack()
    {
        var tb = new TableBlock(1, 1) { Indent = double.PositiveInfinity };
        tb.ColumnWidths[0] = double.NaN;
        tb.RowHeights.Add(double.PositiveInfinity);
        var doc = new FlowDocument
        {
            PageSetup = new PageSetup { Margin = new PageMargins(double.PositiveInfinity, double.NaN, 20, 20) },
            Blocks =
            {
                new Paragraph
                {
                    Indent = double.PositiveInfinity,
                    MarginRight = double.NegativeInfinity,
                    Inlines = { new Run { Text = "hi", FontSize = double.PositiveInfinity } }
                },
                tb
            }
        };

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc));

        var table = Assert.Single(back.Blocks.OfType<TableBlock>());
        Assert.All(table.ColumnWidths, w => Assert.True(double.IsFinite(w)));
    }

    [Fact]
    public void HtmlExport_SemicolonInFontName_CannotStartAnotherDeclaration()
    {
        var doc = new FlowDocument { Blocks = { new Paragraph { Inlines = { new Run { Text = "x", FontFamily = "MyFont;color:red" } } } } };

        string html = HtmlDocumentFormatter.ToHtml(doc);

        Assert.DoesNotContain("MyFont;color:red", html);
        Assert.Contains("font-family:'MyFontcolor:red'", html);
    }
}
