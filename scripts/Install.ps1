# Installs GlassBar for the current user (no admin needed).
# Run from the unzipped release folder:  right-click Install.ps1 -> "Run with PowerShell"

$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path $here 'GlassBar'
$dest = Join-Path $env:LOCALAPPDATA 'Programs\GlassBar'
$exe  = Join-Path $dest 'GlassBar.exe'

if (-not (Test-Path (Join-Path $src 'GlassBar.exe'))) { throw "GlassBar folder not found next to this script ($src)." }

# --- prerequisites: .NET 8 Desktop Runtime + ASP.NET Core Runtime 8 (LocalSend server) ---
$runtimes = ''
try { $runtimes = (& dotnet --list-runtimes 2>$null) -join "`n" } catch { }
if ($runtimes -notmatch 'Microsoft\.WindowsDesktop\.App 8\.') {
    Write-Host 'Installing .NET 8 Desktop Runtime...' -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.DesktopRuntime.8 -e --source winget --accept-package-agreements --accept-source-agreements
}
if ($runtimes -notmatch 'Microsoft\.AspNetCore\.App 8\.') {
    Write-Host 'Installing ASP.NET Core Runtime 8...' -ForegroundColor Yellow
    winget install --id Microsoft.DotNet.AspNetCore.8 -e --source winget --accept-package-agreements --accept-source-agreements
}
# WebView2 (chats, mail, mini player) ships with Windows 10/11 + Edge; install it if missing
$wv = Get-ItemProperty 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue
if (-not $wv) { $wv = Get-ItemProperty 'HKCU:\Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}' -ErrorAction SilentlyContinue }
if (-not $wv) {
    Write-Host 'Installing Microsoft Edge WebView2 Runtime...' -ForegroundColor Yellow
    winget install --id Microsoft.EdgeWebView2Runtime -e --source winget --accept-package-agreements --accept-source-agreements
}

# --- copy the app ---
Get-Process GlassBar -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item (Join-Path $src '*') $dest -Recurse -Force

# --- Start menu shortcut + start with Windows ---
$lnk = Join-Path ([Environment]::GetFolderPath('Programs')) 'GlassBar.lnk'
$sh = New-Object -ComObject WScript.Shell
$s = $sh.CreateShortcut($lnk)
$s.TargetPath = $exe
$s.Description = 'Glass taskbar'
$s.Save()
Set-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' 'ModernGlassBar' "`"$exe`""

Write-Host "`nGlassBar is installed in $dest and starts with Windows." -ForegroundColor Cyan

# --- optional: ffmpeg for the screen recorder ---
if (-not (Get-Command ffmpeg -ErrorAction SilentlyContinue)) {
    $ans = Read-Host "`nThe screen recorder needs ffmpeg. Install it now with winget? (y/n)"
    if ($ans -match '^[yYjJ]') { winget install --id Gyan.FFmpeg -e --source winget --accept-package-agreements --accept-source-agreements }
}

Start-Process $exe
Write-Host "`nGlassBar is running. When Windows asks about network access, allow it (needed for LocalSend)." -ForegroundColor Cyan
Write-Host 'Press Enter to close.'; [void][Console]::ReadLine()
