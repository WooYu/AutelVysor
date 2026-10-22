param(
    [switch]$SkipToolDownload,
    [switch]$SkipInstaller,
    [switch]$RequireSigning,
    [switch]$AllowUnsignedInternal
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-signing.ps1')
if ($RequireSigning -and $AllowUnsignedInternal) { throw 'Choose exactly one signing mode.' }
if (-not $RequireSigning -and -not $AllowUnsignedInternal) { throw 'Specify -RequireSigning or -AllowUnsignedInternal explicitly.' }
if ($RequireSigning -and $SkipInstaller) { throw 'A signed release requires both portable ZIP and installer.' }
# This preflight intentionally precedes build, archive creation, installer and hash calculation.
if ($RequireSigning) { Assert-ReleaseSigningConfiguration }
if ($AllowUnsignedInternal) { Write-Warning 'UNSIGNED release: publisher trust is not verified. Publication requires manifest, hash and source verification.' }
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'src/AutelVysor/AutelVysor.csproj'
$versionMatch = [regex]::Match((Get-Content -Raw $project), '<Version>([^<]+)</Version>')
if (-not $versionMatch.Success) { throw 'Unable to read <Version> from AutelVysor.csproj.' }
$version = $versionMatch.Groups[1].Value
$dist = Join-Path $root 'dist'
$manifestPath = Join-Path $dist 'release-manifest.json'
# Never leave a previous release manifest to attest to a failed/rebuilt candidate.
Remove-Item -LiteralPath $manifestPath -Force -ErrorAction SilentlyContinue
& (Join-Path $PSScriptRoot 'build-windows.ps1') -SkipToolDownload:$SkipToolDownload
$app = Join-Path $dist 'AutelVysor'
$exe = Join-Path $app 'AutelVysor.exe'
& (Join-Path $PSScriptRoot 'sign-release.ps1') -FilePath $exe -RequireSigning:$RequireSigning -AllowUnsignedInternal:$AllowUnsignedInternal
$zip = Join-Path $dist "AutelVysor-v$version-windows-x64.zip"
if (Test-Path -LiteralPath $zip) { Remove-Item -LiteralPath $zip -Force }
# Signing MUST occur before archive creation. Later verification compares the exact signed bytes.
Compress-Archive -Path (Join-Path $app '*') -DestinationPath $zip -CompressionLevel Optimal
Write-Host "Portable ZIP: $zip"
$setup = Join-Path $dist 'AutelVysor-Setup.exe'
Remove-Item -LiteralPath $setup -Force -ErrorAction SilentlyContinue
if (-not $SkipInstaller) {
    $isccCandidates = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6/ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6/ISCC.exe')
    ) | Where-Object { $_ -and (Test-Path $_) }
    $iscc = $isccCandidates | Select-Object -First 1
    if (-not $iscc) { throw 'Inno Setup 6 missing. Install Inno Setup or explicitly use -AllowUnsignedInternal -SkipInstaller.' }
    & $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer/AutelVysor.iss')
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed with exit code $LASTEXITCODE." }
    if (-not (Test-Path -LiteralPath $setup)) { throw 'Inno Setup completed without AutelVysor-Setup.exe.' }
    & (Join-Path $PSScriptRoot 'sign-release.ps1') -FilePath $setup -RequireSigning:$RequireSigning -AllowUnsignedInternal:$AllowUnsignedInternal
    $setupInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($setup)
    # Inno Setup pads version-resource strings; retain the original normalized check.
    $setupDescription = ([string]$setupInfo.FileDescription).Trim()
    $setupProduct = ([string]$setupInfo.ProductName).Trim()
    if ($setupDescription -ne 'Autel') { throw "Installer FileDescription is '$setupDescription', expected 'Autel'." }
    if ($setupProduct -ne 'Autel Vysor') { throw "Installer ProductName is '$setupProduct', expected 'Autel Vysor'." }
    Write-Host "Installer FileDescription: $setupDescription"
    Write-Host "Installer ProductName: $setupProduct"
}
$paths = @('AutelVysor/AutelVysor.exe', "AutelVysor-v$version-windows-x64.zip")
if (-not $SkipInstaller) { $paths += 'AutelVysor-Setup.exe' }
# Every hash is calculated ONLY after all signing and timestamping have completed.
Assert-ReleaseZipExecutable -ZipPath $zip -ExecutablePath $exe
$records = @($paths | ForEach-Object {
    $path = Join-Path $dist $_
    $record = [ordered]@{ path = $_; size = (Get-Item -LiteralPath $path).Length; sha256 = (Get-ReleaseHash $path) }
    if ($RequireSigning -and $_.EndsWith('.exe')) { $record.signature = Get-VerifiedReleaseSignature -FilePath $path -ExpectedThumbprint (Get-ReleasePublisherThumbprint) }
    $record
})
$commit = $env:GITHUB_SHA
if (-not $commit) { $commit = (& git -C $root rev-parse HEAD) }
$manifest = [ordered]@{
    schemaVersion = 1
    version = $version
    sourceCommit = [string]$commit
    sourceRunId = [string]$env:GITHUB_RUN_ID
    releaseMode = $(if ($RequireSigning) { 'signed-release' } else { 'unsigned-internal' })
    hashesCalculatedAfterSigning = [bool]$RequireSigning
    createdAtUtc = [DateTime]::UtcNow.ToString('o')
    files = $records
}
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $manifestPath -Encoding UTF8
$records | ForEach-Object { "$($_.sha256)  $($_.path)" } | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ASCII
if ($RequireSigning) { & (Join-Path $PSScriptRoot 'verify-release.ps1') -ArtifactDirectory $dist -ExpectedVersion $version -ExpectedSourceCommit $commit }
Write-Host "Release evidence: $manifestPath"
