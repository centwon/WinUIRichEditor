# CLAUDE.md

WinUI 3 + Win2D 리치텍스트 에디터 컨트롤. `AvaloniaRichEditor`(WPF `RichTextBox`/`FlowDocument`를 순수
C# + Avalonia `TextLayout`로 바닥부터 이식한 에디터)를 포팅해 시작했고, **2026-07-31 `1.0.0` 게시로
공개 API가 동결**됐다(SemVer, 표면은 `PublicAPI.Shipped.txt`가 추적).

**상류와의 관계**: 이제 포팅-원본이 아니라 **대등한 수렴 peer**다
(`C:\Users\centw\source\repos\AvaloniaRichEditor`, 그쪽도 1.0). 양방향으로 개선을 주고받았다.
공유 설계(포매터·문서 모델·편집 규칙)를 건드릴 때는 **원본을 먼저 대조할 것** — 대개 그쪽이 이미 같은
문제를 겪었다. 공개 표면 대조는 **눈대중 말고 원본 `PublicAPI.Shipped.txt`와 기계적으로** 할 것
(눈대중 한 번이 플래그 9개 중 8개를 놓친 적이 있다).

- **진행 상황·다음 우선순위는 `Project_Roadmap.md`를 먼저 확인**하고 작업 후 갱신한다.
  개발 이력은 `docs/roadmap-archive.md`, 릴리스별 변경은 `CHANGELOG.md`.
- **공개 API를 바꾸면** `PublicAPI.Unshipped.txt`에 선언해야 빌드가 통과한다(제거는 `*REMOVED*`).
  breaking 변경은 major 범프가 필요하다.

## 기술 기반 (Tech Stack)

- **런타임**: .NET 10 (`net10.0-windows10.0.26100.0`), C# `Nullable enable`. 목표: **Unpackaged**(MSIX 아님) + **Native AOT**.
- **UI**: WinUI 3 / Windows App SDK. 컨트롤은 XAML 없는 **코드 전용**(AOT의 컴파일 바인딩 함정 회피).
  라이브러리는 분할 패키지 **`Microsoft.WindowsAppSDK.WinUI` 2.2.1**만 참조하고(메타패키지는 WebView2/AI/ML/
  Widgets를 끌고 온다), 데모는 메타패키지 **`Microsoft.WindowsAppSDK` 2.3.1**을 쓴다 — 데모가 최신을
  따라가는 **전방 호환 카나리아** 역할, 라이브러리는 **가장 낮은 지원 버전**이라는 역할 분리다.
  ⚠ 라이브러리 하한선 규칙 2가지: ① 실제로 빌드·테스트·AOT가 통과하는 **가장 낮은** 버전 ② 소비자
  메타패키지가 주는 WinUI를 **넘지 말 것**(2.2.0→WinUI 2.2.1, 2.3.1→WinUI **2.3.0**). 0.9.0이 2.3.2로
  올렸다가 프레임워크 의존 앱을 깨뜨려 0.9.1에서 환원했다. 올리는 건 소비자에게 breaking이다.
  ⚠ **소비자 앱이 self-contained 배포**(`WindowsAppSDKSelfContained=true`)를 한다면 **메타패키지**를
  참조해야 한다 — `Microsoft.WindowsAppSDK.Runtime`(재배포 런타임)은 메타패키지에만 딸려온다. 라이브러리의
  분할 참조를 앱이 따라 하면 번들할 런타임이 없어진다(어느 버전에서든 동일).
- **렌더 백엔드**: **Win2D** `Microsoft.Graphics.Win2D` **1.4.0** (WinUI와 같은 AOT 프로젝션 레일).
  ⚠ Win2D 1.4.0의 nuspec은 `Microsoft.WindowsAppSDK.WinUI` **1.8.260204000 하한선**을 선언한다. 위 명시
  참조가 그걸 들어올리는 역할이므로 **빼면 안 된다** — 빼면 1.8이 해석되어 PRI277 self-contained 병합
  충돌이 되살아난다(예전엔 그 충돌을 데모의 PRI 스트립 타깃으로 우회했고, 지금은 그 타깃이 불필요해 삭제됨).
  - `CanvasControl.Draw` + `CanvasDrawingSession` = 즉시모드 렌더(Avalonia `Control.Render(DrawingContext)` 대응).
  - `CanvasTextLayout`(+`CanvasTextFormat`) = **단일 진실의 원천**. DirectWrite 기반이라 Avalonia 윈도우 `TextLayout`과 글리프 메트릭 일치. `HitTest`/`GetCaretPosition`/`GetCharacterRegions`/`SetUnderline`/인라인 객체 보유.
- **NuGet**: 라이브러리 = Microsoft.WindowsAppSDK.WinUI 2.2.1 + Microsoft.Graphics.Win2D 1.4.0 +
  HtmlAgilityPack 1.12.4(외부 HTML 붙여넣기 파싱). 데모 = Microsoft.WindowsAppSDK 2.3.1 + Win2D 1.4.0.

### Avalonia → WinUI/Win2D 매핑 (포팅 사전)
| Avalonia | WinUI 3 / Win2D |
|---|---|
| `TextLayout`/`ITextSource`/`TextCharacters`/`DrawableTextRun` | `CanvasTextLayout` + `CanvasTextFormat`, 인라인 이미지는 커스텀 inline object |
| `Control.Render(DrawingContext)` / `DrawText`·`DrawImage`·`FillRectangle` | `CanvasControl.Draw` + `CanvasDrawingSession.DrawTextLayout`·`DrawImage`·`FillRectangle` |
| `OnTextInput` + IME `TextInputMethodClient` | `CoreTextEditContext` (Windows.UI.Text.Core) — **가장 어려운 부분** |
| `IClipboard`(Avalonia 12 API), CF_HTML | `Windows.ApplicationModel.DataTransfer.Clipboard` + `DataPackage`/`DataPackageView` (CF_HTML 로직 재사용) |
| `TopLevel.StorageProvider` 피커 | `FileOpenPicker`/`FileSavePicker` + **HWND interop**(`InitializeWithWindow`, unpackaged 필수) |
| `IBrush`/`SolidColorBrush`/`ImmutableSolidColorBrush`/`Color`/`Colors` | `Windows.UI.Color`(그리기) / `Microsoft.UI.Xaml.Media.SolidColorBrush`(필요 시) |
| `TextDecoration`/`TextDecorationCollection` | `CanvasTextLayout.SetUnderline`/`SetStrikethrough` |
| `Bitmap`/`IImage` | `CanvasBitmap` |
| `FontWeight`/`FontStyle`/`FontFamily` | `Windows.UI.Text.FontWeights`·`FontStyle` / `CanvasTextFormat` |
| `AvaloniaList<T>` | `ObservableCollection<T>` 또는 `List<T>` |

## 빌드 / 실행

솔루션 2개 프로젝트: `src/WinUIRichEditor`(라이브러리=컨트롤+모델+포매터) + `samples/WinUIRichEditor.Demo`(unpackaged WinExe 데모).

```
dotnet build WinUIRichEditor.slnx
dotnet build samples/WinUIRichEditor.Demo/WinUIRichEditor.Demo.csproj   # exe 산출
# 실행: bin/.../win-x64/WinUIRichEditor.Demo.exe 직접 Start-Process (unpackaged)
```

- ⚠️ 실행 중 exe가 잠긴다. 재빌드 전 종료: `Get-Process -Name "WinUIRichEditor.Demo" -ErrorAction SilentlyContinue | Stop-Process -Force`
- GUI 검증은 직접 못 하므로 실행 후 사용자에게 확인 요청.
- 커밋 메시지 끝: `Co-Authored-By: Claude <모델명> <noreply@anthropic.com>` — **작업한 모델 이름을 그대로**
  쓴다(예: `Claude Opus 5`). 버전을 고정해 두면 모델이 바뀔 때마다 stale해지고, 그러면 히스토리의
  공동저자 표기가 실제 작성자와 어긋난다. 과거 커밋이 `Opus 4.8`인 것은 그때 맞았던 표기이므로
  고치지 않는다.

## 검증 (1.0 이후 이 프로젝트의 실제 리스크)
포팅 단계(Phase 0~6)는 전부 끝났다 — 이력은 `docs/roadmap-archive.md`.

기능은 완성돼 있고 **남은 리스크는 검증 쪽**이다. 이 코드베이스에서 결함을 실제로 잡아온 수단은
정독이 **아니라** 아래 넷이며, 새 작업에도 그대로 쓸 것:
1. **상류 대조** — 원본이 같은 문제를 먼저 겪었을 확률이 높다.
2. **왕복 테스트는 2회** — 1회로는 "구분자가 내용이 되어 매번 쌓이는" 계열이 안 보인다(실제로 세 번 나왔다).
3. **랜덤 편집열 퍼즈**(`DocumentFuzzTests`) — 정독이 못 보는 "안 시험한 조합"을 잡는다.
   포매터를 건드리면 **시드를 넓혀** 돌릴 것(CI 예산은 24, 결함 하나는 200시드에서만 나왔다).
4. **실기 확인** — 컨트롤 계층(포커스·캐럿·포인터)은 자동 검증이 없어 사람이 봐야 한다.
   이것이 1.1 최우선 항목(상호작용 테스트 인프라)인 이유다.

⚠️ **자기 변경분이 가장 위험하다.** 이 프로젝트에서 고친 결함의 상당수가 같은 세션에 새로 넣은
코드였다(캐럿 수정 하나가 결함 셋을 파생시켰다). 큰 변경 뒤에는 그 변경분부터 감사할 것.

## 원본의 비자명한 핵심 규칙 (이식 시 유지)
원본 `AvaloniaRichEditor/CLAUDE.md`의 "비자명한 핵심 규칙" 8개를 그대로 따른다. 요약:
1. **단일 TextLayout이 진실의 원천**(렌더·커서·히트테스트·선택 모두 하나의 layout에서). Win2D는 `CanvasTextLayout`로 이 규칙을 직역.
2. **오프셋 모델 "이미지=1글자"**(`U+FFFC` placeholder). 길이/오프셋은 `InlineLen()`/`BuildPlain()` 경유.
3. **Enter=새 Paragraph 분할**(컨테이너 일반화: Document.Blocks 또는 TableCell.Blocks). Shift+Enter=소프트 `\n`.
4. **블록 vs 인라인 이미지 / 셀=재귀 컨테이너**(셀이 문단·블록이미지·구분선·중첩표를 담음, LayoutTable과 임의 깊이 상호 재귀).
5. **NormalizeBlocks**: 문서 처음/끝·비문단 블록 사이에만 문단 보장.
6. **선택/삭제**: 텍스트=TextRange, 큰 이미지/표=블록 선택 후 Delete.
7. **클립보드 우선순위**: 내부 리치 → 외부 HTML → 평문. 워드 그림(VML) 미지원.
8. **모델 객체는 UI 스레드에서만 생성**(브러시 등 스레드 친화 객체). 비동기 로더는 파싱까지만 백그라운드.
