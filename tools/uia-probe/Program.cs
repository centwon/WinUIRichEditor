using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Automation;

// A minimal UI Automation CLIENT — what Narrator/NVDA are, reduced to the part this verification needs.
//
// Standalone on purpose: NOT in WinUIRichEditor.slnx, so it never builds in CI. Run it by hand.
//
//   1. start the demo
//   2. dotnet run --project tools/uia-probe -- 22          (subscribes, then listens for 22s)
//   3. in another shell: pwsh tools/uia-probe/drive-demo.ps1  (real SendInput keystrokes)
//
// Narrator is the wrong instrument for this check even though it is the "real" screen reader: it speaks
// and keeps no log, so the result cannot be read back. This client answers the same question in text.
//
// The claim under test: RichEditorAutomationPeer implements ITextProvider but never raised
// TextPatternOnTextChanged / TextPatternOnTextSelectionChanged, so a screen reader could not follow the
// caret. The fix raises both from RaiseStatusChanged, behind AutomationPeer.ListenerExists.
//
// ListenerExists is the reason a client is REQUIRED here and Narrator-by-ear is not enough: with no UIA
// client attached the guarded code never runs at all, so "it works on my machine with no screen reader"
// is not evidence of anything. Subscribing below is what flips ListenerExists to true.
internal static class Program
{
    private static int _textChanged;
    private static int _selectionChanged;

    private static void Main(string[] args)
    {
        int seconds = args.Length > 0 && int.TryParse(args[0], out var s) ? s : 60;

        var proc = FindDemo();
        if (proc == null) { Console.WriteLine("FAIL: WinUIRichEditor.Demo is not running"); return; }
        Console.WriteLine($"demo pid={proc.Id} hwnd=0x{proc.MainWindowHandle.ToInt64():X}");

        var window = AutomationElement.FromHandle(proc.MainWindowHandle);
        if (window == null) { Console.WriteLine("FAIL: no automation element for the window"); return; }

        // The editor exposes itself as an Edit control named RichEditor (GetClassNameCore).
        var editor = window.FindFirst(TreeScope.Descendants,
            new PropertyCondition(AutomationElement.ClassNameProperty, "RichEditor"));
        if (editor == null)
        {
            Console.WriteLine("FAIL: no element with ClassName=RichEditor under the window");
            Dump(window, 0, 3);
            return;
        }

        Console.WriteLine($"editor found: name=\"{Safe(() => editor.Current.Name)}\" " +
                          $"type={Safe(() => editor.Current.ControlType.ProgrammaticName)} " +
                          $"isContent={Safe(() => editor.Current.IsContentElement.ToString())}");

        // Does it actually offer TextPattern? ITextProvider was implemented before this change; if this
        // says false, the peer is not reachable and the event question is moot.
        bool hasText = editor.TryGetCurrentPattern(TextPattern.Pattern, out var textObj);
        Console.WriteLine($"TextPattern supported: {hasText}");
        if (hasText && textObj is TextPattern tp)
        {
            Console.WriteLine($"  DocumentRange text: \"{Trim(Safe(() => tp.DocumentRange.GetText(60)))}\"");
            var sel = Safe(() => tp.GetSelection()?.Length.ToString()) ?? "?";
            Console.WriteLine($"  GetSelection() ranges: {sel}");
        }

        Automation.AddAutomationEventHandler(
            TextPattern.TextChangedEvent, editor, TreeScope.Element,
            (_, _) => { Interlocked.Increment(ref _textChanged); Log("TextChanged"); });

        Automation.AddAutomationEventHandler(
            TextPattern.TextSelectionChangedEvent, editor, TreeScope.Element,
            (_, _) => { Interlocked.Increment(ref _selectionChanged); Log("TextSelectionChanged"); });

        Console.WriteLine($"subscribed — ListenerExists is now true in the editor process.");
        Console.WriteLine($"drive the editor for {seconds}s (type text, move the caret with arrows)...");
        Console.Out.Flush();

        Thread.Sleep(seconds * 1000);

        Automation.RemoveAllEventHandlers();
        Console.WriteLine();
        Console.WriteLine($"RESULT  TextChanged={_textChanged}  TextSelectionChanged={_selectionChanged}");
        Console.WriteLine(_textChanged + _selectionChanged > 0
            ? "PASS: the editor notified a real UIA client."
            : "FAIL: no notifications arrived.");
    }

    private static Process FindDemo()
    {
        foreach (var p in Process.GetProcessesByName("WinUIRichEditor.Demo"))
            if (p.MainWindowHandle != IntPtr.Zero) return p;
        return null;
    }

    private static void Log(string what)
        => Console.WriteLine($"  [{DateTime.Now:HH:mm:ss.fff}] {what}");

    private static string Safe(Func<string> f) { try { return f(); } catch (Exception e) { return "<" + e.GetType().Name + ">"; } }
    private static string Trim(string s) => s == null ? "" : s.Replace("\r", " ").Replace("\n", " ");

    private static void Dump(AutomationElement e, int depth, int max)
    {
        if (depth > max || e == null) return;
        Console.WriteLine(new string(' ', depth * 2) + $"- {Safe(() => e.Current.ControlType.ProgrammaticName)} " +
                          $"cls=\"{Safe(() => e.Current.ClassName)}\" name=\"{Safe(() => e.Current.Name)}\"");
        var child = TreeWalker.ControlViewWalker.GetFirstChild(e);
        while (child != null) { Dump(child, depth + 1, max); child = TreeWalker.ControlViewWalker.GetNextSibling(child); }
    }
}
