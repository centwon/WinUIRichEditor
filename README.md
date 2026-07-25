# WinUIRichEditor

A from-scratch rich text editor control for **WinUI 3**, rendered with **Win2D**
(`CanvasVirtualControl` / `CanvasTextLayout`). It is a port of
[AvaloniaRichEditor](https://github.com/centwon/AvaloniaRichEditor) — the same document model,
formatters, and "single `TextLayout` is the source of truth" engine design, rebuilt on the
DirectWrite-backed `CanvasTextLayout` instead of Avalonia's `TextLayout`.

> **Status: converged with AvaloniaRichEditor — feature parity in both directions.**
> Beyond matching the original's feature set, the two projects have since exchanged improvements both
> ways (a source-compatible alias layer here, platform-agnostic features back-ported there), and the
> original's own full-source audit has been swept against this codebase. Tables, images, formatting,
> clipboard, page view, print/PDF, the drop-in host controls, localization and accessibility all work,
> including the edge cases (nested/inline-table row·column resize, full keyboard caret traversal through
> inline-table cells). **Native AOT** publish works end-to-end (self-contained — builds, runs, renders;
> re-verified each release). The document format is byte-compatible with AvaloniaRichEditor (verified by
> loading a `.flow` saved by the original). See [`Project_Roadmap.md`](Project_Roadmap.md) and
> [`CHANGELOG.md`](CHANGELOG.md). The control API may still change.

> **Requires Windows App SDK 2.3.2 or later.** (Raised from 2.2.1 in 0.9.0 — see the CHANGELOG for the
> other behavioural changes in that release.)

## Tech stack

- **.NET 10** (`net10.0-windows10.0.26100.0`), C# `nullable enable`. Target: **unpackaged** (no MSIX).
- **WinUI 3 / Windows App SDK 2.3.x** — the library references the split `Microsoft.WindowsAppSDK.WinUI`
  package (2.3.2); controls are **code-only** (no XAML, avoids AOT compiled-binding pitfalls).
- **Win2D 1.4.0** (`Microsoft.Graphics.Win2D`) — immediate-mode rendering; one `CanvasTextLayout` per
  paragraph drives render, caret, hit-testing, and selection.
- **HtmlAgilityPack 1.12.4** for external HTML paste parsing.

## Features

**Text & formatting**
- Bold / italic / underline / strikethrough, font family & size, foreground & highlight color, clear formatting
- Paragraph alignment, headings (H1–H6), blockquote, indent, bullet / numbered lists, **custom line spacing**
- **Hyperlinks** (insert / edit / open / remove), **format painter**
- Caret + drag/Shift selection, double-click word / triple-click paragraph select, **undo/redo**
- Keyboard: arrows, Home/End (visual line), **Ctrl+←/→ word**, **Ctrl+Home/End document**, **Ctrl+Backspace/Delete
  word delete**, **PageUp/PageDown**, and shortcuts (Ctrl+B/I/U, Ctrl+Z/Y, Ctrl+C/X/V, **Ctrl+Shift+V** plain paste)
- **Korean / CJK IME** composition (inline underline) via `CoreTextEditContext`

**Tables**
- colspan/rowspan, nested tables, recursive cell content (paragraphs, images, dividers, nested tables)
- Tab cell navigation, right-click row/column insert·delete, **cell merge/split**, drag-select cells
- **Draw-to-size insert** (toolbar grid picker), **column-width & row-height resize** (drag borders),
  table block selection, **block↔inline ("treat as character") toggle**

**Images**
- Block & inline rendering (async GPU decode), insert from file, **clipboard image paste**, **drag-and-drop image files**
- Click-to-select with **resize handles** (aspect-locked), right-click **copy** / size presets / replace / save / delete

**Clipboard & I/O**
- Internal rich copy/paste, **RTF**, external **HTML** (`CF_HTML`), **image**, and **Excel/TSV→table** paste;
  plain-text paste (Ctrl+Shift+V); copy a selected image to the system clipboard
- File formats: `.flow` (ZIP package), `.json`, `.html`, `.rtf`, **PDF export** — see the
  [document format spec](docs/DOCUMENT_FORMAT.md) (byte-compatible with AvaloniaRichEditor)

**Page view, print & PDF**
- `PageSize` / `PageOrientation` / `ShowPageBoundaries`, stacked page view with **line-aware page breaks**,
  headers / footers / page numbers
- `RenderPrintPage(i, dpi)` (offscreen bitmap) and `SavePdf(stream)` (rasterized pages → PDF)

**Host controls & chrome**
- Drop-in **`RichEditorView`** (toolbar + page/zoom chrome + editor + status bar + **Export/Import/Print**)
  and a standalone **`RichEditorToolbar`** (`LeadingItems`/`TrailingItems` host slots, wrap-on-narrow)
- **Icon theming**: built-in Segoe Fluent Icons glyphs, host-overridable per slot via `RichEditorIcons.Provider`
- **Capability**: `IsReadOnly` (a viewer is `IsReadOnly=true` + no/minimal toolbar) + feature flags
  (`AllowImages` / `AllowTables` / `AllowRichPaste`). Toolbar density via **`ToolbarLevel`** (Minimal / Normal / Maximum)
- **Word-standard keyboard shortcuts** from a single table (`RichEditorShortcuts`), shown in menu hints + toolbar tooltips
- **Localization** (KO / EN, host-extensible) via `RichEditorLocalization`; **accessibility** peer (`IValueProvider`)
- Change events (`TextChanged` / `SelectionChanged` / `DocumentChanged`) and appearance DPs
  (`SelectionBrush` / `CaretBrush`)

## Quick start

**Drop-in host control** (toolbar + editor + status bar):

```csharp
using WinUIRichEditor.Controls;
using WinUIRichEditor.Formatters;

var view = new RichEditorView();
view.Document = HtmlDocumentFormatter.ParseHtml("<h1>Title</h1><p>Hello <b>world</b></p>");
// view.IsReadOnly = true;  // make it a viewer

// the toolbar's image button needs a host file picker (window handle required, unpackaged):
view.ImagePicker = async () => /* return image bytes, or null */;

// page view + export
view.Editor.PageSize = RichEditorPageSize.A4;
using var pdf = new MemoryStream();
view.Editor.SavePdf(pdf);
```

**Bare control**:

```csharp
var editor = new RichEditor();
editor.Document = HtmlDocumentFormatter.ParseHtml("<p>Hello <b>world</b></p>");
editor.ToggleBold();                       // operates on the selection / caret word
editor.SetForeground(Windows.UI.Color.FromArgb(255, 204, 0, 0));

string json = editor.ToJson();             // also ToHtml / ToRtf / SavePackageAsync
editor.LoadHtml("<p>replaced</p>");
```

Controls are **code-only** — a no-XAML `Page` crashes WinUI 3 navigation, so host them in a XAML-shell
page (see `samples/.../ViewDemoPage.xaml`). File pickers need HWND interop (`InitializeWithWindow`).

## Build, test & run

```
dotnet build WinUIRichEditor.slnx
dotnet test  tests/WinUIRichEditor.Tests/WinUIRichEditor.Tests.csproj   # 26 headless model/formatter tests
dotnet build samples/WinUIRichEditor.Demo/WinUIRichEditor.Demo.csproj
# run the unpackaged exe directly:
#   samples/WinUIRichEditor.Demo/bin/Debug/net10.0-windows10.0.26100.0/win-x64/WinUIRichEditor.Demo.exe
```

A running instance locks the exe — stop it before rebuilding:
`Get-Process -Name "WinUIRichEditor.Demo" | Stop-Process -Force`.

The demo is a **four-page nav shell**, one per library layer: **컨트롤** (bare `RichEditor`),
**읽기 전용** (`IsReadOnly=true`), **컨트롤+툴바** (`RichEditor` + `RichEditorToolbar`), and **View**
(full `RichEditorView` with page/zoom chrome, status bar, Export/Import/Print, and PDF export).

## Native AOT

Native AOT publish **works end-to-end**. The library is AOT-shaped (source-gen JSON, no reflection
serialization, code-only control, `IsAotCompatible`, no WinRT-static activation in the model/formatter
layer), and the **self-contained** profile (`samples/.../PublishProfiles/win-x64.pubxml`: `PublishAot` +
`SelfContained` + `PublishSingleFile` + `PublishTrimmed`, `WindowsAppSDKSelfContained=true`) builds
cleanly (~13 MB native exe, 0 trim/AOT warnings), runs, and renders — Win2D `CanvasTextLayout`,
`CanvasDevice`, and `CanvasFontSet` all activate under AOT. (An earlier "crashes at startup in `combase
0x80004005`" was a *framework-dependent*-only limitation; the self-contained bundle supplies WinRT
activation.)

**Build workaround (still required):** `GenerateLibraryLayout` on the library plus an MSBuild target that
strips the stale `windowsappsdk.winui\1.8` PRIs (pulled in transitively by Win2D 1.4.0) clears the
`PRI277` conflict with Windows App SDK 2.2's PRI. Removable once a WinAppSDK-2.x-aligned Win2D ships.

## Project layout

| Path | Contents |
|---|---|
| `src/WinUIRichEditor` | Control library: `Controls`, document model `Documents`, `Formatters`. |
| `samples/WinUIRichEditor.Demo` | Unpackaged WinExe demo: four-page nav shell (control / read-only / control+toolbar / view). |
| `tests/WinUIRichEditor.Tests` | Headless xUnit tests for the model + formatters (`dotnet test`). |

## License

[MIT](LICENSE) © 2026 centwon. Depends on the [Windows App SDK](https://github.com/microsoft/WindowsAppSDK),
[Win2D](https://github.com/microsoft/Win2D), and [HtmlAgilityPack](https://html-agility-pack.net/)
(all MIT) — see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
