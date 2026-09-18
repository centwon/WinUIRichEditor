using System;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using Xunit;

namespace WinUIRichEditor.Tests;

/// <summary>The toolbar's Import / Export (RichEditorToolbar.PageFile.cs) — a file no test had referenced
/// (audit 2026-09-19). Import is <c>ImportBytesAsync</c>: the handler minus its file picker, so a test can
/// hand it the bytes of any file.</summary>
[Collection(UiTests.Collection)]
public class ToolbarFileActionTests
{
    private const string Korean = "가져오기 한글 문장";

    private static string TextOf(RichEditor ed)
        => string.Concat(ed.Document!.Blocks.OfType<Paragraph>().SelectMany(p => p.Inlines).OfType<Run>().Select(r => r.Text));

    private static string Import(byte[] bytes)
    {
        string text = "";
        UiThread.RunAsync(async () =>
        {
            var ed = new RichEditor();
            ed.LoadHtml("<p>before</p>");
            var toolbar = new RichEditorToolbar { Target = ed };
            await toolbar.ImportBytesAsync(bytes);
            text = TextOf(ed);
        });
        return text;
    }

    private static byte[] WithBom(Encoding enc, string s) => enc.GetPreamble().Concat(enc.GetBytes(s)).ToArray();

    private static string JsonDoc()
    {
        string json = "";
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            ed.LoadHtml($"<p>{Korean}</p>");
            json = ed.ToJson();
        });
        return json;
    }

    private static string RtfDoc()
    {
        string rtf = "";
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            ed.LoadHtml($"<p>{Korean}</p>");
            rtf = ed.ToRtf();
        });
        return rtf;
    }

    // Control: the file as this editor writes it (UTF-8, no BOM).
    [Theory]
    [InlineData("html")]
    [InlineData("json")]
    [InlineData("rtf")]
    public void AFileWithoutABom_Imports(string kind)
    {
        string s = kind switch { "html" => $"<p>{Korean}</p>", "json" => JsonDoc(), _ => RtfDoc() };
        Assert.Contains(Korean, Import(Encoding.UTF8.GetBytes(s)));
    }

    // Windows editors (Notepad before 1903, Visual Studio, PowerShell 5's Out-File/Set-Content -Encoding utf8,
    // Excel's "Unicode text") write a byte-order mark. A BOM is not whitespace to string.TrimStart, so the
    // format sniff saw neither "<" nor "{\rtf" and handed the file to the JSON reader.
    [Theory]
    [InlineData("html", "utf-8")]
    [InlineData("json", "utf-8")]
    [InlineData("rtf", "utf-8")]
    [InlineData("html", "utf-16")]
    [InlineData("json", "utf-16")]
    public void AFileWithAByteOrderMark_Imports(string kind, string encoding)
    {
        var enc = encoding == "utf-16" ? Encoding.Unicode : Encoding.UTF8;
        string s = kind switch { "html" => $"<p>{Korean}</p>", "json" => JsonDoc(), _ => RtfDoc() };
        Assert.Contains(Korean, Import(WithBom(enc, s)));
    }

    // ---- the built-in buttons and page controls --------------------------------------------------------

    private const BindingFlags NP = BindingFlags.NonPublic | BindingFlags.Instance;
    private static T? Field<T>(RichEditorToolbar tb, string name) where T : class
        => typeof(RichEditorToolbar).GetField(name, NP)!.GetValue(tb) as T;

    // Printing is host-specific, so the Print button exists only while someone handles PrintRequested.
    [Fact]
    public void PrintButton_FollowsWhetherPrintRequestedIsHandled()
    {
        UiThread.Run(() =>
        {
            var tb = new RichEditorToolbar { Target = new RichEditor(), ToolbarLevel = ToolbarLevel.Maximum };
            Visibility Print() => Field<Button>(tb, "_printBtn")!.Visibility;
            Assert.Equal(Visibility.Collapsed, Print());
            EventHandler h = (_, _) => { };
            tb.PrintRequested += h;
            Assert.Equal(Visibility.Visible, Print());
            tb.PrintRequested -= h;
            Assert.Equal(Visibility.Collapsed, Print());
        });
    }

    // Import replaces the document, so a viewer's toolbar has no Import — including one that became a viewer
    // after the toolbar was built.
    [Fact]
    public void Import_IsHiddenInAViewer_EvenOneThatBecameReadOnlyLater()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
            Assert.Equal(Visibility.Visible, Field<Button>(tb, "_importBtn")!.Visibility);
            ed.IsReadOnly = true;
            var import = Field<Button>(tb, "_importBtn");
            Assert.True(import == null || import.Visibility == Visibility.Collapsed, "a viewer's toolbar offers Import");
            Assert.NotNull(Field<Button>(tb, "_exportBtn")); // Export stays: a viewer may save a copy
        });
    }

    // Ctrl+wheel produces factors outside the preset list (110%, 121%, ...). They show as the combo's
    // placeholder with nothing selected — not a stale preset, and not a blank box.
    [Fact]
    public void ANonPresetZoom_ShowsAsThePlaceholder_APresetSelectsItsItem()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor();
            var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
            var zoom = Field<ComboBox>(tb, "_zoom")!;
            ed.SetZoom(1.1);
            typeof(RichEditorToolbar).GetMethod("Sync", NP)!.Invoke(tb, null);
            Assert.Equal(-1, zoom.SelectedIndex);
            Assert.Equal("110%", zoom.PlaceholderText);
            ed.SetZoom(1.5);
            typeof(RichEditorToolbar).GetMethod("Sync", NP)!.Invoke(tb, null);
            Assert.Equal(150, (zoom.SelectedItem as ComboBoxItem)?.Tag);
        });
    }

    // Fit-width has no meaning without a page; leaving a fitted paged view for Continuous drops back to 100%.
    [Fact]
    public void ChoosingContinuous_LeavesFitWidthAt100Percent()
    {
        UiThread.Run(() =>
        {
            var ed = new RichEditor { PageSize = RichEditorPageSize.A4 };
            var tb = new RichEditorToolbar { Target = ed, ToolbarLevel = ToolbarLevel.Maximum };
            ed.FitToWidth();
            Assert.True(ed.IsFitWidth);
            Field<ComboBox>(tb, "_paper")!.SelectedIndex = 0; // Continuous
            Assert.Equal(RichEditorPageSize.Continuous, ed.PageSize);
            Assert.False(ed.IsFitWidth);
            Assert.Equal(1.0, ed.Zoom, 3);
        });
    }
}
