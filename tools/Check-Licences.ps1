<#
.SYNOPSIS
    Fails if any NuGet package used by Papery (directly or indirectly) has a licence
    that is not on the permissive allow-list.

.DESCRIPTION
    Papery is sold closed-source, so GPL/AGPL/LGPL code must never get in.
    This reads every obj/project.assets.json after a restore, looks up each package's
    licence in its .nuspec, and prints a table. Exit code 1 means a problem was found.

    Run after "dotnet restore":   pwsh tools/Check-Licences.ps1
#>
$ErrorActionPreference = 'Stop'

$allowed = @('MIT', 'Apache-2.0', 'BSD-2-Clause', 'BSD-3-Clause', 'MS-PL', 'Zlib', 'ISC')

# Microsoft's own runtime/SDK packages use a licence URL instead of an expression.
# They are the .NET platform itself (MIT) or Microsoft redistributables meant for app use.
$microsoftPrefixes = @('Microsoft.', 'System.', 'runtime.', 'NETStandard.')

$repo = Split-Path $PSScriptRoot -Parent
$packagesRoot = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget\packages' }

$packages = @{}
Get-ChildItem $repo -Recurse -Filter project.assets.json | ForEach-Object {
    $assets = Get-Content $_.FullName -Raw | ConvertFrom-Json
    foreach ($lib in $assets.libraries.PSObject.Properties) {
        if ($lib.Value.type -eq 'package') { $packages[$lib.Name] = $true }
    }
}

$problems = 0
$rows = foreach ($key in ($packages.Keys | Sort-Object)) {
    $name, $version = $key -split '/'
    $nuspec = Get-ChildItem (Join-Path $packagesRoot "$($name.ToLowerInvariant())\$version") -Filter *.nuspec -ErrorAction SilentlyContinue | Select-Object -First 1
    $licence = '(unknown)'
    if ($nuspec) {
        [xml]$xml = Get-Content $nuspec.FullName -Raw
        $meta = $xml.package.metadata
        if ($meta.license -and $meta.license.type -eq 'expression') { $licence = $meta.license.'#text' }
        elseif ($meta.licenseUrl) { $licence = $meta.licenseUrl }
    }

    $isMicrosoft = $microsoftPrefixes | Where-Object { $name.StartsWith($_, 'OrdinalIgnoreCase') }
    $ok = ($allowed -contains $licence) -or ($isMicrosoft -and $licence -notmatch 'GPL')
    if (-not $ok) { $problems++ }
    [pscustomobject]@{ Package = $name; Version = $version; Licence = $licence; OK = $(if ($ok) { 'yes' } else { 'NO' }) }
}

$rows | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
if ($problems -gt 0) {
    Write-Host "$problems package(s) are not on the permissive allow-list. Do not ship until fixed." -ForegroundColor Red
    exit 1
}
Write-Host 'All package licences are permissive.' -ForegroundColor Green
