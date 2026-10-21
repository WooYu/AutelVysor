# v0.10.4 correction and publication checklist

User requirements: AV-006 Chinese/special-character echo, explicit command diagnostics and correct batch failure totals, DNS compatibility/statuses, complete Unicode/ADBKeyBoard recovery and clipboard acceptance, trusted timestamped signing and post-signing package hashes.

Base: main 79f35791036c329a40bd018e512ce979c1f357cc.

- [x] Reproduce text encoding/diagnostics/DNS failures through subprocess and simulated-runner regressions; implement explicit UTF-8, separate streams, and verified remote exit status. Seventeen command tests and three existing bridge tests pass locally.
- [x] Implement explicit ADBKeyBoard setup/activation/restoration with persistent recovery and receiver/result verification; add service tests and physical clipboard acceptance steps. Eighteen portable Unicode/IME tests pass; device acceptance is pending.
- [x] Require trusted valid timestamped EXE/installer signatures for public release, hash after signing, and verify packaged executable bytes. Eleven portable negative signing/package tests pass; trusted-positive signing is pending certificate configuration.
- [x] Integrate all focused tests into Windows CI and retain existing Bridge/WPF regressions.
- [ ] Review combined changes, build v0.10.4 and verify executable metadata remains FileDescription=Autel.
- [ ] Merge the validated code and publish only when signing requirements pass.

Physical Unicode and clipboard acceptance require an authorized connected device; simulated tests are not that acceptance. A trusted company signing certificate/private key is external input and will not be replaced by a self-signed certificate. If absent, complete the code and candidate checks and leave final publication blocked explicitly.

Independent review found two important false-success cases: existing matching Unicode text and legacy ADB host-zero network failures. Both were corrected with new regressions. Windows-native compilation, tests, executable metadata and credential availability are checked in CI before integration.
