using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using WinUIRichEditor.Controls;

namespace WinUIRichEditor.Demo;

// Layer ①/②: the RichEditor control on its own. Navigated with a bool parameter — true = read-only
// (IsReadOnly=true: no caret/editing, selection & scrolling still work), false = full editing.
public sealed partial class ControlPage : Page
{
    public ControlPage() => InitializeComponent();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        bool readOnly = e.Parameter is true;
        Editor.Document = DemoContent.Sample();
        Editor.IsReadOnly = readOnly; // a "viewer" = read-only editor with no toolbar
        // No toolbar here, so surface the full formatting groups in the right-click menu (default is slim,
        // leaving formatting to the toolbar on the toolbar/View pages).
        Editor.ShowFormattingMenu = !readOnly;
        Caption.Text = readOnly
            ? "읽기 전용 RichEditor (IsReadOnly=true) — 편집·캐럿 없음, 선택/스크롤만 동작."
            : "RichEditor 단독 — 툴바 없음. 키보드 입력과 우클릭 메뉴로 편집하세요.";
    }
}
