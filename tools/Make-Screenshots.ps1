<#
.SYNOPSIS
    Makes the Store screenshots by working the real app through accessibility, no mouse or display needed.
    Run after tools/Run-Papery.ps1 has registered a Debug build (Debug unlocks Pro, so no lock icons show).
#>
param([ValidateSet('Dark', 'Light')] [string] $Theme = 'Dark')

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$out = Join-Path $repo 'branding\screenshots'
New-Item -ItemType Directory -Force $out | Out-Null
$files = Join-Path $repo 'PaperyTestFiles'
$drive = Join-Path $PSScriptRoot 'Drive-Papery.ps1'
$size = 'size:1800x1125'

function Stop-App { Get-Process Papery -ErrorAction SilentlyContinue | Stop-Process -Force; Start-Sleep -Seconds 2 }

function Start-App([string[]] $arguments) {
    Stop-App
    if ($arguments) { Start-Process papery.exe -ArgumentList $arguments } else { Start-Process papery.exe }
    Start-Sleep -Seconds 6
}

function Drive([string[]] $steps) {
    $ErrorActionPreference = 'Continue' # one missed step shouldn't lose the whole set
    & powershell -NoProfile -ExecutionPolicy Bypass -File $drive @steps 2>&1 | Where-Object { "$_" -match 'Nothing named|Exception|Saved' } | ForEach-Object { "  $_" }
}

function Shot([string] $name) { return "shot:$(Join-Path $out "$name.png")" }

# The app's theme comes from its settings file, so set it there and put it back afterwards.
$settingsFile = Join-Path $env:LOCALAPPDATA 'Packages\Papery.Dev_vnfd10t67y7fa\LocalState\settings.json'
Stop-App
$settingsBefore = Get-Content $settingsFile -Raw

# Clearing Recent for the shot lets the tools show, so everything in the app's folder is put back afterwards.
$stateFolder = Split-Path $settingsFile
$stateBackup = New-Item -ItemType Directory -Force (Join-Path $env:TEMP 'papery-shots-state')
Copy-Item (Join-Path $stateFolder '*.json') $stateBackup -Force
$settings = $settingsBefore | ConvertFrom-Json
$settings.Theme = $Theme
$settings | ConvertTo-Json -Depth 8 | Set-Content $settingsFile -Encoding utf8

# Work on copies, so the test files stay as they are.
$scratch = New-Item -ItemType Directory -Force (Join-Path $env:TEMP 'papery-shots')
Copy-Item (Join-Path $files '*.pdf') $scratch -Force
$q = { param($n) '"' + (Join-Path $scratch $n) + '"' }

Write-Host 'Home'
Start-App @()
Drive @($size, 'wait:2', 'size:1790x1120', 'wait:1', $size, 'wait:2', 'click:Clear', 'wait:3', (Shot "01-home-tools-$Theme"))

Write-Host 'Reading'
Start-App @((& $q 'bookmarks-and-links.pdf'))
Drive @($size, 'wait:2', 'size:1790x1120', 'wait:1', $size, 'wait:4', (Shot "02-reading-$Theme"))

Write-Host 'Organise'
Drive @('select:Organise', 'wait:4', (Shot "03-organise-$Theme"))

Write-Host 'Fill and sign'
Start-App @((& $q 'forms-interactive.pdf'))
Drive @($size, 'wait:1', 'select:Fill and Sign', 'wait:3', 'click:Sign', 'wait:2', 'click:New signature', 'wait:3', 'select:Type', 'wait:1', 'type:Your name=Alex Morgan', 'wait:2', (Shot "04-fill-and-sign-$Theme"))

Write-Host 'Compress'
Start-App @((& $q 'scanned-document.pdf'))
Drive @($size, 'wait:1', 'select:Tools', 'wait:2', 'click:Compress', 'wait:10', (Shot "05-compress-$Theme"))

Write-Host 'Merge'
$set = Get-ChildItem (Join-Path $files 'merge-set') -Filter *.pdf | Select-Object -First 5 | ForEach-Object { '"' + $_.FullName + '"' }
Start-App (@('--merge') + $set)
Drive @($size, 'wait:3', (Shot "06-merge-$Theme"))

Write-Host 'Redact'
Start-App @((& $q 'redaction-test.pdf'))
Drive @($size, 'wait:1', 'select:Protect', 'wait:2', 'click:Find what to remove', 'wait:2', 'toggle:Email addresses', 'toggle:Phone numbers', 'click:Mark them', 'wait:4', (Shot "07-redact-$Theme"))

Write-Host 'Annotate'
Start-App @((& $q 'bookmarks-and-links.pdf'))
Drive @($size, 'wait:1', 'select:Annotate', 'wait:3', (Shot "08-annotate-$Theme"))

Stop-App
Copy-Item (Join-Path $stateBackup '*.json') $stateFolder -Force
Remove-Item $stateBackup -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $scratch -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Done: $out" -ForegroundColor Green
