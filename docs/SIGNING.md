# Windows release signing and publication

Public releases may be signed or unsigned. A trusted certificate is optional for publication. Both modes require a successful main push build, a pinned source commit/run/artifact and manifest SHA-256, matching package hashes and sizes, and an exact match between the portable ZIP executable and the application executable.

The publishing workflow adds the signing status to release notes. Unsigned releases may trigger Windows unknown-publisher warnings. The historical manifest value `unsigned-internal` is retained for compatibility and is accepted for public publication.

## Build and verify

For an unsigned release, no certificate is needed:

```powershell
.\scripts\build-release.ps1 -SkipToolDownload -AllowUnsignedInternal
.\scripts\verify-release.ps1 -ArtifactDirectory dist -ExpectedVersion 0.10.7
```

For a trusted signed release, configure both Actions secrets `AUTEL_SIGN_PFX_BASE64` and `AUTEL_SIGN_PFX_PASSWORD`, then use `-RequireSigning`. Optionally configure `AUTEL_SIGN_CERT_THUMBPRINT` to pin the publisher. Do not commit certificates, private keys or passwords.

Signed mode requires trusted Authenticode signatures and timestamps on both application and installer, verified with Windows and SignTool. Hashes are calculated after signing. Invalid, untrusted, missing-timestamp or wrong-publisher signatures still fail signed verification. A failed signed build never silently falls back to unsigned mode; explicitly select unsigned mode instead. Partially configured credentials remain a build configuration error.

## Publish

Update `release-request.json` with the new tag, successful main push build commit/run/artifact, exact manifest SHA-256 and ZIP/installer hashes. Both signing modes use the same publication workflow. The artifact must include:

- `AutelVysor/AutelVysor.exe`
- `AutelVysor-v<VERSION>-windows-x64.zip`
- `AutelVysor-Setup.exe`
- `release-manifest.json`
- `SHA256SUMS.txt`

Verification happens before creating a tag or release. The workflow checks uploaded asset sizes and GitHub SHA-256 digests before making the draft public and refuses to modify existing public releases.

Run `.\scripts\tests\ReleaseSigning.Tests.ps1` on Windows for unsigned publication, tampering/provenance rejection, archive integrity and signed-mode failure regressions. `-PortableOnly` excludes Windows Authenticode tests.
