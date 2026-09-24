using System;
using System.Collections.Generic;
using System.IO;
using WinUIRichEditor.Controls;
using WinUIRichEditor.Documents;
using WinUIRichEditor.Formatters;

namespace WinUIRichEditor.Demo;

/// <summary><c>--pageprobe=&lt;path&gt;</c>: drives the page margins through every path a host reaches and writes
/// what came back, one line per step, for <c>tools/fault-sweep.ps1</c> to diff between the JIT build and the
/// Native AOT publish.
/// <para>Why a probe and not a keystroke phase: <see cref="RichEditor.PageMarginProperty"/> is the first
/// dependency property that holds a C# <c>record struct</c> — a value WinRT has no type for, boxed across the
/// projection on every <c>SetValue</c>/<c>GetValue</c>. AOT renders and still differs (1.1.0 shipped broken
/// line metrics that way), so the value has to be read back, not just set. The sweep's input is keyboard
/// and one click; no key reaches the margins, and clicking the toolbar picker by coordinates would test
/// the coordinates. It runs on an editor of its own, off screen, so the page the sweep types into keeps
/// the layout its click offsets assume.</para></summary>
internal static class PageMarginProbe
{
    internal static void Run(string path)
    {
        if (path.Length == 0) return;
        var lines = new List<string>();
        void Step(string name, Func<object?> read)
        {
            try { lines.Add($"{name}: {read()}"); }
            catch (Exception ex) { lines.Add($"{name}: EXCEPTION {ex.GetType().Name}: {ex.Message}"); }
        }

        RichEditor? ed = null;
        Step("create", () => { ed = new RichEditor { Document = DemoContent.Sample(), PageSize = RichEditorPageSize.A4 }; return "ok"; });
        if (ed is null) { Write(path, lines); return; }
        var margins = new PageMargins(20, 12.5, 25, 30.4);

        Step("default", () => ed.PageMargin);
        Step("set", () => { ed.PageMargin = margins; return ed.PageMargin; });
        Step("boxed", () => ed.GetValue(RichEditor.PageMarginProperty) is PageMargins m ? $"PageMargins {m}" : "NOT a PageMargins");
        Step("document", () => ed.Document?.PageSetup?.Margin);
        Step("paper", () => ed.GetPaperPixelSize());
        Step("pages", () => ed.GetPrintPageCount());
        // Refused: WinUI has no coerce callback, so the changed-callback puts the old value back with a
        // second SetValue — a re-entrant round trip through the projection.
        Step("refused", () => { ed.PageMargin = new PageMargins(-5); return ed.PageMargin; });
        Step("json", () => DocumentSerializer.Deserialize(DocumentSerializer.Serialize(ed.Document!)).PageSetup?.Margin);
        Step("rtf", () => RtfDocumentFormatter.Parse(RtfDocumentFormatter.Write(ed.Document!)).PageSetup?.Margin);
        // The model -> property direction: a loaded document's setup is pushed into the DP.
        Step("load", () =>
        {
            var reopened = new RichEditor { Document = DocumentSerializer.Deserialize(DocumentSerializer.Serialize(ed.Document!)) };
            return reopened.PageMargin;
        });
        // The toolbar reads Target.PageMargin in its sync and matches it against its presets.
        Step("toolbar", () => { _ = new RichEditorToolbar { Target = ed }; ed.PageMargin = new PageMargins(10); return ed.PageMargin; });
        Write(path, lines);
    }

    private static void Write(string path, List<string> lines)
    {
        try { File.WriteAllLines(path, lines); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"pageprobe: {ex.Message}"); }
    }
}
