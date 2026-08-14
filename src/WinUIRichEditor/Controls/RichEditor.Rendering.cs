using System;
using System.Collections.Generic;
using Windows.Foundation;
using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

// The read-only render pass (Phase 2a): a CanvasVirtualControl region callback that walks the document
// in continuous coordinates and draws each block. Caret/selection/tables grow on this in later phases.
public partial class RichEditor
{
    // The fixed fallback; actual drawing uses EffectiveTextColor (TextForeground DP, themeable).
    private static readonly Color DefaultTextColor = Color.FromArgb(255, 0, 0, 0);
    private static readonly Color GrayBorderColor = Color.FromArgb(255, 128, 128, 128);
    private static readonly Color QuoteBarColor = Colors.Silver;
    private static readonly Color PlaceholderFill = Color.FromArgb(255, 244, 244, 244);
    private static readonly Color PlaceholderBorder = Color.FromArgb(255, 200, 200, 200);

    // CanvasVirtualControl invalidates in regions; each region draws only the blocks intersecting it
    // (plain paragraphs outside the clip advance by their cached height — see DrawContentWalk).
    private void OnRegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
    {
        foreach (var region in args.InvalidatedRegions)
        {
            try
            {
                using var ds = sender.CreateDrawingSession(region);
                // Engine zoom: scale the whole render. Content draws at logical coords, so glyphs re-rasterize
                // crisply at this scale. The paged page transform composes with this base (see DrawPagedDocument).
                double z = EffectiveZoom;
                if (z != 1.0) ds.Transform = System.Numerics.Matrix3x2.CreateScale((float)z);
                DrawDocument(ds, region);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x80070057)) // E_INVALIDARG
            {
                // A zoom/resize storm (Ctrl+wheel) can shrink the canvas between the invalidation and
                // this callback, leaving a stale region outside the new bounds — CreateDrawingSession
                // then throws E_INVALIDARG. Skip it: the resize already queued a fresh invalidation for
                // the new size. Left unhandled this is app-fatal (a stowed 0xc000027b XAML crash).
                // Reported like every other swallow: a host debugging "the canvas went blank" otherwise
                // has no way to tell a skipped region from a document that drew nothing. Diagnostics
                // dedupes per site, so a persistent fault cannot flood the render path.
                RichEditorDiagnostics.Report(ex);
            }
        }
    }

    // The cell-block selection, computed ONCE per draw pass. CellBlockSelection() walks the whole
    // document twice (FindCell per endpoint); calling it per drawn paragraph made selection rendering
    // O(paragraphs × document). Print paths bypass DrawDocument, but every consumer gates on _printMode.
    private (TableBlock tb, int r0, int c0, int r1, int c1)? _renderCellSel;

    // Hit-test geometry (inline-object rects, table outers/boundaries) is recorded as objects DRAW and
    // invalidated on relayout (ClearRecordedGeometry) — the walk no longer needs a document-wide
    // geometry pass per frame, so everything culls against the clip.
    private void DrawDocument(CanvasDrawingSession ds, Rect region)
    {
        if (Document == null) return;
        _renderCellSel = CellBlockSelection();

        double z = EffectiveZoom;
        if (PagedChrome) DrawPagedDocument(ds, region);
        else
        {
            // The region is physical (zoom-scaled); the walk works in logical doc space. ±1 avoids seams.
            DrawContentWalk(ds, region.Top / z - 1, region.Bottom / z + 1);
        }
        DrawTableDrawRubberBand(ds); // "draw table" overlay, on top of content
    }

    // Hard-line count of a list paragraph (its marker count), for advancing the ordered-list numbering
    // past paragraphs the clip culled without building their text.
    private static int HardLineCount(Paragraph p)
    {
        int n = 1;
        foreach (var inl in p.Inlines)
            if (inl is Run r && r.Text is { } t)
                foreach (char ch in t) if (ch == '\n') n++;
        return n;
    }

    // The clipped document walk (also reused per page, under a transform, in paged mode). Positions come
    // from the shared block layout map: a binary search finds the first block in [clipTop, clipBottom]
    // and the loop stops past the clip, so a draw pass costs O(visible) with no per-block advance work at
    // all for off-screen content (the map froze positions at the last relayout). Hit-test rects for
    // inline objects/tables record as they draw and persist until relayout (see ClearRecordedGeometry).
    private void DrawContentWalk(CanvasDrawingSession ds, double clipTop, double clipBottom)
    {
        if (Document == null) return;
        double width = _layoutWidth;
        const double listIndent = 10;
        var map = EnsureBlockLayout(width);

        for (int i = BlockIndexAtY(map, clipTop); i < map.Count; i++)
        {
            var (block, y, h, orderedStart) = map[i];
            if (y > clipBottom) break;

            if (block is Paragraph paragraph)
            {
                double px = ParaLeft(paragraph);
                double pWidth = Math.Max(10, width - 20 - px - paragraph.MarginRight);

                string fullText = BuildPlain(paragraph);
                var layout = BuildTextLayout(paragraph, pWidth);

                if (paragraph.Background is { } bg)
                    ds.FillRectangle(new Rect(px, y, pWidth, h), bg);

                if (paragraph.IsQuote)
                    ds.FillRectangle(new Rect(Math.Max(0, px - 10), y, 3, h), QuoteBarColor);

                DrawRunBackgrounds(ds, paragraph, layout, px, y);
                DrawFindHighlights(ds, paragraph, layout, px, y);
                DrawSelectionHighlight(ds, paragraph, layout, px, y);

                if (paragraph.ListType != ListKind.None)
                {
                    int orderedIndex = orderedStart; // numbering consumed before this block (from the map)
                    DrawListMarkers(ds, paragraph, layout, fullText, px, y, ref orderedIndex);
                }

                ds.DrawTextLayout(layout, (float)px, (float)y, EffectiveTextColor);
                DrawInlineObjects(ds, paragraph, layout, px, y);
                DrawCompositionUnderline(ds, paragraph, layout, px, y);
                DrawCaret(ds, paragraph, layout, px, y);
                DrawDropPreview(ds, paragraph, layout, px, y);
            }
            else if (block is ImageBlock img)
            {
                double imgX = listIndent + img.Indent;
                var bmp = _images.Get(_canvas, img, img.RawBytes, img.Image);
                var (w, _) = BlockImageDims(img); // single source with measure/hit-test (no drift)
                var rect = new Rect(imgX, y, w, h);
                if (bmp != null)
                    ds.DrawImage(bmp, rect);
                else
                    DrawPlaceholder(ds, rect, "loading image…");
                DrawBlockImageChrome(ds, img, rect);
            }
            else if (block is DividerBlock)
            {
                double ly = y + DividerHeight / 2;
                ds.DrawLine((float)listIndent, (float)ly, (float)Math.Max(listIndent + 1, width - 10), (float)ly, GrayBorderColor, 1f);
            }
            else if (block is TableBlock tb)
            {
                double startX = listIndent + tb.Indent;
                var tl = LayoutTable(tb, startX, y);
                DrawTableBlock(ds, tb, startX, y);
                RecordTopLevelTableRect(tb, y, tl);
                DrawTableSelectionChrome(ds, tb, startX, y, tl);
            }
        }
    }

    private static readonly Color DeskColor = Color.FromArgb(255, 0xDA, 0xDA, 0xDA);
    private static readonly Color PageBorderColor = Color.FromArgb(255, 0xC0, 0xC0, 0xC0);

    // Paged render: stacks white pages on a grey desk and draws each page's content slice via a
    // translate+clip, reusing the continuous walk. Only pages intersecting the invalidated region are
    // walked, each clipped to its own doc range; hit-test geometry records during those walks (doc
    // space — the page translate lives in the transform) and persists until relayout, so no page is
    // ever walked just for geometry.
    private void DrawPagedDocument(CanvasDrawingSession ds, Rect region)
    {
        var breaks = EnsurePageBreaks();
        int pages = breaks.Count;
        double pageContentH = PaperContentHeight;
        double z = EffectiveZoom;
        double rTop = region.Top / z, rBottom = region.Bottom / z; // region in logical view coords

        double deskW = Math.Max(CanvasLogicalWidth, PaperWidth), deskH = pages * (PaperHeight + PageGap) + PageGap;
        ds.FillRectangle(new Rect(0, 0, deskW, deskH), DeskColor);

        for (int i = 0; i < pages; i++)
        {
            double pageTop = PageGap + i * (PaperHeight + PageGap);
            bool pageVisible = pageTop <= rBottom && pageTop + PaperHeight >= rTop;
            if (!pageVisible) continue;

            var pageRect = new Rect(PageDeskX, pageTop, PaperWidth, PaperHeight);
            ds.FillRectangle(pageRect, Colors.White);
            ds.DrawRectangle(pageRect, PageBorderColor, 1f);

            double viewContentLeft = PageDeskX + PagePadX;
            double viewContentTop = pageTop + PagePadY;
            var clip = new Rect(viewContentLeft, viewContentTop, PaperContentWidth, pageContentH);
            using (ds.CreateLayer(1f, clip))
            {
                var saved = ds.Transform; // the zoom base; compose so the page translate happens in logical units
                ds.Transform = System.Numerics.Matrix3x2.CreateTranslation(
                    (float)(viewContentLeft - DocContentLeft),
                    (float)(viewContentTop - breaks[i])) * saved; // map this page's doc-top to the content box
                DrawContentWalk(ds, breaks[i] - 1, breaks[i] + pageContentH + 1);
                ds.Transform = saved;
            }
            DrawPageMarginChrome(ds, pageRect, i, pages);
        }
    }

    private static readonly Color MarginChromeColor = Color.FromArgb(255, 0x80, 0x80, 0x80);

    // Header/footer/page-number drawn in the page's margin bands (outside the content box, so pagination
    // is unaffected). `pageRect` is in view space.
    private void DrawPageMarginChrome(CanvasDrawingSession ds, Rect pageRect, int pageIndex, int pageCount)
    {
        if (string.IsNullOrEmpty(PageHeader) && string.IsNullOrEmpty(PageFooter) && !ShowPageNumbers) return;
        using var fmt = new CanvasTextFormat { FontFamily = DefaultFontFamily, FontSize = 11f, WordWrapping = CanvasWordWrapping.NoWrap };
        double left = pageRect.X + PagePadX;

        void DrawSmall(string text, bool top, bool right)
        {
            using var tl = new CanvasTextLayout(ds, text, fmt, (float)PaperContentWidth, (float)PagePadY);
            double x = right ? left + PaperContentWidth - tl.LayoutBounds.Width : left;
            double bandCenter = top ? pageRect.Y + PagePadY / 2 : pageRect.Bottom - PagePadY / 2;
            ds.DrawTextLayout(tl, (float)x, (float)(bandCenter - tl.LayoutBounds.Height / 2), MarginChromeColor);
        }

        if (!string.IsNullOrEmpty(PageHeader)) DrawSmall(PageHeader!, top: true, right: false);
        if (!string.IsNullOrEmpty(PageFooter)) DrawSmall(PageFooter!, top: false, right: false);
        if (ShowPageNumbers) DrawSmall($"{pageIndex + 1} / {pageCount}", top: false, right: true);
    }

    // Draws one marker per hard line (\n) of a list paragraph, right-aligned just left of the text and
    // BASELINE-aligned with that line's text. Top-aligning (the old GetCharacterRegions Y) drifted
    // whenever the text baseline moved inside the line box — custom LineSpacing sets a uniform line with
    // LineSpacingBaseline, so at 200% the bullet floated well above the text it labels.
    private void DrawListMarkers(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, string fullText, double px, double oy, ref int orderedIndex)
    {
        var lines = LineMetricsOf(layout, fullText.Length);

        int segStart = 0;
        for (int i = 0; i <= fullText.Length; i++)
        {
            if (i == fullText.Length || fullText[i] == '\n')
            {
                int marker = p.ListType == ListKind.Ordered ? ++orderedIndex : 0;
                // The visual line containing this hard line's first character: its top (accumulated
                // heights) and its baseline (distance from the line top, per DirectWrite).
                double lineTop = 0, lineBaseline = double.NaN;
                int acc = 0; double yAcc = 0;
                for (int li = 0; li < lines.Length; li++)
                {
                    if (segStart < acc + lines[li].CharacterCount || li == lines.Length - 1)
                    {
                        lineTop = yAcc;
                        lineBaseline = lines[li].Baseline;
                        break;
                    }
                    yAcc += lines[li].Height;
                    acc += lines[li].CharacterCount;
                }
                DrawOneMarker(ds, p, marker, px, oy + lineTop, lineBaseline);
                segStart = i + 1;
            }
        }
    }

    private void DrawOneMarker(CanvasDrawingSession ds, Paragraph p, int num, double textLeft, double lineTopY, double lineBaseline)
    {
        string m = ListMarkers.Text(p.ListType, p.ListMarker, num);
        Run? first = null;
        foreach (var inl in p.Inlines) if (inl is Run r) { first = r; break; }
        double size = first is { FontSize: > 0 } ? first.FontSize : DefaultFontSize;
        string family = first != null && !string.IsNullOrEmpty(first.FontFamily) ? first.FontFamily! : DefaultFontFamily;
        var weight = first?.FontWeight ?? FontWeights.Normal;
        if (p.HeadingLevel is >= 1 and <= 6)
        {
            if (first == null || RunSizeIsBodyDefault(first)) size = HeadingFontSize(p.HeadingLevel);
            weight = FontWeights.Bold;
        }
        var color = first?.Foreground ?? EffectiveTextColor;

        var ml = GetMarkerLayout(m, family, size, weight); // cached across frames (cache owns/disposes it)
        const double gap = 6;
        double mw = ml.LayoutBounds.Width;
        // Baseline-align: the marker's own baseline sits on the text line's baseline. Falls back to
        // top-align when line metrics were unavailable. With natural spacing the two baselines coincide
        // (same font metrics), so this is a no-op there and only moves markers under custom spacing.
        double y = lineTopY;
        if (!double.IsNaN(lineBaseline))
        {
            double mlBaseline = 0;
            var mlm = LineMetricsOf(ml, m.Length);
            if (mlm.Length > 0 && !double.IsNaN(mlm[0].Baseline)) mlBaseline = mlm[0].Baseline;
            if (mlBaseline > 0) y = lineTopY + lineBaseline - mlBaseline;
        }
        ds.DrawTextLayout(ml, (float)(textLeft - gap - mw), (float)y, color);
    }

    // List markers repeat heavily and change rarely; caching their small layouts across frames avoids
    // allocating a CanvasTextFormat + CanvasTextLayout per marker per draw pass (a measurable cost when
    // scrolling long lists). Keyed by text + face; disposed with the main layout cache.
    private readonly Dictionary<(string text, string family, double sizePt, ushort weight), CanvasTextLayout> _markerLayouts = new();

    private CanvasTextLayout GetMarkerLayout(string m, string family, double sizePt, Windows.UI.Text.FontWeight weight)
    {
        var key = (m, family, sizePt, weight.Weight);
        if (_markerLayouts.TryGetValue(key, out var cached)) return cached;
        if (_markerLayouts.Count > 256) ClearMarkerLayouts(); // tiny working set; clear-all is fine
        using var fmt = new CanvasTextFormat { FontFamily = family, FontSize = (float)PtToPx(sizePt), FontWeight = weight, WordWrapping = CanvasWordWrapping.NoWrap };
        var layout = new CanvasTextLayout(CanvasDevice.GetSharedDevice(), m, fmt, 1000f, 0f);
        _markerLayouts[key] = layout;
        return layout;
    }

    private void ClearMarkerLayouts()
    {
        foreach (var l in _markerLayouts.Values) l.Dispose();
        _markerLayouts.Clear();
    }

    // Paints each atomic inline (image or table) over the U+FFFC slot the layout reserved (the spacer
    // object only sized it). Drawing happens here, after the text layout, so a cached layout never holds
    // a stale drawing session.
    private void DrawInlineObjects(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        using var pin = new LayoutPin(this); // `layout` (host) is used across DrawNestedTable's cell builds
        int off = 0;
        foreach (var inl in p.Inlines)
        {
            // GetCharacterRegions can throw (a transient device/layout race, an out-of-range slot during
            // a resize storm); every OTHER caller wraps it in a try/catch, but this one didn't — and
            // OnRegionsInvalidated only rescues E_INVALIDARG, so any other HResult here killed the app.
            try
            {
                if (inl is InlineImage ii)
                {
                    var regions = layout.GetCharacterRegions(off, 1);
                    if (regions.Length > 0)
                    {
                        var lb = regions[0].LayoutBounds;
                        double w = Math.Max(8, ii.Width > 0 ? ii.Width : 16);
                        double h = Math.Max(8, ii.Height > 0 ? ii.Height : 16);
                        var rect = new Rect(px + lb.X, oy + lb.Bottom - h, w, h);
                        var bmp = _images.Get(_canvas, ii, ii.RawBytes, ii.Image);
                        if (bmp != null) ds.DrawImage(bmp, rect);
                        else DrawPlaceholder(ds, rect, "");
                        TrackInlineImage(ds, p, ii, rect);
                    }
                }
                else if (inl is InlineTable it)
                {
                    var regions = layout.GetCharacterRegions(off, 1);
                    if (regions.Length > 0)
                    {
                        var lb = regions[0].LayoutBounds;
                        DrawNestedTable(ds, it.Table, px + lb.X, oy + lb.Y);
                        TrackInlineTable(ds, p, it, new Rect(px + lb.X, oy + lb.Y, lb.Width, lb.Height));
                    }
                }
            }
            catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
            off += InlineLen(inl);
        }
    }

    // Fills the highlight (run background) color behind each run that has one (CanvasTextLayout has no
    // per-range background, so it's painted manually from the character regions, under the text).
    private void DrawRunBackgrounds(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        int off = 0;
        foreach (var inl in p.Inlines)
        {
            int len = InlineLen(inl);
            if (inl is Run r && r.Background is { } bg && len > 0)
            {
                try
                {
                    foreach (var reg in layout.GetCharacterRegions(off, len))
                    {
                        var lb = reg.LayoutBounds;
                        ds.FillRectangle(new Rect(px + lb.X, oy + lb.Y, lb.Width, lb.Height), bg);
                    }
                }
                catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
            }
            off += len;
        }
    }

    // Highlight-all overlay for the active find query (browser-style): every occurrence in a drawn
    // paragraph is tinted amber; the (stronger, blue) selection highlight paints the current match on
    // top. Only runs while a find UI is open (FindHighlightQuery non-null), so the per-paragraph
    // IndexOf scan costs nothing in normal editing.
    private static readonly Color FindMatchFill = Color.FromArgb(70, 255, 190, 0);

    private void DrawFindHighlights(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        if (_printMode || FindHighlightQuery is not { } q) return;
        var cmp = FindHighlightMatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        string text = BuildPlain(p);

        // The CURRENT match is already marked by the selection, so it must not be tinted as well: amber
        // over the translucent selection blue blends into a muddy low-contrast fill and the user loses
        // track of which match the caret is on. Highlight-all marks the OTHER matches (browser / VS Code
        // behaviour). Same "the selection IS a match" test as GetFindMatchPosition.
        int selStart = -1;
        if (_selStart.Paragraph != null && ReferenceEquals(_selStart.Paragraph, p)
            && ReferenceEquals(_selEnd.Paragraph, p))
        {
            int a = Math.Min(_selStart.Offset, _selEnd.Offset);
            int b = Math.Max(_selStart.Offset, _selEnd.Offset);
            if (b - a == q.Length) selStart = a;
        }

        int from = 0;
        while (from <= text.Length)
        {
            int idx = text.IndexOf(q, from, cmp);
            if (idx < 0) break;
            if (idx != selStart)
            {
                try
                {
                    foreach (var r in layout.GetCharacterRegions(idx, q.Length))
                    {
                        var lb = r.LayoutBounds;
                        ds.FillRectangle(new Rect(px + lb.X, oy + lb.Y, lb.Width, lb.Height), FindMatchFill);
                    }
                }
                catch (Exception ex) { RichEditorDiagnostics.Report(ex); }
            }
            from = idx + 1;
        }
    }

    // Grey preview caret at the pending drop position while dragging selected text (see RichEditor.DragText.cs).
    private void DrawDropPreview(CanvasDrawingSession ds, Paragraph p, CanvasTextLayout layout, double px, double oy)
    {
        if (_printMode || !_dragTextActive || _dropPreview is not { } dp || !ReferenceEquals(dp.Paragraph, p)) return;
        var (cx, cy, ch, _, _) = CaretInLayout(layout, p, dp.Offset, dp.AtLineEnd);
        float x = (float)(px + cx);
        ds.DrawLine(x, (float)(oy + cy), x, (float)(oy + cy + ch), GrayBorderColor, 2f);
    }

    private void DrawPlaceholder(CanvasDrawingSession ds, Rect rect, string label)
    {
        ds.FillRectangle(rect, PlaceholderFill);
        ds.DrawRectangle(rect, PlaceholderBorder, 1f);
        if (!string.IsNullOrEmpty(label) && rect.Width > 40 && rect.Height > 14)
            ds.DrawText(label, (float)(rect.X + 6), (float)(rect.Y + 4), GrayBorderColor);
    }
}
