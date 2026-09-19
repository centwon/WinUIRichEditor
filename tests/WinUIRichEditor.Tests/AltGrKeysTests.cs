using Windows.System;
using WinUIRichEditor.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>AltGr arrives as Ctrl+Alt, so on a layout with AltGr characters a Ctrl+Alt shortcut swallowed typing —
/// the heading keys Ctrl+Alt+1..6 took German AltGr+2 (²) and AltGr+3 (³) (2026-09-19). The editor now asks the
/// current layout whether the chord makes a character (AltGrKeys) and, if it does, lets it be typed.
/// <para>The decision is tested with injected text; the OS query can only be exercised on the layouts this machine
/// has, so it is checked for what every layout agrees on.</para></summary>
public class AltGrKeysTests
{
    [Theory]
    [InlineData(true, true, "²", true)]    // German AltGr+2
    [InlineData(true, true, "ć", true)]    // Polish AltGr+C
    [InlineData(true, true, null, false)]  // Ctrl+Alt+1 on a layout without AltGr: the heading shortcut
    [InlineData(true, true, "", false)]
    [InlineData(true, true, "", false)] // a control character is not typing
    [InlineData(true, false, "a", false)]  // Ctrl alone is never AltGr
    [InlineData(false, true, "a", false)]
    public void IsTyping_OnlyForACtrlAltChordThatMakesACharacter(bool ctrl, bool alt, string? produced, bool expected)
        => Assert.Equal(expected, AltGrKeys.IsTyping(ctrl, alt, () => produced));

    // The query is not even made unless both modifiers are down — it runs on every key press.
    [Fact]
    public void IsTyping_DoesNotQueryTheLayout_WithoutCtrlAlt()
    {
        bool asked = false;
        AltGrKeys.IsTyping(true, false, () => { asked = true; return "x"; });
        AltGrKeys.IsTyping(false, false, () => { asked = true; return "x"; });
        Assert.False(asked);
    }

    // The P/Invoke really reaches the layout: a plain A is "a" on every Latin layout this suite runs on.
    [Fact]
    public void TheLayoutQuery_Works()
    {
        var layout = GetLayout();
        Assert.Equal("a", AltGrKeys.TextFor(VirtualKey.A, shift: false, ctrlAlt: false, layout));
        Assert.Equal("A", AltGrKeys.TextFor(VirtualKey.A, shift: true, ctrlAlt: false, layout));
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern System.IntPtr GetKeyboardLayout(uint threadId);
    private static System.IntPtr GetLayout() => GetKeyboardLayout(0);
}
