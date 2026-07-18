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

        Editor.Document = DemoContent.Sample();
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
