# Windows Authenticode release gate

A public release requires a trusted Authenticode code-signing certificate and a valid timestamp on **both** `AutelVysor.exe` and `AutelVysor-Setup.exe`. The v0.10.3 build logs report that a trusted certificate was not configured. That release is unsigned; these changes do not retroactively sign or modify it. A self-signed certificate is not a substitute for a trusted publisher certificate.

## Configure the certificate

Set both repository Actions secrets through the repository's Settings → Secrets and variables → Actions:

- `AUTEL_SIGN_PFX_BASE64`: the Base64-encoded PFX including the trusted code-signing private key and any required intermediate certificates.
- `AUTEL_SIGN_PFX_PASSWORD`: the PFX password.

Optionally set Actions variable `AUTEL_SIGN_CERT_THUMBPRINT` to the publisher certificate's SHA-1 thumbprint (40 hexadecimal characters, spaces allowed). When configured, signing and publishing reject other publishers. The thumbprint is a public certificate identifier, not the file hashing algorithm; all file hashes and signing digests use SHA-256.

Do not put PFX files, encoded private keys, passwords or their values in Git, logs, workflow outputs or issue comments. The CI availability probe reports only a configured boolean. Missing both secrets permits an explicitly labelled internal candidate. Partially configured secrets fail CI. The final signed release never falls back to an unsigned mode.

The signing identity must have a private key, an explicit code-signing EKU and a current, trusted chain with online revocation validation. A certificate provider requiring hardware or a managed signing service needs its supported signing integration; do not export a non-exportable key or replace it with a self-signed one. The current implementation accepts PFX-based signing only.

## Build and verify

Run on Windows with .NET 8, Windows SDK SignTool, Inno Setup 6 and access to the certificate's revocation servers and timestamp service:

```powershell
# Credentials are provided through environment variables by the protected CI job.
.\scripts\build-release.ps1 -SkipToolDownload -RequireSigning
.\scripts\verify-release.ps1 -ArtifactDirectory dist -ExpectedVersion 0.10.4
.\scripts\tests\ReleaseSigning.Tests.ps1
```

Required-signing preflight fails before building or packaging if the real credentials are missing or invalid. The build signs and timestamps the application **before** creating the portable ZIP, then builds and signs/timestamps the installer. It independently checks `Get-AuthenticodeSignature.Status = Valid`, a non-null `TimeStamperCertificate`, and `signtool verify /pa /all /tw /v` with exit code zero for each EXE. SignTool's warning exit code is a failure. Timestamp-server outages fail the signed build.

Only after all signing completes does the build calculate SHA-256 hashes and create `dist/release-manifest.json` and `dist/SHA256SUMS.txt`. The manifest records the version, source commit/run, package sizes and hashes, publisher/timestamp certificate identities and verbose SignTool verification evidence (including the timestamp). The gate checks that the portable ZIP contains exactly one root `AutelVysor.exe` with precisely the same SHA-256 as the verified signed application. Changing either signed file or re-creating a ZIP requires re-running package validation and hash generation.

For a deliberately unsigned **internal test candidate**:

```powershell
.\scripts\build-release.ps1 -SkipToolDownload -AllowUnsignedInternal
# If Inno Setup is deliberately unavailable for local portable testing:
.\scripts\build-release.ps1 -SkipToolDownload -AllowUnsignedInternal -SkipInstaller
```

The internal manifest has `releaseMode: unsigned-internal` and cannot pass the public release gate. Running the build without an explicit signing mode fails. Negative tests use a temporary self-signed certificate without adding it to a trusted root store; it is never a release certificate.

## Publish a new signed version

The Windows artifact must preserve these paths relative to `dist`:

- `AutelVysor/AutelVysor.exe` (for independent verification and byte comparison)
- `AutelVysor-v<VERSION>-windows-x64.zip`
- `AutelVysor-Setup.exe`
- `release-manifest.json`
- `SHA256SUMS.txt`

For a new version, prepare a new `release-request.json` pointing to a successful main push build, its exact commit/run/artifact, the two package hashes from the post-signing manifest, and `manifest_sha256` containing the SHA-256 of the exact `release-manifest.json` bytes. Preserve the build's UTF-8 bytes; editing or reformatting the manifest changes its hash. Existing unsigned v0.10.3 requests intentionally fail this new gate.

The publishing workflow runs on Windows and validates the pinned manifest hash, version/source commit, all three recorded file hashes/sizes, trusted signatures, timestamp certificates and SignTool verification again, then compares the signed portable application bytes. This happens before creating a tag, draft, or release asset. It publishes the ZIP, installer, `release-manifest.json` and release-specific `SHA256SUMS.txt`, checking uploaded size and GitHub SHA-256 digests before making the draft public. It refuses to mutate any existing public release.

The script and negative regressions can establish that missing credentials, invalid trust, missing timestamps and mismatched packages are rejected. A successful trusted signed v0.10.4 build and public-release approval remain blocked until the real signing certificate is configured and the Windows verification succeeds. A valid Authenticode signature identifies the publisher; it does not guarantee immediate Microsoft SmartScreen reputation.

References: [Microsoft SignTool options](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool), [Get-AuthenticodeSignature](https://learn.microsoft.com/en-us/powershell/module/microsoft.powershell.security/get-authenticodesignature), [Authenticode timestamping](https://learn.microsoft.com/en-us/windows/win32/seccrypto/time-stamping-authenticode-signatures).
