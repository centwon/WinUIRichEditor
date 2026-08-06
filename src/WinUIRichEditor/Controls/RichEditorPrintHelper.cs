using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage.Streams;

namespace WinUIRichEditor.Controls;

/// <summary>Connects a <see cref="RichEditor"/> to the Windows print dialog. Pages come from
/// <see cref="RichEditor.RenderPrintPage"/> (the same Win2D raster pipeline the PDF export uses) and
/// are handed to a XAML <see cref="PrintDocument"/>; the dialog is shown through
/// <c>PrintManagerInterop</c>, which works for unpackaged apps given the host window's HWND.</summary>
public static class RichEditorPrintHelper
{
    // The print flow outlives ShowPrintUIAsync: the dialog calls back into PrintDocument for pagination,
    // preview, and the pages themselves, and the spooler runs later still. Nothing in the print system
    // holds a managed reference back, so locals here are collectable the moment the method awaits — with
    // the PrintDocument gone its callbacks never run and the dialog sits on "loading preview" forever.
    // These fields are the strong reference for the whole job; PrintTask.Completed releases them.
    private static PrintDocument? _document;
    private static IPrintDocumentSource? _documentSource;
    private static PrintManager? _manager;
    private static TypedEventHandler<PrintManager, PrintTaskRequestedEventArgs>? _onTaskRequested;
    private static List<BitmapImage>? _pages;

    /// <summary>Shows the system print UI for the editor's current document.
    /// <paramref name="windowHandle"/> is the host window's HWND
    /// (<c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c>). Returns <see langword="false"/>
    /// when printing is unavailable on this system or the dialog could not be shown; the print job
    /// itself (after the dialog) is asynchronous and owned by the spooler. UI thread only.
    /// <para><paramref name="dpi"/> (default 150, same as <see cref="RichEditor.SavePdf"/>) sets the
    /// raster resolution for both preview and output. Every page is rendered before the dialog opens
    /// — the print callbacks are synchronous, so encoding cannot be awaited inside them — which costs
    /// roughly 8 MB per A4 page at 150 DPI. Lower it for very long documents.</para></summary>
    public static async Task<bool> ShowPrintUIAsync(RichEditor editor, nint windowHandle, string jobTitle = "Document", double dpi = 150)
    {
        if (editor.Document == null || windowHandle == 0) return false;
        try { if (!PrintManager.IsSupported()) return false; }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return false; }

        // Bail out before anything is shown when the host has dynamic code disabled — Native AOT, or any
        // build with PublishAot set, which bakes IsDynamicCodeSupported=false into runtimeconfig.json.
        // PrintManagerInterop.ShowPrintUIForWindowAsync casts its result to IAsyncOperation<bool> through
        // IDynamicInterfaceCastable; CsWinRT (2.2) resolves that ABI helper by reflection, which it refuses
        // to attempt without dynamic code. The throw lands *after* the dialog is on screen, leaving a live
        // window with no document behind it — worse than not offering the dialog at all. There is no public
        // way to pre-register the instantiation (WinRT.TypeExtensions.RegisterHelperType and the ABI types
        // are internal), and the CsWinRT AOT generator emits CCW vtables only, so callers on AOT need their
        // own path (e.g. export a PDF).
        if (!RuntimeFeature.IsDynamicCodeSupported) return false;

        int pageCount = Math.Max(1, editor.GetPrintPageCount());
        var paper = editor.GetPaperPixelSize();

        // Rendered up front: PrintDocument's callbacks are synchronous, and turning a Win2D render
        // target into an ImageSource needs an await (see RenderPageAsync).
        List<BitmapImage> pages;
        try
        {
            pages = new List<BitmapImage>(pageCount);
            for (int i = 0; i < pageCount; i++) pages.Add(await RenderPageAsync(editor, i, dpi));
        }
        catch (Exception ex) { RichEditorDiagnostics.Report(ex); return false; }

        Release();   // drop any previous job's references

        var printDoc = new PrintDocument();
        _document = printDoc;
        _documentSource = printDoc.DocumentSource;   // grab on the UI thread; PrintTaskRequested is not
        _pages = pages;

        printDoc.Paginate += (_, _) => printDoc.SetPreviewPageCount(pageCount, PreviewPageCountType.Final);
        printDoc.GetPreviewPage += (_, e) => printDoc.SetPreviewPage(e.PageNumber, MakePageElement(pages[e.PageNumber - 1], paper));
        printDoc.AddPages += (_, _) =>
        {
            // A UIElement has one parent, so each page gets its own Image (the bitmaps are shared).
            for (int i = 0; i < pageCount; i++) printDoc.AddPage(MakePageElement(pages[i], paper));
            printDoc.AddPagesComplete();
        };

        var manager = PrintManagerInterop.GetForWindow(windowHandle);
        _manager = manager;

        // Set once the dialog asks for the document: proof it engaged, even if ShowPrintUIForWindowAsync
        // reports false afterwards. Callers use the return value to decide on a fallback, and showing one
        // on top of a live print dialog is worse than trusting the dialog.
        bool taskRequested = false;
        _onTaskRequested = (_, args) =>
        {
            taskRequested = true;
            var task = args.Request.CreatePrintTask(jobTitle, req => req.SetSource(_documentSource));
            task.Completed += (_, _) => Release();
        };
        manager.PrintTaskRequested += _onTaskRequested;

        try
        {
            bool shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle);
            if (!shown && !taskRequested) Release();
            return shown || taskRequested;
        }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            Release();
            return false;
        }
    }

    // Ends the job's lifetime: unhooks the manager and drops the pages (a few MB each).
    private static void Release()
    {
        if (_manager != null && _onTaskRequested != null)
            _manager.PrintTaskRequested -= _onTaskRequested;

        _onTaskRequested = null;
        _manager = null;
        _documentSource = null;
        _document = null;
        _pages = null;
    }

    // One rendered page as a BitmapImage, by way of an in-memory PNG.
    //
    // The obvious route — new WriteableBitmap(w, h) filled from rt.GetPixelBytes() — needs a write into
    // WriteableBitmap.PixelBuffer, and `PixelBuffer.AsStream()` only handles buffers backed by managed
    // arrays under CsWinRT (.NET 5+), not the native buffer XAML hands out. Encoding to PNG and decoding
    // through BitmapImage stays on fully supported APIs, at the cost of the round trip.
    private static async Task<BitmapImage> RenderPageAsync(RichEditor editor, int pageIndex, double dpi)
    {
        using var rt = editor.RenderPrintPage(pageIndex, dpi);
        using var stream = new InMemoryRandomAccessStream();

        await rt.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    // The PrintDocument page element: the page image scaled to the paper.
    private static Image MakePageElement(BitmapImage source, Size paperPixelSize) => new()
    {
        Source = source,
        Width = paperPixelSize.Width,
        Height = paperPixelSize.Height,
        Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
    };
}
