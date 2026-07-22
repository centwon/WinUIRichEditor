# Changelog

All notable changes to WinUIRichEditor. This project is a WinUI 3 + Win2D port of AvaloniaRichEditor;
the format follows [Keep a Changelog](https://keepachangelog.com/). The control API is not yet stable.

## [Unreleased]

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

[Unreleased]: https://github.com/centwon/WinUIRichEditor/compare/v0.8.0...HEAD
[0.8.0]: https://github.com/centwon/WinUIRichEditor/releases/tag/v0.8.0
