# Measures the editor's caret blink from OUTSIDE the process, by capturing the demo window repeatedly
# (PrintWindow + PW_RENDERFULLCONTENT) and timing how often the pixels change.
#
# Why this exists: the caret blink rate and double-click interval come from the OS
# (SystemInputSettings -> GetCaretBlinkTime / GetDoubleClickTime), and on a machine sitting at the
# Windows defaults (530 / 500) a correct implementation is INDISTINGUISHABLE from the hard-coded
# constants it replaced. Verifying it means changing the OS setting and watching -- which is not
# reproducible and not something a reviewer can re-run. This turns it into a number.
#
#   blink ON  -> gap between changes ~= HKCU\Control Panel\Desktop\CursorBlinkRate
#   blink OFF -> changes = 0, AND the caret must remain VISIBLE on screen (the accessibility setting
#                turns off MOTION, not the caret; a caret that vanishes is a defect)
#
# !! A result of 0 changes proves nothing on its own -- an unfocused editor draws no caret and also
#    scores 0. ALWAYS run the control first, at the normal blink rate, and confirm it reports a gap
#    near CursorBlinkRate. Only a 0 that follows a good control run is evidence.
#
# Procedure:
#   1. Start the demo:  WinUIRichEditor.Demo.exe --page=toolbar     (see Project_Roadmap.md for the
#      build-order trap -- build the slnx first, the demo consumes the AnyCPU library output)
#   2. Click once in the editor body so a caret appears.
#   3. Run this. Expect "average gap" near CursorBlinkRate. <- the control
#   4. `control keyboard`, drag "cursor blink rate" to None, OK.
#   5. Click outside the editor and back in (the setting is re-read on FOCUS GAIN, not on a message).
#   6. Run this again. Expect changes=0, and check by eye that the caret is still drawn.
#   7. Restore the slider.
#
# Don't cover the window or move the mouse over it while sampling -- hover effects count as changes.
# ASCII-only output on purpose: Windows PowerShell 5.1 reads .ps1 as the ANSI codepage.
param(
    [int]$Seconds = 6,
    [int]$IntervalMs = 60
)

$src = @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
public class CaretBlinkProbe {
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  // Weighted byte sum over the whole window. A caret is only a few pixels, so the channels are
  // weighted differently to keep two frames from colliding on an equal sum.
  public static long Signature(IntPtr h) {
    RECT r; GetWindowRect(h, out r);
    int w = r.R - r.L, ht = r.B - r.T;
    if (w <= 0 || ht <= 0) return 0;
    using (var bmp = new Bitmap(w, ht))
    using (var g = Graphics.FromImage(bmp)) {
      IntPtr hdc = g.GetHdc();
      PrintWindow(h, hdc, 2); // PW_RENDERFULLCONTENT -- required, Win2D content is composited
      g.ReleaseHdc(hdc);
      var data = bmp.LockBits(new Rectangle(0, 0, w, ht), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      int bytes = data.Stride * ht;
      byte[] buf = new byte[bytes];
      Marshal.Copy(data.Scan0, buf, 0, bytes);
      bmp.UnlockBits(data);
      long sum = 0;
      for (int i = 0; i < bytes; i += 4) sum += buf[i] + buf[i + 1] * 3L + buf[i + 2] * 7L;
      return sum;
    }
  }
}
'@
Add-Type -TypeDefinition $src -ReferencedAssemblies System.Drawing, System.Drawing.Primitives, System.Runtime.InteropServices

$proc = Get-Process -Name "WinUIRichEditor.Demo" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $proc) { Write-Host "Demo is not running." -ForegroundColor Red; exit 1 }
$hwnd = $proc.MainWindowHandle

$rate = (Get-ItemProperty 'HKCU:\Control Panel\Desktop' -Name CursorBlinkRate -ErrorAction SilentlyContinue).CursorBlinkRate
Write-Host "OS CursorBlinkRate = $rate   (-1 means blinking is OFF)" -ForegroundColor Cyan
Write-Host "Sampling for $Seconds s every $IntervalMs ms. The editor must have focus + a caret." -ForegroundColor Cyan

$sw = [Diagnostics.Stopwatch]::StartNew()
$samples = New-Object System.Collections.Generic.List[object]
while ($sw.ElapsedMilliseconds -lt ($Seconds * 1000)) {
    $samples.Add([pscustomobject]@{ T = $sw.ElapsedMilliseconds; Sig = [CaretBlinkProbe]::Signature($hwnd) })
    Start-Sleep -Milliseconds $IntervalMs
}

$changes = @()
for ($i = 1; $i -lt $samples.Count; $i++) {
    if ($samples[$i].Sig -ne $samples[$i - 1].Sig) { $changes += $samples[$i].T }
}

Write-Host ""
Write-Host "frames=$($samples.Count)  changes=$($changes.Count)" -ForegroundColor Yellow
if ($changes.Count -eq 0) {
    Write-Host "RESULT: nothing changed on screen -> caret is NOT blinking." -ForegroundColor Green
    Write-Host "        Only meaningful after a control run showed a gap near CursorBlinkRate." -ForegroundColor DarkGray
} else {
    $gaps = @()
    for ($i = 1; $i -lt $changes.Count; $i++) { $gaps += ($changes[$i] - $changes[$i - 1]) }
    if ($gaps.Count -gt 0) {
        $avg = [math]::Round(($gaps | Measure-Object -Average).Average)
        Write-Host "RESULT: average gap between changes = $avg ms" -ForegroundColor Green
        Write-Host "        gaps: $($gaps -join ', ')" -ForegroundColor DarkGray
        Write-Host "        expect this near CursorBlinkRate ($rate)." -ForegroundColor Green
    }
}
