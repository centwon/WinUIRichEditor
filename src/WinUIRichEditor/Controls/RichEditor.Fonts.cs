using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.Graphics.Canvas.Text;

namespace WinUIRichEditor.Controls;

// System font enumeration (parity with AvaloniaRichEditor). The installed font families — with names
// localized to the OS UI language (e.g. "맑은 고딕" on Korean Windows, "Malgun Gothic" on English) — feed
// the toolbar combo and the right-click font submenu. Both Win2D and Avalonia sit on DirectWrite, so the
// localized family names match and resolve back through the same font system for display AND application.
public partial class RichEditor
{
    // The OS default UI font (Windows message font) so unstyled documents look native rather than
    // defaulting to a fixed face. Falls back to "Segoe UI" when the query is unavailable.
    internal static string SystemDefaultFontFamily()
        => SystemFontInfo.MessageFontFaceName() ?? "Segoe UI";

    // Cached system font list. DirectWrite reports family names localized to the UI language, and those
    // names resolve back through the same font set, so they're used as-is for both display and application.
    private static IReadOnlyList<string>? _systemFontChoices;

    private static IReadOnlyList<string> SystemFontChoices()
    {
        if (_systemFontChoices != null) return _systemFontChoices;
        var names = new List<string>();
        try
        {
            string locale = CultureInfo.CurrentUICulture.Name.ToLowerInvariant();      // e.g. "ko-kr"
            string lang = locale.Length >= 2 ? locale.Substring(0, 2) : locale;          // e.g. "ko"
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var face in CanvasFontSet.GetSystemFontSet().Fonts)
            {
                string? name = LocalizedFamilyName(face.FamilyNames, locale, lang);
                if (!string.IsNullOrWhiteSpace(name) && seen.Add(name!)) names.Add(name!);
            }
            names.Sort(StringComparer.Create(CultureInfo.CurrentUICulture, ignoreCase: true));
        }
        catch
        {
            names.Clear();
        }
        // Platforms/environments without font enumeration: widely-available fallback families.
        if (names.Count == 0)
            names.AddRange(new[] { "Segoe UI", "Arial", "Times New Roman", "Courier New", "Verdana", "Georgia" });
        return _systemFontChoices = names;
    }

    // Picks the family name for the UI language: exact locale → language-only → en-us → first available.
    private static string? LocalizedFamilyName(IReadOnlyDictionary<string, string> familyNames, string locale, string lang)
    {
        if (familyNames.Count == 0) return null;
        if (familyNames.TryGetValue(locale, out var exact)) return exact;
        foreach (var kv in familyNames)
            if (kv.Key.StartsWith(lang, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        if (familyNames.TryGetValue("en-us", out var en)) return en;
        foreach (var kv in familyNames) return kv.Value;
        return null;
    }

    /// <summary>Font families offered in the font pickers (toolbar combo and right-click submenu). Defaults
    /// to the installed system fonts, with names localized by — and sorted for — the OS UI language. Assign a
    /// non-empty list to curate the offered set.</summary>
    public static readonly DependencyProperty FontFamilyChoicesProperty = DependencyProperty.Register(
        nameof(FontFamilyChoices), typeof(IReadOnlyList<string>), typeof(RichEditor),
        new PropertyMetadata(Array.Empty<string>()));

    /// <summary>Font families offered in the font pickers; defaults to the installed system fonts.</summary>
    public IReadOnlyList<string> FontFamilyChoices
    {
        get
        {
            var v = (IReadOnlyList<string>)GetValue(FontFamilyChoicesProperty);
            return v.Count > 0 ? v : SystemFontChoices();
        }
        set => SetValue(FontFamilyChoicesProperty, value);
    }
}
