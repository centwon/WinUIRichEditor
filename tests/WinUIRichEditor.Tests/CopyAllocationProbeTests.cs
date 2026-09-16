using System;
using System.Reflection;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>What copying a picture allocates, stage by stage. A measurement harness, not an assertion suite:
/// skipped unless <c>RICHEDITOR_PERF=1</c> (see <see cref="PerfProbeTests"/>).
/// <para>Copy puts three renderings of the selection on the clipboard — CF_HTML, RTF and the internal clone —
/// and the HTML and RTF ones carry the picture's bytes as text: base64 (4 chars per 3 bytes) and hex (2 chars
/// per byte), at 2 bytes per UTF-16 char. Every intermediate string of that size is a large-object-heap
/// allocation. The OS clipboard itself is not called (it is shared with the desktop and flakes under test);
/// the probe stops at the strings handed to it.</para>
/// <para>Run: <c>$env:RICHEDITOR_PERF=1; dotnet test tests\WinUIRichEditor.Tests --filter CopyAllocationProbe --logger "console;verbosity=detailed"</c></para></summary>
[Collection(UiTests.Collection)]
public class CopyAllocationProbeTests(ITestOutputHelper output)
{
    // Also appended to %TEMP%\richeditor_copy_probe.txt: runner output capture differs by logger.
    private void Log(string line) { output.WriteLine(line); System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "richeditor_copy_probe.txt"), line + "\n"); }

    private static bool Enabled => Environment.GetEnvironmentVariable("RICHEDITOR_PERF") == "1";
    private const int Mb = 1024 * 1024;

    private static FlowDocument DocWithPicture(int bytes)
    {
        var raw = new byte[bytes];
        new Random(7).NextBytes(raw);
        raw[0] = 0xFF; raw[1] = 0xD8; raw[2] = 0xFF; // looks like a JPEG to the writers
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "before" } } });
        var img = new ImageBlock { Width = 400, Height = 300 };
        img.SetImageData(raw, "image/jpeg");
        doc.Blocks.Add(img);
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "after" } } });
        return doc;
    }

    private static long Measure(Action body)
    {
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        long before = GC.GetAllocatedBytesForCurrentThread();
        body();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    [Fact]
    public void CopyingAPicture_AllocationsByStage()
    {
        if (!Enabled) return;
        UiThread.Run(() =>
        {
            const int size = 10 * Mb;
            var ed = new RichEditor();
            var doc = DocWithPicture(size);
            var buildHtml = typeof(RichEditor).GetMethod("BuildSelectionHtml", BindingFlags.NonPublic | BindingFlags.Instance)!;

            string? html = null, cf = null, rtf = null;
            FlowDocument? clone = null;
            long aHtml = Measure(() => html = (string)buildHtml.Invoke(ed, [doc])!);
            long aCf = Measure(() => cf = Windows.ApplicationModel.DataTransfer.HtmlFormatHelper.CreateHtmlFormat(html));
            long aRtf = Measure(() => rtf = RtfDocumentFormatter.Write(doc));
            long aClone = Measure(() => clone = doc.Clone());

            double Mib(long b) => b / (double)Mb;
            Log($"picture: {Mib(size):F1} MB");
            Log($"  selection HTML  alloc {Mib(aHtml),7:F1} MB  result {Mib(html!.Length * 2L),6:F1} MB  ({aHtml / (double)size:F1}x the picture)");
            Log($"  CF_HTML         alloc {Mib(aCf),7:F1} MB  result {Mib(cf!.Length * 2L),6:F1} MB  ({aCf / (double)size:F1}x)");
            Log($"  RTF             alloc {Mib(aRtf),7:F1} MB  result {Mib(rtf!.Length * 2L),6:F1} MB  ({aRtf / (double)size:F1}x)");
            Log($"  clone           alloc {Mib(aClone),7:F1} MB");
            long total = aHtml + aCf + aRtf + aClone;
            long liveAtHandOff = (html.Length + cf.Length + rtf.Length) * 2L;
            Log($"  TOTAL alloc {Mib(total):F1} MB ({total / (double)size:F1}x); strings alive when handed to the clipboard {Mib(liveAtHandOff):F1} MB");
            GC.KeepAlive(clone);
        });
    }
}
