# CLAUDE.md

`AvaloniaRichEditor`(WPF `RichTextBox`/`FlowDocument`를 순수 C# + Avalonia `TextLayout`로 바닥부터 이식한 리치텍스트 에디터)를 **WinUI 3로 포팅**하는 프로젝트.
원본: `C:\Users\centw\source\repos\AvaloniaRichEditor` (라이브러리 ~13,000줄). 이 포팅은 그 엔진을 WinUI 3 + Win2D 위에 다시 세운다.

- **진행 상황·단계는 `Project_Roadmap.md`를 먼저 확인**하고 작업 후 갱신한다.

## 기술 기반 (Tech Stack)

- **런타임**: .NET 10 (`net10.0-windows10.0.26100.0`), C# `Nullable enable`. 목표: **Unpackaged**(MSIX 아님) + **Native AOT**.
- **UI**: WinUI 3 / Windows App SDK **2.2.0**. 컨트롤은 XAML 없는 **코드 전용**(AOT의 컴파일 바인딩 함정 회피).
- **렌더 백엔드**: **Win2D** `Microsoft.Graphics.Win2D` **1.4.0** (CsWinRT 2.2.0 기반 → WinUI와 같은 AOT 프로젝션 레일).
  - `CanvasControl.Draw` + `CanvasDrawingSession` = 즉시모드 렌더(Avalonia `Control.Render(DrawingContext)` 대응).
  - `CanvasTextLayout`(+`CanvasTextFormat`) = **단일 진실의 원천**. DirectWrite 기반이라 Avalonia 윈도우 `TextLayout`과 글리프 메트릭 일치. `HitTest`/`GetCaretPosition`/`GetCharacterRegions`/`SetUnderline`/인라인 객체 보유.
- **NuGet**: Microsoft.WindowsAppSDK 2.2.0, Microsoft.Graphics.Win2D 1.4.0, HtmlAgilityPack 1.12.4(외부 HTML 붙여넣기 파싱).

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
- 커밋 메시지 끝: `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`.

## 포팅 단계 (Phase)
0. ✅ 스캐폴드(라이브러리+unpackaged 데모, Win2D, CanvasControl 스모크 렌더).
1. 문서 모델(`Documents/`) + 포매터(`Formatters/`) — Avalonia 타입 치환, 대부분 기계적.
2. 렌더링 엔진(`BuildTextLayout`→`CanvasTextLayout`), 읽기 전용 렌더.
3. 입력(포인터·키보드·히트테스트·선택·캐럿).
4. IME(`CoreTextEditContext`).
5. 클립보드·서식·표·이미지·찾기·툴바·컨텍스트메뉴.
6. 테스트 + 데모 마감 + AOT 퍼블리시 검증.

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
