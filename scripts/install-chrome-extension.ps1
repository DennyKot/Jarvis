<#
.SYNOPSIS
  Installs the Jarvis Bridge extension into your running Google Chrome.
.DESCRIPTION
  Starts Jarvis with --install-extension. Jarvis opens chrome://extensions, turns on Developer mode,
  clicks "Load unpacked" and selects %LOCALAPPDATA%\Jarvis\ChromeExtension\unpacked.
  Set Jarvis:Browser:Mode to "Extension" to make Jarvis use it.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Get-ChildItem -Path (Join-Path $root 'src\Jarvis.App\bin') -Recurse -Filter Jarvis.exe -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1

if (Get-Process Jarvis -ErrorAction SilentlyContinue) {
    Write-Host 'Jarvis is already running: right-click the robot and choose "Install Chrome extension".'
    exit 0
}

if ($exe) {
    Start-Process $exe.FullName -ArgumentList '--install-extension'
} else {
    Start-Process dotnet -ArgumentList 'run', '--project', (Join-Path $root 'src\Jarvis.App'), '--', '--install-extension'
}

Write-Host 'Jarvis is installing the extension in Chrome. Please do not use the mouse or keyboard for a few seconds.'
