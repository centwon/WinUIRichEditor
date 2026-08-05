using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Controls;

/// <summary>How much of the toolbar is shown. A coarse density knob layered over the individual controls;
/// capability (read-only, Allow*) still vetoes what a level would otherwise show.</summary>
public enum ToolbarLevel
{
    /// <summary>Derive from the target: editable → <see cref="Normal"/>. (Read-only always shows the view
    /// toolbar regardless of level.)</summary>
    Auto,
    /// <summary>Undo/redo, B/I/U/S, font size only.</summary>
    Minimal,
    /// <summary>Full text formatting (font, color, heading, align, lists, indent, spacing) + inserts.</summary>
    Normal,
    /// <summary>Everything, plus the page/zoom controls and file actions (Export/Import/Print).</summary>
    Maximum,
}

// Built-in page/zoom controls (zoom · paper · orientation) and file actions (Export / Import / Print),
// moved here from RichEditorView so a standalone toolbar carries them too. RichEditorView now delegates to
// these (WindowHandle / PrintRequested / ShowFileActions forward straight through). Both sections default on
// and sit at the end of the wrapping strip, before any host TrailingItems.
public partial class RichEditorToolbar
{
    // ---- toolbar density level --------------------------------------------
    private ToolbarLevel _level = ToolbarLevel.Auto;

    /// <summary>Toolbar density. <see cref="ToolbarLevel.Auto"/> (default) resolves to
    /// <see cref="ToolbarLevel.Normal"/> for an editable target; a read-only target always shows the view
    /// toolbar (page/zoom + Export/Print) regardless of this. Capability (Allow*) still vetoes buttons.</summary>
    public ToolbarLevel ToolbarLevel
    {
        get => _level;
        set { if (_level == value) return; _level = value; Content = Build(); Sync(); }
    }

    // The concrete level to build (Auto → Normal). Read-only is handled separately in Build().
    private ToolbarLevel EffectiveLevel() => _level == ToolbarLevel.Auto ? ToolbarLevel.Normal : _level;

    // ---- page / zoom ------------------------------------------------------
    private ComboBox? _zoom, _paper, _orient;
    private ComboBoxItem? _zoomFit;    // the "Fit width" entry (disabled in Continuous mode)

    private static readonly int[] ZoomLevels = { 50, 75, 100, 125, 150, 200 };
    private const string FitWidthTag = "fit";
    private static readonly (string label, RichEditorPageSize size)[] PaperSizes =
    {
        ("PaperContinuous", RichEditorPageSize.Continuous), ("A4", RichEditorPageSize.A4),
        ("A3", RichEditorPageSize.A3), ("A5", RichEditorPageSize.A5), ("B4", RichEditorPageSize.B4),
        ("B5", RichEditorPageSize.B5), ("Letter", RichEditorPageSize.Letter),
        ("Legal", RichEditorPageSize.Legal), ("Tabloid", RichEditorPageSize.Tabloid),
    };

    private bool _showPageControls = true;
    /// <summary>Whether the built-in zoom / paper-size / orientation controls are shown at the end of the
    /// strip. Default true.</summary>
    public bool ShowPageControls
    {
        get => _showPageControls;
        set { if (_showPageControls == value) return; _showPageControls = value; Content = Build(); Sync(); }
    }

    private void BuildPageControls(ToolbarWrapPanel strip)
    {
        _zoom = MakeCombo(104, Loc("ZoomTip"));
        _zoomFit = new ComboBoxItem { Content = Loc("FitWidth"), Tag = FitWidthTag };
        _zoom.Items.Add(_zoomFit); // fit width at the top
        foreach (var pct in ZoomLevels) _zoom.Items.Add(new ComboBoxItem { Content = pct + "%", Tag = pct });
        _zoom.SelectionChanged += (_, _) =>
        {
            if (_suppress || Target == null || _zoom!.SelectedItem is not ComboBoxItem ci) return;
            if (Equals(ci.Tag, FitWidthTag)) Target.FitToWidth();
            else if (ci.Tag is int pct) Target.SetZoom(pct / 100.0);
        };
        // No icon: the Segoe Fluent "Zoom" glyph is a magnifier, and so is the Find button's "Search" one
        // — two unlabelled magnifiers side by side in the same strip read as the same control. The combo
        // identifies itself without help: its first item is "Fit width" and the rest are percentages, and
        // its tooltip says zoom. (The original toolbar has no icon here either.)
        strip.Children.Add(_zoom);

        _paper = MakeCombo(120, Loc("PaperTip"));
        foreach (var (label, _) in PaperSizes) _paper.Items.Add(new ComboBoxItem { Content = label == "PaperContinuous" ? Loc(label) : label });
        _paper.SelectionChanged += (_, _) => OnPaperChanged();
        strip.Children.Add(_paper);

        _orient = MakeCombo(86, Loc("OrientationTip"));
        _orient.Items.Add(new ComboBoxItem { Content = Loc("OrientPortrait"), Tag = RichEditorPageOrientation.Portrait });
        _orient.Items.Add(new ComboBoxItem { Content = Loc("OrientLandscape"), Tag = RichEditorPageOrientation.Landscape });
        _orient.SelectionChanged += (_, _) => { if (!_suppress && Target != null && _orient!.SelectedItem is ComboBoxItem ci) Target.PageOrientation = (RichEditorPageOrientation)ci.Tag; };
        strip.Children.Add(_orient);
    }

    private void OnPaperChanged()
    {
        if (_suppress || Target == null || _paper!.SelectedIndex < 0) return;
        var size = PaperSizes[_paper.SelectedIndex].size;
        Target.PageSize = size;
        // A concrete paper size shows the page outline (page view); Continuous reflows with no chrome.
        Target.ShowPageBoundaries = size != RichEditorPageSize.Continuous;
        // Fit-width has no meaning in Continuous; drop back to 100% when leaving a fitted paged view.
        if (size == RichEditorPageSize.Continuous && Target.IsFitWidth) Target.SetZoom(1.0);
        // Suppressed: SyncPage writes SelectedItem/SelectedIndex on the zoom and orientation combos, and
        // this runs from a SelectionChanged handler (so _suppress is NOT already set). Without the guard
        // those writes re-enter their own handlers and push values back into the editor — harmless today
        // only because they happen to be the values just set. Sync() wraps SyncPage the same way.
        _suppress = true;
        try { SyncPage(); } finally { _suppress = false; }
    }

    // Reflect the editor's page/zoom state onto the built-in controls (called from Sync()).
    private void SyncPage()
    {
        if (_zoom == null || Target == null) return;
        bool paged = Target.PageSize != RichEditorPageSize.Continuous;
        if (_zoomFit != null) _zoomFit.IsEnabled = paged;
        if (paged && Target.IsFitWidth) SelectByTag(_zoom, FitWidthTag);
        else
        {
            // Non-preset factors (Ctrl+wheel produces 110%, 121%, …) show via PlaceholderText with
            // nothing selected, instead of leaving the combo blank. Deliberately NOT a dynamic
            // ComboBoxItem: SyncPage runs once per wheel notch during a Ctrl+wheel storm, and mutating
            // Items + selection there crashed WinUI's ComboBox (stowed E_INVALIDARG, app-fatal).
            // PlaceholderText only renders while SelectedIndex is -1, so stale text is harmless.
            int pct = (int)Math.Round(Target.Zoom * 100);
            if (Array.IndexOf(ZoomLevels, pct) >= 0) SelectByTag(_zoom, pct);
            else
            {
                _zoom.SelectedIndex = -1;
                _zoom.PlaceholderText = pct + "%";
            }
        }
        if (_paper != null)
            for (int i = 0; i < PaperSizes.Length; i++)
                if (PaperSizes[i].size == Target.PageSize) { _paper.SelectedIndex = i; break; }
        if (_orient != null)
        {
            SelectByTag(_orient, Target.PageOrientation);
            _orient.IsEnabled = paged; // orientation is meaningless in Continuous
        }
    }

    // ---- file actions -----------------------------------------------------
    private Button? _exportBtn, _importBtn, _printBtn;

    /// <summary>HWND of the owning window, required to show the file picker in an unpackaged app. Set this
    /// from the host (<c>WinRT.Interop.WindowNative.GetWindowHandle(window)</c>); Export/Import are inert
    /// until it is set.</summary>
    public nint WindowHandle { get; set; }

    private bool _showFileActions = true;
    /// <summary>Whether the built-in Export / Import (and Print, once <see cref="PrintRequested"/> is
    /// handled) buttons are shown at the end of the strip. Default true.</summary>
    public bool ShowFileActions
    {
        get => _showFileActions;
        set { if (_showFileActions == value) return; _showFileActions = value; Content = Build(); Sync(); }
    }

    private EventHandler? _printRequested;
    /// <summary>Raised when the user clicks the built-in Print button. Printing is host-specific, so a host
    /// handles this to drive its own print/preview. The Print button stays hidden until a handler is attached.</summary>
    public event EventHandler? PrintRequested
    {
        add { _printRequested += value; Sync(); }
        remove { _printRequested -= value; Sync(); }
    }

    private void BuildFileActions(ToolbarWrapPanel strip)
    {
        _exportBtn = IconButton("⤓", Loc("Export"), () => _ = ExportAsync(), RichEditorIcon.Export);
        _importBtn = IconButton("⤒", Loc("Import"), () => _ = ImportAsync(), RichEditorIcon.Import);
        _printBtn = IconButton("⎙", Loc("Print"), () => _printRequested?.Invoke(this, EventArgs.Empty), RichEditorIcon.Print);
        _printBtn.Visibility = Visibility.Collapsed; // shown by SyncFileActions once a Print handler exists
        strip.Children.Add(_exportBtn);
        strip.Children.Add(_importBtn);
        strip.Children.Add(_printBtn);
    }

    // Import edits, so it's hidden in the read-only view toolbar; Print stays hidden until a host handles
    // PrintRequested. (Called from Sync().)
    private void SyncFileActions()
    {
        bool ro = Target?.IsReadOnly == true;
        if (_importBtn != null) _importBtn.Visibility = ro ? Visibility.Collapsed : Visibility.Visible;
        if (_printBtn != null)
            _printBtn.Visibility = _printRequested != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void InitWithWindow(object target) => WinRT.Interop.InitializeWithWindow.Initialize(target, WindowHandle);

    // Both file actions are invoked fire-and-forget from their button (`() => _ = ExportAsync()`), so an
    // exception that escapes becomes an UNOBSERVED task exception and disappears without a trace — no
    // dialog, no error, nothing on screen. The picker calls themselves are the likeliest throwers (COM
    // failure, a broker that refuses, an unreadable file), which is exactly why the guard has to wrap the
    // WHOLE body including the picker, not just the parsing that follows it.
    private async Task ExportAsync()
    {
        try { await ExportCoreAsync(); }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            System.Diagnostics.Debug.WriteLine($"Export failed: {ex.Message}");
        }
    }

    private async Task ExportCoreAsync()
    {
        if (WindowHandle == 0 || Target?.Document == null) return;
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "document",
        };
        picker.FileTypeChoices.Add("JSON document", new List<string> { ".json" });
        picker.FileTypeChoices.Add("Flow package", new List<string> { ".flow" });
        picker.FileTypeChoices.Add("HTML document", new List<string> { ".html", ".htm" });
        picker.FileTypeChoices.Add("RTF document", new List<string> { ".rtf" });
        picker.FileTypeChoices.Add("PDF document", new List<string> { ".pdf" });
        InitWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        // Format follows the chosen extension: .flow = ZIP package, .html/.htm = HTML, .rtf = RTF,
        // .pdf = rasterized pages (the control's SavePdf engine; output-only), else JSON.
        var ext = file.FileType.ToLowerInvariant();
        if (ext == ".flow")
        {
            using var ms = new MemoryStream();
            await Target.SavePackageAsync(ms);
            await FileIO.WriteBytesAsync(file, ms.ToArray());
        }
        else if (ext is ".html" or ".htm")
            await FileIO.WriteTextAsync(file, Target.ToHtml());
        else if (ext == ".rtf")
            await FileIO.WriteTextAsync(file, Target.ToRtf()); // RTF is ASCII (non-ASCII is \u-escaped)
        else if (ext == ".pdf")
        {
            using var ms = new MemoryStream();
            Target.SavePdf(ms); // Win2D render — UI thread (we're on it)
            await FileIO.WriteBytesAsync(file, ms.ToArray());
        }
        else
            await FileIO.WriteTextAsync(file, await Target.ToJsonAsync());
        // PDF is an output-only rendering (not reloadable), so it doesn't count as "saving" the
        // document — the dirty flag stays. The document formats clear it.
        if (ext != ".pdf") Target.MarkSaved();
    }

    private async Task ImportAsync()
    {
        try { await ImportCoreAsync(); }
        catch (Exception ex)
        {
            RichEditorDiagnostics.Report(ex);
            System.Diagnostics.Debug.WriteLine($"Import failed: {ex.Message}");
        }
    }

    private async Task ImportCoreAsync()
    {
        if (WindowHandle == 0 || Target == null) return;
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in new[] { ".json", ".flow", ".html", ".htm", ".rtf" }) picker.FileTypeFilter.Add(ext);
        InitWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var buffer = await FileIO.ReadBufferAsync(file);
        byte[] bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        if (bytes.Length == 0) return;

        // Sniff the content: ZIP magic ("PK") = .flow package, "{\rtf" = RTF, "<" = HTML, else JSON.
        // Faults land in ImportAsync's guard; the RTF branch reports through TryParse before that.
        if (bytes.Length >= 2 && bytes[0] == (byte)'P' && bytes[1] == (byte)'K')
        {
            using var ms = new MemoryStream(bytes);
            await Target.LoadPackageAsync(ms);
            return;
        }

        string latin1 = System.Text.Encoding.Latin1.GetString(bytes);
        string utf8 = System.Text.Encoding.UTF8.GetString(bytes);
        // RTF is parsed here rather than through LoadRtf so a damaged file reports on the same channel
        // as every other import fault: LoadRtf deliberately keeps the open document and stays silent,
        // which on a file-open reads as "nothing happened".
        if (RtfDocumentFormatter.LooksLikeRtf(latin1))
        {
            if (RtfDocumentFormatter.TryParse(latin1, out var rtfDoc, out var rtfError))
                Target.LoadDocument(rtfDoc);
            else
                System.Diagnostics.Debug.WriteLine($"Import failed: {rtfError}");
        }
        else if (utf8.TrimStart().StartsWith("<", StringComparison.Ordinal)) Target.LoadHtml(utf8);
        else await Target.LoadJsonAsync(utf8);
    }
}
