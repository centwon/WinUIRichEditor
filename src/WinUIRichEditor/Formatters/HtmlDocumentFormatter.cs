using HtmlAgilityPack;
using System.Collections.Generic;
using System.Linq;
using WinUIRichEditor.Documents;
using System;
using System.Text;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;

namespace WinUIRichEditor.Formatters;

/// <summary>Converts between <see cref="FlowDocument"/> and HTML.
/// Supports full round-trip for bold/italic/underline/strikethrough, colors, sizes, alignment,
/// headings, lists, tables (with cell merge), images, hyperlinks, and horizontal rules.</summary>
public static class HtmlDocumentFormatter
{
    private static readonly HashSet<string> BlockOrMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        "div","p","table","ul","ol","li","img","h1","h2","h3","h4","h5","h6",
        "section","article","figure","figcaption","header","footer","main","aside","tr","blockquote","hr","pre"
    };

    private static readonly HashSet<string> BlockLeaf = new(StringComparer.OrdinalIgnoreCase)
    {
        "p","h1","h2","h3","h4","h5","h6","li","blockquote","div","section","article",
        "figure","figcaption","header","footer","main","aside","pre","caption"
    };

    // 20MB response cap: a hostile/huge <img src="http:…"> can't balloon memory during a paste.
    private static readonly System.Net.Http.HttpClient Http = new()
    { Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 20 * 1024 * 1024 };
    private static readonly TimeSpan RemoteImageBudget = TimeSpan.FromSeconds(5);
    [ThreadStatic] private static DateTime _remoteImageDeadline;
    [ThreadStatic] private static bool _blockLocalFileImages;
    [ThreadStatic] private static bool _blockRemoteImages;
    [ThreadStatic] private static Dictionary<string, byte[]?>? _prefetchedRemoteImages;
    // Per-parse memo for HasBlockOrMedia: the naive Descendants().Any() per node is O(n²) on deep
    // documents (Word/Docs HTML nests hard); the memoized child recursion is O(n) total.
    [ThreadStatic] private static Dictionary<HtmlNode, bool>? _blockOrMediaMemo;

    // A normalized bold/normal weight: HTML parsing only ever needs the two states, and the WinRT
    // FontWeight struct can't be a default parameter value, so we carry default(FontWeight) around and
    // collapse it here at the point a Run is built.
    private static FontWeight NormalizeWeight(FontWeight w) => w.IsBold() ? FontWeightValues.Bold : FontWeightValues.Normal;

    /// <summary>Parses an HTML string into a <see cref="FlowDocument"/>.
    /// When <paramref name="allowLocalFileImages"/> is false, <c>file://</c> image sources are skipped.
    /// Remote (<c>http</c>) images are downloaded synchronously on the calling thread (a per-parse 5 s
    /// budget caps the stall); use <see cref="ParseHtmlAsync"/> to fetch them without blocking.</summary>
    public static FlowDocument ParseHtml(string html, bool allowLocalFileImages = true, bool allowRemoteImages = true)
    {
        _remoteImageDeadline = DateTime.UtcNow + RemoteImageBudget;
        _blockLocalFileImages = !allowLocalFileImages;
        _blockRemoteImages = !allowRemoteImages;
        _prefetchedRemoteImages = null;
        _blockOrMediaMemo = null; // fresh memo per parse
        var doc = LoadHtmlDoc(ref html);
        try { return RunNormalizer.Compact(BuildDocument(doc, html)); }
        finally { _blockOrMediaMemo = null; } // don't retain the DOM past the parse
    }

    /// <summary>Same as <see cref="ParseHtml"/> but downloads remote (<c>http</c>) images concurrently
    /// off the UI thread first, so a slow network can't freeze the UI while pasting web content.
    /// <paramref name="allowRemoteImages"/> false skips the network entirely (privacy: pasting HTML
    /// otherwise issues HTTP requests, e.g. to tracking pixels).</summary>
    public static async System.Threading.Tasks.Task<FlowDocument> ParseHtmlAsync(string html, bool allowLocalFileImages = true, bool allowRemoteImages = true)
    {
        var doc = LoadHtmlDoc(ref html);
        var prefetched = allowRemoteImages
            ? await PrefetchRemoteImagesAsync(doc).ConfigureAwait(true)
            : new Dictionary<string, byte[]?>();
        _remoteImageDeadline = DateTime.UtcNow + RemoteImageBudget; // data:/file: images still honored
        _blockLocalFileImages = !allowLocalFileImages;
        _blockRemoteImages = !allowRemoteImages;
        _prefetchedRemoteImages = prefetched;
        _blockOrMediaMemo = null; // fresh memo per parse
        try { return RunNormalizer.Compact(BuildDocument(doc, html)); }
        finally { _prefetchedRemoteImages = null; _blockOrMediaMemo = null; }
    }

    // Excel's CF_HTML fragment markers can sit inside the <table>, leaving orphan <tr>/<td> with no
    // wrapping <table>. Wrap it so it's recognized.
    private static HtmlDocument LoadHtmlDoc(ref string html)
    {
        if (System.Text.RegularExpressions.Regex.IsMatch(html, "<tr[\\s>]", System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            && !System.Text.RegularExpressions.Regex.IsMatch(html, "<table", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            html = "<table>" + html + "</table>";
        var doc = new HtmlDocument();
        doc.LoadHtml(html);
        return doc;
    }

    private static FlowDocument BuildDocument(HtmlDocument doc, string html)
    {
        var flowDoc = new FlowDocument();
        var root = doc.DocumentNode.Descendants("body").FirstOrDefault() ?? doc.DocumentNode;
        WalkBlocks(root, flowDoc);

        if (flowDoc.Blocks.Count == 0)
        {
            var p = new Paragraph();
            p.Inlines.Add(new Run { Text = HtmlEntity.DeEntitize(html) });
            flowDoc.Blocks.Add(p);
        }
        return flowDoc;
    }

    private static async System.Threading.Tasks.Task<Dictionary<string, byte[]?>> PrefetchRemoteImagesAsync(HtmlDocument doc)
    {
        var result = new Dictionary<string, byte[]?>();
        var urls = doc.DocumentNode.Descendants("img")
            .Select(n => n.GetAttributeValue("src", ""))
            .Where(s => s.StartsWith("http"))
            .Distinct()
            .ToList();
        if (urls.Count == 0) return result;

        using var cts = new System.Threading.CancellationTokenSource(RemoteImageBudget);
        async System.Threading.Tasks.Task<(string, byte[]?)> Fetch(string url)
        {
            try { return (url, await Http.GetByteArrayAsync(url, cts.Token).ConfigureAwait(false)); }
            catch { return (url, null); }
        }
        foreach (var (url, bytes) in await System.Threading.Tasks.Task.WhenAll(urls.Select(Fetch)).ConfigureAwait(false))
            result[url] = bytes;
        return result;
    }

    // Recursively walks the DOM, emitting Paragraph/TableBlock/ImageBlock as it goes. Consecutive inline
    // siblings accumulate into a single paragraph, flushed whenever a block-level element is encountered.
    private static void WalkBlocks(HtmlNode node, FlowDocument flow, string? linkUri = null)
    {
        Paragraph? current = null;
        void Flush()
        {
            if (current != null && current.Inlines.Count > 0) flow.Blocks.Add(current);
            current = null;
        }

        foreach (var child in node.ChildNodes)
        {
            string name = child.Name.ToLowerInvariant();

            string? childLink = linkUri;
            if (name == "a")
            {
                var href = child.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href)) childLink = href;
            }
            bool hasLink = !string.IsNullOrEmpty(childLink);

            if (name == "table")
            {
                Flush();
                var tbl = ParseTable(child);
                if (tbl != null) flow.Blocks.Add(tbl);
            }
            else if (name == "img")
            {
                var (bytes, w, h, alt) = LoadImage(child);
                if (bytes != null)
                {
                    if (w < IconMaxSize && h < IconMaxSize)
                    {
                        // Small icon/logo -> keep on a text line rather than its own block.
                        var icon = new InlineImage { Width = w, Height = h, AltText = alt };
                        icon.SetImageData(bytes, ImageMime.Detect(bytes));
                        if (current != null)
                            current.Inlines.Add(icon);
                        else if (flow.Blocks.Count > 0 && flow.Blocks[flow.Blocks.Count - 1] is Paragraph lastP)
                            lastP.Inlines.Add(icon);
                        else
                        {
                            current = new Paragraph();
                            current.Inlines.Add(icon);
                        }
                    }
                    else
                    {
                        Flush();
                        var ib = new ImageBlock { Width = w, Height = h, AltText = alt };
                        ib.SetImageData(bytes, ImageMime.Detect(bytes));
                        flow.Blocks.Add(ib);
                    }
                }
            }
            else if (name == "ul" || name == "ol")
            {
                Flush();
                ParseList(child, flow, name == "ol" ? ListKind.Ordered : ListKind.Bullet, 0, linkUri);
            }
            else if (name == "hr")
            {
                Flush();
                flow.Blocks.Add(new DividerBlock());
            }
            else if (name == "br")
            {
                current ??= new Paragraph();
                current.Inlines.Add(new Run { Text = "\n" });
            }
            else if (name == "#text")
            {
                string t = HtmlEntity.DeEntitize(child.InnerText);
                if (!string.IsNullOrWhiteSpace(t))
                {
                    current ??= new Paragraph();
                    current.Inlines.Add(new Run
                    {
                        Text = CollapseWhitespace(t),
                        NavigateUri = linkUri,
                        Foreground = hasLink ? Colors.Blue : (Color?)null
                    });
                }
            }
            else if (name == "#comment" || name == "script" || name == "style" || name == "head" || name == "meta" || name == "link")
            {
                // ignore
            }
            else if (HasBlockOrMedia(child))
            {
                Flush();
                WalkBlocks(child, flow, childLink);
            }
            else if (BlockLeaf.Contains(name))
            {
                Flush();
                int hl = (name.Length == 2 && name[0] == 'h' && name[1] >= '1' && name[1] <= '6') ? name[1] - '0' : 0;
                var p = new Paragraph
                {
                    HeadingLevel = hl,
                    Background = ReadBackground(child),
                    Indent = ReadIndentPx(child),
                    IsQuote = name == "blockquote",
                    TextAlignment = ReadAlign(child)
                };
                ApplyLineHeightStyle(child, p);
                double size = HeadingSize(name, out var headingWeight);
                ParseInlines(child, p, headingWeight, FontStyle.Normal, null, childLink, size, hasLink,
                    pre: name == "pre"); // <pre> keeps its whitespace/newlines verbatim
                if (p.Inlines.Count > 0) flow.Blocks.Add(p);
            }
            else
            {
                current ??= new Paragraph();
                // The node itself (not just its children) goes through the inline path, so a bare
                // formatted element with no block wrapper keeps its own tag/style formatting.
                // Parent link context is passed; the node's own <a>/style is read inside.
                ParseInlineNode(child, current, uri: linkUri, inLink: !string.IsNullOrEmpty(linkUri));
            }
        }

        Flush();
    }

    private static void ParseList(HtmlNode listNode, FlowDocument flow, ListKind kind, int level, string? linkUri)
    {
        var marker = ListMarkerFromCss(ReadStyleValue(listNode, "list-style-type"));
        foreach (var li in listNode.ChildNodes.Where(n => n.Name.Equals("li", StringComparison.OrdinalIgnoreCase)))
        {
            var p = new Paragraph { ListType = kind, ListLevel = level, ListMarker = marker };
            ApplyLineHeightStyle(li, p);
            ParseInlines(li, p, uri: linkUri, inLink: !string.IsNullOrEmpty(linkUri));
            if (p.Inlines.Count > 0) flow.Blocks.Add(p);

            foreach (var nested in li.ChildNodes.Where(n => n.Name.Equals("ul", StringComparison.OrdinalIgnoreCase) || n.Name.Equals("ol", StringComparison.OrdinalIgnoreCase)))
                ParseList(nested, flow, nested.Name.Equals("ol", StringComparison.OrdinalIgnoreCase) ? ListKind.Ordered : ListKind.Bullet, level + 1, linkUri);
        }
    }

    private static string? ReadStyleValue(HtmlNode node, string prop)
    {
        var style = node.GetAttributeValue("style", "");
        if (string.IsNullOrEmpty(style)) return null;
        var m = System.Text.RegularExpressions.Regex.Match(style, prop + @"\s*:\s*([^;]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value.Trim() : null;
    }

    private static TextAlignment ReadAlign(HtmlNode node)
    {
        string a = node.GetAttributeValue("align", "").ToLowerInvariant();
        var style = node.GetAttributeValue("style", "").ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(style, "text-align\\s*:\\s*(left|center|right|justify)");
        if (m.Success) a = m.Groups[1].Value;
        return a switch { "center" => TextAlignment.Center, "right" => TextAlignment.Right, "justify" => TextAlignment.Justify, _ => TextAlignment.Left };
    }

    // Heading sizes in points (pt), mirroring RichEditor.HeadingFontSize.
    private static double HeadingSize(string name, out FontWeight weight)
    {
        switch (name)
        {
            case "h1": weight = FontWeightValues.Bold; return 20;
            case "h2": weight = FontWeightValues.Bold; return 16;
            case "h3": weight = FontWeightValues.Bold; return 14;
            case "h4": weight = FontWeightValues.Bold; return 12;
            case "h5": weight = FontWeightValues.Bold; return 11;
            case "h6": weight = FontWeightValues.Bold; return 10;
            default: weight = FontWeightValues.Normal; return 10;
        }
    }

    private static bool HasBlockOrMedia(HtmlNode n)
    {
        var memo = _blockOrMediaMemo ??= new Dictionary<HtmlNode, bool>();
        if (memo.TryGetValue(n, out var known)) return known;
        bool result = false;
        foreach (var c in n.ChildNodes)
            if (BlockOrMedia.Contains(c.Name) || HasBlockOrMedia(c)) { result = true; break; }
        memo[n] = result;
        return result;
    }

    private static string CollapseWhitespace(string s)
    {
        return System.Text.RegularExpressions.Regex.Replace(s, "\\s+", " ");
    }

    private static TableBlock? ParseTable(HtmlNode node)
    {
        var rows = node.Descendants("tr")
            .Where(tr => tr.Ancestors("table").FirstOrDefault() == node)
            .ToList();
        if (rows.Count == 0) return null;
        int R = rows.Count;

        var cellNodes = new List<List<HtmlNode>>();
        foreach (var tr in rows)
            cellNodes.Add(tr.ChildNodes.Where(n => n.Name == "td" || n.Name == "th").ToList());

        var occupied = new List<List<bool>>();
        for (int i = 0; i < R; i++) occupied.Add(new List<bool>());
        var placements = new List<List<(int col, int cs, int rs, HtmlNode node)>>();
        for (int i = 0; i < R; i++) placements.Add(new List<(int, int, int, HtmlNode)>());
        int colCount = 0;

        static void Ensure(List<bool> row, int upTo) { while (row.Count <= upTo) row.Add(false); }

        for (int r = 0; r < R; r++)
        {
            int col = 0;
            foreach (var td in cellNodes[r])
            {
                Ensure(occupied[r], col);
                while (col < occupied[r].Count && occupied[r][col]) col++;
                int cs = Math.Max(1, td.GetAttributeValue("colspan", 1));
                int rs = Math.Max(1, Math.Min(td.GetAttributeValue("rowspan", 1), R - r));
                placements[r].Add((col, cs, rs, td));
                for (int rr = r; rr < r + rs; rr++)
                {
                    Ensure(occupied[rr], col + cs - 1);
                    for (int cc = col; cc < col + cs; cc++) occupied[rr][cc] = true;
                }
                col += cs;
                colCount = Math.Max(colCount, col);
            }
        }
        if (colCount == 0) return null;

        var tb = new TableBlock(R, colCount);
        var colNodes = node.Descendants("col")
            .Where(co => co.Ancestors("table").FirstOrDefault() == node)
            .ToList();
        if (colNodes.Count > 0)
        {
            tb.ColumnWidths.Clear();
            for (int c = 0; c < colCount; c++)
            {
                double cw = c < colNodes.Count ? ReadPx(colNodes[c], "width", "width") : 0;
                tb.ColumnWidths.Add(cw > 0 ? cw : 100);
            }
        }
        for (int r = 0; r < R; r++)
            foreach (var (col, cs, rs, td) in placements[r])
            {
                if (cs > 1 || rs > 1) tb.SetSpan(r, col, cs, rs);
                var cell = tb.Cells[r][col];
                cell.Background = ReadBackground(td);
                cell.VerticalAlignment = ReadCellVAlign(td);
                var cellFlow = new FlowDocument();
                WalkBlocks(td, cellFlow);
                cell.Blocks.Clear();
                foreach (var b in cellFlow.Blocks) cell.Blocks.Add(b);
                if (cell.Blocks.Count == 0) cell.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "" } } });
            }
        return tb;
    }

    // Images below this size (px, both dimensions) are treated as inline icons/logos/emoji.
    private const double IconMaxSize = 64;

    // Loads an <img> and returns the original encoded bytes plus its intended display size (declared px
    // when present, otherwise the natural size read from the header) and its alt text. The actual GPU
    // decode is deferred to the render layer. Returns (null,0,0,null) on failure/unsupported source.
    private static (byte[]?, double, double, string?) LoadImage(HtmlNode node)
    {
        var src = node.GetAttributeValue("src", "");
        if (string.IsNullOrEmpty(src)) return (null, 0, 0, null);

        string altAttr = node.GetAttributeValue("alt", "");
        string? alt = string.IsNullOrWhiteSpace(altAttr) ? null : HtmlEntity.DeEntitize(altAttr);
        double declW = ReadPx(node, "width", "width");
        double declH = ReadPx(node, "height", "height");

        try
        {
            byte[]? bytes = null;
            if (src.StartsWith("data:image"))
            {
                var comma = src.IndexOf(',');
                if (comma >= 0) bytes = System.Convert.FromBase64String(src.Substring(comma + 1));
            }
            else if (src.StartsWith("http"))
            {
                if (_blockRemoteImages) return (null, 0, 0, null);
                if (_prefetchedRemoteImages != null)
                {
                    _prefetchedRemoteImages.TryGetValue(src, out bytes);
                    if (bytes == null) return (null, 0, 0, null);
                }
                else
                {
                    var remaining = _remoteImageDeadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero) return (null, 0, 0, null);
                    using var cts = new System.Threading.CancellationTokenSource(remaining);
                    bytes = Http.GetByteArrayAsync(src, cts.Token).GetAwaiter().GetResult();
                }
            }
            else if (src.StartsWith("file:") || src.StartsWith("ms-clipboard-file:", StringComparison.OrdinalIgnoreCase))
            {
                // HtmlFormatHelper.GetStaticFragment (the CF_HTML fragment extractor the paste path
                // uses) REWRITES file:/// references to the ms-clipboard-file: scheme — treat it as
                // file:, or every HWP/Word picture reference silently misses this branch.
                if (src.StartsWith("ms-clipboard-file:", StringComparison.OrdinalIgnoreCase))
                    src = "file:" + src.Substring("ms-clipboard-file:".Length);
                var path = new Uri(src).LocalPath;
                // Even when local files are blocked (the paste default), allow paths under %TEMP%:
                // Word/HWP CF_HTML reference the pictures the COPY itself just wrote there — refusing
                // them silently drops every image pasted from those apps. Anything outside the temp
                // directory (a hostile page referencing user files) stays blocked.
                if (_blockLocalFileImages && !IsTempPath(path)) return (null, 0, 0, null);
                if (System.IO.File.Exists(path)) bytes = System.IO.File.ReadAllBytes(path);
            }
            if (bytes == null) return (null, 0, 0, null);
            var (natW, natH) = ImageInfo.GetPixelSize(bytes);
            // Unknown natural size with no declared size -> NaN (= "natural", resolved at render): this
            // also makes the icon test below fail, so an unsized image becomes a block, not a 0px icon.
            double w = (!double.IsNaN(declW) && declW > 0) ? declW : (natW > 0 ? natW : double.NaN);
            double h = (!double.IsNaN(declH) && declH > 0) ? declH : (natH > 0 ? natH : double.NaN);
            return (bytes, w, h, alt);
        }
        catch { return (null, 0, 0, null); }
    }

    // True when the path resolves under the user's temp directory (clipboard image files live there).
    private static bool IsTempPath(string path)
    {
        try
        {
            string temp = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())
                .TrimEnd(System.IO.Path.DirectorySeparatorChar) + System.IO.Path.DirectorySeparatorChar;
            return System.IO.Path.GetFullPath(path).StartsWith(temp, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static double ReadPx(HtmlNode node, string attr, string cssProp)
    {
        var a = node.GetAttributeValue(attr, "").Trim();
        // HWP emits nonstandard unit suffixes in the ATTRIBUTES (width="168pt"); accept pt (→px ×4/3)
        // and px so those sizes aren't silently dropped. Percentages still fall through (NaN).
        double mult = 1;
        if (a.EndsWith("pt", StringComparison.OrdinalIgnoreCase)) { mult = 96.0 / 72.0; a = a[..^2]; }
        else if (a.EndsWith("px", StringComparison.OrdinalIgnoreCase)) a = a[..^2];
        if (double.TryParse(a, System.Globalization.CultureInfo.InvariantCulture, out var v)) return v * mult;
        var style = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(style))
        {
            var m = System.Text.RegularExpressions.Regex.Match(style, cssProp + "\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)px",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var px)) return px;
        }
        return double.NaN;
    }

    private static void ParseInlines(HtmlNode node, Paragraph p, FontWeight weight = default, FontStyle style = FontStyle.Normal, Color? color = null, string? uri = null, double baseSize = 10, bool inLink = false, Color? background = null, string? family = null, bool underline = false, bool strike = false, bool pre = false)
    {
        foreach (var child in node.ChildNodes)
            ParseInlineNode(child, p, weight, style, color, uri, baseSize, inLink, background, family, underline, strike, pre);
    }

    // Parses ONE node as inline content, applying the node's OWN tag/style formatting before descending
    // into its children. Split out of ParseInlines so WalkBlocks can feed a formatted inline element that
    // has no block wrapper (a bare <span style=…>/<b> directly under body — the shape CF_HTML fragments
    // take for small copies) through the same path; handing such an element to ParseInlines directly
    // dropped its own formatting, since ParseInlines only reads formatting off the nodes it descends INTO.
    private static void ParseInlineNode(HtmlNode child, Paragraph p, FontWeight weight = default, FontStyle style = FontStyle.Normal, Color? color = null, string? uri = null, double baseSize = 10, bool inLink = false, Color? background = null, string? family = null, bool underline = false, bool strike = false, bool pre = false)
    {
        {
            var cw = weight;
            var cs = style;
            var cc = color;
            var cu = uri;
            double sz = baseSize;
            var cbg = background;
            var cfam = family;
            bool cunder = underline;
            bool cstrike = strike;

            string name = child.Name.ToLowerInvariant();

            if (name == "br") { p.Inlines.Add(new Run { Text = "\n" }); return; }
            if (name == "ul" || name == "ol") return;

            if (name == "b" || name == "strong") cw = FontWeightValues.Bold;
            if (name == "i" || name == "em") cs = FontStyle.Italic;
            if (name == "u") cunder = true;
            if (name == "s" || name == "strike" || name == "del") cstrike = true;
            if (name == "h1") { cw = FontWeightValues.Bold; sz = 20; } // pt, mirrors HeadingSize
            if (name == "h2") { cw = FontWeightValues.Bold; sz = 16; }
            if (name == "h3") { cw = FontWeightValues.Bold; sz = 14; }

            bool childInLink = inLink || name == "a";
            if (name == "a")
            {
                var href = child.GetAttributeValue("href", "");
                if (!string.IsNullOrEmpty(href)) cu = href;
            }

            ApplyInlineStyle(child.GetAttributeValue("style", ""), ref cw, ref cs, ref cc, ref sz, ref cbg, ref cfam, ref cunder, ref cstrike);

            // Links stay visually distinct (blue) regardless of the site's own inline color.
            if (childInLink) cc = Colors.Blue;

            if (name == "#text")
            {
                string text = HtmlEntity.DeEntitize(child.InnerText);
                if (pre)
                {
                    // Preformatted: whitespace and newlines are content (code blocks); default to a
                    // monospace face when nothing more specific was inherited.
                    if (text.Length > 0)
                        p.Inlines.Add(new Run { Text = text.Replace("\r\n", "\n").Replace('\r', '\n'), FontWeight = NormalizeWeight(cw), FontStyle = cs, Foreground = cc, FontSize = sz, NavigateUri = cu, Background = cbg, FontFamily = cfam ?? "Consolas", TextDecorations = MakeDecorations(cunder, cstrike) });
                    return;
                }
                if (string.IsNullOrWhiteSpace(text))
                {
                    if (p.Inlines.Count > 0 && p.Inlines[^1] is Run last && last.Text != null &&
                        !last.Text.EndsWith(" ") && !last.Text.EndsWith("\n"))
                        p.Inlines.Add(new Run { Text = " " });
                    return;
                }
                p.Inlines.Add(new Run { Text = CollapseWhitespace(text), FontWeight = NormalizeWeight(cw), FontStyle = cs, Foreground = cc, FontSize = sz, NavigateUri = cu, Background = cbg, FontFamily = cfam, TextDecorations = MakeDecorations(cunder, cstrike) });
            }
            else if (name == "img")
            {
                var (bytes, w, h, alt) = LoadImage(child);
                if (bytes != null)
                {
                    var im = new InlineImage { Width = w, Height = h, AltText = alt };
                    im.SetImageData(bytes, ImageMime.Detect(bytes));
                    p.Inlines.Add(im);
                }
            }
            else
            {
                ParseInlines(child, p, cw, cs, cc, cu, sz, childInLink, cbg, cfam, cunder, cstrike, pre);
            }
        }
    }

    private static TextDecorationFlags MakeDecorations(bool underline, bool strike)
    {
        var d = TextDecorationFlags.None;
        if (underline) d |= TextDecorationFlags.Underline;
        if (strike) d |= TextDecorationFlags.Strikethrough;
        return d;
    }

    private static void ApplyInlineStyle(string styleAttr, ref FontWeight weight, ref FontStyle style, ref Color? color, ref double size, ref Color? background, ref string? family, ref bool underline, ref bool strike)
    {
        if (string.IsNullOrEmpty(styleAttr)) return;
        string s = styleAttr.ToLowerInvariant();

        if (s.Contains("font-weight"))
        {
            if (s.Contains("bold") || s.Contains(":600") || s.Contains(": 600") || s.Contains(":700") || s.Contains(": 700") || s.Contains(":800") || s.Contains(":900"))
                weight = FontWeightValues.Bold;
        }
        if (s.Contains("font-style:italic") || s.Contains("font-style: italic")) style = FontStyle.Italic;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "text-decoration[^;]*underline")) underline = true;
        if (System.Text.RegularExpressions.Regex.IsMatch(s, "text-decoration[^;]*line-through")) strike = true;

        var m = System.Text.RegularExpressions.Regex.Match(s, "(?<!background-)color\\s*:\\s*([^;]+)");
        if (m.Success)
        {
            var col = ParseCssColor(m.Groups[1].Value.Trim());
            if (col != null) color = col;
        }

        var bm = System.Text.RegularExpressions.Regex.Match(s, "background(?:-color)?\\s*:\\s*([^;]+)");
        if (bm.Success)
        {
            var col = ParseCssColor(bm.Groups[1].Value.Trim());
            if (col != null) background = col;
        }

        var famMatch = System.Text.RegularExpressions.Regex.Match(styleAttr, "font-family\\s*:\\s*([^;]+)",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (famMatch.Success)
        {
            string fam = famMatch.Groups[1].Value.Split(',')[0].Trim().Trim('\'', '"').Trim();
            if (!string.IsNullOrEmpty(fam)) family = fam;
        }

        // Accept px and pt; the model stores pt, so pt passes through and px converts px->pt (횞0.75).
        var fm = System.Text.RegularExpressions.Regex.Match(s, "font-size\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)\\s*(px|pt)?");
        if (fm.Success && double.TryParse(fm.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out double val) && val > 0)
            size = fm.Groups[2].Value == "pt" ? val : val * 72.0 / 96.0;
    }

    // Reads CSS line-height into the paragraph: "150%" or a unitless multiplier ("1.5") map to the
    // proportional LineSpacing; "24px" maps to the absolute LineHeight. "normal"/absent = leave unset.
    // The inverse of ToHtml's line-height emission, closing the round-trip (line spacing used to be
    // dropped entirely on HTML export/import).
    private static void ApplyLineHeightStyle(HtmlNode node, Paragraph p)
    {
        var v = ReadStyleValue(node, "line-height")?.Trim();
        if (string.IsNullOrEmpty(v) || v.Equals("normal", StringComparison.OrdinalIgnoreCase)) return;
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        const System.Globalization.NumberStyles fl = System.Globalization.NumberStyles.Float;
        if (v.EndsWith("%", StringComparison.Ordinal)
            && double.TryParse(v[..^1], fl, inv, out var pct) && pct > 0)
            p.LineSpacing = pct / 100.0;
        else if (v.EndsWith("px", StringComparison.OrdinalIgnoreCase)
            && double.TryParse(v[..^2], fl, inv, out var px) && px > 0)
            p.LineHeight = px;
        else if (double.TryParse(v, fl, inv, out var mult) && mult > 0)
            p.LineSpacing = mult; // unitless multiplier
    }

    private static double ReadIndentPx(HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "").ToLowerInvariant();
        var m = System.Text.RegularExpressions.Regex.Match(style, "margin-left\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)\\s*(px|pt)?");
        if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(style, "padding-left\\s*:\\s*([0-9]+(?:\\.[0-9]+)?)\\s*(px|pt)?");
        if (m.Success && double.TryParse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
            return m.Groups[2].Value == "pt" ? v * 96.0 / 72.0 : v;
        return 0;
    }

    // A cell's vertical alignment from the valign attribute or CSS vertical-align.
    private static CellVerticalAlignment ReadCellVAlign(HtmlNode td)
    {
        string v = td.GetAttributeValue("valign", "").ToLowerInvariant();
        if (v.Length == 0) v = ReadStyleValue(td, "vertical-align")?.ToLowerInvariant() ?? "";
        return v switch
        {
            "middle" or "center" => CellVerticalAlignment.Center,
            "bottom" => CellVerticalAlignment.Bottom,
            _ => CellVerticalAlignment.Top,
        };
    }

    // Background color from a node's style="background[-color]:..." or legacy bgcolor="..." attr.
    private static Color? ReadBackground(HtmlNode node)
    {
        var style = node.GetAttributeValue("style", "");
        if (!string.IsNullOrEmpty(style))
        {
            var m = System.Text.RegularExpressions.Regex.Match(style.ToLowerInvariant(), "background(?:-color)?\\s*:\\s*([^;]+)");
            if (m.Success)
            {
                var b = ParseCssColor(m.Groups[1].Value.Trim());
                if (b != null) return b;
            }
        }
        var bg = node.GetAttributeValue("bgcolor", "");
        return string.IsNullOrEmpty(bg) ? null : ParseCssColor(bg.Trim());
    }

    // CSS color value -> Color (hex / rgb() / named), or null. Delegates to the shared parser.
    private static Color? ParseCssColor(string value) => ColorUtil.Parse(value);

    private static string CssListStyle(ListKind kind, ListMarkerStyle marker) => marker switch
    {
        ListMarkerStyle.Circle => "circle",
        ListMarkerStyle.Square => "square",
        ListMarkerStyle.LowerAlpha => "lower-alpha",
        ListMarkerStyle.UpperAlpha => "upper-alpha",
        ListMarkerStyle.LowerRoman => "lower-roman",
        _ => kind == ListKind.Ordered ? "decimal" : "disc",
    };

    private static ListMarkerStyle ListMarkerFromCss(string? cssValue) => (cssValue ?? "").Trim().ToLowerInvariant() switch
    {
        "circle" => ListMarkerStyle.Circle,
        "square" => ListMarkerStyle.Square,
        "lower-alpha" or "lower-latin" => ListMarkerStyle.LowerAlpha,
        "upper-alpha" or "upper-latin" => ListMarkerStyle.UpperAlpha,
        "lower-roman" => ListMarkerStyle.LowerRoman,
        _ => ListMarkerStyle.Default,
    };

    /// <summary>Serializes <paramref name="doc"/> to an HTML string.</summary>
    public static string ToHtml(FlowDocument doc)
    {
        var sb = new StringBuilder();
        var listStack = new List<ListKind>();

        void CloseOne()
        {
            sb.Append(listStack[^1] == ListKind.Ordered ? "</ol>\n" : "</ul>\n");
            listStack.RemoveAt(listStack.Count - 1);
        }
        void CloseAll() { while (listStack.Count > 0) CloseOne(); }
        void SyncList(ListKind kind, ListMarkerStyle marker, int level)
        {
            while (listStack.Count > level + 1) CloseOne();
            if (listStack.Count == level + 1 && listStack[^1] != kind) CloseOne();
            while (listStack.Count < level + 1)
            {
                string lst = CssListStyle(kind, marker);
                sb.Append(kind == ListKind.Ordered ? $"<ol style=\"list-style-type:{lst}\">\n" : $"<ul style=\"list-style-type:{lst}\">\n");
                listStack.Add(kind);
            }
        }

        foreach (var block in doc.Blocks)
        {
            if (block is Paragraph p)
            {
                if (p.IsListItem) SyncList(p.ListType, p.ListMarker, p.ListLevel);
                else CloseAll();

                string tag = p.IsListItem ? "li"
                    : p.IsQuote ? "blockquote"
                    : (p.HeadingLevel >= 1 && p.HeadingLevel <= 6 ? $"h{p.HeadingLevel}" : "p");
                string align = p.TextAlignment switch { TextAlignment.Center => "center", TextAlignment.Right => "right", TextAlignment.Justify => "justify", _ => "left" };
                string pStyle = $"text-align:{align};";
                if (p.Background is { } pbg) pStyle += $"background-color:{ColorUtil.ToCss(pbg)};";
                if (p.Indent > 0) pStyle += $"margin-left:{p.Indent.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px;";
                // Line spacing round-trips as CSS line-height (% for proportional, px for absolute).
                if (!double.IsNaN(p.LineSpacing) && p.LineSpacing > 0)
                    pStyle += $"line-height:{(p.LineSpacing * 100).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}%;";
                else if (!double.IsNaN(p.LineHeight) && p.LineHeight > 0)
                    pStyle += $"line-height:{p.LineHeight.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}px;";
                sb.Append($"<{tag} style=\"{pStyle}\">");
                foreach (var inline in p.Inlines) EmitInline(sb, inline);
                sb.Append($"</{tag}>\n");
            }
            else if (block is DividerBlock)
            {
                CloseAll();
                sb.Append("<hr/>\n");
            }
            else if (block is TableBlock tb)
            {
                CloseAll();
                EmitTable(sb, tb);
            }
            else if (block is ImageBlock ib && (ib.RawBytes != null || ib.Image != null))
            {
                CloseAll();
                sb.Append($"<p>{ImgTag(ib.RawBytes, ib.MimeType, ib.RawBytes == null ? ib.Image : null, ib.Width, ib.Height, ib.AltText)}</p>\n");
            }
        }
        CloseAll();
        return sb.ToString();
    }

    // Emits a table as an HTML <table>. Shared by block tables and inline tables.
    private static void EmitTable(StringBuilder sb, TableBlock tb)
    {
        sb.Append("<table border=\"1\" style=\"border-collapse:collapse; width:100%;\">\n");
        if (tb.ColumnWidths.Count > 0)
        {
            sb.Append("<colgroup>");
            for (int c = 0; c < tb.Columns; c++)
            {
                double cw = c < tb.ColumnWidths.Count ? tb.ColumnWidths[c] : 100;
                sb.Append($"<col style=\"width:{(int)System.Math.Max(1, cw)}px\"/>");
            }
            sb.Append("</colgroup>\n");
        }
        for (int r = 0; r < tb.Rows; r++)
        {
            sb.Append("<tr>\n");
            for (int c = 0; c < tb.Columns; c++)
            {
                if (tb.IsCovered(r, c)) continue;
                var cell = tb.Cells[r][c];
                var (cs, rs) = tb.SpanOf(r, c);
                var span = (cs > 1 ? $" colspan=\"{cs}\"" : "") + (rs > 1 ? $" rowspan=\"{rs}\"" : "");
                if (cell.VerticalAlignment != CellVerticalAlignment.Top)
                    span += $" valign=\"{(cell.VerticalAlignment == CellVerticalAlignment.Center ? "middle" : "bottom")}\"";
                if (cell.Background is { } cbg)
                    sb.Append($"<td{span} style=\"background-color:{ColorUtil.ToCss(cbg)}\">");
                else
                    sb.Append($"<td{span}>");
                bool firstCellPara = true;
                foreach (var cblk in cell.Blocks)
                {
                    if (cblk is Paragraph cpara)
                    {
                        if (!firstCellPara) sb.Append("<br>");
                        firstCellPara = false;
                        foreach (var inline in cpara.Inlines) EmitInline(sb, inline);
                    }
                    else if (cblk is ImageBlock cib && (cib.RawBytes != null || cib.Image != null))
                        sb.Append(ImgTag(cib.RawBytes, cib.MimeType, cib.RawBytes == null ? cib.Image : null, cib.Width, cib.Height, cib.AltText));
                    else if (cblk is TableBlock nt)
                        EmitTable(sb, nt);
                    else if (cblk is DividerBlock)
                        sb.Append("<hr/>");
                }
                sb.Append("</td>\n");
            }
            sb.Append("</tr>\n");
        }
        sb.Append("</table>\n");
    }

    private static void EmitInline(StringBuilder sb, Inline inline)
    {
        if (inline is InlineImage im && (im.RawBytes != null || im.Image != null))
        {
            sb.Append(ImgTag(im.RawBytes, im.MimeType, im.RawBytes == null ? im.Image : null, im.Width, im.Height, im.AltText));
            return;
        }
        if (inline is InlineTable itbl)
        {
            EmitTable(sb, itbl.Table);
            return;
        }
        if (inline is not Run r || r.Text == null) return;

        string t = HtmlEntity.Entitize(r.Text);
        // Soft line breaks (Shift+Enter, stored as '\n' in run text) must survive as <br/> — a literal
        // newline collapses to a space in HTML, silently losing the break on export/copy. The parser's
        // inverse (ParseInlines) already turns <br> back into a "\n" run, so this closes the round-trip.
        t = t.Replace("\n", "<br/>");

        var styles = new List<string>();
        if (!string.IsNullOrEmpty(r.FontFamily)) styles.Add($"font-family:'{AttrEscape(r.FontFamily).Replace("'", "")}'");
        if (r.FontSize > 0 && System.Math.Abs(r.FontSize - 10) > 0.01)
            styles.Add($"font-size:{r.FontSize.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}pt");
        if (r.Foreground is { } fg) styles.Add($"color:{ColorUtil.ToCss(fg)}");
        if (r.Background is { } bg) styles.Add($"background-color:{ColorUtil.ToCss(bg)}");

        if (styles.Count > 0) t = $"<span style=\"{string.Join(";", styles)}\">{t}</span>";
        // Underline/strikethrough as <u>/<s> TAGS, not CSS text-decoration: clipboard importers
        // (Word/HWP) reliably honour the tags but routinely drop CSS text-decoration.
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Underline)) t = $"<u>{t}</u>";
        if (r.TextDecorations.HasFlag(TextDecorationFlags.Strikethrough)) t = $"<s>{t}</s>";
        if (r.FontWeight.IsBold()) t = $"<b>{t}</b>";
        if (r.FontStyle == FontStyle.Italic) t = $"<i>{t}</i>";
        if (!string.IsNullOrEmpty(r.NavigateUri)) t = $"<a href=\"{AttrEscape(r.NavigateUri)}\">{t}</a>";
        sb.Append(t);
    }

    private static string AttrEscape(string s) =>
        s.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    // Emits a data: URI <img>. RawBytes (with their MIME type) are used verbatim when present;
    // a bitmap set without bytes is PNG-encoded. `alt` round-trips the accessibility description.
    private static string ImgTag(byte[]? raw, string? mime, Microsoft.Graphics.Canvas.CanvasBitmap? bmp, double w, double h, string? alt = null)
    {
        string b64, m;
        if (raw != null)
        {
            b64 = System.Convert.ToBase64String(raw);
            m = mime ?? "image/png";
        }
        else if (bmp != null)
        {
            var encoded = ImageEncoder.ToPngBytes(bmp);
            if (encoded == null) return "";
            b64 = System.Convert.ToBase64String(encoded);
            m = "image/png";
        }
        else return "";
        string size = "";
        if (!double.IsNaN(w) && w > 0) size += $" width=\"{(int)w}\"";
        if (!double.IsNaN(h) && h > 0) size += $" height=\"{(int)h}\"";
        if (!string.IsNullOrEmpty(alt)) size += $" alt=\"{AttrEscape(alt)}\"";
        return $"<img src=\"data:{m};base64,{b64}\"{size}/>";
    }
}
