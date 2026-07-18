using Windows.UI;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace WinUIRichEditor.Controls;

// Caret + selection appearance. The original exposes IBrush SelectionBrush/CaretBrush; here they are
// WinUI Brushes (defaulting to SolidColorBrush) and the renderer pulls a Windows.UI.Color out of them,
// since Win2D's DrawLine/FillRectangle take a Color. A non-solid brush falls back to the default color.
public partial class RichEditor
{
    /// <summary>Fill brush for the text selection highlight. Defaults to a translucent blue.</summary>
    public static readonly DependencyProperty SelectionBrushProperty = DependencyProperty.Register(
        nameof(SelectionBrush), typeof(Brush), typeof(RichEditor),
        new PropertyMetadata(new SolidColorBrush(Color.FromArgb(80, 0, 120, 215)), OnAppearanceBrushChanged));

    /// <summary>Fill brush for the text selection highlight.</summary>
    public Brush SelectionBrush
    {
        get => (Brush)GetValue(SelectionBrushProperty);
        set => SetValue(SelectionBrushProperty, value);
    }

    /// <summary>Brush for the blinking caret. Defaults to black.</summary>
    public static readonly DependencyProperty CaretBrushProperty = DependencyProperty.Register(
        nameof(CaretBrush), typeof(Brush), typeof(RichEditor),
        new PropertyMetadata(new SolidColorBrush(Colors.Black), OnAppearanceBrushChanged));

    /// <summary>Brush for the blinking caret.</summary>
    public Brush CaretBrush
    {
        get => (Brush)GetValue(CaretBrushProperty);
        set => SetValue(CaretBrushProperty, value);
    }

    /// <summary>Default text color for runs without an explicit foreground (and list markers). Defaults
    /// to black; a dark-theme host assigns a light brush. Runs with their own Foreground are unaffected.</summary>
    public static readonly DependencyProperty TextForegroundProperty = DependencyProperty.Register(
        nameof(TextForeground), typeof(Brush), typeof(RichEditor),
        new PropertyMetadata(new SolidColorBrush(Colors.Black), OnAppearanceBrushChanged));

    /// <summary>Default text color for runs without an explicit foreground.</summary>
    public Brush TextForeground
    {
        get => (Brush)GetValue(TextForegroundProperty);
        set => SetValue(TextForegroundProperty, value);
    }

    /// <summary>Background of the editing canvas in continuous mode. Default null = transparent (the
    /// host's background shows through). Paged mode keeps its white paper / grey desk regardless.</summary>
    public static readonly DependencyProperty CanvasBackgroundProperty = DependencyProperty.Register(
        nameof(CanvasBackground), typeof(Brush), typeof(RichEditor),
        new PropertyMetadata(null, OnCanvasBackgroundChanged));

    /// <summary>Background of the editing canvas in continuous mode (null = transparent).</summary>
    public Brush? CanvasBackground
    {
        get => (Brush?)GetValue(CanvasBackgroundProperty);
        set => SetValue(CanvasBackgroundProperty, value);
    }

    private static void OnCanvasBackgroundChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var ed = (RichEditor)d;
        ed._canvas.ClearColor = ed.CanvasBackground is SolidColorBrush scb ? scb.Color : Colors.Transparent;
        ed.InvalidateCanvas();
    }

    private static void OnAppearanceBrushChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((RichEditor)d).InvalidateCanvas();

    // The effective default text color (Win2D draws take a Color, not a Brush).
    internal Color EffectiveTextColor => BrushColor(TextForeground, Colors.Black);

    // The selection-highlight color, resolved from SelectionBrush (Win2D fills take a Color).
    private Color SelectionFill => BrushColor(SelectionBrush, Color.FromArgb(80, 0, 120, 215));

    // The caret color, resolved from CaretBrush.
    private Color CaretColor => BrushColor(CaretBrush, Colors.Black);

    private static Color BrushColor(Brush? brush, Color fallback)
        => brush is SolidColorBrush scb ? scb.Color : fallback;
}
