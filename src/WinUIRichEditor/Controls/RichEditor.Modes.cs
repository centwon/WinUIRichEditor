using System;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Read-only behavior and the document image soft-limit. Capability is now expressed directly by the
// IsReadOnly DP (see RichEditor.cs) plus the Allow* feature flags (RichEditor.Features.cs) — there is no
// EditorMode preset. A "viewer" is simply IsReadOnly=true with no (or a minimal) toolbar. ReadOnly disables
// the caret blink and undo history (see OnReadOnlyChanged).
public partial class RichEditor
{
    /// <summary>Soft limit on the document's image count. When the count first exceeds this value,
    /// <see cref="RecommendedImageLimitExceeded"/> is raised once; editing is never blocked. Zero or
    /// negative disables the warning. Default 50.</summary>
    public int MaxRecommendedImages { get; set; } = 50;

    /// <summary>Raised (once per crossing) when the document's image count exceeds
    /// <see cref="MaxRecommendedImages"/>. Re-arms when the count drops back to the limit or below.</summary>
    public event EventHandler? RecommendedImageLimitExceeded;

    private bool _imageLimitNotified;

    /// <summary>Counts the document's images: top-level <see cref="ImageBlock"/>s plus
    /// <see cref="InlineImage"/>s in paragraphs and table cells.</summary>
    public int GetImageCount()
    {
        var doc = Document;
        if (doc == null) return 0;
        int n = 0;
        foreach (var b in doc.Blocks)
        {
            switch (b)
            {
                case ImageBlock: n++; break;
                case Paragraph p: n += CountInlineImages(p); break;
                case TableBlock t:
                    foreach (var (_, _, cell) in t.LogicalCells())
                        foreach (var cb in cell.Blocks)
                        {
                            if (cb is Paragraph cp) n += CountInlineImages(cp);
                            else if (cb is ImageBlock) n++;
                        }
                    break;
            }
        }
        return n;

        static int CountInlineImages(Paragraph p)
        {
            int k = 0;
            foreach (var i in p.Inlines) if (i is InlineImage) k++;
            return k;
        }
    }

    // Edge-triggered soft-limit check, run after each content edit (AfterEdit).
    internal void CheckImageLimit()
    {
        int limit = MaxRecommendedImages;
        if (limit <= 0) { _imageLimitNotified = false; return; }
        if (GetImageCount() > limit)
        {
            if (!_imageLimitNotified)
            {
                _imageLimitNotified = true;
                RecommendedImageLimitExceeded?.Invoke(this, EventArgs.Empty);
            }
        }
        else _imageLimitNotified = false;
    }

    // ReadOnly perf: a viewer needs no blinking caret and no undo history. Fires whether ReadOnly
    // arrives via the DP setter or the initial value.
    private void OnReadOnlyChanged(bool readOnly)
    {
        if (readOnly)
        {
            StopBlink();
            _undo.Clear();
        }
        else if (_hasFocus)
        {
            RestartBlink();
        }
        _automationPeer?.NotifyReadOnlyChanged(!readOnly, readOnly); // let assistive tech know
        InvalidateCanvas();
    }
}
