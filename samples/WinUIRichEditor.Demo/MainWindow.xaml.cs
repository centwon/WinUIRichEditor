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

        RootFrame.Navigate(typeof(ControlPage), false); // start on the bare editing control
    }

    private void OnNavControl(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ControlPage), false);
    private void OnNavReadOnly(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ControlPage), true);
    private void OnNavToolbar(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ToolbarPage));
    private void OnNavView(object sender, RoutedEventArgs e) => RootFrame.Navigate(typeof(ViewDemoPage));
}
