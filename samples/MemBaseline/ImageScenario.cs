using System;
using System.Collections;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Storage.Streams;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;

namespace MemBaseline;

/// <summary>
/// MEMTEST_MODE=images — what pictures cost, and whether that cost leaves when the pictures do.
///
/// Every earlier mode measured TEXT. A decoded picture is a GPU texture, which Private bytes does not see
/// on a discrete GPU, so each log line carries the process's video-memory usage too (<see cref="GpuMemory"/>).
///
/// Sequence (one log line per step, to %TEMP%\membaseline_images.txt):
///   empty                 editor with an empty document
///   generated             every cycle's JPEGs built up front (the baseline for the cycles)
///   cycle k: inserted     MEMTEST_COUNT fresh photos inserted inline (small on screen), decodes finished
///   cycle k: edit-del     all content removed by an EDIT (select all + delete), not a document swap
///   swapped               editor.Clear() — document swap (prunes the image cache) + undo history cleared
///   later                 the same, ~10 s on (native releases lag)
///
/// Each cycle uses NEW pixel content, so the content-hash cache can't dedupe across cycles: if deleting by
/// edit doesn't release bitmaps, GPU usage ratchets once per cycle. The 8/15 lesson this exists for: a
/// cycle that ends in a document swap tests the swap, not the editing.
///
/// Knobs: MEMTEST_COUNT (photos per cycle, default 6), MEMTEST_IMGPX ("4000x3000", the pixel size of each
/// generated JPEG), MEMTEST_CYCLES (default 3). Run twice with different MEMTEST_IMGPX to see whether the
/// texture cost follows the SOURCE pixels (full-res decode) or the on-screen size.
///
/// Internals (select all, delete, the decoded-bitmap count) are reached by reflection: this is a measuring
/// harness, and the library has no public "delete the selection" or cache-count API to call instead.
/// </summary>
internal sealed class ImageScenario
{
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "membaseline_images.txt");
    private const BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly RichEditor _editor;
    private readonly int _count, _cycles, _pxW, _pxH;

    public ImageScenario(RichEditor editor)
    {
        _editor = editor;
        _count = EnvInt("MEMTEST_COUNT", 6);
        _cycles = EnvInt("MEMTEST_CYCLES", 3);
        (_pxW, _pxH) = ParsePx(Environment.GetEnvironmentVariable("MEMTEST_IMGPX"), 4000, 3000);
    }

    public async Task RunAsync()
    {
        try
        {
            File.AppendAllText(LogPath,
                $"\n=== images  count={_count} px={_pxW}x{_pxH} cycles={_cycles}  {DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
                $"    adapters: {GpuMemory.Describe()}\n" +
                $"    RGBA of one full-res decode = {_pxW * 4L * _pxH / 1048576.0:N1}MB, x{_count} = {_pxW * 4L * _pxH * _count / 1048576.0:N1}MB\n");

            await Settle();
            Log("empty");

            // Every cycle's photos are generated BEFORE the baseline: the generator draws into full-size
            // render targets, and the allocator keeps those pages after dispose — generating per cycle put
            // that retention into the very columns being measured. The JPEG bytes stay in the managed column.
            var photos = new byte[_cycles][][];
            for (int c = 0; c < _cycles; c++)
            {
                photos[c] = new byte[_count][];
                for (int i = 0; i < _count; i++) photos[c][i] = await MakePhotoAsync(seed: c * 1000 + i);
            }
            await Settle();
            await Settle();
            Log("generated", $"jpegTotal={Sum(photos) / 1048576.0:N1}MB (held until inserted)");

            for (int c = 0; c < _cycles; c++)
            {
                int decodedBefore = DecodedCount();
                long cycleBytes = 0;
                foreach (var jpeg in photos[c]) { cycleBytes += jpeg.Length; _editor.InsertInlineImage(jpeg, "image/jpeg"); }
                photos[c] = [];
                await WaitForDecodes(decodedBefore + _count);
                await Settle();
                Log($"c{c} inserted", $"inDoc={ImagesInDocument()} jpeg={cycleBytes / 1048576.0:N1}MB");

                Invoke("SelectAll");
                Invoke("PushUndo", (string?)null);
                Invoke("DeleteSelection");
                Invoke("AfterEdit");
                await Settle();
                Log($"c{c} edit-del", $"inDoc={ImagesInDocument()}");
            }

            _editor.Clear(); // LoadDocument: swaps the document (prunes the image cache) AND clears undo history
            await Settle();
            Log("swapped");

            // Native releases (texture destruction, heap decommit) lag the swap; a second reading shows how far.
            await Task.Delay(8000);
            await Settle();
            Log("later");
            File.AppendAllText(LogPath, "DONE\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(LogPath, $"FAILED: {ex}\n");
        }
    }

    // A photo-like JPEG: smooth gradients plus many soft shapes, so the encoded size lands in the range of a
    // real camera picture instead of compressing to nothing. The seed makes each cycle's pixels distinct.
    private async Task<byte[]> MakePhotoAsync(int seed)
    {
        var rnd = new Random(seed);
        var device = CanvasDevice.GetSharedDevice();
        using var rt = new CanvasRenderTarget(device, _pxW, _pxH, 96);
        using (var ds = rt.CreateDrawingSession())
        {
            ds.Clear(Windows.UI.Color.FromArgb(255, (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)));
            for (int k = 0; k < 400; k++)
            {
                var col = Windows.UI.Color.FromArgb((byte)rnd.Next(60, 200), (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256));
                ds.FillEllipse(rnd.Next(_pxW), rnd.Next(_pxH), rnd.Next(10, _pxW / 6), rnd.Next(10, _pxH / 6), col);
            }
            for (int k = 0; k < 3000; k++)
                ds.DrawLine(rnd.Next(_pxW), rnd.Next(_pxH), rnd.Next(_pxW), rnd.Next(_pxH),
                    Windows.UI.Color.FromArgb(90, (byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256)), 1);
        }
        using var stream = new InMemoryRandomAccessStream();
        await rt.SaveAsync(stream, CanvasBitmapFileFormat.Jpeg, 0.9f);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }

    // Decodes start only when an image is DRAWN, and finish asynchronously. Wait until the cache holds the
    // count read just before inserting plus this cycle's photos — so an earlier cycle's entries, whether or
    // not they were released, can't satisfy the wait early.
    private async Task WaitForDecodes(int target)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < TimeSpan.FromSeconds(60))
        {
            if (InflightCount() == 0 && DecodedCount() >= target) return;
            await Task.Delay(250);
        }
        File.AppendAllText(LogPath, $"    (timeout waiting for decodes: decoded={DecodedCount()} target={target} inflight={InflightCount()})\n");
    }

    private int ImagesInDocument()
    {
        int n = 0;
        if (_editor.Document != null)
            foreach (var b in _editor.Document.Blocks)
                if (b is Paragraph p)
                    foreach (var inl in p.Inlines)
                        if (inl is InlineImage) n++;
        return n;
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
            $"{step,-12} Priv={p.PrivateMemorySize64 / 1048576.0,7:N1}MB  WS={p.WorkingSet64 / 1048576.0,7:N1}MB  " +
            $"managed={GC.GetTotalMemory(false) / 1048576.0,6:N1}MB  gpu[{GpuMemory.QueryText()}]  " +
            $"decoded={DecodedCount(),2}  {extra}\n");
    }

    private object? ImageCache => typeof(RichEditor).GetField("_images", Priv)!.GetValue(_editor);
    private int DecodedCount() => CountOf("_decoded");
    private int InflightCount() => CountOf("_inflight");
    // Dictionary is an ICollection but HashSet is not, so read the Count property itself.
    private int CountOf(string field)
    {
        var value = ImageCache?.GetType().GetField(field, Priv)?.GetValue(ImageCache);
        return value?.GetType().GetProperty("Count")?.GetValue(value) is int n ? n : -1;
    }

    private static long Sum(byte[][][] photos)
    {
        long n = 0;
        foreach (var cycle in photos) foreach (var p in cycle) n += p.Length;
        return n;
    }

    private void Invoke(string method, params object?[] args)
    {
        var m = typeof(RichEditor).GetMethod(method, Priv, args.Length == 0 ? Type.EmptyTypes : [typeof(string)])
            ?? throw new MissingMethodException(nameof(RichEditor), method);
        m.Invoke(_editor, args);
    }

    private static int EnvInt(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static (int, int) ParsePx(string? s, int w, int h)
    {
        var parts = s?.Split('x', 'X');
        return parts is { Length: 2 } && int.TryParse(parts[0], out var pw) && int.TryParse(parts[1], out var ph) ? (pw, ph) : (w, h);
    }
}
