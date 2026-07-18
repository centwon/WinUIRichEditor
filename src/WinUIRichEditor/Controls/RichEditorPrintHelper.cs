using System;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Windows.Foundation;
using Windows.Graphics.Printing;

namespace WinUIRichEditor.Controls;

/// <summary>Connects a <see cref="RichEditor"/> to the Windows print dialog. Pages come from
/// <see cref="RichEditor.RenderPrintPage"/> (the same Win2D raster pipeline the PDF export uses) and
/// are handed to a XAML <see cref="PrintDocument"/>; the dialog is shown through
/// <c>PrintManagerInterop</c>, which works for unpackaged apps given the host window's HWND.</summary>
public static class RichEditorPrintHelper
{
    /// <summary>Shows the system print UI for the editor's current document.
    /// <paramref name="windowHandle"/> is the host window's HWND
    /// (<c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c>). Returns <see langword="false"/>
    /// when printing is unavailable on this system or the dialog could not be shown; the print job
    /// itself (after the dialog) is asynchronous and owned by the spooler. UI thread only.</summary>
    public static async Task<bool> ShowPrintUIAsync(RichEditor editor, nint windowHandle, string jobTitle = "Document", double dpi = 300)
    {
        if (editor.Document == null || windowHandle == 0) return false;
        try { if (!PrintManager.IsSupported()) return false; } catch { return false; }

        var printDoc = new PrintDocument();
        var source = printDoc.DocumentSource; // grab on the UI thread; PrintTaskRequested is not
        int pageCount = Math.Max(1, editor.GetPrintPageCount());

        printDoc.Paginate += (_, _) => printDoc.SetPreviewPageCount(pageCount, PreviewPageCountType.Final);
        printDoc.GetPreviewPage += (_, e) => printDoc.SetPreviewPage(e.PageNumber, MakePageImage(editor, e.PageNumber - 1, 150));
        printDoc.AddPages += (_, _) =>
        {
            for (int i = 0; i < pageCount; i++) printDoc.AddPage(MakePageImage(editor, i, dpi));
            printDoc.AddPagesComplete();
        };

        var pm = PrintManagerInterop.GetForWindow(windowHandle);
        TypedEventHandler<PrintManager, PrintTaskRequestedEventArgs> onRequested = (_, args) =>
            args.Request.CreatePrintTask(jobTitle, req => req.SetSource(source));
        pm.PrintTaskRequested += onRequested;
        try { return await PrintManagerInterop.ShowPrintUIForWindowAsync(windowHandle); }
        catch { return false; }
        finally { pm.PrintTaskRequested -= onRequested; }
    }

    // One rendered page as a XAML Image sized to the paper (the PrintDocument page element). The Win2D
    // render target's BGRA8 premultiplied pixels match WriteableBitmap's layout, so a straight copy works.
    private static Image MakePageImage(RichEditor editor, int pageIndex, double dpi)
    {
        using var rt = editor.RenderPrintPage(pageIndex, dpi);
        var px = rt.SizeInPixels;
        var wb = new WriteableBitmap((int)px.Width, (int)px.Height);
        byte[] pixels = rt.GetPixelBytes();
        using (var s = wb.PixelBuffer.AsStream()) s.Write(pixels, 0, pixels.Length);
        var paper = editor.GetPaperPixelSize();
        return new Image
        {
            Source = wb,
            Width = paper.Width,
            Height = paper.Height,
            Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform,
        };
    }
}
