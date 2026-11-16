# TaskDock installer. Run in PowerShell:
#   irm https://raw.githubusercontent.com/AniketRaut02/TaskDock/master/install.ps1 | iex
#
# Installs the latest release for the current user (no admin rights):
#   - TaskDock.exe            -> %LOCALAPPDATA%\Programs\TaskDock, plus a Start menu shortcut
#   - browser extension       -> %LOCALAPPDATA%\Programs\TaskDock\browser-extension (for "Load unpacked")
#   - VS Code extension       -> only if the `code` command exists and you say yes
# Running it again upgrades in place. Settings, presets and Claude Code hooks are kept.

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Invoke-WebRequest is many times slower with the progress bar
$repo = 'AniketRaut02/TaskDock'
$dir = Join-Path $env:LOCALAPPDATA 'Programs\TaskDock'

$release = Invoke-RestMethod "https://api.github.com/repos/$repo/releases/latest" -Headers @{ 'User-Agent' = 'TaskDock-installer' }
function Get-Asset([string]$pattern, [string]$to) {
    $asset = $release.assets | Where-Object name -like $pattern | Select-Object -First 1
    if (-not $asset) { throw "Release $($release.tag_name) has no file matching '$pattern'." }
    Invoke-WebRequest $asset.browser_download_url -OutFile $to -UseBasicParsing
    return $to
}

Write-Host "Installing TaskDock $($release.tag_name) to $dir" -ForegroundColor Cyan
New-Item -ItemType Directory -Force $dir | Out-Null

# An upgrade can't overwrite the running exe, and TaskDock allows one instance: stop any copy first.
Get-Process TaskDock -ErrorAction SilentlyContinue | Stop-Process -Force
$exe = Get-Asset 'TaskDock.exe' (Join-Path $dir 'TaskDock.exe')
Unblock-File $exe

# Browser extension: unpack to a folder that stays put, since "Load unpacked" keeps pointing at it.
$zip = Get-Asset 'taskdock-browser-extension.zip' (Join-Path $env:TEMP 'taskdock-browser-extension.zip')
$ext = Join-Path $dir 'browser-extension'
if (Test-Path $ext) { Remove-Item -Recurse -Force $ext }
Expand-Archive $zip $ext
Remove-Item $zip

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'TaskDock.lnk'))
$link.TargetPath = $exe
$link.Description = 'Live status of Claude Code, Unity, VS Code, downloads and media'
$link.Save()

$vscode = $false
if (Get-Command code -ErrorAction SilentlyContinue) {
    $answer = Read-Host 'VS Code found. Install the TaskDock VS Code extension too? [Y/n]'
    if ($answer -notmatch '^(n|no)$') {
        $vsix = Get-Asset 'taskdock-*.vsix' (Join-Path $env:TEMP 'taskdock.vsix')
        code --install-extension $vsix --force | Out-Null
        Remove-Item $vsix
        $vscode = $true
    }
}

Start-Process $exe

Write-Host "`nTaskDock is running. Look for its icon in the tray." -ForegroundColor Green
Write-Host @"

Next steps (each one is optional):
  Claude Code   Right-click the tray icon > Connect Claude Code.
  Unity         Window > Package Manager > + > Add package from git URL:
                https://github.com/$repo.git?path=/adapters/unity/com.darkpixelgd.taskdock
  Browser       Open chrome://extensions (or edge:// / brave://), turn on Developer mode,
                click Load unpacked and pick: $ext
"@
if (-not $vscode) { Write-Host "  VS Code       Rerun this installer once the 'code' command is available, or install the .vsix from the release page." }
