# Captures a running window to a PNG with PrintWindow(PW_RENDERFULLCONTENT).
#
# The roadmap's GUI-check recipe, as a script instead of a paragraph. WinUI 3 composes its content
# off the normal WM_PRINT path, so a plain PrintWindow returns a blank frame — flag 2
# (PW_RENDERFULLCONTENT) is what makes the Win2D canvas appear.
#
#   powershell -File tools\capture-window.ps1 -ProcessName WinUIRichEditor.Demo -Out shot.png
#
# Known limit (measured): the window cannot be made larger than the work area, and WinUI ignores blind
# SendKeys, so this only ever captures the FIRST screen of the document. Put whatever needs checking
# near the top of the demo sample.
param(
    [Parameter(Mandatory = $true)][string]$ProcessName,
    [Parameter(Mandatory = $true)][string]$Out
)

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class WinCap
{
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }

    public const uint PW_RENDERFULLCONTENT = 2;

    public static void Save(IntPtr hwnd, string path)
    {
        RECT r;
        if (!GetWindowRect(hwnd, out r)) throw new Exception("GetWindowRect failed");
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0) throw new Exception("window has no size");
        using (var bmp = new System.Drawing.Bitmap(w, h))
        {
            using (var g = System.Drawing.Graphics.FromImage(bmp))
            {
                IntPtr hdc = g.GetHdc();
                try { if (!PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)) throw new Exception("PrintWindow failed"); }
                finally { g.ReleaseHdc(hdc); }
            }
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
'@

$proc = Get-Process -Name $ProcessName -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if ($null -eq $proc) { throw "no window found for process '$ProcessName'" }
[WinCap]::Save($proc.MainWindowHandle, $Out)
Write-Output "captured $ProcessName -> $Out"
