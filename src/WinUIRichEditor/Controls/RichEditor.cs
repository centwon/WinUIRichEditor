using System;
using System.Collections.Generic;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>A from-scratch rich text editor built on Win2D's <c>CanvasTextLayout</c> engine (a port of
/// AvaloniaRichEditor). Phase 2a: read-only rendering of paragraphs (runs, lists, headings, quotes,
/// alignment, inline images), dividers and block images. Caret/selection/input (Phase 3), IME
/// (Phase 4), tables and the rest (Phase 5) build on this rendering core.</summary>
public partial class RichEditor : ContentControl
{
    // ---- layout constants (mirror the Avalonia original) -------------------
    internal const double BodyFontSizePt = 10;
    internal const double NaturalLineFactor = 1.2;
    private const double ListMarkerWidth = 22;
    private const double DividerHeight = 18;
    internal const double A4ContentWidth = 698;

    // Font sizes in the model/API/serialization are points (pt). Win2D's CanvasTextLayout takes
    // device-independent pixels at a 96-DPI baseline (1pt = 4/3 px); convert only at this boundary.
    internal static double PtToPx(double pt) => pt * (4.0 / 3.0);

    internal static double HeadingFontSize(int level)
        => level switch { 1 => 20, 2 => 16, 3 => 14, 4 => 12, 5 => 11, 6 => 10, _ => BodyFontSizePt };

    private static bool RunSizeIsBodyDefault(Run r) => r.FontSize <= 0 || Math.Abs(r.FontSize - BodyFontSizePt) < 0.01;

    // Left x where a paragraph's text starts: base indent + manual indent + nesting + (list marker gap).
    private static double ParaLeft(Paragraph p)
        => 10 + p.Indent + p.ListLevel * 20 + (p.ListType != ListKind.None ? ListMarkerWidth : 0);

    /// <summary>The list-item marker text (forwarded to the shared helper, kept for API parity).</summary>
    internal static string ListMarkerText(ListKind kind, ListMarkerStyle style, int num)
        => ListMarkers.Text(kind, style, num);

    // ---- visual tree -------------------------------------------------------
    private readonly CanvasVirtualControl _canvas;
    private readonly ScrollViewer _scroll;
    private readonly ImageCache _images = new();

    // Per-paragraph CanvasTextLayout cache, keyed by paragraph identity. Built with the shared device
    // (CanvasTextLayout is device-independent for layout, and drawing it on any session is valid).
    // CanvasTextLayout is a heavy native DirectWrite object. Only the render/hit-test paths cache here,
    // so in continuous mode the cache naturally stays viewport-sized; measurement uses transient layouts
    // (see CreateLayout / ParagraphHeight) and the cheap per-paragraph height cache below, so total
    // memory no longer scales with document length. LayoutCacheCap is a safety net (clear-all when
    // exceeded) far larger than any single draw's working set, so it never disposes an in-use layout.
    private const int LayoutCacheCap = 2048;
    private readonly Dictionary<Paragraph, (long sig, double width, CanvasTextLayout layout)> _layoutCache = new();

    // Cheap per-paragraph height cache (a double, not a native layout). Lets MeasureContentHeight size
    // the scrollbar for the whole document without retaining a CanvasTextLayout per paragraph.
    private readonly Dictionary<Paragraph, (long sig, double width, double height)> _heightCache = new();

    private double _measuredHeight;

    public RichEditor()
    {
        _canvas = new CanvasVirtualControl
        {
            // 캔버스 Height/Width는 문서 크기에 맞춰 명시적으로 설정된다(RelayoutToViewport).
            // 기본 정렬(Stretch)인 채로 두면 뷰포트보다 작을 때 Stretch+고정크기 조합 때문에
            // 캔버스가 스크롤뷰어 안에서 세로로 가운데 배치되어 캐럿이 문서 상단이 아닌
            // 뷰포트 중앙에 나타난다. 항상 좌상단에 고정해 문서가 짧아도 위에서부터 시작하게 한다.
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            // 상단 여백. 문서 좌표계(y=0부터 렌더링)를 건드리면 측정·히트테스트·페이지분할 등
            // 여러 곳을 함께 고쳐야 하므로, 호스트 레벨(캔버스 위치)에서만 밀어내 시각적 여백만 준다.
            // 좌측 여백(ParaLeft의 DocContentLeft=10)과 동일한 값으로 맞춤 — 포인터 이벤트는
            // 캔버스 자신에 붙어 캔버스 로컬 좌표를 쓰므로 Margin이 캐럿/클릭 좌표에 영향 없음.
            Margin = new Thickness(0, DocContentLeft, 0, 0),
        };
        _canvas.RegionsInvalidated += OnRegionsInvalidated;

        _scroll = new ScrollViewer
        {
            Content = _canvas,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            ZoomMode = ZoomMode.Disabled,
        };
        Content = _scroll;
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;

        _images.OnReady += () => _canvas.Invalidate();
        _scroll.SizeChanged += (_, _) => OnViewportResized();
        _canvas.CreateResources += (_, _) => { ClearLayoutCache(); RelayoutToViewport(); };

        SetupInput();
    }

    // ---- accessibility ----------------------------------------------------
    private RichEditorAutomationPeer? _automationPeer;

    /// <inheritdoc/>
    protected override Microsoft.UI.Xaml.Automation.Peers.AutomationPeer OnCreateAutomationPeer()
        => _automationPeer ??= new RichEditorAutomationPeer(this);

    // ---- dependency properties --------------------------------------------
    /// <summary>The document model being rendered.</summary>
    public static readonly DependencyProperty DocumentProperty = DependencyProperty.Register(
        nameof(Document), typeof(FlowDocument), typeof(RichEditor), new PropertyMetadata(null, OnDocumentChanged));

    /// <summary>The document model being rendered.</summary>
    public FlowDocument? Document
    {
        get => (FlowDocument?)GetValue(DocumentProperty);
        set => SetValue(DocumentProperty, value);
    }

    private static void OnDocumentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed.ClearLayoutCache();
        // Prune (not Clear): dispose bitmaps the new document doesn't reference. Undo/redo swaps in a
        // snapshot whose images share RawBytes with the cached entries, so those stay warm — no
        // placeholder flash / full re-decode on every Ctrl+Z. A genuine document swap frees everything.
        ed._images.Prune(ed.CollectLiveImageKeys());
        ed.OnDocumentAssigned();
        ed.SyncPageSetupOnDocumentChanged(); // apply the loaded doc's page setup (or adopt current into it)
        ed.RelayoutToViewport();
        ed.MarkTextChanged();      // wholesale content swap flushes as TextChanged
        ed.RaiseDocumentChanged();
        ed.RaiseStatusChanged();
    }

    /// <summary>When true, edits/text input are blocked; selection and caret navigation still work,
    /// and the caret is hidden.</summary>
    public static readonly DependencyProperty IsReadOnlyProperty = DependencyProperty.Register(
        nameof(IsReadOnly), typeof(bool), typeof(RichEditor), new PropertyMetadata(false, OnIsReadOnlyChanged));

    private static void OnIsReadOnlyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RichEditor)d).OnReadOnlyChanged((bool)e.NewValue);

    /// <summary>When true, edits/text input are blocked.</summary>
    public bool IsReadOnly
    {
        get => (bool)GetValue(IsReadOnlyProperty);
        set => SetValue(IsReadOnlyProperty, value);
    }

    /// <summary>Font family for runs that don't specify one. Defaults to the OS UI font
    /// (e.g. "맑은 고딕" on Korean Windows); assign to override.</summary>
    public static readonly DependencyProperty DefaultFontFamilyProperty = DependencyProperty.Register(
        nameof(DefaultFontFamily), typeof(string), typeof(RichEditor),
        new PropertyMetadata(SystemDefaultFontFamily(), OnLayoutAffectingChanged));

    /// <summary>Font family for runs that don't specify one.</summary>
    public string DefaultFontFamily
    {
        get => (string)GetValue(DefaultFontFamilyProperty);
        set => SetValue(DefaultFontFamilyProperty, value);
    }

    /// <summary>Font size in points for runs WITHOUT an explicit size (<c>Run.FontSize</c> ≤ 0).
    /// Default: 10.
    /// <para>Scope note: runs the editor itself creates (typing, paste, load) carry an explicit size
    /// (the model default 10pt — see <c>Run.FontSize</c>; the .flow format stores it, matching the
    /// original AvaloniaRichEditor wire format), so changing this property does NOT restyle typed
    /// text. It applies to host-constructed documents that deliberately leave <c>FontSize</c> unset
    /// (≤ 0), to empty-paragraph line heights, and to the toolbar's displayed fallback size. To change
    /// the size of actual content, use <see cref="SetFontSize"/> on a selection instead.</para></summary>
    public static readonly DependencyProperty DefaultFontSizeProperty = DependencyProperty.Register(
        nameof(DefaultFontSize), typeof(double), typeof(RichEditor),
        new PropertyMetadata(BodyFontSizePt, OnLayoutAffectingChanged));

    /// <summary>Font size in points for runs that don't specify one (see the scope note on
    /// <see cref="DefaultFontSizeProperty"/> — this does not restyle editor-created runs).</summary>
    public double DefaultFontSize
    {
        get => (double)GetValue(DefaultFontSizeProperty);
        set => SetValue(DefaultFontSizeProperty, value);
    }

    private static void OnLayoutAffectingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed.ClearLayoutCache();
        ed.RelayoutToViewport();
        ed.CapturePageSetupToDocument(); // keep the doc's page setup in sync when a page DP changes
    }

    // ---- sizing / invalidation --------------------------------------------
    private double _layoutWidth = A4ContentWidth;

    // The content width = the ScrollViewer viewport (so text wraps to the visible area, no horizontal
    // scroll), floored so a tiny window still reads. Recomputes content height and resizes the canvas.
    private void RelayoutToViewport()
    {
        // Unconditional: drag-resize paths (image/table column/row) mutate the model per pointer move
        // with only ONE PushUndo at drag start, so MarkTextChanged alone would leave the map stale.
        InvalidateBlockLayout();
        double z = EffectiveZoom;
        // Paged: text wraps to the paper's content width (fixed; zoom magnifies it). Continuous: reflow to
        // the LOGICAL viewport width (viewport / zoom), so the zoom-scaled content fills the viewport.
        double width = IsPaged
            ? PaperContentWidth
            : (_scroll.ViewportWidth is var vw && vw > 1 ? vw / z : A4ContentWidth);
        _layoutWidth = Math.Max(50, width);

        _measuredHeight = MeasureContentHeight(_layoutWidth);
        _pageBreaks = null; // recompute lazily at the new width
        ClearRecordedGeometry(); // positions may have moved; the next draw re-records what's visible
        _paraIndexMap = null;    // belt-and-braces alongside MarkTextChanged (see ComparePositions)
        // The physical canvas is the logical content size × zoom (Win2D re-rasterizes under the scale, so
        // text stays crisp). Geometry/draw code works in logical units under the zoom transform.
        if (PagedChrome)
        {
            int pages = EnsurePageBreaks().Count;
            // Canvas at least as wide as the viewport so the page centers on a grey desk (with side margins).
            _canvas.Width = Math.Max(PaperWidth * z, _scroll.ViewportWidth);
            _canvas.Height = (pages * (PaperHeight + PageGap) + PageGap) * z;
        }
        else
        {
            _canvas.Width = _layoutWidth * z;
            _canvas.Height = _measuredHeight * z;
        }
        _canvas.Invalidate();
    }

    private void ClearLayoutCache()
    {
        foreach (var entry in _layoutCache.Values) entry.layout.Dispose();
        _layoutCache.Clear();
        _heightCache.Clear();
        _lineCache.Clear();
        _statsCache.Clear(); // sig-validated, but keyed by Paragraph — don't retain a swapped-out document
        _tableRowHeights.Clear();
        InvalidateBlockLayout();
        ClearMarkerLayouts(); // device-bound like the layouts above (device recreate path)
    }

    // Bound the heavy layout cache: dispose + drop the oldest entries so only ~viewport-worth of native
    // CanvasTextLayouts stay resident. Safe because no caller holds a returned layout across building
    // LayoutCacheCap other layouts (each use is synchronous within one measure/draw step).
    private void EvictLayouts()
    {
        foreach (var entry in _layoutCache.Values) entry.layout.Dispose();
        _layoutCache.Clear();
    }

    // ---- block layout map ---------------------------------------------------
    // Per-top-level-block layout: content top (after MarginTop), height, and the ordered-list index
    // consumed BEFORE the block. Built ONCE per (content change × width) and shared by every doc-space
    // walk — draw, hit-test, caret geometry, block-image rects, total height. Without it each of those
    // walked the whole document accumulating y (a paragraph-sig + cache lookup per block), so scrolling
    // and every caret move cost O(document) even after the caches. Invalidated by MarkTextChanged (all
    // edits funnel through PushUndo) and rebuilt lazily at whatever width is current (print's temporary
    // width swap just triggers a rebuild via the width key).
    private List<(Block block, double top, double height, int orderedStart)>? _blockLayout;
    private Dictionary<Block, int>? _blockLayoutIndex;
    private double _blockLayoutWidth = -1;

    internal void InvalidateBlockLayout() { _blockLayout = null; _blockLayoutIndex = null; }

    private List<(Block block, double top, double height, int orderedStart)> EnsureBlockLayout(double width)
    {
        if (_blockLayout != null && _blockLayoutWidth == width) return _blockLayout;
        var map = new List<(Block, double, double, int)>(Document?.Blocks.Count ?? 0);
        var index = new Dictionary<Block, int>();
        double y = 0;
        // Ordered-list numbering counts PER ListLevel (HTML nested-<ol> semantics): entering a deeper
        // sublist starts it at 1, returning to a shallower level continues that level's own count, and
        // re-entering a deeper level restarts (its counter is dropped on the way up). Bullet items keep
        // the surrounding numbering context alive (a bulleted sublist doesn't reset its parent's
        // numbers); any non-list block still breaks the list. The old single counter made a nested
        // ordered list continue its PARENT's numbers (1, 2, then the sublist showing 3).
        var ordCounters = new List<int>(); // ordCounters[level] = items consumed at that level
        if (Document != null)
        {
            foreach (var block in Document.Blocks)
            {
                y += block.MarginTop;
                double h = BlockHeight(block, width);
                int orderedStart = 0;
                if (block is Paragraph { IsListItem: true } lp)
                {
                    int lvl = Math.Clamp(lp.ListLevel, 0, 16);
                    if (ordCounters.Count > lvl + 1) ordCounters.RemoveRange(lvl + 1, ordCounters.Count - lvl - 1);
                    if (lp.ListType == ListKind.Ordered)
                    {
                        while (ordCounters.Count <= lvl) ordCounters.Add(0);
                        orderedStart = ordCounters[lvl];
                        ordCounters[lvl] += HardLineCount(lp);
                    }
                }
                else ordCounters.Clear();
                index[block] = map.Count;
                map.Add((block, y, h, orderedStart));
                y += h + block.MarginBottom;
            }
        }
        _blockLayout = map;
        _blockLayoutIndex = index;
        _blockLayoutWidth = width;
        return map;
    }

    // First map index whose block bottom reaches docY (map tops are ascending). map.Count when past the end.
    private static int BlockIndexAtY(List<(Block block, double top, double height, int orderedStart)> map, double docY)
    {
        int lo = 0, hi = map.Count - 1, ans = map.Count;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (map[mid].top + map[mid].height < docY) lo = mid + 1;
            else { ans = mid; hi = mid - 1; }
        }
        return ans;
    }

    // ---- content height ----------------------------------------------------
    // Total rendered height at the given content width (the block map's end plus breathing room).
    private double MeasureContentHeight(double width)
    {
        var map = EnsureBlockLayout(width);
        if (map.Count == 0) return 40;
        var last = map[^1];
        return last.top + last.height + last.block.MarginBottom + 40;
    }

    private double BlockHeight(Block block, double width)
    {
        switch (block)
        {
            case Paragraph p:
            {
                double px = ParaLeft(p);
                double pWidth = Math.Max(10, width - 20 - px - p.MarginRight);
                return ParagraphHeight(p, pWidth);
            }
            case ImageBlock img:
                return BlockImageDims(img).h;
            case DividerBlock:
                return DividerHeight;
            case TableBlock tb:
                return LayoutTable(tb, 10 + tb.Indent, 0).TotalHeight;
            default:
                return 0;
        }
    }

    // A natural single-line height for an empty paragraph (no glyphs to measure). Honors a custom
    // LineSpacing/LineHeight so an empty paragraph keeps the same height as its surrounding lines.
    private double EmptyLineHeight(Paragraph p)
    {
        double pt = p.HeadingLevel is >= 1 and <= 6 ? HeadingFontSize(p.HeadingLevel) : DefaultFontSize;
        double lh = ResolveLineHeight(p, pt, pt);
        return !double.IsNaN(lh) && lh > 0 ? lh : PtToPx(pt) * NaturalLineFactor;
    }

    // ---- text layout (the single source of truth) -------------------------
    // The paragraph's logical text, with each atomic object inline (image, table) collapsed to one
    // U+FFFC so character offsets line up with the CanvasTextLayout (rule #2).
    private const char ObjChar = '￼';

    private static int InlineLen(Inline inline) => inline is Run r ? (r.Text?.Length ?? 0) : 1;

    private static string BuildPlain(Paragraph p)
    {
        var sb = new System.Text.StringBuilder();
        foreach (var inline in p.Inlines)
        {
            if (inline is Run r && r.Text != null) sb.Append(r.Text);
            else if (inline is not Run) sb.Append(ObjChar);
        }
        return sb.ToString();
    }

    private static CanvasHorizontalAlignment MapAlign(TextAlignment a) => a switch
    {
        TextAlignment.Center => CanvasHorizontalAlignment.Center,
        TextAlignment.Right => CanvasHorizontalAlignment.Right,
        TextAlignment.Justify => CanvasHorizontalAlignment.Justified,
        _ => CanvasHorizontalAlignment.Left,
    };

    // Builds (or returns a cached) CanvasTextLayout for a paragraph at the given wrap width. Per-run
    // formatting is applied over character ranges; inline images/tables reserve one U+FFFC each via a
    // SpacerInlineObject (the bitmap/grid is painted afterward by the render walk).
    internal CanvasTextLayout BuildTextLayout(Paragraph p, double maxWidth)
    {
        long sig = ParagraphSig(p);
        if (_layoutCache.TryGetValue(p, out var cached) && cached.width == maxWidth && cached.sig == sig)
            return cached.layout;

        var layout = CreateLayout(p, maxWidth);
        if (_layoutCache.TryGetValue(p, out var prev)) { prev.layout.Dispose(); _layoutCache.Remove(p); }
        if (_layoutCache.Count >= LayoutCacheCap) EvictLayouts();
        _layoutCache[p] = (sig, maxWidth, layout);
        return layout;
    }

    // Constructs a fresh CanvasTextLayout (NOT cached). Measurement callers wrap it in `using` so the
    // heavy native object is released immediately instead of polluting the bounded layout cache, which
    // is what keeps total memory from scaling with document length.
    private CanvasTextLayout CreateLayout(Paragraph p, double maxWidth)
    {
        var device = CanvasDevice.GetSharedDevice();
        string defaultFamily = DefaultFontFamily;
        double defaultSize = DefaultFontSize;

        bool heading = p.HeadingLevel is >= 1 and <= 6;
        double headingSize = heading ? HeadingFontSize(p.HeadingLevel) : 0;

        string plain = BuildPlain(p);
        var fmt = new CanvasTextFormat
        {
            FontFamily = defaultFamily,
            FontSize = (float)PtToPx(defaultSize),
            HorizontalAlignment = MapAlign(p.TextAlignment),
            WordWrapping = CanvasWordWrapping.Wrap,
        };
        var layout = new CanvasTextLayout(device, plain, fmt, (float)Math.Max(1, maxWidth), 0f);

        int pos = 0;
        double maxRunPt = 0;
        foreach (var inline in p.Inlines)
        {
            int len = InlineLen(inline);
            if (len == 0) continue;
            if (inline is Run r && r.Text != null)
            {
                var family = string.IsNullOrEmpty(r.FontFamily) ? defaultFamily : r.FontFamily!;
                var weight = heading ? FontWeights.Bold : r.FontWeight;
                double size = r.FontSize <= 0 ? defaultSize : r.FontSize;
                if (heading && RunSizeIsBodyDefault(r)) size = headingSize;
                if (size > maxRunPt) maxRunPt = size;

                layout.SetFontFamily(pos, len, family);
                layout.SetFontSize(pos, len, (float)PtToPx(size));
                layout.SetFontWeight(pos, len, weight);
                layout.SetFontStyle(pos, len, r.FontStyle);
                if (r.Foreground is { } fg) layout.SetColor(pos, len, fg);
                // A hyperlink without its own color renders link-blue — same as HTML-pasted links, so
                // in-app SetHyperlink and pasted links look identical (an explicit Foreground wins).
                else if (!string.IsNullOrEmpty(r.NavigateUri)) layout.SetColor(pos, len, Windows.UI.Color.FromArgb(255, 0, 0, 255));
                bool underline = r.TextDecorations.HasFlag(TextDecorationFlags.Underline) || !string.IsNullOrEmpty(r.NavigateUri);
                if (underline) layout.SetUnderline(pos, len, true);
                if (r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)) layout.SetStrikethrough(pos, len, true);
            }
            else if (inline is InlineImage img)
            {
                double w = img.Width > 0 ? img.Width : 16;
                double h = img.Height > 0 ? img.Height : 16;
                layout.SetInlineObject(pos, 1, new SpacerInlineObject(new Windows.Foundation.Size(w, h)));
            }
            else if (inline is InlineTable itbl)
            {
                // Reserve a box exactly the size of the wrapped table so the grid, hit-test, and selection
                // highlight all share one rect (no padding gap that would make the highlight sag below).
                var box = LayoutTable(itbl.Table, 0, 0);
                layout.SetInlineObject(pos, 1, new SpacerInlineObject(new Windows.Foundation.Size(box.TableWidth, box.TotalHeight)));
            }
            pos += len;
        }

        // Custom line spacing: proportional LineSpacing wins (scales with the paragraph's font; ≤1.0
        // keeps the font's natural metrics so glyphs never clip), else an absolute LineHeight, else auto.
        double lh = ResolveLineHeight(p, maxRunPt, heading ? headingSize : defaultSize);
        if (!double.IsNaN(lh) && lh > 0)
        {
            layout.LineSpacingMode = CanvasLineSpacingMode.Uniform;
            layout.LineSpacing = (float)lh;
            layout.LineSpacingBaseline = (float)(lh * 0.8);
        }

        return layout;
    }

    // The resolved fixed line height in DIPs, or NaN for the font's natural spacing. LineSpacing
    // (proportional, scales with font size) takes priority over LineHeight (absolute DIPs).
    private double ResolveLineHeight(Paragraph p, double maxRunPt, double basePtFallback)
    {
        if (!double.IsNaN(p.LineSpacing))
        {
            double basePt = maxRunPt > 0 ? maxRunPt : basePtFallback;
            return p.LineSpacing <= 1.0 + 1e-6 ? double.NaN : p.LineSpacing * PtToPx(basePt) * NaturalLineFactor;
        }
        return !double.IsNaN(p.LineHeight) ? p.LineHeight : double.NaN;
    }

    // A cheap content+formatting fingerprint of a paragraph; when it (and the wrap width) are unchanged
    // the cached layout is reused. Brushes/colors fold in via value (Color) hashing. Run TEXT folds in
    // via the per-run cached hash (Run.TextHash) — this sig is recomputed for EVERY paragraph on each
    // relayout (i.e. per keystroke), and hashing the characters here made typing O(document chars).
    // With the cache the sig is O(runs); formatting fields are still read live, so nothing can go stale.
    private static long ParagraphSig(Paragraph p)
    {
        unchecked
        {
            long h = 1469598103934665603; // FNV-1a 64-bit offset basis
            void Mix(long v) { h = (h ^ v) * 1099511628211; }
            void MixStr(string? s) { if (s == null) { Mix(0); return; } foreach (char ch in s) Mix(ch); Mix(s.Length + 1); }
            // Full size-relevant fingerprint of a (possibly nested) table hosted by an inline table.
            // Runs only for paragraphs that actually host one, so the hot plain-paragraph path is untouched.
            void MixTable(TableBlock t)
            {
                Mix(t.Rows);
                Mix(t.Columns);
                foreach (var w in t.ColumnWidths) Mix(BitConverter.DoubleToInt64Bits(w));
                foreach (var rh in t.RowHeights) Mix(BitConverter.DoubleToInt64Bits(rh));
                foreach (var row in t.ColSpans) foreach (var v in row) Mix(v);
                foreach (var row in t.RowSpans) foreach (var v in row) Mix(v);
                foreach (var (_, _, cell) in t.LogicalCells())
                {
                    Mix((long)cell.VerticalAlignment);
                    foreach (var b in cell.Blocks)
                    {
                        switch (b)
                        {
                            case Paragraph cp: Mix(ParagraphSig(cp)); break;
                            case ImageBlock ib:
                                Mix(31);
                                Mix(BitConverter.DoubleToInt64Bits(ib.Width));
                                Mix(BitConverter.DoubleToInt64Bits(ib.Height));
                                Mix(ib.RawBytes?.GetHashCode() ?? ib.Image?.GetHashCode() ?? 0);
                                break;
                            case TableBlock nt: Mix(41); MixTable(nt); break;
                            case DividerBlock: Mix(37); break;
                        }
                    }
                }
            }

            Mix((long)p.TextAlignment);
            Mix(BitConverter.DoubleToInt64Bits(p.Indent));
            Mix((long)p.ListType);
            Mix((long)p.ListMarker);
            Mix(p.ListLevel);
            Mix(p.HeadingLevel);
            Mix(BitConverter.DoubleToInt64Bits(p.LineHeight));
            Mix(BitConverter.DoubleToInt64Bits(p.LineSpacing));
            foreach (var inl in p.Inlines)
            {
                if (inl is Run r)
                {
                    Mix(r.TextHash);
                    Mix(r.Text?.Length ?? -1);
                    MixStr(r.FontFamily);
                    MixStr(r.NavigateUri);
                    Mix(BitConverter.DoubleToInt64Bits(r.FontSize));
                    Mix(r.FontWeight.Weight);
                    Mix((long)r.FontStyle);
                    Mix(r.Foreground?.GetHashCode() ?? 0);
                    Mix(r.Background?.GetHashCode() ?? 0);
                    Mix((long)r.TextDecorations);
                }
                else if (inl is InlineImage img)
                {
                    Mix(7);
                    Mix(BitConverter.DoubleToInt64Bits(img.Width));
                    Mix(BitConverter.DoubleToInt64Bits(img.Height));
                    Mix(img.RawBytes?.GetHashCode() ?? img.Image?.GetHashCode() ?? 0);
                }
                else if (inl is InlineTable it)
                {
                    // The inline table reserves a run-sized box, so EVERYTHING that changes its rendered
                    // size must invalidate this paragraph's cached layout/height: row heights (row-boundary
                    // drags mutate only RowHeights, with one PushUndo at drag start), merge spans, cell
                    // vertical alignment, and non-paragraph cell blocks — not just column widths and cell
                    // text. Missing any of these left a stale spacer box (the table drew larger than the
                    // reserved slot and overlapped following lines until an unrelated edit).
                    Mix(13);
                    MixTable(it.Table);
                }
            }
            return h;
        }
    }
}
