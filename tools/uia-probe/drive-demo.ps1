# Drives the demo with REAL input injection (SendInput), not SendKeys — WinUI ignores the journal-style
# path. Refuses to send anything unless the demo is genuinely the foreground window, so stray keystrokes
# cannot land in another application.
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Native
{
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern uint SendInput(uint n, INPUT[] pInputs, int cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct INPUT { public uint type; public KEYBDINPUT ki; public int pad1; public int pad2; }
    [StructLayout(LayoutKind.Sequential)]
    public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

    const uint INPUT_KEYBOARD = 1;
    const uint KEYEVENTF_KEYUP = 0x0002;
    const uint KEYEVENTF_UNICODE = 0x0004;

    static void Send(INPUT[] a) { SendInput((uint)a.Length, a, Marshal.SizeOf(typeof(INPUT))); }

    public static void Char(char c)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE } };
        var up   = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = c, dwFlags = KEYEVENTF_UNICODE | KEYEVENTF_KEYUP } };
        Send(new[] { down, up });
    }

    public static void Vk(ushort vk)
    {
        var down = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk } };
        var up   = new INPUT { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = vk, dwFlags = KEYEVENTF_KEYUP } };
        Send(new[] { down, up });
    }
}
'@

$p = Get-Process -Name "WinUIRichEditor.Demo" -ErrorAction Stop
$h = $p.MainWindowHandle
[void][Native]::ShowWindow($h, 9)          # SW_RESTORE
[void][Native]::SetForegroundWindow($h)
Start-Sleep -Milliseconds 900

if ([Native]::GetForegroundWindow() -ne $h) {
    "ABORT: demo is not foreground - refusing to inject keystrokes"
    exit 1
}
"foreground confirmed: demo"

# Caret moves only -> should raise TextSelectionChanged, never TextChanged.
"phase 1: arrow keys (selection only)"
1..6 | ForEach-Object { [Native]::Vk(0x27); Start-Sleep -Milliseconds 220 }   # VK_RIGHT
1..3 | ForEach-Object { [Native]::Vk(0x28); Start-Sleep -Milliseconds 220 }   # VK_DOWN
Start-Sleep -Milliseconds 700

# Typing -> should raise TextChanged (and selection, since the caret advances).
"phase 2: typing"
foreach ($c in "UIA".ToCharArray()) { [Native]::Char($c); Start-Sleep -Milliseconds 320 }
Start-Sleep -Milliseconds 500

# Backspace the three characters back out so the demo document is left as it was found.
"phase 3: backspace (restoring the document)"
1..3 | ForEach-Object { [Native]::Vk(0x08); Start-Sleep -Milliseconds 320 }   # VK_BACK
"done"
