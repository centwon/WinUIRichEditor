using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Windows.Storage.Streams;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>Decodes image bytes to device-bound <see cref="CanvasBitmap"/>s off the render path.
/// A Win2D bitmap is async + device-bound, unlike Avalonia's synchronous device-free <c>Bitmap</c>,
/// so the model keeps the encoded <c>RawBytes</c> and this cache holds the decoded GPU bitmap.
/// <see cref="Get"/> returns null until the decode finishes, then fires <see cref="OnReady"/> so the
/// control can repaint. Keyed by the bytes' CONTENT hash: pasting the same picture twice (distinct
/// arrays, equal content) shares one decode and one GPU bitmap — array identity only deduplicated
/// <c>Clone()</c>-shared arrays (undo snapshots). The hash is computed once per array and memoized by
/// array identity (<see cref="System.Runtime.CompilerServices.ConditionalWeakTable{TKey,TValue}"/>),
/// so the per-draw <see cref="Get"/>
/// never rehashes. It never mutates the model's <c>RawBytes</c> (the <c>Image</c> setter would
/// discard them).</summary>
internal sealed class ImageCache
{
    private readonly Dictionary<object, CanvasBitmap?> _decoded = new(); // value null = decode failed
    private readonly HashSet<object> _inflight = new();
    // Array identity -> content-hash key, computed once per array (weak: dropped with the array).
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<byte[], string> _hashOf = new();

    /// <summary>Raised on the UI thread when a decode completes (the control re-invalidates).</summary>
    public event Action? OnReady;

    // The cache key for an image: content hash of its bytes (memoized per array), or the element
    // itself when there are no bytes. Callers passing byte[] keys (Invalidate/Prune) map through this
    // too, so all key spaces agree.
    private object KeyFor(byte[]? rawBytes, object element)
    {
        if (rawBytes == null) return element;
        if (!_hashOf.TryGetValue(rawBytes, out var h))
        {
            h = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(rawBytes));
            _hashOf.Add(rawBytes, h);
        }
        return h;
    }

    /// <summary>Returns the decoded bitmap for an image element, or null while decoding/failed.
    /// <paramref name="already"/> is an in-model bitmap (the rare "set a bitmap directly" path).</summary>
    public CanvasBitmap? Get(ICanvasResourceCreator device, object key, byte[]? rawBytes, CanvasBitmap? already)
    {
        if (already != null) return already;
        object k = KeyFor(rawBytes, key);
        if (_decoded.TryGetValue(k, out var bmp)) return bmp;
        if (rawBytes != null && _inflight.Add(k))
            _ = DecodeAsync(device, k, rawBytes);
        return null;
    }

    private async Task DecodeAsync(ICanvasResourceCreator device, object key, byte[] rawBytes)
    {
        CanvasBitmap? result = null;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(rawBytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            result = await CanvasBitmap.LoadAsync(device, stream);
        }
        // undecodable: cache the failure so we don't retry every frame
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); result = null; }
        // If the key was invalidated/cleared/pruned while decoding, don't resurrect a stale entry —
        // dispose the freshly decoded bitmap instead of publishing it.
        if (!_inflight.Remove(key)) { result?.Dispose(); return; }
        _decoded[key] = result;
        OnReady?.Invoke();
    }

    /// <summary>Drops the cached bitmap for one element so its (changed) bytes are re-decoded.
    /// The decoded <see cref="CanvasBitmap"/> is a device-bound GPU resource, so dispose it
    /// explicitly rather than waiting for the finalizer. A byte[] key maps to its content hash;
    /// content-identical siblings simply re-decode on demand.</summary>
    public void Invalidate(object key)
    {
        object k = key is byte[] b ? KeyFor(b, key) : key;
        if (_decoded.TryGetValue(k, out var bmp)) bmp?.Dispose();
        _decoded.Remove(k);
        _inflight.Remove(k);
        Unretire(k);
    }

    // Entries the document no longer references but that are kept decoded anyway, oldest first, so undoing
    // the edit that removed a picture finds it warm. Bounded by the retainBytes each Prune passes.
    //
    // Why this exists (measured 2026-09-16, MemBaseline images mode): Prune used to run only on a document
    // swap, so a picture removed by EDITING — Delete, Backspace, cut, paste over it — stayed decoded until the
    // next swap. Six 4000x3000 photos inserted and deleted three times held 18 full-size bitmaps with none
    // in the document: Private bytes 112 -> 1404 MB. Disposing on the spot instead would re-decode (placeholder
    // flash) on every Ctrl+Z of a delete, the reason a swap prunes rather than clears.
    private readonly LinkedList<(object key, long bytes)> _retired = new();
    private readonly Dictionary<object, LinkedListNode<(object key, long bytes)>> _retiredIndex = new();
    private long _retiredBytes;

    /// <summary>True when nothing is decoded or decoding — the edit-path sweep skips its document walk.</summary>
    public bool IsEmpty => _decoded.Count == 0 && _inflight.Count == 0;

    /// <summary>Drops cache entries whose key is not in <paramref name="live"/> — the set of RawBytes the
    /// current document references. Entries leaving the document are kept, oldest disposed first, while their
    /// decoded pixels total at most <paramref name="retainBytes"/>; 0 disposes every one of them (a genuine
    /// document swap, a lost device). An entry back in the document (undo) stops counting against the budget.</summary>
    public void Prune(HashSet<object> live, long retainBytes = 0)
    {
        // Map the live byte arrays to the cache's content-hash key space first.
        var liveKeys = new HashSet<object>();
        foreach (var k in live) liveKeys.Add(k is byte[] b ? KeyFor(b, k) : k);

        foreach (var (k, bmp) in _decoded)
        {
            if (liveKeys.Contains(k)) Unretire(k);
            else if (!_retiredIndex.ContainsKey(k))
            {
                long bytes = PixelBytes(bmp);
                _retiredIndex[k] = _retired.AddLast((k, bytes));
                _retiredBytes += bytes;
            }
        }
        while (_retired.First is { } oldest && (retainBytes <= 0 || _retiredBytes > retainBytes))
        {
            var k = oldest.Value.key;
            Unretire(k);
            if (_decoded.TryGetValue(k, out var bmp)) bmp?.Dispose();
            _decoded.Remove(k);
        }
        _inflight.RemoveWhere(k => !liveKeys.Contains(k)); // their decodes self-dispose on completion
    }

    private void Unretire(object key)
    {
        if (!_retiredIndex.Remove(key, out var node)) return;
        _retiredBytes -= node.Value.bytes;
        _retired.Remove(node);
    }

    // What a decoded bitmap holds: 4 bytes per pixel (Win2D decodes to BGRA8). A failed decode holds nothing.
    private static long PixelBytes(CanvasBitmap? bmp)
    {
        if (bmp == null) return 0;
        var px = bmp.SizeInPixels;
        return (long)px.Width * px.Height * 4;
    }

    // ---- test seams: decodes are async and device-bound, so tests place entries directly ----
    internal int DecodedCount => _decoded.Count;
    internal long RetiredBytes => _retiredBytes;
    internal bool IsCached(byte[] rawBytes) => _decoded.ContainsKey(KeyFor(rawBytes, rawBytes));
    internal void Seed(byte[] rawBytes, CanvasBitmap? bitmap) => _decoded[KeyFor(rawBytes, rawBytes)] = bitmap;

    // No Clear(): the document-swap path deliberately uses Prune(liveKeys) instead, so undo/redo
    // snapshots that share RawBytes keep their bitmaps warm (a Clear there caused a placeholder flash
    // and a full re-decode on every Ctrl+Z). Prune with an empty set (retainBytes 0) is a Clear.
}
