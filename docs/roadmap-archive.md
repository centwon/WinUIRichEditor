# Roadmap archive — WinUIRichEditor

The dated work log from the port's development: the phase-by-phase build-out (model → rendering →
input → IME → clipboard/formatting), the memory-optimization pass, review rounds 1–7, the parity-gap
analysis and two-way convergence with AvaloniaRichEditor, and the 0.9.0 release preparation.

Kept because the *reasoning* is the valuable part — most entries record why a fix looks the way it
does, and several record judgements that were later found to be wrong. Current status and open work
live in [`../Project_Roadmap.md`](../Project_Roadmap.md).

---

## 🎯 최종 목표: AvaloniaRichEditor **0.8.0 기능 동등성(parity)** — 달성
원본 README의 전체 기능 셋을 따라잡는 것이 목표였고, 현재 달성했다. 아래 Phase 이력은 포팅 과정 기록이다.

> **현재 상태 (2026-06-25):** AvaloniaRichEditor **0.8.0 기능 동등성 달성**.
> - 솔루션 빌드 **0 warn / 0 err**, 단위 테스트 **26/26** 통과.
> - **Native AOT 게시**(self-contained `win-x64.pubxml`)가 빌드·실행·Win2D 렌더·편집·IME까지 동작.
> - 데모 4페이지 셸(컨트롤 / 읽기전용 / 컨트롤+툴바 / View)로 실동작 검증.
>
> 아래 "0.8.0 격차" 체크리스트는 모두 완료 상태다. 시간순 상세 변경 이력은 [CHANGELOG.md](CHANGELOG.md),
> 포팅 사전·핵심 규칙은 [CLAUDE.md](CLAUDE.md) 참조.
>
> **🔧 전수 감사 배치 (2026-07-08):** 전체 소스(~14k줄) 정독 감사 후 30개 항목을 일괄 처리했다 — 빌드 0/0, 테스트 **43/43**(신규 9).
> 버그 15건(IME pending 서식, 셀 내 블록 삽입 no-op, 붙여넣기 undo 구멍, RTF 특수문자 소실, 이미지 측정/렌더 불일치,
> 중첩 표 캐시, TextPointer/Undo 인덱스 재귀, ImageCache 재설계 등), 성능 4건(캐럿/히트테스트 O(visible)화, 페이지 모드
> 가상화, 마커 캐시, HTML 파서 O(n)화), 기능 11건(RTF 병합셀·정렬·형광, WEBP VP8/VP8L, 드래그 자동 스크롤, 내장 찾기 바
> Ctrl+F/H/F3, Ctrl+클릭 링크·Ctrl+K, 리스트 Tab 레벨, 다크 테마 DP, IsModified, 원격 이미지 옵트아웃, 셀 세로 정렬,
> 블록 캐럿, PrintManager 헬퍼, TextPattern). 상세는 CHANGELOG "2026-07-08" 항목.
> **실기 미검증(사람 확인 필요):** IME pending 서식(한글), 드래그 자동 스크롤, 찾기 바, 블록 캐럿, 인쇄 대화상자, 셀 세로 정렬 히트테스트, 페이지 모드 스크롤 회귀.
>
> **⚠️ parity 이후 의도적 이탈 (2026-07-06):** 툴바/에디터 모델을 재정립하며 **`EditorMode` enum을 폐기**했다
> (능력 = `IsReadOnly` DP + `Allow*` 플래그, 뷰어 = `IsReadOnly=true` + 툴바 없음/최소의 조합). 툴바에 밀도 프리셋
> **`ToolbarLevel`**(Auto/Minimal/Normal/Maximum)과 페이지/줌·파일 액션 내장, 컨텍스트 메뉴 HWP식 재구성(+슬림
> 기본 `ShowFormattingMenu`), 단축키 중앙 테이블(`RichEditorShortcuts`, Word 표준)을 추가했다. 상세는 CHANGELOG의
> "2026-07-06" 항목. 아래 EditorMode 관련 서술은 그 이전 기준이다.
>
> ~~**남은 외부 의존(차단 아님):** Win2D의 WinUI 1.8 의존 → PRI 충돌 → 스트립 워크어라운드~~ —
> **해소(2026-07-25).** Win2D 1.4.0은 여전히 `Microsoft.WindowsAppSDK.WinUI` **1.8.260204000 하한선**을
> 선언하지만(nuspec 확인), 라이브러리·데모가 WindowsAppSDK **2.3.x를 명시 참조**하므로 NuGet이 그 위로
> 해석해 1.8 PRI가 병합에 도달하지 않는다. **PRI 스트립 타깃은 삭제**했다 — 타깃을 끄고 AOT
> self-contained 게시를 돌려 성공하는 것으로 확인했다(추정이 아니라 실측).
> ⚠ 명시 참조를 빼면 Win2D의 하한선이 1.8을 다시 끌어와 충돌이 되살아난다. `WinUIRichEditor.csproj`의
> 해당 `PackageReference`는 load-bearing이다.

### 0.8.0 격차 (parity gap — 남은 것 / [x]=완료)
- **표**: [x] Tab 셀 이동, [x] 행/열 추가·삭제·셀 병합 UI(우클릭), [x] **draw-to-size 삽입**(툴바 그리드 피커), [x] **열 너비·행 높이 리사이즈**(경계 드래그), [x] **표 블록 선택**(좌/상단 경계 클릭→Delete), [x] **인라인 표 편집**(마우스 셀 클릭 `HitInlineTable`)·**블록↔인라인 토글**(우클릭). 표 parity 완료.
- **이미지**: [x] 클립보드 **이미지 붙여넣기**, [x] 블록 이미지 선택·**리사이즈 핸들**·삭제, [x] **인라인 이미지 삽입·선택·리사이즈·삭제**, [x] **우클릭 메뉴(크기 프리셋·교체·저장·삭제)**. 이미지 항목 사실상 완료.
- **클립보드**: [x] 외부 **RTF 붙여넣기**, [x] **Excel/TSV → 표** 붙여넣기, [x] 표 객체 우클릭 메뉴, [x] 이미지/링크 객체별 우클릭 메뉴(`RichEditor.ContextMenu.cs`/`RichEditor.Hyperlink.cs`), [x] **이미지 클립보드 복사**(우클릭 "복사"→`CopyImageToClipboardAsync`, `DataPackage.SetBitmap`), [x] **Ctrl+Shift+V 평문 붙여넣기**(`PasteAsync(plainOnly)`), [x] **이미지 파일 드래그앤드롭**(`OnEditorDrop`, 헤더 검증 후 삽입). 클립보드·객체 메뉴 parity 완료.
- **입력/키보드**: [x] 화살표·Shift 선택·더블/트리플 클릭·Shift+클릭, [x] **Home/End 시각 줄 경계**(`MoveToLineEdge`, `LineMetrics` 기반), [x] **Ctrl+←/→ 단어 이동**(`WordMove`, 한글 어절=1단어)·**Ctrl+Backspace/Delete 단어 삭제**(`WordDelete`), [x] **Ctrl+Home/End 문서 처음/끝**(`GoToDocEdge`), [x] **PageUp/PageDown**(`MovePage`). 입력 parity 완료. (미세 차이: 원본의 화살표 키 "블록 캐럿"(`_caretBlock`)은 미이식 — 이미지/표는 클릭 선택+화살표 통과로 대체.)
- **서식 UI**: [x] 글꼴/크기/색 선택기(툴바), [x] **시스템 폰트 열거**(`SystemFontInfo` P/Invoke로 OS UI 기본 폰트=맑은 고딕, `FontFamilyChoices` DP가 `CanvasFontSet.GetSystemFontSet()`로 설치 폰트 전체를 OS 언어 로컬라이즈 이름으로 열거→툴바 콤보·우클릭 폰트 서브메뉴, 각 항목 자기 서체 렌더), [x] **커스텀 줄 간격 렌더**+명령, [x] **하이퍼링크 편집 다이얼로그**(ContentDialog, 우클릭 열기/편집/제거/삽입), [x] **포맷 페인터**(`StartFormatPainter`, 드래그 선택 시 적용, 툴바 토글). 서식 UI 완료.
- **페이지 뷰**: [x] PageSize/Orientation/ShowPageBoundaries + 용지 치수, [x] 페이지 스택 렌더(translate+clip), [x] doc↔view 좌표 변환(연속 모드 항등), [x] **줄단위 페이지 나눔**(문단=줄·표=행 원자, `ComputePageBreaks`+`LineMetrics`), [x] **머리글/바닥글/쪽번호**(여백 밴드), [x] **엔진 레벨 crisp 줌**(`Zoom` DP [0.25~5.0] → Win2D 드로잉 세션 스케일 + 캔버스 크기 + `ViewToDoc`/`DocToView`에 fold. 어느 배율에서도 글리프 선명, 연속 모드 reflow, 줌 상태 히트테스트 정확 — 200% 실기 검증). 페이지 뷰 완료.
- **인쇄 & PDF**: [x] `RenderPrintPage`(`CanvasRenderTarget` 오프스크린 렌더, content-only `_printMode` 가드), [x] `SavePdf`(페이지 RGB 추출→이식된 `PdfWriter` 연결), `GetPrintPageCount`. 데모 "PDF 저장" 버튼.
- **호스트 컨트롤**: [x] 드롭인 `RichEditorView`(에디터+툴바+상태바), [x] `RichEditorToolbar`, [x] 기능 플래그(`Allow*`), [x] **페이지/줌**(줌 콤보 50~200%, 용지 콤보 Continuous/A4/A3/A5/B4/B5/Letter/Legal/Tabloid→`PageSize`+`ShowPageBoundaries`, 방향 콤보, 상태바 페이지 수). 호스트 컨트롤 완료. *(2026-07-06: `EditorMode`→`IsReadOnly`+`Allow*`, 페이지/줌·파일 액션이 `RichEditorView` 크롬 바에서 **툴바 내장**으로 이동, 툴바 밀도 `ToolbarLevel` 추가 — CHANGELOG 참조.)*
  - [x] **아이콘 추상화**(`RichEditorIcons.cs`+`ToolbarIcons.cs`): 원본 `RichEditorIcon` enum(45개, ordinal 동일) + `RichEditorIcons.Provider`(`Func<RichEditorIcon, UIElement?>?` 호스트 오버라이드) 이식. 내장 기본은 **FontIcon(Segoe Fluent Icons)** — WinUI엔 `Geometry.Parse`가 없어 원본의 벡터 Path 대신 FontIcon 채택(코드전용 동작 확인, `SymbolIcon`만 크래시였음 → [[winui3-fonticon-ok]]). 글리프 없는 슬롯(포맷페인터·번호목록·들여쓰기·표구조·구분선)은 텍스트 폴백, 컬러 이모지는 VS15로 모노크롬화. 해석 체인 `Provider > FontIcon > 텍스트`.
  - [x] **툴바 구성 원본 parity**(`RichEditorToolbar.cs`+`ToolbarWrapPanel.cs`): ① **`LeadingItems`/`TrailingItems`** 호스트 슬롯(원본 PublicAPI 갭) + **`ToolbarWrapPanel`**(WinUI엔 WrapPanel 부재→자체 `Panel`, 좁아지면 줄바꿈) ② **40색 팔레트 + hex 입력** 색상 피커(글자색=**"A" 글자 + 현재색 스와치 바**(FontIcon 미사용, 색 중복 회피), 캐럿 색 반영) ③ **리스트 콤보박스**[아이콘(토글)|마커 프리뷰|▾ 스타일 메뉴] + 공개 `SetListStyle(ListMarkerStyle)`(내부 `SetListType` 래핑) ④ **줄간격 박스**[↕|편집%|▾ 프리셋|▲▼ 스테퍼] ⑤ **이미지 버튼 1개**(블록 삽입)로 통합 + 삽입 후 **이미지·표 우클릭 block↔inline 토글**(`ToggleMenuFlyoutItem` 체크박스 "글자처럼 취급", 인라인일 때 체크 표시; 이미지=`ConvertImageBlockToInline`/`ConvertInlineImageToBlock`, 표=`ConvertTableBlockToInline`/`ConvertInlineTableToBlock` 모두 동일 체크박스 UX) ⑥ **인용 툴바 버튼 제거**(원본도 툴바 버튼 없음, 우클릭/`ToggleQuote()`로 제공) + 글자색 콤보 폭 확대. 글꼴/크기 콤보는 원본대로 아이콘 없음.
  - [x] **View 파일 액션**(`RichEditorView.FileActions.cs`): 원본 `RichEditorView`의 **Export/Import/Print**를 툴바 `TrailingItems`에 이식 — `ShowFileActions`, `WindowHandle`(unpackaged 파일 피커 HWND interop), `PrintRequested` 이벤트(호스트 처리 전까지 인쇄 버튼 숨김). Export=JSON/.flow/HTML/RTF(확장자별), Import=내용 스니핑(PK/RTF/HTML/JSON). 데모 ViewDemoPage가 `WindowHandle`+`PrintRequested`(→PDF 저장) 배선. 표 drag-to-size(`BeginTableDraw`)도 이식 완료(아래 ⓚ).
- **로컬라이제이션**: [x] KO/EN `RichEditorLocalization`(이식 완료). [x] **접근성 오토메이션 피어**(`RichEditorAutomationPeer : FrameworkElementAutomationPeer, IValueProvider`).
- **컨트롤 편의 API**: [x] `ToJson/LoadJson/ToHtml/LoadHtml/ToRtf/LoadRtf/SavePackageAsync/LoadPackageAsync/Clear/GetPlainText/CanUndo/CanRedo/InsertHtml`, [x] `GetCaretFormat/GetStatus/StatusChanged`.
  - [x] **이벤트 parity**(`RichEditor.Status.cs`): 원본 0.8.0 `PublicAPI`에 있던 `TextChanged`(문서 내용·구조·서식 변경), `SelectionChanged`(캐럿/선택 이동), `DocumentChanged`(Document 인스턴스 교체) 이식. `PushUndo`(모든 변경의 보편 초크포인트)에서 `MarkTextChanged` 마킹→`RaiseStatusChanged`가 pending 플러시, 선택은 `_selSnapshot` 비교로 실제 이동 시에만 발화. `StatusChanged`는 coarse 신호로 유지.
  - [x] **외형 커스터마이징**(`RichEditor.Appearance.cs`): `SelectionBrush`/`CaretBrush` DP(WinUI `Brush`, 기본 SolidColorBrush) — 하드코딩 `SelectionFill`/`CaretColor` static을 brush에서 색을 뽑아내는 인스턴스 계산으로 치환(캐럿·선택 하이라이트·표 선택·IME 밑줄 모두 반영), 변경 시 캔버스 무효화.
  - 남은 순수 명명 차이(기능은 이미 존재): 원본 `SetFontFamily`↔포트 `SetRunFontFamily`, 원본 `InsertImageBytes/InsertImage/InsertImageFromFileAsync`↔포트 `InsertImageBlock/InsertInlineImage`, 공개 래퍼 미노출 `SetListStyle`(내부 `SetListType`)·`FocusDocumentEnd`·`PasteFromClipboardAsync`(Ctrl+V 경로는 존재). 기능 parity는 충족, API 시그니처 별칭은 선택 사항.
- **Native AOT 퍼블리시 완료** — self-contained `win-x64.pubxml`(PublishAot+SelfContained+SingleFile+Trimmed)로 13.2MB 네이티브 exe 게시·실행·Win2D 렌더 모두 확인. 정식 단위 테스트.

## 진행 상황
- [x] **Phase 0 — 스캐폴드**
  - `src/WinUIRichEditor` (라이브러리, net10.0-windows, Win2D 1.4.0, IsAotCompatible).
  - `samples/WinUIRichEditor.Demo` (unpackaged WinExe, WindowsPackageType=None).
  - `WinUIRichEditor.slnx`.
  - 데모 `MainPage`에 Win2D `CanvasControl` 스모크 렌더(`CanvasTextLayout` 텍스트). 빌드 0 warn/0 err, 런타임 실행 확인.
- [x] **Phase 1 — 문서 모델 + 포매터**
  - `Documents/` 16개 모델 파일 + 헬퍼(ColorUtil, ListMarkers, ImageInfo, ImageEncoder, FontExtensions).
  - 타입 치환: `IBrush?`→`Color?`, `TextDecorationCollection`→`[Flags] TextDecorationFlags`, `FontWeight`(enum)→`Windows.UI.Text.FontWeight`(struct, 비교는 `.IsBold()`), `FontStyle`→`Windows.UI.Text.FontStyle`, `TextAlignment`→`Microsoft.UI.Xaml.TextAlignment`, `AvaloniaList`→`List`, `AvaloniaObject` 제거, `Bitmap`→`CanvasBitmap`(디코드는 렌더 계층으로 연기).
  - `Formatters/`: DocumentSerializer(JSON source-gen), DocumentPackage(.flow zip), HtmlDocumentFormatter, RtfDocumentFormatter, PdfWriter, RoundTripHarness.
  - 이미지: 자연 크기는 `ImageInfo`가 인코딩 헤더에서 동기 파싱(디바이스/비동기 디코드 회피), PNG 인코딩 폴백은 `ImageEncoder`(SaveAsync + ConfigureAwait(false)).
  - 검증: 라이브러리+데모 0 warn/0 err 빌드. 데모에 JSON/HTML/RTF 라운드트립 자가검증 하니스(6 checks).
  - 비고: `WinRT TextDecorations`와 충돌 피해 enum명 `TextDecorationFlags`. RTF writer의 `RichEditor.ListMarkerText`는 공유 `ListMarkers.Text`로 추출(컨트롤은 Phase 2에서 forward).
- [x] **Phase 2 — 렌더링 엔진**
  - [x] **2a**: `RichEditor : ContentControl`(ScrollViewer + `CanvasVirtualControl`), `Document`/`DefaultFontFamily`/`DefaultFontSize` DP.
    - `BuildTextLayout`→`CanvasTextLayout`(공유 디바이스, per-range `SetFontFamily/Size/Weight/Style/Color/Underline/Strikethrough`), 문단 서식 캐시(`ParagraphSig`).
    - 인라인 이미지: `SpacerInlineObject : ICanvasTextInlineObject`로 U+FFFC 공간 예약 + 레이아웃 후 `GetCharacterRegions`로 비트맵 직접 그리기.
    - 이미지 비동기 디코드 `ImageCache`(요소 키, RawBytes 보존, 완료 시 invalidate).
    - 렌더: 문단(배경·인용막대·리스트마커 per-line·정렬·헤딩), 구분선, 블록이미지. 표/인라인표=placeholder 박스.
    - 콘텐츠 높이 측정 → 캔버스 크기 → ScrollViewer 스크롤.
  - [x] **2b (표)**: 재귀 `LayoutTable`+`AssembleTableLayout`+`MeasureCellContentHeight`+`CellImageSize` 이식(`RichEditor.Tables.cs`), rowH는 위치 독립이라 `tb` 키로 캐시. 읽기 전용 표 렌더(셀 배경·테두리·재귀 셀 블록·중첩 표), 인라인 표 렌더(region 원점에 `DrawNestedTable`).
  - [x] **2c**: 커스텀 줄간격(LineHeight/LineSpacing), 런 배경 하이라이트, 페이지네이션(페이지 뷰) — 모두 완료(아래 Phase 5 / "페이지 뷰" 항목 참조).
- [x] **Phase 3 — 입력**
  - [x] **3a**: 포커스(IsTabStop), 포인터 히트테스트(`HitTest(x,y,out region)`→오프셋), 캐럿(블링크·`GetCaretPosition`)·드래그/Shift 선택, 자체 편집 코어(텍스트 삽입·Backspace/Delete·Enter 분할·Shift+Enter 소프트개행·좌우상하/Home/End·Ctrl+A). 부모 와이어링(`UpdateParents`/`NormalizeBlocks`) 이식. 최상위 문단 대상.
  - [x] **3b**: 더블클릭=단어 선택, 트리플클릭=문단 선택(`OnCanvasPointerPressed` 수동 클릭카운트 — WinUI는 ClickCount 미제공, 시간+근접 판정), Shift+클릭 선택 확장. 표 **Tab/Shift+Tab 셀 이동**(`HandleTab`+`AllCellsInOrder`/`FindCell`, 문서순·중첩표 하강, 마지막 셀 Tab=최상위 표 행 추가), 셀 밖 Tab=공백 삽입. 블록(이미지/표) 클릭 선택은 Phase 5(`BlockSelection`/`TableResize`).
  - [x] **3c (키보드 마감, 2026-06-25)**: Home/End 시각 줄 경계(`MoveToLineEdge`), Ctrl+←/→ 단어 이동(`WordMove`), Ctrl+Backspace/Delete 단어 삭제(`WordDelete`), Ctrl+Home/End 문서 처음/끝(`GoToDocEdge`), PageUp/PageDown(`MovePage`). (원본의 화살표 "블록 캐럿" `_caretBlock`만 미이식 — 클릭 선택+화살표 통과로 대체.)
  - 비고: 한글 등 IME 조합 입력은 Phase 4(`CoreTextEditContext`). 평문/확정 문자는 `CharacterReceived`.
- [x] **Phase 4 — IME** (`CoreTextEditContext`)
  - `CoreTextServicesManager.GetForCurrentView()`가 WinUI3 데스크톱(Win26100)에서 **동작 확인**(런타임 프로브). 우회책 불필요.
  - 설계: IME 버퍼 = **캐럿 문단 평문**. `TextUpdating`이 조합/확정 문자를 문단에 삽입, `FormatUpdating` 범위에 밑줄 오버레이, `CompositionStarted/Completed`로 조합 상태 추적.
  - `RichEditor.Ime.cs`: TextRequested/SelectionRequested/TextUpdating/SelectionUpdating/FormatUpdating/LayoutRequested + 포커스 알림 + `SyncIme`(편집/캐럿 후 IME 동기화).
  - **핵심 발견**: WinUI3 데스크톱에서 `TextUpdating`은 **IME 조합에만** 발화. 평문 영문은 `CharacterReceived`로 들어옴 → `CharacterReceived`는 `_composing`일 때만 억제(=`_imeEnabled`로 막으면 영문 전멸). 영문+한글(a: 조합·밑줄·정상확정) 둘 다 동작 확인.
  - [x] **후보창 위치 수정**: `CaretScreenRect`가 `TransformToVisual(null)`(DIP)×rasterization scale + `GetActiveWindow`/`ClientToScreen` 클라이언트 원점(물리 px)을 더해 진짜 화면 물리좌표 반환 → LayoutRequested가 캐럿 아래에 IME UI 배치(문서화된 WinUI3 CustomEditControl 방식과 일치). 한글 조합 회귀 없음 확인.
  - [x] **undo 연계**: `OnImeTextUpdating`이 편집 전 `PushUndo("ime")`로 스냅샷 → 조합의 다수 TextUpdating이 undo 한 그룹으로 코얼레스, `CompositionCompleted`가 `_coalesceKey`를 리셋해 다음 조합/편집이 새 그룹. 실기 확인: "안녕" 조합 후 Ctrl+Z 한 번에 전체 제거.
  - [x] **표 셀 내 IME 검증**: 표 셀 클릭→캐럿 진입→한글 조합("안녕") 확정이 셀 문단에 정상 삽입(실기 확인). IME 버퍼가 캐럿 문단 기반이라 셀도 동일 경로로 동작.
- [x] **Phase 5 — 클립보드·서식·표·이미지·찾기·툴바**
  - [x] **Undo/Redo**: `UndoManager` 이식(스냅샷+전역 인덱스 캐럿 복원), 코얼레싱(type/del/ime 묶음), Ctrl+Z/Y/Shift+Z. `ApplyHistoryState`가 Document DP 교체 후 캐럿 복원.
  - [x] **클립보드**: Ctrl+C/X/V. 복사=평문+HTML(`HtmlFormatHelper.CreateHtmlFormat`)+내부 리치 스냅샷. 붙여넣기 우선순위 내부리치(텍스트 일치 시)→외부HTML(`GetStaticFragment`→ParseHtml)→평문. `InsertDocumentAtCaret`(단일 문단=인라인, 다중=문단 분할+스플라이스).
  - [x] **서식 명령**(`RichEditor.Formatting.cs`): ToggleBold/Italic/Underline/Strikethrough, SetForeground/Highlight(Color), SetFontSize/RunFontFamily, 정렬, 헤딩, 인용, Indent, 불릿/번호(하드라인 분할+선택 재매핑). 캐럿 워드 적용 + pending 스타일(빈 위치 토글→다음 타이핑). Ctrl+B/I/U. 런 배경(형광) 렌더 추가(`GetCharacterRegions` 직접 채움).
  - [x] 데모 툴바(텍스트 버튼) — Symbol enum 크래시 회피 위해 StackPanel+Button.
  - [x] **찾기/바꾸기**(`RichEditor.FindReplace.cs`): FindNext/Prev/ReplaceNext/ReplaceAll(랩어라운드), `ScrollCaretIntoView`(캐럿/매치를 뷰포트로 스크롤 — 화살표·편집·찾기 공통). 데모 찾기 바.
  - [x] **파일 열기/저장**(데모): FileOpen/SavePicker + HWND interop(`WindowNative.GetWindowHandle`/`InitializeWithWindow`), .flow(`DocumentPackage`)/.json(`DocumentSerializer`)/.html(`HtmlDocumentFormatter`). `App.MainWindow` 노출.
  - [x] **컨텍스트 메뉴**(`RichEditor.ContextMenu.cs`): 우클릭 MenuFlyout. *(2026-07-06 HWP식 재구성 + 슬림 기본 `ShowFormattingMenu` + 단축키 힌트 — CHANGELOG "2026-07-06" 참조.)*
  - [x] **이미지 삽입**(`RichEditor.Images.cs`): `InsertImageBlock(bytes)` — 자연 크기는 `ImageInfo`, 콘텐츠 폭에 맞춰 축소, 문단 분할 후 블록 삽입. 데모 피커.
  - [x] **표 셀 편집(3b 일부)**: 히트테스트 셀 하강(`HitTestBlockList`), 셀 캐럿 위치(`CaretInBlockList`), 셀 문단에 캐럿/선택/IME밑줄 렌더, 셀 편집 시 조상 표 행높이 캐시 무효화(`InvalidateCaretTableMeasure`).
  - [x] **표 구조 우클릭 메뉴**(`RichEditor.ContextMenu.cs`+`RichEditor.Tables.cs`): 셀 안 우클릭 시 "표" 서브메뉴 — 위/아래 행 삽입·행 삭제, 좌/우 열 삽입·열 삭제, 셀 병합/분할(선택 사각형 `SelectedCellRange`+`IsCleanRect`), 표 삭제. 모델 `InsertRow/DeleteRow/InsertColumn/DeleteColumn/MergeCells/UnmergeCell` 활용.
  - [x] **클립보드 확장**(`RichEditor.Clipboard.cs`): 붙여넣기 우선순위 내부리치→**RTF**(`view.GetRtfAsync`+`RtfDocumentFormatter.Parse`)→HTML→**이미지**(`StandardDataFormats.Bitmap`→바이트→`InsertImageBlock`)→**TSV/Excel→표**(`LooksTabular`+`InsertTableFromTsv`)→평문. `AllowRichPaste`/`AllowImages`/`AllowTables` 플래그 게이트. **이미지 클립보드 복사**(`CopyImageToClipboardAsync`+`DataPackage.SetBitmap`), **Ctrl+Shift+V 평문 붙여넣기**(`PasteAsync(plainOnly)`), **이미지 파일 드래그앤드롭**(`OnEditorDrop`, `ImageInfo` 헤더 검증) 포함.
  - [x] **컨트롤 편의 API**(`RichEditor.DocumentApi.cs`): `ToHtml/LoadHtml(Async)`, `ToRtf/LoadRtf`, `ToJson/LoadJson(Async)`, `SavePackageAsync/LoadPackageAsync`(.flow), `Clear`, `GetPlainText`, `CanUndo/CanRedo`, `InsertHtml`. 값타입 색상이라 async는 Clone 스냅샷+백그라운드 인코딩.
  - [x] **커스텀 줄 간격 렌더**(`RichEditor.cs`): `LineSpacing`(비례)·`LineHeight`(절대 DIP) → `CanvasTextLayout.LineSpacingMode=Uniform`. `ParagraphSig`/`EmptyLineHeight` 반영. 명령 `SetLineSpacing/SetLineHeight`(선택 문단 전체).
  - [x] **기능 플래그**(`RichEditor.Features.cs`): `AllowImages/AllowTables/AllowRichPaste`.
  - [x] ~~**EditorMode 프리셋**~~ → **2026-07-06 폐기.** 능력은 `IsReadOnly` DP + `Allow*` 플래그로 직접 표현(모드 번들 없음). `RichEditor.Modes.cs`에는 `IsReadOnly` 콜백(`OnReadOnlyChanged`: 블링크 정지·undo 클리어)과 이미지 소프트 리밋(`MaxRecommendedImages`/`GetImageCount`/`RecommendedImageLimitExceeded`)만 남음. 상세: CHANGELOG "2026-07-06".
  - [x] **상태/캐럿 서식 API**(`RichEditor.Status.cs`): `StatusChanged` 이벤트(편집·서식·캐럿/선택 이동 시), `GetCaretFormat()`(record struct), `GetStatus()`(글자/단어/줄·칸).
  - [x] **로컬라이제이션**(`RichEditorLocalization.cs`): KO/EN 키 테이블, `Language`/`Register`/`GetString`, `LanguageChanged`(원본 직역).
  - [x] **호스트 컨트롤(코드 전용)**: `RichEditorToolbar`(undo/redo, 글꼴·크기 콤보, B/I/U/S 토글+캐럿 반영, 색/형광 Flyout, 제목·정렬 콤보, 불릿/번호/인용, 들여쓰기, 줄간격, 표/이미지/구분선 삽입; `Target.StatusChanged`로 동기화, `ImagePicker` 콜백). `RichEditorView`(툴바+에디터+상태바 Grid). **데모는 4페이지 네비 셸**(MainWindow 네비 바 + Frame): ① 컨트롤 ② 읽기 전용(`IsReadOnly=true`, ControlPage 파라미터) ③ 컨트롤+툴바(ToolbarPage) ④ View(ViewDemoPage). 공유 샘플 `DemoContent`. View 툴바는 동일 `RichEditorToolbar`에 파일 액션(`TrailingItems`)+크롬 줄+상태바를 더한 것(레이어 차이 의도적).
  - [x] **블록 이미지 선택·리사이즈·삭제**(`RichEditor.BlockSelection.cs`): 블록 이미지 클릭→선택(파란 테두리+우하단 핸들), 핸들 드래그→비율 고정 리사이즈(`BlockImageRects` 워크가 DrawDocument y-전진 미러), Delete/Backspace 삭제. 타이핑·다른 키·다른 클릭·문서변경 시 선택 해제.
  - [x] **하이퍼링크**(`RichEditor.Hyperlink.cs`): `SetHyperlink/CurrentLinkUri/EditHyperlinkAsync`(ContentDialog: OK/제거/취소)/`OpenLinkAtCaretAsync`(`Launcher.LaunchUriAsync`). 우클릭 메뉴에 링크 열기/편집/제거/삽입.
  - [x] **포맷 페인터**(`RichEditor.Formatting.cs`): `StartFormatPainter`(소스 런 서식 캡처·토글), 드래그 선택 후 release 시 `ApplyFormatPainterToSelection`, 툴바 🖌 토글 버튼+armed 반영.
  - [x] **인라인 이미지 선택·리사이즈·삭제 + 삽입**(`RichEditor.BlockSelection.cs`+`RichEditor.Images.cs`): 렌더가 `_inlineImageRects`에 인라인 이미지 rect 적재(DrawInlineObjects→TrackInlineImage), 클릭 선택·핸들 드래그 리사이즈(비율 고정, 셀 내부면 표 행높이 무효화)·Delete 삭제. `InsertInlineImage(bytes)` + 툴바 🖼ᵢ 버튼. 블록/인라인 선택은 `_selectedBlock`/`_selectedInline`로 분리, `ClearObjectSelection`로 통합 해제. 핸들 hover 시 `ProtectedCursor`로 대각 리사이즈 커서(`OnCanvasPointerMoved`→`UpdateHoverCursor`).
  - [x] **이미지 우클릭 메뉴**(`RichEditor.ContextMenu.cs`+`RichEditor.Images.cs`): 이미지 우클릭 시(블록/인라인) 크기 프리셋(원본/½/⅓/¼, 자연크기×비율, 콘텐츠폭 캡)·교체(`ImageReplacePicker` 콜백+`ImageCache.Invalidate`)·저장(`ImageSaveHandler` 콜백)·삭제. 데모에서 picker/save 콜백 연결.
  - [x] **draw-to-size 표 삽입**(`RichEditorToolbar.cs`): 표 버튼 Flyout에 8×10 그리드 피커 — 칸 hover로 행×열 미리보기("R × C"), 클릭 시 `InsertTable(r,c)`.
  - [x] **표 열 너비 리사이즈**(`RichEditor.TableResize.cs`): 렌더가 `_columnBoundaries`에 열 경계 rect 적재(`RecordTableResizeBoundaries`, `DrawNestedTable`에서 호출해 **최상위·중첩·인라인 표 모두** 커버), 경계 드래그로 너비 조정(내부=인접 두 열 재분배·총폭 고정, 우측끝=표 폭 변경), hover 시 ↔ 커서. 중첩/인라인은 조상 표 행·호스트 문단까지 리플로되도록 `_tableRowHeights.Clear()`.
  - [x] **표 행 높이 리사이즈**(`RichEditor.TableResize.cs`): `_rowBoundaries`에 행 하단 경계 적재, 드래그로 `RowHeights[r]` 조정(min 20, 콘텐츠 높이 미만으론 안 줄어듦), hover 시 ↕ 커서.
  - [x] **표 블록 선택**(`RichEditor.TableResize.cs`): `_tableRects`에 표 외곽 rect 적재, 좌/상단 경계(우/하단은 리사이즈 경계라 제외) 클릭 시 `_selectedBlock=tb`, 파란 외곽선 그리기(`DrawTableSelectionChrome`), Delete로 표 삭제(`DeleteSelectedBlock` 재사용), hover 시 ⤧ 이동 커서.
  - [x] **접근성 피어**(`RichEditorAutomationPeer.cs`): `IValueProvider`(Value=평문, IsReadOnly, SetValue=줄별 `<p>`로 LoadHtml), `OnCreateAutomationPeer` 연결, ReadOnly 변경 시 `NotifyReadOnlyChanged`.
  - [x] **인라인 표 편집·블록↔인라인 토글**(`RichEditor.Tables.cs`+`RichEditor.Input.cs`): `ConvertTableBlockToInline`/`ConvertInlineTableToBlock`(우클릭 표 메뉴 "글자처럼 취급"/"본문 표로 전환"), `HitInlineTable`로 인라인 표 셀 마우스 히트테스트(클릭해 캐럿 진입·편집).
  - [x] **표 셀 세로 캐럿 이동**(`RichEditor.Input.cs` `VerticalInCell`): 인라인 표는 `SetInlineObject`로 표 전체 박스를 레이아웃에 예약해 ±2px 캐럿 넛지가 셀 여백에 갇혀 포인트 기반 ↑/↓가 안 됐음 → 셀 안에서는 **논리적 행 이동**으로 처리. (a) 같은 셀의 위/아래 줄 → (b) 위/아래 행 같은 열 셀(desiredX 보존) → (c) 표 첫/끝 행이면 표 박스 전체를 건너뛰어 바깥 문단으로 탈출. 표 doc 사각형은 `_inlineTableRects`(인라인)·`_tableRects`(최상위 블록 표)에서 조회(`TableDocRect`). `CaretToDocPoint`/`CaretInBlockList`에 `CaretInInlineTable` 하강 추가(인라인 셀 캐럿 기하 해상).
  - [x] **페이지네이션→인쇄/PDF** 완료(위 페이지 뷰·인쇄&PDF 항목 참조).
- [x] **Phase 6 — 테스트 + 데모 마감 + AOT 퍼블리시 검증**
  - [x] Release 솔루션 빌드 0/0. 플레인 `dotnet publish -c Release -r win-x64` 성공(~97MB exe).
  - [x] **Native AOT 퍼블리시 — 빌드+런타임 모두 성공**(2026-06-23 재검증).
    - 게시 레시피: self-contained `samples/.../PublishProfiles/win-x64.pubxml`(`PublishAot`+`SelfContained`+`PublishSingleFile`+`PublishTrimmed`, csproj `WindowsAppSDKSelfContained=true`) → **13.2MB 네이티브 exe**. 산출물에 `coreclr.dll`/`clrjit.dll`/`System.Private.CoreLib.dll`/관리형 `WinUIRichEditor.dll` 부재(=진짜 AOT), 동봉 DLL(Microsoft.ui.xaml/Win2D/WindowsAppRuntime 등)은 self-contained WinAppSDK 네이티브 런타임.
    - 실행·렌더 확인: `PrintWindow` 캡처로 H1·서식 런·정렬·인용·리스트·표까지 Win2D `CanvasTextLayout`이 정상 렌더. → **Win2D `CanvasDevice.GetSharedDevice()`가 AOT에서 동작.**
    - **빌드 차단(PRI277) 해결책**(유지 필요): ① 라이브러리 `GenerateLibraryLayout=true` ② 데모 MSBuild 타깃 `_StripStaleWinAppSdkRuntimePri`로 `windowsappsdk.winui\1.8` PRI 제거(Win2D 1.4.0이 WinUI 1.8.x 런타임 의존 → WinAppSDK 2.2와 `TextCommandDescriptionCopy` 충돌).
    - **핵심 교정**: 이전 "런타임 크래시(combase 0x80004005)"는 **framework-dependent** AOT의 한계였고, **self-contained**로 게시하면 WinRT 활성화가 되어 해결됨. "Win2D #960이 AOT를 막는다"는 이전 결론은 이 구성에서 **오류**. (메모리 `winui3-aot-pri-conflict`)
  - [x] **단위 테스트**(`tests/WinUIRichEditor.Tests`, xUnit.v3, `dotnet test`로 헤드리스 실행, 프로젝트는 `OutputType=Exe`(v3 요건)): 모델·포매터 26개 통과 — JSON/HTML/RTF/.flow 라운드트립(텍스트·서식·표·병합셀), TableBlock 연산(행열 삽입삭제·병합·span·clone), TextRange(GetText/Delete/ApplyPropertyValue/Coalesce), ImageInfo·ListMarkers·Localization. **헤드리스 가능 핵심**: 모델 생성자/포매터가 `Microsoft.UI.Text.FontWeights`(WinRT 정적)를 호출하던 것을 값 구조체 `FontWeightValues`로 치환 → WinAppSDK 런타임 활성화 없이 테스트(겸 런타임 팩토리 호출 제거로 미세 성능 이점). 컨트롤(`RichEditor`) 자체는 DP 등록 정적 생성자가 런타임을 요구해 헤드리스 불가 → 모델/포매터 위주.

## 메모리 최적화 (2026-06-26)
parity 달성 후 라이브러리 메모리 사용량을 측정·개선. 측정 하네스 `samples/MemBaseline`(env `MEMTEST_MODE`로
baseline/editor/editorbig/editorro/editorfrag/multi/reclaim 모드 전환, `MEMTEST_COUNT`로 규모 지정) 추가.
WinUI3 키보드 포커스가 자식 콘텐츠-아일랜드 HWND라 외부 입력 주입이 에디터에 안 닿아, 자가구동 인앱 하네스로 측정.

**측정된 한계(marginal) 비용:** 호스트 앱에 더해지는 라이브러리 비용 = **고정 ~33MB**(Win2D `CanvasDevice`/D3D,
`GetSharedDevice`로 인스턴스 간 공유 → multi 모드 1→8개 ≈ +0.1~0.5MB/개, 다중 에디터 저렴) + **문서 내용 비례**.
**누수 없음**(load→clear 사이클마다 관리 힙 완전 해제, Priv는 할당자 재사용으로 평탄화). **read-only는 렌더 비용이
동일해 메모리 절감 아님**(실측 확인) — undo 미적립·블링크 정지는 CPU/의미 기능. 상세는 `CHANGELOG`/소스 주석.

- [x] **배포 다이어트**(`samples/.../PublishProfiles/win-x64.pubxml` + `WinUIRichEditor.Demo.csproj`): AOT 게시 폴더
  **181.5 → 80.3MB**. (1) MSBuild 타깃 `_TrimUnusedWindowsMlRuntime`로 미사용 `onnxruntime.dll`+`DirectML.dll`(~38.5MB,
  WinAppSDK self-contained이 끌고 온 Windows-ML, 프로세스에 미로드) 제거 (2) `CopyOutputSymbolsToPublishDirectory=false`로
  네이티브 AOT pdb(~64MB) 미복사(`DebugType=none`·후처리 삭제는 ILCompiler `_CopyAotSymbols`가 이후 복사라 무효).
- [x] **레이아웃 캐시 가상화**(`RichEditor.cs`/`RichEditor.Tables.cs`): `MeasureContentHeight`가 스크롤바 높이 때문에
  전 문단 `CanvasTextLayout`을 빌드·캐시(상한 10,000)하던 근본 원인 → ① 싼 `_heightCache`(double)로 측정 분리 ② 측정은
  캐시 안 거치는 transient `CreateLayout`(`using`으로 즉시 dispose) ③ 무거운 layout 캐시는 렌더/히트테스트만 채워 뷰포트
  크기 유지(안전망 cap 2048). 캐시 비울 때 네이티브 layout/bitmap **Dispose**(기존 GC 파이널라이저 의존 제거).
  `ImageCache`: 미호출이던 `Prune` 누수 수정 + `Clear`로 문서 교체 시 GPU 비트맵 해제. 대용량 Priv 200→4000문단
  128→170MB(준선형, 기존 추정 ~600MB → ~75%↓).
- [x] **Undo 메모리 예산**(`UndoManager.cs`): 최대 50개 전체 문서 깊은 복제 → **개수(50)+바이트 예산(64MB, 최소 3단계)
  이중 제한**(`ApproxBytes`는 텍스트만; 이미지 RawBytes는 Clone 참조공유라 제외). 대용량 편집 시 undo 폭증 방지.
- [x] **모델 컴팩션**(`Documents/RunNormalizer.cs`): 로드/파싱(HTML/.flow/RTF)이 단어별 span→Run 단편화를 남기던 빈틈
  (편집 경로만 `TextRange.CoalesceRuns` 호출) → 로드 진입점(`ParseHtml`/`ParseHtmlAsync`/`FromDto`/`RtfDocumentFormatter.Parse`)에
  `RunNormalizer.Compact`(인접 동일서식 Run 병합 + FontFamily 인터닝) 연결. Word/Docs 붙여넣기형 문서에서 run 수 20×↓,
  관리 힙 ~46%↓(11.4→6.1MB). 단 헤드라인 Priv엔 미미(모델은 전체의 작은 조각, 네이티브 렌더가 지배) — GC압력·붙여넣기
  perf 이득 위주. 콘텐츠 불변이라 .flow 라운드트립 호환 유지.

> **검증 상태:** 빌드 0/0, 데모가 리치 샘플(표·이미지·리스트)로 회귀 없이 렌더. 단, 스크롤/캐럿/편집·undo의
> **인터랙티브 정확성은 외부 입력 주입 불가로 미검증** — 사람이 긴 문서 스크롤·편집·undo로 확인 권장.
> 더 짜낼 여지: 모델 플라이웨이트는 한계체감(모델이 작은 조각)이라 비권장. 이미지 위주 문서는 Tier2(오프스크린
> 디코드 축출/해시키 공유)가 네이티브를 실제로 줄일 수 있음(미구현).

## 전수 코드 리뷰 후속 수정 (2026-07-13)
전 소스 정밀 리뷰에서 나온 버그·성능 항목 중 안전한 것들을 일괄 수정 (빌드 0/0, 테스트 43/43 통과).

- [x] **셀 내 문단 병합**(`RichEditor.Input.cs` `MergeContainerOf`): Enter는 셀 안 문단 분할을 지원하는데
  Backspace/Delete 병합은 top-level 전용이던 비대칭 → 컨테이너 일반화(FlowDocument | TableCell). 셀 경계는
  여전히 병합 경계(다른 셀로 안 넘어감). `InsertParagraphBreak`도 분할 불가 컨테이너면 PushUndo 전에 반환
  (유령 undo 스텝 방지).
- [x] **HTML 내보내기 소프트 줄바꿈**(`HtmlDocumentFormatter.EmitInline`): Run 내 '
'(Shift+Enter)을 `<br/>`로
  방출(기존엔 HTML 공백으로 붕괴 → 복사/저장 시 소실). 파서의 `<br>`→"
"과 라운드트립 폐합.
- [x] **셀 문단 Home/End 폭 오류**(`RichEditor.Tables.cs` `ParagraphWrapWidth` + `MoveToLineEdge`): 셀 문단을
  top-level 폭으로 레이아웃해 시각적 줄 경계가 틀리고 layout 캐시를 스래싱하던 것 → 셀 내부 폭(anchor span
  열너비 합 − 패딩)으로 계산.
- [x] **IME 조합 시 문단 걸친 선택 미삭제**(`RichEditor.Ime.cs`): CaretSelectionRange가 collapsed로 보고하는
  cross-paragraph 선택 상태에서 한글 조합 시작 시 선택을 먼저 삭제(영문 CharacterReceived 경로와 동일 동작)하고,
  이동한 캐럿만큼 `_imeRangeDelta`로 이후 조합 범위를 재매핑. CompositionCompleted/FocusEnter/버퍼 재통지 시 리셋.
- [x] **찾기/바꾸기 이벤트 플러시**(`RichEditor.FindReplace.cs`): FindNext/FindPrev/ReplaceAll에
  `RaiseStatusChanged()` 추가 — TextChanged/SelectionChanged가 다음 무관한 입력까지 밀리던 지연 해소.
- [x] **페이지 카운트 캐시**(`RichEditor.Print.cs` `GetPrintPageCount`): 페이지 모드에선 `EnsurePageBreaks()` 재사용
  (상태바가 StatusChanged마다 전체 문서를 재페이지네이션하던 비용 제거).
- [x] **드롭 위치 삽입**(`RichEditor.Input.cs` `OnEditorDrop`): 이미지 드롭 시 드롭 지점으로 캐럿 이동 후 삽입
  (기존엔 드래그 전 캐럿 위치에 삽입).
- [x] **RTF 검정 하이라이트**(`RtfDocumentFormatter.ColorIndex(blackIsDefault)`): 검정 배경 하이라이트가
  colortbl 인덱스 0으로 접혀 소실되던 것 수정(전경색 검정=기본 규칙은 유지).
- [x] **렌더 perf — 셀 선택 캐시**(`_renderCellSel`): `CellBlockSelection()`(문서 2회 전수 탐색)을 draw 패스당
  1회로 — 문단마다 재계산해 선택 렌더가 O(문단×문서)이던 것 제거.
- [x] **렌더 perf — 표 리사이즈 무효화 표적화**(`InvalidateTableMeasure`): 열/행 드래그 중 `_tableRowHeights.Clear()`
  (전 표 재측정) → 해당 표+조상 체인만 무효화. `AfterStructuralEdit`와 헬퍼 공유.

- [x] **P1 — 키 입력당 전체 문서 텍스트 해싱 제거**(`Run.TextHash` + `ParagraphSig` + `GetStatus` 캐시):
  ① `Run.Text`를 backing field로 바꾸고 FNV 해시를 run 단위 lazy 캐시(문자열 불변 → 모든 텍스트 변경이 setter 경유,
  누락 불가능한 무효화). `Clone`은 해시까지 복사(undo 스냅샷 재해싱 방지). ② `ParagraphSig`가 `MixStr(r.Text)` 대신
  캐시 해시+길이를 mix → 문단 sig가 O(문자수)→O(런수). 측정(`MeasureContentHeight`)·페이지네이션(`_lineCache`)·
  렌더 컬링·`_heightCache`/`_layoutCache` 판정이 전부 자동 수혜. 서식 필드는 여전히 live로 읽으므로 stale 불가.
  ③ `GetStatus`(상태바가 StatusChanged마다 호출)도 문단별 (chars,words,breaks)를 sig로 캐시(`_statsCache`,
  `ClearLayoutCache`에서 해제) — 캐럿 문단만 문자 단위 스캔. 합산 정합성: 단어는 문단 경계('
')를 못 넘으므로
  문단별 합=전체. 타이핑 비용이 문서 문자수 비례 → 문단수(런수) 비례로.

- [x] **P2 — 매 프레임 문서 전체 지오메트리 기록 패스 제거**: 히트테스트 rect(인라인 이미지/표, 표 외곽·리사이즈
  경계)를 "매 draw마다 전 문서 재기록"에서 "그려질 때 객체 identity 키로 기록(Dictionary) + `RelayoutToViewport`에서만
  무효화(`ClearRecordedGeometry`)"로 전환. rect는 문서 좌표라 스크롤엔 불변; 위치가 변하는 모든 경로는 relayout을
  지나므로 무효화 누락 없음. 상호작용은 가시성이 전제라 클릭 가능한 객체는 항상 기록돼 있음. 이로써
  `_recordGeometryThisPass` 제거 — 연속 모드에서 atomic-inline 문단·표도 완전 컬링, 페이지 모드의 "0페이지 강제
  워크"도 제거(보이는 페이지만 그림). print는 `_printMode` 게이트로 화면 지오메트리 오염 방지. 부작용: 캐럿이
  화면 밖 표에 있을 때 상하 이동이 정밀 지오메트리 없이 폴백 경로를 탈 수 있음(스크롤 복귀 시 자동 회복).
- [x] **RTF `onttbl` 파싱**(2순위 기능): `N`+폰트 테이블을 읽어 붙여넣기/로드 시 폰트 패밀리 보존(기존엔 전량
  소실). 폰트별 `charset`→코드페이지 매핑(129=cp949 등)으로 **폰트명과 본문 'hh 바이트 모두** 문서 `nsicpg`보다
  정확한 인코딩으로 디코드. 자체 Write와 라운드트립. 테스트 3건 추가(폰트 라운드트립, cp949 폰트명/본문, HTML `<br/>`).
- [x] **Ctrl+휠 줌**(3순위 기능): 노치당 ~10%, `SetZoom` 클램프·fit-width 해제, Handled로 스크롤 차단.

> ⚠ 테스트 실행 주의: `dotnet build WinUIRichEditor.slnx` 후 `dotnet test --no-build`는 **다른 출력 경로의 스테일
> DLL**(bin\Debug vs bind\Debug)을 실행할 수 있음 — 테스트는 `dotnet test <csproj>`(빌드 포함)로 돌릴 것.

- [x] **P3 — 선택 비교 O(문서) 제거**(`ComparePositions` + `_paraIndexMap`): 문단 순서 인덱스 맵을 lazy 캐시
  (`MarkTextChanged`=모든 편집 경로 + `RelayoutToViewport`에서 무효화). `HasSelection`·`DrawSelectionHighlight`가
  `TextPointer.CompareTo`(전체 문서 워크) 대신 맵 조회 — 드래그 선택 렌더가 O(가시문단×문서)→O(가시문단).
  순서는 AllParagraphs와 CompareTo 모두 문서순 재귀라 동일; 미등록 문단은 -1(CompareTo와 같은 규칙).
- [x] **링크 파란색 렌더 일관성**(`CreateLayout`): NavigateUri가 있고 명시 Foreground가 없는 런은 링크 파랑으로
  렌더 — HTML 붙여넣기 링크(파랑 명시)와 인앱 `SetHyperlink`(기존엔 검정 밑줄)가 동일하게 보임.
- [x] **URL 자동링크**(`AutoLinkOnType`, 기본 on): 공백/탭/Enter로 토큰 완성 시 http(s)://·www. 토큰에 NavigateUri
  적용(www.는 https:// 접두). 문장부호 꼬리 제외, 이미 링크된 토큰은 불변경, 타이핑과 같은 undo 그룹.
- [x] **`DefaultFontSize` DP 계약 문서화**: 에디터 생성 런은 명시 10pt(원본 와이어 포맷 호환)라 이 DP가 입력
  텍스트를 재스타일하지 않음을 XML doc에 명시(호스트 구성 문서의 미지정 런·빈 문단 줄높이·툴바 폴백에만 적용).
  동작 변경은 .flow 호환/패리티 리스크로 보류.

- [x] **RTF 내보내기 심화 + 필드 파싱**: ① 하이퍼링크를 `{ield{\*ldinst{HYPERLINK "url"}}{ldrslt ...}}`
  실제 필드로 내보내고, 파서도 `ldinst`를 읽어(중첩 그룹 닫힘 시 URL 보존 가드) `ldrslt` 런에 NavigateUri
  적용 — Word/HWP/자체 라운드트립. ② 셀 콘텐츠: 블록/인라인 이미지 `\pict` 내보내기, 셀 내 중첩 표는 텍스트로
  평탄화(탭/\line — `\itap` 진짜 중첩은 subset 밖, 무단 드롭보다 나음).
- [x] **번호 리스트 단축키** Ctrl+Shift+7(Docs 관례; Word 표준 없음) — 단축키 테이블+컨텍스트 메뉴 힌트.
- [x] **여러 줄 평문 붙여넣기 = 문단 분할**(`InsertPlainTextBlock`): 개행을 문단 경계로(Word/HWP 기대). 한 줄이면
  기존 InsertText(서식 상속·undo 병합 유지), 셀 캐럿은 InsertDocumentAtCaret의 소프트 '
' 폴백.
- [x] **HTML `<pre>` 공백 보존**: pre 서브트리는 CollapseWhitespace 미적용(들여쓰기·개행 유지), 상속 폰트 없으면
  Consolas 기본. 테스트 2건 추가(RTF 하이퍼링크 라운드트립, pre 공백) — 총 48개.

- [x] **HWP/Word 이미지 붙여넣기 호환 보강**(사용자 리포트): ① RTF 파서 — `\dibitmap`(raw DIB)을 BMP 헤더
  래핑으로 수용, blip 키워드 없는 `\pict`도 바이트 스니핑으로 수용(WMF/EMF는 여전히 미지원 → ②로 폴백).
  ② PasteAsync — RTF가 `\pict`를 담고 있는데 파싱 결과에 이미지가 없으면 HTML 플레이버를 파싱해 이미지가
  있으면 그쪽 선택(표 충실도는 RTF 우선 원칙 유지). ③ HTML 파서 — file: 이미지 차단 상태에서도 `%TEMP%` 아래
  경로는 허용(Word/HWP CF_HTML이 방금 복사한 그림을 임시 파일로 참조; 임시 폴더 밖은 계속 차단). ④ RTF 라이터 —
  `\pict`에 `\picw/\pich`(자연 픽셀 크기) 동봉(goal만 있으면 무시하는 소비자 대응). HWP 실붙여넣기: 에디터→HWP OK,
  HWP→에디터 실패 지속 → 실제 HWP 클립보드 덤프로 진단: HWP RTF는 그림을 `\wmetafile8`(EMF 래핑, 디코드 불가)로
  넣고, HTML은 `file:///%TEMP%\Hnc\BinData\*.png` + `width="168pt"` 속성. **진짜 원인 2개 추가 발견·수정**:
  ⑤ `HtmlFormatHelper.GetStaticFragment`가 `file:///`를 **`ms-clipboard-file:///`로 재작성** → LoadImage가
  이 스킴을 file:로 정규화해 수용. ⑥ `ReadPx`가 속성의 pt/px 접미사 수용(pt→px ×4/3). 헤드리스 재현으로
  앱 경로 이미지 2/2 파싱 확인, 회귀 테스트 추가(총 49개). **HWP 양방향 사람 검증 완료(2026-07-13)**.

## 2차 전수 리뷰 + 블록 레이아웃 맵 (2026-07-13)
1차 리뷰에서 못 본 파일(툴바 본체·WrapPanel·PrintHelper·SystemFontInfo)과 이번 세션 신규 코드를 재검토,
남은 최대 핫패스를 제거. 빌드 0/0, 테스트 49/49.

- [x] **블록 레이아웃 맵**(`EnsureBlockLayout` — RichEditor.cs): 최상위 블록별 (top, height, 번호시작)을
  (내용×폭)당 1회 계산해 모든 doc-space 워크가 공유. 효과: ① `DrawContentWalk` — 스크롤 프레임마다 전 블록
  y-누적(컬링된 문단도 sig+캐시 조회)하던 것 → 이진 탐색 + 가시 블록만, 클립 지나면 break. ② `GetPositionFromPoint`
  (클릭/드래그마다) → O(log n) + 최근접 문단은 정렬된 top 기반 양방향 스캔·조기 종료. ③ `CaretToDocPoint`
  (키 입력마다 수 회: 스크롤/IME/desiredX) → **부모 체인**으로 최상위 블록 O(깊이) + 맵 O(1) 조회
  (`TopLevelBlockFor`). ④ `BlockImageRects`·`MeasureContentHeight`도 맵 소비. 번호 리스트는 블록별
  orderedStart를 맵에 저장해 컬링과 무관하게 정확.
  **무효화 계약**: `MarkTextChanged`(모든 편집) + `ClearLayoutCache` + `RelayoutToViewport` 진입 시 무조건
  (드래그 리사이즈는 PushUndo가 드래그 시작 1회뿐이라 per-move 무효화가 필수 — RelayoutToViewport가 그 funnel).
- [x] **붙여넣기 이중 언두 스텝 수정**(`InsertPlainNoUndo`): `InsertDocumentAtCaret`의 셀 폴백이 `InsertText`를
  불러 caller의 스냅샷 + InsertText의 스냅샷 2개가 쌓이던 것 → 언두 없는 삽입 헬퍼로 교체(1 붙여넣기 = 1 언두).
- [x] **툴바 Sync 할당 제거**: 키 입력마다 `new SolidColorBrush(Black)` 3개 만들던 것 → 정적 `BlackInk` 공유.
- 추가 검토 결과: ToolbarWrapPanel/PrintHelper/SystemFontInfo/AutomationPeer 특이사항 없음.

> **인터랙티브 검증 완료(2026-07-13, 사람 확인)**: 대형 문서 스크롤·클릭·드래그 선택, 이미지/표 드래그 리사이즈,
> 번호 리스트 연속성, 페이지 모드, 붙여넣기 언두 — 블록 레이아웃 맵 이후 전부 정상.

- [x] **읽기 전용 링크 UX(브라우저 관례)**(2026-07-13, 사용자 요청·검증 완료): 뷰어(IsReadOnly)에서 링크에
  호버하면 손 커서, **일반 클릭**으로 바로 열기. 누를 때가 아닌 **뗄 때** 발사(`_pressLink` arm→release) +
  무선택·슬롭 이내 조건 — 링크 텍스트 드래그 선택은 그대로 가능. 편집 모드는 기존 Ctrl+클릭(Word 관례) 유지.
  데모 샘플(DemoContent)에 하이퍼링크 문단 추가.

> ~~**잔여 큰 항목**: 원본 대비 패리티 갭 분석 미실시~~ — **완료(2026-07-24~25)**: 갭 분석 + 트랙 1(원본→포트)
> + 트랙 2(포트→원본, 원본 저장소에서 수행) + 역방향 스윕까지 끝나 양방향 수렴 완료. 아래 해당 절 참조.
> **인터랙티브 검증 완료(2026-07-13, 사람 확인)**: P1(대형 문서 타이핑)·P2(이미지/표 문서 스크롤 + 객체 선택/
> 리사이즈/우클릭)·P3(드래그 선택), RTF 폰트 유지 붙여넣기, Ctrl+휠 줌, URL 자동링크, 링크 파란색, 상태바 통계
> 모두 정상.

> **인터랙티브 검증 완료(2026-07-13, 사람 확인)**: 셀 내 Enter→Backspace 병합, 한글 IME로 여러 문단 선택
> 덮어쓰기, 셀 안 Home/End, 표 열/행 드래그 리사이즈, 이미지 드롭 위치, 캐럿 어피니티(End/클릭/상하 이동) 모두 정상.

## 3차 전수 리뷰 후속 수정 (2026-07-16)
전 소스 재정독(3차)에서 나온 버그 7건 일괄 수정. 빌드 0/0, 테스트 **52/52**(신규 3).

- [x] **LoadJson 예외 계약**(`DocumentSerializer.ParseJson`): 손상 JSON에서 `JsonException`이 그대로
  전파되던 것(문서화는 "빈 문서 반환") → catch 후 null DTO(=빈 문서). .flow 경로는 원래 안전했고
  평문 문자열 경로(LoadJson/LoadJsonAsync)만 뚫려 있었음.
- [x] **HTML 붙여넣기: 블록 래퍼 없는 최상위 인라인 서식 소실**(`HtmlDocumentFormatter`):
  `WalkBlocks`가 bare `<span style>`/`<b>`(브라우저 소량 복사의 CF_HTML 형태)를 `ParseInlines`로 넘겨
  요소 **자신의** 태그/style이 무시되던 것 → 루프 본문을 `ParseInlineNode`(노드 1개 처리, 자기 서식
  적용 후 하강)로 추출해 그 경로로 태움.
- [x] **인라인 표 sig 누락 필드**(`RichEditor.ParagraphSig` → `MixTable`): sig가 RowHeights/병합
  span/셀 세로정렬/셀 내 비문단 블록을 미포함 → 행높이 드래그·셀 병합 후 호스트 문단의 캐시된
  레이아웃(스페이서 박스)·높이가 stale, 표가 예약 박스보다 커져 다음 줄과 겹침 → 전 필드 mix.
- [x] **Enter 분할/블록 삽입 tail 서식 승계**(`Paragraph.CloneFormat()` 신설): 분할 시 ListMarker(◦,
  "a)" 등 글리프 튐)·LineSpacing/LineHeight·MarginRight·IsQuote·여백이 소실되던 것 → 전 문단 서식
  승계. Enter의 HeadingLevel→0(제목 뒤 본문) 기존 의도는 유지. `InsertBlockAtCaret`/
  `InsertDocumentAtCaret`의 tail(같은 문단의 연속)도 CloneFormat 사용.
- [x] **SelectAll 이벤트 미발화**: `RaiseStatusChanged()` 추가 — Ctrl+A 후 SelectionChanged/툴바
  반영이 다음 무관 입력까지 밀리던 것(2026-07-13 FindNext 수정과 동일 부류).
- [x] **임의 줌 배율 콤보 공백**(`SyncPage`): Ctrl+휠이 만든 110%/121% 등 비프리셋 배율에서 줌
  콤보가 빈 값 → 동적 "N%" 항목을 만들어 선택(프리셋 복귀 시 제거).
- [x] **글자색 "자동(기본색)" 복귀**(`SetForeground(Color?)` + 팔레트에 AutoColor 버튼): 기존엔
  명시색만 지정 가능해 다크테마(TextForeground)에서 검정이 박히고 ClearFormatting 외 복귀 수단이
  없었음. 형광펜의 "형광 없음"과 대칭.
- 신규 테스트 3: 손상 JSON→빈 문서, bare 인라인 서식 보존(+bare `<b>`), CloneFormat 전 필드 승계.
- **1차 실기 검증 후속 수정 2건**:
  - ⚠ **Ctrl+휠 줌 크래시**(combase E_INVALIDARG, stowed 0xc000027b — 사용자 재현): 줌 콤보의 동적
    ComboBoxItem 방식(위 항목의 최초 구현)이 휠 노치마다 Items 추가/제거+선택 변경을 반복해 WinUI
    ComboBox가 사망 → **Items 무변경 `PlaceholderText` 방식으로 재구현**(SelectedIndex=-1 + "N%"
    표시). 보험으로 `OnRegionsInvalidated`에 stale region 가드 추가(줌/리사이즈 폭주 시 캔버스가
    줄어든 뒤 도착한 옛 region의 `CreateDrawingSession`이 E_INVALIDARG → skip; 방치 시 앱 즉사).
  - **리스트 마커 베이스라인 정렬**(사용자 리포트: 200% 줄간격에서 불릿이 글자보다 위에 뜸):
    마커를 줄 상단이 아니라 **그 줄 텍스트 베이스라인**에 정렬(`LineMetrics.Baseline` − 마커 자체
    baseline). 자연 간격에선 두 베이스라인이 일치해 기존과 동일, 커스텀 간격에서만 내려앉음.
> **인터랙티브 검증 완료(2026-07-16, 사람 확인)**: 인라인 표 행높이 드래그 리플로, ◦/200% 문단
> Enter 승계 + 불릿·글자 베이스라인 정렬, Ctrl+A 툴바 반영, Ctrl+휠 연속 줌(크래시 없음 + 콤보
> "N%" 표시), 글자색 팔레트 "자동(기본색)" 모두 정상.

### 성능 후속 4건 (2026-07-16, 같은 리뷰의 perf 항목)
빌드 0/0, 테스트 **53/53**(신규 1: RTF 이미지 hex 라운드트립).
- [x] **RTF 이미지 hex 단일 할당**(`RtfDocumentFormatter`): `WritePict`의 바이트당 `ToString("x2")`
  (1MB 그림 = 문자열 100만 개; 이미지 포함 **모든 복사**에서 실행) → `Convert.ToHexStringLower` 1회.
  파서 `HexToBytes`도 `Convert.FromHexString`으로(입력은 hex만 수집되므로 catch는 계약 유지용).
- [x] **붙여넣기 이중 클론 제거**(`InsertDocumentAtCaret`): 모든 호출처가 1회용 문서(신선한 파스
  또는 명시 Clone)를 넘기므로 내부의 전체 deep clone은 순수 중복 → **소유권 이전 계약**으로 변경
  (블록을 그대로 배선; 호출처 5곳 검증). 단일 문단 경로의 `InsertInlinesAtCaret`도 이동으로 통일
  (인라인 표 재복제 제거).
- [x] **GetStatus 키입력당 할당 제거**: `AllParagraphs()`(전 문서 List 구축) → `ParagraphsInBlocks`
  lazy 열거. 툴바 폰트 콤보도 `_fontReflected` 캐시로 키입력당 O(설치 폰트) 항목 스캔 skip
  (Build/PopulateFontList에서 리셋).
- [x] **ImageCache 콘텐츠 해시 공유**(Tier2, 로드맵 잔여 항목): 캐시 키를 배열 identity → **SHA256
  콘텐츠 해시**로. 같은 그림을 여러 번 붙여넣어도(배열 상이) 디코드 1회·GPU 비트맵 1개. 해시는
  배열당 1회만 계산(`ConditionalWeakTable` memoize — per-draw `Get`은 재해싱 없음), Invalidate/Prune의
  byte[] 키도 같은 키 공간으로 매핑. 이미지 반복 문서의 네이티브 메모리 실절감.
> **인터랙티브 검증 완료(2026-07-16, 사람 확인)**: 리치/이미지/표 붙여넣기 + 붙여넣기 언두(소유권
> 이전), 같은 이미지 다중 붙여넣기 렌더(해시 캐시), 이미지 포함 선택 복사 모두 정상.

## 기능 확장 8건 (2026-07-17, 3차 리뷰의 기능 제안 전부)
빌드 0/0, 테스트 **57/57**(신규 4: HTML/RTF 줄간격 라운드트립, alt JSON+HTML, RTF 레벨별 번호).

- [x] **① 셀 배경색 UI**(`RichEditor.ContextMenu.cs` `ShowCellBackgroundPalette`): 표 우클릭 서브메뉴
  "셀 배경색…" → 툴바 40색 팔레트(공유, `RichEditorToolbar.Palette` internal화) + "없음" 플라이아웃.
  셀 사각형 선택 시 전체, 아니면 현재 셀. 모델/직렬화는 원래 지원 — 편집 수단만 신설.
- [x] **② 찾기 전체 하이라이트 + n/m 카운터**: 에디터 `SetFindHighlight`/`ClearFindHighlight`/
  `GetFindMatchPosition` + 렌더 `DrawFindHighlights`(앰버 틴트, top-level·셀 문단 모두; 찾기 UI 열려
  있을 때만 스캔). FindBar는 타이핑 즉시 라이브 하이라이트 + "n/m" 라벨, 닫으면 해제.
- [x] **③ 레벨별 목록 번호**(`EnsureBlockLayout` + `RtfWriter.Build`): 단일 ordered 카운터 →
  ListLevel별 카운터 스택(HTML 중첩 `<ol>` 의미론). 하위 목록은 재진입 시 1부터, 상위는 자기 번호
  이어감, 불릿은 컨텍스트 유지, 비리스트 블록은 리셋. 기존엔 하위 목록이 부모 번호를 이어받았음.
- [x] **④ 줄간격 직렬화**: HTML `line-height`(%·px·무단위) 라운드트립(문단·리스트 항목),
  RTF `\slN\slmult1`(비례)/`\sl-N\slmult0`(절대) 라운드트립 + `\pard` 리셋. 기존엔 내보내기에서 전량 소실.
- [x] **⑤ 텍스트 드래그&드롭 이동**(`RichEditor.DragText.cs`): 선택 내부 프레스→드래그로 이동(Ctrl=
  복사), 회색 드롭 프리뷰 캐럿, 슬롭 미만이면 일반 클릭(캐럿 배치). 이동은 빈 마커 런(고유 서식 →
  Coalesce/Delete에서 생존)으로 삭제 후 드롭 위치 복원, 언두 1스텝. 셀 블록 선택은 제외, 멀티클릭과
  비충돌(트리플클릭 유지).
- [x] **⑥ 이미지 대체 텍스트**: 모델 `AltText`(ImageBlock/InlineImage, Clone 포함) + JSON `Alt`
  (null 생략 → 기존 포맷 불변) + HTML `alt` 라운드트립 + 이미지 우클릭 "대체 텍스트…" 다이얼로그.
- [x] **⑦ 접근성 지오메트리**(`RichEditorAutomationPeer`): `GetBoundingRectangles`(범위→물리 화면
  줄 rect, 256개 캡)·`RangeFromPoint`(화면점→오프셋) 구현. doc↔screen 변환을 IME `CaretScreenRect`와
  공용 헬퍼로 재구성(`DocPointToScreen`/`DocRectToScreen`/`ScreenPointToView`), 문단 원점은
  `CaretToDocPoint(0) − CaretInLayout(0)`으로 셀 문단까지 커버.
- [x] **⑧ 가로 캐럿 추적**(`ScrollCaretIntoView`): 줌/페이지 모드로 캔버스가 뷰포트보다 넓을 때
  캐럿을 가로로도 따라감(ScrollableWidth>0일 때만, 세로와 동일 마진).
- **1차 실기 검증 후속 2건 (2026-07-17)**:
  - **찾기 입력창 포커스**: Ctrl+F 직후 타이핑이 문서로 들어가던 것 — 바가 같은 틱에 막 Visible이
    되어 WinUI가 `Focus()`를 조용히 무시(레이아웃 전) → `DispatcherQueue.TryEnqueue`로 지연 포커스.
  - **목록 제거**(`RemoveList()` 공개 API): 글머리표/문단 번호 속성(마커 스타일·중첩 레벨 포함)을
    명시적으로 제거. 셀 블록 선택에도 적용. (토글은 캐럿 문단과 같은 종류일 때만 꺼져 발견성이 낮았음.)
    사용자 피드백으로 UI 재배치: 툴바 리스트 ▾ 드롭다운과 컨텍스트 메뉴 글머리표/번호 모양
    서브메뉴의 **첫 항목 "없음"**(HWP/Word 스타일 피커 관례) — 하단 "목록 제거"보다 발견성 우선.
> **인터랙티브 검증(2026-07-17, 사람 확인)**: 찾기 입력창 포커스, 목록 제거("없음" 재배치 후) 정상.
> 나머지(셀 배경색 팔레트, 찾기 하이라이트+카운터, 중첩 번호 목록, 줄간격 내보내기 라운드트립,
> 텍스트 드래그 이동/Ctrl 복사, 이미지 alt 저장·재로드, 200% 줌 가로 캐럿 추적)는 사용 중 확인 예정.

- [x] **툴바 Export에 PDF 추가**(2026-07-17): 내장 Export의 확장자 선택에 .pdf — 컨트롤의 `SavePdf`
  엔진을 툴바가 배선(그동안 데모만 자체 버튼으로 연결). PDF는 재로드 불가한 출력 전용이라
  **dirty 플래그(`MarkSaved`)는 문서 포맷(.json/.flow/.html/.rtf)에서만 해제**하도록 분기.
  ViewDemoPage의 중복 크롬 줄(페이지 토글·PDF 저장 버튼) 제거 — 툴바 용지 콤보/Export가 대체,
  인쇄 실패 폴백(`SavePdfFallbackAsync`)만 잔류.
- [x] **툴바 시각 정돈**(2026-07-17, 사용자 리포트 "들쭉날쭉"): ① `ToolbarWrapPanel.Arrange`가 줄
  안에서 자식을 **세로 중앙 정렬**(기존 top-정렬이 높이 차이를 그대로 노출 — 최대 원인) ② 전 컨트롤
  **높이 32px 통일**(버튼·토글·색버튼·리스트박스·줄간격박스 = WinUI 콤보 기본 높이), 아이콘 버튼
  최소폭 36px ③ 텍스트 폴백 글리프 14px 통일, 구분선 20px 고정, 🔍 이모지 → Segoe Fluent Zoom
  (0xE71E) FontIcon.
- [x] **툴바 그룹 순서 원본 정렬 + 아이콘 2차 통일**(2026-07-17): ① Build 순서를 원본 툴바와 동일하게
  재배열 — 실행취소·재실행 → **B I U S → 글자색·형광·페인터·서식지우기 → 글꼴·크기** → 본문·정렬 →
  목록·들여쓰기·줄간격 → 표·이미지·구분선 → 페이지/줌 → 파일 (기존엔 글꼴·크기가 B I U S 앞).
  ② "▾" 텍스트 캐럿 3곳 → 10px ChevronDown(0xE70D) FontIcon, 구분선 삽입 버튼 → 0xE738 가로 바
  FontIcon, 형광펜 버튼 얼굴 → Highlight(0xE7E6) FontIcon 14px(✎ 텍스트가 유독 작아 보이던 것).
  ③ **글자형 글리프 시각 크기 보정**(`ToolbarIcons.SizeFor`): B/I/U/S(+글자 서식 계열)는 em을 꽉
  채워 그려져 같은 FontSize에서 사물형 아이콘(저장 등)보다 커 보임 → 해당 슬롯만 16→13px.
  ④ **폭·간격 통일**: 아이콘 버튼 고정 Width(내용 편차 제거), 콤보·리스트박스·줄간격 박스의 개별
  Margin 전부 제거 — 간격은 패널 `HorizontalSpacing` 단일 소스(그룹 사이만 구분선 마진 +2).
- [x] **툴바 미세 조정 3차**(2026-07-17, PrintWindow 캡처로 실측 확인): 콤보 콘텐츠 FontSize 12
  고정(글꼴 콤보가 자기 서체로 커 보이던 것), 사물형 아이콘 16→15/글자형 13, **아이콘 버튼 폭
  36→30→26**(30px에서도 15px 글리프 주위 여백이 커 인접 아이콘 간격이 넓어 보임 — 특히 표↔이미지·
  들여쓰기), 텍스트 폴백 Content를 `AsElement`(15px·`TextLineBounds.Tight`·중앙)로 감싸 FontIcon과
  수직·크기 정렬(표 ▦ 원시 문자열이 낮게·넓게 앉던 것).
  **최종 치수**: 컨트롤 높이 **28**(버튼·토글·색버튼·리스트/줄간격 박스 + 콤보), 아이콘 버튼 폭 26,
  패널 간격 4(그룹 구분선 +2), 콤보 12pt·아이콘 15/13pt. 콤보는 기본 스타일의 `MinHeight`
  (TextControlThemeMinHeight=32)가 Height를 도로 늘리므로 **MinHeight도 함께 28**로 내려야 함.
  > **캡처 검증 방법(재사용)**: unpackaged 데모는 computer-use가 못 잡으므로 `FindWindow` +
  > `PrintWindow(hwnd, dc, 2 /*PW_RENDERFULLCONTENT*/)`로 창을 비트맵에 렌더 → 크롭/확대해 확인.

## 4차 전수 리뷰 (2026-07-23) — 진행 중
1~3차가 커버한 범위를 확인한 뒤 재정독. **아직 완주하지 못했고**, 약 4,000줄이 미정독으로 남아 있다.
빌드 0/0, 테스트 57/57.

**정독 완료**: `RichEditor.Input.cs`(1930 전체), `TextRange.cs`, `UndoManager.cs`, `TableCell.cs`,
`RichEditor.cs` 핵심부, `Clipboard.cs` 복사/붙여넣기, `Rendering.cs`, `Pagination.cs`, `Tables.cs`(1–450),
`TableBlock.cs`, `Ime.cs` 합성 경로, `Status.cs`, `FindReplace.cs`, RTF 파서, HTML 포매터 주요부.

**정독 추가분(2차 라운드)**: `Formatting.cs`(463, 2건 발견), `BlockSelection.cs`(292, 이슈 없음),
`TableResize.cs`(247, 1건 발견), `ContextMenu.cs`(568, 이슈 없음 — 선언적 메뉴 구성).

**여전히 미정독(~3,400줄)**: `RichEditorToolbar.cs`(853)+`PageFile`(277),
`RichEditorLocalization.cs`(353), `AutomationPeer.cs`(309), Tables·RTF-writer·DocumentSerializer 잔여분,
HTML 포매터 1–240·680–893. → 배선/문자열 테이블 위주라 수확 기대는 낮다.

### 수정 완료 (3건 + UX 1건, CHANGELOG 참조)
- [x] **IME 합성이 `AfterEdit` 우회** — 셀 안 한글 입력 시 행 높이 미갱신 + 캐럿 스크롤 없음 +
  `TextChanged` 지연. `ReplaceNext`/`ReplaceAll`도 같은 원인의 형제 경로였다.
  **핵심 계약**: 표 행높이 캐시 `_tableRowHeights`를 버리는 경로는 `AfterEdit()`뿐이며
  `RelayoutToViewport()`는 이를 건드리지 않는다 — `AfterEdit`을 우회하는 새 편집 경로를 만들 때 주의.
- [x] **`CreateLayout`의 `CanvasTextFormat` 미해제** — 최다 호출 경로의 네이티브 누수.
- [x] **`FindCell` O(문서) → 부모 체인** + 렌더 핫패스용 `CellTableOf`(O(1)).
- [x] **찾기 바 `▸`/`▾` 바꾸기 전환 셰브런**(사용자 요청).
> **인터랙티브 검증 완료(2026-07-23, 사람 확인)**: 셀 안 한글 입력 행 성장, 셀 안 찾아 바꾸기 행 성장,
> 한글 입력 중 캐럿 스크롤, 셀 드래그 선택, 중첩/인라인 표 캐럿·Tab·Home/End, 셰브런 동작 모두 정상.

### 수정 완료 (2차 라운드 — 정독 진행분 + 실기 리포트)
- [x] **선택이 표를 가로지를 때 행높이 stale**: `AfterFormat`이 캐럿 조상 표만 무효화해, 표 위→아래
  드래그 선택에 서식을 적용하면(캐럿이 표 밖) 그 표의 행 높이가 옛 서식 기준으로 남았다.
- [x] **`SplitByNewlines` 문단 서식 소실**: 서식을 5개 필드만 수기 복사 → `CloneFormat()`으로 교체.
- [x] **리사이즈 핸들 클릭만으로 문서가 "수정됨"**: press 시점 `PushUndo` → 첫 이동으로 지연
  (`PushDragUndoOnce`). 드래그 없는 클릭이 문서를 복제하고 `IsModified`를 뒤집던 것.
- [x] **언두 예산이 인라인 표 미계수** / **HTML `font-weight` 오탐** / **RTF 셀 그림 탈출** /
  **인라인 표 셀 미정규화** / **동기 `ParseHtml`의 UI 블로킹**(⚠️ 공개 API 동작 변경).
- [x] **↑/↓가 표를 건너뛰던 문제**(실기 리포트, 원인 4겹) + **Shift+Enter 캐럿 위치** — 상세는
  CHANGELOG 참조. **핵심 계약**: 인접 형제 표 진입은 포인트 히트보다 **먼저** 평가해야 한다
  (`HitTestBlockList`는 실패 시 "셀의 마지막 문단 끝"을 주는데 이것이 유효한 결과처럼 보인다).
  중첩 표는 `_tableRects`/`_inlineTableRects` 어디에도 없으므로 기하가 필요하면 `_tableOrigins`를 쓸 것.
> **인터랙티브 검증 완료(2026-07-23, 사람 확인)**: 최상위/중첩 표 ↑↓ 진입·탈출, 진입 시 열 보존,
> Shift+Enter 캐럿, 셀 안 한글 입력 행 성장, 셀 드래그 선택, 찾기 바 셰브런 모두 정상.

### 표 셀 목록·붙여넣기·Ctrl+A (2026-07-24, 실기 리포트 후속)
- [x] **셀 안 목록 마커가 렌더링되지 않던 문제**: `DrawListMarkers` 호출부가 최상위 경로뿐이었다.
  `DrawCellBlockList`에 마커 그리기 + 셀 단위 번호를 추가하고, 마커 거터를 렌더·히트테스트·캐럿·측정
  네 walk에 동일 적용하는 `CellParaLeft`를 도입. **핵심 계약**: 셀 문단의 좌측 인셋을 바꾸면 네 walk를
  전부 맞춰야 한다(규칙 #1).
- [x] **셀 안 툴바 목록 토글 무산**: `SetListType`이 캐럿-단독-셀에서 `SplitByNewlines`(doc.Blocks 전용)를
  타 no-op였다 → 셀 문단 제자리 토글.
- [x] **여러 문단 셀 붙여넣기가 평문으로 납작해짐**: `InsertDocumentAtCaret`이 셀 캐럿에서 `PlainTextOf`
  폴백. 컨테이너를 규칙 #3대로 일반화(`MergeContainerOf`). 빈 문단 승계는 `Paragraph.CopyFormatFrom`으로
  (CloneFormat/Clone과 필드 목록 통일).
- [x] **Ctrl+A 단계 선택**(HWP/Excel): 셀 → (중첩 타고 오르는) 표 → 문서.
> **인터랙티브 검증 완료(2026-07-24, 사람 확인)**: 셀 안 목록 마커 + 클릭/캐럿 정합, 툴바 목록 토글,
> 빈 문단 서식 승계, 표 밖/셀/중첩 표 Ctrl+A 단계 선택 모두 정상.

### 미수정 (4차에서 확인된 잔여 결함 — 낮음만 남음)
- [x] **`DrawInlineObjects`만 `GetCharacterRegions` 가드 없음** — `e1b00db`에서 try/catch로 감쌈.
- [x] `EvictLayouts`가 사용 중 레이아웃 dispose 가능 — `e1b00db`에서 `_layoutPinDepth` 핀 카운터로
  walk 중 eviction 유예(`HitInlineTable`/`CaretInInlineTable`/`DrawInlineObjects`에 `LayoutPin`).
- [x] 죽은 `_pressLink.uri` — `e1b00db`에서 `OpenUriAsync(pl.uri)` 오버로드로 press 시점 캡처 URI 사용.
- [x] `TextRange`의 `TopLevelBlockOf`·`RemoveParagraphFromDocument`가 중첩·인라인 표 미재귀 —
  **2026-07-25 수정**(`BlockHolds` 재귀 판정). ⚠️ 4차·6차가 두 번 "호출부(`Delete`)가 셀 엔드포인트를
  감지하므로 무해"로 판정했으나 **틀렸다**: 그 가드는 병합 분기만 막고, 그 앞의 "선택이 가로지른
  최상위 블록 제거" 구간은 `si`/`ei == -1`로 통째로 건너뛰고 있었다. 같은 함수의 다른 분기를 보고
  안전하다고 결론 낸 사례 — 무해 판정은 반증 테스트로 확인할 것.

## 5차 전수 리뷰 (2026-07-24) — 완료
4차의 미정독 잔여분(~3,400줄)을 전량 정독하고 전체를 재스윕. 빌드 그린, 테스트 **67/67**.

**정독 완료**: `RichEditorToolbar.cs`(853)+`PageFile`(277), `RichEditorAutomationPeer.cs`(309),
`HtmlDocumentFormatter.cs`(896 전체), `RtfDocumentFormatter.cs`(976 전체), `DocumentSerializer.cs`(551 전체),
`RichEditor.Tables.cs`(450–681 잔여), `RichEditorLocalization.cs` 스윕.

**결과: 새 결함 0건.** 위젯 배선·이벤트 짝맞춤·Clamp/TryParse 폴백·UIA 범위 재산정(offset 재fetch로 stale
graceful)·RTF 병합셀/코드페이지/필드 파싱·HTML bare-inline/pre/원격이미지 게이트·직렬화 라운드트립 모두 정상.
- 확인된 **무해한 코드 스멜 1건**(수정 불요): `RichEditorToolbar.TryParseHex`의 `v >> (s.Length == 8 ? 16 : 16)`
  삼항이 양쪽 동일(6자리 `RRGGBB`·8자리 `AARRGGBB` 모두 r=비트16이라 결과는 정답).
- 4차 "미수정" 3건은 이미 마지막 커밋 `e1b00db`에서 처리됨을 확인(위 체크박스 반영). 로드맵만 stale였음.

## 파리티 갭 분석 + 수렴 (2026-07-24)
원본 `PublicAPI.Shipped.txt`(499줄) + README 기능 셋을 포트 전체 공개 표면과 대조. **기능 레벨 갭 0건**
(README 전 항목 존재), 포트가 순 신규 API에서 오히려 앞섬. 남은 차이는 공개 API 이름/토글 수준.
방향 결정: **원본은 살아있는 상류** → 양방향·선별 수렴.

### 트랙 1 — 원본 → 포트 (드롭인 소스 호환) — ✅ 완료
`RichEditor.Compat.cs` 신설 + `RichEditorView` 2건. 빌드 라이브러리/데모/테스트 **0/0**.
**실기 검증 완료(2026-07-25, 사람 확인)** — 데모 View 페이지 상단 compat 스트립에서 8개 멤버 전부 호출 확인.
- 원본 이름 별칭(`[EditorBrowsable(Never)]`로 IntelliSense 비오염): `SetFontFamily`→`SetRunFontFamily`,
  `InsertImageBytes`→`InsertImageBlock`, `PasteFromClipboardAsync`→`PasteAsync`.
- 공개 래퍼: `FocusDocumentEnd()`(캔버스 포커스+`GoToDocEdge(end)`), `InsertInlineTable(r,c)`(인라인 삽입 패턴
  네이티브 구현 — `SplitInlinesAt`+`InlineTable`, 변환 우회 아님), `InsertImageFromFileAsync(nint hwnd)`
  (unpackaged 피커 HWND interop 편의 오버로드).
- `RichEditorView.ShowStatusBar`(상태바 토글, 원본 유일 실 갭 해소), `RichEditorView.ZoomFactor`
  (→`Editor.Zoom`/`SetZoom` 프록시 — 줌은 에디터 레벨 유지, 원본 표면만 복원).
- **제외**(원본 타입 종속으로 소스 별칭 불가): `InsertImage(Avalonia.Bitmap)` — 플랫폼 타입 상이라 별칭 무의미.

### 트랙 2 — 포트 → 원본 (플랫폼-무관 개선 역이식) — ✅ 완료 (2026-07-25, 원본 저장소에서 수행)
대상 5건 전부 원본 `AvaloniaRichEditor`에 착륙한 것을 소스로 확인:
`IsModified`/`MarkSaved`/`IsModifiedChanged`(`RichEditor.cs`), `AutoLinkOnType`(`RichEditor.cs`),
찾기 하이라이트+n/m(`SetFindHighlight`/`ClearFindHighlight`/`GetFindMatchPosition` — `RichEditor.FindReplace.cs`),
`RemoveList`(`RichEditor.Formatting.cs`), `AllowRemoteImagesOnPaste`(`RichEditor.Modes.cs`, StyledProperty).
원본은 0.9.0 이후 `f5e647e`(PR #6 winui-parity-backport) → `924d366` → `86427cf` → `bcc12e4`로 진행했고,
5건 외에 **동기 `ParseHtml`의 네트워크 I/O 제거**(포트 4차 변경)·**셀 목록 마커**·**Ctrl+A 단계 선택**까지
함께 가져갔다. **제외**(원본에 이미 존재): `InsertInlineImage`·`SetHyperlink`.
**역이식 금지(유지)**: 줌 아키텍처(원본은 View 줌으로 충분)·이미지 콜백(WinUI HWND 제약 산물)·
Win2D 전용(`CanvasBackground`/`Dispose`).

### 역방향 스윕 (2026-07-25) — 원본 감사 결과를 포트에 대조, 완료
원본의 `924d366`(full source audit 결함 5건)·`86427cf`(backport 결함 7건)·`bcc12e4`를 포트 대응 코드와
1:1 대조. **포트에 남아 있던 결함 4건 수정**, 8건은 포트가 이미 가지고 있음을 확인.

- [x] **찾기 하이라이트가 현재 매치를 흐림**(`DrawFindHighlights`): 앰버 틴트를 **모든** 매치에 칠하고
  그 위에 반투명 선택 파랑이 얹혀, 캐럿이 어느 매치에 있는지 구분이 안 됐다. 선택이 곧 매치인 경우
  그 오프셋만 틴트에서 제외한다(브라우저/VS Code 방식, `GetFindMatchPosition`과 동일한 판정).
- [x] **한 셀 안에서 여러 문단을 선택하고 목록 토글 시 캐럿 문단만 적용**(`SetListType`): 같은 셀 안의
  선택은 `CellBlockSelection`이 아니라(양 끝이 같은 셀) 캐럿 문단만 건드리는 분기로 빠졌다. 셀 분기를
  `SelectedParagraphs()` 기반으로 통합 — 사각 셀 선택·셀 내 다중 문단·무선택을 한 경로로 처리.
- [x] **`LoadHtmlAsync`가 `AllowRemoteImagesOnPaste`를 무시**(`RichEditor.DocumentApi.cs`): 3번째 인자를
  안 넘겨 기본값 `true`가 먹었다. 프라이버시 목적으로 끈 호스트도 `LoadHtmlAsync`에서는 원격 이미지를
  **실제로 다운로드**했다(추적 픽셀). `LoadHtml`/`InsertHtml`도 일관성을 위해 함께 연결 — 동기 파서는
  애초에 네트워크를 안 타므로 그 둘은 실질 영향 없음. 속성 문서도 "paste 전용 아님"으로 정정.
- [x] **블록 오브젝트 선택 중 Ctrl+Shift+X가 잘라내기로 먹힘**: 블록 선택 분기의 Ctrl+C/Ctrl+X가 shift를
  배제하지 않아 취소선 단축키를 삼켰다. `!shift` 추가(원본이 평문 잘라내기 분기에서 맞은 것과 동종).

**포트가 이미 가지고 있어 해당 없음(8건)**: `MergeCells` 다중 블록 일반화(원본만 `.Para` 시절이었음) ·
`TextPointer.CompareTo` 재귀 · Enter/붙여넣기/목록 변환의 문단 서식 승계(`CloneFormat`/`CopyFormatFrom`) ·
셀 목록 마커 · 리사이즈 핸들 no-op 언두 · HTML `font-weight` 오탐 · RTF 셀 그림 탈출 ·
인라인 표 셀 정규화. 중첩/인라인 표의 셀 블록 선택 틴트도 `DrawNestedTable`이 `_renderCellSel`을 보므로 정상.

> **자동 검증 없음**: 4건 모두 렌더 패스 또는 컨트롤 레벨(DP 정적 생성자가 WinUI 런타임을 요구)이라
> 헤드리스 테스트가 불가능하다.
> **실기 검증 완료(2026-07-25, 사람 확인)**: 찾기 하이라이트에서 현재 매치만 파랑, 셀 내 다중 문단
> 목록 토글 전체 적용.

### 목록 토글 완결 + "없음" 중복 제거 (2026-07-25, 사용자 요청)
"툴바 글머리표/번호 ▾의 '없음'은 없어도 될 것 같다"에서 출발. 그냥 빼면 중첩 목록 되돌리기가 우클릭
메뉴에만 남으므로, 토글을 온전하게 만든 뒤 중복을 걷어냈다.
- [x] **토글-오프가 목록 상태를 절반만 지우던 결함**: 끄는 경로 3곳이 `ListType`만 지웠는데
  `ParaLeft`는 `ListLevel * 20`을 **ListType과 무관하게** 더한다 → 중첩 목록을 끄면 마커 없이
  레벨×20px 들여쓰기가 남았다. 새 `ClearList(Paragraph)`(`ListType`+`ListMarker`+`ListLevel`)를
  `RemoveList`와 토글-오프 3경로가 공유. **핵심 계약**: 목록을 끄는 새 경로는 반드시 `ClearList`를
  거칠 것 — `ListType`만 지우면 유령 들여쓰기가 남는다.
- [x] 툴바 ▾ 드롭다운에서 "없음" 제거(스타일 피커는 스타일만), 우클릭 메뉴의 `RemoveList` 3중 노출을
  1개("목록 제거")로, 로컬라이제이션 키 `ListNone` 제거.
> **자동 검증 없음**(컨트롤 레벨). 실기 확인 필요: 중첩 글머리표(Tab으로 레벨↑)를 만든 뒤 툴바
> 아이콘으로 끄면 **들여쓰기까지 원래대로** 돌아오는지, ▾에 "없음"이 사라졌는지.

### 오탐으로 배제 (재조사 방지 — 5차에서 다시 파지 말 것)
붙여넣기 이미지 분기의 `AfterEdit` 누락(내부 호출됨) · 이미지 붙여넣기 `CaretCanHostBlock` 조기 반환
(부모가 FlowDocument/TableCell이라 도달 불가) · `RenderPageToTarget` 미해제(소유권 이전) ·
`Clipboard.cs` 스트림 미해제(DataPackage가 소유) · `TableCell` 문단 불변식(3중 방어) ·
`ComputePageBreaks` vs 블록맵 전진량(동일) · 열 리사이즈 행높이 stale(`InvalidateTableMeasure` 호출) ·
`TableBlock.Clone()`의 `RowHeights.Clear()` 누락(생성자가 시딩하지 않으므로 정상) ·
RTF 코드페이지(`CodePagesEncodingProvider` 등록됨) · `DrawCellBlockList` 전진량(동일) ·
HTML 파서 정적 상태 누수(`[ThreadStatic]`+`finally`) · IME 중 `IsModified` 미발화(즉시 발화됨) ·
`ParseList` 중첩 리스트 이중 파싱(`ul`/`ol`은 인라인 경로에서 skip).

## 6차 전수 리뷰 (2026-07-25) — 완료
전 소스 재정독 + 미커밋 신규 코드(`RichEditor.Compat.cs`, `RichEditorView` 트랙1 변경분) 검토.
빌드 0/0, 테스트 **71/71**(신규 4). 새 결함 **6건**(정독 4 + HWP 실기 리포트 2) — 상세는 CHANGELOG
"6차 전수 리뷰" 절.

### HWP 붙여넣기 실기 리포트 후속 2건 (RTF writer 상호운용)
- [x] **표가 평문으로 풀림**: `\trowd`/`\cellx`/`\cell`/`\row`는 다 있었지만 **행 정의를 한 번만**
  방출했다. HWP처럼 엄격한 리더는 `\row` 시점에 행 정의가 살아 있어야 한다(Word는 셀 앞·`\row` 앞
  두 번 쓴다) → `BuildRowDefinition`으로 두 번 방출 + `\trgaph108\trleft0`·`\itap1`·표 뒤 `\pard\plain`.
- [x] **오른쪽 정렬이 뒤 문단으로 샘**: HWP가 `\pard`를 정렬 리셋으로 취급하지 않는다 →
  왼쪽도 `\ql`로 **모든 문단이 정렬을 명시**.
> **핵심 계약**: RTF writer의 대상은 스펙이 아니라 **실제 리더(HWP/Word)** 다. 스펙상 생략 가능한
> 것도 Word가 항상 쓰는 형태면 그대로 따라갈 것 — 리더 기본값에 기대지 말 것.
> 진단 방법(재사용): 헤드리스 테스트에서 `HtmlDocumentFormatter.ParseHtml(DemoContent.SampleHtml)` →
> 블록 구조 덤프 + `RtfDocumentFormatter.Write` 출력을 파일로 뽑아 실제 RTF를 눈으로 볼 것.
> 스크린샷만 보고 모델을 추측하면 틀린다(이번에도 인라인 표로 오진했다 — 실제로는 최상위 `TableBlock`).

- [x] **RTF 내보내기가 인라인 표("글자처럼 취급")를 통째로 버림** (유일한 실제 데이터 손실,
  사용자 실기 확정). HTML·JSON은 처리하는데 RTF writer만 `InlineTable` 분기가 없었다. 모든 복사가
  RTF를 싣고 Word/HWP가 RTF를 우선하므로 붙여넣기에서 표가 사라졌다. **최상위 문단**은 호스트
  문단을 표 앞뒤로 쪼개고 **진짜 `\trowd` 행**을 방출(다시 읽으면 블록 표), **셀 안**은 subset 밖이라
  텍스트 평탄화 유지.
  **핵심 계약**: 인라인 오브젝트(`InlineImage`/`InlineTable`)를 소비하는 곳은 **네 포매터 모두**
  (HTML/RTF/JSON + 클립보드) 분기를 갖춰야 한다. RTF 파서는 `InlineTable`을 만들지 않으므로
  라운드트립 테스트로는 절대 안 걸린다 — 단방향 writer는 별도 테스트가 필요하다.
  **교훈**: "내용은 살리고 구조는 버린다"(평탄화)는 사용자에겐 **손실과 구분되지 않는다**.
  표는 표로 나가야 한다 — 포맷이 그대로 표현 못 하면 주변을 쪼개서라도.
- [x] **IME `_imeRangeDelta`를 `FormatUpdating`/`SelectionUpdating`이 미적용** → 문단 걸친 선택 위 조합
  시작 시 조합 밑줄이 어긋남. **핵심 계약**: `_imeRangeDelta`가 살아 있는 동안 IME에서 들어오는
  **모든** 범위는 같은 시프트를 받아야 한다(현재 `TextUpdating`/`FormatUpdating`/`SelectionUpdating` 3곳).
- [x] **`RunNormalizer.Compact`가 인라인 표 셀 미재귀** — 4차의 `UndoManager.EstimateBytes`와 같은 사각지대.
- [x] **읽기 전용에서 표 블록 선택·이동 커서가 열려 있음** — 다른 표 상호작용 3곳과 가드 일치화.

### 6차 후속 기능 1건 (2026-07-25, 사용자 요청)
- [x] **읽기 전용 뷰어 캐럿 옵트인** `ShowCaretWhenReadOnly`(기본 off, `RichEditorView`에도 포워딩).
  읽기 전용에서도 키보드 탐색이 전부 동작하는데 위치를 볼 수 없다는 지적에서 나왔다.
  **설계 근거**: 기본 off — 캐럿은 "입력 가능" 어포던스이고 브라우저·PDF 뷰어 관례가 그렇다(이 컨트롤은
  이미 읽기 전용 링크를 브라우저 관례로 맞춰 놨다). **깜빡이지 않음** — 정적=위치표시, 깜빡임=편집가능.
  `RestartBlink`가 읽기 전용에선 타이머를 안 돌린다(캐럿을 숨길 때도 초당 2회 무효화하던 낭비 제거).
  `OnReadOnlyChanged`의 `StopBlink()`가 `_caretOn`을 영구히 꺼 캐럿이 다시 켜질 수 없던 것도 함께 수정.
  데모 "읽기 전용" 페이지에서 켜 둠.

### 무해 4건 정리 (2026-07-25, 사용자 요청) — 완료
6차에서 "확인만 하고 수정하지 않음"으로 남겼던 것들. 하나는 무해가 아니었다.

- [x] **`TextRange.TopLevelBlockOf`/`RemoveParagraphFromDocument` 미재귀** — *무해가 아니었다.*
  둘 다 정확히 한 단계(최상위 표의 셀)만 봐서, **중첩/인라인 표 안의 문단은 "이 문서에 없음"으로 읽혔다.**
  그 결과 `Delete()`가 선택이 가로지른 최상위 블록들을 지우지 않고 남겼다(중첩 셀에서 시작해 뒤쪽
  문단까지 끄는 선택에서 중간 문단이 살아남음). 공용 재귀 판정 `BlockHolds`를 도입해 임의 깊이 셀 +
  인라인 표까지 훑는다(`CollectParagraphs`와 같은 논리 셀 순회). **회귀 테스트 추가** — 수정 전 FAIL을
  확인했다. 4차·6차 두 번 "호출부가 막고 있어 무해"로 판정했던 건이라, 그 판정 근거(`Delete`의 셀
  엔드포인트 가드)가 **다른 분기**를 막을 뿐 이 경로는 막지 않았음을 놓쳤다.
- [x] `ImageCache.Clear()` 삭제(사문화). 문서 교체는 의도적으로 `Prune(liveKeys)`를 쓴다 — undo 스냅샷이
  공유하는 비트맵을 살려두기 위해서다. 필요하면 빈 집합 `Prune`이 곧 `Clear`라고 주석에 남겼다.
- [x] `RichEditorToolbar`가 `Target.StatusChanged`를 `Unloaded`에서 해제하도록(+`Loaded`에서 재구독,
  `-=` 후 `+=`로 멱등). 에디터가 핸들러를 쥐고 있어 툴바만 분리하면 툴바 전체가 도달 가능하게 남았다.
- [x] `OnPaperChanged`의 `SyncPage()`를 `_suppress`로 감쌈(SelectionChanged 핸들러에서 호출되므로
  suppress가 걸려 있지 않은 상태였다). `Sync()`가 하는 것과 동일하게 맞췄다.

> **실기 검증 완료(2026-07-25, 사람 확인)**: 블록 표·인라인 표 모두 HWP에 격자로 붙고 정렬 정상.
>
> **실기 미검증 + 검증 방법**:
> - **IME 조합 밑줄**: **정방향(아래로) 드래그만 재현된다.** 델타는 "IME에 보고된 캐럿"과 "선택 삭제 후
>   실제 캐럿"의 차이인데, 역방향 드래그는 캐럿이 이미 병합 지점이라 델타가 0이다. 긴 문단의 **오른쪽
>   깊은 지점**(예: 20번째 글자)에서 시작해 **아래 문단 앞쪽**(2~3번째)까지 선택 → 한글 자모 입력 →
>   조합 중인 글자 바로 밑에 밑줄이 있으면 정상(수정 전엔 ~17글자 왼쪽에 그려졌다).
> - ~~**뷰어 표 테두리**~~ — **검증 완료(사람 확인)**. 확인 방법 기록: 읽기 전용은 기본적으로 캐럿을
>   안 그리므로(`ShowCaretWhenReadOnly` off) "캐럿이 놓이는지"로는 확인할 수 없다. 표 좌/상단 테두리에서
>   ① 호버 시 이동(✥) 커서가 뜨지 않고 I-beam 유지 ② 클릭해도 파란 선택 테두리가 안 생김
>   ③ 드래그하면 텍스트 선택 하이라이트가 정상 — 이 셋으로 확인한다.
> - ~~**읽기 전용 캐럿**(신규)~~ — **검증 완료(2026-07-25, 사람 확인)**: 데모 "읽기 전용" 페이지에서
>   캐럿 표시·비깜빡임 정상.
> - ~~**파리티 트랙1 API**~~ — **검증 완료(2026-07-25, 사람 확인)**. 데모 View 페이지 상단의 compat
>   스트립(`SetFontFamily`·`InsertImageBytes`·`PasteFromClipboardAsync`·`FocusDocumentEnd`·
>   `InsertInlineTable`·`InsertImageFromFileAsync`·`ShowStatusBar`·`ZoomFactor`)에서 8개 멤버 전부 통과.
>   스트립은 상시 유지 — 앞으로 compat 표면이 늘면 여기에 버튼을 더한다.

> **데모 GUI 검증 워크플로(재사용)**: `--page=control|readonly|toolbar|view` 인자로 시작 페이지를 지정한 뒤
> `PrintWindow`로 캡처한다. 캡처는 열려 있는 페이지만 보여주므로 이 인자가 없으면 첫 페이지밖에 못 본다.
> ⚠ 산출물 경로 함정: 데모 exe는 **`bin\x64\Debug\...`**에 나온다. `bin\Debug\...`에도 옛 exe가 남아 있어
> `Get-ChildItem -Recurse | Select -First 1`로 잡으면 어제 빌드를 띄운다(테스트 DLL 함정과 같은 부류).

> **오진 기록(재발 방지)**: 데모의 h2 "표"가 표 왼쪽 아래에 겹쳐 보인 것은 **버그가 아니라** 사용자가
> 그 표를 "글자처럼 취급"으로 바꾼 상태였기 때문이다(인라인 오브젝트가 든 줄은 줄 높이가 오브젝트
> 높이가 되고 텍스트는 베이스라인 = 아래쪽에 앉는다 — 인라인 이미지와 동일). 이때 내가 뜬 모델 덤프는
> `ParseHtml` 직후의 **원본**(블록 표)이라 화면 상태와 달랐다. **스크린샷과 덤프가 같은 문서 상태인지
> 먼저 확인할 것.**

## 7차 전수 리뷰 (2026-07-25) — 완료
6차 이후 이 세션에서만 소스 24개 파일 722줄이 바뀌었으므로, **자기 변경분 감사**를 1순위로 두고
(새로 넣은 결함이 가장 위험하다) 그동안 한 번도 열지 않은 파일을 이어서 정독했다.
빌드 0/0, 테스트 **74/74**(신규 1). 새 결함 **1건**.

- [x] **인라인 표 앞에 빈 문단이 새던 문제**(`RtfWriter.WriteParagraph`, 6차의 인라인 표 승격이 넣은 결함):
  호스트 문단을 표 앞뒤로 쪼갤 때 `\par`를 **무조건** 먼저 방출했다. 표가 문단의 **첫 내용**일 때 —
  즉 "글자처럼 취급"의 통상적 형태(문단이 표 하나만 담음) — 그 `\par`가 빈 문단을 닫아 Word/HWP에서
  표 위에 빈 줄이 생겼다. 내용이 실제로 쓰였을 때만 닫도록 `wrote` 플래그 도입. 표 **뒤**의 빈 문단은
  RTF가 요구하므로 그대로 둔다. 회귀 테스트 추가 — 수정 전 FAIL 확인.

**정독/검증 완료, 이상 없음**: 이번 세션 변경분 전체(`RichEditor.Formatting`의 `ClearList` 통합 3경로 ·
`TextRange.BlockHolds` 재귀 · RTF 행 정의 2회 방출과 파서 재진입 · `DrawFindHighlights`의 현재 매치 제외 ·
`ShowCaretWhenReadOnly`/`RestartBlink`/`OnReadOnlyChanged` · 툴바 Hook/Unhook 멱등성 · 데모 `--page`),
그리고 `TableBlock`(구조 편집·병합·Extract·Clone 전부, 5차의 `RowHeights.Clear()` 판정 재확인),
`DocumentSerializer` DTO↔모델 양방향, `ImageInfo`/`ImageEncoder`/`DocumentPackage`/`InlineObjects`/
`RoundTripHarness`.

### 오탐/무해로 확인 (근거를 실제로 확인함)
- `DocumentSerializer`의 표 셀이 `Parent`를 세팅하지 않음 — **모든** 로드 경로가
  `LoadDocument`→`Document` DP→`OnDocumentAssigned`→`UpdateParents`를 지나므로 컨트롤이 채운다(경로를
  따라가 확인했다. "아마 괜찮다"로 넘기지 않는다 — 6차의 `TextRange` 오판이 그렇게 나왔다).
- 손상 JSON의 `Cells: null` → `Rows=0` 표. 레이아웃/네비/정규화 전부 빈 그리드에서 무해하게 통과하고
  높이 0으로 렌더된다. 정상 writer는 항상 Cells를 쓴다.

## 0.9.0 릴리스 준비 (2026-07-25) — 완료
게시 파이프라인은 이미 있다: `.github/workflows/publish.yml`이 `v*` 태그 푸시에 pack → nuget.org
(Trusted Publishing/OIDC, 장기 키 없음, user `kanu`). 따라서 준비 작업은 전부 **태그 밀기 전에 맞출 것**이다.

### 완료
- [x] **의존성 상향 + PRI 워크어라운드 제거** — WindowsAppSDK 2.2 → **2.3.x**
  (라이브러리 `Microsoft.WindowsAppSDK.WinUI` **2.3.2**, 데모 메타패키지 **2.3.1**, SDK.BuildTools/Test.Sdk 동반).
  `_StripStaleWinAppSdkRuntimePri` 삭제. csproj/CLAUDE.md/로드맵의 stale 문구 정정.
- [x] **AOT 게시 재검증(실측)** — 워크어라운드 없이 self-contained `win-x64.pubxml` 게시 성공:
  총 **85.2MB**, 네이티브 exe **14.2MB**, `coreclr.dll`·관리형 `WinUIRichEditor.dll` 부재(= 진짜 AOT),
  Windows-ML 다이어트 타깃 동작, **실행 + Win2D 렌더 확인**(PrintWindow 캡처). 빌드 0/0, 테스트 74/74.
  *(이전 기록 80.3MB는 WinAppSDK 2.2 기준. 2.3에서 ~5MB 증가.)*

### 남은 것 — ✅ 전부 완료, `v0.9.0`·`v0.9.1` 게시됨
버전 결정·CHANGELOG 재구성·README 정정·NuGet 소비자 스모크·태그 푸시 모두 끝났다.
(`PublicAPI.Shipped.txt` 도입은 "0.9.0 이후" 후속 후보였고 **2026-07-31 트랙 E에서 완료**했다.)
