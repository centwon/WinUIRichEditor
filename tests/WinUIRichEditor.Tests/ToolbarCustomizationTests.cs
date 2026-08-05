using System;
using WinUIRichEditor.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

// RichEditorToolbar.FontSizes / Palette let a host replace the built-in options. Both are global state,
// so every test restores what it changed.
//
// These are reachable from a plain unit test only because the toolbar's static brushes are created
// LAZILY. They used to be field initializers, and a field initializer runs on first touch of ANY static
// member — so merely reading FontSizes ran the class initializer, which constructs SolidColorBrush,
// which needs the WinUI runtime, and the whole thing died with a COMException. The same trap hit hosts
// that assigned these before Application.Start.
public class ToolbarCustomizationTests
{
    [Fact]
    public void Statics_AreReachableWithoutTheWinUIRuntime()
    {
        // The regression guard for the lazy-brush change: if any static brush goes back to a field
        // initializer, this line throws COMException and every test below fails with it.
        Assert.NotEmpty(RichEditorToolbar.FontSizes);
        Assert.NotEmpty(RichEditorToolbar.Palette);
    }

    [Fact]
    public void FontSizes_RoundTripsAReplacement()
    {
        var original = RichEditorToolbar.FontSizes;
        try
        {
            RichEditorToolbar.FontSizes = new double[] { 11, 13, 17 };
            Assert.Equal(new double[] { 11, 13, 17 }, RichEditorToolbar.FontSizes);
        }
        finally { RichEditorToolbar.FontSizes = original; }
    }

    [Fact]
    public void Palette_RoundTripsAReplacement()
    {
        var original = RichEditorToolbar.Palette;
        try
        {
            RichEditorToolbar.Palette = new[] { "#112233", "#445566" };
            Assert.Equal(new[] { "#112233", "#445566" }, RichEditorToolbar.Palette);
        }
        finally { RichEditorToolbar.Palette = original; }
    }

    // ---- guards ------------------------------------------------------------
    //
    // Validated at ASSIGNMENT, not at use: the arrays are consumed while the toolbar builds itself, so a
    // bad value would otherwise surface as a crash inside Build() with nothing pointing back at the line
    // that caused it.

    [Fact]
    public void FontSizes_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => RichEditorToolbar.FontSizes = null!);

    [Fact]
    public void FontSizes_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => RichEditorToolbar.FontSizes = Array.Empty<double>());

    [Theory]
    [InlineData(0)]
    [InlineData(-12)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void FontSizes_RejectsSizesThatAreNotPositiveAndFinite(double bad)
        => Assert.Throws<ArgumentException>(() => RichEditorToolbar.FontSizes = new[] { 10, bad });

    [Fact]
    public void Palette_RejectsNull()
        => Assert.Throws<ArgumentNullException>(() => RichEditorToolbar.Palette = null!);

    [Fact]
    public void Palette_RejectsEmpty()
        => Assert.Throws<ArgumentException>(() => RichEditorToolbar.Palette = Array.Empty<string>());

    // A rejected assignment must not have half-applied.
    [Fact]
    public void RejectedAssignment_LeavesTheCurrentValuesIntact()
    {
        var sizes = RichEditorToolbar.FontSizes;
        var palette = RichEditorToolbar.Palette;
        Assert.Throws<ArgumentException>(() => RichEditorToolbar.FontSizes = Array.Empty<double>());
        Assert.Throws<ArgumentNullException>(() => RichEditorToolbar.Palette = null!);
        Assert.Same(sizes, RichEditorToolbar.FontSizes);
        Assert.Same(palette, RichEditorToolbar.Palette);
    }

    // Palette entry FORMAT is deliberately not validated — ParseHex falls back to black and the swatch
    // grid is cosmetic, so a typo shows up as a wrong colour rather than a toolbar that fails to build.
    // (The Avalonia peer's Color.Parse throws instead; that fallback is load-bearing there too.)
    [Fact]
    public void Palette_AcceptsAnUnparseableEntry()
    {
        var original = RichEditorToolbar.Palette;
        try
        {
            RichEditorToolbar.Palette = new[] { "not-a-colour", "#00FF00" };
            Assert.Equal(2, RichEditorToolbar.Palette.Length);
        }
        finally { RichEditorToolbar.Palette = original; }
    }
}
