param(
    [Parameter(Mandatory=$true)][string]$ArtifactDirectory,
    [string]$ExpectedVersion,
    [string]$ExpectedSourceCommit,
    [string]$ExpectedSourceRunId
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'release-signing.ps1')
$expected = Get-ReleasePublisherThumbprint
$manifestPath = Join-Path $ArtifactDirectory 'release-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.schemaVersion -ne 1 -or $manifest.releaseMode -ne 'signed-release' -or $manifest.hashesCalculatedAfterSigning -ne $true) {
    throw 'Only a signed release with post-signing hashes can be published.'
}
if ($ExpectedVersion -and $manifest.version -cne $ExpectedVersion) { throw 'Manifest version differs from requested release.' }
if ($ExpectedSourceCommit -and $manifest.sourceCommit -cne $ExpectedSourceCommit) { throw 'Manifest source commit differs from requested build.' }
if ($ExpectedSourceRunId -and [string]$manifest.sourceRunId -cne $ExpectedSourceRunId) { throw 'Manifest source run differs from requested build.' }
if ($manifest.version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid manifest version.' }
$files = @($manifest.files)
$expectedPaths = @('AutelVysor/AutelVysor.exe', 'AutelVysor-Setup.exe', "AutelVysor-v$($manifest.version)-windows-x64.zip")
if ($files.Count -ne 3) { throw 'Manifest must describe the application EXE, installer and ZIP.' }
foreach ($relative in $expectedPaths) {
    $records = @($files | Where-Object { $_.path -ceq $relative })
    if ($records.Count -ne 1) { throw "Missing or duplicate manifest record: $relative" }
    $record = $records[0]
    $path = Join-Path $ArtifactDirectory $relative
    if ($record.sha256 -cnotmatch '^[a-f0-9]{64}$' -or (Get-ReleaseHash $path) -cne $record.sha256 -or (Get-Item -LiteralPath $path).Length -ne $record.size) {
        throw "Manifest hash/size mismatch: $relative"
    }
    if ($relative.EndsWith('.exe')) {
        $verified = Get-VerifiedReleaseSignature -FilePath $path -ExpectedThumbprint $expected
        foreach ($field in @('status', 'signerSubject', 'signerThumbprint', 'signerIssuer', 'timestampPresent', 'timestampSubject', 'timestampThumbprint', 'timestampIssuer', 'verificationPolicy')) {
            if ($null -eq $record.signature -or $record.signature.$field -cne $verified[$field]) { throw "Manifest signature metadata mismatch: $relative ($field)" }
        }
        if ([string]::IsNullOrWhiteSpace($record.signature.verificationOutput)) { throw 'Manifest timestamp verification evidence missing.' }
    }
}
Assert-ReleaseZipExecutable -ZipPath (Join-Path $ArtifactDirectory $expectedPaths[2]) -ExecutablePath (Join-Path $ArtifactDirectory $expectedPaths[0])
Write-Host 'Release gate passed: trusted publisher and timestamp, post-signing hashes, and exact signed portable EXE.'
