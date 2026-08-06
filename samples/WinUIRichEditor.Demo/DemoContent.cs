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
        // 셀 안 문단 서식이 왜 문서 앞쪽에 있는가: 이 데모는 손으로 확인하는 수단이고, 확인 방법은
        // 로드맵의 `--page=` + PrintWindow 캡처다. 그 방법은 **첫 화면만** 본다 — 창은 작업 영역보다
        // 크게 만들 수 없어서(실측: 높이가 1084로 클램프된다) 스크롤해야 보이는 것은 그 레시피로는
        // 아예 확인할 수 없다. 데모의 표는 홑 문단 평문 셀만 담고 있어서 셀 안 문단 서식 축을 하나도
        // 보여주지 않았고, 그게 2026-08-07 전수조사에서 결함 3건이 나온 바로 그 축이다.
        // 복사 → Word/HWP/브라우저 붙여넣기로 확인할 형태들이 정확히 이것이다.
        "<h2>셀 안 문단 서식</h2>" +
        "<table>" +
        "<tr><td><ul style=\"list-style-type:square\"><li>글머리표 셀 하나</li>" +
        "<li>글머리표 셀 둘</li></ul></td>" +
        "<td><h3>셀 안 제목</h3><p style=\"text-align:center\">가운데 정렬 문단</p></td></tr>" +
        "<tr><td><p>한 셀에 두 문단 — 첫째</p><p>두 문단이 하나로 합쳐지지 않아야 한다</p></td>" +
        "<td><p style=\"background-color:#fff2cc;margin-left:20px\">문단 배경 + 들여쓰기</p></td></tr>" +
        "</table>" +
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
