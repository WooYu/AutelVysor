# Windows Authenticode signing

Autel Vysor test builds are unsigned unless a trusted Authenticode certificate is configured. An unsigned build does not remove Windows SmartScreen warnings.

## CI secrets (optional)

Provide both repository Actions secrets:

- AUTEL_SIGN_PFX_BASE64: Base64-encoded trusted code-signing certificate PFX, including private key.
- AUTEL_SIGN_PFX_PASSWORD: Password for that PFX.

Do not commit PFX files, passwords or encoded private keys to Git.

The Windows release script now:

1. Builds the self-contained Windows application.
2. Signs dist/AutelVysor/AutelVysor.exe before creating the portable ZIP, if secrets are configured.
3. Builds the Inno Setup installer.
4. Signs dist/AutelVysor-Setup.exe if secrets are configured.
5. Verifies both signatures with signtool verify /pa /v.
6. Fails the release when signing is configured but fails.

Without configured secrets, the build prints an explicit UNSIGNED message and still produces a test installer/ZIP.

For production release, prefer a managed code-signing provider or protected certificate lifecycle, and review other redistributed executable components. Update check only opens the official GitHub Releases page; automatic download-and-execute is intentionally disabled until a trusted release verification and rollback policy are in place.
