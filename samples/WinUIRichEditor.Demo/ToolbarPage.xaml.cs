using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinUIRichEditor.Controls;

namespace WinUIRichEditor.Demo;

// Layer ②: the RichEditor wired to the library's RichEditorToolbar (no status bar / page-zoom chrome —
// that's the full RichEditorView on the View page).
public sealed partial class ToolbarPage : Page
{
    public ToolbarPage()
    {
        InitializeComponent();

        var toolbar = new RichEditorToolbar { ToolbarLevel = ToolbarLevel.Maximum, Target = Editor };
        toolbar.ImagePicker = PickImageBytesAsync;
        Editor.ImageReplacePicker = PickImageBytesAsync;
        // The toolbar now carries the built-in Export/Import (needs the window handle for the unpackaged
        // file picker) and a Print button wired to print-to-PDF.
        toolbar.WindowHandle = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        toolbar.PrintRequested += async (_, _) =>
        {
            if (Editor.Document is null) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "document" };
            picker.FileTypeChoices.Add("PDF", new[] { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSaveFileAsync();
            if (file == null) return;
            using var ms = new System.IO.MemoryStream();
            Editor.SavePdf(ms);
            await FileIO.WriteBytesAsync(file, ms.ToArray());
        };
        ToolbarHost.Child = toolbar;

        // How a host consumes RichEditorDiagnostics: the library handles these faults itself and carries
        // on, so this changes nothing about behaviour — it just makes the handling visible. Worth having
        // in the sample because the alternative is indistinguishable from success: a file-open that threw
        // inside the picker looks exactly like one the user cancelled. (That is not hypothetical — it is
        // how the fire-and-forget hole in Import/ExportAsync was found.)
        WinUIRichEditor.RichEditorDiagnostics.Fault += OnEditorFault;

        Editor.Document = DemoContent.Sample();
    }

    private void OnEditorFault(object? sender, WinUIRichEditor.RichEditorFaultEventArgs e)
    {
        // HResult, not just Message: a COMException usually has an empty message and the code is the
        // only identifying part.
        string detail = $"{e.File}:{e.Line} {e.Member} — {e.Exception.GetType().Name} "
            + $"0x{e.Exception.HResult:X8} {e.Exception.Message}";
        // Faults can arrive on a background thread (image decode / document parsing).
        DispatcherQueue.TryEnqueue(() =>
        {
            Caption.Foreground = new Microsoft.UI.Xaml.Media.SolidColorBrush(Microsoft.UI.Colors.Firebrick);
            Caption.Text = "진단: " + detail;
        });
    }

    private static async Task<byte[]?> PickImageBytesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp" }) picker.FileTypeFilter.Add(ext);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSingleFileAsync();
        if (file == null) return null;
        var buffer = await FileIO.ReadBufferAsync(file);
        return buffer.ToArray();
    }
}
