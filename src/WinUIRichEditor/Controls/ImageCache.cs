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
/// array identity (<see cref="ConditionalWeakTable{TKey,TValue}"/>), so the per-draw <see cref="Get"/>
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
        catch { result = null; } // undecodable: cache the failure so we don't retry every frame
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
    }

    /// <summary>Drops (and disposes) cache entries whose key is not in <paramref name="live"/> — called
    /// with the set of RawBytes referenced by the current document, so a document swap frees the old
    /// document's bitmaps while undo/redo (whose snapshots share RawBytes) keeps its cache warm.</summary>
    public void Prune(HashSet<object> live)
    {
        // Map the live byte arrays to the cache's content-hash key space first.
        var liveKeys = new HashSet<object>();
        foreach (var k in live) liveKeys.Add(k is byte[] b ? KeyFor(b, k) : k);

        var stale = new List<object>();
        foreach (var k in _decoded.Keys) if (!liveKeys.Contains(k)) stale.Add(k);
        foreach (var k in stale)
        {
            if (_decoded.TryGetValue(k, out var bmp)) bmp?.Dispose();
            _decoded.Remove(k);
        }
        _inflight.RemoveWhere(k => !liveKeys.Contains(k)); // their decodes self-dispose on completion
    }

    /// <summary>Disposes every decoded bitmap and empties the cache (used on a wholesale document swap).</summary>
    public void Clear()
    {
        foreach (var bmp in _decoded.Values) bmp?.Dispose();
        _decoded.Clear();
        _inflight.Clear();
    }
}
