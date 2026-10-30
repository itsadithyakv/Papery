<#
.SYNOPSIS
    Builds everything you need to install and test Papery yourself: signed MSIX packages and an EXE installer.

.DESCRIPTION
    Output goes to dist/:
      Papery-Setup-x64.exe     double-click installer (asks for admin once, to trust the test certificate)
      Papery_x64.msix          the x64 package, signed with a test certificate
      Papery_ARM64.msix        the ARM64 package, signed with a test certificate
      Papery-Test.cer          that certificate's public half
      Install-Papery.ps1       installs an MSIX by hand (use this on ARM64 PCs)

    These are for testing on your own PCs. The Store doesn't use them: for the Store, associate the app
    with your reserved name and build the upload package (see docs/STORE-SETUP.md). Microsoft signs Store packages itself.

    Run:  powershell -ExecutionPolicy Bypass -File tools/Build-Release.ps1
#>
param(
    [string[]] $Platforms = @('x64', 'ARM64'),
    [switch] $SkipSetup
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'src\Papery.App\Papery.App.csproj'
$dist = Join-Path $repo 'dist'
New-Item -ItemType Directory -Force $dist | Out-Null

# ---- a test certificate, whose subject must match the manifest's Publisher ----
[xml] $manifest = Get-Content (Join-Path $repo 'src\Papery.App\Package.appxmanifest')
$publisher = $manifest.Package.Identity.Publisher
$pfx = Join-Path $dist 'Papery-Test.pfx'
$cer = Join-Path $dist 'Papery-Test.cer'
$password = 'papery-test-only'
if (-not (Test-Path $pfx)) {
    Write-Host "Making a test certificate for $publisher..." -ForegroundColor Cyan
    $cert = New-SelfSignedCertificate -Type Custom -Subject $publisher -KeyUsage DigitalSignature -FriendlyName 'Papery test signing' `
        -CertStoreLocation 'Cert:\CurrentUser\My' -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
    $secure = ConvertTo-SecureString $password -AsPlainText -Force
    Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $secure | Out-Null
    Export-Certificate -Cert $cert -FilePath $cer | Out-Null
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)"
}

$signtool = Get-ChildItem (Join-Path $env:USERPROFILE '.nuget\packages\microsoft.windows.sdk.buildtools') -Recurse -Filter signtool.exe | Where-Object { $_.FullName -match '\\x64\\' } | Sort-Object FullName | Select-Object -Last 1 -ExpandProperty FullName

# ---- the packages ----
Get-Process Papery -ErrorAction SilentlyContinue | Stop-Process -Force
foreach ($platform in $Platforms) {
    Write-Host "Packaging Papery for $platform..." -ForegroundColor Cyan
    $out = Join-Path $dist "package-$platform\"
    if (Test-Path $out) { Remove-Item $out -Recurse -Force }
    dotnet build $project -c Release "-p:Platform=$platform" -nologo -v q `
        -p:GenerateAppxPackageOnBuild=true -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly `
        -p:AppxPackageSigningEnabled=true "-p:PackageCertificateKeyFile=$pfx" "-p:PackageCertificatePassword=$password" `
        "-p:AppxPackageDir=$out" -p:GenerateTestArtifacts=false
    if ($LASTEXITCODE -ne 0) { throw "Packaging for $platform failed." }
    $msix = Get-ChildItem $out -Recurse -Filter *.msix | Where-Object { $_.FullName -notmatch 'Dependencies' } | Select-Object -First 1
    if (-not $msix) { throw "No package came out for $platform." }
    $final = Join-Path $dist "Papery_$platform.msix"
    Copy-Item $msix.FullName $final -Force
    & $signtool sign /fd SHA256 /f $pfx /p $password $final | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Signing for $platform failed." }
    $runtime = Get-ChildItem $out -Recurse -Filter 'Microsoft.WindowsAppRuntime*.msix' | Where-Object { $_.DirectoryName -match "\\$platform$" } | Select-Object -First 1
    Copy-Item $runtime.FullName (Join-Path $dist "WindowsAppRuntime_$platform.msix") -Force
    Write-Host ("  Papery_{0}.msix  {1:N0} MB" -f $platform, ($msix.Length / 1MB))
}

# ---- the installer ----
if (-not $SkipSetup) {
    Write-Host 'Building the EXE installer...' -ForegroundColor Cyan
    dotnet publish (Join-Path $repo 'tools\Papery.Setup\Papery.Setup.csproj') -c Release -r win-x64 -nologo -v q `
        "-p:PaperyMsix=$(Join-Path $dist 'Papery_x64.msix')" "-p:PaperyCer=$cer" "-p:PaperyRuntime=$(Join-Path $dist 'WindowsAppRuntime_x64.msix')" -o (Join-Path $dist 'setup')
    if ($LASTEXITCODE -ne 0) { throw 'The installer failed to build.' }
    Copy-Item (Join-Path $dist 'setup\PaperySetup.exe') (Join-Path $dist 'Papery-Setup-x64.exe') -Force
    Copy-Item (Join-Path $PSScriptRoot 'Install-Papery.ps1') $dist -Force
    Write-Host ("  Papery-Setup-x64.exe  {0:N0} MB" -f ((Get-Item (Join-Path $dist 'Papery-Setup-x64.exe')).Length / 1MB))
}

Write-Host "Done. Everything is in $dist" -ForegroundColor Green
