using System.Collections.Generic;
using Windows.System;

namespace WinUIRichEditor.Controls;

/// <summary>Identifies a command that has a keyboard shortcut. Used as the key that ties the shortcut
/// table, the editor's key handler, the context-menu hints, and the toolbar tooltips together.</summary>
internal enum ShortcutId
{
    Cut, Copy, Paste, PastePlain, SelectAll, Undo, Redo,
    Bold, Italic, Underline, Strikethrough, FontLarger, FontSmaller,
    IndentIncrease, IndentDecrease,
    AlignLeft, AlignCenter, AlignRight, AlignJustify,
    Heading1, Heading2, Heading3, Heading4, Heading5, Heading6, BodyText,
    BulletList, NumberedList, LineSpacingSingle, LineSpacingOneHalf, LineSpacingDouble,
    Find, FindReplace, InsertLink,
}

internal readonly record struct ShortcutSpec(ShortcutId Id, bool Ctrl, bool Shift, bool Alt, VirtualKey Key, string Display);

/// <summary>The single source of truth for command keyboard shortcuts (Word-standard scheme). The editor's
/// <c>OnKeyDown</c> matches events against <see cref="All"/>; the context menu and toolbar read
/// <see cref="Display"/> for their hint text — so behavior and the shown shortcut never drift.</summary>
internal static class RichEditorShortcuts
{
    private const VirtualKey OemComma = (VirtualKey)0xBC;  // ','  (Shift → '<')
    private const VirtualKey OemPeriod = (VirtualKey)0xBE; // '.'  (Shift → '>')

    public static readonly ShortcutSpec[] All =
    {
        new(ShortcutId.Cut,           true, false, false, VirtualKey.X, "Ctrl+X"),
        new(ShortcutId.Copy,          true, false, false, VirtualKey.C, "Ctrl+C"),
        new(ShortcutId.Paste,         true, false, false, VirtualKey.V, "Ctrl+V"),
        new(ShortcutId.PastePlain,    true, true,  false, VirtualKey.V, "Ctrl+Shift+V"),
        new(ShortcutId.SelectAll,     true, false, false, VirtualKey.A, "Ctrl+A"),
        new(ShortcutId.Undo,          true, false, false, VirtualKey.Z, "Ctrl+Z"),
        new(ShortcutId.Redo,          true, false, false, VirtualKey.Y, "Ctrl+Y"),
        new(ShortcutId.Redo,          true, true,  false, VirtualKey.Z, "Ctrl+Shift+Z"), // alias (display keeps Ctrl+Y)
        new(ShortcutId.Bold,          true, false, false, VirtualKey.B, "Ctrl+B"),
        new(ShortcutId.Italic,        true, false, false, VirtualKey.I, "Ctrl+I"),
        new(ShortcutId.Underline,     true, false, false, VirtualKey.U, "Ctrl+U"),
        new(ShortcutId.Strikethrough, true, true,  false, VirtualKey.X, "Ctrl+Shift+X"),
        new(ShortcutId.FontLarger,    true, true,  false, OemPeriod,    "Ctrl+Shift+."),
        new(ShortcutId.FontSmaller,   true, true,  false, OemComma,     "Ctrl+Shift+,"),
        new(ShortcutId.IndentIncrease,true, false, false, VirtualKey.M, "Ctrl+M"),
        new(ShortcutId.IndentDecrease,true, true,  false, VirtualKey.M, "Ctrl+Shift+M"),
        new(ShortcutId.AlignLeft,     true, false, false, VirtualKey.L, "Ctrl+L"),
        new(ShortcutId.AlignCenter,   true, false, false, VirtualKey.E, "Ctrl+E"),
        new(ShortcutId.AlignRight,    true, false, false, VirtualKey.R, "Ctrl+R"),
        new(ShortcutId.AlignJustify,  true, false, false, VirtualKey.J, "Ctrl+J"),
        new(ShortcutId.Heading1,      true, false, true,  VirtualKey.Number1, "Ctrl+Alt+1"),
        new(ShortcutId.Heading2,      true, false, true,  VirtualKey.Number2, "Ctrl+Alt+2"),
        new(ShortcutId.Heading3,      true, false, true,  VirtualKey.Number3, "Ctrl+Alt+3"),
        new(ShortcutId.Heading4,      true, false, true,  VirtualKey.Number4, "Ctrl+Alt+4"),
        new(ShortcutId.Heading5,      true, false, true,  VirtualKey.Number5, "Ctrl+Alt+5"),
        new(ShortcutId.Heading6,      true, false, true,  VirtualKey.Number6, "Ctrl+Alt+6"),
        new(ShortcutId.BodyText,      true, true,  false, VirtualKey.N, "Ctrl+Shift+N"),
        new(ShortcutId.BulletList,    true, true,  false, VirtualKey.L, "Ctrl+Shift+L"),
        new(ShortcutId.NumberedList,  true, true,  false, VirtualKey.Number7, "Ctrl+Shift+7"), // Docs convention; Word has no standard binding
        new(ShortcutId.LineSpacingSingle,  true, false, false, VirtualKey.Number1, "Ctrl+1"),
        new(ShortcutId.LineSpacingOneHalf, true, false, false, VirtualKey.Number5, "Ctrl+5"),
        new(ShortcutId.LineSpacingDouble,  true, false, false, VirtualKey.Number2, "Ctrl+2"),
        new(ShortcutId.Find,               true, false, false, VirtualKey.F, "Ctrl+F"),
        new(ShortcutId.FindReplace,        true, false, false, VirtualKey.H, "Ctrl+H"),
        new(ShortcutId.InsertLink,         true, false, false, VirtualKey.K, "Ctrl+K"),
    };

    private static readonly Dictionary<ShortcutId, string> DisplayMap = BuildDisplayMap();

    private static Dictionary<ShortcutId, string> BuildDisplayMap()
    {
        var d = new Dictionary<ShortcutId, string>();
        foreach (var s in All) d.TryAdd(s.Id, s.Display); // keep the first (primary) display per id
        return d;
    }

    /// <summary>The shortcut hint text for a command (e.g. "Ctrl+B"), or "" if none.</summary>
    public static string Display(ShortcutId id) => DisplayMap.TryGetValue(id, out var s) ? s : "";

    /// <summary>Matches a key event to a command. Modifiers must match exactly.</summary>
    public static bool TryMatch(bool ctrl, bool shift, bool alt, VirtualKey key, out ShortcutId id)
    {
        foreach (var s in All)
            if (s.Ctrl == ctrl && s.Shift == shift && s.Alt == alt && s.Key == key) { id = s.Id; return true; }
        id = default;
        return false;
    }
}
