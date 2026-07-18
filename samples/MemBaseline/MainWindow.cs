using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;

namespace MemBaseline;

/// <summary>
/// A deliberately minimal WinUI 3 host used to isolate the library's marginal memory cost.
/// MEMTEST_MODE selects what gets put in the window:
///   baseline  — empty window, library never instantiated (WinUI/Win2D baseline)
///   editor    — one empty RichEditor (fixed control + Win2D device cost)
///   editorbig — one RichEditor with a large parsed document (variable per-content cost)
///   reclaim   — load big doc, then clear it + GC, logging WS before/after (reclaim/leak check)
/// Results are read externally via Get-Process; reclaim mode also writes a small text log.
/// </summary>
public sealed class MainWindow : Window
{
    public MainWindow()
    {
        Title = "MemBaseline";
        string mode = (Environment.GetEnvironmentVariable("MEMTEST_MODE") ?? "baseline").ToLowerInvariant();

        var root = new Grid();
        Content = root;

        if (mode == "baseline")
        {
            root.Children.Add(new TextBlock { Text = "baseline — no editor" });
            return;
        }

        // Multi-instance: N empty RichEditors in the visual tree, each sized so it loads and
        // initializes its Win2D render path. Reveals whether the CanvasDevice is shared across
        // instances (small per-instance increment) or per-control (large increment).
        if (mode == "multi")
        {
            int n = int.TryParse(Environment.GetEnvironmentVariable("MEMTEST_COUNT"), out var c) ? c : 4;
            var stack = new StackPanel();
            var scroller = new ScrollViewer { Content = stack };
            root.Children.Add(scroller);
            for (int i = 0; i < n; i++)
            {
                var ed = new RichEditor { Height = 140 };
                ed.Document = new FlowDocument();
                stack.Children.Add(ed);
            }
            return;
        }

        var editor = new RichEditor();
        root.Children.Add(editor);

        switch (mode)
        {
            case "editor":
                editor.Document = new FlowDocument();
                break;
            case "editorbig":
                int paras = int.TryParse(Environment.GetEnvironmentVariable("MEMTEST_COUNT"), out var pc) ? pc : 200;
                editor.Document = HtmlDocumentFormatter.ParseHtml(BigHtml(paras));
                break;
            case "editorro":
                int parasRo = int.TryParse(Environment.GetEnvironmentVariable("MEMTEST_COUNT"), out var pr) ? pr : 200;
                editor.Document = HtmlDocumentFormatter.ParseHtml(BigHtml(parasRo));
                editor.IsReadOnly = true;
                break;
            case "editorfrag":
                int parasF = int.TryParse(Environment.GetEnvironmentVariable("MEMTEST_COUNT"), out var pf) ? pf : 1000;
                var fragDoc = HtmlDocumentFormatter.ParseHtml(FragHtml(parasF, 20));
                editor.Document = fragDoc;
                LogRunCount(fragDoc, parasF);
                break;
            case "reclaim":
                editor.Document = HtmlDocumentFormatter.ParseHtml(BigHtml(200));
                ScheduleReclaim(editor);
                break;
            default:
                editor.Document = new FlowDocument();
                break;
        }
    }

    // ~200 paragraphs of mixed Hangul/Latin with inline formatting → a genuinely large layout.
    private static string BigHtml(int paras)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paras; i++)
        {
            sb.Append("<p>문단 ").Append(i)
              .Append(" — <b>굵게</b> <i>기울임</i> <u>밑줄</u> <s>취소선</s> ")
              .Append("Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod ")
              .Append("tempor incididunt ut labore et dolore magna aliqua. ")
              .Append("한글과 영문이 충분히 섞인 긴 문장으로 컨트롤 폭에서 자동 줄바꿈을 유발합니다.</p>");
        }
        return sb.ToString();
    }

    // Fragmented HTML: each word in its own same-format <span> (and trailing space inside the span so all
    // runs share formatting), mimicking Word/Google-Docs paste. Without load coalescing this is M runs per
    // paragraph; with it, ~1.
    private static string FragHtml(int paras, int wordsPerPara)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < paras; i++)
        {
            sb.Append("<p>");
            for (int w = 0; w < wordsPerPara; w++)
                sb.Append("<span style=\"font-family:Segoe UI\">word").Append(w).Append(" </span>");
            sb.Append("</p>");
        }
        return sb.ToString();
    }

    private static void LogRunCount(FlowDocument doc, int paras)
    {
        int runs = 0;
        foreach (var b in doc.Blocks)
            if (b is Paragraph p)
                foreach (var inl in p.Inlines)
                    if (inl is Run) runs++;
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        double managed = GC.GetTotalMemory(true) / 1048576.0;
        var path = Path.Combine(Path.GetTempPath(), "membaseline_runcount.txt");
        File.AppendAllText(path, $"paras={paras}  totalRuns={runs}  runsPerPara={(double)runs / paras:N2}  managedHeap={managed:N1}MB\n");
    }

    private static void Log(string line)
    {
        var p = Process.GetCurrentProcess();
        p.Refresh();
        var path = Path.Combine(Path.GetTempPath(), "membaseline_reclaim.txt");
        File.AppendAllText(path,
            $"{line,-8} WS={p.WorkingSet64 / 1048576.0,6:N1}MB  Priv={p.PrivateMemorySize64 / 1048576.0,6:N1}MB  managedHeap={GC.GetTotalMemory(false) / 1048576.0,6:N1}MB\n");
    }

    // Repeated load->clear cycles. If "cleared" Priv ratchets up every cycle, it's a real leak;
    // if it plateaus, the unreturned memory is just allocator retention (reused, benign).
    private void ScheduleReclaim(RichEditor editor)
    {
        const int cycles = 6;
        var q = DispatcherQueue.GetForCurrentThread();
        var t = q.CreateTimer();
        t.Interval = TimeSpan.FromSeconds(2);
        t.IsRepeating = true;
        int phase = 0;
        t.Tick += (s, e) =>
        {
            int c = phase / 3, sub = phase % 3;
            if (c >= cycles) { t.Stop(); Log("DONE"); return; }
            switch (sub)
            {
                case 0:
                    editor.Document = HtmlDocumentFormatter.ParseHtml(BigHtml(200));
                    break;
                case 1:
                    Log($"loaded {c}");
                    editor.Document = new FlowDocument();
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    break;
                case 2:
                    GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                    Log($"cleared {c}");
                    break;
            }
            phase++;
        };
        t.Start();
    }
}
