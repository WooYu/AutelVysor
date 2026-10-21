# v0.10.0 Implementation Status

Baseline: v0.9.0 code on main. Integration commit: 3fcc6e391edef8fa5d70e950dc5a9269a59d23e7.
Windows CI: https://github.com/WooYu/AutelVysor/actions/runs/39552698115

## Implemented and Windows-build validated

- [x] Fit / 1:1 / F11 full screen / collapsible sidebar (from v0.9).
- [x] Copy Unicode text to Windows clipboard for scrcpy paste; optional ADBKeyBoard Base64 broadcast for installed and selected legacy IME.
- [x] Periodic Android orientation state check; shows degrees and auto/locked.
- [x] Audio policy indicator and expandable Android audio diagnostics (not definitive Bluetooth active-route confirmation).
- [x] Short ADB dropout tolerance and bounded scrcpy restart retry.
- [x] Remote file browsing, upload/download/new folder/confirmed delete.
- [x] ADB Shell with command history.
- [x] Grant/revoke runtime permission and AppOps get/set with confirmation.
- [x] Installed package filtering: All / User / System, searchable package names.
- [x] Logcat text, Tag, PID, severity and selected-app PID filters.
- [x] Network view for IP, DNS, routes, TCP/UDP sockets, Wi-Fi and ping.
- [x] Device overview: build properties, display, battery, memory, storage, network (v0.9).
- [x] Bugreport export to local disk.
- [x] Local screenshot and recording history with image preview/open/delete.
- [x] Multiple selected devices: batch ADB shell / APK install, confirmation, results, cancellation.
- [x] Saved frequently-used ADB shell commands.
- [x] Interactive and optional startup check of GitHub Releases; opens the official release page.
- [x] Optional Authenticode certificate signing and verification in Windows release workflow.

## Deliberately partial / blocked / unverified

- [ ] Android app *display name* search: stock adb package listing exposes package identifiers but not reliable localized labels. Requires optional helper APK or trusted metadata source.
- [ ] Full unattended update installation: disabled until signed artifacts, checksum verification, user consent and rollback design are implemented.
- [ ] Trusted signed distribution: CI generated an **unsigned test build** because code-signing certificate secrets are not configured; see SIGNING.md.
- [ ] Android 4.4 Unicode keyboard helper is not bundled or auto-installed. Install and select a supported open-source ADBKeyBoard IME before use.
- [ ] Runtime/physical-device acceptance for Bluetooth playback, forced rotation on vendor ROMs, USB/Wi-Fi reconnect and specific device permissions.
- [ ] Capture manager manages saved files; recording process start/stop/finalization can be improved separately.
- [ ] Legacy minicap frame rate and Android 4.4 vendor compatibility need physical testing.

## CI result

Run #45: Windows Build success. WPF compilation, CLI self-test, Inno Setup installer, ZIP and artifact upload passed.
FileDescription=Autel, ProductName=Autel Vysor, FileVersion=0.10.0.0.
This is not equivalent to physical Android/Windows GUI testing.
