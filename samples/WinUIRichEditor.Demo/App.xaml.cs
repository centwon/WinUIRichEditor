using Windows.ApplicationModel;
using Windows.ApplicationModel.Activation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Microsoft.UI.Xaml.Shapes;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIRichEditor.Demo;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>The main window, exposed so pages can get its HWND for file-picker interop (unpackaged).</summary>
    public static Window? MainWindow { get; private set; }

    /// <summary>
    /// Initializes the singleton application object.  This is the first line of authored code
    /// executed, and as such is the logical equivalent of main() or WinMain().
    /// </summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        StartFaultLogIfAsked();
        _window = new MainWindow();
        MainWindow = _window;
        _window.Activate();
    }

    // `--faultlog=<path>`: append every RichEditorDiagnostics fault to a file.
    //
    // ToolbarPage shows the latest fault in its caption, which is how a host would surface one; this is
    // for MEASURING instead — the comparison that matters is the same session driven twice, once JIT and
    // once Native AOT, because AOT's failures are silent by construction. Every swallow point in the
    // library reports here, so the file is the complete list of what the editor handled and carried on
    // from. That is how the AOT line-metrics defect was eventually seen: it built, it rendered, and the
    // only visible difference was a fault nobody was listening for.
    //
    // App level, not page level: a page-scoped subscription only ever covers the page that happens to
    // be open. A WinExe has no console, so a file it is (same reason as `--roundtrip=`).
    private static void StartFaultLogIfAsked()
    {
        string? path = null;
        foreach (var arg in System.Environment.GetCommandLineArgs())
            if (arg.StartsWith("--faultlog=", System.StringComparison.OrdinalIgnoreCase))
                path = arg["--faultlog=".Length..];
        if (string.IsNullOrEmpty(path)) return;

        var gate = new object();
        WinUIRichEditor.RichEditorDiagnostics.Fault += (_, e) =>
        {
            // Faults can arrive on a background thread (image decode, document parsing), and the whole
            // point is not to disturb what is being measured — so this never throws back into the editor.
            try
            {
                string line = $"{System.DateTime.Now:HH:mm:ss.fff}\t{e.File}:{e.Line}\t{e.Member}\t"
                    + $"{e.Exception.GetType().Name}\t0x{e.Exception.HResult:X8}\t{e.Exception.Message}";
                lock (gate) System.IO.File.AppendAllText(path!, line + System.Environment.NewLine);
            }
            catch { }
        };
    }
}
