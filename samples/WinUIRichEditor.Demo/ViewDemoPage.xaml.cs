using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Demo;

// Demonstrates the drop-in RichEditorView host control (toolbar + editor + status bar). The page is a
// normal XAML shell (code-only navigated pages crash WinUI 3 navigation); the RichEditorView is built
// in code-behind and dropped into the Host border.
public sealed partial class ViewDemoPage : Page
{
    public ViewDemoPage()
    {
        InitializeComponent();

        var view = new RichEditorView();
        // Enable the toolbar's built-in Export/Import (file picker needs the window handle in an
        // unpackaged app) and wire the Print button to the Windows print dialog, falling back to the
        // demo's print-to-PDF when printing is unavailable on this system.
        view.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        view.PrintRequested += async (_, _) =>
        {
            if (!await RichEditorPrintHelper.ShowPrintUIAsync(view.Editor, view.WindowHandle, "WinUIRichEditor"))
                await SavePdfFallbackAsync();
        };

        async System.Threading.Tasks.Task<byte[]?> PickImageBytesAsync()
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
            foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }) picker.FileTypeFilter.Add(ext);
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSingleFileAsync();
            if (file == null) return null;
            var buffer = await FileIO.ReadBufferAsync(file);
            return buffer.ToArray();
        }

        view.ImagePicker = PickImageBytesAsync;
        view.Editor.ImageReplacePicker = PickImageBytesAsync;
        view.Editor.ImageSaveHandler = async (bytes, mime) =>
        {
            var ext = mime switch { "image/jpeg" => ".jpg", "image/gif" => ".gif", "image/bmp" => ".bmp", _ => ".png" };
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary, SuggestedFileName = "image" };
            picker.FileTypeChoices.Add("Image", new[] { ext });
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
            var file = await picker.PickSaveFileAsync();
            if (file != null) await FileIO.WriteBytesAsync(file, bytes);
        };

        const string html =
            "<h1>RichEditorView</h1>" +
            "<p>드롭인 호스트 컨트롤: <b>툴바</b> + 에디터 + <i>상태바</i>(글자/단어/줄·칸).</p>" +
            "<p>툴바로 굵게/기울임/밑줄, 글꼴·크기, 색/형광, 정렬, 제목, 목록, 줄 간격, 표/이미지/구분선 삽입을 시험하세요.</p>" +
            "<ul><li>표 셀에서 Tab 이동</li><li>셀 우클릭 → 표 메뉴</li><li>더블클릭 단어 선택</li></ul>";
        view.Document = HtmlDocumentFormatter.ParseHtml(html);

        Host.Child = view;
    }

    // Print fallback when the system print dialog is unavailable: save to PDF instead. (The toolbar's
    // Export button also offers PDF; this only backs the Print button.)
    private async System.Threading.Tasks.Task SavePdfFallbackAsync()
    {
        var ed = ((RichEditorView)Host.Child).Editor;
        if (ed.Document is null) return;
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "document" };
        picker.FileTypeChoices.Add("PDF", new[] { ".pdf" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        using var ms = new System.IO.MemoryStream();
        ed.SavePdf(ms);                       // renders on the UI thread (Win2D)
        await FileIO.WriteBytesAsync(file, ms.ToArray());
    }
}
