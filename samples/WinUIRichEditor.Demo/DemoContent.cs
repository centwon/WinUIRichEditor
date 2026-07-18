using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Demo;

// Shared sample document used by the demo pages so each layer (bare control, read-only, control+toolbar,
// full view) shows the same content.
internal static class DemoContent
{
    public const string SampleHtml =
        "<h1>WinUIRichEditor</h1>" +
        "<p>일반 문단입니다. <b>굵게</b>, <i>기울임</i>, <u>밑줄</u>, <s>취소선</s>, " +
        "<span style=\"color:#cc0000\">빨강</span>, <span style=\"font-size:14pt\">큰 글씨</span>.</p>" +
        "<p>하이퍼링크: <a href=\"https://github.com/microsoft/Win2D\">Win2D 저장소</a> — " +
        "편집 모드에서는 Ctrl+클릭, 읽기 전용에서는 일반 클릭으로 열립니다.</p>" +
        "<p style=\"text-align:center\">가운데 정렬 문단 — Win2D <b>CanvasTextLayout</b> 렌더.</p>" +
        "<p style=\"text-align:right\">오른쪽 정렬.</p>" +
        "<blockquote>인용 블록입니다. 왼쪽에 회색 막대가 보여야 합니다.</blockquote>" +
        "<h2>리스트</h2>" +
        "<ul><li>불릿 항목 하나</li><li>불릿 항목 둘</li></ul>" +
        "<ol><li>번호 항목 하나</li><li>번호 항목 둘</li><li>번호 항목 셋</li></ol>" +
        "<h2>표</h2>" +
        "<table><tr><td>이름</td><td>점수</td></tr>" +
        "<tr><td>가나다</td><td>95</td></tr>" +
        "<tr><td>라마바 긴 셀 내용이 줄바꿈되는지 확인</td><td>80</td></tr></table>" +
        "<hr/>" +
        "<p>구분선 위/아래. 긴 문단의 자동 줄바꿈을 확인하기 위한 텍스트입니다. " +
        "Lorem ipsum dolor sit amet, consectetur adipiscing elit, sed do eiusmod tempor " +
        "incididunt ut labore et dolore magna aliqua. 한글과 영문이 섞인 긴 문장을 충분히 길게 " +
        "이어서 컨트롤 폭에서 자연스럽게 줄바꿈되는지 봅니다.</p>";

    public static FlowDocument Sample() => HtmlDocumentFormatter.ParseHtml(SampleHtml);
}
