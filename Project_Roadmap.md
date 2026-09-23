# Project Roadmap — WinUIRichEditor

WinUI 3 + Win2D 리치텍스트 에디터. `AvaloniaRichEditor`의 WinUI 포트로 출발했고, 지금은 **대등한 수렴 peer**다.

| 문서 | 내용 |
|---|---|
| 이 파일 | 현재 상태 · 다음 우선순위 · 알려진 한계 · 검증 레시피 · 고정된 결정 |
| [`CHANGELOG.md`](CHANGELOG.md) | 릴리스별 변경(사용자 관점) |
| [`docs/roadmap-archive.md`](docs/roadmap-archive.md) | 날짜별 개발 이력 전체 — Phase 0~6 포팅, 전수 리뷰 1~7차, 1.0 대조, 1.0 이후 감사 라운드 |
| [`docs/DOCUMENT_FORMAT.md`](docs/DOCUMENT_FORMAT.md) | `.flow`/JSON 문서 형식 명세 |
| [`CLAUDE.md`](CLAUDE.md) | 기술 기반 · 포팅 사전 · 비자명한 핵심 규칙 |

---

## ✅ 현재 상태 (2026-09-20 · `1.2.0` 게시)

| | |
|---|---|
| 릴리스 | 1.0.0(07-31, **API 동결**) → 1.1.0(08-07) → 1.1.1(09-06, AOT 결함 수정) → **1.2.0(09-20 게시)** |
| 빌드 | 라이브러리 0 warn / 0 err (테스트 프로젝트에 xUnit1031 1건 — 블로킹 대기, 기존) |
| 테스트 | **815** (1.0 시점 106). OS 클립보드 테스트는 경합으로 간헐적 빨강 — 단독 재실행으로 확인 |
| AOT | self-contained 게시 성공(2026-09-20): 네이티브 exe **15.2MB**, 게시 **74.6MB**(pdb 제외), CoreCLR·관리 dll 없음 |
| 공개 표면 | **574** — `PublicAPI.Shipped.txt`로 추적, 1.2.0은 **추가 7**(breaking 없음) |

**1.2.0에 들어간 것**(상세는 CHANGELOG):
- **검증 축을 세웠다** — 줌 · 편집 명령 · 기능 플래그 · 문서 API · 하이퍼링크 · 캐럿 서식 보고 · 호스트
  이벤트 · 공개 멤버 잔여 · 표 크기 끌기 · AOT 줄 메트릭 · 테스트 참조 0 파일. **결함 40여 건**을 이 축들이 잡았다.
- **기능**: 벡터 인쇄·PDF(`SavePdf`), 표·그림 끌어 옮기기(Ctrl=복사), 그림 가로·세로 손잡이, 한 셀 선택(F5)과
  Shift+방향키 셀 블록, 찾기 UI(Ctrl+F/H·F3), XAML 바인딩 가능한 툴바·뷰 속성 7개.
- **동작 결정**(사용자 승인, 아래 "결정 사항"): Word 방식 문자 토글 · 제목 서식을 글자 속성으로 · 줄 간격 HWP 기준(기본 160%) ·
  손상 입력은 거부하고 열린 문서를 지킨다.
- **메모리·성능**: 그림을 그려지는 크기로 디코드, 지운 그림 즉시 해제, 되돌리기 예산에 그림 포함,
  그림 복사 일시 할당 352MB → 160MB.

**상류와의 관계**: 공유 설계(포매터·문서 모델·편집 규칙)를 건드릴 때는 상류를 **먼저 대조**할 것 — 대개
그쪽이 같은 문제를 이미 겪었다. 공개 표면 대조는 눈대중 말고 상류 `PublicAPI.Shipped.txt`와 **기계적으로**
(눈대중 한 번이 플래그 9개 중 8개를 놓쳤다). 이번 주기에도 백포트가 양방향으로 오갔다(상류 PR #26~#44).

### 1.2.0 릴리스 체크리스트
- [x] `<Version>` 1.2.0 + `PackageReleaseNotes` 재작성
- [x] `PublicAPI.Unshipped.txt` → `Shipped.txt` 이관(7개), Unshipped 비움
- [x] CHANGELOG `[1.2.0]` 절 · README 상태/수치 갱신 · 문서 정리
- [x] AOT self-contained 게시(위 표) — **빌드·크기까지만**
- [x] **동등성 스윕**(`tools/fault-sweep.ps1`, JIT vs AOT) — 2026-09-20 실측, `control`·`toolbar` 두 페이지
      모두 되읽은 문서가 **바이트 동일**(713자), AOT 전용 fault는 `LineMetricsOf`(비blittable 배열, 폴백 정상
      발동) **하나뿐**. 1.1.0이 깨진 채 나간 지점이므로 릴리스마다 생략 금지 — 스크립트가 데모에 입력을
      주입하고 클립보드를 덮으므로 사람이 있는 데스크톱에서 돌릴 것
- [x] **NuGet 소비자 스모크 15/15**(2026-09-20) — 로컬 feed + `nuget.config` + `PackageReference`(ProjectReference
      아님)로 unpackaged WinUI 앱을 세워 빌드 0/0·실행. 확인: 패키지에서 온 1.2.0 어셈블리 · 복원된 패키지의 XML 문서 ·
      JSON/HTML/RTF/`.flow` 왕복 · `HeadingFormat` 표식 · **`{}` JSON과 `document.json` 없는 zip이 예외를 던지고
      열린 문서를 지킨다** · 제목이 런에 굵게·크기를 쓴다 · 새 DP 7개가 `SetValue`로 CLR 속성에 닿는다 ·
      `FindNext`/`FindAgain`/`LastFindQuery` · `SavePdf`.
      ⚠ "PDF가 써졌다"만으로는 벡터인지 비트맵 폴백인지 모른다 — 산출물을 열어 `Producer: Microsoft: Print To PDF`,
      **글꼴 서브셋 2 + ToUnicode 2, 이미지 XObject 0**으로 벡터 경로임을 확인했다.
- [x] `v1.2.0` 태그 푸시(2026-09-20) → PR #47 머지(CI 초록) · publish 워크플로 success(`.nupkg`·`.snupkg`
      push 완료, Trusted Publishing) · [GitHub Release](https://github.com/centwon/WinUIRichEditor/releases/tag/v1.2.0) 작성.
      nuget.org 검색·복원 인덱싱은 push 뒤 수 분에서 길게는 한 시간까지 걸린다 — 안 보인다고 재게시하지 말 것

## 다음 우선순위 (1.3)

1. ~~**포인터 순서를 재현하는 상호작용 테스트**~~ → **2026-09-20 1~3단계 완료**(아래 절). 남은 것은
   프레임워크의 라우팅·히트테스트·포커스뿐이고, 그건 여전히 `fault-sweep` + 실기의 몫이다.
2. **실기에만 있는 검증 항목**: AltGr 자판(처리기 배선은 `KeyRoutedEventArgs`를 만들 수 없어 자동 검증 밖),
   IME 조합, 포커스·캐럿 깜빡임.
3. **상류 백포트 잔여**: 라운드31~32 이후 상류 변경분 대조. 라운드34(상류 PR #53)는 2026-09-23 옮김(아래 절).
   PR #50·#52(페이지 여백·여백 후속)는 2026-09-24 옮김 — CHANGELOG `[Unreleased]`. 목록에 있던 "그림 손잡이 vs 열 경계
   우선순위"와 테스트 `CtrlU_AtALinksEnd_LeavesTheLinkAlone`은 이미 들어와 있었다(`ControlImageResizeTests`·
   `ControlCaretFormatTests`). PR #48(표 행·열 공개 API)·#49(단축키 표 공개)도 2026-09-24 옮김 — 상류 PR #53까지
   대조 끝. (`Gesture(id)`는 Avalonia 전용이라 뺐다. enum 순서는 포트 것 유지.)
4. 아래 "알려진 한계"의 미수정 항목.

### 상류 라운드34 백포트 (2026-09-23) — 테스트 832 → 862, 결함 14(보안 2 포함) + 동작 변경 3, 반증 완료
상류 결함 32건과 결정 4건을 **이 포트에서 먼저 측정**했다. 빨강이 된 것만 고쳤다(상세는 CHANGELOG).
- **보안 2건**: UNC 그림(SMB/NTLM), 스크립트 링크 — 둘 다 포트에도 있었다.
- 이미 맞던 것이 많다 — 포트는 블록 위치 맵으로 그리고(디코드 실패 그림이 자리를 지킴), 인용 막대를 한 함수로 그리며,
  인라인 그림 삭제가 캐럿을 옮긴다. 대응 코드가 없는 것: 래스터 PDF 채널 순서(포트는 Print to PDF), `IsCellOf`.
- ⚠ **헛도는 테스트 둘을 잡았다**: ① 맨 앞 삽입을 볼 때 문서 첫 빈 문단을 걸러 내 포트의 빈 머리 문단까지 가림
  ② 캐럿이 문서 안인지를 **부모 사슬**로 봄 — 떨어져 나간 노드도 옛 부모를 기억해 "문서 안"으로 나온다. 문서의 문단
  목록(`AllParagraphs`)으로 볼 것.
- 여백 메뉴 클릭을 `PickMargin`으로 분리(메뉴 항목의 Click은 코드에서 일으킬 수 없다).

### 포인터 파이프라인 (2026-09-20) — 1~3단계 완료, 테스트 815 → 832, 결함 1건(포트 전용), 반증 16종
포인터 처리기가 이벤트에서 읽는 것은 **위치·오른쪽 버튼·모디파이어·캡처** 넷뿐이다. 그것을 `PointerStep`으로
묶고 캡처를 `IPointerCapture`로 추상화해(`RichEditor.PointerPipeline.cs`) 처리기를 어댑터로 줄였다. 테스트는
`FakeCapture`로 **누르기→캡처→이동→놓기→캡처상실**을 실제 순서로 구동한다 — 해제가 캡처 상실을 **동기로**
일으키는 것까지. 포인터 경로의 `Ctrl`/`Shift` 정적 키보드 읽기도 전부 step으로 옮겨, Ctrl 복사 끌기·Ctrl+클릭
링크·Shift 선택·Ctrl+휠이 처음으로 자동 검증에 들어왔다. 공개 표면 변화 없음(전부 `internal`).
- **결함(포트 전용)**: **캡처를 잃은 텍스트 끌기가 무장된 채 남아 다음 클릭이 드롭을 수행했다.** 옛 주석은
  이것을 의도라고 적고 있었다("끝내면 텍스트가 떨어진다") — *끝내기*와 *떨어뜨리기*는 다르다. 개체 끌기와
  같은 규칙으로 `CancelTextDrag()`를 만들어 캡처 상실·문서 교체에서 부른다. 상류엔 문서 내 텍스트 끌기가
  없어 대조할 선례가 없었다.
- ⚠ **반증이 가르쳐 준 두 가지**: ① **열 끌기 놓기 순서를 뒤집어도 아무것도 안 빨개진다** — 캡처 상실이
  열 끌기는 *취소*가 아니라 *완료*하고 `FinishColumnResize`가 멱등이라 실제로 무해하다(상류와 같다).
  순서 규칙이 힘을 쓰는 곳은 **캡처 상실이 취소하는 경로**(표 그리기·개체 끌기)뿐이다. ② **가짜가 캡처
  상실을 동기로 안 쏘면 깨진 순서도 통과한다** — 보증이 가짜의 충실도에 얹혀 있으므로 호출 순서
  `capture → release → lost`를 테스트가 직접 단정한다.
- ⚠ **하네스 함정(제품 아님)**: 공유 에디터에 **같은 좌표를 밀리초 간격으로** 누르면 다중 클릭이 되어
  누르기가 끌기 무장 대신 단어 선택이 된다. 실패 메시지는 제품 결함처럼 보인다 — `Hosted`와 `NoRepeat`가
  `_lastPressTime`/`_clickCount`를 초기화한다.
- **테스트 seam 1개 추가**(`internal`): `RichEditor.LaunchOverride` — 어느 제스처가 링크를 여는지는
  브라우저를 띄우지 않고는 단정할 수 없었다. 결정(`IsLaunchableLink`)은 전부터 직접 시험돼 있었고, 이제
  거기 **닿는 경로**까지 덮는다.

## 알려진 한계 (의도적)
- **HWP는 RTF 중첩 표를 구현하지 않는다** — `\nestcell`을 버리고 중첩 셀 텍스트를 이어 붙인다. 우리 출력은
  상류와 바이트 동일하고 Word는 정확히 읽는다. 포트 결함이 아니다.
- 모든 행이 똑같이 병합된 표는 RTF에서 한 열로 접힌다(파일에 격자 단서가 없다 — 렌더는 동일).
- 중첩 표의 **열 너비**는 RTF의 무시 가능 그룹에 있어 기본값으로 들어온다.

## 재사용할 검증 레시피
이 코드베이스에서 결함을 실제로 잡아온 수단은 정독이 아니라 아래다.

- **AOT 동등성 스윕** (`tools/fault-sweep.ps1`) — **AOT는 렌더돼도 동등하지 않을 수 있다**(1.1.0이 그래서
  깨졌다). 같은 편집 순서를 JIT와 AOT 게시본에 돌려 ① 라이브러리가 보고한 fault 집합 ② `Ctrl+A`/`Ctrl+C`로
  되읽은 문서 텍스트를 비교한다. 채널은 데모의 `--faultlog=<경로>`.
  ```
  dotnet publish samples/.../WinUIRichEditor.Demo.csproj -c Release -r win-x64 --self-contained true \
    -p:Platform=x64 -p:PublishAot=true -p:PublishTrimmed=true
  tools\fault-sweep.ps1 -Exe <jit exe> -Log C:\tmp\faults-jit.txt   # 대조군
  tools\fault-sweep.ps1 -Exe <publish\...exe> -Log C:\tmp\faults-aot.txt
  ```
  ⚠ **키만 주입하면 아무 데도 안 간다** — 데모 컨트롤 페이지는 클릭 전엔 캔버스에 포커스가 없어, 입력 0인
  세션이 "fault 없음"으로 정상처럼 보인다(그래서 phase 0에 마우스 클릭이 있다). 되읽은 문서가 비면 비교는
  공허하다. `tools/uia-probe/drive-demo.ps1`에는 아직 이 클릭이 없다.
- **랜덤 편집열 퍼즈** (`DocumentFuzzTests`) — 정독이 못 보는 "안 시험한 조합"을 잡는다. 포매터를 건드리면
  `RICHEDITOR_FUZZ_SEEDS=20000`으로 **시드를 넓혀** 돌릴 것(기본 400, 결함 하나는 200시드에서만 나왔다).
- **왕복은 2회** — 1회로는 "구분자가 내용이 되어 매번 쌓이는" 계열이 안 보인다(실제로 세 번 나왔다).
- **반증(falsify)** — 수정을 되돌려 새 테스트가 **빨강이 되는지** 확인. 공허한 테스트를 여러 번 잡았다
  (16:1 정수비라 결함이 가려진 앨리어싱 테스트, 현재 대상만 읽어 누수가 안 보이던 구독 테스트).
- **성능 측정**: `$env:RICHEDITOR_PERF=1` + `dotnet test ... --filter PerfProbe` → `perf-report.txt`
  (경로는 `RICHEDITOR_PERF_REPORT`). **Release로 돌릴 것**(Debug는 `WeakReference` 판정이 다르게 나온다).
  ⚠ 성능 가설은 **계측으로 먼저 확인**할 것 — 읽어서 세운 후보는 전부 빗나갔고, 임시 카운터 하나가 한 번에 찾았다.
- **UIA 알림 측정** (`tools/uia-probe`) — 데모 실행 → `dotnet run --project tools/uia-probe -- 22` →
  다른 셸에서 `drive-demo.ps1`. ⚠ "UIA에 노출되는가"만 보면 알림이 하나도 없는 빌드도 통과한다 — **이벤트 수**를 볼 것.
  내레이터로는 이 검증을 못 한다(소리뿐).
- **캐럿 깜빡임 측정** (`tools/measure-caret-blink.ps1`) — 60ms 간격 `PrintWindow` 캡처의 픽셀 변화 간격.
  켬 → 평균 ≈ `CursorBlinkRate`, 끔 → 변화 0회 **+ 캐럿은 계속 보여야 한다**. ⚠ 변화 0회는 단독으로 증거가
  아니다(포커스 없는 에디터도 0). 설정은 `control keyboard` 슬라이더로 바꿀 것 — 레지스트리를 고치면 재로그인해야 반영된다.
- **창 캡처**: `tools\capture-window.ps1 -ProcessName WinUIRichEditor.Demo -Out shot.png`. WinUI 3는
  `PW_RENDERFULLCONTENT`(플래그 2)여야 캔버스가 나온다. 데모는 `--page=control|readonly|toolbar|view`로 시작 페이지 지정.

### 함정 (실제로 밟은 것들)
- **테스트 파일을 `%TEMP%` 스크래치패드에 두지 말 것** — WinRT `StorageFile`이 거부해 피커가 `E_FAIL`로 던진다.
  대화상자가 아예 안 떠서 "파일이 안 열린다"로 보인다. 픽스처는 **`Documents`** 에.
- **데모 빌드 순서**: `dotnet build WinUIRichEditor.slnx` **먼저**, 그다음 데모 csproj(데모는 AnyCPU 출력을
  참조한다). 데모만 빌드하면 "성공"이라면서 옛 라이브러리를 들고 간다 — **DLL 타임스탬프로 확인**할 것.
  데모 exe는 `bin\x64\Debug\...`에 나온다.
- **테스트 실행은 `dotnet test <csproj>`**(빌드 포함). `--no-build`는 다른 출력 경로의 스테일 DLL을 돌린다.
- **NuGet 소비자 스모크**: 데모 csproj를 본떠 `WindowsPackageType=None` + 메타패키지 + `WinUIRichEditor`
  `PackageReference`. ① 비동기 API를 UI 스레드에서 `.GetResult()`로 블록하면 **데드락** ② `DispatcherQueueTimer`를
  지역 변수로 두면 GC됨 ③ `FocusEditor()`는 레이아웃 전 `false`(실패 아님) ④ **패키지의 XML 문서는 소비자 출력
  폴더로 복사되지 않는다** — 복원된 패키지에서 볼 것.
- **OS 클립보드 테스트는 간헐적으로 빨갛다**(다른 앱과의 경합). 단독 재실행으로 먼저 확인하고 diff를 의심할 것.

## 결정 사항 (고정)
되돌리기 전에 여기를 읽을 것. 전부 측정 또는 사용자 결정으로 고정된 것이다.

- **줌은 엔진이 인다**(상류는 뷰의 `LayoutTransformControl`) — **수렴하지 않는다.** `CanvasVirtualControl`은
  즉시모드 래스터 표면이라 위에 XAML 변환을 걸면 이미 래스터화된 출력을 확대해 200/300%에서 글자가 뭉갠다.
  크리스프하려면 스케일이 `ds.Transform`까지 내려가야 하고, 그 순간 엔진이 줌을 알아야 한다. 대가(`EffectiveZoom`
  15곳, `ViewToDoc`/`DocToView` 16곳)는 `ControlZoomTests`로 갚았다. 어느 쪽으로 통일해도 한쪽이 나빠진다.
- **문자 토글(굵게·기울임·밑줄·취소선)은 Word 방식** — 대상이 전부 그 서식이면 끄고, 아니면 모두 켠다.
  런마다 뒤집던 옛 동작으로 되돌리지 말 것. 수렴은 **상류가 이쪽으로** 오는 방향이다.
- **제목의 굵게·크기는 글자 속성** — 제목을 적용할 때마다(같은 수준 재적용 포함) 모든 글자에 쓰고, 그 사이의
  변경은 그대로 반영한다. 렌더러는 제목을 강제하지 않는다. 옛 문서(`HeadingFormat` 없는 JSON/.flow, 상류 파일)는
  편집기가 받을 때 한 번 변환하고 **직렬화기는 변환하지 않는다**. 상류는 여전히 강제한다 — 수렴 대상 아님.
- **줄 간격은 HWP 기준** — 글자 크기의 %(기본 160%), 문단 기본 아래 여백 0. 상류와 같은 변경.
- **렌더러가 강제하는 서식은 그려진 대로 보고하고, 그것에 대한 토글은 아무것도 하지 않는다** — 링크의 밑줄·파랑.
  대상이 전부 강제 서식이면 Ctrl+B/U는 변경도 언두 단계도 없다. 섞여 있으면 강제 서식은 "켜진 것"으로 친다.
- **타이핑한 글자의 서식은 캐럿 앞의 가장 가까운 글자에서, 이미지는 건너뛰고, 없으면 뒤에서**(Word 방식) —
  삽입(`TryInsertTextCore`)과 캐럿 보고(`CaretFormatRun`)가 `TypingSource` **하나**를 쓴다. 따로 두지 말 것:
  따로 있던 동안 이미지 옆 4개 위치에서 툴바와 타이핑이 달랐다.
- **링크 끝·시작에서 친 글자는 링크가 아니다**(Word 방식) — 링크는 양쪽 글자가 같은 링크일 때만 이어진다.
- **문서가 아닌 입력은 거부하고 열린 문서를 지킨다** — `LoadJson`/`LoadJsonAsync`/`LoadPackageAsync`는
  `Blocks` 없는 JSON을 `JsonException`으로, `document.json` 없는 zip을 `InvalidDataException`으로 거부한다.
  리터럴 `null`만 빈 문서다. 빈 문서로 읽으면 "저장됨"이 되어 다음 저장이 원본을 덮는다.
- **페이지 설정은 호스트 기본값에서 시작한다** — 설정 없는 문서·새 문서는 호스트가 코드로 정한 값에서 시작하고
  직전 문서의 값을 물려받지 않는다. 툴바의 용지·방향 선택은 열린 문서의 편집이다.
- **이미지·표를 끈 에디터에 들어오는 내용은 적응한다** — 붙여넣기(모든 리치 형식)와 `InsertHtml`에서 이미지는
  버리고 표는 셀 글자로 푼다. 이미 문서에 있는 내용(텍스트 드래그)과 `Load*`는 적응하지 않는다.
  `AllowImages`/`AllowTables`는 **추가**를 막는 플래그다.
- **`TargetProperty`는 `object`로 등록** — `RichEditor`로 형식을 두면 `{Binding}`이 null을 넣는다(측정).
  바인딩 엔진이 XAML 형식 메타데이터로 대입을 검사하는데, 앱은 자기 마크업이 이름을 댄 형식에만 그것을 만든다.
- **편집 중 링크는 Ctrl+클릭으로 연다**(Word 방식). 상류는 일반 클릭 — 상류 사용자 결정(2026-09-23)으로 유지되는 알려진 분기.
- **붙여넣은/읽은 HTML의 스크립트 링크는 읽을 때 버리고, 네트워크 공유 `file://` 그림은 설정과 무관하게 읽지 않는다**(상류와 같음).
- 렌더 백엔드 **Win2D** · 패키징 **Unpackaged**(MSIX 아님) · **Native AOT** 게시 가능 · 컨트롤은 **XAML 없는 코드 전용**.

## 보류 / 백로그
실수요가 생기면 그때 꺼낼 항목. **파리티 갭 아님**(상류에도 없다).
- **암호화 `.flow` 저장** — 지금 구현 안 함(수요 근거 없음). 만들게 되면 *제대로*: 현대 KDF(Argon2id 등,
  PBKDF2는 앵커하지 말 것), 평문 `.flow`와 공존하는 외부 봉투, 분실 시 복구 불가 경고, 편집 중 평문이
  UI/임시파일로 새지 않게. **어중간하게 넣어 "암호화됐다"는 잘못된 안심을 주는 건 금지**
  (우회로: 7-Zip AES / BitLocker·VeraCrypt로 감싸면 된다).
- **미측정 성능 후보 2건**(옛 `implementation_plan.md`에서 살아남은 것 — 그 파일은 2026-09-20에 지웠다.
  나머지 제안은 이미 반영됐거나(`FindCell`은 `Parent` 기반) 근거가 없었다):
  ① `EvictLayouts()`가 캐시를 **전부** 비운다 — 바로 위 주석은 "가장 오래된 것만"이라고 말한다(주석/코드 불일치).
  ② `GetImageCount()`가 호출마다 트리를 훑는다(`AfterEdit` 경로).
  ⚠ 둘 다 **계측부터** 할 것 — 이 리포에서 읽어서 세운 성능 가설은 전부 빗나갔다.

## 메모
- 상류 경로: `C:\Users\centw\source\repos\AvaloniaRichEditor`.
