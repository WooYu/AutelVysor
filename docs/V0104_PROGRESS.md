# v0.10.4 correction and publication checklist

User requirements: AV-006 Chinese/special-character echo, explicit command diagnostics and correct batch failure totals, DNS compatibility/statuses, complete Unicode/ADBKeyBoard recovery and clipboard acceptance, trusted timestamped signing and post-signing package hashes.

Base: main 79f35791036c329a40bd018e512ce979c1f357cc.

- [x] Reproduce text encoding/diagnostics/DNS failures through subprocess and simulated-runner regressions; implement explicit UTF-8, separate streams, and verified remote exit status. Twenty-five command tests and three existing bridge tests pass locally, including advanced permission/AppOps/audio/bugreport diagnostics.
- [x] Implement explicit ADBKeyBoard setup/activation/restoration with persistent recovery and receiver/result verification; add service tests and physical clipboard acceptance steps. Eighteen portable Unicode/IME tests pass; device acceptance is pending.
- [x] Require trusted valid timestamped EXE/installer signatures for public release, hash after signing, and verify packaged executable bytes. Eleven portable negative signing/package tests pass; trusted-positive signing is pending certificate configuration.
- [x] Integrate all focused tests into Windows CI and retain existing Bridge/WPF regressions.
- [x] Review combined changes and run Windows regressions: all 63 pass in run 39616912533 (17 command, 18 Unicode/IME, 13 signing/package, 3 bridge, 12 WPF/native). Application build confirms FileDescription=Autel. Installer packaging is rerun after restoring the original version-resource whitespace normalization.
- [ ] Merge the validated code and publish only when signing requirements pass.

Physical Unicode and clipboard acceptance require an authorized connected device; simulated tests are not that acceptance. A trusted company signing certificate/private key is external input and will not be replaced by a self-signed certificate. If absent, complete the code and candidate checks and leave final publication blocked explicitly.

Independent review found two important false-success cases: existing matching Unicode text and legacy ADB host-zero network failures. Both were corrected with new regressions. Windows-native compilation, tests, executable metadata and credential availability are checked in CI before integration.

Windows CI run 39616912533 explicitly reports `Trusted signing credential configured: False`. Final signed publication is blocked until the repository Actions secrets `AUTEL_SIGN_PFX_BASE64` and `AUTEL_SIGN_PFX_PASSWORD` contain a trusted code-signing certificate with its private key and its password. Do not provide private credentials in chat. Self-signed certificates and unsigned internal artifacts are rejected by the public publishing gate.
