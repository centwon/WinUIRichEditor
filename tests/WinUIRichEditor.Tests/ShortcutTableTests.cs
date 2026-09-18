using System.Linq;
using Windows.System;
using WinUIRichEditor.Controls;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The shortcut table (RichEditorShortcuts) is the single source for the key handler, the context-menu
/// hints and the toolbar tooltips — which is exactly why a mistake in it is invisible: the hint and the handler
/// agree with each other even when both are wrong. No test had referenced it (audit 2026-09-19); the audit found
/// it correct, and these keep it that way when entries are added. No runtime needed.</summary>
public class ShortcutTableTests
{
    // TryMatch returns the FIRST entry with a chord, so a second entry with the same one could never run.
    [Fact]
    public void NoTwoEntriesShareAChord()
    {
        var dup = RichEditorShortcuts.All
            .GroupBy(s => (s.Ctrl, s.Shift, s.Alt, s.Key))
            .Where(g => g.Count() > 1)
            .Select(g => string.Join(" / ", g.Select(s => $"{s.Id} {s.Display}")))
            .ToList();
        Assert.True(dup.Count == 0, "chords bound twice: " + string.Join("; ", dup));
    }

    // The hint a menu shows must be the chord the handler matches — "Ctrl+Shift+X" on an entry bound to
    // Ctrl+X would send the user to the wrong key, and nothing else would notice.
    [Fact]
    public void EveryDisplayString_IsItsOwnChord()
    {
        foreach (var s in RichEditorShortcuts.All)
        {
            var parts = s.Display.Split('+');
            Assert.Equal(s.Ctrl, parts.Contains("Ctrl"));
            Assert.Equal(s.Shift, parts.Contains("Shift"));
            Assert.Equal(s.Alt, parts.Contains("Alt"));
            Assert.Equal(KeyName(s.Key), parts[^1]);
        }
    }

    private static string KeyName(VirtualKey k) => k switch
    {
        >= VirtualKey.A and <= VirtualKey.Z => k.ToString(),
        >= VirtualKey.Number0 and <= VirtualKey.Number9 => ((int)(k - VirtualKey.Number0)).ToString(),
        (VirtualKey)0xBC => ",",
        (VirtualKey)0xBE => ".",
        _ => k.ToString(), // F5
    };

    // Display() gives the FIRST entry of an id — the primary chord. Redo has an alias (Ctrl+Shift+Z) that must
    // work but not be what the menu advertises.
    [Fact]
    public void AnAliasMatches_ButThePrimaryIsWhatIsShown()
    {
        Assert.True(RichEditorShortcuts.TryMatch(true, true, false, VirtualKey.Z, out var id));
        Assert.Equal(ShortcutId.Redo, id);
        Assert.Equal("Ctrl+Y", RichEditorShortcuts.Display(ShortcutId.Redo));
    }

    // Modifiers must match exactly: Ctrl+Shift+X is strikethrough, not cut; Ctrl+Alt+1 a heading, not single spacing.
    [Theory]
    [InlineData(true, false, false, VirtualKey.X, "Cut")]
    [InlineData(true, true, false, VirtualKey.X, "Strikethrough")]
    [InlineData(true, false, false, VirtualKey.Number1, "LineSpacingSingle")]
    [InlineData(true, false, true, VirtualKey.Number1, "Heading1")]
    public void ModifiersMatchExactly(bool ctrl, bool shift, bool alt, VirtualKey key, string expected)
    {
        Assert.True(RichEditorShortcuts.TryMatch(ctrl, shift, alt, key, out var id));
        Assert.Equal(expected, id.ToString());
    }
}
