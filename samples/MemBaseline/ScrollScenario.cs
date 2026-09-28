using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Streams;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;

namespace MemBaseline;

/// <summary>
/// MEMTEST_MODE=scroll — what READING a long document leaves behind.
///
/// Every earlier mode measured a document that sat at the top of the window. The render path caches what it
/// DRAWS (the CanvasTextLayout per paragraph, the decoded bitmap per picture), and nothing scrolled off screen
/// was ever measured: a user who pages to the end of a long document has drawn all of it.
///
/// Sequence (one log line per step, to %TEMP%\membaseline_scroll.txt):
///   loaded     MEMTEST_COUNT paragraphs (+ a block photo every MEMTEST_IMGEVERY paragraphs), top of the window
///   scrolled   paged down to the end one viewport at a time, waiting for each page's decodes
///   top        scrolled back to the top
///   unloaded   the editor taken out of the visual tree (a hidden tab, a navigated-away page), still alive
///   reloaded   put back
/// Knobs: MEMTEST_COUNT (default 2000), MEMTEST_IMGEVERY (default 0 = no pictures), MEMTEST_IMGPX (default
/// 1600x1200, the JPEG's own pixel size; each is drawn 480 DIPs wide).
/// </summary>
internal sealed class ScrollScenario
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "membaseline_scroll.txt");
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly RichEditor _editor;
    private readonly Grid _host;
    private readonly int _paras, _imgEvery, _pxW, _pxH;

    public ScrollScenario(RichEditor editor, Grid host)
    {
        _editor = editor;
        _host = host;
        _paras = EnvInt("MEMTEST_COUNT", 2000);
        _imgEvery = EnvInt("MEMTEST_IMGEVERY", 0);
        (_pxW, _pxH) = ParsePx(Environment.GetEnvironmentVariable("MEMTEST_IMGPX"), 1600, 1200);
    }

    public async Task RunAsync()
    {
        try
        {
            File.AppendAllText(LogPath,
                $"\n=== scroll  paras={_paras} imgEvery={_imgEvery} px={_pxW}x{_pxH}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"    adapters: {GpuMemory.Describe()}\n");
            await Settle();
            Log("empty");

            var doc = HtmlDocumentFormatter.ParseHtml(BigHtml(_paras));
            if (_imgEvery > 0)
            {
                // Distinct pixels per picture, so the content-hash cache can't share one decode between them.
                int n = 0;
                for (int i = doc.Blocks.Count - 1; i > 0; i -= _imgEvery) n++;
                var photos = new byte[n][];
                for (int i = 0; i < n; i++) photos[i] = await MakePhotoAsync(i);
                int k = 0;
                for (int i = doc.Blocks.Count - 1; i > 0 && k < n; i -= _imgEvery)
                {
                    var img = new ImageBlock { Width = 480, Height = 480.0 * _pxH / _pxW };
                    img.SetImageData(photos[k++], "image/jpeg");
                    doc.Blocks.Insert(i, img);
                }
            }
            _editor.Document = doc;
            await Settle();
            await WaitForDecodes();
            Log("loaded");

            var scroll = (ScrollViewer)typeof(RichEditor).GetField("_scroll", Priv)!.GetValue(_editor)!;
            var sw = Stopwatch.StartNew();
            int pages = 0;
            while (scroll.VerticalOffset + scroll.ViewportHeight < scroll.ExtentHeight - 1 && pages < 100_000)
            {
                scroll.ChangeView(null, scroll.VerticalOffset + scroll.ViewportHeight * 0.9, null, disableAnimation: true);
                await Task.Delay(30);
                pages++;
                if (_imgEvery > 0) await WaitForDecodes();
            }
            await Settle();
            Log("scrolled", $"pages={pages} in {sw.Elapsed.TotalSeconds:N1}s");

            scroll.ChangeView(null, 0, null, disableAnimation: true);
            await Settle();
            Log("top");

            if (Environment.GetEnvironmentVariable("MEMTEST_DEVTRIM") == "1")
            {
                var tsw = Stopwatch.StartNew();
                CanvasDevice.GetSharedDevice().Trim();
                double ms = tsw.Elapsed.TotalMilliseconds;
                await Settle();
                Log("devtrim", $"Trim() took {ms:N2}ms");
            }

            _host.Children.Clear();
            await Settle();
            await Settle();
            Log("unloaded");

            _host.Children.Add(_editor);
            await Settle();
            await WaitForDecodes();
            Log("reloaded");
            File.AppendAllText(LogPath, "DONE\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"FAILED: {ex}\n");
        }
    }

    private static string BigHtml(int paras)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paras; i++)
            sb.Append("<p>문단 ").Append(i)
              .Append(" — <b>굵게</b> <i>기울임</i> <u>밑줄</u> <s>취소선</s> ")
              .Append("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod ")
              .Append("tempor incididunt ut labore et dolore magna aliqua. ")
              .Append("한글과 영문이 충분히 섞인 긴 문장으로 컨트롤 폭에서 자동 줄바꿈을 유발합니다.</p>");
        return sb.ToString();
    }

    private async Task<byte[]> MakePhotoAsync(int seed)
    {
        var rnd = new Random(seed);
        using var rt = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), _pxW, _pxH, 96);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
            for (int k = 0; k < 200; k++)
                ds.FillEllipse(rnd.Next(_pxW), rnd.Next(_pxH), rnd.Next(10, _pxW / 6), rnd.Next(10, _pxH / 6),
                    Windows.UI.Color.FromArgb((byte)rnd.Next(60, 200), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
        }
        using var stream = new InMemoryRandomAccessStream();
        await rt.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, 0.9f);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    private async Task WaitForDecodes()
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(30) && InflightCount() > 0) await Task.Delay(20);
    }

    private static async Task Settle()
    {
        await Task.Delay(1500);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        await Task.Delay(500);
    }

    private void Log(string step, string extra = "")
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        File.AppendAllText(LogPath,
            $"{step,-9} Priv={p.PrivateMemorySize64 / 1048576.0,7:N1}MB  WS={p.WorkingSet64 / 1048576.0,7:N1}MB  " +
            $"managed={GC.GetTotalMemory(false) / 1048576.0,6:N1}MB  gpu[{GpuMemory.QueryText()}]  " +
            $"layouts={LayoutCount(),5}  decoded={CountOf("_decoded"),4}  {extra}\n");
    }

    private int LayoutCount()
        => typeof(RichEditor).GetProperty("LayoutCacheCount", Priv)?.GetValue(_editor) is int n ? n : -1;

    private object? ImageCache => typeof(RichEditor).GetField("_images", Priv)!.GetValue(_editor);
    private int InflightCount() => CountOf("_inflight");
    private int CountOf(string field)
    {
        var value = ImageCache?.GetType().GetField(field, Priv)?.GetValue(ImageCache);
        return value?.GetType().GetProperty("Count")?.GetValue(value) is int n ? n : -1;
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v >= 0 ? v : fallback;

    private static (int, int) ParsePx(string? s, int w, int h)
    {
        var parts = s?.Split('x', 'X');
        return parts is { Length: 2 } && int.TryParse(parts[0], out var pw) && int.TryParse(parts[1], out var ph) ? (pw, ph) : (w, h);
    }
}
