# Changelog

All notable changes to WinUIRichEditor. This project is a WinUI 3 + Win2D port of AvaloniaRichEditor;
the format follows [Keep a Changelog](https://keepachangelog.com/). The public API is frozen as of 1.0.0
and follows [Semantic Versioning](https://semver.org/).

## [Unreleased]

### Fixed — 캐럿 깜빡임·더블클릭 간격이 OS 접근성 설정을 무시함 (2026-08-05, 외부 코드 리뷰)

캐럿 깜빡임 `530ms`와 더블클릭 판정 `500ms`가 하드코딩이었다. 리뷰는 이걸 "매직 넘버"로 묶었지만
실제 무게는 **접근성**이다 — 둘 다 사용자가 바꾸는 설정이고, 컨트롤이 자기 상수를 쓰면 조용히 덮어쓴다.

- 깜빡임을 **끈** 사용자(설정 > 접근성 > 텍스트 커서)에게도 계속 깜빡였다. 상수로는 "끔"을 표현할
  방법 자체가 없었다 — `GetCaretBlinkTime`은 그 경우 `INFINITE`를 돌려준다.
- 더블클릭을 **느리게** 맞춘 사용자(손 떨림 등)는 500ms 안에 두 번 누르지 못해 단어 선택이 아예
  동작하지 않았다.

- **Added**: `SystemInputSettings`(internal) — `GetCaretBlinkTime`/`GetDoubleClickTime`을 읽고,
  값이 없거나 실패하면 종전 상수로 폴백한다. 깜빡임 끔은 `null`로 표현되고, 이때 캐럿은 **숨기는 게
  아니라 켜진 채로 정지**한다(설정이 끄는 것은 *움직임*이지 캐럿이 아니다).
- 값은 **포커스를 얻을 때마다** 다시 읽는다 — 이 컨트롤에는 `WM_SETTINGCHANGE` 훅이 없고, 포커스
  복귀가 곧 "사용자가 설정을 만지고 돌아온" 시점이다.
- 테스트 110 → 113. OS 값 자체는 단정할 수 없으므로(기계마다 다름) **인터롭이 성립하는지**를 잡는다 —
  P/Invoke 시그니처가 틀리면 조용히 쓰레기 값을 받아 그대로 채택하는 게 이 변경의 실패 모드다.

⚠️ **실기 확인 미완료**: 개발 기계가 Windows 기본값(530/500)이라 눈으로는 차이가 없다. 검증하려면
설정을 실제로 바꿔야 한다(`Project_Roadmap.md`의 "재사용할 검증 레시피" 참조).

### Fixed — 손상된 RTF를 열면 문서가 조용히 비워짐 (2026-08-05, 외부 코드 리뷰)

1.0은 "손상된 문서는 빈 문서로 읽지 않고 **보고한다**"를 계약으로 세웠다(`LoadJson`/`LoadJsonAsync`/
`LoadPackageAsync`가 예외를 던지도록 바꾼 것). **RTF만 그 계약에서 빠져 있었다** — 설계가 아니라 누락이다.

증상: 손상된 `.rtf`를 열면 `LooksLikeRtf`는 통과하고 → `Parse`가 내부에서 던진 예외를 삼켜 빈 문서를
돌려주고 → `LoadRtf`가 **열려 있던 문서를 그 빈 것으로 교체**했다. 툴바 파일 열기의 `catch`는 예외가
이미 삼켜졌으니 걸리지 않아 사용자에게 아무 표시도 없었고, **다음 저장이 원본 파일을 덮었다.**

- **Added**: `RtfDocumentFormatter.TryParse(string, out FlowDocument, out string?)` — 손상과 "정상이지만
  빈 문서"를 구분한다. 열려 있는 내용을 **교체하는** 경로(파일 열기)는 이걸 써야 한다.
- **동작 변경**: `LoadRtf`가 파싱 실패 시 **열린 문서를 건드리지 않는다**(종전: 빈 문서로 교체).
  단 `Document`가 아직 `null`이면 지킬 것이 없으므로 빈 문서를 넣는다 — 안 그러면 에디터가 캐럿도 없는
  무력 상태로 남는다.
  시그니처는 `void` 그대로다 — 반환 타입은 IL 시그니처의 일부라 바꾸면 기존 소비자가
  `MissingMethodException`을 맞고, 새 예외를 던지면 핸들러 없는 1.0 호스트가 죽는다. 그래서 **조용히
  유지**하고, 실패 상세가 필요한 호스트는 `TryParse`를 직접 쓴다.
- 툴바 파일 열기가 `TryParse`로 바뀌어 실패 사유를 다른 임포트 오류와 같은 채널로 보고한다.
- `Parse`는 **의미가 완전히 동일**하다(실패 시 빈 문서). 붙여넣기 경로가 "빈 결과 → HTML → 평문"
  폴백에 그 동작을 의존하므로 바꾸지 않았다. 테스트 106 → 110.

## [1.0.0] - 2026-07-31

**공개 API를 동결한다.** 이제부터 SemVer를 따른다 — 주 버전을 올리지 않고는 breaking 변경이 없다.
표면은 `PublicAPI.Shipped.txt`(552줄)로 추적되므로 공개 API 변경은 빌드 경고와 diff로 드러난다.

1.0을 만든 것은 새 기능이 아니라 **검증 깊이**다. 상류 AvaloniaRichEditor가 1.0을 낸 뒤 그 검증 라운드를
포트 소스에 1:1로 대조해 **결함 20건**을 고쳤다 — 붙여넣기로 도달 가능한 메모리 고갈 2건, 평범한 Word
RTF를 가져올 때의 텍스트 손실, 툴바가 조용히 키보드 포커스를 앗아가던 문제를 포함한다. 아래 절들이
트랙 A~E와 그 뒤의 2차 대조·전수조사 내역이다.

### ⚠️ 이 버전으로 올릴 때 확인할 것
- **Breaking**: `Formatters.RoundTripHarness` 제거(개발 도구, 데모로 이관).
- **동작 변경**: 손상된 문서가 **예외를 던진다**(`InvalidDataException`/`JsonException`). 종전엔 빈 문서를
  돌려줬다 — `LoadJson`/`LoadJsonAsync`/`LoadPackageAsync`를 쓰는 호스트는 예외를 처리해야 한다.
- **동작 변경**: RTF 표 출력 형태(가로 병합=기하 형태, 셀 안 중첩 표=실제 `\itap` 중첩).
- 최소 요구 `Microsoft.WindowsAppSDK.WinUI`는 **2.2.1 그대로**다.

### 읽는 법
아래는 **작업이 일어난 순서 그대로의 로그**다(최신이 위). 각 항목이 결함 하나와 그 **근거**를 담고
있어서, 나중에 "왜 이렇게 짜여 있나"를 되짚을 때 쓰라고 통째로 남긴다 — 몇 개는 **나중에 틀린 것으로
드러난 판단**도 그대로 기록돼 있다.

소비자용으로 읽을 수 있게 정리한 요약은
[GitHub Release v1.0.0](https://github.com/centwon/WinUIRichEditor/releases/tag/v1.0.0)에 있다.
한눈에 보는 갈래:

| 갈래 | 무엇 |
|---|---|
| 상류 1.0 대조 (트랙 A~E) | 원본 AvaloniaRichEditor 1.0의 검증 라운드를 1:1로 대조해 고친 것 |
| 전수조사 2~4차 | 이 세션이 **직접 만든 코드**를 포함해 다시 훑은 것 |
| 랜덤 편집열 퍼즈 | 정독·대조·왕복이 다 놓친 "안 시험한 조합"을 잡은 것 |
| 사용자 리포트 | 실기에서만 드러난 것(툴바 포커스, 캐럿 정렬, 세로 이동, 아이콘 중복) |

### 상류 1.0 대조 — 트랙 D: 상호운용 (2026-07-31, 부분)

빌드 0/0, 테스트 **86/86**(신규 4). 신규 3건은 **수정 전 FAIL 확인**(나머지 1건은 인라인 마커가 생긴
뒤에야 의미가 생기는 회귀 가드다). **RTF 중첩 표는 미착수** — 아래 "남은 것" 참조.

#### Interoperability
- **인라인 표가 HTML 왕복에서 인라인으로 남는다.** HTML에는 인라인 표가 없어 `<table>`로 나갔다가
  **블록** 표로 돌아왔다 — 저장하고 다시 열 때마다 그 표를 품은 문단이 쪼개지고 표 뒤의 텍스트가 새
  문단이 됐다. 자체 내보내기는 `data-are-inline`으로 표시하고 가져오기는 그것을 텍스트 줄에 복원한다
  (외부 HTML은 표시가 없으니 종전대로 블록 표).
- **인라인 표가 전폭 밴드로 깔리던 것 수정.** 블록 표의 `width:100%`를 그대로 물려받아, 자체 임포터를
  제외한 모든 소비자(브라우저·Word)가 자기 줄을 차지하는 블록으로 배치했다. 자기 열 너비로 크기를 잡고
  `display:inline-table`로 표시해 텍스트 줄 안에 앉는다.
- **HTML 왕복마다 인라인 표 뒤에 공백이 1칸씩 늘던 것 수정.** 내보낸 `</table>`이 블록 표용 정렬
  개행을 달고 나갔고, 텍스트 줄 안에서 그 공백 텍스트 노드가 공백 하나로 파싱됐다.
- **인라인 표가 RTF 왕복에서 살아남는다.** 무시 가능 그룹 `{\*\arinline}`을 함께 실어 보낸다 — 다른
  앱은 정의상 그 그룹을 건너뛰고 종전과 똑같은 블록 표를 보며, 자체 리더만 마커를 읽어 표를 텍스트
  줄에 되돌린다. 이로써 RTF도 .flow/JSON/HTML과 함께 인라인 표를 왕복시킨다.
- **RTF 셀 배경색을 쓰고 읽는다**(`\clcbpat`). 에디터에 셀 배경색 UI가 있고 모델·JSON·HTML은 값을
  나르는데 RTF만 쓰지도 읽지도 않아, Word·HWP는 물론 **자체 저장/열기에서도** 색이 사라졌다.
  검정 배경은 실제 선택이므로 `blackIsDefault: false`로 색상표에 등록한다(형광펜과 같은 이유).

> **실기 검증 완료(2026-07-31, 사람 확인 — Word·HWP·브라우저·자체 왕복 비교)**: 위 3건 전부 동작.
> 브라우저에서 인라인 표가 줄 안에 앉고, RTF 저장→불러오기에서 인라인 표가 인라인으로 복원되며 셀
> 배경색도 유지된다. Word 붙여넣기는 중첩 표·셀 배경·병합 모두 정상.

- 인라인 표가 Word/HWP에서 줄에서 분리되는 것은 결함이 아니다: RTF에 인라인 표가 없어 호스트 문단을
  쪼개는 구조이고 원본 1.0도 동일하다(자체 왕복만 마커로 복원, HTML 경로는 브라우저에서 줄 안에 앉는다).

### 1.0 직전 2차 대조 + 전수조사 (2026-07-31)

동결 전 한 번 더 대조하고 훑었다. 상류는 `v1.0.0`에서 멈춰 있고(1.0 이후 커밋 없음) 작업 트리도 깨끗해
새로 들어온 것은 없었다. **결함 4건**을 더 찾았다. 테스트 90 → 91.

#### 2차 대조 — 공개 표면을 멤버 단위로 재대조
- **동작 플래그 8개가 더 CLR 속성이었다**(위 트랙 E 항목 참조). 트랙 E에서 `AutoLinkOnType`만 그런 줄
  알고 고쳤는데, 원본 `PublicAPI.Shipped.txt`와 멤버 단위로 맞춰 보니 `Allow*` 6개 + `MaxRecommendedImages`
  + `ShowFormattingMenu`도 같은 상태였다. **혼자만 그렇다는 판단의 근거가 틀렸던 것** — 표면 대조를
  눈대중이 아니라 기계적으로 했어야 했다.
- 나머지 차이는 전부 플랫폼 전용(Avalonia `OnPointerPressed`/`Render` 등 protected override)이거나
  문서화된 의도적 이탈(줌 훅 API, `InsertImage(Bitmap)`)로 확인했다.

#### 전수조사 — 이번 세션이 넣은 코드부터
가장 위험한 것은 방금 넣은 코드라는 7차 리뷰의 규칙대로 자기 변경분(24파일)을 먼저 봤다.
- **중첩 표 누산기가 표 사이에 새던 결함**(방금 넣은 RTF 표 모델의 결함). `_nestRow`/`_nestRows`/
  `_cellPending`이 어디서도 비워지지 않아, 끊긴 입력이 depth 2에 남긴 잔여를 **다음 표의 첫 셀이 주워
  갔다** — 중첩 표가 무관한 표로 순간이동한다. `FinalizeTable`에서 정리한다(최상위 표가 끝나면 그 안에
  중첩된 것은 이미 소비됐어야 하므로 남은 것은 전부 고아다). 반증 테스트로 재현 확인 후 수정.
- **찾기 바가 닫힐 때 캐럿이 죽던 결함**(트랙 C의 커버리지 구멍). `HideFindBar`가
  `Editor.Focus(FocusState.Programmatic)`를 부르는데 `RichEditor`는 탭 스톱이 아니고 포커스는 내부
  캔버스에 있다 — `FocusEditor()`를 만든 바로 그 이유다. 툴바만 고치고 찾기 바를 안 본 것이 구멍이었다.
- **찾기 바 버튼이 포커스를 가져가던 것** — "다음"을 클릭하면 포커스가 버튼으로 가서 이어지는 Enter가
  검색이 아니라 버튼을 다시 눌렀다. 검색 상자에 포커스가 남도록 했다(VS Code/브라우저 관례).
- **포매터 클래스 문서가 실제 동작과 어긋나 있었다.** XML 문서가 이제 패키지에 실리므로 소비자에게
  그대로 간다: RTF는 "simple tables"라고만 적혀 있었고(병합·셀 배경·중첩·인라인 표 마커·손실 목록 누락),
  HTML은 인라인 표 왕복을 언급하지 않았다. 둘 다 실제 동작으로 갱신했다. 곁들여 **깨진 문자 4곳**
  (`—`가 `??`로)도 고쳤다 — 이번 세션 이전부터 있던 것이지만 이제 배포된다.

### Added — 랜덤 편집열 퍼즈, 그리고 그것이 찾은 결함 3건 (2026-07-31)

정독·대조·왕복 테스트를 다 소진한 뒤 남은 유일한 검출 수단. 모델 레벨이라 헤드리스로 돈다
(`DocumentFuzzTests`). 24시드 × 300스텝, **매 스텝마다 구조 불변식 검사** + 네 포맷 전부 **2회 왕복**
멱등성 비교. 원본이 이 방법으로만 찾은 결함이 있었던 것이 도입 근거였고, 실제로 **3건**을 뱉었다.

#### Fixed
- **셀 안 여러 문단이 RTF 왕복마다 하나로 합쳐졌다.** 리더가 셀 안 `\par`를 문단 경계가 아니라 개행
  **내용**으로 읽었다 — 모델은 셀이 블록 컨테이너가 된 이래 다중 문단을 담는데 RTF 리더만 안 따라갔다.
  Word/HWP 붙여넣기에서도 셀의 문단 구조가 매번 소실되고 있었다는 뜻이다. 이제 셀 안 `\par`는
  진짜 문단을 만든다(깊이별 보류 목록에 쌓여 `TakeCell`이 순서대로 조립한다).
- **HTML `<br>` 구분자가 내용이 되어 왕복마다 늘었다.** 셀 문단 사이 `<br>`을 "문단이 하나라도 나왔으면"
  붙이고 있었는데, 앞 블록이 표·이미지·구분선이면 문단 경계는 **이미 존재**하므로 그 `<br>`은 구분자가
  아니라 다음 문단의 선두 개행으로 읽힌다. 조건을 "직전 블록이 문단일 때"로 바꿨다. 셀 안 중첩 표의
  `</table>` 뒤 정렬 개행도 같은 이유로 제거(`tight`) — `<td>`의 내용은 인라인으로 파싱된다.
- **들여쓴 목록 항목만 있는 문서를 HTML로 내보냈다 열면 파일 전체가 마크업 텍스트가 됐다**(실데이터
  손실). 목록 항목 하나를 Tab으로 들여쓰고 그게 문서 전부이면 내보내기가 `<ol><ol><li>…`를 만드는데,
  파서가 **직계 `<li>`만** 보고 있어 항목을 못 찾고 → 블록 0개 → 원시 텍스트 폴백이 문서 전체를
  literal HTML로 덤프했다. 리스트의 직계 자식이 또 리스트인 경우도 재귀한다.
  (`<li>` **안**의 하위 목록은 원래 처리되고 있었다 — 구멍은 `<li>` 없이 중첩된 형태였다.)

#### 알려진 미수정 1건
시드를 24 → 27 이상으로 넓히면 **병합 셀 안 공백 하나가 2회째 HTML 왕복에서 사라진다**. `MergeCells`가
피복 셀 텍스트를 공백으로 이어 붙이는데, 그 공백이 `<span>` 두 개 사이의 공백-only `#text` 노드가 되면
블록 워크가 버린다. **1.0에서 의도적으로 안 고쳤다** — HTML 공백 처리는 브라우저 복사·Word 붙여넣기·
`<pre>` 보존이 각각 따로 튜닝된 영역이고, 2회째 왕복의 공백 하나가 릴리스 당일 그 위험을 감수할
이유가 못 된다. 퍼즈 주석과 로드맵에 재현 조건을 남겼다.

### Fixed — 문단에 혼자 있는 인라인 표가 앞 문단을 삼킴 (2026-07-31, 4차 전수조사)

**HTML·RTF 양쪽에 같은 결함이 있었고, 하필 인라인 표의 가장 흔한 형태에서 터진다.**

인라인 표를 텍스트 줄에 되돌리는 마커(`{\*rinline}` / `data-are-inline`)는 "표 바로 앞에 닫힌 호스트
문단이 있다"고 가정하고 **직전 블록을 호스트로 집었다**. 그런데 표가 문단의 **첫 내용**일 때 —
즉 문단이 표 하나만 담는, "글자처럼 취급"의 통상적 형태 — 그 가정이 깨진다:
- RTF: 7차 리뷰가 넣은 규칙대로 그 경우 `\par`를 **일부러 안 쓴다**(쓰면 Word에서 표 위에 빈 줄이 생긴다).
- HTML: 파서가 `<table>`을 만나면 열려 있던 `<p>`를 **닫아 버린다**.

두 경우 모두 직전 블록은 **무관한 앞 문단**이고, 표가 거기 붙으면서 두 문단이 합쳐져 **앞 문단이
표와 같은 줄로 빨려 들어갔다**. 마커가 그 사실을 함께 실어 나르도록 고쳤다 —
RTF는 매개변수(`{\*rinline1}` = 호스트 문단을 연다), HTML은 `data-are-opens="1"`. 그때는 앞 문단을
건드리지 않고 새 호스트 문단을 만든다.

**왜 여태 안 걸렸나**: 기존 테스트와 실기 검증이 전부 "앞 텍스트 + 표 + 뒤 텍스트" 형태였다.
그 형태에서는 `\par`가 실제로 호스트를 닫으므로 가정이 성립한다. 축을 하나 바꾼 것만으로 드러났다.

### Fixed — 큰 줄간격에서 세로 이동이 깨지던 회귀 (2026-07-31, 3차 전수조사 + 사용자 리포트)

같은 날 캐럿을 baseline 기준·글자 높이로 바꾼 수정이 넣은 회귀다. 세로 이동은 "캐럿에서 넛지"로 다음
줄을 찾는데, **캐럿이 더 이상 줄 상자가 아니게 되면서** 넛지가 같은 줄 안에 떨어질 수 있게 됐다.
어긋남은 100%에서 0이고 줄간격에 비례해 벌어진다 — 그래서 기본값에서는 아무 증상이 없었다.

- **아래쪽**: 캐럿 바닥이 줄 상자 바닥보다 `0.2 × (줄상자 − 글자높이)` 만큼 위다(200%에서 ~3px,
  300%에서 ~6px). 셀 안 이동(`+2` 넛지)은 200%부터, 문단 간 이동(`+14`)은 300% 근처부터 걸린다.
- **위쪽**(사용자 리포트: "200/300%에서 맨 아랫줄에서 ↑ 하면 표 밖으로 나가버림"): 이쪽이 더 심하다 —
  캐럿 **top**이 줄 상자 top보다 `0.8 × (줄상자 − 글자높이)` 아래다(200%에서 ~13px, **300%에서 ~26px**).
  `−2` 넛지로는 같은 줄을 못 벗어나므로 셀 내 이동 단계가 실패하고, 그 다음 단계인 **셀 탈출**로 빠졌다.
- 그리기용 캐럿 높이와 **넘어가야 할 줄의 범위는 이제 다른 개념**이므로, `CaretInLayout`/
  `CaretToDocPoint`가 줄 상자의 `LineTop`/`LineBottom`을 함께 돌려주고 **이동 경로만** 그것을 쓴다.
  **핵심 계약**: 세로 이동 probe는 캐럿이 아니라 **줄**을 넘어야 한다 — 위는 `LineTop`, 아래는
  `LineBottom`. 100%에서는 두 값이 같아 기존 동작과 동일하다.
- 그대로 둔 것: 그리기·드롭 프리뷰·`ScrollCaretIntoView`는 캐럿의 시각 범위를 쓴다(캐럿을 보이게 하는
  것이 목적이다). `MovePage`는 뷰포트 크기 스텝이라 이 차이가 무의미하다.
> **실기 검증 완료(2026-07-31, 사람 확인)**: 100/200/300%에서 셀 안·문단 간 ↑↓, 맨 윗줄에서의 의도된
> 셀 탈출까지 모두 정상.

### Fixed — 툴바에 돋보기 아이콘 두 개 (2026-07-31, 사용자 리포트)

- **줌 콤보 앞의 아이콘을 제거했다.** Segoe Fluent의 "Zoom"(`0xE71E`)은 돋보기이고 찾기 버튼의
  "Search"(`0xE721`)도 돋보기라, **라벨 없는 돋보기 두 개가 같은 줄에 나란히** 놓여 같은 컨트롤로 읽혔다.
  콤보는 도움 없이도 자기를 설명한다 — 첫 항목이 "폭 맞춤"이고 나머지는 퍼센트이며 툴팁이 줌이라고
  말한다. 원본 툴바도 여기에 아이콘을 두지 않는다.
- 겸사겸사 아이콘 표 전체(33개)에서 중복 코드포인트를 훑었다. 4쌍이 더 있지만(이미지 삽입/교체,
  링크 복사/삽입, 삭제/링크 제거, 내보내기/이미지 저장) 전부 **서로 다른 메뉴**에 있고 라벨이 의미를
  나르므로 문제가 아니다 — 충돌은 라벨 없이 인접할 때만 생긴다.

### 1.0 직전 RTF 표 모델 테스트 보강 (2026-07-31)

동결 전 마지막 작업. 표 모델은 이 영역 최대 변경이고 붙여넣기 경로에 있는데, 축별 테스트가
**축을 겹친 조합**을 덮지 못하고 있었다. 테스트 91 → 101. **결함 1건 추가 발견·수정.**

#### Fixed
- **셀 안 중첩 표 앞 문단에 왕복마다 개행이 하나씩 늘던 결함.** 쓰는 쪽은 중첩 표에 하강하기 전
  `\par`로 앞 문단을 **반드시** 닫아야 하는데(안 닫으면 Word가 부모 텍스트를 첫 중첩 셀에 붙인다),
  읽는 쪽은 셀 안 `\par`를 개행 **내용**으로 읽었다. 그래서 구분자가 내용이 되고, 다음 왕복에서
  그 내용 + 새 구분자가 다시 쌓였다(`앞` → `앞`+LF → `앞`+LF+LF). 깊이가 바뀌는 지점에서 후행 개행
  하나를 구분자로 보고 떼어낸다. **한 번의 왕복으로는 안 보이는 종류라 2회 왕복 테스트가 잡았다** —
  HTML 인라인 표 뒤 공백 증식과 같은 계열이다.

#### 테스트
- **`RtfExportFlattensInlineTableInsideCell`이 공허해져 있었다**: 리라이트로 셀 안 인라인 표가 평탄화가
  아니라 실제 중첩이 됐는데, 텍스트 존재만 단언해서 **이름과 전제가 거짓인 채 통과**하고 있었다.
  `RtfExportNestsInlineTableInsideCell`로 바꾸고 실제 중첩을 단언한다.
- 축을 겹친 조합 신규: **3단 중첩**(`\itap3` — 깊이별 보류 목록과 "자기 `\itap` 재선언" 규칙은 3단부터
  상호작용한다) · **병합 셀이 중첩 표를 품은 경우**(기하 병합의 열 건너뛰기와 중첩 표의 `\itap` 복귀가
  같은 방출 루프를 고쳐 쓰므로 함께 걸어야 한다) · **가로+세로 동시 병합**(기하 인코딩과 플래그
  인코딩이 같은 슬롯에서 합의해야 하는 유일한 지점) · **종합 문서 2회 왕복**(위 결함을 잡은 것) ·
  **손상 입력 6종**(중첩 중간 절단, `\itap` 레벨 점프, 셀 없는 행, 셀 중간 깊이 하강, `\cellx` 없는
  `\cell`, 음수 경계) — 던지지도 멈추지도 않아야 한다.

### 상류 1.0 대조 — 트랙 E: 공개 API 표면 (2026-07-31)

빌드 0/0, 테스트 90/90, `dotnet pack` 정상.

#### API
- **`Formatters.RoundTripHarness`가 라이브러리에서 빠졌다**(⚠️ **breaking**). HTML 왕복 충실도를 재는
  개발 도구이지 소비자 API가 아니고, 공개 포매터 표면만 쓰므로 **데모 프로젝트로 옮겨 `internal`**로
  내렸다. 포트에서는 어디에서도 참조되지 않는 **죽은 공개 API**였다. 공개 API 제거는 breaking이라
  동결 이후에는 못 한다 — 그래서 지금 한다. 데모에서 `--roundtrip=<dir>`로 실행하면 그 디렉터리에
  `roundtrip-report.txt`를 쓴다(WinExe라 stdout이 갈 곳이 없다).
- **동작 플래그 9개가 전부 의존성 속성이 됐다** — `AutoLinkOnType`, `AllowImages`, `AllowTables`,
  `AllowRichPaste`, `AllowFindReplace`, `AllowLocalFileImages`, `AllowRemoteImagesOnPaste`,
  `MaxRecommendedImages`, `ShowFormattingMenu`. 전부 평범한 CLR 속성이라 **바인딩도 스타일도 안 됐다**
  (`IsReadOnly`만 DP였다). 원본은 이들을 모두 `StyledProperty`로 두고 있다. 이름도 기본값도 그대로이고
  DP 추가는 순수 가산이므로 기존 코드는 그대로 동작한다.
  *(2차 대조에서 잡았다 — 처음엔 `AutoLinkOnType`만 그런 줄 알고 그것만 고쳤는데, 공개 표면을 원본과
  멤버 단위로 다시 맞춰 보고서야 나머지 8개도 같은 상태임이 드러났다.)*

#### Added
- **`PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` 도입**(`Microsoft.CodeAnalysis.PublicApiAnalyzers`,
  분석기 전용 — 패키지에는 아무것도 추가되지 않는다). 공개 표면 **543개**를 Shipped 기준선으로 고정했고,
  이제 공개 API가 바뀌면 **빌드 경고 + diff**로 드러난다. 이번 세션의 델타는 Unshipped에 그대로 보인다:
  `FocusEditor()`·`AutoLinkOnTypeProperty` 추가, `RoundTripHarness` 제거.
- **패키지에 XML 문서 포함**(`GenerateDocumentationFile`). 라이브러리에 XML 주석이 빼곡한데 소비자
  IntelliSense에는 하나도 안 닿고 있었다. 켜자 **끊어진 `cref` 4건**이 드러나 함께 고쳤다
  (`ConditionalWeakTable`·`RichEditor.LoadHtml`/`InsertHtml`/`LoadHtmlAsync` — 네임스페이스 한정 누락).
  CS1591("XML 주석 없음")은 끈 상태로 둔다.

### 상류 1.0 대조 — 트랙 D 마무리: RTF 표 모델 (2026-07-31)

빌드 0/0, 테스트 **90/90**(신규 4). 위 실기 검증에서 나온 잔여 2건은 같은 코드를 건드리므로 함께 했다.

#### Changed — 파서의 셀이 블록을 담는다
`_tableRows`가 `List<List<Paragraph>>`(문단 전용)에서 **`List<List<TableCell>>`**로 바뀌었다. 모델은
진작부터 셀이 블록 컨테이너였는데 RTF 리더만 문단 하나로 보고 있었고, 그래서 중첩 표를 담을 자리가
없었다. 아래 두 항목의 공통 선행 조건이다.

#### Fixed
- **셀 안 중첩 표가 양방향으로 평탄화되던 것**(쓰기 `WriteNestedTableAsText` → 탭/`\line`, 읽기
  `\nestcell` → 탭 바이트). 이제 Word가 하는 그대로 **`\itapN` + `\nestcell`/`\nestrow`**로 쓰고
  같은 방식으로 읽는다 — 깊이는 `\itap`으로 구분하므로 **임의 깊이**를 지원한다(모든 `\nestcell`은
  겉보기가 같아서 이것 말고는 레벨을 알 방법이 없다). 부모 셀의 문단은 중첩 표 **앞/뒤 원래 순서**를
  유지한다(깊이별 보류 목록 `_cellPending` — 없으면 중첩 셀이 부모 텍스트를 가져간다). 쓰기 쪽 필수
  규칙 2가지: 중첩 표에 하강하기 전에 `\par`로 앞 문단을 **닫아야** 하고(안 닫으면 Word가 부모 텍스트를
  첫 중첩 셀에 붙인다), 중첩 표가 끝나면 셀이 **자기 `\itap`을 다시 선언**해야 한다(안 하면 뒤 문단이
  이미 닫힌 표에 들어가 Word가 통째로 버린다). **남은 손실**: 중첩 표의 열 너비는 무시 가능 그룹
  `{\*\nesttableprops}` 안에 있어 기본값으로 들어온다.
- **HWP에서 가로 셀 병합이 풀리던 것**(사용자 캡처). 병합을 Word식 **플래그** 형태(열마다 `\cellx` +
  앵커 `\clmgf`/피복 `\clmrg`)로 쓰고 있었는데 HWP가 이 형태를 안 따른다. 이제 **기하 형태**로 쓴다 —
  병합 구간은 **셀 하나**이고 그 `\cellx`가 병합 오른쪽 끝에 앉는다(피복 열은 폭만 보태고 자기 `\cellx`를
  갖지 않는다). 이것이 기본 RTF 표 모델이고 Word도 정확히 읽으므로 한 형태로 양쪽을 만족시킨다.
  세로 병합은 기하로 표현할 방법이 없어(모든 행이 그 열에 셀을 가진다) `\clvmgf`/`\clvmrg` 플래그를
  유지한다 — HWP도 이쪽은 정상이었다.
  - 리더는 이제 **행별 `\cellx` 경계의 합집합**으로 열 격자를 만들고, 각 셀이 삼킨 격자 열 수로 colspan을
    역산한다. **Word의 플래그 형태도 계속 읽는다**(`\clmrg` 셀은 앞 셀을 넓히는 것으로 접는다) —
    Word 붙여넣기가 깨지면 안 되므로.
  - **문서화된 한계**: 모든 행이 똑같이 병합된 표는 파일 어디에도 원래 격자가 안 남아 한 열로 접힌다.
    렌더링은 동일하지만(병합 셀의 총 폭이 같다) 모델은 다르므로, 나중에 결함처럼 보이지 않도록
    테스트로 고정해 뒀다.
> **실기 검증 완료(2026-07-31, 사람 확인)**: HWP 가로 병합 **해결**, Word 붙여넣기 회귀 없음(병합·중첩
> 표·세로 병합·셀 배경 전부 정상), Word 문서 → 에디터 가져오기에서 중첩 표가 격자로 들어옴(열 너비만
> 기본값 = 위에 적은 남은 손실), 자체 RTF 왕복 정상.
>
> **알려진 한계 — HWP는 RTF 중첩 표를 구현하지 않는다.** HWP에서는 중첩 셀이 구분자 없이 이어붙는다
> (`중첩1중첩2`) — `\nestcell`을 모르는 제어어로 버리고 그 텍스트를 부모 셀의 이어지는 글로 읽는다는
> 뜻이다. 우리가 쓰는 구성은 **원본 AvaloniaRichEditor 1.0과 바이트 단위로 동일**하므로 원본도 HWP에서
> 같은 결과가 된다. Word는 정확히 읽는다. `\nestcell` 앞에 `\tab`을 넣으면 HWP 가독성은 오르지만 Word의
> 중첩 셀에도 탭이 들어가므로, Word 출력을 오염시키지 않는 쪽을 택했다.

### Fixed — 캐럿이 글자보다 아래에 그려짐 (2026-07-31, 사용자 리포트 · 실기 검증 완료)

- **캐럿을 줄 상자가 아니라 그 줄의 baseline 기준으로 놓는다.** 문자 region의 `LayoutBounds`는 글리프가
  아니라 **줄 상자 전체**를 덮고, 줄 상자는 언제나 글자보다 크다 — 자연 간격에서도 글꼴의 line gap만큼,
  커스텀 줄간격에서는 훨씬 더. 캐럿이 그 상자를 그대로 쓰니 글자보다 아래로 삐져나왔다.
  **규칙이 두 개였던 것이 근본 원인**이다: 커스텀 간격은 우리가 지정한 baseline(`LineSpacingBaseline`)에
  글자가 앉는데 캐럿 코드는 "줄이 텍스트의 1.5배 이상 높으면 큰 인라인 오브젝트가 있는 줄"로 보고 **줄
  상자 바닥**에 정렬했고(200%에서 뚜렷, 그 위로는 더 심함), 자연 간격은 **줄 상자 전체**를 썼다(항상
  살짝 낮음). 이제 **모든 간격에서 하나의 규칙** — 그 줄의 baseline 기준, 높이는 해당 런의 글자 높이.
  공식이 baseline과 글꼴 크기만 참조하고 둘 다 줄간격에 따라 변하지 않으므로 간격과 무관하게 성립한다.
  (리스트 마커가 2026-07-16에 받은 것과 **동일한 수정**이다.)
  - 예외 하나: 큰 인라인 오브젝트(이미지/표)가 있는 줄은 종전대로 바닥 정렬한다. 인라인 오브젝트의
    baseline이 곧 자기 **바닥 edge**라(`InlineObjects.Baseline => Size.Height`) 이미 baseline 정렬과 같다.
- **빈 줄의 캐럿이 줄 상자 높이만큼 길었다** — 빈 문단이나 Shift+Enter 직후의 새 줄은 잴 글리프가 없어
  높이가 줄 상자 전체였다(200%면 글자 두 배). 같은 규칙으로 통일했다 — 빈 줄과 글자 있는 줄의 캐럿이
  달라 보일 이유가 없다.
- baseline 비율을 상수 **`RichEditor.BaselineFraction`** 하나로 통일했다. DirectWrite에 "baseline을 여기
  둬라"라고 말할 때와 그 baseline에서 글리프 상단을 역산할 때 **같은 값이어야 한다** — 하드코딩된 두
  개가 어긋나는 것이 이 결함의 뿌리다.
> **실기 검증 완료(2026-07-31, 사람 확인)**: 100/200/300%, 빈 줄·Shift+Enter 직후, 제목(h1~h3),
> 이미지·표가 있는 줄, 표 셀 안 캐럿 모두 정상.

### 상류 1.0 대조 — 트랙 C: 툴바 포커스 (2026-07-31)

빌드 0/0, 테스트 82/82. **사용자 실기 리포트로 확인된 결함**("툴바 버튼 누른 뒤 타이핑이 안 된다").

#### Fixed
- **툴바가 포커스를 빼앗아 캐럿이 사라지고 키보드가 죽었다.** 캐럿은 에디터 캔버스가 포커스를 가진
  동안에만 그려지는데, 버튼을 누르면 포커스가 버튼으로 넘어가고 아무도 돌려주지 않았다. 명령 자체는
  기억된 캐럿 위치로 실행되므로 **버튼은 동작하는 것처럼 보이면서 다음 키 입력이 사라졌다** — 이래서
  눈에 안 띄었다. 두 가지 규칙으로 정리했다.
  - **(a) 스트립의 어떤 것도 클릭으로 포커스를 가져가지 않는다** — `AllowFocusOnInteraction=false`.
    버튼/토글 팩토리에 적용하고, 호스트가 `LeadingItems`/`TrailingItems`로 넣은 버튼까지 빌드 시
    논리 트리를 훑어 동일하게 적용한다(같은 스트립에 앉아 있으니 똑같이 캐럿을 숨긴다).
  - **(b) 열려 있는 동안 포커스가 필요한 것은 닫힐 때 돌려준다** — 콤보는 드롭다운이 열려 있어야
    목록 조작이 되므로 버튼처럼 거부할 수 없다. `DropDownClosed`에서 반환한다(`SelectionChanged`가
    아니라 — 그러면 열린 목록을 화살표로 훑는 도중에 포커스를 뺏는다). 피커 팝업 4종(표 그리드·
    글머리표/번호 스타일·줄간격 프리셋·색상 팔레트)은 `Closed`에서 반환한다.
  - 줄간격 입력 상자는 진짜 편집 필드라 의도적으로 포커스를 받는다 — **Enter가 "완료"** 이므로
    적용 후 포커스를 돌려준다. 커밋은 언두 체크포인트를 쌓으므로 Enter가 유발하는 `LostFocus`의
    중복 커밋을 가드해 **Enter 1회 = 언두 1스텝**을 유지한다.
- 새 공개 API **`RichEditor.FocusEditor()`** — 캐럿을 움직이지 않고 편집면에 포커스를 돌려준다.
  포커스는 내부 캔버스에 있고 컨트롤 자신은 탭 스톱이 아니라서, 호스트나 툴바가 컨트롤에 `Focus()`를
  불러도 닿지 않는다. `FocusDocumentEnd()`도 이제 이걸 쓴다.

### 상류 1.0 대조 — 트랙 B: 컨트롤 계층 (2026-07-31)

빌드 0/0, 테스트 82/82(변동 없음 — **전부 컨트롤 레벨이라 헤드리스 테스트가 불가능하다**. DP 정적
생성자가 WinUI 런타임을 요구한다. 실기 검증 필요 항목은 로드맵 참조).

#### Fixed
- **목록을 켜면 같은 문단의 인라인 표·이미지가 문서에서 떨어져 나갔다.** 문단을 목록 항목으로 바꾸는
  경로는 하드라인마다 새 문단을 만들어 문서에 스플라이스하는데, non-Run 인라인을 **복제**해 넣고
  원본은 버려지는 소스 문단에 남겼다. 인라인 표 셀 안에 있던 캐럿은 그 순간 **문서에 없는 서브트리**를
  가리키게 되어 타이핑이 화면 어디에도 나타나지 않았고, 인라인 이미지의 선택 크롬은 이미 복사본으로
  교체된 객체를 추적했다. 개행을 걸친 Run만 새 객체가 되어야 한다 — 나머지는 전부 **이동**하므로
  토글 전후가 같은 인스턴스다. (원본에서 랜덤 편집열 퍼즈만이 찾아낸 결함.)
- **표 셀 안의 블록 이미지를 선택·리사이즈·삭제할 수 없었다.** 최상위 블록 이미지는 블록 레이아웃
  맵에서 rect를 얻는데 셀 이미지는 그 맵에 아예 없어서, 깊이와 무관하게 클릭 자체가 닿지 않았다
  (원본은 "핸들이 안 먹는다"였고 포트는 "핸들이 없다"였다). 그려질 때 rect를 적재하는 `_cellImageRects`
  레지스트리를 신설했다. 셀은 그림을 셀 폭에 맞춰 **축소해서** 그리므로(`CellImageSize`) 선언 Width는
  보통 몇 배 크다 — 그래서 레지스트리는 **그려진 크기**를 들고, 드래그는 그 기준으로 계산한다(선언
  크기에서 출발하면 웬만한 드래그가 전부 같은 축소 폭으로 클램프되는 구간에 떨어진다). 리사이즈는
  열·행 드래그와 동일하게 조상 표 체인을 evict하므로 행이 **같은 프레임에** 자란다.
- **인라인 표 셀 안의 블록은 삭제되지 않았다**(`RemoveBlockFromCells`). 문서 최상위와 **블록** 표의
  셀만 재귀해서, 인라인 표 셀에 사는 이미지·구분선·중첩 표는 끝내 못 찾았다 — Delete가 언두
  체크포인트를 쌓고 선택만 풀고 블록은 화면에 남았다. 위 셀 이미지 수정이 이 경로를 실제로 도달
  가능하게 만들었으므로 함께 고쳤다. (`TextRange.BlockHolds`가 2026-07-25에 고친 것과 같은 계열의
  **다른 사본**이다.)
- **`GetPlainText()`가 중첩 표·인라인 표의 텍스트를 전부 누락**했다 — 최상위 문단과 셀의 자기 문단까지
  딱 한 단계만 내려갔다. `RichEditorAutomationPeer`가 이 메서드를 읽으므로 **보조기술에도 안 보였다.**
  이미 있던 재귀 워커(`AllParagraphs`)를 쓴다.
- **`GetImageCount()`도 한 단계만 세어** 중첩·인라인 표 안의 이미지를 빠뜨렸고, 그래서
  `RecommendedImageLimitExceeded` 소프트 리밋 경고가 늦게 뜨거나 아예 안 떴다.
- **`RichEditorLocalization.Language`를 UI 스레드 밖에서 바꾸면 붙어 있는 툴바가 죽었다.**
  `LanguageChanged`는 `Language`를 세팅한 **그 스레드에서 동기 발생**하는데 핸들러가 곧바로 XAML을
  건드렸다 → 설정 로드·로케일 감시 같은 백그라운드 경로에서 `RPC_E_WRONG_THREAD`. 이제
  `DispatcherQueue`로 넘긴다.

### 상류 1.0 대조 — 트랙 A: 포매터 계층 (2026-07-31)

원본 AvaloniaRichEditor가 **1.0.0**을 냈다. 새 기능이 아니라 검증 깊이로 끊은 릴리스다(상호작용·렌더
픽셀 테스트 인프라, 전 소스 감사 라운드 3~7, 랜덤 편집열 퍼즈, Word/HWP/브라우저 실물 검증, 유닛
355→528). 포트와의 양방향 수렴은 2026-07-25(원본 `bcc12e4`)까지였으므로 그 뒤 약 35커밋이 미대조
구간이었다. 그 구간의 결함을 포트 소스에 1:1로 확인해 **포매터 계층부터** 맞춘다. 빌드 0/0,
테스트 **82/82**(신규 8). 신규 테스트는 **수정 전 6건 FAIL + DoS 2건은 테스트 프로세스 정지**를
실제로 확인한 뒤 넣었다.

#### Security / robustness
포매터는 전부 붙여넣기·파일 선택으로 도달 가능한 **신뢰 불가 입력 경로**다. 정상 왕복만 테스트하면
안 된다는 것이 상류의 교훈이고, 아래 3건은 그 각도에서만 보인다.
- **`<td colspan="100000000">` 하나로 메모리 고갈**(`HtmlDocumentFormatter`). 점유 그리드를 속성값
  그대로 키우고 `TableBlock`까지 그 크기로 할당했다. `rowspan`은 실제 존재하는 행 수로 자연히 막혀
  있었지만 `colspan`엔 상한이 아예 없어서, 조작된 — 혹은 그냥 버그 있는 — 표 하나를 붙여넣으면
  애플리케이션이 멈췄다. 실제 문서와는 무관한 높이(1000)로 상한을 뒀다.
- **`.flow`/JSON이 선언한 표 크기를 셀 하나 읽기 전에 할당**(`DocumentSerializer`). 생성자에 파일의
  `Rows`/`Columns`를 그대로 넘겼는데, 거기서 만든 것은 **바로 다음 세 줄에서 전부 버려지고** 실제
  존재하는 셀로 재구성된다 — 즉 공격자 제어 숫자로 사이즈를 잡는 것은 처음부터 순수 낭비였다.
  1×1에서 시작하도록 바꾸고, 짧은 행을 패딩하는 선언 열 수에도 같은 상한을 걸었다.
- **`Blocks` 안의 JSON `null`이 `NullReferenceException`**을 던졌다(건너뛰는 대신). `Cells`·`Inlines`의
  null 항목도 마찬가지로 무방비였다.

#### Changed
- **손상된 문서를 빈 문서로 읽지 않고 보고한다.** `DocumentPackage.Load`는 손상 패키지를 삼켜 빈
  문서를 돌려줬고, `DocumentSerializer.Deserialize`는 3차 리뷰(2026-07-16)에서 "문서화된 계약"에
  맞춰 **같은 방향으로** 맞춰 놨었다. 삼키는 쪽이 데이터를 잃는 쪽이다 — 호스트가 "이 파일은 비어
  있었다"와 "이 파일은 손상됐다"를 구분할 수 없고, 사용자에게 빈 편집기를 보여준 다음 저장 한 번이
  복구 가능한 파일을 아무것도 아닌 것으로 덮어쓴다. 이제 둘 다 던진다(읽을 수 없는 패키지는
  `InvalidDataException`, 잘못된 JSON은 `JsonException`). `LoadJson`/`LoadJsonAsync`/`LoadPackageAsync`
  문서에 명시했다. `document.json`이 아예 없는 패키지는 여전히 오류가 아니며(이미지만 담은 컨테이너도
  열린다), 내장 툴바 Import는 원래 예외를 잡고 있었으므로 **잘못된 파일이 열려 있던 문서를 빈 문서로
  갈아치우는 대신 그대로 둔다**. ⚠️ **동작 변경**: 이 세 API를 쓰는 호스트는 예외를 처리해야 한다.

#### Fixed — RTF 가져오기 (Word 일상 출력에서 발생)
- **무시 가능 그룹 앞의 텍스트가 사라졌다.** pending run을 그룹의 **닫는 중괄호**에서 flush하는데,
  그 시점엔 건너뛰는 destination이 아직 살아 있어 `FlushRun`이 통째로 버렸다. Word는 이런 그룹을
  일상적으로 쓴다(북마크·필드·중첩 표의 `{\*\nesttableprops …}`) — 즉 **평범한 Word 문서가 텍스트가
  빠진 채로** 들어왔다. 이제 그룹에 하강하기 **전에** run을 커밋한다.
- **건너뛰는 그룹 안에서 표 행이 재시작됐다.** `\trowd`/`\cell`/`\row`/`\cellx`가 destination과
  무관하게 동작했고, Word는 중첩 표의 행 정의를 `{\*\nesttableprops \trowd …\nestrow}`에 넣는다 —
  그래서 절반쯤 쌓인 부모 셀의 텍스트가 행 중간에 폐기됐다. 네 개 모두 destination을 가드한다.
- **`{\nonesttables …}` 폴백 사본을 건너뛴다.** 중첩을 못 하는 리더용 평탄화 사본이라 우리에겐
  중복이다 — 그 `\par`가 부모 셀에 빈 줄로 남고 텍스트가 두 번 들어왔다.

## [0.9.1] - 2026-07-25

**0.9.0을 쓰지 말고 이 버전을 쓰세요.** 0.9.0의 유일한 실질 차이는 잘못 올라간 SDK 하한선입니다.

### Fixed
- **최소 요구 `Microsoft.WindowsAppSDK.WinUI` 2.3.2 → 2.2.1로 환원.** 0.9.0이 요구한 2.3.2는
  메타패키지 `Microsoft.WindowsAppSDK`가 실제로 주는 WinUI보다 **높습니다**(2.3.1 → WinUI 2.3.0,
  2.2.0 → WinUI 2.2.1). 그래서 **프레임워크 의존 앱**이 자기가 배포하는 런타임보다 높은 WinUI로
  컴파일되어 어긋났고, self-contained로 바꾸거나 편집기를 되돌리는 수밖에 없었습니다.
  라이브러리는 2.3에서 추가된 API를 하나도 쓰지 않습니다 — **2.2.1에서 솔루션 빌드 0/0, 테스트 74/74,
  AOT self-contained 게시·실행·렌더 모두 확인**했습니다.
  - 하한선에 적용되는 규칙 두 가지를 csproj 주석에 못박았습니다: ① 실제로 빌드·테스트·AOT가 통과하는
    **가장 낮은** 버전일 것 ② 소비자 메타패키지가 주는 WinUI 버전을 **넘지 말 것**.
- **문서**: 소비자 앱이 self-contained 배포를 한다면 **메타패키지**를 참조해야 한다는 점을 README에
  명시. 오직 메타패키지만 `Microsoft.WindowsAppSDK.Runtime`(재배포 런타임)을 가져옵니다 — 라이브러리가
  분할 컴포넌트를 쓰는 건 컴파일 표면만 필요한 라이브러리의 사정이고, 앱이 그걸 따라 하면 번들할
  런타임이 없어집니다.

## [0.9.0] - 2026-07-25

> ⚠️ **이 버전은 사용하지 마세요.** WindowsAppSDK 하한선을 2.3.2로 잘못 올려 프레임워크 의존 앱을
> 깨뜨립니다. [0.9.1](#091---2026-07-25)에서 수정됐고, 아래 내용은 0.9.1에도 모두 포함됩니다.

원본 `AvaloniaRichEditor`와의 **양방향 수렴**(파리티 갭 분석 → 원본→포트 소스 호환 → 포트→원본 역이식 →
원본 감사 결과의 역방향 스윕)과 **HWP/Word 상호운용 교정**이 이 릴리스의 축이다. 6·7차 전수 리뷰까지
포함해 결함 **20건**을 고쳤고, 테스트는 67 → **74**로 늘었다.

### ⚠️ 업그레이드 전 확인 (breaking / 동작 변경)
- **최소 요구 `Microsoft.WindowsAppSDK.WinUI` 2.2.1 → 2.3.2.** 2.2.x에 머무는 앱은 설치할 수 없다.
- **동기 `ParseHtml`/`LoadHtml`/`InsertHtml`이 원격(`http`) 이미지를 더 이상 받지 않는다.** UI를 최대 5초
  멈추던 동기 다운로드를 제거했다. 원격 이미지가 필요하면 `ParseHtmlAsync`/`LoadHtmlAsync`를 쓸 것.
- **`AllowRemoteImagesOnPaste`가 붙여넣기 전용이 아니다** — `LoadHtml`/`LoadHtmlAsync`/`InsertHtml`까지
  관장한다. 끈 호스트가 `LoadHtmlAsync`에서 실제로 원격 요청을 보내던 버그의 수정이다.
- **목록 토글 해제가 `ListLevel`/`ListMarker`까지 지운다.** 중첩 목록을 끄면 들여쓰기도 함께 사라진다
  (종전에는 마커만 사라지고 레벨×20px 들여쓰기가 유령처럼 남았다).
- **RTF 출력 형태가 크게 바뀌었다** — 행 정의 2회 방출, 모든 문단에 명시 정렬(`\ql` 포함), 인라인 표를
  실제 `\trowd` 행으로 승격. HWP/Word가 표와 정렬을 제대로 읽도록 하기 위한 변경이다.
- **로컬라이제이션 키 `ListNone` 제거**, 툴바 ▾ 드롭다운과 우클릭 메뉴의 "없음" 항목 제거.
  이 키에 오버라이드를 등록한 호스트는 등록을 지워도 된다.

### 주요 추가
- 읽기 전용 뷰어 캐럿 옵트인 `ShowCaretWhenReadOnly`(기본 off, 깜빡이지 않음).
- 원본 API 소스 호환 계층: `SetFontFamily`/`InsertImageBytes`/`PasteFromClipboardAsync` 별칭,
  `FocusDocumentEnd()`, `InsertInlineTable(r,c)`, `InsertImageFromFileAsync(nint)`,
  `RichEditorView.ShowStatusBar`/`ZoomFactor`.

*아래는 이 릴리스에 들어간 작업의 시간순 상세다.*

### ⚠️ 최소 요구 사항 상향 (2026-07-25)
- **`Microsoft.WindowsAppSDK.WinUI` 최소 버전 2.2.1 → 2.3.2** (데모/호스트 쪽 메타패키지는
  `Microsoft.WindowsAppSDK` 2.3.1). 라이브러리의 `PackageReference` 버전이 곧 **NuGet 소비자의 최소
  요구치**이므로, 아직 WindowsAppSDK 2.2.x에 머무는 앱은 이 버전을 설치할 수 없다 — 소비자 관점에서
  breaking이다.
- **PRI 워크어라운드 제거**(`_StripStaleWinAppSdkRuntimePri`, 데모 csproj): WinUI 1.8.x 런타임 PRI를
  self-contained 병합 전에 걷어내 PRI277(`TextCommandDescriptionCopy`)을 피하던 빌드 타깃이다.
  Win2D 1.4.0은 **여전히** `Microsoft.WindowsAppSDK.WinUI 1.8.260204000` 하한선을 선언하지만(nuspec),
  이제 양쪽 프로젝트가 2.3.x를 명시 참조하므로 NuGet이 그 위로 해석해 1.8 PRI가 병합에 도달하지 않는다.
  타깃을 끈 채 AOT self-contained 게시가 성공하는 것을 **실측 확인**한 뒤 삭제했다.
  ⚠ 라이브러리의 명시 `Microsoft.WindowsAppSDK.WinUI` 참조는 그 하한선을 들어올리는 load-bearing
  참조다 — 빼면 1.8이 해석되어 충돌이 되살아난다.

4차 전수 리뷰(2026-07-23)에서 확인된 결함 10건 수정 + 찾기 바 UX 1건, 그리고 6차 전수 리뷰(2026-07-25)
결함 4건 (맨 아래 절).

### Fixed
- **표 셀 안에서 목록(글머리표·번호)이 사라지던 문제** (실기 리포트, 두 겹):
  - **렌더러가 셀 안 목록 마커를 아예 그리지 않았다**: `DrawListMarkers` 호출부가 최상위 렌더
    경로 한 곳뿐이라, 셀 문단은 `ListType`을 모델에 유지해도 평문으로 그려졌다(붙여넣기든 토글이든
    셀에선 목록이 안 보임). `DrawCellBlockList`가 마커를 그리도록 하고, 마커 거터를 렌더·히트테스트·
    캐럿·측정 **네 walk에 동일하게** 적용하는 `CellParaLeft`를 도입(규칙 #1 — 안 맞추면 글자와
    캐럿/클릭이 어긋난다). 번호 매기기는 셀 단위로 센다.
  - **셀 안에서 툴바 목록 토글이 무산**: 캐럿만 셀에 있고 선택이 없으면 `SetListType`이
    `SplitByNewlines`를 타는데, 그건 `Document.Blocks` 문단만 처리해 셀 문단에선 no-op였다.
    셀 문단은 제자리에서 `ListType`만 토글한다.
- **여러 문단을 표 셀에 붙여넣으면 서식이 평문으로 납작해지던 문제** (`InsertDocumentAtCaret`):
  셀 안 캐럿에 대해 `PlainTextOf` 폴백을 타, 목록·제목·정렬·글자 서식이 전부 사라졌다. 컨테이너를
  규칙 #3대로 일반화(`Document.Blocks` 또는 `TableCell.Blocks`)해 최상위와 같은 블록 스플라이스를
  쓴다. 빈 문단(빈 셀/빈 줄)에 붙여넣을 때는 첫 붙여넣기 문단의 서식을 승계하고, 내용이 있으면
  대상 문단 서식을 유지한다(`Paragraph.CopyFormatFrom` — `CloneFormat`/`Clone`과 필드 목록 통일).
- **↑/↓가 표를 통째로 건너뛰던 문제** (실기 리포트, 여러 겹의 원인):
  - 세로 이동의 ±14px 넛지가 **문단 `MarginBottom` + 표 `MarginTop`** 여백에 떨어지면
    `GetPositionFromPoint`가 표를 인정하지 않고(블록 안쪽을 요구), 폴백
    `AdjacentTopLevelParagraph`는 설계상 표를 건너뛴다 → 새 `EnterAdjacentTable`로 인접 형제
    표에 확정 진입한다. 포인트 히트 **이전에** 평가해야 한다 — 프로브가 셀 안에 떨어지면
    `HitTestBlockList`가 어느 블록에도 안 맞을 때 **그 셀의 마지막 문단 끝**을 폴백으로 주는데,
    그것도 "다른 문단"이라 포인트 경로가 덥석 채택했다(셀이 중첩 표·이미지로 시작하면 상시 발생).
  - **중첩 표**는 별개 경로였다. `VerticalInCell` 단계 (a)는 결과가 **같은 셀**일 것을 요구하는데
    중첩 표 안 문단은 *중첩* 셀을 보고하므로 거부되고, 단계 (b)가 **바깥 표의 다음 행**으로
    점프해 중첩 표를 건너뛰었다 → 단계 (a2) 신설. 또한 단계 (a)는 프로브가 중첩 표의 열 범위를
    벗어났을 때(표 밖에서 물려받은 `_desiredCaretX`) 폴백인 "같은 셀의 마지막 문단 끝"을
    **수락**해버렸다 → 다음 형제가 표면 같은 문단 안 이동만 허용하도록 가드 추가.
  - 중첩 표는 `_tableRects`(최상위)·`_inlineTableRects`(인라인) 어디에도 기록되지 않아
    `TableDocRect`가 항상 null이었고, 그 결과 "표 밖으로 나가기"(단계 (c))가 중첩 표에서는
    **한 번도 동작한 적이 없었다** → 모든 표의 원점을 `_tableOrigins`에 기록해 폴백으로 쓴다.
  - 진입 위치는 셀의 **첫 문단 첫 줄**(↓) / **마지막 문단 마지막 줄**(↑)로 확정하되 그 안에서
    `_desiredCaretX`를 적용해, 다른 모든 세로 이동과 같은 열 보존 규칙을 따른다.
- **Shift+Enter 직후 캐럿이 이전 줄에 그려지던 문제**: 문단 끝에서 소프트 개행을 넣으면 캐럿이
  새 빈 줄이 아니라 **원래 줄 앞**에 보이다가, 글자를 치면 제자리를 찾았다. `CaretInLayout`이
  `offset == len`일 때 `len-1`을 프로브하는데 그게 `\n`이면 그 글리프 region이 **이전 줄 끝**에
  있어 X는 새 줄, Y만 이전 줄에서 왔다. 이 경우 `GetCaretPosition`의 Y와 자연 빈 줄 높이를 쓴다.
- **표 안에서 서식·리사이즈·바꾸기가 행 높이에 반영되지 않던 문제** (여러 경로, 같은 원인):
  행 높이 캐시 `_tableRowHeights`를 버리는 경로가 `AfterEdit`뿐이었고 `RelayoutToViewport`는
  이를 건드리지 않는다. IME 합성·`ReplaceNext`/`ReplaceAll`(아래)에 더해, `AfterFormat`도
  캐럿 조상 표만 무효화해 **표 위에서 아래로 드래그한 선택에 서식을 적용하면**(캐럿이 표 밖에서
  끝남) 그 표의 행 높이가 옛 서식 기준으로 남았다 — 선택이 있을 때는 캐시를 전부 비운다.
- **리스트 변환 시 문단 서식 소실** (`SplitByNewlines`): 여러 줄 문단을 리스트 항목으로 나눌 때
  서식을 5개 필드만 수기 복사해 **ListMarker(◦/"a)" 글리프)·LineSpacing/LineHeight·MarginRight·
  IsQuote·HeadingLevel**을 잃었다. Enter 분할용으로 도입된 `CloneFormat()`으로 교체.
- **리사이즈 핸들을 클릭만 해도 문서가 "수정됨"이 되던 문제**: 표 열/행 경계와 이미지 핸들이
  press 시점에 언두를 밀어, 드래그 없이 누르고 떼기만 해도 문서 전체가 복제되고 `IsModified`가
  뒤집혀 호스트의 저장 경고가 떴다. 첫 실제 이동 때 스냅샷을 찍도록 지연(`PushDragUndoOnce`).
- **언두 메모리 예산이 인라인 표 내용을 세지 않던 문제** (`UndoManager.EstimateBytes`): 문단
  인라인의 `InlineTable`을 고정 크기로 치고 셀을 걸지 않아, 내용이 인라인 표에 몰린 문서는
  스냅샷이 작아 보이고 64MB 예산이 발동하지 않았다(예산을 도입한 바로 그 상황).
- **HTML `font-weight` 오탐**: 선언의 값이 아니라 style 문자열 **전체**에서 부분문자열을 찾아
  `font-weight:normal;width:600px`가 굵게 파싱됐다. 값만 보도록 스코프하고, 고정 목록 대신
  수치 비교(≥600)로 바꿔 650 같은 값도 처리한다.
- **RTF 표 셀 안의 그림이 셀을 탈출하던 문제** (`FinalizePict`): 64px 이상 그림을 무조건 문서
  본문에 넣어, Word/HWP 표에 든 사진이 셀 밖으로 순서까지 어긋나 나왔다. 행이 열려 있는 동안은
  셀의 문단에 인라인 이미지로 유지한다.
- **인라인 표 셀이 정규화되지 않던 문제** (`NormalizeBlockList`): 블록 표 셀만 재귀해 규칙 #5가
  인라인 표 셀에 적용되지 않았다. 역직렬화는 셀 블록이 0개일 때만 문단을 넣으므로, 인라인 표
  셀이 이미지 하나뿐인 `.flow`는 문단 없는 셀로 남아 **캐럿이 들어갈 수 없었다.**
- **IME 합성이 편집 후처리를 건너뛰던 문제** (`RichEditor.Ime.cs`): `OnImeTextUpdating`이
  `RelayoutToViewport` + `RestartBlink`만 수행해, 한글 입력은 영문(`CharacterReceived` →
  `InsertText` → `AfterEdit`)과 달리 ① 조상 표의 캐시된 행 높이를 버리지 않고(→ **셀에 한글을
  칠 때 행이 자라지 않음**; `RelayoutToViewport`는 `_tableRowHeights`를 건드리지 않는다)
  ② 캐럿을 화면 안으로 스크롤하지 않으며 ③ `TextChanged`/`SelectionChanged`를 다음 무관한
  입력까지 미뤘다. 이제 `AfterEdit()`를 호출한다(내부 `SyncIme`는 `_inTextUpdating` 가드로 no-op).
- **찾아 바꾸기 후 표 행 높이가 갱신되지 않던 문제** (`RichEditor.FindReplace.cs`): 같은
  원인의 다른 경로. `ReplaceNext`는 `InvalidateCaretTableMeasure()`를, `ReplaceAll`은 치환이
  여러 표에 흩어질 수 있어 `_tableRowHeights.Clear()`를 수행한다.
- **`CanvasTextFormat` 네이티브 누수** (`RichEditor.cs` `CreateLayout`): 컨트롤에서 가장 자주
  실행되는 경로가 `CanvasTextFormat`을 만들고 해제하지 않았다. `ParagraphHeight`/`ParagraphLines`가
  전이 **레이아웃**을 일부러 `using`으로 즉시 해제해 메모리를 묶어두는 설계였는데, 그 안의 format이
  매 호출 누적돼 의도가 상쇄되고 있었다(같은 파일의 다른 두 생성 지점은 이미 `using`).

### Changed
- **⚠️ 동기 `ParseHtml`/`LoadHtml`/`InsertHtml`은 더 이상 원격 이미지를 받지 않는다**: 기존에는
  `http` 이미지를 호출 스레드에서 **동기로 다운로드**해(파스당 5초 예산) UI가 그동안 멈췄다.
  이제 동기 경로는 네트워크 I/O를 하지 않고 `data:`/`file:` 이미지만 싣는다. 원격 이미지가
  필요하면 `ParseHtmlAsync` / `RichEditor.LoadHtmlAsync`를 쓰면 된다(붙여넣기는 원래 이 경로라
  영향 없음).
- **`FindCell`이 O(문서) 스캔 → 부모 체인 조회** (`RichEditor.Tables.cs`): 문단이 속한 셀은
  `p.Parent`이므로 문서 전체 재귀 탐색(`FindCellIn`)이 불필요했다. 호출 16곳 중 최악은
  `DrawSelectionHighlight`로, 셀 사각형 선택이 활성인 동안 **그려지는 문단마다** 호출돼 프레임당
  O(가시 × 문서)였다. 표 identity만 필요한 그 지점은 새 `CellTableOf`(O(1))를 쓰고, (r,c)가 필요한
  나머지는 해당 표 하나만 훑는다. 중첩/인라인 표도 부모 배선(`WireBlockParents`)으로 그대로 동작.

### Added
- **표 셀 안 Ctrl+A 단계 선택** (`SelectAll`, HWP/Excel 방식): 셀 안에서 누르면 셀 내용 → (중첩을
  타고 오르는) 표 전체 → 문서 순으로 한 단계씩 확장하고 문서에서 멈춘다. 표 밖에서는 종전대로
  한 번에 문서 전체.
- **찾기 바의 바꾸기 전환 셰브런** (`RichEditorView.FindBar.cs`): 찾기 행 왼쪽의 `▸`/`▾` 토글로
  Ctrl+H로 다시 열지 않고 바꾸기 행을 펼친다(VS Code/브라우저 관례). 표시 상태는
  `SetReplaceVisible` 한 곳에서 관리해 Ctrl+H 경로와 어긋나지 않으며, 읽기 전용에서는 셰브런을
  숨긴다. 로컬라이제이션 키 `ToggleReplace`(EN/KO) 추가.

### 6차 전수 리뷰 — Fixed (2026-07-25)
전 소스 재정독(미커밋 `RichEditor.Compat.cs` 포함) + HWP 붙여넣기 실기 리포트 후속.
빌드 0/0, 테스트 **71/71**(신규 4).

- **HWP 붙여넣기에서 표가 평문으로 풀리던 문제** (`WriteTable`, 사용자 리포트): RTF엔
  `\trowd`·`\cellx`·`\cell`·`\row`가 모두 정상적으로 나가고 있었는데도 HWP는 행 구조를 인식하지 못하고
  셀을 그냥 줄바꿈된 텍스트로 붙였다. 원인은 **행 정의가 한 번만 나간 것** — 스펙상으론 허용되지만
  Word는 `\trowd`+`\cellx` 묶음을 **셀 앞과 `\row` 직전에 두 번** 쓰며, HWP를 비롯한 엄격한 리더는
  `\row` 시점에 행 정의가 살아 있어야 한다. 정의를 `BuildRowDefinition`으로 뽑아 두 번 방출하고,
  Word가 항상 쓰는 `\trgaph108\trleft0`와 셀 문단의 `\itap1`을 추가했다. 표 뒤에는 `\pard\plain`으로
  문자 서식까지 리셋. (파서 쪽은 두 번째 `\trowd`가 셀을 다 읽은 뒤에 오므로 `_curCellx`를 비웠다가
  동일 값으로 다시 채울 뿐이라 라운드트립은 그대로 — `StartRow` 주석에 명시.)
- **오른쪽 정렬이 뒤 문단으로 새던 문제** (`WriteParagraph`, 사용자 리포트): 스펙상 `\pard`가 정렬을
  왼쪽으로 리셋하지만 HWP는 `\pard`를 "현재 기본값으로 복귀"로 보고 앞서 본 `\qr`을 계속 물고 갔다 →
  오른쪽 정렬 문단 하나가 그 뒤 인용/제목/목록을 전부 오른쪽으로 만들었다. 이제 왼쪽도 `\ql`로
  **모든 문단이 정렬을 명시**한다(문단당 3바이트, 리더 의존성 제거). 헤더에 `\uc1`도 명시.

- **RTF 내보내기가 인라인 표("글자처럼 취급")를 통째로 버리던 문제** (`RtfDocumentFormatter`,
  사용자 리포트로 확정): `WriteParagraph`와 셀 안 문단 루프가 `Run`/`InlineImage`만 처리하고
  `InlineTable` 분기가 없어 조용히 사라졌다. **실제 데이터 손실**인 이유:
  `SetClipboardFromSelection`이 *모든* 복사에 RTF를 실으며 Word/HWP는 RTF를 CF_HTML보다 우선하므로,
  인라인 표가 든 문단을 그 앱들에 붙여넣으면 표가 없어졌다(`.rtf` 익스포트도 동일).
  HTML(`EmitInline`)·JSON은 원래 처리하고 있었다.
  - **최상위 문단**: RTF엔 인라인 그리드가 없으므로 **호스트 문단을 표 앞뒤로 쪼개고 진짜
    `\trowd` 행을 방출**한다(텍스트 사이에 표가 있을 때 Word가 하는 것과 동일). 표 뒤에는 빈 문단이
    남을 수 있는데 이는 의도적이다 — RTF는 표 뒤에 문단을 요구한다. 인라인성 자체는 포맷이
    표현하지 못하므로 다시 읽으면 **블록 표**가 된다. *(1차 시도였던 탭 구분 텍스트 평탄화는 단어만
    남고 격자가 사라져 사용자에겐 "표가 사라진" 것과 같았다 — 되돌렸다.)*
  - **셀 안 문단**: 진짜 중첩(`\itap2`+`\nestcell`/`\nestrow`)은 이 writer의 subset 밖이라 종전대로
    텍스트 평탄화. 겸사겸사 `WriteNestedTableAsText` 자신도 셀 안의 그림·더 깊은 중첩/인라인 표를
    버리던 것을 재귀 처리하도록 보강.
  - RTF 파서는 `InlineTable`을 만들지 않으므로 라운드트립 테스트로는 잡히지 않던 유형이다.
- **IME `_imeRangeDelta` 재매핑 누락** (`RichEditor.Ime.cs`): 문단 걸친 선택 위에서 조합을 시작하면
  `OnImeTextUpdating`이 선택을 지우고 이후 IME 범위를 `+_imeRangeDelta`로 재매핑하는데, **같은
  좌표계로 들어오는** `OnImeFormatUpdating`(조합 밑줄)과 `OnImeSelectionUpdating`은 delta를 적용하지
  않았다 → 조합 밑줄이 delta만큼 어긋난 자리에 그려졌다(텍스트 자체는 정상, `CompositionCompleted`가
  리셋할 때까지).
- **`RunNormalizer.Compact`가 인라인 표를 건너뛰던 문제**: `CompactBlock(Paragraph)`이 자기 `Inlines`의
  `Run`만 훑고 `InlineTable`의 셀로 내려가지 않아, 내용이 인라인 표에 든 문서는 로드 시 run 병합·폰트
  인터닝을 전혀 못 받았다(클래스 주석은 셀 재귀를 표방). `UndoManager.EstimateBytes`가 4차에서 고친
  것과 정확히 같은 사각지대.
- **읽기 전용에서 표 블록 선택이 열려 있던 문제** (`TrySelectTableBlock` + `UpdateHoverCursor`):
  `TrySelectInlineTable`·`OverColumnBoundary`·`OverRowBoundary`는 모두 `IsReadOnly`로 자기 검사를 하는데
  최상위 표 좌/상단 테두리 경로만 빠져 있었다. 뷰어에서 파란 선택 크롬과 이동 커서가 뜨지만
  Delete는 막혀 있어 죽은 상태였고, 캐럿을 놓아야 할 클릭도 삼켰다.

### 6차 후속 — Added (2026-07-25)
- **읽기 전용 뷰어 캐럿 옵트인** (`RichEditor.ShowCaretWhenReadOnly`, 기본 `false`;
  `RichEditorView`에도 동일 이름으로 포워딩): 읽기 전용에서도 화살표·Shift+화살표·Home/End·
  PageUp/Down·Ctrl+F가 전부 동작하는데 캐럿이 없어 키보드 탐색 위치를 알 수 없다는 사용자 지적.
  - **기본 off인 이유**: 캐럿은 "여기 입력하세요"라는 어포던스다. 브라우저·PDF 리더가 뷰어에
    캐럿을 안 그리는 이유이고, 이 컨트롤은 이미 읽기 전용 링크를 브라우저 관례(맨클릭으로 열기)로
    맞춰 놨다. 기존 뷰어 호스트의 겉모습도 그대로 유지된다.
  - **깜빡이지 않는다**: 정적인 선은 "현재 위치", 깜빡이는 선은 "편집 가능"으로 읽힌다.
    `RestartBlink`가 읽기 전용에서는 타이머를 아예 돌리지 않는다(캐럿을 숨기는 경우에도 초당 2회
    캔버스를 무효화하던 낭비가 함께 사라진다).
  - `OnReadOnlyChanged`의 `StopBlink()`는 `_caretOn`을 영구히 꺼버려 읽기 전용 캐럿이 다시 켜질 수
    없었다 → `RestartBlink()`로 교체(편집 가능일 때만 타이머 시작).
  - 데모 "읽기 전용" 페이지에서 켜 두어 동작을 보여준다.

### 역방향 스윕 — Fixed (2026-07-25)
원본 `AvaloniaRichEditor`가 WinUI 백포트 과정에서 한 전수 감사(`924d366` 5건, `86427cf` 7건, `bcc12e4`)를
포트 대응 코드와 1:1 대조. 8건은 포트가 이미 가지고 있었고, **4건이 포트에 남아 있어 수정**.
넷 다 렌더 패스 또는 컨트롤 레벨이라 **헤드리스 테스트 불가** — 실기 확인이 필요하다.

- **찾기 하이라이트가 현재 매치를 흐리던 문제** (`DrawFindHighlights`): 앰버 틴트를 모든 매치에 칠하고
  그 위에 반투명 선택 파랑이 얹혀 저대비 진흙색이 되어, 캐럿이 어느 매치에 있는지 알 수 없었다.
  선택 범위가 정확히 매치 하나와 같으면 그 오프셋만 틴트에서 제외한다(브라우저/VS Code 방식,
  `GetFindMatchPosition`과 동일한 판정).
- **한 셀 안 여러 문단을 선택하고 목록을 토글하면 캐럿 문단에만 적용되던 문제** (`SetListType`):
  같은 셀 안의 선택은 양 끝이 같은 셀이라 `CellBlockSelection`이 아니고, 그래서 캐럿 문단만 건드리는
  분기로 빠졌다. 셀 경로를 `SelectedParagraphs()` 기반 한 갈래로 통합해 사각 셀 선택·셀 내 다중 문단·
  무선택을 동일하게 처리한다(그룹 토글: 전부 같은 종류일 때만 해제).
- **`LoadHtmlAsync`가 `AllowRemoteImagesOnPaste`를 무시하던 문제**: 3번째 인자를 넘기지 않아 기본값
  `true`가 적용됐다. 프라이버시 목적으로 끈 호스트도 이 경로에서는 원격 이미지를 **실제로
  다운로드**했다(추적 픽셀 등). `LoadHtml`/`InsertHtml`에도 함께 연결했다 — 동기 파서는 애초에
  네트워크를 타지 않으므로 실질 영향은 없지만, 기본값에 기대는 형태를 남기지 않는다. 속성 XML 문서도
  "paste 전용이 아님"으로 정정.
- **블록 오브젝트 선택 중 Ctrl+Shift+X가 취소선 대신 잘라내기로 먹히던 문제**: 블록 선택 분기의
  Ctrl+C/Ctrl+X가 shift를 배제하지 않아 단축키를 삼켰다. `!shift` 추가(원본이 평문 잘라내기 분기에서
  맞은 것과 같은 부류).

### 목록 토글 완결 + "없음" 중복 제거 (2026-07-25, 사용자 요청)
사용자가 "툴바 글머리표/문단 번호 ▾의 '없음'은 없어도 될 것 같다"고 지적. 그냥 빼면 중첩 목록을
되돌릴 길이 우클릭 메뉴에만 남아, 먼저 토글을 온전하게 만든 뒤 중복을 걷어냈다.

#### Fixed
- **목록을 토글로 끄면 들여쓰기가 유령처럼 남던 문제** (`SetListType`): 끄는 경로 3곳이
  `ListType = None`만 지웠는데 `ParaLeft`는 `ListLevel * 20`을 **ListType과 무관하게** 더한다
  (`RichEditor.cs:40`). 그래서 중첩(레벨 ≥ 1) 목록을 아이콘으로 끄면 마커만 사라지고 레벨×20px
  들여쓰기가 설명 없이 남았다. `ListMarker`도 남아 나중에 다시 목록으로 만들면 되살아났다.
  새 `ClearList(Paragraph)`가 `ListType`+`ListMarker`+`ListLevel`을 함께 지우고, `RemoveList()`와
  토글-오프 3경로가 이를 공유한다 — 이제 "끈다"의 의미가 어디서나 같다.
  *(툴바의 "없음"이 이 결함을 가려주고 있었다.)*

#### Changed
- **툴바 글머리표/번호 ▾ 드롭다운에서 "없음" 제거**: 스타일 피커는 스타일만 나열한다. 제거는 바로 왼쪽
  아이콘(토글)이 담당하며, 위 수정으로 토글이 `RemoveList`와 동일해졌다. Word/HWP 피커에는 "없음"이
  있고 Google Docs에는 없다 — 토글이 온전해진 이상 단순한 쪽을 택했다.
- **우클릭 목록 메뉴의 `RemoveList` 3중 노출 → 1개**: 글머리표 모양·번호 모양 서브메뉴의 첫 항목
  "없음" 둘을 빼고, 명시적으로 이름 붙은 "목록 제거"만 남겼다.
- 로컬라이제이션 키 `ListNone`(EN/KO) 제거 — 사용처가 없어졌다.
  *(2026-07-17에 발견성을 이유로 "없음"을 첫 항목으로 올렸던 결정을 되돌린다. 그때 필요했던 이유가
  토글이 완전히 지우지 못한 데 있었으므로, 토글을 고친 지금은 발견성 손실이 없다.)*

### 잔여 "무해" 4건 정리 (2026-07-25)
6차 리뷰가 "확인만 하고 수정하지 않음"으로 남겼던 항목들. **하나는 무해가 아니었다.**

#### Fixed
- **선택이 중첩/인라인 표를 벗어날 때 가로지른 블록이 지워지지 않던 문제**
  (`TextRange.TopLevelBlockOf`/`RemoveParagraphFromDocument`): 둘 다 정확히 한 단계 —
  **최상위** 표의 셀만 — 들여다봐서, 중첩 표나 인라인 표 안의 문단은 "이 문서에 없음"(null)으로
  읽혔다. `Delete()`는 그 결과 `si`/`ei`가 -1이 되어 **선택이 통과한 최상위 블록들을 제거하는 구간을
  통째로 건너뛰었다** — 중첩 셀에서 시작해 뒤쪽 문단까지 끄는 선택에서 중간 문단이 그대로 살아남는다.
  공용 재귀 판정 `BlockHolds`를 도입해 임의 깊이의 표 셀과 인라인 표까지 훑는다(`CollectParagraphs`와
  동일한 논리 셀 순회). 회귀 테스트 추가(수정 전 실패 확인).
  *4차와 6차가 각각 "호출부가 막고 있어 무해"로 판정했던 건이다. 그 근거였던 `Delete`의 셀 엔드포인트
  가드는 **병합 분기**만 막고 이 블록 제거 구간은 막지 않았다.*
- **툴바가 `Target.StatusChanged`를 놓지 않던 문제**: 에디터가 핸들러를 쥐므로, 호스트가 에디터는
  살려두고 툴바만 트리에서 떼면 툴바와 그 시각 트리 전체가 계속 도달 가능했다. `Unloaded`에서 해제하고
  `Loaded`에서 재구독한다(`-=` 후 `+=`로 멱등 — 재부모화나 Target 세터와 겹쳐도 중복 구독되지 않는다).

#### Changed
- `RichEditorToolbar.OnPaperChanged`가 `SyncPage()`를 `_suppress`로 감싼다: `SelectionChanged`
  핸들러에서 호출되어 suppress가 걸리지 않은 상태였고, `SyncPage`가 쓰는 줌/방향 콤보의 선택 변경이
  자기 핸들러로 재진입했다(지금까지 무해했던 건 되쓰는 값이 방금 설정한 값과 같았기 때문). `Sync()`와
  동일한 형태로 맞췄다.
- `ImageCache.Clear()` 삭제 — 호출처가 없다. 문서 교체는 **의도적으로** `Prune(liveKeys)`를 쓴다(undo
  스냅샷이 공유하는 비트맵을 살려둬 Ctrl+Z마다 플레이스홀더가 번쩍이지 않게). 필요해지면 빈 집합
  `Prune`이 곧 `Clear`라는 점을 주석에 남겼다.

### 7차 전수 리뷰 — Fixed (2026-07-25)
6차 이후 이 세션에서만 소스 24개 파일이 바뀌었으므로 **자기 변경분 감사**를 1순위로 두고(새로 넣은
결함이 가장 위험하다) 미정독 파일을 이어서 정독했다. 새 결함 1건.

- **인라인 표 위에 빈 줄이 생기던 문제** (`RtfWriter.WriteParagraph` — 6차의 인라인 표 승격이 넣은
  결함): 호스트 문단을 표 앞뒤로 쪼갤 때 `\par`를 **무조건** 먼저 방출했다. 표가 문단의 첫 내용일 때 —
  "글자처럼 취급"의 통상적 형태, 즉 문단이 표 하나만 담는 경우 — 그 `\par`가 빈 문단을 닫아
  Word/HWP에서 표 위에 빈 줄이 나타났다. 내용이 실제로 쓰였을 때만 닫는다(`wrote` 플래그).
  표 **뒤**의 빈 문단은 RTF가 요구하므로 유지. 회귀 테스트 추가(수정 전 실패 확인).

## [0.8.1] - 2026-07-22

### Fixed
- `RichEditorPrintHelper.ShowPrintUIAsync` never printed: the dialog opened but preview stayed on
  "loading" and no output was produced. The `PrintDocument` and its `IPrintDocumentSource` were locals,
  and nothing in the print system holds a managed reference back to them — they were collectable as soon
  as the method awaited, taking the pagination/preview/page callbacks with them. Both are now rooted in
  static fields for the life of the job and released on `PrintTask.Completed`.
- `ShowPrintUIAsync` returned `false` after the dialog had already opened, so callers with a fallback path
  (e.g. export to PDF) showed it on top of a live print dialog. It now reports success once the dialog has
  asked for the document, regardless of what `ShowPrintUIForWindowAsync` reports afterwards.
- Page rasters no longer go through `WriteableBitmap.PixelBuffer.AsStream()`, which only covers buffers
  backed by managed arrays under CsWinRT (.NET 5+) and not the native buffer XAML returns; they are encoded
  to an in-memory PNG and decoded through `BitmapImage` instead. PDF export was never affected — it uses a
  separate raster path.

### Changed
- `ShowPrintUIAsync` renders every page before opening the dialog — the `PrintDocument` callbacks are
  synchronous and image decoding cannot be awaited inside them. Its `dpi` default drops from 300 to 150
  (matching `SavePdf`), holding roughly 8 MB per A4 page; lower it further for very long documents.
- `ShowPrintUIAsync` now returns `false` immediately on hosts with dynamic code disabled (Native AOT, or
  any build setting `PublishAot`, which bakes `IsDynamicCodeSupported=false` into `runtimeconfig.json`).
  `PrintManagerInterop.ShowPrintUIForWindowAsync` casts its result to `IAsyncOperation<bool>` through
  `IDynamicInterfaceCastable`, and CsWinRT 2.2 refuses to resolve that ABI helper without dynamic code —
  the throw lands *after* the native call has already put the dialog on screen, stranding a window with no
  document behind it. Neither `CsWinRTAotOptimizerEnabled` (it emits CCW vtables only) nor manual
  registration (`WinRT.TypeExtensions.RegisterHelperType` and the ABI types are internal) covers that
  instantiation, so AOT callers need their own path — e.g. `SavePdf` and a viewer.

## [0.8.0] - 2026-07-18

First published release — feature parity with AvaloniaRichEditor `0.8.0`. The port was developed in phases
(see [`Project_Roadmap.md`](Project_Roadmap.md)):

### Phase 0 — Scaffold
- WinUI 3 class library `src/WinUIRichEditor` + **unpackaged** WinExe demo `samples/WinUIRichEditor.Demo`,
  solution `WinUIRichEditor.slnx`. .NET 10, Win2D 1.4.0, `IsAotCompatible`, code-only control.
- Verified the Win2D `CanvasControl` + `CanvasTextLayout` rendering pipeline end to end.

### Phase 1 — Document model + formatters
- Ported `Documents/` (FlowDocument, Paragraph, Run, TableBlock, …) and `Formatters/`
  (JSON source-gen serializer, `.flow` package, HTML, RTF, PDF) — **byte-compatible** document format.
- Type swaps: `IBrush?`→`Windows.UI.Color?`, `TextDecorationCollection`→`[Flags] TextDecorationFlags`,
  `FontWeight` enum→`Windows.UI.Text.FontWeight` struct, `Avalonia` brushes→WinRT colors,
  `Bitmap`→`CanvasBitmap` (decode deferred to the render layer; natural size parsed from headers via `ImageInfo`).

### Phase 2 — Rendering engine
- `RichEditor : ContentControl` hosting a `ScrollViewer` + `CanvasVirtualControl`.
- `BuildTextLayout` → `CanvasTextLayout` with per-range formatting; one layout drives render/caret/hit-test.
- Inline images via `SpacerInlineObject : ICanvasTextInlineObject` (U+FFFC space reservation) + post-draw
  bitmap from character regions; async image decode cache (`ImageCache`).
- Recursive table rendering (`LayoutTable`), inline tables, run-background highlight.

### Phase 3 — Input
- Focus, pointer hit-testing (`HitTest` → offset), caret (blink, `GetCaretPosition`), drag/Shift selection.
- Self-contained editing core: text insert, Backspace/Delete (+ paragraph merge), Enter split,
  Shift+Enter soft break, arrow/Home/End navigation, Ctrl+A.

### Phase 4 — IME
- Korean/CJK composition via `CoreTextEditContext` (works in WinUI 3 desktop on Windows 26100).
- IME buffer = caret paragraph; composing text applied by `TextUpdating`, underline via `FormatUpdating`.
- Hybrid input: `CharacterReceived` is suppressed only while composing (plain English still arrives there).

### Phase 5 — Editing features
- **Undo/redo** (`UndoManager`, snapshot + coalescing), Ctrl+Z/Y.
- **Clipboard**: copy/cut/paste (internal rich → external HTML → plain text), `CF_HTML` out.
- **Formatting commands** + demo toolbar: bold/italic/underline/strikethrough, foreground/highlight color,
  alignment, headings, blockquote, bullet/numbered lists, indent.
- **Find / replace** with wrap-around and caret scroll-into-view.
- **File open/save** (`.flow`/`.json`/`.html`) via pickers + HWND interop.
- **Right-click context menu**, **block image insert**, **table cell editing** (click-in, type, IME,
  row auto-grow).

### Parity completion — tables, images, page view, host controls
- **Tables**: Tab cell navigation; right-click row/column insert·delete, cell merge/split; draw-to-size
  toolbar grid picker; column-width & row-height resize (drag borders) for top-level **and** nested/inline
  tables; table block selection; block↔inline ("treat as character") toggle; inline-table cell editing +
  full arrow-key caret traversal (horizontal cell entry/exit **and** vertical row navigation / box exit).
- **Images**: clipboard image paste; block & inline image selection with aspect-locked resize handles
  (resize cursor); right-click size presets / replace / save / delete; inline image insert.
- **Clipboard**: RTF, image, and Excel/TSV→table paste connected.
- **Formatting UI**: font/size/color pickers, custom line spacing render + commands, hyperlink edit
  dialog (`ContentDialog`), format painter.
- **Page view / print / PDF**: `PageSize`/`Orientation`/`ShowPageBoundaries`, stacked page render with
  line-aware page breaks, headers/footers/page numbers; `RenderPrintPage`, `SavePdf` (rasterized → PDF).
- **Host controls**: drop-in `RichEditorView` + `RichEditorToolbar`; `EditorMode` presets + feature flags;
  `RichEditorLocalization` (KO/EN); accessibility automation peer (`IValueProvider`).
- **Convenience API**: `ToHtml/LoadHtml`, `ToRtf/LoadRtf`, `ToJson/LoadJson(Async)`,
  `SavePackageAsync/LoadPackageAsync`, `Clear`, `GetPlainText`, `CanUndo/CanRedo`, `InsertHtml`,
  `GetCaretFormat`, `GetStatus`, `StatusChanged`.
- **Tests**: `tests/WinUIRichEditor.Tests` (xUnit.v3, headless `dotnet test`, `OutputType=Exe`) — 26
  model/formatter tests. Model/formatter `FontWeight` defaults moved to plain struct values
  (`FontWeightValues`) so that layer constructs without activating the WinUI runtime.

### Parity polish (2026-06-24)
- **System font enumeration**: `DefaultFontFamily` now defaults to the OS UI font (e.g. "맑은 고딕" on
  Korean Windows) via `SystemFontInfo` (P/Invoke). New `RichEditor.FontFamilyChoices` enumerates the
  installed fonts through `CanvasFontSet.GetSystemFontSet()` with names localized to the OS UI language;
  the toolbar combo and a new right-click font submenu are built from it (each item rendered in its own face).
- **IME**: candidate-window placement fixed — `CaretScreenRect` now returns true physical screen coordinates
  (`TransformToVisual(null)` × rasterization scale + `GetActiveWindow`/`ClientToScreen` client origin), so the
  IME UI tracks the caret. Composition now integrates with undo (`PushUndo("ime")` coalesces one composition
  into a single undo group; `CompositionCompleted` resets the key). Table-cell IME verified.
- **Engine-level zoom**: new `RichEditor.Zoom` ([0.25–5.0]) scales the Win2D drawing session and canvas and
  folds into `ViewToDoc`/`DocToView`, so text stays crisp at any factor and hit-testing/caret stay correct
  (continuous mode reflows to the zoomed width).
- **Host controls**: `RichEditorView` gained a page/zoom chrome bar — zoom combo, paper-size combo
  (Continuous/A4/A3/A5/B4/B5/Letter/Legal/Tabloid → `PageSize` + page outline), orientation combo, and a page
  count in the status bar.

### Native AOT — works end-to-end
- Self-contained Native AOT publish (`samples/.../PublishProfiles/win-x64.pubxml`: `PublishAot` +
  `SelfContained` + `PublishSingleFile` + `PublishTrimmed`, `WindowsAppSDKSelfContained=true`) **builds
  (0 trim/AOT warnings, ~14 MB native exe), runs, and renders** — Win2D `CanvasTextLayout`/`CanvasDevice`
  *and* `CanvasFontSet` all work under AOT. The earlier "crashes at startup in `combase 0x80004005`" was a
  **framework-dependent**-only limitation; the self-contained bundle supplies WinRT activation.
- **Build workaround (still required):** `GenerateLibraryLayout` on the library + an MSBuild target stripping
  the stale `windowsappsdk.winui\1.8` PRIs that Win2D 1.4.0 pulls in (conflicts with WinAppSDK 2.2's PRI).
  Removable once a WinAppSDK-2.x-aligned Win2D ships (latest is still 1.4.0, depending on WinUI 1.8.x).

### API surface, toolbar composition & document-format verification (2026-06-25)
- **Events & appearance**: added `TextChanged` / `SelectionChanged` / `DocumentChanged` (driven off the
  `PushUndo` mutation choke point + a selection snapshot, flushed via `RaiseStatusChanged`) and
  `SelectionBrush` / `CaretBrush` dependency properties (the previously hard-coded caret/selection colors).
- **Icon abstraction**: ported `RichEditorIcon` (45-value enum, original ordinals) + `RichEditorIcons.Provider`
  host override. Built-in icons are **Segoe Fluent Icons** `FontIcon`s (`ToolbarIcons`), resolved
  `Provider > FontIcon > text` — `FontIcon` works in the code-only control (only `SymbolIcon` had crashed).
- **Toolbar composition → original parity**: `LeadingItems`/`TrailingItems` host slots + a custom
  `ToolbarWrapPanel` (WinUI ships no `WrapPanel`); 40-swatch palette + hex-input color pickers with a
  caret-reflecting swatch; list combo-boxes `[icon | marker preview | ▾ style menu]` + public
  `SetListStyle(ListMarkerStyle)`; line-spacing box `[↕ | editable % | ▾ presets | ▲▼ steppers]`. Removed the
  quote toolbar button (the original has none — available via right-click / `ToggleQuote()`).
- **Objects**: one image insert button; image **and** table gained a checkbox "treat as character"
  (`ToggleMenuFlyoutItem`) toggling block↔inline (`ConvertImageBlockToInline`/`ConvertInlineImageToBlock`).
  `InsertTable` now sizes equal columns to the document (or enclosing cell) content width.
- **View file actions**: `RichEditorView` gained `ShowFileActions` + Export/Import (JSON/.flow/HTML/RTF via the
  picker, `WindowHandle` for unpackaged interop) + a host-handled `PrintRequested`, in the toolbar's trailing slot.
- **Demo**: rebuilt as a four-page nav shell — control / read-only / control+toolbar / view.
- **Document format**: confirmed the `.flow`/JSON format is byte-compatible with AvaloniaRichEditor by loading
  a `.flow` saved by the original (paragraphs + image + table round-tripped). Found that the original stores
  colors as **CSS named colors** (`"Blue"`), so `ColorUtil` was expanded from ~10 names to the full CSS/X11 set.
- **Context menu → original parity**: rebuilt with organized Character-format and Paragraph submenus (size,
  color, highlight, font, clear; alignment, list + bullet/number styles, heading, indent, quote), a Margin
  submenu, clipboard Delete, Undo/Redo, inserts, a concise dedicated link menu (with copy-address), a
  read-only branch, and `RichEditorIcons` glyphs on items.
- **Two-step table insert**: pick rows×columns from the 8×10 hover grid (toolbar button or context menu —
  the menu opens the grid as a flyout, since WinUI menus can't host a grid), then drag from the caret on the
  document to set the table's width/height. The table inserts at the caret with the picked rows×columns; the
  rubber-band (anchored at the caret) previews the grid at the dragged size, and a plain click uses the
  default size (Esc / right-click cancels).
- **Fix — vertical caret navigation** (Up/Down), three related bugs:
  - *Across paragraphs:* the ±2 px nudge didn't clear the inter-paragraph margin and `GetPositionFromPoint`
    snapped a margin hit to the *last* paragraph (pinning the caret). Now within-paragraph visual-line moves
    use `CanvasTextLayout.LineMetrics`, the cross-paragraph nudge clears the margin, and a margin hit snaps to
    the *nearest* paragraph's line.
  - *Table cells:* in a multi-paragraph cell, ↑ from the top line snapped to the cell's last line (couldn't
    exit) — the in-cell step now requires the result to actually move in the pressed direction.
  - *Dividers / block images:* a no-caret block between paragraphs left a gap the nudge couldn't cross; the
    move now falls back to the adjacent top-level paragraph (keeping the desired column).
- **Fix**: navigating to the View page crashed (`0x800F1000`) because `FrameworkElement.Parent` is null before
  load, so the toolbar's reparent-on-rebuild silently no-op'd; reused host items are now detached via the
  previous panel's `Children.Clear()`.

### Input & clipboard parity finish (2026-06-25)
Filled the remaining Phase-3 keyboard/clipboard gaps found by a line-by-line diff against the original:
- **Home/End → visual line edge** (`MoveToLineEdge`): previously jumped to the paragraph start/end; now
  stops at the current wrapped line using `CanvasLineMetrics.CharacterCount` (End trims the soft-wrap
  trailing whitespace), matching Word/VS.
- **Ctrl navigation/deletion**: Ctrl+←/→ word move (`WordMove`, a Korean eojeol counts as one word),
  Ctrl+Backspace/Delete word delete (`WordDelete`, a no-op at a paragraph boundary so it doesn't push a
  stray undo step), Ctrl+Home/End to document start/end (`GoToDocEdge`).
- **PageUp/PageDown** (`MovePage` + `PageStep`): caret moves ~one viewport (viewport height ÷ zoom),
  keeping the desired column; auto-scroll follows.
- **Ctrl+Shift+V** pastes as plain text (`PasteAsync(bool plainOnly)` skips the rich/RTF/HTML/image/TSV branches).
- **Copy image to clipboard**: image right-click "Copy" → `CopyImageToClipboardAsync` puts a bitmap on the
  system clipboard (`DataPackage.SetBitmap`) — round-trips in-app and into other apps; clears the internal snapshot.
- **Drag-and-drop image files**: `AllowDrop` + `OnEditorDragOver`/`OnEditorDrop` insert dropped image files
  (`StorageItems`), gated on a recognized image header (`ImageInfo.GetPixelSize`) so a non-image file
  doesn't become a broken box.

Solution builds 0 warn / 0 err; 26/26 tests pass; verified live in the demo.

### Memory optimization (2026-06-26)
Profiled the library's marginal footprint with a new `samples/MemBaseline` harness (env `MEMTEST_MODE`).
Marginal cost = **~33MB fixed** (Win2D `CanvasDevice`/D3D, shared across instances via `GetSharedDevice` —
multiple editors are nearly free) + per-document content; **no leak** across load→clear cycles. Read-only
mode does **not** reduce memory (rendering cost is identical).
- **Deployment diet**: AOT publish folder **181.5 → 80.3 MB** — MSBuild target `_TrimUnusedWindowsMlRuntime`
  drops the unused bundled Windows-ML stack (`onnxruntime.dll` + `DirectML.dll`, ~38.5MB), and
  `CopyOutputSymbolsToPublishDirectory=false` suppresses the ~64MB native AOT `.pdb`.
- **Layout-cache virtualization**: split the heavy `CanvasTextLayout` cache from scrollbar measurement —
  a cheap per-paragraph height cache (`_heightCache`) drives measure; measurement builds **transient**
  layouts (`CreateLayout`, disposed immediately); the heavy cache only holds render/hit-test (viewport-sized,
  safety-net cap). Evicted layouts and pruned image bitmaps are now **disposed** (no more reliance on GC
  finalizers); `ImageCache.Prune`/`Clear` wired up (was a never-pruned decoded-bitmap leak). Large-doc Priv
  is now ~sublinear (200→4000 paras: 128→170MB vs. an estimated ~600MB before).
- **Undo memory budget** (`UndoManager`): snapshot history bounded by a 64MB byte budget (+min 3 steps),
  not just a 50-step count, so editing a large document can't let undo dominate memory.
- **Model compaction** (`Documents/RunNormalizer`): load/parse paths (HTML/.flow/RTF) now coalesce adjacent
  equal-format runs (Word/Docs paste emits a run per word) and intern font-family strings. ~20× fewer runs
  / ~46% smaller managed heap on fragmented docs; content unchanged (`.flow` round-trip preserved). Modest
  effect on headline process memory (the managed model is a small slice; native rendering dominates).

Solution builds 0 warn / 0 err; demo renders the rich sample with no regression. Interactive scroll/caret/
edit/undo correctness after these changes is **not** automated-verifiable (WinUI keyboard focus lives on a
child content-island HWND that external input injection can't reach) — recommend a manual long-document pass.

### Fit-to-width zoom (2026-06-26)
- `RichEditor.FitToWidth()` / `SetZoom(factor)` + `IsFitWidth`: fit-width auto-computes the zoom so the page
  (paged) or content (continuous) width fills the viewport, and **re-fits on viewport resize** (`OnViewportResized`).
- `RichEditorView` zoom-combo gains a **"Fit width"** item; **Ctrl+0** triggers it (the `ZoomTip` already advertised
  it — now implemented). Localized `FitWidth` key (EN/KO).

### Page setup persisted in the document (2026-06-28)
- `FlowDocument.PageSetup` (new `PageSetup` model — paper size, orientation, page boundaries, header/footer,
  page numbers) is serialized in JSON/.flow and **applied to the editor on load** (like a word processor's
  page setup). Zoom is intentionally excluded (view-only, not a document property).
- The editor keeps model↔DP in sync: loading a document that specifies a page setup drives the page DPs;
  changing a page DP captures it back into the document so a later save persists what's shown.
- **Format compatibility**: plain (Continuous) documents emit no `PageSetup` object, so their bytes are
  unchanged, and the original AvaloniaRichEditor (content-only format) safely ignores the field on read.
- +3 round-trip tests (JSON + .flow + plain-omits-it); 29/29 pass.

### Toolbar/editor model re-establishment + context-menu/shortcut overhaul (2026-07-06)
A round of UX polish and an architecture cleanup. The cleanup **diverges from AvaloniaRichEditor 0.8.0 parity**
(a deliberate simplification) — see the `toolbar-editor-model` memory.
- **Context menu → HWP-style**: reorganized to edit → 글자 모양 / 문단 모양 / 목록 / 제목 → hyperlink → select/undo
  → inserts, flattened (alignment as radios, margin folded into 문단 모양, list/heading promoted to top level;
  current state shown via radio/toggle checks from `GetCaretFormat`). Object menus (image / table / link) regrouped
  edit → shape → file → delete. Item font set to **12 px**.
- **Slim by default**: new `RichEditor.ShowFormattingMenu` flag (default false) — the control's menu shows only
  clipboard + quick B/I/U toggles + object menus; the rich formatting groups are opt-in (formatting lives on the
  toolbar). 글자 모양 trimmed to B/I/U/S + **글자 크게/작게** (`IncreaseFontSize`/`DecreaseFontSize`, standard-size
  ladder) + clear — the specific-size / color / font-family pickers are toolbar-only.
- **Shortcuts → central table** (`RichEditorShortcuts`, Word-standard): a single source shared by the key handler
  (`OnKeyDown` → `TryMatch`/`RunShortcut`), the context-menu hints (`KeyboardAcceleratorTextOverride`), and the
  toolbar tooltips (`TipSc`, "굵게 (Ctrl+B)"). New: align `Ctrl+L/E/R/J`, headings `Ctrl+Alt+1..6`, body
  `Ctrl+Shift+N`, bullet list `Ctrl+Shift+L`, line spacing `Ctrl+1/5/2`, strikethrough `Ctrl+Shift+X`, font
  size `Ctrl+Shift+.`/`,`, indent `Ctrl+M`/`Ctrl+Shift+M`. New icons `CharacterFormat`/`FontSizeIncrease`/`Decrease`.
- **Toolbar owns page/zoom + file actions** (`RichEditorToolbar.PageFile.cs`): the zoom·paper·orientation controls
  and Export/Import/Print moved from `RichEditorView`'s separate chrome bar **into the toolbar** (built-in, `WindowHandle`
  / `PrintRequested` forward through). `RichEditorView` now delegates and dropped its chrome row.
- **`ToolbarLevel`** `{ Auto, Minimal, Normal, Maximum }`: a density knob (Auto→Normal editable; read-only builds a
  *view toolbar* = zoom/paper + Export/Print, no editing/Import). Capability (`Allow*`) still vetoes buttons.
  Reflected controls are now nullable and `Sync()` null-guards each (a subset is built at Minimal / read-only).
- **`EditorMode` removed**: capability is now the `IsReadOnly` DP (core viewer/editor switch) + the `Allow*` flags;
  a "viewer" is composition (`IsReadOnly=true` + no/minimal toolbar), not a preset or a separate control. `Modes.cs`
  keeps only the image soft-limit + `OnReadOnlyChanged`. Demo/`MemBaseline` migrated to `IsReadOnly`.
- **Not done**: compacting the `MenuFlyoutItem` row height — WinUI ignores the item `MinHeight`/`Padding` and the
  `MenuFlyoutItem*` theme resources via property, instance, anchor, and app-level overrides; only a full custom
  `ControlTemplate` would work, judged not worth the cost/risk. Font size (12 px) is as compact as it got.

### Deep-audit bug-fix / performance / feature batch (2026-07-08)
A full-source audit (all ~14k lines) followed by a 30-item fix/enhancement pass. Build 0 warn / 0 err,
tests 43/43 (9 new).

**Bug fixes**
- **IME pending format**: a format toggled at an empty caret now reaches composed (Korean) text —
  `OnImeTextUpdating` applies pending styles per update and `CompositionCompleted` clears them
  (previously only the `CharacterReceived` path applied them, so 굵게+한글 입력 lost the format).
- **Block inserts inside table cells**: `InsertBlockAtCaret` generalizes its container to `TableCell.Blocks`
  (rule #3), fixing silently-no-op table/divider/TSV-paste/draw-to-size inserts in cells; `CaretCanHostBlock`
  pre-validates so impossible inserts no longer leave empty undo steps. `InsertImageBlock` now shares the
  same splice (block images insert in cells too).
- **Paste-image undo hole**: pasting an image over a selection snapshots BEFORE deleting the selection
  (one Ctrl+Z restores both); the insert paths take `pushUndo: false` from paste.
- **RTF**: parse `\rquote \lquote \rdblquote \ldblquote \emdash \endash \bullet \emspace \enspace \qmspace`
  (Word pastes lost apostrophes: "don't"→"dont").
- **Unsized block images**: one `BlockImageDims` source (declared → header natural → decoded bitmap → 200px)
  shared by render/measure/hit-test/cells — they diverged (render used the bitmap, measure a fixed 200px),
  skewing every y below an HTML-loaded unsized image.
- **Nested-table structural edits** invalidate every ancestor table's cached row heights (host rows now grow).
- **`TextPointer.CompareTo`** recurses through nested/inline tables (ParagraphsInBlocks order) — selections
  spanning an inline table compared as equal/reversed before.
- **UndoManager caret index** walks the same fully-recursive paragraph order in both directions — a caret
  in a nested/inline-table cell no longer snaps to the document start after undo.
- **No-op keypresses** (Backspace at doc start, Delete at end) no longer push empty undo steps or fire
  phantom `TextChanged`; merges push after validation with the `del` coalesce key.
- **ImageCache**: keyed by `RawBytes` identity (Clone shares bytes → undo/redo and duplicate images hit one
  GPU bitmap; no more full re-decode flash per Ctrl+Z), document swap prunes instead of clearing, in-flight
  decodes self-dispose when invalidated mid-flight (`Prune` is finally wired).
- **End key** reaches the true paragraph end on the last visual line (whitespace-trim now wrap-lines only).
- **`MergeCells`** re-parents moved inlines and moves covered cells' non-paragraph blocks (nested tables,
  images, dividers) into the anchor instead of discarding them.
- **`SyncIme`** compares buffer content, catching same-length replacements (find/replace) it missed.
- Context-menu character-format items (and 링크 삽입) enabled without a selection (caret-word/pending apply).

**Performance**
- **Caret/hit-test paths O(visible)**: `CaretToDocPoint`/`GetPositionFromPoint`/`HitTestBlockList`/
  `CaretInBlockList`/`BlockImageRects` advance non-target paragraphs by the cheap `_heightCache` height;
  the heavy `CanvasTextLayout` is built only for the target (or inline-table hosts). Kills the
  2048-cap clear-all thrash on large documents (per-keystroke native layout rebuilds).
- **Paged-mode virtualization**: `ComputePageBreaks` uses a light per-paragraph `(height, lineBottoms)`
  cache (transient layouts — the heavy cache no longer swallows the whole doc in paged mode);
  `DrawPagedDocument` skips pages outside the invalidated region (page 0 stays the geometry pass);
  `DrawContentWalk` culls clip-외 plain paragraphs (ordered numbering advanced via `HardLineCount`).
  Print/PDF renders each page O(page).
- List-marker `CanvasTextLayout`s cached across frames; `HasBlockOrMedia` memoized (O(n²)→O(n) HTML parse).

**Features**
- **RTF merged cells** round-trip (`\clmgf/\clmrg/\clvmgf/\clvmrg` writer + parser) — merged tables survive
  copy to Word/HWP; parser also reads `\qc/\qr/\qj/\ql`, `\li` (indent), `\highlight` (writer emits it too).
- **WEBP**: plain lossy `VP8 ` and lossless `VP8L` header sizes parse (drag-drop/paste accepted, not just VP8X).
- **Drag-selection auto-scroll** past the viewport edge (timer, speed ∝ distance).
- **Find bar**: `Ctrl+F`/`Ctrl+H`/`F3`/`Shift+F3` (central shortcut table), `RichEditor.FindRequested` +
  `LastFindQuery`/`FindAgain`; `RichEditorView` grew a built-in code-only find/replace bar
  (`ShowBuiltInFindBar` opt-out for hosts with their own UI).
- **Hyperlink UX**: Ctrl+클릭 opens the link (hand cursor while Ctrl-hovering), `Ctrl+K` opens the edit dialog.
- **List Tab levels**: Tab/Shift+Tab on a list item outside a cell changes `ListLevel` (cells keep cell-nav).
- **Theming**: `TextForeground` (default run color incl. markers/cell text) and `CanvasBackground` DPs for
  dark-theme hosts; paged paper stays white.
- **`IsModified`** dirty flag + `MarkSaved()` + `IsModifiedChanged` (reset on load; toolbar Export marks saved).
- **`AllowRemoteImagesOnPaste`** (privacy opt-out for HTML-paste image downloads) + 20MB response cap.
- **Cell vertical alignment** (`TableCell.VerticalAlignment` Top/Center/Bottom): renders + hit-test/caret agree
  via `CellContentOffsetY`; persists in JSON (`VAlign`), HTML (`valign`), RTF (`\clvertalt/c/b`); cell
  context menu gained a 셀 세로 정렬 radio group.
- **Block caret**: ←/→ at a top-level paragraph edge selects an adjacent block image as an object (keyboard
  access to image selection; arrows step out, Delete removes). Tables keep cell-entry, dividers skip.
- **`RichEditorPrintHelper`**: Windows print dialog via `PrintManagerInterop` + `PrintDocument` fed by
  `RenderPrintPage` (unpackaged-friendly); demo Print button uses it, falling back to PDF.
- **TextPattern**: `RichEditorAutomationPeer` now implements `ITextProvider`/`ITextRangeProvider`
  (character/word/line/document units, selection tracking via a new automation text bridge) alongside
  `IValueProvider`.

### Known gaps
- Control-level logic isn't covered by the headless tests (DP registration needs the WinUI runtime), so
  caret/selection/table interaction is verified manually in the demo.
- The original's arrow-key "block caret" (selecting an adjacent image/table as an intermediate caret stop,
  `_caretBlock`) isn't ported — images/tables are selected by click and traversed by arrow keys instead.

[Unreleased]: https://github.com/centwon/WinUIRichEditor/compare/v0.8.1...HEAD
[0.8.1]: https://github.com/centwon/WinUIRichEditor/compare/v0.8.0...v0.8.1
[0.8.0]: https://github.com/centwon/WinUIRichEditor/releases/tag/v0.8.0
