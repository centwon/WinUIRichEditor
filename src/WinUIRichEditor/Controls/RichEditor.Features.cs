namespace WinUIRichEditor.Controls;

// Feature flags gating optional capabilities (images, tables, rich paste). These are the capability layer:
// set them directly (there is no EditorMode preset). They only restrict an editable editor — a read-only
// viewer (IsReadOnly=true) blocks editing regardless of these.
public partial class RichEditor
{
    /// <summary>When true, image insertion and image paste are allowed. Default true.</summary>
    public bool AllowImages { get; set; } = true;

    /// <summary>When true, table insertion and TSV-to-table paste are allowed. Default true.</summary>
    public bool AllowTables { get; set; } = true;

    /// <summary>When true, paste honors rich formats (internal snapshot, RTF, HTML, image, TSV).
    /// When false, paste falls back to plain text only. Default true.</summary>
    public bool AllowRichPaste { get; set; } = true;

    /// <summary>When true, the right-click menu includes the full formatting groups (글자 모양 / 문단 모양 /
    /// 목록 / 제목). When false (default), the control shows a slim menu — clipboard, quick B/I/U toggles,
    /// select-all/undo/redo — leaving rich formatting to the toolbar. Object menus (image / table / link) and
    /// inserts are unaffected. Hosts that use <see cref="RichEditor"/> standalone (no toolbar) can set this
    /// true to expose formatting from the context menu; the toolbar/View path leaves it off. Default false.</summary>
    public bool ShowFormattingMenu { get; set; } = false;
}
