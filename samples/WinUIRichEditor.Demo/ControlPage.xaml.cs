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
        // Opt into the static viewer caret (off by default) so this page also demonstrates keyboard
        // navigation in a viewer: arrows / Shift+arrows / Home·End / PageUp·Down / Ctrl+F all work.
        Editor.ShowCaretWhenReadOnly = readOnly;
        // No toolbar here, so surface the full formatting groups in the right-click menu (default is slim,
        // leaving formatting to the toolbar on the toolbar/View pages).
        Editor.ShowFormattingMenu = !readOnly;
        Caption.Text = readOnly
            ? "읽기 전용 RichEditor (IsReadOnly=true) — 편집 불가. 선택·스크롤·키보드 이동 동작, "
              + "캐럿은 ShowCaretWhenReadOnly로 표시(깜빡이지 않음)."
            : "RichEditor 단독 — 툴바 없음. 키보드 입력과 우클릭 메뉴로 편집하세요.";
    }
}
