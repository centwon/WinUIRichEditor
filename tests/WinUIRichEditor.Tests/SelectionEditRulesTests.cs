using System;
using System.Linq;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Selection and editing rules made the same as upstream's (2026-11-09).</summary>
[Collection(UiTests.Collection)]
public class SelectionEditRulesTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static Paragraph P(string text)
    {
        var p = new Paragraph();
        p.Inlines.Add(new Run { Text = text });
        return p;
    }

    private static RichEditor Editor(params Block[] blocks)
    {
        var doc = new FlowDocument();
        foreach (var b in blocks) doc.Blocks.Add(b);
        return new RichEditor { Document = doc };
    }

    private static void Caret(RichEditor ed, Paragraph p, int offset) => Select(ed, p, offset, p, offset);

    // A drag from (a, ao) to (b, bo): the caret sits at the drag's end.
    private static void Select(RichEditor ed, Paragraph a, int ao, Paragraph b, int bo)
    {
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(a, ao));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(b, bo));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(b, bo));
    }

    private static void Call(RichEditor ed, string name, params object?[] args)
    {
        try { T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
        }
    }

    private static string TextOf(Paragraph p) => string.Concat(p.Inlines.OfType<Run>().Select(r => r.Text));

    // ---- Tab over paragraphs ------------------------------------------------------------------------

    [Fact]
    public void Tab_OverSeveralParagraphs_IndentsEachAndKeepsTheText() => UiThread.Run(() =>
    {
        // Only the caret's list item moved, and without a list the key typed four spaces over the selection. Now each
        // paragraph steps in: a list item a level, any other an indent step; Shift+Tab steps them back.
        var a = P("first");
        var b = P("second");
        var ed = Editor(a, b);
        a.ListType = ListKind.Bullet;
        Select(ed, a, 1, b, 3);
        Call(ed, "HandleTab", false);
        Assert.Equal("first", TextOf(a));
        Assert.Equal("second", TextOf(b));
        Assert.Equal(1, a.ListLevel);
        Assert.Equal(0, a.Indent);
        Assert.Equal(20, b.Indent);

        Call(ed, "HandleTab", true);
        Assert.Equal(0, a.ListLevel);
        Assert.Equal(0, b.Indent);
    });

    [Fact]
    public void ShiftTab_TakesBackTheSpacesTabTyped_ElseOutdents() => UiThread.Run(() =>
    {
        // Shift+Tab outside a list did nothing.
        var a = P("ab");
        var ed = Editor(a);
        Caret(ed, a, 2);
        Call(ed, "HandleTab", false);
        Assert.Equal("ab    ", TextOf(a));
        Call(ed, "HandleTab", true);
        Assert.Equal("ab", TextOf(a));

        a.Indent = 40;
        Call(ed, "HandleTab", true);
        Assert.Equal(20, a.Indent);
    });

    // ---- "treat as character" inside a cell -----------------------------------------------------------

    [Fact]
    public void ATableInACell_ConvertsToInline_WithinItsCell_AndBack() => UiThread.Run(() =>
    {
        var outer = new TableBlock(1, 1);
        var nested = new TableBlock(1, 1);
        var cell = outer.Cells[0][0];
        cell.Blocks.Clear();
        var a = P("cell");
        cell.Blocks.Add(a);
        cell.Blocks.Add(nested);
        cell.Blocks.Add(P(""));
        var ed = Editor(P("top"), outer, P("end"));
        Call(ed, "ConvertTableBlockToInline", nested);
        Assert.DoesNotContain(cell.Blocks, x => x is TableBlock);
        var it = Assert.Single(a.Inlines.OfType<InlineTable>());

        Call(ed, "ConvertInlineTableToBlock", a, it);
        Assert.DoesNotContain(a.Inlines, i => i is InlineTable);
        Assert.Single(cell.Blocks.OfType<TableBlock>());
    });

    // ---- list toggle: one rule ----------------------------------------------------------------------

    [Fact]
    public void ListToggle_OnAMixedSelection_TurnsEveryParagraphOn() => UiThread.Run(() =>
    {
        // Outside a table the caret's paragraph used to decide: with the caret in the bulleted one, the toggle took the
        // bullets OFF both. A mixed selection turns the list on everywhere; only an all-bulleted selection turns it off.
        var a = P("first");
        var b = P("second");
        var ed = Editor(a, b);
        a.ListType = ListKind.Bullet;
        Select(ed, b, 2, a, 2);
        ed.ToggleBullet();
        Assert.All(ed.Document!.Blocks.OfType<Paragraph>(), p => Assert.Equal(ListKind.Bullet, p.ListType));

        ed.ToggleBullet();
        Assert.All(ed.Document.Blocks.OfType<Paragraph>(), p => Assert.Equal(ListKind.None, p.ListType));
    });

    [Fact]
    public void ListToggle_AcrossATable_ListsTheCellParagraphsToo() => UiThread.Run(() =>
    {
        // The outside-a-table path collected only top-level paragraphs, so the cells a selection crossed stayed unlisted.
        var a = P("before");
        var tb = new TableBlock(1, 1);
        var cellPara = P("cell");
        tb.Cells[0][0].Blocks.Clear();
        tb.Cells[0][0].Blocks.Add(cellPara);
        var b = P("after");
        var ed = Editor(a, tb, b);
        Select(ed, a, 0, b, 2);
        ed.ToggleBullet();
        Assert.Equal(ListKind.Bullet, cellPara.ListType);
        Assert.All(ed.Document!.Blocks.OfType<Paragraph>(), p => Assert.Equal(ListKind.Bullet, p.ListType));
    });
}
