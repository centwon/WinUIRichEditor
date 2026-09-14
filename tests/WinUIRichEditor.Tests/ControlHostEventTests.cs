using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The events a host builds its chrome on — <see cref="RichEditor.SelectionChanged"/> (Copy and Delete
/// enabled, a status bar's selection) and <see cref="RichEditor.IsModifiedChanged"/> (the "*" in a title, "save
/// changes?") — fire where the state they announce changes. Neither had a test before 2026-09-14, when a probe
/// measured: selecting an image or a table as an object, F5 in an empty cell and F5 over an already-selected range
/// raised no SelectionChanged (the endpoints did not move); a right-click on a table's border raised nothing at
/// all; every open raised IsModifiedChanged twice ("modified", then not); and an edit raised it in the middle,
/// with the document as it was before the edit. Upstream shares the code of all of it.</summary>
[Collection(UiTests.Collection)]
public class ControlHostEventTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static object? Call(RichEditor ed, string name, params object?[] args)
    {
        try { return T.GetMethod(name, NP)!.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    // The caret collapsed at (p, offset), and the editor told about it — so a count taken afterwards sees only
    // what the step under test raises.
    private static void Caret(RichEditor ed, Paragraph p, int offset)
    {
        foreach (var f in new[] { "_caret", "_selStart", "_selEnd" }) T.GetField(f, NP)!.SetValue(ed, new TextPointer(p, offset));
        Call(ed, "RaiseStatusChanged");
    }

    private sealed class Count
    {
        public int Selection, Status, Modified;
        public Count(RichEditor ed)
        {
            ed.SelectionChanged += (_, _) => Selection++;
            ed.StatusChanged += (_, _) => Status++;
            ed.IsModifiedChanged += (_, _) => Modified++;
        }
    }

    private static (RichEditor ed, Paragraph p, ImageBlock img) WithImage()
    {
        var p = new Paragraph { Inlines = { new Run { Text = "ab" } } };
        var img = new ImageBlock { Width = 40, Height = 40 };
        var doc = new FlowDocument();
        doc.Blocks.Add(p); doc.Blocks.Add(img); doc.Blocks.Add(new Paragraph());
        return (new RichEditor { Document = doc }, p, img);
    }

    private static (RichEditor ed, TableBlock tb) WithTable(string cellText)
    {
        var tb = new TableBlock(2, 2);
        ((Run)tb.Cells[0][0].Para.Inlines[0]).Text = cellText;
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph()); doc.Blocks.Add(tb); doc.Blocks.Add(new Paragraph());
        return (new RichEditor { Document = doc }, tb);
    }

    // ---- SelectionChanged ---------------------------------------------------------------------------------

    [Fact]
    public void SelectingAnImage_RaisesSelectionChanged_ThoughTheCaretStays() => UiThread.Run(() =>
    {
        var (ed, p, img) = WithImage();
        Caret(ed, p, 1);
        var n = new Count(ed);

        Call(ed, "SelectBlockObject", img);

        Assert.True(ed.HasBlockSelection);
        Assert.Equal(1, n.Selection);
    });

    // The control for the snapshot: flushing again with nothing changed raises nothing — with an object selected
    // too, so a snapshot whose object slot compared unequal to itself (a boxed value, say) is caught here.
    [Fact]
    public void SelectionChanged_IsNotRaised_WhenNothingChanged() => UiThread.Run(() =>
    {
        var (ed, p, img) = WithImage();
        Caret(ed, p, 1);
        Call(ed, "SelectBlockObject", img);
        var n = new Count(ed);

        Call(ed, "RaiseStatusChanged");
        Call(ed, "RaiseStatusChanged");

        Assert.Equal(2, n.Status);
        Assert.Equal(0, n.Selection);
    });

    // An empty cell's one-cell block is the caret itself: (p, 0) to (p, 0).
    [Fact]
    public void F5_InAnEmptyCell_RaisesSelectionChanged() => UiThread.Run(() =>
    {
        var (ed, tb) = WithTable("");
        Caret(ed, tb.Cells[0][0].Para, 0);
        var n = new Count(ed);

        Call(ed, "SelectCellAsBlock", tb.Cells[0][0]);

        Assert.NotNull(Call(ed, "CellBlockSelection"));
        Assert.Equal(1, n.Selection);
    });

    // Ctrl+A's first stage in a cell selects exactly the cell's text; F5 then marks the same range as a cell
    // block — Delete now empties the cell, Copy takes a 1×1 table. Same endpoints, different selection.
    [Fact]
    public void F5_OverTheSameTextRange_RaisesSelectionChanged() => UiThread.Run(() =>
    {
        var (ed, tb) = WithTable("xyz");
        var cp = tb.Cells[0][0].Para;
        T.GetField("_selStart", NP)!.SetValue(ed, new TextPointer(cp, 0));
        T.GetField("_selEnd", NP)!.SetValue(ed, new TextPointer(cp, 3));
        T.GetField("_caret", NP)!.SetValue(ed, new TextPointer(cp, 3));
        Call(ed, "RaiseStatusChanged");
        var n = new Count(ed);

        Call(ed, "SelectCellAsBlock", tb.Cells[0][0]);

        Assert.NotNull(Call(ed, "CellBlockSelection"));
        Assert.Equal(1, n.Selection);
    });

    private static readonly Lazy<RichEditor> Hosted = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    // The right-click path selected the table and returned without a flush: no StatusChanged, no SelectionChanged,
    // until the next unrelated input — a toolbar showed the state from before the click.
    [Fact]
    public void RightClickingATablesBorder_RaisesStatusAndSelectionChanged()
    {
        var ed = Hosted.Value;
        UiThread.Run(() =>
        {
            var tb = new TableBlock(1, 2);
            var doc = new FlowDocument();
            doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
            doc.Blocks.Add(tb);
            ed.Document = doc;
            Call(ed, "RelayoutToViewport");
            Caret(ed, (Paragraph)doc.Blocks[0], 2);
            // The border band is found through the rects the renderer records; the test host does not draw, so
            // plant one far below the document, where only the band can answer.
            var rects = (IDictionary<TableBlock, Windows.Foundation.Rect>)T.GetField("_tableRects", NP)!.GetValue(ed)!;
            rects[tb] = new Windows.Foundation.Rect(20, 3000, 300, 100);
            var n = new Count(ed);

            ed.BuildContextMenuAt(new Windows.Foundation.Point(21, 3020));

            Assert.True(ed.HasBlockSelection);
            Assert.Equal(1, n.Status);
            Assert.Equal(1, n.Selection);
        });
    }

    // ---- IsModifiedChanged --------------------------------------------------------------------------------

    [Fact]
    public void LoadingIntoACleanEditor_RaisesNoIsModifiedChanged() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>first</p>");
        var n = new Count(ed);

        ed.LoadHtml("<p>abc</p>");

        Assert.False(ed.IsModified);
        Assert.Equal(0, n.Modified);
    });

    // The control for the load guard: over a MODIFIED document a load does change the flag, and says so once.
    [Fact]
    public void LoadingOverAModifiedDocument_RaisesIsModifiedChangedOnce() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>ab</p>");
        Caret(ed, (Paragraph)ed.Document!.Blocks[0], 2);
        ed.InsertText("X");
        Assert.True(ed.IsModified);
        var n = new Count(ed);

        ed.LoadHtml("<p>abc</p>");

        Assert.False(ed.IsModified);
        Assert.Equal(1, n.Modified);
    });

    // A host that saves or snapshots on "modified" has to see the edit it is told about. PushUndo sets the flag
    // BEFORE the command mutates, and the event used to go with it: the handler read "ab".
    [Fact]
    public void IsModifiedChanged_ComesAfterTheEdit_AndSeesIt() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>ab</p>");
        var p = (Paragraph)ed.Document!.Blocks[0];
        Caret(ed, p, 2);
        var seen = new List<string>();
        ed.IsModifiedChanged += (_, _) => seen.Add($"{ed.IsModified}:{((Run)p.Inlines[0]).Text}");

        ed.InsertText("X");

        Assert.Equal(new[] { "True:abX" }, seen);
    });

    // MarkSaved is the host's own call: it reports at once, not with the next flush.
    [Fact]
    public void MarkSaved_RaisesIsModifiedChanged_AtOnce() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>ab</p>");
        Caret(ed, (Paragraph)ed.Document!.Blocks[0], 2);
        ed.InsertText("X");
        var n = new Count(ed);

        ed.MarkSaved();

        Assert.False(ed.IsModified);
        Assert.Equal(1, n.Modified);
    });

    // Pinned as documented (upstream says the same): assigning Document directly is an edit — undo swaps its
    // snapshots in this way — and only the Load* methods and Clear start clean.
    [Fact]
    public void ARawDocumentAssignment_IsAChange() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>ab</p>");
        var n = new Count(ed);

        ed.Document = new FlowDocument();

        Assert.True(ed.IsModified);
        Assert.Equal(1, n.Modified);
    });

    // ---- LastFindMatchCase ----------------------------------------------------------------------------------

    // F3 repeats the last find with ITS case rule: after a case-sensitive "Ab", the lowercase "ab" is skipped.
    [Fact]
    public void FindAgain_KeepsTheLastFindsCaseRule() => UiThread.Run(() =>
    {
        var ed = new RichEditor();
        ed.LoadHtml("<p>Ab ab Ab</p>");
        Caret(ed, (Paragraph)ed.Document!.Blocks[0], 0);

        Assert.True(ed.FindNext("Ab", matchCase: true));
        Assert.True(ed.LastFindMatchCase);
        Assert.True(ed.FindAgain(backwards: false));

        var s = (TextPointer)T.GetField("_selStart", NP)!.GetValue(ed)!;
        Assert.Equal(6, s.Offset);
    });
}
