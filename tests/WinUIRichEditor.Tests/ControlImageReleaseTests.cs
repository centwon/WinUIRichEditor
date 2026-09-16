using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Threading;
using Microsoft.Graphics.Canvas;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>Decoded pictures leave memory when an EDIT removes them, not only on a document swap
/// (2026-09-16). Measured before the fix with MemBaseline's images mode: six 4000x3000 photos inserted and
/// deleted three times held 18 full-size bitmaps with none left in the document — Private bytes 112 → 1404 MB.
/// <para>Removed pictures are kept up to <c>ImageRetainBytes</c> of pixels so undoing the delete (and redoing
/// it) doesn't re-decode; beyond it the oldest is disposed. A genuine document swap still frees all.</para>
/// <para>Decodes are async and device-bound, so the cache is seeded directly (<c>ImageCache.Seed</c>) with a
/// 4×4 bitmap — 64 pixel bytes — and nothing is drawn: a draw would start a real decode that could replace
/// the seeded entry when it lands.</para></summary>
[Collection(UiTests.Collection)]
public class ControlImageReleaseTests
{
    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly Type T = typeof(RichEditor);

    private static readonly Lazy<RichEditor> Shared = new(() =>
    {
        var ed = UiThread.Run(() => new RichEditor { Document = new FlowDocument(), PageSize = RichEditorPageSize.Continuous });
        UiThread.Host(ed);
        return ed;
    }, LazyThreadSafetyMode.ExecutionAndPublication);

    private static void Hosted(Action<RichEditor> body)
    {
        var ed = Shared.Value;
        UiThread.Run(() =>
        {
            long budget = ed.ImageRetainBytes;
            try { body(ed); }
            finally { ed.ImageRetainBytes = budget; ed.IsReadOnly = false; }
        });
    }

    private static object? Call(RichEditor ed, string name, params object?[] args)
    {
        var m = T.GetMethod(name, NP, args.Select(a => a?.GetType() ?? typeof(string)).ToArray())
            ?? T.GetMethod(name, NP)!;
        try { return m.Invoke(ed, args); }
        catch (TargetInvocationException tie) when (tie.InnerException != null)
        {
            ExceptionDispatchInfo.Capture(tie.InnerException).Throw();
            throw;
        }
    }

    private static ImageCache Cache(RichEditor ed) => (ImageCache)T.GetField("_images", NP)!.GetValue(ed)!;

    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAYAAABytg0kAAAAFElEQVR4nGP8//8/AzJgYkAD5AsAAP//A+8DTgn2rL0AAAAASUVORK5CYII=");

    private static CanvasBitmap Bitmap4x4()
        => CanvasBitmap.CreateFromBytes(CanvasDevice.GetSharedDevice(), new byte[4 * 4 * 4], 4, 4,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);

    private static bool IsDisposed(CanvasBitmap bmp)
    {
        try { _ = bmp.SizeInPixels; return false; }
        catch (ObjectDisposedException) { return true; }
    }

    // A document of "ab<picture>cd", loaded clean, with the picture's bitmap seeded into the cache.
    private static (byte[] raw, CanvasBitmap bmp) LoadWithPicture(RichEditor ed)
    {
        var img = new InlineImage { Width = 16, Height = 16 };
        img.SetImageData(TinyPng, "image/png");
        var doc = new FlowDocument();
        doc.Blocks.Add(new Paragraph { Inlines = { new Run { Text = "ab" }, img, new Run { Text = "cd" } } });
        ed.LoadJson(DocumentSerializer.Serialize(doc));
        var raw = ed.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<InlineImage>().Single().RawBytes!;
        var bmp = Bitmap4x4();
        Cache(ed).Seed(raw, bmp);
        return (raw, bmp);
    }

    // What the Delete key does with a selection.
    private static void SelectAllAndDelete(RichEditor ed)
    {
        Call(ed, "SelectAll");
        Call(ed, "PushUndo", (string?)null);
        Call(ed, "DeleteSelection");
        Call(ed, "AfterEdit");
    }

    [Fact]
    public void DeletingAPictureByEditing_DisposesItsBitmap_BeyondTheRetainBudget()
    {
        Hosted(ed =>
        {
            var (raw, bmp) = LoadWithPicture(ed);
            ed.ImageRetainBytes = 1; // below the seeded 64 pixel bytes

            SelectAllAndDelete(ed);

            Assert.False(Cache(ed).IsCached(raw));
            Assert.True(IsDisposed(bmp));
        });
    }

    [Fact]
    public void DeletingAPictureByEditing_KeepsItWithinTheBudget_AndUndoRedoFindItWarm()
    {
        Hosted(ed =>
        {
            var (raw, bmp) = LoadWithPicture(ed);
            ed.ImageRetainBytes = 1024;

            SelectAllAndDelete(ed);
            Assert.True(Cache(ed).IsCached(raw));
            Assert.Equal(64, Cache(ed).RetiredBytes);

            ed.Undo(); // back in the document: no longer charged against the budget
            Assert.True(Cache(ed).IsCached(raw));
            Assert.Equal(0, Cache(ed).RetiredBytes);

            ed.Redo(); // a history swap removes it again — kept, not disposed like a genuine swap
            Assert.True(Cache(ed).IsCached(raw));
            Assert.Equal(64, Cache(ed).RetiredBytes);
            Assert.False(IsDisposed(bmp));
        });
    }

    [Fact]
    public void ADocumentSwap_StillFreesEveryRemovedPicture_WhateverTheBudget()
    {
        Hosted(ed =>
        {
            var (raw, bmp) = LoadWithPicture(ed);
            ed.ImageRetainBytes = 1L << 40;

            ed.LoadJson(DocumentSerializer.Serialize(new FlowDocument()));

            Assert.False(Cache(ed).IsCached(raw));
            Assert.True(IsDisposed(bmp));
        });
    }

    [Fact]
    public void TheCache_DisposesOldestRemovedFirst_AndARevivedEntryLeavesTheBudget()
    {
        UiThread.Run(() =>
        {
            var cache = new ImageCache();
            byte[] a = [1], b = [2], c = [3];
            CanvasBitmap ba = Bitmap4x4(), bb = Bitmap4x4(), bc = Bitmap4x4();
            cache.Seed(a, ba);
            cache.Prune(new HashSet<object> { a, b, c }); // nothing removed yet
            cache.Seed(b, bb);
            cache.Seed(c, bc);

            cache.Prune(new HashSet<object> { b, c }, retainBytes: 1000); // a leaves first
            cache.Prune(new HashSet<object> { c }, retainBytes: 1000);    // then b
            Assert.Equal(128, cache.RetiredBytes);

            cache.Prune(new HashSet<object> { b }, retainBytes: 100); // b revives; a (oldest) goes, c fits
            Assert.True(IsDisposed(ba));
            Assert.False(IsDisposed(bb));
            Assert.False(IsDisposed(bc));
            Assert.Equal(64, cache.RetiredBytes);

            cache.Prune(new HashSet<object>()); // budget 0: a clear
            Assert.Equal(0, cache.DecodedCount);
            Assert.Equal(0, cache.RetiredBytes);
            Assert.True(IsDisposed(bb) && IsDisposed(bc));
        });
    }
}
