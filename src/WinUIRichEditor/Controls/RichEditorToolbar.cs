using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Threading.Tasks;
using Windows.UI;
using Windows.UI.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using WinUIRichEditor.Documents;

namespace WinUIRichEditor.Controls;

/// <summary>Optional formatting toolbar for <see cref="RichEditor"/>. Point <see cref="Target"/> at an
/// editor and the toolbar drives it through the editor's public commands and reflects the caret's
/// formatting on its buttons (via <see cref="RichEditor.StatusChanged"/> + <see cref="RichEditor.GetCaretFormat"/>).
/// Labels come from <see cref="RichEditorLocalization"/>. Built in code (no XAML), AOT-friendly.</summary>
public partial class RichEditorToolbar : UserControl
{
    private RichEditor? _target;

    /// <summary>The editor this toolbar drives.</summary>
    public RichEditor? Target
    {
        get => _target;
        set
        {
            if (ReferenceEquals(_target, value)) return;
            if (_target != null) _target.StatusChanged -= OnTargetStatusChanged;
            _target = value;
            if (_target != null) _target.StatusChanged += OnTargetStatusChanged;
            Content = Build(); // rebuild so the strip reflects the new target's read-only state
            Sync();
        }
    }

    // Fills the font combo from the editor's FontFamilyChoices (installed system fonts, localized names).
    // Each item renders in its own typeface. Built lazily because Target is usually assigned after Build().
    private void PopulateFontList()
    {
        if (_font == null) return;
        string? prev = (_font.SelectedItem as ComboBoxItem)?.Content as string;
        _font.Items.Clear();
        _fontReflected = null; // items changed; the next Sync must re-resolve the selection
        var choices = Target?.FontFamilyChoices;
        if (choices == null || choices.Count == 0) return;
        foreach (var f in choices)
            _font.Items.Add(new ComboBoxItem { Content = f, FontFamily = SafeFontFamily(f) });
        if (prev != null) { _suppress = true; try { SelectByContent(_font, prev); } finally { _suppress = false; } }
    }

    private static FontFamily SafeFontFamily(string name)
    {
        try { return new FontFamily(name); } catch { return FontFamily.XamlAutoFontFamily; }
    }

    /// <summary>Optional async image-bytes provider (e.g. a file picker). When set, the image button
    /// awaits it and inserts the returned bytes. Hosts supply this because picking a file needs the
    /// owning window handle, which a control in the tree can't reach on its own.</summary>
    public Func<Task<byte[]?>>? ImagePicker { get; set; }

    /// <summary>Host controls shown at the start of the strip, before the formatting buttons (e.g.
    /// app-shell actions like save/open). Add/remove controls and the toolbar rebuilds; they share the
    /// strip's wrapping, so the whole toolbar stays one row that wraps together when narrow.</summary>
    public ObservableCollection<UIElement> LeadingItems { get; } = new();

    /// <summary>Host controls shown at the end of the strip, after the formatting buttons (e.g. zoom).</summary>
    public ObservableCollection<UIElement> TrailingItems { get; } = new();

    // "Active" (toggled-on) face: a soft tint, not the system accent. WinUI's ToggleButton Checked
    // state paints the accent colour with a white glyph, which shouts next to the flat icon strip —
    // ApplyToggleCheckedStyle overrides the template's checked brushes with these.
    private static readonly SolidColorBrush ActiveBrush = new(Color.FromArgb(255, 0xDD, 0xE7, 0xF3));
    private static readonly SolidColorBrush ActiveHoverBrush = new(Color.FromArgb(255, 0xCB, 0xDA, 0xEC));
    private static readonly SolidColorBrush ClearBrush = new(Colors.Transparent);
    private static readonly SolidColorBrush BlackInk = new(Colors.Black); // shared: Sync runs per keystroke

    // Variation Selector-15: forces text (monochrome) presentation of an emoji that has no symbol-font
    // glyph, so the leftover emoji fallbacks don't render as colour and clash with the FontIcon set.
    private static readonly string Mono = ((char)0xFE0E).ToString();

    // Reflected controls are nullable: a Minimal / read-only toolbar builds only a subset, so Sync() must
    // null-guard every access. Null = "not built at the current ToolbarLevel".
    private ToggleButton? _bold, _italic, _underline, _strike, _painter;
    private Button? _bullet, _number;                 // list-box icon buttons (toggle the list)
    private TextBlock? _bulletPreview, _numberPreview; // current list marker shown in the list boxes
    private static readonly SolidColorBrush DimInk = new(Color.FromArgb(255, 0xBF, 0xC3, 0xC7)); // inactive marker
    private ComboBox? _font, _size, _heading, _align;
    private TextBox? _spacingBox; // editable line-spacing %, reflects/sets the caret paragraph
    private Button? _undo, _redo;
    private Button? _tableBtn, _imageBtn, _dividerBtn, _findBtn;
    private bool _suppress; // guards combo SelectionChanged while syncing toolbar <- caret state
    private bool _builtReadOnly; // read-only state captured at the last Build (to rebuild the view toolbar on toggle)
    private string? _fontReflected; // family last reflected into the font combo — skips the O(installed fonts) item scan per keystroke

    private static readonly double[] FontSizes = { 8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 40, 48, 60, 72 };
    private const double BodySizePt = 10; // the model's default run size, shown when a run has none

    // A point-size label ("10 pt", "10.5 pt") — the unit the model, API and serialization all speak.
    private static string PtText(double pt)
        => pt.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) + " pt";
    private static readonly int[] SpacingPercents = { 100, 110, 120, 130, 150, 160, 180, 200, 250, 300 };

    // The 40-swatch palette (greys + hues in a few shades) shared by the text-color/highlight pickers,
    // matching the original AvaloniaRichEditor toolbar. Internal: the editor's cell-background
    // context-menu palette reuses it so all color pickers offer the same swatches.
    internal static readonly string[] Palette =
    {
        "#000000","#444444","#666666","#999999","#BBBBBB","#DDDDDD","#EEEEEE","#FFFFFF",
        "#FF0000","#E67E22","#F1C40F","#2ECC71","#1ABC9C","#3498DB","#9B59B6","#E91E63",
        "#C0392B","#D35400","#F39C12","#27AE60","#16A085","#2980B9","#8E44AD","#AD1457",
        "#7B241C","#935116","#9A7D0A","#196F3D","#0E6251","#1A5276","#5B2C6F","#78281F",
        "#FFCDD2","#FFE0B2","#FFF9C4","#C8E6C9","#B2DFDB","#BBDEFB","#E1BEE7","#F8BBD0",
    };

    private static readonly SolidColorBrush NoColorBrush = new(Color.FromArgb(255, 0xDD, 0xDD, 0xDD)); // "no highlight" face
    private Border? _colorSwatch, _highlightSwatch; // current-colour bars under the picker glyphs

    // Uniform strip metrics: every control renders in a 32px-tall box (the WinUI ComboBox default
    // height) and icon buttons are a FIXED 36px wide, so rows read as one even line — mixed natural
    // heights (buttons ~28, combos ~32, custom boxes 28) and content-driven button widths made the
    // strip look ragged. Gaps come from the wrap panel's spacing ALONE (no per-control margins), so
    // every gap is identical.
    private const double CtlHeight = 28;
    // Icon buttons: 26px wide around a ~15px glyph leaves ~5px each side. 30/36 left so much intra-button
    // whitespace that adjacent icons (indent, table↔image, file) looked far apart even at 4px panel spacing.
    private const double BtnWidth = 26;
    // Uniform combo content point-size. Without it each combo inherited the default and the font-name
    // combo (whose items carry their own FontFamily) rendered its selected value in that face at an
    // apparently different size than the plain-text combos (size/heading/align/zoom/paper/orient).
    private const double ComboFontSize = 12;

    private static string Loc(string key) => RichEditorLocalization.GetString(key);

    // Tooltip with the command's shortcut appended, e.g. "굵게 (Ctrl+B)". Single-sourced from the table.
    private static string TipSc(string key, ShortcutId id) => Loc(key) + " (" + RichEditorShortcuts.Display(id) + ")";

    public RichEditorToolbar()
    {
        // Changing the host item slots rebuilds the strip so they sit inline with the formatting buttons.
        void Rebuild(object? s, NotifyCollectionChangedEventArgs e) { Content = Build(); Sync(); }
        LeadingItems.CollectionChanged += Rebuild;
        TrailingItems.CollectionChanged += Rebuild;
        Content = Build();
        Loaded += (_, _) => { RichEditorLocalization.LanguageChanged += OnLanguageChanged; Sync(); };
        Unloaded += (_, _) => RichEditorLocalization.LanguageChanged -= OnLanguageChanged;
    }

    private void OnLanguageChanged(object? sender, EventArgs e) { Content = Build(); Sync(); }
    private void OnTargetStatusChanged(object? sender, EventArgs e) => Sync();

    private ToolbarWrapPanel? _strip; // current strip, kept so a rebuild can detach reused host items

    private UIElement Build()
    {
        // Detach reused host (leading/trailing) items from the previous strip. FrameworkElement.Parent is
        // null before the toolbar is loaded, so clearing the old collection is the reliable way to reparent
        // them — adding an element that still has a parent throws (0x800F1000).
        _strip?.Children.Clear();
        var strip = new ToolbarWrapPanel { HorizontalSpacing = 4, VerticalSpacing = 2 };
        _strip = strip;

        // Reset reflected controls; only the ones the current level/state re-builds are re-assigned. Sync()
        // null-guards each, so a Minimal or read-only toolbar (a subset) reflects safely.
        _bold = _italic = _underline = _strike = _painter = null;
        _bullet = _number = _undo = _redo = _tableBtn = _imageBtn = _dividerBtn = _findBtn = null;
        _bulletPreview = _numberPreview = null;
        _font = _size = _heading = _align = null; _fontReflected = null;
        _spacingBox = null; _colorSwatch = _highlightSwatch = null;
        _zoom = _paper = _orient = null; _zoomFit = null;
        _exportBtn = _importBtn = _printBtn = null;

        bool ro = Target?.IsReadOnly == true;
        _builtReadOnly = ro;
        var lvl = EffectiveLevel();
        bool normal = lvl >= ToolbarLevel.Normal;
        bool maximum = lvl >= ToolbarLevel.Maximum;

        void Add(UIElement c) => strip.Children.Add(c);
        void AddSep() => strip.Children.Add(Sep());

        // Leading host items (always).
        foreach (var c in LeadingItems) Add(c);
        if (LeadingItems.Count > 0) AddSep();

        if (ro)
        {
            // Read-only = view toolbar: find + page/zoom + Export/Print (no editing controls, Import
            // hidden). Find works read-only, so a viewer keeps it.
            _findBtn = IconButton("🔎" + Mono, TipSc("Find", ShortcutId.Find),
                () => Target?.RaiseFindRequested(false), RichEditorIcon.Find);
            Add(_findBtn); AddSep();
            bool page = ShowPageControls, file = ShowFileActions;
            if (page) BuildPageControls(strip);
            if (file) { if (page) AddSep(); BuildFileActions(strip); }
        }
        else
        {
            // Group order mirrors the AvaloniaRichEditor original toolbar: history → character
            // toggles → colours → font face/size → paragraph style/align → lists·indent·spacing →
            // inserts (table/image/divider) → page/zoom → file actions.
            _undo = IconButton("↶", TipSc("Undo", ShortcutId.Undo), () => Target?.Undo(), RichEditorIcon.Undo);
            _redo = IconButton("↷", TipSc("Redo", ShortcutId.Redo), () => Target?.Redo(), RichEditorIcon.Redo);
            Add(_undo); Add(_redo); AddSep();

            _bold = ToggleBtn("B", TipSc("Bold", ShortcutId.Bold), () => Target?.ToggleBold(), bold: true, icon: RichEditorIcon.Bold);
            _italic = ToggleBtn("I", TipSc("Italic", ShortcutId.Italic), () => Target?.ToggleItalic(), italic: true, icon: RichEditorIcon.Italic);
            _underline = ToggleBtn("U", TipSc("Underline", ShortcutId.Underline), () => Target?.ToggleUnderline(), icon: RichEditorIcon.Underline);
            _strike = ToggleBtn("S", TipSc("Strikethrough", ShortcutId.Strikethrough), () => Target?.ToggleStrikethrough(), icon: RichEditorIcon.Strikethrough);
            Add(_bold); Add(_italic); Add(_underline); Add(_strike);

            if (normal)
            {
                Add(ColorButton("A", Loc("TextColor"), false));
                Add(ColorButton("✎", Loc("Highlight"), true));
                _painter = ToggleBtn("🖌" + Mono, Loc("FormatPainter"), () => Target?.StartFormatPainter(), icon: RichEditorIcon.FormatPainter);
                Add(_painter);
                Add(IconButton("✕", Loc("ClearFormatting"), () => Target?.ClearFormatting(), RichEditorIcon.ClearFormatting));
            }
            AddSep();

            if (normal)
            {
                var font = MakeCombo(160, Loc("FontFamily")); _font = font;
                font.SelectionChanged += (_, _) => { if (!_suppress && font.SelectedItem is ComboBoxItem ci) Target?.SetRunFontFamily((string)ci.Content); };
                PopulateFontList();
                Add(font);
            }

            // Sizes read as points ("10 pt"), so the unit is explicit — the model/API speak pt. The
            // numeric value lives in Tag, keeping display text and the sync/apply value separate.
            var size = MakeCombo(86, Loc("FontSize")); _size = size;
            foreach (var s in FontSizes) size.Items.Add(new ComboBoxItem { Content = PtText(s), Tag = s });
            size.SelectionChanged += (_, _) => { if (!_suppress && size.SelectedItem is ComboBoxItem ci && ci.Tag is double v) Target?.SetFontSize(v); };
            Add(size);

            if (normal)
            {
                AddSep();
                var heading = MakeCombo(108, Loc("ParagraphStyle")); _heading = heading;
                heading.Items.Add(new ComboBoxItem { Content = Loc("BodyText"), Tag = 0 });
                for (int i = 1; i <= 6; i++) heading.Items.Add(new ComboBoxItem { Content = Loc("Heading" + i), Tag = i });
                heading.SelectionChanged += (_, _) => { if (!_suppress && heading.SelectedItem is ComboBoxItem ci) Target?.SetHeading((int)ci.Tag); };
                Add(heading);

                var align = MakeCombo(96, Loc("Alignment")); _align = align;
                void AddAlign(string text, TextAlignment a) => align.Items.Add(new ComboBoxItem { Content = text, Tag = a });
                AddAlign(Loc("AlignLeft"), TextAlignment.Left);
                AddAlign(Loc("AlignCenter"), TextAlignment.Center);
                AddAlign(Loc("AlignRight"), TextAlignment.Right);
                AddAlign(Loc("AlignJustify"), TextAlignment.Justify);
                align.SelectionChanged += (_, _) => { if (!_suppress && align.SelectedItem is ComboBoxItem ci) Target?.SetTextAlignment((TextAlignment)ci.Tag); };
                Add(align); AddSep();

                // Lists: each is a combo-style box [icon (toggles the list) | current marker | ▾ (glyph/format)].
                var bullet = BuildListBox(RichEditorIcon.BulletList, Loc("BulletList"), () => Target?.ToggleBullet(),
                    (ListMarkerStyle.Disc, "•"), (ListMarkerStyle.Circle, "◦"), (ListMarkerStyle.Square, "▪"), (ListMarkerStyle.Dash, "–"));
                _bullet = bullet.Icon; _bulletPreview = bullet.Preview;
                Add(bullet.Box);
                var number = BuildListBox(RichEditorIcon.NumberedList, Loc("NumberedList"), () => Target?.ToggleNumbering(),
                    (ListMarkerStyle.Decimal, "1."), (ListMarkerStyle.DecimalParen, "1)"),
                    (ListMarkerStyle.LowerAlpha, "a)"), (ListMarkerStyle.UpperAlpha, "A)"), (ListMarkerStyle.LowerRoman, "i)"));
                _number = number.Icon; _numberPreview = number.Preview;
                Add(number.Box);
                // Quote is via the right-click menu / ToggleQuote(); no toolbar button (matches original).
                Add(IconButton("⇥", TipSc("IndentIncrease", ShortcutId.IndentIncrease), () => Target?.Indent(20), RichEditorIcon.IndentIncrease));
                Add(IconButton("⇤", TipSc("IndentDecrease", ShortcutId.IndentDecrease), () => Target?.Indent(-20), RichEditorIcon.IndentDecrease));
                Add(BuildLineSpacingControl());
                AddSep();

                _tableBtn = BaseButton("▦", Loc("InsertTable"), RichEditorIcon.InsertTable);
                _tableBtn.Flyout = BuildTableGridPicker();
                // One image button: inserts a block image; the right-click menu then offers "treat as character".
                _imageBtn = IconButton("🖼", Loc("InsertImage"), async () => await PickAndInsertImageAsync(), RichEditorIcon.InsertImage);
                _dividerBtn = IconButton("―", Loc("InsertDivider"), () => Target?.InsertDivider(), RichEditorIcon.InsertDivider);
                Add(_tableBtn); Add(_imageBtn); Add(_dividerBtn);

                // Find: opens whatever find UI the host wired to RichEditor.FindRequested (the built-in
                // bar in RichEditorView), the same path Ctrl+F takes. Hidden when find is disabled.
                AddSep();
                _findBtn = IconButton("🔎" + Mono, TipSc("Find", ShortcutId.Find),
                    () => Target?.RaiseFindRequested(false), RichEditorIcon.Find);
                Add(_findBtn);
            }

            // Maximum adds the page/zoom controls and file actions.
            if (maximum)
            {
                if (ShowPageControls) { AddSep(); BuildPageControls(strip); }
                if (ShowFileActions) { AddSep(); BuildFileActions(strip); }
            }
        }

        // Host trailing items, after a separator.
        if (TrailingItems.Count > 0) AddSep();
        foreach (var c in TrailingItems) Add(c);

        return new Border { Padding = new Thickness(4), Child = strip };
    }

    // A flyout presenter with no default padding / min-size, so a flyout hugs its content (the default
    // presenter adds ~12px padding and a min-size, which dwarfs the small grid picker).
    internal static Style TightFlyoutPresenter()
    {
        var s = new Style(typeof(FlyoutPresenter));
        s.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        s.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0.0));
        s.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0.0));
        return s;
    }

    // Draw-to-size table picker: hover the grid to choose rows×columns, click to arm the size drag.
    private const int GridRows = 8, GridCols = 10;
    private Flyout BuildTableGridPicker()
    {
        var flyout = new Flyout { FlyoutPresenterStyle = TightFlyoutPresenter() };
        var root = new StackPanel { Spacing = 4, Padding = new Thickness(2) };
        var label = new TextBlock { Text = "1 × 1", HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 };
        var grid = new Grid();
        for (int c = 0; c < GridCols; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int r = 0; r < GridRows; r++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var idle = new SolidColorBrush(Color.FromArgb(255, 0xE4, 0xE4, 0xE4));
        var hot = new SolidColorBrush(Color.FromArgb(255, 0x60, 0xA0, 0xE0));
        var border = new SolidColorBrush(Color.FromArgb(255, 0xAA, 0xAA, 0xAA));
        var cells = new Border[GridRows, GridCols];

        void Highlight(int rr, int cc)
        {
            for (int r = 0; r < GridRows; r++)
                for (int c = 0; c < GridCols; c++)
                    cells[r, c].Background = (r <= rr && c <= cc) ? hot : idle;
            label.Text = $"{rr + 1} × {cc + 1}";
        }

        for (int r = 0; r < GridRows; r++)
            for (int c = 0; c < GridCols; c++)
            {
                var cell = new Border
                {
                    Width = 16, Height = 16, Margin = new Thickness(1),
                    Background = idle, BorderBrush = border, BorderThickness = new Thickness(0.5),
                };
                Grid.SetRow(cell, r);
                Grid.SetColumn(cell, c);
                int rr = r, cc = c;
                cell.PointerEntered += (_, _) => Highlight(rr, cc);
                // Pick rows×cols here; then drag from the caret on the document to set the table's size.
                cell.Tapped += (_, _) => { Target?.BeginTableDraw(rr + 1, cc + 1); flyout.Hide(); };
                cells[r, c] = cell;
                grid.Children.Add(cell);
            }
        Highlight(0, 0);
        root.Children.Add(grid);
        root.Children.Add(label);
        flyout.Content = root;
        return flyout;
    }

    private async Task PickAndInsertImageAsync()
    {
        if (Target == null || ImagePicker == null) return;
        try
        {
            var bytes = await ImagePicker();
            if (bytes is { Length: > 0 }) Target.InsertImageBlock(bytes);
        }
        catch { /* host picker failed/cancelled */ }
    }

    // A combo-style list control: a bordered box of [icon (toggles the list) | current marker | ▾ (style
    // menu)]. Returns the box plus the icon button and preview label so Sync can highlight the active state
    // and show the caret paragraph's current marker.
    private (Border Box, Button Icon, TextBlock Preview) BuildListBox(
        RichEditorIcon iconKind, string tip, Action toggle, params (ListMarkerStyle Style, string Glyph)[] options)
    {
        var icon = new Button
        {
            Content = IconOrText(iconKind, options[0].Glyph),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 2, 2, 2), MinWidth = 0, VerticalAlignment = VerticalAlignment.Center,
        };
        icon.Click += (_, _) => toggle();
        ToolTipService.SetToolTip(icon, tip);

        var preview = new TextBlock
        {
            Text = options[0].Glyph, FontSize = 12, MinWidth = 16,
            TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        };

        var menu = new MenuFlyout();
        // "없음" FIRST (HWP/Word style-picker convention): removes the list attribute entirely —
        // bullet/numbering, marker style, and nesting level (RemoveList).
        var none = new MenuFlyoutItem { Text = Loc("ListNone") };
        none.Click += (_, _) => Target?.RemoveList();
        menu.Items.Add(none);
        menu.Items.Add(new MenuFlyoutSeparator());
        foreach (var (style, g) in options)
        {
            var s = style;
            var item = new MenuFlyoutItem { Text = g };
            item.Click += (_, _) => Target?.SetListStyle(s);
            menu.Items.Add(item);
        }
        var caret = new Button
        {
            Content = SmallChevron(),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0, 2, 0), MinWidth = 16, VerticalAlignment = VerticalAlignment.Center,
            Flyout = menu,
        };
        ToolTipService.SetToolTip(caret, tip);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(icon);
        row.Children.Add(preview);
        row.Children.Add(caret);
        var box = new Border
        {
            Child = row,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0xDC, 0xDC, 0xDC)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(4, 0, 4, 0),
            Height = CtlHeight, VerticalAlignment = VerticalAlignment.Center,
        };
        return (box, icon, preview);
    }

    // Line-spacing control: a bordered box (matching the list boxes) holding the glyph, an editable % box,
    // tight ▲▼ steppers (±10%) and a ▾ presets dropdown. Each maps to Paragraph.LineSpacing = %/100.
    private Border BuildLineSpacingControl()
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(AsElement(IconOrText(RichEditorIcon.LineSpacing, "↕")));

        _spacingBox = new TextBox
        {
            Text = "100%", MinWidth = 36, FontSize = 12, MinHeight = 0,
            Padding = new Thickness(2, 1, 2, 1), BorderThickness = new Thickness(0),
            Background = ClearBrush, TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        void Commit() => ApplySpacingPercent(CurrentSpacingPercent());
        _spacingBox.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Enter) { Commit(); e.Handled = true; } };
        _spacingBox.LostFocus += (_, _) => Commit();
        ToolTipService.SetToolTip(_spacingBox, Loc("LineSpacing"));
        row.Children.Add(_spacingBox);

        var menu = new MenuFlyout();
        foreach (var pct in SpacingPercents)
        {
            int p = pct;
            var item = new MenuFlyoutItem { Text = p + "%" };
            item.Click += (_, _) => ApplySpacingPercent(p);
            menu.Items.Add(item);
        }
        var presets = new Button
        {
            Content = SmallChevron(),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(3),
            Padding = new Thickness(2, 0, 2, 0), MinWidth = 16, VerticalAlignment = VerticalAlignment.Center,
            Flyout = menu,
        };
        ToolTipService.SetToolTip(presets, Loc("LineSpacing"));

        var steppers = new StackPanel { Orientation = Orientation.Vertical, VerticalAlignment = VerticalAlignment.Center };
        steppers.Children.Add(StepButton(up: true, +10));
        steppers.Children.Add(StepButton(up: false, -10));

        row.Children.Add(presets);
        row.Children.Add(steppers);
        return new Border
        {
            Child = row,
            BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0xDC, 0xDC, 0xDC)),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 0, 4, 0),
            Height = CtlHeight, VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private Button StepButton(bool up, int delta)
    {
        var b = new Button
        {
            Content = new FontIcon { FontFamily = new FontFamily("Segoe Fluent Icons"), Glyph = ((char)(up ? 0xE70E : 0xE70D)).ToString(), FontSize = 8 },
            Width = 16, Height = 11, Padding = new Thickness(0),
            Background = ClearBrush, BorderThickness = new Thickness(0), CornerRadius = new CornerRadius(2),
            VerticalAlignment = VerticalAlignment.Center, HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        b.Click += (_, _) => ApplySpacingPercent(CurrentSpacingPercent() + delta);
        return b;
    }

    // The percentage currently shown in the spacing box (digits only); 100 when empty/unparsable.
    private int CurrentSpacingPercent()
    {
        var sb = new System.Text.StringBuilder();
        foreach (char c in _spacingBox?.Text ?? "") if (char.IsDigit(c)) sb.Append(c);
        return int.TryParse(sb.ToString(), out int p) && p > 0 ? p : 100;
    }

    // Clamps a line-spacing %, reflects it in the box, and applies it to the caret paragraph.
    private void ApplySpacingPercent(int pct)
    {
        pct = Math.Clamp(pct, 100, 1000);
        if (_spacingBox != null) _spacingBox.Text = pct + "%";
        Target?.SetLineSpacing(pct / 100.0);
    }

    // ---- caret-state reflection -------------------------------------------
    private void Sync()
    {
        var rt = Target;
        // Read-only toggled since the last Build → rebuild (edit toolbar ⇄ view toolbar).
        if (rt != null && rt.IsReadOnly != _builtReadOnly) Content = Build();
        if (rt == null) return;

        _suppress = true;
        try
        {
            var f = rt.GetCaretFormat();
            if (_bold != null) SetActive(_bold, f.Bold);
            if (_italic != null) SetActive(_italic, f.Italic);
            if (_underline != null) SetActive(_underline, f.Underline);
            if (_strike != null) SetActive(_strike, f.Strike);
            if (_bullet != null) SetActive(_bullet, f.List == ListKind.Bullet);
            if (_number != null) SetActive(_number, f.List == ListKind.Ordered);
            if (_painter != null) SetActive(_painter, rt.IsFormatPainterActive);

            // List previews show the caret paragraph's current marker, full-ink when that list kind is
            // active and dimmed otherwise.
            if (_bulletPreview != null)
            {
                bool bulletOn = f.List == ListKind.Bullet;
                _bulletPreview.Text = RichEditor.ListMarkerText(ListKind.Bullet, bulletOn ? f.ListMarker : ListMarkerStyle.Default, 1);
                _bulletPreview.Foreground = bulletOn ? BlackInk : DimInk;
            }
            if (_numberPreview != null)
            {
                bool numberOn = f.List == ListKind.Ordered;
                _numberPreview.Text = RichEditor.ListMarkerText(ListKind.Ordered, numberOn ? f.ListMarker : ListMarkerStyle.Default, 1);
                _numberPreview.Foreground = numberOn ? BlackInk : DimInk;
            }

            // Picker bars follow the caret's run: explicit colours show as-is, defaults fall back to
            // black text / "no highlight" grey. (ReflectPickerColor null-guards the swatches.)
            ReflectPickerColor(false, f.Foreground is { } fg ? new SolidColorBrush(fg) : BlackInk);
            ReflectPickerColor(true, f.Background is { } bg ? new SolidColorBrush(bg) : NoColorBrush);

            if (_font != null)
            {
                string fam = f.FontFamily ?? rt.DefaultFontFamily;
                if (fam != _fontReflected) { SelectByContent(_font, fam); _fontReflected = fam; }
            }
            if (_size != null)
            {
                // Off-ladder sizes (e.g. 10.5 pt from a pasted document) have no item — show them via
                // PlaceholderText rather than leaving the combo blank (same trick as the zoom combo).
                double fs = f.FontSize > 0 ? f.FontSize : BodySizePt;
                ComboBoxItem? hit = null;
                foreach (var it in _size.Items)
                    if (it is ComboBoxItem ci && ci.Tag is double d && Math.Abs(d - fs) < 0.01) { hit = ci; break; }
                if (hit != null) _size.SelectedItem = hit;
                else { _size.SelectedIndex = -1; _size.PlaceholderText = PtText(fs); }
            }
            if (_heading != null) SelectByTag(_heading, f.Heading);
            if (_align != null) SelectByTag(_align, f.Align);

            // Spacing box shows the caret paragraph's current % (unset / ≤1.0 = single = 100%). Don't
            // overwrite while the user is editing the field.
            if (_spacingBox != null && _spacingBox.FocusState == FocusState.Unfocused)
            {
                double ls = f.LineSpacing;
                int pct = double.IsNaN(ls) || ls <= 0 ? 100 : (int)Math.Round(ls * 100);
                _spacingBox.Text = pct + "%";
            }

            if (_undo != null) _undo.IsEnabled = rt.CanUndo;
            if (_redo != null) _redo.IsEnabled = rt.CanRedo;
            if (_tableBtn != null) _tableBtn.Visibility = rt.AllowTables ? Visibility.Visible : Visibility.Collapsed;
            if (_imageBtn != null) _imageBtn.Visibility = rt.AllowImages ? Visibility.Visible : Visibility.Collapsed;
            if (_findBtn != null) _findBtn.Visibility = rt.AllowFindReplace ? Visibility.Visible : Visibility.Collapsed;
            SyncPage();        // reflect zoom/paper/orientation state
            SyncFileActions(); // Print/Import button visibility
        }
        finally { _suppress = false; }
    }

    private static void SetActive(ToggleButton b, bool active)
    {
        b.IsChecked = active;
        b.Background = active ? ActiveBrush : ClearBrush;
    }

    // List-box icon buttons aren't ToggleButtons; reflect active state via background only.
    private static void SetActive(Button b, bool active) => b.Background = active ? ActiveBrush : ClearBrush;

    private static void SelectByContent(ComboBox combo, string content)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && (string)ci.Content == content) { combo.SelectedItem = ci; return; }
        combo.SelectedIndex = -1;
    }

    private static void SelectByTag(ComboBox combo, object tag)
    {
        foreach (var item in combo.Items)
            if (item is ComboBoxItem ci && Equals(ci.Tag, tag)) { combo.SelectedItem = ci; return; }
        combo.SelectedIndex = -1;
    }

    // ---- small widget builders --------------------------------------------
    // Fixed-height group divider (a full-line bar looked heavy; wrap-panel centering aligns it).
    // Small extra margin so group gaps (spacing 4 + 2×2) read wider than in-group gaps (4).
    private static UIElement Sep() => new Border
    {
        Width = 1, Height = 20, Margin = new Thickness(2, 0, 2, 0),
        Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)),
    };

    // Icon precedence: host override (RichEditorIcons.Provider) > built-in Segoe Fluent Icons FontIcon
    // (ToolbarIcons) > styled-text fallback (`text`, e.g. the letter B/I/U/S or a unicode glyph).
    private static object IconOrText(RichEditorIcon? icon, string text)
        => (icon is { } k ? RichEditorIcons.TryCreate(k) ?? ToolbarIcons.Create(k) : null) ?? (object)text;

    private Button IconButton(string glyph, string tip, Action act, RichEditorIcon? icon = null)
    {
        var b = BaseButton(glyph, tip, icon);
        b.Click += (_, _) => act();
        return b;
    }

    private Button IconButton(string glyph, string tip, Func<Task> act, RichEditorIcon? icon = null)
    {
        var b = BaseButton(glyph, tip, icon);
        b.Click += async (_, _) => await act();
        return b;
    }

    private static Button BaseButton(string glyph, string tip, RichEditorIcon? icon = null)
    {
        var b = new Button
        {
            // AsElement centres + tightens text fallbacks (▦ table, ✕ clear, ⇥/⇤ indent) so they
            // line up with the FontIcon buttons instead of sitting low as raw strings.
            Content = AsElement(IconOrText(icon, glyph)),
            Width = BtnWidth,
            Height = CtlHeight,
            Padding = new Thickness(0),
            Background = ClearBrush,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(b, tip);
        return b;
    }

    private ToggleButton ToggleBtn(string glyph, string tip, Action act, bool bold = false, bool italic = false, RichEditorIcon? icon = null)
    {
        var b = new ToggleButton
        {
            Content = AsElement(IconOrText(icon, glyph)), // centred + tight, like the icon buttons
            Width = BtnWidth,
            Height = CtlHeight,
            Padding = new Thickness(0),
            Background = ClearBrush,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            // Letter styling only matters for the text fallback; a FontIcon ignores it.
            FontWeight = bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
        };
        ToolTipService.SetToolTip(b, tip);
        ApplyToggleCheckedStyle(b);
        // Drive on click; Sync() owns IsChecked, so don't react to Checked/Unchecked (would double-toggle).
        b.Click += (_, _) => { if (!_suppress) act(); };
        return b;
    }

    // Retints the ToggleButton template's Checked visual states. Without this the checked background
    // comes from the theme's accent brushes (solid blue + white glyph) regardless of what we assign to
    // Background, because the Checked visual state overwrites it.
    private static void ApplyToggleCheckedStyle(ToggleButton b)
    {
        b.Resources["ToggleButtonBackgroundChecked"] = ActiveBrush;
        b.Resources["ToggleButtonBackgroundCheckedPointerOver"] = ActiveHoverBrush;
        b.Resources["ToggleButtonBackgroundCheckedPressed"] = ActiveHoverBrush;
        b.Resources["ToggleButtonForegroundChecked"] = BlackInk;
        b.Resources["ToggleButtonForegroundCheckedPointerOver"] = BlackInk;
        b.Resources["ToggleButtonForegroundCheckedPressed"] = BlackInk;
        b.Resources["ToggleButtonBorderBrushChecked"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushCheckedPointerOver"] = ClearBrush;
        b.Resources["ToggleButtonBorderBrushCheckedPressed"] = ClearBrush;
    }

    private static ComboBox MakeCombo(double width, string tip)
    {
        // No margin: the wrap panel's HorizontalSpacing is the single source of gaps, so the space
        // between any two strip controls is identical (per-control margins made combo gaps wider).
        // FontSize pinned so every combo's selected value renders at the same point size (the font-name
        // items still show in their own typeface — a feature — just at the uniform size).
        // MinHeight too: the default ComboBox style pins TextControlThemeMinHeight (32), which would
        // clamp a smaller Height right back up and leave the combos taller than the buttons.
        var c = new ComboBox
        {
            Width = width,
            FontSize = ComboFontSize,
            Height = CtlHeight,
            MinHeight = CtlHeight,
            Padding = new Thickness(10, 0, 6, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        ToolTipService.SetToolTip(c, tip);
        return c;
    }

    // A palette + hex-input flyout button. The face is the picker glyph over a thin bar that shows the
    // caret's current colour (updated in Sync). `highlight` selects foreground vs highlight (background).
    private Button ColorButton(string glyph, string tip, bool highlight)
    {
        var initial = new SolidColorBrush(highlight ? Color.FromArgb(255, 0xFF, 0xF1, 0x76) : Colors.Black);

        // Text colour keeps the plain "A" letter (a FontColor FontIcon carries its own colour element,
        // which doubles up awkwardly with the bar); the highlight face uses the real Highlight pen icon
        // so its visual weight matches the other 16px FontIcons (the ✎ text glyph looked undersized).
        var glyphEl = highlight
            ? ToolbarIcons.Create(RichEditorIcon.Highlight, 14) ?? AsElement(glyph)
            : AsElement(glyph);
        var swatch = new Border
        {
            Height = 6, MinWidth = 24, CornerRadius = new CornerRadius(1),
            Background = initial, Margin = new Thickness(0, 2, 0, 0),
        };
        if (highlight) _highlightSwatch = swatch; else _colorSwatch = swatch;
        var face = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        face.Children.Add(glyphEl);
        face.Children.Add(swatch);

        var btn = new Button
        {
            Content = face,
            Width = BtnWidth,
            Height = CtlHeight,
            // Tight: the face stacks a glyph over the colour bar, which needs the full 28px box.
            Padding = new Thickness(0),
            Background = ClearBrush,
            BorderThickness = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        ToolTipService.SetToolTip(btn, tip);

        var flyout = new Flyout();
        void Apply(Color? c)
        {
            if (Target == null) return;
            if (highlight) Target.SetHighlight(c);
            else Target.SetForeground(c); // null = back to the automatic default (theme-aware)
            ReflectPickerColor(highlight, c is { } cc ? new SolidColorBrush(cc) : (highlight ? NoColorBrush : BlackInk));
            flyout.Hide();
        }

        // 8-column palette: fixed-size swatches in a width-capped wrap panel wrap to 5 rows of 8.
        var grid = new ToolbarWrapPanel { Width = 8 * 24, HorizontalSpacing = 0, VerticalSpacing = 0 };
        foreach (var hex in Palette)
        {
            var color = ParseHex(hex);
            var sw = new Button
            {
                Background = new SolidColorBrush(color),
                Width = 22, Height = 22, Margin = new Thickness(1), Padding = new Thickness(0),
                BorderThickness = new Thickness(1),
                BorderBrush = new SolidColorBrush(Color.FromArgb(255, 0x80, 0x80, 0x80)),
            };
            sw.Click += (_, _) => Apply(color);
            grid.Children.Add(sw);
        }

        var panel = new StackPanel { Spacing = 6, Width = 200, Padding = new Thickness(4) };
        panel.Children.Add(grid);

        // "No highlight" / "Automatic (default)": both clear the explicit color back to null. The
        // foreground previously had no way back to the automatic default short of ClearFormatting.
        var none = new Button { Content = Loc(highlight ? "NoHighlight" : "AutoColor"), HorizontalAlignment = HorizontalAlignment.Stretch };
        none.Click += (_, _) => Apply(null);
        panel.Children.Add(none);

        var hexRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        var hexBox = new TextBox { PlaceholderText = "#RRGGBB", Width = 110 };
        var applyBtn = new Button { Content = Loc("Apply") };
        applyBtn.Click += (_, _) => { if (TryParseHex(hexBox.Text, out var c)) Apply(c); };
        hexRow.Children.Add(hexBox);
        hexRow.Children.Add(applyBtn);
        panel.Children.Add(hexRow);

        flyout.Content = panel;
        btn.Flyout = flyout;
        return btn;
    }

    // Pushes `brush` onto the relevant picker's current-colour bar.
    private void ReflectPickerColor(bool highlight, Brush brush)
    {
        if (highlight) { if (_highlightSwatch != null) _highlightSwatch.Background = brush; }
        else { if (_colorSwatch != null) _colorSwatch.Background = brush; }
    }

    // Small dropdown chevron (Segoe Fluent ChevronDown) — the "▾" text glyph rendered at wildly
    // different sizes depending on the fallback font; a 10px FontIcon is identical everywhere.
    private static UIElement SmallChevron() => new FontIcon
    {
        FontFamily = new FontFamily("Segoe Fluent Icons"),
        Glyph = ((char)0xE70D).ToString(), // ChevronDown
        FontSize = 10,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // Coerces an IconOrText result (UIElement or string) into a centred element for a button face.
    // Text fallbacks render at the object-icon size with TIGHT line bounds — otherwise the glyph's
    // ascent/descent padding pushes it off the vertical centre that FontIcons sit on (this is why a
    // raw-string "▦" table glyph looked low and wide next to the image FontIcon).
    private static UIElement AsElement(object content)
        => content as UIElement ?? new TextBlock
        {
            Text = content.ToString(), FontSize = 15,
            TextLineBounds = Microsoft.UI.Xaml.TextLineBounds.Tight,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

    private static Color ParseHex(string hex) => TryParseHex(hex, out var c) ? c : Colors.Black;

    private static bool TryParseHex(string? hex, out Color color)
    {
        color = Colors.Black;
        if (string.IsNullOrWhiteSpace(hex)) return false;
        var s = hex.Trim().TrimStart('#');
        if (s.Length != 6 && s.Length != 8) return false;
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var v)) return false;
        byte a = s.Length == 8 ? (byte)(v >> 24) : (byte)255;
        byte r = (byte)((v >> (s.Length == 8 ? 16 : 16)) & 0xFF);
        byte g = (byte)((v >> 8) & 0xFF);
        byte b = (byte)(v & 0xFF);
        color = Color.FromArgb(a, r, g, b);
        return true;
    }
}
