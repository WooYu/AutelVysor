param(
    [switch]$SkipToolDownload,
    [switch]$SkipInstaller
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/AutelVysor/AutelVysor.csproj'
$projectText = Get-Content -Raw $project
$versionMatch = [regex]::Match($projectText, '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { throw 'Unable to read <Version> from AutelVysor.csproj.' }
$version = $versionMatch.Groups[1].Value

& (Join-Path $PSScriptRoot 'build-windows.ps1') -SkipToolDownload:$SkipToolDownload

$dist = Join-Path $root 'dist'
$app = Join-Path $dist 'AutelVysor'
& (Join-Path $PSScriptRoot 'sign-release.ps1') -FilePath (Join-Path $app 'AutelVysor.exe')
$zip = Join-Path $dist "AutelVysor-v$version-windows-x64.zip"
if (Test-Path $zip) { Remove-Item -Force $zip }
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable ZIP: $zip"

if ($SkipInstaller) { exit 0 }

$isccCandidates = @(
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
    (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
) | Where-Object { $_ -and (Test-Path $_) }

$iscc = $isccCandidates | Select-Object -First 1
if (-not $iscc) {
    Write-Warning 'Inno Setup 6 was not found. Portable ZIP was created; installer was skipped.'
    Write-Warning 'Install Inno Setup 6 and rerun scripts/build-release.ps1 to create AutelVysor-Setup.exe.'
    exit 0
}

& $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer/AutelVysor.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
$setup = Join-Path $dist 'AutelVysor-Setup.exe'
if (-not (Test-Path $setup)) { throw 'Inno Setup completed without AutelVysor-Setup.exe.' }
& (Join-Path $PSScriptRoot 'sign-release.ps1') -FilePath $setup

$setupInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
$setupDescription = [string]$setupInfo.FileDescription
$setupProduct = [string]$setupInfo.ProductName
$setupDescription = $setupDescription.Trim()
$setupProduct = $setupProduct.Trim()
if ($setupDescription -ne 'Autel') {
    throw "Installer FileDescription is '$setupDescription', expected 'Autel'."
}
if ($setupProduct -ne 'Autel Vysor') {
    throw "Installer ProductName is '$setupProduct', expected 'Autel Vysor'."
}
Write-Host "Installer: $setup"
Write-Host "Installer FileDescription: $setupDescription"
Write-Host "Installer ProductName: $setupProduct"
