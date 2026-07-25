# Changelog

All notable changes to WinUIRichEditor. This project is a WinUI 3 + Win2D port of AvaloniaRichEditor;
the format follows [Keep a Changelog](https://keepachangelog.com/). The control API is not yet stable.

## [Unreleased]

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
