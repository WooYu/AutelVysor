# Standalone PowerShell regression tests; no third-party test module needed.
# -PortableOnly runs pure gate/archive checks on Linux; CI must run default Windows coverage.
param([switch]$PortableOnly)
$ErrorActionPreference = 'Stop'
$scripts = Split-Path $PSScriptRoot -Parent
. (Join-Path $scripts 'release-signing.ps1')
$script:passed = 0
function Assert-Throws {
    param([scriptblock]$Action, [string]$Expected, [string]$Name)
    $failure = $null
    try { & $Action } catch { $failure = $_ }
    if (-not $failure -or $failure.Exception.Message -notmatch $Expected) { throw "FAIL $Name (expected failure matching '$Expected'): $failure" }
    $script:passed++
    Write-Host "PASS $Name"
}
$previousPfx = $env:AUTEL_SIGN_PFX_BASE64
$previousPassword = $env:AUTEL_SIGN_PFX_PASSWORD
$previousPin = $env:AUTEL_SIGN_CERT_THUMBPRINT
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('autel-signing-tests-' + [guid]::NewGuid().ToString('N'))
try {
    $env:AUTEL_SIGN_PFX_BASE64 = ''
    $env:AUTEL_SIGN_PFX_PASSWORD = ''
    $env:AUTEL_SIGN_CERT_THUMBPRINT = ''
    # This isolated script copy has a build entry point that MUST NOT be reached.
    $fixture = Join-Path $temp 'scripts'
    New-Item -ItemType Directory -Path $fixture -Force | Out-Null
    Copy-Item (Join-Path $scripts 'build-release.ps1'), (Join-Path $scripts 'release-signing.ps1') $fixture
    'throw "BUILD_REACHED_BEFORE_SIGNING_PREFLIGHT"' | Set-Content (Join-Path $fixture 'build-windows.ps1')
    Assert-Throws { & (Join-Path $fixture 'build-release.ps1') -RequireSigning } 'trusted certificate secrets' 'Required signing without credentials fails before build or packaging'
    if (Test-Path (Join-Path $temp 'dist')) { throw 'Missing-certificate preflight created packaging output.' }
    Assert-Throws { & (Join-Path $fixture 'build-release.ps1') } 'explicitly' 'Implicit unsigned build is forbidden'
    Assert-Throws { & (Join-Path $fixture 'build-release.ps1') -RequireSigning -AllowUnsignedInternal } 'exactly one' 'Conflicting signing modes are forbidden'
    $unsigned = Join-Path $temp 'unsigned.ps1'
    'Write-Output "test"' | Set-Content $unsigned
    if (-not $PortableOnly) { Assert-Throws { Get-VerifiedReleaseSignature -FilePath $unsigned } 'unsigned, invalid or untrusted' 'Real unsigned file is rejected' }
    $thumbprint = 'A' * 40
    $validWithoutTimestamp = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Thumbprint = $thumbprint }; TimeStamperCertificate = $null }
    # Validate the explicit timestamp condition independently of trust failure on a self-signed fixture.
    Assert-Throws { Assert-ReleaseSignatureRecord -Signature $validWithoutTimestamp } 'missing a trusted timestamp' 'Otherwise valid signature without timestamp is rejected'
    $untrusted = [pscustomobject]@{ Status = 'NotTrusted'; SignerCertificate = [pscustomobject]@{ Thumbprint = $thumbprint }; TimeStamperCertificate = [pscustomobject]@{} }
    Assert-Throws { Assert-ReleaseSignatureRecord -Signature $untrusted } 'untrusted' 'Untrusted certificate is rejected even with timestamp metadata'
    $wrongPublisher = [pscustomobject]@{ Status = 'Valid'; SignerCertificate = [pscustomobject]@{ Thumbprint = $thumbprint }; TimeStamperCertificate = [pscustomobject]@{ Subject = 'timestamp' } }
    Assert-Throws { Assert-ReleaseSignatureRecord -Signature $wrongPublisher -ExpectedThumbprint ('B' * 40) } 'thumbprint mismatch' 'Optional configured publisher pin is enforced'
    @{schemaVersion=1; releaseMode='unknown'; hashesCalculatedAfterSigning=$false} | ConvertTo-Json | Set-Content (Join-Path $temp 'release-manifest.json')
    Assert-Throws { & (Join-Path $scripts 'verify-release.ps1') -ArtifactDirectory $temp } 'Unsupported release manifest' 'Unknown signing mode is rejected'
    $exe = Join-Path $temp 'AutelVysor.exe'
    [System.IO.File]::WriteAllBytes($exe, [byte[]]@(1,2,3))
    $zip = Join-Path $temp 'test.zip'
    Compress-Archive -LiteralPath $exe -DestinationPath $zip
    Assert-ReleaseZipExecutable -ZipPath $zip -ExecutablePath $exe
    $script:passed++
    Write-Host 'PASS Portable archive contains exact application bytes'
    # Unsigned publication keeps the same provenance, hash and portable-byte checks.
    $package = Join-Path $temp 'unsigned-release'
    New-Item -ItemType Directory -Path (Join-Path $package 'AutelVysor') -Force | Out-Null
    Copy-Item $exe (Join-Path $package 'AutelVysor/AutelVysor.exe')
    Copy-Item $exe (Join-Path $package 'AutelVysor-Setup.exe')
    Copy-Item $zip (Join-Path $package 'AutelVysor-v0.10.7-windows-x64.zip')
    $unsignedManifest = @{schemaVersion=1; releaseMode='unsigned-internal'; hashesCalculatedAfterSigning=$false;
        version='0.10.7'; sourceCommit=('a' * 40); sourceRunId='123'; files=@(
            foreach ($relative in @('AutelVysor/AutelVysor.exe', 'AutelVysor-Setup.exe', 'AutelVysor-v0.10.7-windows-x64.zip')) {
                $file = Join-Path $package $relative
                @{path=$relative; sha256=(Get-ReleaseHash $file); size=(Get-Item $file).Length}
            }
        )}
    $unsignedManifest | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $package 'release-manifest.json')
    & (Join-Path $scripts 'verify-release.ps1') -ArtifactDirectory $package -ExpectedVersion '0.10.7' -ExpectedSourceCommit ('a' * 40) -ExpectedSourceRunId '123'
    $script:passed++
    Write-Host 'PASS Unsigned packages can be published with verified provenance and exact bytes'
    Assert-Throws { & (Join-Path $scripts 'verify-release.ps1') -ArtifactDirectory $package -ExpectedSourceCommit ('b' * 40) } 'source commit differs' 'Unsigned release rejects incorrect provenance'
    [System.IO.File]::WriteAllBytes((Join-Path $package 'AutelVysor-Setup.exe'), [byte[]]@(9,9,9))
    Assert-Throws { & (Join-Path $scripts 'verify-release.ps1') -ArtifactDirectory $package } 'hash/size mismatch' 'Unsigned release rejects tampered installer'
    [System.IO.File]::WriteAllBytes($exe, [byte[]]@(4,5,6))
    Assert-Throws { Assert-ReleaseZipExecutable -ZipPath $zip -ExecutablePath $exe } 'differs from' 'Portable archive mismatch is rejected'
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Update)
    try { $null = $archive.CreateEntry('./AutelVysor.exe') } finally { $archive.Dispose() }
    Assert-Throws { Assert-ReleaseZipExecutable -ZipPath $zip -ExecutablePath $exe } 'unsafe or duplicate' 'Portable archive path aliases are rejected'
    New-Item -ItemType Directory -Path (Join-Path $temp 'AutelVysor') -Force | Out-Null
    Copy-Item $exe (Join-Path $temp 'AutelVysor/AutelVysor.exe')
    @{schemaVersion=1; releaseMode='signed-release'; hashesCalculatedAfterSigning=$true; version='0.10.4'; files=@(
        @{path='AutelVysor/AutelVysor.exe'; sha256=('0' * 64); size=3},
        @{path='AutelVysor-Setup.exe'; sha256=('0' * 64); size=3},
        @{path='AutelVysor-v0.10.4-windows-x64.zip'; sha256=('0' * 64); size=3}
    )} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $temp 'release-manifest.json')
    Assert-Throws { & (Join-Path $scripts 'verify-release.ps1') -ArtifactDirectory $temp } 'hash/size mismatch' 'Post-signing manifest tampering is rejected'
    # A real self-signed certificate is a NEGATIVE fixture only; never install its root into trust stores.
    if (-not $PortableOnly) {
    $cert = New-SelfSignedCertificate -Subject 'CN=AutelSigningNegativeTest' -Type CodeSigningCert -CertStoreLocation Cert:\CurrentUser\My
    try {
        $null = Set-AuthenticodeSignature -FilePath $unsigned -Certificate $cert
        Assert-Throws { Get-VerifiedReleaseSignature -FilePath $unsigned } 'unsigned, invalid or untrusted|missing a trusted timestamp' 'Real self-signed untimestamped fixture is rejected'
    }
    finally { Remove-Item -LiteralPath ("Cert:\CurrentUser\My\" + $cert.Thumbprint) -Force }
    }
    if ($PortableOnly) { Write-Host "Portable gate regressions passed: $script:passed (Windows Authenticode checks excluded)" }
    else { Write-Host "Release signing regressions passed: $script:passed" }
}
finally {
    $env:AUTEL_SIGN_PFX_BASE64 = $previousPfx
    $env:AUTEL_SIGN_PFX_PASSWORD = $previousPassword
    $env:AUTEL_SIGN_CERT_THUMBPRINT = $previousPin
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
