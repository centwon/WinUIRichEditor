using System.Linq;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Page margins, ported from upstream (AvaloniaRichEditor PR #50/#52, <c>PageMarginTests</c>) on
/// 2026-09-24. They were two constants (48 x 40 DIP) that nothing could reach: a host could pick the paper but
/// not how much of it to write on, and an RTF from Word or HWP had its margins dropped. Now they are four sides,
/// in millimetres, on the document's PageSetup — saved with it and applied on load, as the paper is.
/// <para>A margin is also the one page setting that can make the page unusable (negative, or two sides past the
/// paper). It arrives from a file (untrusted) and from a binding, so both entries are tested.</para></summary>
[Collection(UiTests.Collection)]
public class PageMarginTests
{
    private static readonly PageMargins Wide = new(25, 20, 25, 20); // mm

    private static FlowDocument A4Doc(PageMargins? margin = null, int paragraphs = 1)
    {
        var doc = new FlowDocument();
        for (int i = 0; i < paragraphs; i++) doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "line" } } });
        doc.PageSetup = new PageSetup { PageSize = RichEditorPageSize.A4, Margin = margin ?? PageSetup.DefaultMargin };
        return doc;
    }

    [Fact]
    public void TheContentColumnFollowsTheMargins() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = A4Doc() };
        double before = ed.PaperContentWidth;

        ed.PageMargin = Wide;

        // A4 is 794 DIP (210 mm) wide. 15 mm a side = 56.7 DIP; 25 mm = 94.5 DIP — each rounded to a whole DIP
        // on the way in (see PagePadLeft), so the content box sits on whole pixels.
        Assert.Equal(794 - 2 * System.Math.Round(15 * PageSetup.DipsPerMm), before, 3);
        Assert.Equal(794 - 2 * System.Math.Round(25 * PageSetup.DipsPerMm), ed.PaperContentWidth, 3);
    });

    [Fact]
    public void ChangingTheMarginsRepaginates() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = A4Doc(paragraphs: 60) };
        int before = ed.GetPrintPageCount();

        ed.PageMargin = new PageMargins(12.7, 105, 12.7, 105); // a tall band top and bottom: less page to write on

        Assert.True(ed.GetPrintPageCount() > before, $"{before} pages before, {ed.GetPrintPageCount()} after");
    });

    // The margins belong to the document, like the paper: set them on the editor and a save carries them.
    [Fact]
    public void TheMarginsAreCapturedIntoTheDocument_AndRoundTripThroughJson() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = A4Doc() };

        ed.PageMargin = Wide;

        Assert.Equal(Wide, ed.Document!.PageSetup!.Margin);
        var once = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(ed.Document));
        var twice = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(once)); // round trips are run twice here
        Assert.Equal(Wide, twice.PageSetup!.Margin);
    });

    [Fact]
    public void OpeningADocumentAppliesItsMargins() => UiThread.Run(() =>
    {
        var ed = new RichEditor();

        ed.Document = A4Doc(Wide);

        Assert.Equal(Wide, ed.PageMargin);
    });

    // A document that never touched the margins keeps its bytes — the reason the fields are omitted at the default.
    [Fact]
    public void DefaultMarginsAreNotWrittenToJson()
    {
        string json = DocumentSerializer.Serialize(A4Doc());

        var setup = System.Text.Json.JsonDocument.Parse(json).RootElement.GetProperty("PageSetup");
        foreach (string side in new[] { "MarginLeft", "MarginTop", "MarginRight", "MarginBottom" })
            Assert.False(setup.TryGetProperty(side, out _), $"{side} was written at its default");
    }

    // RTF carries whole twips, so a millimetre comes back to within a twip, not identically.
    [Fact]
    public void MarginsAndPaperRoundTripThroughRtf_ToWithinATwip()
    {
        var back = RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(A4Doc(Wide))).PageSetup!;

        Assert.Equal(RichEditorPageSize.A4, back.PageSize); // this reader dropped the paper before the port
        const double twipMm = 25.4 / 1440;
        Assert.Equal(Wide.Left, back.Margin.Left, twipMm);
        Assert.Equal(Wide.Top, back.Margin.Top, twipMm);
        Assert.Equal(Wide.Right, back.Margin.Right, twipMm);
        Assert.Equal(Wide.Bottom, back.Margin.Bottom, twipMm);
    }

    // A file from another word processor keeps its own margins. 1440 twips = 1 inch (Word's default), 720 = half.
    [Fact]
    public void AnExternalRtfKeepsItsOwnMargins()
    {
        var doc = RtfDocumentFormatter.Parse(
            @"{\rtf1\ansi\paperw11910\paperh16845\margl1440\margr720\margt1440\margb720 hello\par}");

        Assert.Equal(new PageMargins(25.4, 25.4, 12.7, 12.7), doc.PageSetup!.Margin);
    }

    [Fact]
    public void AnRtfThatStatesOneSideKeepsTheDefaultsForTheOthers()
    {
        var doc = RtfDocumentFormatter.Parse(@"{\rtf1\ansi\paperw11910\paperh16845\margl1440 hello\par}");

        var d = PageSetup.DefaultMargin;
        Assert.Equal(new PageMargins(25.4, d.Top, d.Right, d.Bottom), doc.PageSetup!.Margin);
    }

    // ---- margins that leave no page ---------------------------------------------------------------

    [Theory]
    [InlineData(-10.0, 10.0)]            // negative
    [InlineData(120.0, 10.0)]            // 2 x 120 mm > A4's 210 mm across
    [InlineData(12.7, 160.0)]            // 2 x 160 mm > A4's 297 mm down
    [InlineData(double.NaN, 10.0)]
    [InlineData(double.PositiveInfinity, 10.0)]
    public void AMarginThatLeavesNoPage_IsRefusedByTheProperty(double x, double y) => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = A4Doc() };

        ed.PageMargin = new PageMargins(x, y, x, y);

        Assert.Equal(PageSetup.DefaultMargin, ed.PageMargin); // kept the last usable value
        Assert.True(ed.PaperContentWidth > 0);
    });

    [Fact]
    public void AJsonFileWithAMarginThatLeavesNoPage_FallsBackToTheDefault()
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(DocumentSerializer.Serialize(A4Doc()))!;
        node["PageSetup"]!["MarginLeft"] = 900;

        var doc = DocumentSerializer.Deserialize(node.ToJsonString());

        Assert.Equal(PageSetup.DefaultMargin, doc.PageSetup!.Margin);
    }

    [Fact]
    public void AnRtfWithAMarginThatLeavesNoPage_FallsBackToTheDefault()
    {
        var doc = RtfDocumentFormatter.Parse(@"{\rtf1\ansi\paperw11910\paperh16845\margl20000\margr20000 hello\par}");

        Assert.Equal(PageSetup.DefaultMargin, doc.PageSetup!.Margin);
    }

    // Margins can arrive before the paper, and one that fits A4 (the fallback) need not fit the A5 declared later.
    [Fact]
    public void MarginsStatedBeforeASmallerPaper_AreRecheckedAgainstIt()
    {
        var doc = RtfDocumentFormatter.Parse(@"{\rtf1\ansi\margl4400\margr4400\paperw8385\paperh11910 hello\par}");

        Assert.Equal(RichEditorPageSize.A5, doc.PageSetup!.PageSize);
        Assert.Equal(PageSetup.DefaultMargin, doc.PageSetup.Margin);
    }

    // ---- the gap above an object block (Block.AutoTopMargin) -------------------------------------

    // A table, picture or divider sits one line gap below the text above it; paragraphs carry no bottom margin,
    // so it used to butt straight against that text. A stated margin, 0 included, is used as given.
    [Fact]
    public void ATableStartsOneLineGapBelowTheTextAbove_UnlessItStatesItsOwnMargin() => UiThread.Run(() =>
    {
        var auto = new TableBlock(1, 1);
        var zero = new TableBlock(1, 1) { MarginTop = 0 };
        Assert.True(double.IsNaN(auto.MarginTop)); // precondition: the new default
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "above" } } });
        doc.Blocks.Add(auto);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "between" } } });
        doc.Blocks.Add(zero);
        var ed = new RichEditor { Document = doc };

        var map = (System.Collections.IList)typeof(RichEditor).GetMethod("EnsureBlockLayout",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(ed, new object[] { 600.0 })!;
        double TopOf(int i) => (double)map[i]!.GetType().GetField("Item2")!.GetValue(map[i])!;
        double BottomOf(int i) => TopOf(i) + (double)map[i]!.GetType().GetField("Item3")!.GetValue(map[i])!;

        Assert.Equal(ed.AutoBlockTopGap, TopOf(1) - BottomOf(0) - ((Paragraph)doc.Blocks[0]).MarginBottom, 3);
        Assert.True(ed.AutoBlockTopGap > 0);
        Assert.Equal(0, TopOf(3) - BottomOf(2) - ((Paragraph)doc.Blocks[2]).MarginBottom, 3);
    });

    // JSON has no NaN: "auto" goes out as no field and comes back as NaN; an explicit 0 stays 0.
    [Fact]
    public void TheAutoTopMarginRoundTripsThroughJson()
    {
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "x" } } });
        doc.Blocks.Add(new TableBlock(1, 1));
        doc.Blocks.Add(new DividerBlock { MarginTop = 0 });
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "y" } } });

        var back = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(DocumentSerializer.Deserialize(DocumentSerializer.Serialize(doc))));

        Assert.True(double.IsNaN(back.Blocks.OfType<TableBlock>().Single().MarginTop));
        Assert.Equal(0, back.Blocks.OfType<DividerBlock>().Single().MarginTop);
    }
}

/// <summary>The toolbar's margin picker (upstream PR #52, ported 2026-09-24): five steps in millimetres, and a
/// margin that matches no step is stated in millimetres rather than as a step it is not.</summary>
[Collection(UiTests.Collection)]
public class ToolbarMarginPickerTests
{
    private const System.Reflection.BindingFlags NP = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static string LabelAfterSync(RichEditorToolbar tb)
    {
        typeof(RichEditorToolbar).GetMethod("Sync", NP)!.Invoke(tb, null);
        var label = (Microsoft.UI.Xaml.Controls.TextBlock?)typeof(RichEditorToolbar).GetField("_marginLabel", NP)!.GetValue(tb);
        Assert.NotNull(label); // precondition: the picker was built at this level
        return label!.Text;
    }

    [Theory]
    [InlineData(15.0, "MarginNormal")]
    [InlineData(5.0, "MarginNarrowest")]
    [InlineData(30.0, "MarginWidest")]
    public void TheBoxNamesTheStepInForce(double mm, string key) => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.A4, PageMargin = new PageMargins(mm) };
        var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };

        Assert.Equal(RichEditorLocalization.GetString(key), LabelAfterSync(tb));
    });

    [Fact]
    public void AMarginThatIsNoStep_IsStatedInMillimetres() => UiThread.Run(() =>
    {
        var ed = new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.A4, PageMargin = PageMargins.Symmetric(25.4, 12.7) };
        var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };

        Assert.Equal("25.4 / 12.7mm", LabelAfterSync(tb));
    });
}

/// <summary>The right-click margin menu's Top list for an object block starts with "Auto (one line)" — the default
/// since Block.AutoTopMargin — checked while it is in force, and able to restore it (upstream round 34 decision).</summary>
[Collection(UiTests.Collection)]
public class MarginMenuAutoTests
{
    private const System.Reflection.BindingFlags NP = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    private static Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem TopList(RichEditor ed, Block target)
    {
        var sub = (Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem)typeof(RichEditor).GetMethod("MarginMenu", NP)!.Invoke(ed, new object[] { target })!;
        return sub.Items.OfType<Microsoft.UI.Xaml.Controls.MenuFlyoutSubItem>()
            .Single(s => s.Text == RichEditorLocalization.GetString("MarginTop"));
    }

    [Fact]
    public void ATablesTopList_LeadsWithAuto_CheckedByDefault_AndRestoresIt() => UiThread.Run(() =>
    {
        var tb = new TableBlock(1, 1);
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "x" } } });
        doc.Blocks.Add(tb);
        var ed = new RichEditor { Document = doc };

        var first = (Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem)TopList(ed, tb).Items[0];
        Assert.Equal(RichEditorLocalization.GetString("MarginAuto"), first.Text);
        Assert.True(first.IsChecked);

        tb.MarginTop = 10;
        System.Func<double> get = () => tb.MarginTop;
        System.Action<double> set = v => tb.MarginTop = v;
        typeof(RichEditor).GetMethod("PickMargin", NP)!.Invoke(ed, new object[] { get, set, Block.AutoTopMargin });
        Assert.True(double.IsNaN(tb.MarginTop));
    });

    [Fact]
    public void AParagraphsTopList_HasNoAuto() => UiThread.Run(() =>
    {
        var p = new Paragraph { Inlines = { new Run { Text = "x" } } };
        var doc = new FlowDocument();
        doc.Blocks.Add(p);
        var ed = new RichEditor { Document = doc };

        Assert.DoesNotContain(TopList(ed, p).Items.OfType<Microsoft.UI.Xaml.Controls.RadioMenuFlyoutItem>(),
            i => i.Text == RichEditorLocalization.GetString("MarginAuto"));
    });
}
