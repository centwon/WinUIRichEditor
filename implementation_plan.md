# WinUIRichEditor 구조 개선 및 최적화 구현 계획 (Implementation Plan)

지금까지의 탐색(Discovery)을 통해 발견된 `WinUIRichEditor` 프로젝트의 치명적인 성능 병목(O(N) 지연) 및 기능적 버그들을 해결하기 위한 구현 계획입니다.

## User Review Required

> [!WARNING]
> 이 수정 계획은 에디터의 핵심 렌더링, 레이아웃 측정, 탐색, 편집 등 **거의 모든 기본 동작의 기반 코드를 변경**합니다. 성능은 비약적으로 향상되지만, 내부 캐시와 트리 탐색 로직이 근본적으로 수정되므로 적용 후 충분한 테스트가 필요합니다.

## Open Questions

> [!IMPORTANT]
> 1. `UndoManager`의 경우 깊은 복사(Deep Clone)를 사용하고 있어 CPU/메모리 낭비가 큽니다. 당장은 Undo에 소요되는 O(N) 인덱스 검색을 최적화할 계획이지만, 추후 Delta 기반 스냅샷으로 완전히 재작성할 의향이 있으신가요?
> 2. 리팩토링 시 기능적 무결성 유지를 위해 자동화된 UI 단위 테스트 코드를 추가로 작성해야 합니까, 아니면 런타임 성능 측정만으로 충분합니까?

---

## Proposed Changes

문제를 해결하기 위한 코드 수정 사항을 모듈(기능)별로 그룹화하여 제안합니다.

### 1. 코어 트리 탐색 최적화 (O(N) -> O(1))

문서 내의 특정 단락(Paragraph)이나 셀(Cell)을 찾을 때 문서 전체를 훑는 구조를 폐기하고, 객체의 `Parent` 포인터를 역추적하여 O(1) ~ O(Depth) 시간 복잡도로 즉각 찾을 수 있도록 개선합니다.

#### [MODIFY] [RichEditor.Tables.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.Tables.cs)
- `FindCell(Paragraph p)` 메서드 재작성: `FindCellIn`을 통한 재귀 순회를 삭제하고, `p.Parent`를 타고 올라가며 `TableCell`과 `TableBlock`을 찾도록 O(1) 로직으로 교체합니다.
- (효과): 렌더링 시 매 프레임 발생하는 렉, Home/End 키 이동 시의 렉, 각종 서식 지정 렉이 모두 해결됩니다.

---

### 2. 레이아웃 캐시 붕괴 버그 수정 (스크롤링/페이지네이션 성능)

캐시 용량 초과 시 전체 캐시를 날려버리는 `Clear()` 호출을 제거하고, 오래된 항목만 제거하는 LRU(Least Recently Used) 알고리즘 혹은 안정적인 용량 관리 기법을 도입합니다.

#### [MODIFY] [RichEditor.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.cs)
- `EvictLayouts()`: `_layoutCache.Clear()`를 삭제하고, 접근 시간을 추적(Timestamp)하여 가장 오래된 레이아웃 N개만 삭제하도록 변경.

#### [MODIFY] [RichEditor.Pagination.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.Pagination.cs)
- `_lineCache`를 관리하는 코드에서 캐시 사이즈 오버플로우 시 `Clear()` 하는 버그 수정.
- 페이지 브레이크 계산 시 순차 탐색 대신 이진 탐색(Binary Search) 도입 여부 검토 및 캐시 무효화 범위 최소화.

---

### 3. 타이핑 렉(Input Lag) 주범 제거

타이핑(Keystroke) 마다 수행되는 무거운 O(N) 연산들을 제거하여 실시간 입력 성능을 확보합니다.

#### [MODIFY] [RichEditor.Modes.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.Modes.cs)
- `GetImageCount()` 수정: 트리를 순회하지 않도록, `RichEditor` 전역 상태에 `_imageCount` 변수를 캐싱. 이미지 삽입/삭제시에만 해당 변수를 증감(Delta Update) 시키도록 변경.
- **버그 수정**: 기존 `GetImageCount()`가 중첩 표(Nested Table) 안의 이미지를 카운트하지 못하던 버그도 이 과정에서 자연스럽게 해결됨.

#### [MODIFY] [RichEditor.Input.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.Input.cs)
- `AfterEdit()` 내부에서 `CheckImageLimit()` 호출 로직 구조 개선 (또는 백그라운드 지연 실행/Debounce 처리).

---

### 4. 선형 탐색(Flattening) 병목 및 버그 제거

`GetAllParagraphsInOrder`나 `AllParagraphs`처럼 전체 문서를 1차원 리스트로 만드는 무거운 연산을, 필요한 구간만 탐색하도록 변경합니다.

#### [MODIFY] [RichEditor.FindReplace.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.FindReplace.cs)
- `ReplaceAll`: 매 루프마다 `AllParagraphs()`를 두 번씩 호출하는 O(M*N) 로직을 수정하여, 한 번 생성한 리스트와 인덱스를 재활용(Iterator 활용) 하도록 변경.

#### [MODIFY] [TextRange.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Documents/TextRange.cs)
- `ApplyPropertyValue`, `GetText`: 서식 변경 시 `GetAllParagraphsInOrder` 대신 `TextPointer` 간의 구조적 거리를 계산하거나 `Parent`를 활용하도록 리팩토링.
- **버그 수정 (`RemoveParagraphFromDocument`, `TopLevelBlockOf`)**: 단락 삭제 시 최상위 레벨 표만 검사하여 중첩 표(Nested Table)의 삭제가 무시되는 버그 수정. 재귀적 또는 `Parent` 트리를 통한 정상 삭제 로직 구현.

#### [MODIFY] [RichEditor.Formatting.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.Formatting.cs)
- `SetListType`: `targets.OrderByDescending(t => Document.Blocks.IndexOf(t))`의 O(N^2) 정렬 로직을 제거하고, 미리 인덱스를 캐싱한 후 정렬하여 O(N log N)으로 개선.

---

### 5. 기타 버그 수정

#### [MODIFY] [RichEditor.Ime.cs](file:///c:/Users/centw/source/repos/WinUIRichEditor/src/WinUIRichEditor/Controls/RichEditor.Ime.cs)
- `ClientOriginOnScreen`: `GetActiveWindow()` Win32 API 사용을 폐기하고, `IWindowNative` 또는 XamlRoot 기반의 정확한 HWND 포인터 바인딩으로 교체하여 IME 한글/한자 후보 창이 모니터 허공에 뜨는 팝업 이탈 버그 수정.

---

## Verification Plan

### Manual Verification
- 대용량 문서(10,000줄 이상, 중첩 표 포함)를 로드하여 다음을 테스트합니다.
  - 빠르게 위아래 스크롤 시 화면 끊김(렉) 여부 확인.
  - 키보드 방향키 및 Home/End 키 입력 시 응답 속도 확인.
  - 긴 문단 가운데서 문자를 빠르게 타이핑 시 입력 지연 여부.
  - 굵게(Bold) 등의 텍스트 서식 핫키 사용 시 프리징 여부.
  - 중첩 표 내부의 이미지가 정상적으로 카운트 되는지, 중첩 표 내용을 선택하고 Delete를 눌렀을 때 텍스트가 정상 삭제되는지 확인.
  - "모두 바꾸기" 실행 후 CPU 스파이크 및 앱 크래시 방지 테스트.
  - 멀티 윈도우 띄운 상태에서 한글 입력 시 조합 창(IME 후보)이 올바른 텍스트 커서 위치 하단에 붙는지 확인.
