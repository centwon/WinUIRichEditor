using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
        CompatBar.Child = BuildCompatStrip(view, PickImageBytesAsync);
    }

    // Parity track 1 — the compat surface that mirrors the original AvaloniaRichEditor's API names.
    // Nothing else in the demo calls these, so they shipped build-verified but never actually invoked;
    // this strip is the manual harness. Each button is one call, labelled with the exact member.
    private static UIElement BuildCompatStrip(RichEditorView view, System.Func<System.Threading.Tasks.Task<byte[]?>> pickImage)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var readout = new TextBlock { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 0, 0) };
        void Report(string s) => readout.Text = s;

        Button Btn(string label, string tip, RoutedEventHandler onClick)
        {
            var b = new Button { Content = label, FontSize = 12, Padding = new Thickness(8, 3, 8, 3) };
            ToolTipService.SetToolTip(b, tip);
            b.Click += onClick;
            return b;
        }

        var ed = view.Editor;

        // --- aliases of the port's own names (hidden from IntelliSense, still callable) ---
        row.Children.Add(Btn("SetFontFamily", "RichEditor.SetFontFamily → SetRunFontFamily. 선택(없으면 캐럿 단어)을 Consolas로.",
            (_, _) => { ed.SetFontFamily("Consolas"); Report("SetFontFamily(\"Consolas\") 호출"); }));

        row.Children.Add(Btn("InsertImageBytes", "RichEditor.InsertImageBytes → InsertImageBlock. 파일을 골라 바이트로 삽입.",
            async (_, _) =>
            {
                var bytes = await pickImage();
                if (bytes is { Length: > 0 }) { ed.InsertImageBytes(bytes); Report($"InsertImageBytes({bytes.Length}B) 호출"); }
                else Report("InsertImageBytes 취소");
            }));

        row.Children.Add(Btn("PasteFromClipboard", "RichEditor.PasteFromClipboardAsync → PasteAsync.",
            async (_, _) => { await ed.PasteFromClipboardAsync(); Report("PasteFromClipboardAsync 호출"); }));

        // --- public wrappers the original had and the port lacked ---
        row.Children.Add(Btn("FocusDocumentEnd", "RichEditor.FocusDocumentEnd — 포커스 + 캐럿을 문서 끝으로.",
            (_, _) => { ed.FocusDocumentEnd(); Report("FocusDocumentEnd 호출 — 캐럿이 문서 끝"); }));

        row.Children.Add(Btn("InsertInlineTable 2×2", "RichEditor.InsertInlineTable(2,2) — 캐럿에 '글자처럼 취급' 표 삽입.",
            (_, _) => { ed.InsertInlineTable(2, 2); Report("InsertInlineTable(2,2) 호출"); }));

        row.Children.Add(Btn("InsertImageFromFile", "RichEditor.InsertImageFromFileAsync(hwnd) — 라이브러리 내장 피커.",
            async (_, _) => { await ed.InsertImageFromFileAsync(view.WindowHandle); Report("InsertImageFromFileAsync(hwnd) 호출"); }));

        // --- RichEditorView-level surface ---
        var statusToggle = new ToggleButton { Content = "ShowStatusBar", FontSize = 12, IsChecked = view.ShowStatusBar, Padding = new Thickness(8, 3, 8, 3) };
        ToolTipService.SetToolTip(statusToggle, "RichEditorView.ShowStatusBar — 하단 상태바 표시 토글.");
        statusToggle.Click += (_, _) =>
        {
            view.ShowStatusBar = statusToggle.IsChecked == true;
            Report($"ShowStatusBar = {view.ShowStatusBar}");
        };
        row.Children.Add(statusToggle);

        row.Children.Add(Btn("ZoomFactor 1.5", "RichEditorView.ZoomFactor — Editor.Zoom/SetZoom 프록시. 설정 후 되읽어 확인.",
            (_, _) =>
            {
                view.ZoomFactor = 1.5;
                // Read back through the View AND the editor: the getter must proxy Editor.Zoom.
                Report($"ZoomFactor set 1.5 → get {view.ZoomFactor:0.##} (Editor.Zoom {view.Editor.Zoom:0.##})");
            }));

        row.Children.Add(Btn("ZoomFactor 1.0", "RichEditorView.ZoomFactor — 원래 배율로 복귀.",
            (_, _) => { view.ZoomFactor = 1.0; Report($"ZoomFactor set 1.0 → get {view.ZoomFactor:0.##}"); }));

        row.Children.Add(readout);

        return new ScrollViewer
        {
            Content = row,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Auto,
        };
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
