using System;
using System.Collections.Generic;
using Windows.Foundation;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// Pagination — increment 1: page-size/orientation properties, paper dimensions, and a paged wrap width
// (text reflows to the paper's content width). The visual page stack + line-aware breaks (the doc→view
// coordinate transform), print, and PDF build on this in later increments. Default PageSize is
// Continuous, so the existing continuous rendering is unchanged unless a host opts into a paper size.
public partial class RichEditor
{
    internal const double A4PageWidth = 794;
    internal const double A4PageHeight = 1123;
    internal const double PagePadX = 48;   // left/right page margin
    internal const double PagePadY = 40;   // top/bottom page margin
    internal const double PageGap = 14;    // grey desk gap between stacked pages (page view)

    // ---- visual zoom -------------------------------------------------------
    // Engine-level zoom: the document lays out in LOGICAL coordinates exactly as at 1.0, then the render
    // session is scaled and the canvas sized by this factor, so glyphs re-rasterize crisply at any zoom
    // (no bitmap scaling). All view↔doc coordinate conversion folds the factor in at one place
    // (ViewToDoc / DocToView), so input, caret, and hit-testing stay correct under zoom.
    internal const double MinZoom = 0.25, MaxZoom = 5.0;

    /// <summary>Visual zoom for the document (1.0 = 100%). Text stays crisp at any factor; clamped to
    /// [0.25, 5.0]. The host chrome (toolbar/status bar) is unaffected.</summary>
    public static readonly DependencyProperty ZoomProperty = DependencyProperty.Register(
        nameof(Zoom), typeof(double), typeof(RichEditor), new PropertyMetadata(1.0, OnZoomChanged));

    /// <summary>Visual zoom for the document (1.0 = 100%); clamped to [0.25, 5.0].</summary>
    public double Zoom
    {
        get => (double)GetValue(ZoomProperty);
        set => SetValue(ZoomProperty, value);
    }

    private static void OnZoomChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed.ClearLayoutCache();      // wrap width changes in continuous mode → rebuild layouts
        ed.RelayoutToViewport();
        ed.RaiseStatusChanged();
    }

    // The clamped, effective zoom used by all engine math.
    internal double EffectiveZoom => System.Math.Clamp(Zoom, MinZoom, MaxZoom);

    // The canvas size in LOGICAL (pre-zoom) units. The physical canvas is this × EffectiveZoom; draw and
    // desk-geometry code works in logical units under the zoom transform.
    private double CanvasLogicalWidth => _canvas.Width / EffectiveZoom;

    // ---- fit-to-width ------------------------------------------------------
    // When true, the zoom auto-recomputes on every viewport resize so the page/content width keeps filling
    // the view. Cleared when a host sets an explicit zoom via SetZoom.
    private bool _fitWidth;

    /// <summary>True while fit-to-width is active (zoom tracks the viewport width).</summary>
    public bool IsFitWidth => _fitWidth;

    /// <summary>Sets an explicit zoom factor and leaves fit-to-width mode. Clamped to [0.25, 5.0].</summary>
    public void SetZoom(double factor)
    {
        _fitWidth = false;
        Zoom = System.Math.Clamp(factor, MinZoom, MaxZoom);
    }

    /// <summary>Zooms so the page (paged) or content (continuous) width fills the viewport, and keeps it
    /// fitted as the view resizes. The chrome's "Fit width" item and Ctrl+0 call this.</summary>
    public void FitToWidth()
    {
        _fitWidth = true;
        double z = ComputeFitWidthZoom();
        if (z > 0)
        {
            double clamped = System.Math.Clamp(z, MinZoom, MaxZoom);
            if (System.Math.Abs(clamped - EffectiveZoom) > 0.001) { Zoom = clamped; return; } // relayouts via OnZoomChanged
        }
        RelayoutToViewport(); // viewport not laid out yet, or zoom unchanged — still refresh
        RaiseStatusChanged();
    }

    // The zoom that makes the page/content width fill the viewport (0 if the viewport isn't laid out yet).
    private double ComputeFitWidthZoom()
    {
        double vw = _scroll.ViewportWidth;
        if (vw <= 1) return 0;
        if (!IsPaged) return 1.0; // continuous already reflows to the viewport width
        const double inset = 24;   // small desk margin so the page isn't edge-to-edge
        double target = PagedChrome ? PaperWidth : PaperContentWidth;
        return (vw - inset) / target;
    }

    // Viewport resized: in fit mode, re-fit the zoom; otherwise just relayout to the new width.
    private void OnViewportResized()
    {
        if (_fitWidth)
        {
            double z = ComputeFitWidthZoom();
            if (z > 0)
            {
                double clamped = System.Math.Clamp(z, MinZoom, MaxZoom);
                if (System.Math.Abs(clamped - EffectiveZoom) > 0.001) { Zoom = clamped; return; } // relayouts via OnZoomChanged
            }
        }
        RelayoutToViewport();
    }

    // ---- page setup <-> document model ------------------------------------
    // Page setup (paper/orientation/header/footer/page numbers) is a DOCUMENT property persisted in
    // JSON/.flow, but the live source of truth is the control's DPs. These keep the two in sync, guarded
    // against the apply->DP-change->capture feedback loop.
    private bool _syncingPageSetup;

    // On Document change: a document that specifies a PageSetup drives the control's page DPs (model ->
    // control); a document that doesn't adopts the control's current settings (control -> model) so a later
    // save persists what's shown. Called from OnDocumentChanged before the status flush updates the chrome.
    private void SyncPageSetupOnDocumentChanged()
    {
        var doc = Document;
        if (doc == null) return;
        if (doc.PageSetup is { } ps)
        {
            _syncingPageSetup = true;
            try
            {
                PageSize = ps.PageSize;
                PageOrientation = ps.Orientation;
                ShowPageBoundaries = ps.ShowPageBoundaries;
                PageHeader = ps.Header;
                PageFooter = ps.Footer;
                ShowPageNumbers = ps.ShowPageNumbers;
            }
            finally { _syncingPageSetup = false; }
        }
        else CapturePageSetupToDocument();
    }

    // Writes the control's current page DPs into Document.PageSetup (control -> model) so serialization
    // captures them. Stores null when everything is default, keeping plain documents' format unchanged.
    private void CapturePageSetupToDocument()
    {
        if (_syncingPageSetup) return;
        var doc = Document;
        if (doc == null) return;
        var ps = new Documents.PageSetup
        {
            PageSize = PageSize,
            Orientation = PageOrientation,
            ShowPageBoundaries = ShowPageBoundaries,
            Header = PageHeader,
            Footer = PageFooter,
            ShowPageNumbers = ShowPageNumbers,
        };
        doc.PageSetup = ps.IsDefault ? null : ps;
    }

    /// <summary>Paper size for the document. <see cref="RichEditorPageSize.Continuous"/> (the default
    /// here) reflows to the control width; a concrete size wraps text to that paper's content width.</summary>
    public static readonly DependencyProperty PageSizeProperty = DependencyProperty.Register(
        nameof(PageSize), typeof(RichEditorPageSize), typeof(RichEditor),
        new PropertyMetadata(RichEditorPageSize.Continuous, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the paper size. Default <see cref="RichEditorPageSize.Continuous"/>.</summary>
    public RichEditorPageSize PageSize
    {
        get => (RichEditorPageSize)GetValue(PageSizeProperty);
        set => SetValue(PageSizeProperty, value);
    }

    /// <summary>For a concrete <see cref="PageSize"/>, whether to draw page boundaries. Ignored for
    /// Continuous. Default true.</summary>
    public static readonly DependencyProperty ShowPageBoundariesProperty = DependencyProperty.Register(
        nameof(ShowPageBoundaries), typeof(bool), typeof(RichEditor),
        new PropertyMetadata(true, OnLayoutAffectingChanged));

    /// <summary>Gets or sets whether page boundaries are drawn for a concrete <see cref="PageSize"/>.</summary>
    public bool ShowPageBoundaries
    {
        get => (bool)GetValue(ShowPageBoundariesProperty);
        set => SetValue(ShowPageBoundariesProperty, value);
    }

    /// <summary>Page orientation. Landscape swaps the paper's width and height. No effect for Continuous.</summary>
    public static readonly DependencyProperty PageOrientationProperty = DependencyProperty.Register(
        nameof(PageOrientation), typeof(RichEditorPageOrientation), typeof(RichEditor),
        new PropertyMetadata(RichEditorPageOrientation.Portrait, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the page orientation. Default Portrait.</summary>
    public RichEditorPageOrientation PageOrientation
    {
        get => (RichEditorPageOrientation)GetValue(PageOrientationProperty);
        set => SetValue(PageOrientationProperty, value);
    }

    /// <summary>Header text drawn in each page's top margin (page view and print). Null/empty = none.</summary>
    public static readonly DependencyProperty PageHeaderProperty = DependencyProperty.Register(
        nameof(PageHeader), typeof(string), typeof(RichEditor), new PropertyMetadata(null, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the page header text (top margin).</summary>
    public string? PageHeader
    {
        get => (string?)GetValue(PageHeaderProperty);
        set => SetValue(PageHeaderProperty, value);
    }

    /// <summary>Footer text drawn in each page's bottom margin (page view and print). Null/empty = none.</summary>
    public static readonly DependencyProperty PageFooterProperty = DependencyProperty.Register(
        nameof(PageFooter), typeof(string), typeof(RichEditor), new PropertyMetadata(null, OnLayoutAffectingChanged));

    /// <summary>Gets or sets the page footer text (bottom margin).</summary>
    public string? PageFooter
    {
        get => (string?)GetValue(PageFooterProperty);
        set => SetValue(PageFooterProperty, value);
    }

    /// <summary>Draws "page / total" in each page's bottom-right margin. Default false.</summary>
    public static readonly DependencyProperty ShowPageNumbersProperty = DependencyProperty.Register(
        nameof(ShowPageNumbers), typeof(bool), typeof(RichEditor), new PropertyMetadata(false, OnLayoutAffectingChanged));

    /// <summary>Gets or sets whether page numbers are drawn (bottom margin).</summary>
    public bool ShowPageNumbers
    {
        get => (bool)GetValue(ShowPageNumbersProperty);
        set => SetValue(ShowPageNumbersProperty, value);
    }

    /// <summary>True when a concrete (non-Continuous) paper size is set.</summary>
    internal bool IsPaged => PageSize != RichEditorPageSize.Continuous;

    // The paper's pixel size at 96 DPI, accounting for orientation. Continuous reports its A4 fallback.
    private (double w, double h) PaperDims
    {
        get
        {
            var (w, h) = PageSize switch
            {
                RichEditorPageSize.A3 => (1123.0, 1587.0),
                RichEditorPageSize.A5 => (559.0, 794.0),
                RichEditorPageSize.B4 => (971.0, 1376.0),
                RichEditorPageSize.B5 => (688.0, 971.0),
                RichEditorPageSize.Letter => (816.0, 1056.0),
                RichEditorPageSize.Legal => (816.0, 1344.0),
                RichEditorPageSize.Tabloid => (1056.0, 1632.0),
                _ => (A4PageWidth, A4PageHeight), // A4 + Continuous fallback
            };
            return PageOrientation == RichEditorPageOrientation.Landscape ? (h, w) : (w, h);
        }
    }

    internal double PaperWidth => PaperDims.w;
    internal double PaperHeight => PaperDims.h;
    internal double PaperContentWidth => PaperWidth - 2 * PagePadX;
    internal double PaperContentHeight => PaperHeight - 2 * PagePadY;

    /// <summary>The current paper's pixel size at 96 DPI (accounts for <see cref="PageOrientation"/>).
    /// Continuous reports its A4 fallback. Useful for host fit-to-width and print math.</summary>
    public Size GetPaperPixelSize() => new(PaperWidth, PaperHeight);

    // ---- page-view geometry (increment 2) ---------------------------------
    // Paged AND boundaries enabled = stack white pages on a grey desk (the doc->view transform path).
    private bool PagedChrome => IsPaged && ShowPageBoundaries;

    internal const double DocContentLeft = 10; // listIndent: the doc-space left origin of content

    // Page-start positions in continuous document space, recomputed on relayout. Line-aware: a page
    // breaks between paragraph lines / table rows so glyphs and rows are never sliced.
    private List<double>? _pageBreaks;

    private List<double> EnsurePageBreaks()
        => _pageBreaks ??= ComputePageBreaks(PaperContentWidth, PaperContentHeight);

    private int PageOfDocY(double docY)
    {
        var br = EnsurePageBreaks();
        int i = br.Count - 1;
        while (i > 0 && docY < br[i]) i--;
        return i;
    }

    // Per-paragraph (height, visual-line bottoms) for pagination. Cached like the cheap height cache
    // (a double[] per paragraph, not a native layout) and measured with a TRANSIENT layout, so
    // recomputing page breaks — which happens on every relayout — neither inflates the heavy layout
    // cache with the whole document nor rebuilds DirectWrite layouts for unchanged paragraphs.
    private readonly System.Collections.Generic.Dictionary<Paragraph, (long sig, double width, double height, double[] bottoms)> _lineCache = new();

    private (double height, double[] bottoms) ParagraphLines(Paragraph p, double width)
    {
        long sig = ParagraphSig(p);
        if (_lineCache.TryGetValue(p, out var hc) && hc.sig == sig && hc.width == width)
            return (hc.height, hc.bottoms);
        double h;
        double[] bottoms;
        using (var layout = CreateLayout(p, width))
        {
            h = System.Math.Max(EmptyLineHeight(p), layout.LayoutBounds.Height);
            bottoms = ParagraphLineBottoms(layout, h).ToArray();
        }
        if (_lineCache.Count > 100000) _lineCache.Clear(); // guard against pathological growth
        _lineCache[p] = (sig, width, h, bottoms);
        return (h, bottoms);
    }

    // Walks the document (mirroring the render advance) and returns the doc-space Y where each page
    // begins. Atoms are paragraph lines and table rows; an atom that would overflow the current page
    // starts the next one. Mirrors the Avalonia original's ComputePageBreaks.
    internal List<double> ComputePageBreaks(double contentWidth, double pageContentHeight)
    {
        var breaks = new List<double> { 0 };
        if (Document == null || pageContentHeight <= 0) return breaks;

        const double listIndent = 10;
        double y = 0, pageStart = 0;
        const double eps = 0.01;

        void PlaceAtom(double height)
        {
            if (y + height > pageStart + pageContentHeight + eps && y > pageStart)
            { breaks.Add(y); pageStart = y; }
            y += height;
        }

        foreach (var block in Document.Blocks)
        {
            y += block.MarginTop;
            if (block is Paragraph p)
            {
                double px = ParaLeft(p);
                double pWidth = Math.Max(10, contentWidth - 20 - px - p.MarginRight);
                var (paraH, bottoms) = ParagraphLines(p, pWidth);
                double paraTop = y;
                double atomTop = 0;
                foreach (double atomBottom in bottoms)
                {
                    if (paraTop + atomBottom > pageStart + pageContentHeight + eps && paraTop + atomTop > pageStart)
                    { breaks.Add(paraTop + atomTop); pageStart = paraTop + atomTop; }
                    atomTop = atomBottom;
                }
                y = paraTop + paraH;
            }
            else if (block is TableBlock tb)
            {
                var tl = LayoutTable(tb, listIndent + tb.Indent, y);
                for (int r = 0; r < tb.Rows && r + 1 < tl.RowY.Length; r++)
                    PlaceAtom(tl.RowY[r + 1] - tl.RowY[r]);
            }
            else
            {
                PlaceAtom(BlockHeight(block, contentWidth));
            }
            y += block.MarginBottom;
        }
        return breaks;
    }

    // The doc-relative bottom Y of each visual line in a paragraph layout (the page-break atom edges).
    private List<double> ParagraphLineBottoms(CanvasTextLayout layout, double paraH)
    {
        var result = new List<double>();
        CanvasLineMetrics[] lm;
        try { lm = layout.LineMetrics; } catch { return new List<double> { paraH }; }
        if (lm.Length <= 1) return new List<double> { paraH };
        double acc = 0;
        for (int i = 0; i < lm.Length; i++)
        {
            acc += lm[i].Height;
            result.Add(i == lm.Length - 1 ? paraH : acc);
        }
        return result;
    }

    private double PageDeskX => System.Math.Max(0, (CanvasLogicalWidth - PaperWidth) / 2); // centered on the desk

    // Converts a canvas/view point (physical, zoom-scaled) to document space. Zoom is divided out first so
    // the rest works in logical units; the paged page-stack transform is then applied (identity if not paged).
    private Point ViewToDoc(Point v)
    {
        double z = EffectiveZoom;
        v = new Point(v.X / z, v.Y / z);
        if (!PagedChrome) return v;
        var br = EnsurePageBreaks();
        double stride = PaperHeight + PageGap;
        int i = System.Math.Clamp((int)((v.Y - PageGap) / System.Math.Max(1, stride)), 0, br.Count - 1);
        double viewContentLeft = PageDeskX + PagePadX;
        double viewContentTop = PageGap + i * stride + PagePadY;
        return new Point(v.X - (viewContentLeft - DocContentLeft), br[i] + (v.Y - viewContentTop));
    }

    // Converts a document point to canvas/view space (physical, zoom-scaled). The paged transform maps to
    // logical view units; the zoom factor then scales to physical canvas coordinates.
    private Point DocToView(Point d)
    {
        double z = EffectiveZoom;
        Point lv;
        if (!PagedChrome) lv = d;
        else
        {
            var br = EnsurePageBreaks();
            double stride = PaperHeight + PageGap;
            int i = PageOfDocY(d.Y);
            double viewContentLeft = PageDeskX + PagePadX;
            double viewContentTop = PageGap + i * stride + PagePadY;
            lv = new Point(d.X + (viewContentLeft - DocContentLeft), viewContentTop + (d.Y - br[i]));
        }
        return new Point(lv.X * z, lv.Y * z);
    }
}
