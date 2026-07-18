using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUIRichEditor.Controls;

// Built-in icon factory: maps a RichEditorIcon to a Segoe Fluent Icons glyph rendered as a FontIcon.
// Monochrome, themes via Foreground, crisp at any zoom, no asset files — the low-cost default. The symbol
// font lacks good glyphs for some rich-text actions (format painter, indent, numbered list, table
// structure, divider); those return 0 so the chrome falls back to its text glyph. A host can override
// any slot via RichEditorIcons.Provider, which takes precedence over this factory.
internal static class ToolbarIcons
{
    // Segoe Fluent Icons ships on Windows 10 1809+/11; on older builds it resolves to Segoe MDL2 Assets,
    // which shares these codepoints, so the same glyphs render either way.
    private static readonly FontFamily SymbolFont = new("Segoe Fluent Icons");

    // Private-use-area codepoints (0 = no good symbol-font glyph; caller uses its text fallback). Kept as
    // ints so the source carries no literal PUA characters.
    private static int Code(RichEditorIcon icon) => icon switch
    {
        RichEditorIcon.Bold => 0xE8DD,
        RichEditorIcon.Italic => 0xE8DB,
        RichEditorIcon.Underline => 0xE8DC,
        RichEditorIcon.Strikethrough => 0xEDE0,
        RichEditorIcon.TextColor => 0xE8D3,        // FontColor
        RichEditorIcon.Highlight => 0xE7E6,        // Highlight
        RichEditorIcon.BulletList => 0xE8FD,       // BulletedList
        RichEditorIcon.Undo => 0xE7A7,
        RichEditorIcon.Redo => 0xE7A6,
        RichEditorIcon.Cut => 0xE8C6,
        RichEditorIcon.Copy => 0xE8C8,
        RichEditorIcon.Paste => 0xE77F,
        RichEditorIcon.Delete => 0xE74D,
        RichEditorIcon.SelectAll => 0xE8B3,
        RichEditorIcon.AlignLeft => 0xE8E4,
        RichEditorIcon.AlignCenter => 0xE8E3,
        RichEditorIcon.AlignRight => 0xE8E2,
        RichEditorIcon.InsertImage => 0xE8B9,      // Pictures
        RichEditorIcon.ReplaceImage => 0xE8B9,     // Pictures
        RichEditorIcon.InsertDivider => 0xE738,    // Remove (a clean horizontal bar = the rule glyph)
        RichEditorIcon.OpenLink => 0xE8A7,         // OpenInNewWindow
        RichEditorIcon.EditLink => 0xE70F,         // Edit
        RichEditorIcon.RemoveLink => 0xE74D,       // Delete (unlink)
        RichEditorIcon.CopyLink => 0xE71B,         // Link
        RichEditorIcon.InsertLink => 0xE71B,       // Link
        RichEditorIcon.SaveImageAs => 0xE74E,      // Save
        RichEditorIcon.Export => 0xE74E,           // Save
        RichEditorIcon.Import => 0xE8E5,           // OpenFile
        RichEditorIcon.Print => 0xE749,
        RichEditorIcon.CharacterFormat => 0xE8D2,  // Font
        RichEditorIcon.FontSizeIncrease => 0xE8E8, // FontIncrease
        RichEditorIcon.FontSizeDecrease => 0xE8E7, // FontDecrease
        RichEditorIcon.Find => 0xE721,             // Search
        _ => 0, // FormatPainter, NumberedList, IndentIncrease/Decrease, InsertTable, and the
                // table-structure ops have no reliable symbol-font glyph -> text fallback.
    };

    // Letter-shaped glyphs (B/I/U/S, the font "A") are drawn edge-to-edge in their em square, unlike
    // object icons (save, print, picture…) which carry built-in padding — at equal FontSize the letters
    // read visibly LARGER. Down-size them so the whole strip has one apparent icon size.
    private static double SizeFor(RichEditorIcon icon, double baseSize) => icon switch
    {
        RichEditorIcon.Bold or RichEditorIcon.Italic or RichEditorIcon.Underline
            or RichEditorIcon.Strikethrough or RichEditorIcon.CharacterFormat
            or RichEditorIcon.FontSizeIncrease or RichEditorIcon.FontSizeDecrease => baseSize - 3,
        _ => baseSize,
    };

    // A FontIcon for the slot, or null when no good glyph exists (caller falls back to text). Default
    // 15px (object icons); letter glyphs get down-sized in SizeFor so they don't read larger.
    public static UIElement? Create(RichEditorIcon icon, double size = 15)
    {
        int code = Code(icon);
        if (code == 0) return null;
        return new FontIcon
        {
            FontFamily = SymbolFont,
            Glyph = ((char)code).ToString(),
            FontSize = SizeFor(icon, size),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }
}
