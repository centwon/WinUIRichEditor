# Drives the demo through a fixed editing sequence and collects every fault the library reported.
#
# The point is the COMPARISON: run it against the JIT build and against the Native AOT publish and diff
# the two logs. AOT's failures are silent by construction — the 1.1.0 line-metrics defect built, ran and
# rendered, and the only signal was a swallowed exception nobody was listening for. Every swallow site in
# the library reports to RichEditorDiagnostics, and each distinct site reports once, so a log is the SET
# of fallbacks that fired. A site present only in the AOT log is an AOT-only degradation.
#
# Input goes through SendInput (WinUI ignores journal-style injection), and nothing is sent unless the
# demo is genuinely the foreground window.
param(
    [Parameter(Mandatory = $true)][string]$Exe,
    [Parameter(Mandatory = $true)][string]$Log,
    [string]$Page = "control"
)

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Sweep
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] pInputs, int cbSize);
    // Same entry point, mouse-shaped payload. cbSize stays sizeof(INPUT): the union size is what the API
    // strides by, and passing the smaller struct's size makes SendInput read past the second element.
    [DllImport("user32.dll", EntryPoint = "SendInput")] public static extern uint SendInputMouse(uint n, MINPUT[] pInputs, int cbSize);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    // One INPUT union, big enough for the mouse variant — the keyboard fields are a prefix of it, which
    // is why the two padding ints were there to begin with.
    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)]
    public struct MINPUT { public uint type; public MOUSEINPUT mi; }

    const uint INPUT_KEYBOARD = 1;
    const uint INPUT_MOUSE = 0;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;
    const uint MOUSEEVENTF_MOVE = 0x0001;
    const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    const uint MOUSEEVENTF_LEFTUP = 0x0004;
    const uint MOUSEEVENTF_ABSOLUTE = 0x8000;

    static void Send(INPUT[] a) { SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT))); }
    static void SendM(MINPUT[] a) { SendInputMouse((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT))); }

    // Clicks a point inside the window, in client-ish offsets from its top-left. The editor's canvas is
    // what holds keyboard focus, and the demo's plain control page does not focus it on load — without
    // this click every injected keystroke goes to the page, not the editor, and a keyboard-only sweep
    // silently measures a session that received no input at all.
    public static bool ClickIn(IntPtr hWnd, int offsetX, int offsetY)
    {
        RECT r;
        if (!GetWindowRect(hWnd, out r)) return false;
        int x = r.Left + offsetX, y = r.Top + offsetY;
        if (x >= r.Right || y >= r.Bottom) return false;

        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1); // SM_CXSCREEN / SM_CYSCREEN
        int ax = (int)((x * 65535.0) / sw), ay = (int)((y * 65535.0) / sh);
        var move = new MINPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE } };
        var down = new MINPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_ABSOLUTE } };
        var up   = new MINPUT { type = INPUT_MOUSE, mi = new MOUSEINPUT { dx = ax, dy = ay, dwFlags = MOUSEEVENTF_LEFTUP | MOUSEEVENTF_ABSOLUTE } };
        SendM(new[] { move, down, up });
        return true;
    }

    public static void Char(char c)
    {
        var d = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } };
        var u = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } };
        Send(new[] { d, u });
    }

    public static void Vk(ushort vk)
    {
        var d = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
        var u = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } };
        Send(new[] { d, u });
    }

    public static void Chord(ushort mod, ushort vk)
    {
        var md = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = mod } };
        var kd = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
        var ku = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } };
        var mu = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = mod, dwFlags = KEYEVENTF_KEYUP } };
        Send(new[] { md, kd, ku, mu });
    }
}
'@

if (Test-Path $Log) { Remove-Item $Log -Force }
Get-Process -Name "WinUIRichEditor.Demo" -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 400

$proc = Start-Process -FilePath $Exe -ArgumentList "--page=$Page", "--faultlog=$Log" -PassThru
Start-Sleep -Seconds 4
$proc.Refresh()
$h = $proc.MainWindowHandle
if ($h -eq 0) { "ABORT: the demo has no window"; $proc | Stop-Process -Force; exit 1 }

[void][Sweep]::ShowWindow($h, 9)   # SW_RESTORE
[void][Sweep]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 900
if ([Sweep]::GetForegroundWindow() -ne $h) {
    "ABORT: demo is not foreground - refusing to inject keystrokes"
    $proc | Stop-Process -Force
    exit 1
}
"foreground confirmed: $Exe"

$VK_RIGHT = 0x27; $VK_LEFT = 0x25; $VK_DOWN = 0x28; $VK_UP = 0x26
$VK_HOME = 0x24; $VK_END = 0x23; $VK_PRIOR = 0x21; $VK_NEXT = 0x22
$VK_RETURN = 0x0D; $VK_BACK = 0x08; $VK_CONTROL = 0x11

function Beat { Start-Sleep -Milliseconds 160 }

# Click into the body text first: without focus on the editor's canvas the keystrokes below go nowhere,
# and the run still LOOKS fine (it renders, and rendering alone is enough to raise the AOT fault). The
# offset lands in the first body paragraph — above the sample's hyperlink, which a click would open.
"phase 0: click into the editor"
if (-not [Sweep]::ClickIn($h, 120, 265)) { "ABORT: could not place the click"; $proc | Stop-Process -Force; exit 1 }
Start-Sleep -Milliseconds 500

# 1. Vertical movement, the path the AOT line-metrics failure actually broke: crossing a WRAPPED line
#    needs per-line metrics, and without them the caret cannot step inside a paragraph at all.
"phase 1: vertical movement"
1..8 | ForEach-Object { [Sweep]::Vk($VK_DOWN); Beat }
1..8 | ForEach-Object { [Sweep]::Vk($VK_UP); Beat }

# 2. Typing long enough to wrap, then walking back through it.
"phase 2: typing past the wrap"
[Sweep]::Vk($VK_END); Beat
"the quick brown fox jumps over the lazy dog and keeps going well past the wrap point ".ToCharArray() |
    ForEach-Object { [Sweep]::Char($_); Start-Sleep -Milliseconds 12 }
Start-Sleep -Milliseconds 500
1..4 | ForEach-Object { [Sweep]::Vk($VK_UP); Beat }
1..4 | ForEach-Object { [Sweep]::Vk($VK_DOWN); Beat }

# 3. Home/End (line ends, not paragraph ends) and page scrolling — all line-metrics consumers.
"phase 3: line ends and paging"
[Sweep]::Vk($VK_HOME); Beat
[Sweep]::Vk($VK_END); Beat
1..3 | ForEach-Object { [Sweep]::Vk($VK_NEXT); Beat }
1..3 | ForEach-Object { [Sweep]::Vk($VK_PRIOR); Beat }

# 4. Structural edits: split a paragraph, delete back over it, select all.
"phase 4: edits"
[Sweep]::Vk($VK_RETURN); Beat
"second line".ToCharArray() | ForEach-Object { [Sweep]::Char($_); Start-Sleep -Milliseconds 12 }
1..6 | ForEach-Object { [Sweep]::Vk($VK_BACK); Beat }
[Sweep]::Chord($VK_CONTROL, 0x41); Beat        # Ctrl+A
[Sweep]::Vk($VK_RIGHT); Beat
[Sweep]::Chord($VK_CONTROL, 0x5A); Beat        # Ctrl+Z
[Sweep]::Chord($VK_CONTROL, 0x59); Beat        # Ctrl+Y
# 5. Read the document back through the product's own copy path, so the two runs can be compared on
#    RESULT and not just on which fallbacks fired. A silent AOT degradation that still renders would
#    show up here: every character above was typed at a position the caret arrived at by line-metrics
#    navigation, so "the caret did not move" lands the text somewhere else.
#
#    This runs BEFORE Ctrl+F: with a find bar open, Ctrl+A selects the query box instead of the
#    document, and the copy comes back empty — which compares "identical" against another empty run
#    and proves nothing. The read is retried, because a clipboard read can lose the race with the write
#    (and with other processes: the clipboard is shared, and history/sync are on by default).
#    (This overwrites the system clipboard — the same caveat the clipboard test suite carries.)
"phase 5: read the document back"
[Sweep]::Chord($VK_CONTROL, 0x41); Beat        # Ctrl+A
[Sweep]::Chord($VK_CONTROL, 0x43)              # Ctrl+C
$docPath = "$Log.doc.txt"
$doc = ""
1..12 | ForEach-Object {
    if ($doc.Length -eq 0) {
        Start-Sleep -Milliseconds 300
        try { $doc = [string](Get-Clipboard -Raw) } catch { }
    }
}
if ($doc.Length -eq 0) { "WARNING: the document read back EMPTY - the comparison below is void" }
else { "read back $($doc.Length) chars" }
Set-Content -Path $docPath -Value $doc -Encoding utf8

[Sweep]::Chord($VK_CONTROL, 0x46); Beat        # Ctrl+F (find bar / FindRequested) - last, it takes focus
Start-Sleep -Milliseconds 600

Start-Sleep -Seconds 1
$proc | Stop-Process -Force
Start-Sleep -Milliseconds 400

if (Test-Path $Log) {
    "--- faults ---"
    Get-Content $Log
} else {
    "--- no faults reported ---"
}
