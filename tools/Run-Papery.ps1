<#
.SYNOPSIS
    Builds Papery, registers it with Windows from the build folder, and starts it.

.DESCRIPTION
    This is what Visual Studio's F5 does for a packaged (MSIX) app, done from the command line.
    Needs Windows "Developer Mode" on (Settings > System > For developers).

    powershell -ExecutionPolicy Bypass -File tools/Run-Papery.ps1                 # Debug, x64
    powershell -ExecutionPolicy Bypass -File tools/Run-Papery.ps1 -Configuration Release
    powershell -ExecutionPolicy Bypass -File tools/Run-Papery.ps1 -NoLaunch       # just build + register
    powershell -ExecutionPolicy Bypass -File tools/Run-Papery.ps1 -File "C:\x.pdf"
#>
param(
    [ValidateSet('Debug', 'Release')] [string]$Configuration = 'Debug',
    [ValidateSet('x64', 'ARM64')] [string]$Platform = $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }),
    [switch]$NoLaunch,
    [string]$File
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\Papery.App\Papery.App.csproj'

# Close a running copy first: it locks Papery.exe, and the new build must be registered.
Get-Process Papery -ErrorAction SilentlyContinue | Stop-Process -Force

Write-Host "Building Papery ($Configuration|$Platform)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration -p:Platform=$Platform -nologo -v q
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$rid = "win-$($Platform.ToLowerInvariant())"
$manifest = Get-ChildItem (Join-Path $repo "src\Papery.App\bin\$Platform\$Configuration") -Recurse -Filter AppxManifest.xml |
    Where-Object { $_.FullName -match [regex]::Escape($rid) -and $_.FullName -notmatch '\\publish\\' } |
    Sort-Object LastWriteTime -Descending | Select-Object -First 1
if (-not $manifest) { throw 'AppxManifest.xml not found in the build output.' }

Write-Host "Registering $($manifest.Directory.FullName)" -ForegroundColor Cyan
try {
    Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion -ErrorAction Stop
} catch {
    # Windows refuses to re-register a dev package whose manifest changed (0x80073CFB).
    # Remove it, keeping settings and recent files, then register again (as Visual Studio does).
    Write-Host 'Manifest changed: re-registering the development package...' -ForegroundColor Yellow
    Get-AppxPackage -Name 'Papery.Dev' | Remove-AppxPackage -PreserveApplicationData
    Add-AppxPackage -Register $manifest.FullName -ForceUpdateFromAnyVersion
}

if (-not $NoLaunch) {
    $pkg = Get-AppxPackage -Name 'Papery.Dev' | Select-Object -First 1
    $aumid = "$($pkg.PackageFamilyName)!App"
    if ($File) {
        # The execution alias (see Package.appxmanifest) launches with package identity;
        # running Papery.exe from the bin folder directly would not.
        Start-Process (Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\papery.exe') -ArgumentList "`"$File`""
    } else {
        Start-Process "shell:AppsFolder\$aumid"
    }
    Write-Host "Started $aumid" -ForegroundColor Green
}
