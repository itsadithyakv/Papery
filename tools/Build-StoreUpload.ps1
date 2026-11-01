<#
.SYNOPSIS
    Builds the .msixupload file for Partner Center, holding x64 and ARM64.

.DESCRIPTION
    Stops if the add-on's Store ID hasn't been pasted into src/Papery.Core/Licensing/StoreIds.cs, because a
    published build without it couldn't sell Papery Pro. -SkipStoreIdCheck is only for trying the build itself.
    Microsoft signs Store packages, so nothing here is signed.

    Run:  powershell -ExecutionPolicy Bypass -File tools/Build-StoreUpload.ps1
#>
param([switch] $SkipStoreIdCheck)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\Papery.App\Papery.App.csproj'
$out = Join-Path $repo 'dist\store\'

$ids = Get-Content (Join-Path $repo 'src\Papery.Core\Licensing\StoreIds.cs') -Raw
$id = [regex]::Match($ids, 'ProAddOnStoreId = "([^"]*)"').Groups[1].Value
if (-not $SkipStoreIdCheck) {
    if ($id -notmatch '^[0-9A-Z]{12}$') { throw "The add-on's Store ID isn't in StoreIds.cs yet (found '$id')." }
    if ($id -eq '9NK2Q07CQH90') { throw "StoreIds.cs has the app's Store ID. It needs the add-on's: Add-ons > Papery Pro." }
}

Get-Process Papery -ErrorAction SilentlyContinue | Stop-Process -Force
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

dotnet build $project -c Release -p:Platform=x64 -nologo -v q `
    -p:GenerateAppxPackageOnBuild=true -p:UapAppxPackageBuildMode=StoreUpload -p:AppxBundle=Always "-p:AppxBundlePlatforms=x64|ARM64" `
    -p:AppxPackageSigningEnabled=false -p:AppxSymbolPackageEnabled=false "-p:AppxPackageDir=$out" -p:GenerateTestArtifacts=false
if ($LASTEXITCODE -ne 0) { throw 'The Store package failed to build.' }

$upload = Get-ChildItem $out -Recurse -Include *.msixupload, *.appxupload | Select-Object -First 1
if (-not $upload) { throw 'No upload file came out.' }
$final = Join-Path $out $upload.Name
if ($upload.FullName -ne $final) { Copy-Item $upload.FullName $final -Force }
Write-Host ("Done: {0}  {1:N0} MB" -f $final, ($upload.Length / 1MB)) -ForegroundColor Green
