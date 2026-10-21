param(
    [Parameter(Mandatory=$true)][string]$FilePath,
    [switch]$RequireSigning,
    [switch]$AllowUnsignedInternal
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-signing.ps1')
if ($RequireSigning -and $AllowUnsignedInternal) { throw 'Choose exactly one signing mode.' }
if (-not $RequireSigning -and -not $AllowUnsignedInternal) { throw 'Specify -RequireSigning or -AllowUnsignedInternal explicitly.' }
if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) { throw "Signing target missing: $FilePath" }
if ($AllowUnsignedInternal) {
    Write-Host "UNSIGNED INTERNAL candidate; blocked from public release: $FilePath"
    return
}
Assert-ReleaseSigningConfiguration
$tool = Get-ReleaseSignTool
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ('autel-codesign-' + [guid]::NewGuid().ToString('N') + '.pfx')
try {
    [System.IO.File]::WriteAllBytes($temp, [Convert]::FromBase64String($env:AUTEL_SIGN_PFX_BASE64))
    & $tool sign /fd SHA256 /td SHA256 /tr 'http://timestamp.digicert.com' /f $temp /p $env:AUTEL_SIGN_PFX_PASSWORD $FilePath
    if ($LASTEXITCODE -ne 0) { throw 'signtool signing or timestamping failed.' }
    $null = Get-VerifiedReleaseSignature -FilePath $FilePath -ExpectedThumbprint (Get-ReleasePublisherThumbprint)
    Write-Host "Trusted timestamped Authenticode signature verified: $FilePath"
}
finally { Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue }
