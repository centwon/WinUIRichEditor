using WinUIRichEditor.Controls;

namespace WinUIRichEditor.Documents;

/// <summary>Per-document page setup: paper size, orientation, page boundaries, header/footer, and page
/// numbers. Persisted in the JSON/.flow format and applied to the editor on load (like a word processor's
/// page setup). View-only state such as zoom is deliberately NOT part of this — it isn't a document property.
/// <para>The page-size/orientation enums live in the Controls namespace but are plain value enums (no WinUI
/// runtime), so the headless model/formatters can use them freely.</para></summary>
public class PageSetup
{
    /// <summary>Paper size. <see cref="RichEditorPageSize.Continuous"/> (the default) reflows to width.</summary>
    public RichEditorPageSize PageSize { get; set; } = RichEditorPageSize.Continuous;
    /// <summary>Page orientation (ignored for Continuous).</summary>
    public RichEditorPageOrientation Orientation { get; set; } = RichEditorPageOrientation.Portrait;
    /// <summary>Whether page boundaries are drawn for a concrete paper size.</summary>
    public bool ShowPageBoundaries { get; set; } = true;
    /// <summary>Header text drawn in each page's top margin (null/empty = none).</summary>
    public string? Header { get; set; }
    /// <summary>Footer text drawn in each page's bottom margin (null/empty = none).</summary>
    public string? Footer { get; set; }
    /// <summary>Whether "page / total" is drawn in the bottom margin.</summary>
    public bool ShowPageNumbers { get; set; }

    public PageSetup Clone() => new()
    {
        PageSize = PageSize,
        Orientation = Orientation,
        ShowPageBoundaries = ShowPageBoundaries,
        Header = Header,
        Footer = Footer,
        ShowPageNumbers = ShowPageNumbers,
    };

    /// <summary>True when the setup carries no real information (Continuous paper, no header/footer/page
    /// numbers) — such a setup is omitted from serialization so plain documents keep their original format.</summary>
    public bool IsDefault =>
        PageSize == RichEditorPageSize.Continuous
        && string.IsNullOrEmpty(Header)
        && string.IsNullOrEmpty(Footer)
        && !ShowPageNumbers;
}
