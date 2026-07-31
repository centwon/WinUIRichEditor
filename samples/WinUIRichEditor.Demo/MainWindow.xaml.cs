using Microsoft.UI.Xaml;

// To learn more about WinUI, the WinUI project structure,
// and more about our project templates, see: http://aka.ms/winui-project-info.

namespace WinUIRichEditor.Demo;

/// <summary>The application window: a top nav bar selecting one of the four demo layers, hosted in a Frame.</summary>
public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Deferred to Loaded, not run here: App.OnLaunched assigns App.MainWindow only AFTER this
        // constructor returns, and ViewDemoPage reads it in ITS constructor for the file-picker HWND.
        // Navigating straight there from here died with a stowed exception (0xC000027B). By Loaded the
        // window is being activated, so App.MainWindow is set.
        RootFrame.Loaded += OnRootFrameLoaded;
    }

    private void OnRootFrameLoaded(object sender, RoutedEventArgs e)
    {
        RootFrame.Loaded -= OnRootFrameLoaded; // first activation only
        NavigateToStartPage();
    }

    // Start page from the command line: `--page=control|readonly|toolbar|view` (default: control).
    // GUI checks on this demo are done by capturing the window with PrintWindow (the unpackaged exe
    // isn't reachable by the Start-menu-based automation tooling), and a capture can only ever show the
    // page that happens to be open — so being able to land on one directly is what makes that workflow
    // usable for anything but the first page.
    private void NavigateToStartPage()
    {
        string page = "control";
        foreach (var arg in System.Environment.GetCommandLineArgs())
        {
            if (arg.StartsWith("--page=", System.StringComparison.OrdinalIgnoreCase))
                page = arg["--page=".Length..].ToLowerInvariant();
            // `--roundtrip=<dir>`: HTML fidelity report over a corpus directory. A development tool that
            // lives here rather than in the library (it only uses the public formatter API), so shipping
            // it to consumers would have been shipping a dev tool. Writes the report next to the corpus
            // because a WinExe has no console to print to.
            else if (arg.StartsWith("--roundtrip=", System.StringComparison.OrdinalIgnoreCase))
                RunRoundTrip(arg["--roundtrip=".Length..]);
        }

        switch (page)
        {
            case "readonly": RootFrame.Navigate(typeof(ControlPage), true); break;
            case "toolbar": RootFrame.Navigate(typeof(ToolbarPage)); break;
            case "view": RootFrame.Navigate(typeof(ViewDemoPage)); break;
            default: RootFrame.Navigate(typeof(ControlPage), false); break; // the bare editing control
        }
    }

    // Redirects the harness's Console output into a file: the demo is a WinExe, so stdout goes nowhere.
    private static void RunRoundTrip(string dir)
    {
        if (dir.Length == 0) return;
        string outDir = System.IO.Path.Combine(dir, "roundtrip-out");
        var prev = System.Console.Out;
        try
        {
            using var report = new System.IO.StreamWriter(System.IO.Path.Combine(dir, "roundtrip-report.txt"), false);
            System.Console.SetOut(report);
            RoundTripHarness.Run(dir, outDir);
        }
        catch (System.Exception ex) { System.Diagnostics.Debug.WriteLine($"roundtrip failed: {ex.Message}"); }
        finally { System.Console.SetOut(prev); }
    }

    private void OnNavControl(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ControlPage), false);
    private void OnNavReadOnly(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ControlPage), true);
    private void OnNavToolbar(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ToolbarPage));
    private void OnNavView(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ViewDemoPage));
}
