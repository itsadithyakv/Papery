<#
.SYNOPSIS
    Installs a test MSIX by hand. Right-click this file, choose "Run with PowerShell", and approve the admin prompt.
    It picks the package that matches this PC. On x64 PCs, Papery-Setup-x64.exe does the same in one double-click.
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    return
}

$platform = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'ARM64' } else { 'x64' }
Import-Certificate -FilePath (Join-Path $here 'Papery-Test.cer') -CertStoreLocation 'Cert:\LocalMachine\TrustedPeople' | Out-Null
Get-AppxPackage Papery.Dev | Remove-AppxPackage
Add-AppxPackage -Path (Join-Path $here "Papery_$platform.msix") -DependencyPath (Join-Path $here "WindowsAppRuntime_$platform.msix") -ForceApplicationShutdown
Write-Host 'Papery is installed. Find it in the Start menu.' -ForegroundColor Green
Start-Sleep -Seconds 3
