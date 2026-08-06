using System;
using WinUIRichEditor.Controls;
using Xunit;
using Kind = WinUIRichEditor.Controls.RichEditor.ContextMenuKind;
using Target = WinUIRichEditor.Controls.RichEditor.PointerTarget;

namespace WinUIRichEditor.Tests;

/// <summary>The three gates that used to sit behind WinRT event args, now pure functions.
/// <para>Each was unreachable for the same reason: the decision lived inside a handler taking a
/// <c>RightTappedRoutedEventArgs</c>, a <c>PointerRoutedEventArgs</c> or a
/// <c>CoreTextTextUpdatingEventArgs</c>, none of which can be constructed. Splitting the DECISION from
/// the acting leaves the handlers doing hit-testing and mutation — still untestable, and still the part
/// least likely to be wrong — while the priority orders and the offset arithmetic, which are contracts,
/// come out where they can be pinned.</para>
/// <para>No WinUI runtime needed: these take plain values and return plain values, so unlike the rest of
/// the control-level suite they run headless.</para>
/// </summary>
public class ExtractedGateTests
{
    // The decision types are internal (the public surface is frozen), and xunit requires a PUBLIC test
    // class — so an internal type may not appear in a test method's SIGNATURE. Theory parameters use a
    // plain discriminator and resolve it in the body.
    private static Kind KindOf(int i) => i switch
    {
        0 => Kind.BlockImage,
        1 => Kind.InlineImage,
        _ => Kind.InlineTable,
    };

    // ---- which context menu a right-click opens -----------------------------------------------------

    // An object under the pointer wins over any text menu: you right-clicked the image, not the line it
    // sits on. Asserted with every text-state combination underneath, because the object cases must not
    // depend on them at all.
    [Theory]
    [InlineData(true, false, false, 0)]
    [InlineData(false, true, false, 1)]
    [InlineData(false, false, true, 2)]
    public void AnObjectUnderThePointer_WinsOverEveryTextMenu(bool block, bool inlineImg, bool inlineTable, int kind)
    {
        var expected = KindOf(kind);
        foreach (bool readOnly in new[] { false, true })
            foreach (bool hasSel in new[] { false, true })
                foreach (string? link in new[] { null, "https://example.com/x" })
                    Assert.Equal(expected, RichEditor.ChooseContextMenu(block, inlineImg, inlineTable, readOnly, hasSel, link));
    }

    // The object order among themselves: block image, then inline image, then inline table.
    [Fact]
    public void OverlappingObjects_ResolveInAFixedOrder()
    {
        Assert.Equal(Kind.BlockImage, RichEditor.ChooseContextMenu(true, true, true, false, false, null));
        Assert.Equal(Kind.InlineImage, RichEditor.ChooseContextMenu(false, true, true, false, false, null));
    }

    // A viewer offers copy, never edit — so read-only beats the link menu and the full menu both.
    [Theory]
    [InlineData(false, null)]
    [InlineData(true, null)]
    [InlineData(false, "https://example.com/x")]
    [InlineData(true, "https://example.com/x")]
    public void ReadOnly_AlwaysGetsTheViewerMenu(bool hasSel, string? link)
        => Assert.Equal(Kind.ReadOnlyText, RichEditor.ChooseContextMenu(false, false, false, true, hasSel, link));

    // The concise link menu replaces the full one ONLY without a selection: with one, the user is acting
    // on the selection, not on whatever link happens to be under the pointer.
    [Fact]
    public void TheLinkMenu_OnlyReplacesTheFullMenuWhenNothingIsSelected()
    {
        Assert.Equal(Kind.Link, RichEditor.ChooseContextMenu(false, false, false, false, false, "https://example.com/x"));
        Assert.Equal(Kind.Text, RichEditor.ChooseContextMenu(false, false, false, false, true, "https://example.com/x"));
        Assert.Equal(Kind.Text, RichEditor.ChooseContextMenu(false, false, false, false, false, null));
        Assert.Equal(Kind.Text, RichEditor.ChooseContextMenu(false, false, false, false, false, ""));
    }

    // ---- what a pointer press lands on ---------------------------------------------------------------

    // The contract this order exists for: a resize handle's grab band extends OUTSIDE its own rect, so
    // it lands inside a neighbouring object. If selection were tested first the neighbour would swallow
    // the grab and the handle could not be used at all — which is exactly what happened once.
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void AResizeHandle_BeatsEverySelectionClickUnderIt(bool blockHandle, bool inlineHandle)
    {
        var expected = blockHandle ? Target.SelectedBlockImageHandle : Target.SelectedInlineImageHandle;

        // Every selection target simultaneously true underneath: the handle still wins.
        Assert.Equal(expected, RichEditor.ChoosePointerTarget(
            isReadOnly: false, blockHandle, inlineHandle,
            inCellImage: true, inInlineImage: true, inBlockImage: true));
    }

    // Handles only exist while editing, so a viewer goes straight to selection.
    [Fact]
    public void ReadOnly_IgnoresResizeHandles_AndStillSelects()
    {
        Assert.Equal(Target.CellImage, RichEditor.ChoosePointerTarget(
            isReadOnly: true, onSelectedBlockImageHandle: true, onSelectedInlineImageHandle: true,
            inCellImage: true, inInlineImage: false, inBlockImage: false));

        Assert.Equal(Target.None, RichEditor.ChoosePointerTarget(
            isReadOnly: true, onSelectedBlockImageHandle: true, onSelectedInlineImageHandle: true,
            inCellImage: false, inInlineImage: false, inBlockImage: false));
    }

    // Innermost first: a cell-hosted image is drawn inside a table that also covers the point, and an
    // inline image sits within text where the block hit-test is coarser.
    [Fact]
    public void SelectionTargets_ResolveInnermostFirst()
    {
        Assert.Equal(Target.CellImage, RichEditor.ChoosePointerTarget(false, false, false, true, true, true));
        Assert.Equal(Target.InlineImage, RichEditor.ChoosePointerTarget(false, false, false, false, true, true));
        Assert.Equal(Target.BlockImage, RichEditor.ChoosePointerTarget(false, false, false, false, false, true));
        Assert.Equal(Target.None, RichEditor.ChoosePointerTarget(false, false, false, false, false, false));
    }

    // ---- the IME's offset arithmetic ------------------------------------------------------------------

    [Fact]
    public void ImeRange_IsTakenAsReportedWhenNothingShiftedIt()
        => Assert.Equal((2, 5), RichEditor.MapImeRange(2, 5, delta: 0, paragraphLength: 10));

    // The delta exists because a cross-paragraph selection was reported to the IME as a collapsed caret
    // and then deleted: every range the IME reports afterwards is off by how far the deletion moved it.
    [Theory]
    [InlineData(3, 2, 5, 7)]
    [InlineData(-2, 4, 2, 4)]
    public void ImeRange_AppliesTheShiftFromADeletedSelection(int delta, int start, int expectedStart, int expectedEnd)
    {
        var (s, e) = RichEditor.MapImeRange(start, start + 2, delta, paragraphLength: 20);
        Assert.Equal(expectedStart, s);
        Assert.Equal(expectedEnd, e);
    }

    // Out-of-range input is the normal case, not an error: the IME's view of the buffer lags the
    // document. Everything has to land inside the paragraph.
    [Theory]
    [InlineData(-50, 0, 0)]
    [InlineData(500, 10, 10)]
    public void ImeRange_IsClampedIntoTheParagraph(int delta, int expectedStart, int expectedEnd)
    {
        var (s, e) = RichEditor.MapImeRange(1, 3, delta, paragraphLength: 10);
        Assert.Equal(expectedStart, s);
        Assert.Equal(expectedEnd, e);
    }

    // An inverted span would delete text before the start — the end is pinned to the start, never below.
    [Fact]
    public void ImeRange_NeverEndsBeforeItStarts()
    {
        var (s, e) = RichEditor.MapImeRange(reportedStart: 8, reportedEnd: 2, delta: 0, paragraphLength: 10);
        Assert.Equal(8, s);
        Assert.True(e >= s, $"the span runs backwards ({s} -> {e})");
        Assert.Equal(8, e);
    }

    [Theory]
    [InlineData(4, 0, 10, 4)]
    [InlineData(4, 3, 10, 7)]
    [InlineData(4, -9, 10, 0)]     // clamped at the start
    [InlineData(4, 99, 10, 10)]    // clamped at the end
    public void ImeCaret_IsShiftedAndClampedTheSameWay(int reported, int delta, int length, int expected)
        => Assert.Equal(expected, RichEditor.MapImeCaret(reported, delta, length));

    // An empty paragraph has exactly one valid position, whatever the IME reports.
    [Fact]
    public void ImeOffsets_InAnEmptyParagraph_CollapseToZero()
    {
        Assert.Equal((0, 0), RichEditor.MapImeRange(5, 9, delta: 2, paragraphLength: 0));
        Assert.Equal(0, RichEditor.MapImeCaret(5, 2, 0));
    }
}
