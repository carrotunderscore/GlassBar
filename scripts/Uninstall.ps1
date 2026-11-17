# Removes GlassBar and gives the normal Windows taskbar back.
# Right-click Uninstall.ps1 -> "Run with PowerShell"

$dest = Join-Path $env:LOCALAPPDATA 'Programs\GlassBar'
$data = Join-Path $env:LOCALAPPDATA 'GlassBar'

# read what the taskbar looked like before GlassBar hid it
$restoreAutoHide = $null
try {
    $cfg = Get-Content (Join-Path $data 'settings.json') -Raw | ConvertFrom-Json
    if ($cfg.ReplaceTaskbar -and $null -ne $cfg.OriginalAutoHide) { $restoreAutoHide = [bool]$cfg.OriginalAutoHide }
} catch { }

Get-Process GlassBar -ErrorAction SilentlyContinue | Stop-Process -Force

if ($null -ne $restoreAutoHide) {
    Add-Type @'
using System; using System.Runtime.InteropServices;
public static class TB {
  [StructLayout(LayoutKind.Sequential)] struct RECT { public int l, t, r, b; }
  [StructLayout(LayoutKind.Sequential)] struct APPBARDATA { public int cbSize; public IntPtr hWnd; public uint cb; public uint edge; public RECT rc; public IntPtr lParam; }
  [DllImport("shell32.dll")] static extern UIntPtr SHAppBarMessage(uint msg, ref APPBARDATA d);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string c, string t);
  public static void AutoHide(bool on) {
    var d = new APPBARDATA { cbSize = Marshal.SizeOf(typeof(APPBARDATA)), hWnd = FindWindow("Shell_TrayWnd", null), lParam = (IntPtr)(on ? 1 : 2) };
    SHAppBarMessage(10, ref d);
  }
}
'@
    [TB]::AutoHide($restoreAutoHide)
}

Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'ModernGlassBar' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) 'GlassBar.lnk') -ErrorAction SilentlyContinue
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue

$ans = Read-Host 'Also delete settings, chat logins and cached data? (y/n)'
if ($ans -match '^[yYjJ]') { Remove-Item $data -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host 'GlassBar is removed.'
Write-Host 'Press Enter to close.'; [void][Console]::ReadLine()
