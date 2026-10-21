# Shared by signing, packaging and the independent publishing gate. Windows PowerShell 5.1+.
function Get-ReleaseSignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    $sdk = Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits\10\bin'
    $candidate = Get-ChildItem $sdk -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
        Where-Object { $_.DirectoryName -like '*\x64' } | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $candidate) { throw 'signtool.exe missing. Install the Windows SDK.' }
    return $candidate.FullName
}

function Get-ReleasePublisherThumbprint {
    $thumbprint = ($env:AUTEL_SIGN_CERT_THUMBPRINT -replace '\s', '').ToUpperInvariant()
    if ($thumbprint -and $thumbprint -notmatch '^[A-F0-9]{40}$') {
        throw 'AUTEL_SIGN_CERT_THUMBPRINT must be a certificate SHA-1 thumbprint when configured.'
    }
    return $thumbprint
}

function Assert-ReleaseSigningConfiguration {
    # Do not log, echo, or include private-key material in error messages.
    if ([string]::IsNullOrWhiteSpace($env:AUTEL_SIGN_PFX_BASE64) -or
        [string]::IsNullOrWhiteSpace($env:AUTEL_SIGN_PFX_PASSWORD)) {
        throw 'Required signing blocked: trusted certificate secrets AUTEL_SIGN_PFX_BASE64 and AUTEL_SIGN_PFX_PASSWORD are not configured.'
    }
    $expected = Get-ReleasePublisherThumbprint
    $certificate = $null
    $chain = $null
    try {
        $flags = [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::EphemeralKeySet
        $certificate = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList @(
            [Convert]::FromBase64String($env:AUTEL_SIGN_PFX_BASE64), $env:AUTEL_SIGN_PFX_PASSWORD, $flags)
        if (-not $certificate.HasPrivateKey) { throw 'Certificate has no private key.' }
        if ($expected -and $certificate.Thumbprint -ne $expected) { throw 'Publisher thumbprint mismatch.' }
        $eku = @($certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' })
        if ($eku.Count -ne 1 -or @($eku[0].EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.3' }).Count -eq 0) {
            throw 'Certificate must explicitly permit code signing.'
        }
        $chain = New-Object System.Security.Cryptography.X509Certificates.X509Chain
        $chain.ChainPolicy.RevocationMode = [System.Security.Cryptography.X509Certificates.X509RevocationMode]::Online
        $null = $chain.ChainPolicy.ApplicationPolicy.Add((New-Object System.Security.Cryptography.Oid '1.3.6.1.5.5.7.3.3'))
        if (-not $chain.Build($certificate)) { throw 'Certificate chain is not trusted, current and revocation-valid.' }
    }
    catch { throw 'Required signing blocked: PFX could not be validated as a current trusted code-signing certificate with private key and any configured publisher thumbprint.' }
    finally {
        if ($chain) { $chain.Dispose() }
        if ($certificate) { $certificate.Dispose() }
    }
    $null = Get-ReleaseSignTool
}

function Assert-ReleaseSignatureRecord {
    param([Parameter(Mandatory=$true)]$Signature, [string]$ExpectedThumbprint)
    if ([string]$Signature.Status -ne 'Valid' -or -not $Signature.SignerCertificate) {
        throw 'Release signature is unsigned, invalid or untrusted (Get-AuthenticodeSignature must return Valid).'
    }
    if ($ExpectedThumbprint -and $Signature.SignerCertificate.Thumbprint -ne $ExpectedThumbprint) { throw 'Release signature publisher thumbprint mismatch.' }
    if (-not $Signature.TimeStamperCertificate) { throw 'Release signature is missing a trusted timestamp.' }
}

function Get-VerifiedReleaseSignature {
    param([Parameter(Mandatory=$true)][string]$FilePath, [string]$ExpectedThumbprint)
    if (-not (Test-Path -LiteralPath $FilePath -PathType Leaf)) { throw "Signing target missing: $FilePath" }
    $signature = Get-AuthenticodeSignature -LiteralPath $FilePath
    Assert-ReleaseSignatureRecord -Signature $signature -ExpectedThumbprint $ExpectedThumbprint
    $tool = Get-ReleaseSignTool
    # /tw warns about missing timestamps. Any warning (exit 2) is a failure, too.
    $output = @(& $tool verify /pa /all /tw /v $FilePath 2>&1)
    if ($LASTEXITCODE -ne 0) { throw "signtool trust/timestamp verification failed: $FilePath" }
    return [ordered]@{
        status = 'Valid'
        signerSubject = $signature.SignerCertificate.Subject
        signerThumbprint = $signature.SignerCertificate.Thumbprint
        signerIssuer = $signature.SignerCertificate.Issuer
        timestampPresent = $true
        timestampSubject = $signature.TimeStamperCertificate.Subject
        timestampThumbprint = $signature.TimeStamperCertificate.Thumbprint
        timestampIssuer = $signature.TimeStamperCertificate.Issuer
        verificationPolicy = 'signtool verify /pa /all /tw /v; exit 0'
        # SignTool's verbose verification includes the countersignature time and chain.
        verificationOutput = ($output | ForEach-Object { [string]$_ }) -join "`n"
    }
}

function Get-ReleaseHash {
    param([Parameter(Mandatory=$true)][string]$FilePath)
    return (Get-FileHash -LiteralPath $FilePath -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ReleaseZipExecutable {
    param([Parameter(Mandatory=$true)][string]$ZipPath, [Parameter(Mandatory=$true)][string]$ExecutablePath)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        # Exactly one root app EXE; reject duplicate/case-colliding names and traversal aliases.
        $matches = @($archive.Entries | Where-Object { ($_.FullName -replace '\\', '/') -ieq 'AutelVysor.exe' })
        if ($matches.Count -ne 1) { throw 'Portable ZIP must contain exactly one root AutelVysor.exe.' }
        $names = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $normalized = $entry.FullName -replace '\\', '/'
            $parts = $normalized.TrimEnd('/') -split '/'
            if ($normalized.StartsWith('/') -or $normalized.Contains(':') -or $parts -contains '..' -or $parts -contains '.' -or $parts -contains '' -or -not $names.Add($normalized)) {
                throw 'Portable ZIP contains an unsafe or duplicate entry path.'
            }
        }
        $stream = $matches[0].Open()
        $sha = [System.Security.Cryptography.SHA256]::Create()
        try { $actual = ([BitConverter]::ToString($sha.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $sha.Dispose(); $stream.Dispose() }
        if ($actual -ne (Get-ReleaseHash $ExecutablePath)) { throw 'Portable ZIP executable differs from the verified signed executable.' }
    }
    finally { $archive.Dispose() }
}
