# Changelog

## 0.5.0

- Added best-effort Wi-Fi IPv4 detection after switching a USB device to ADB TCP/IP mode.
- Changed wireless pairing to pass the six-digit pairing code directly to `adb pair`, avoiding WinExe console-prompt problems.
- Added Volume Up / Volume Down controls to mirror sessions.
- Added Android 4.4 compatibility-mode drag-and-drop: APK files install, normal files push to `/sdcard/Download/`.
- Added right-click = Back for the Android 4.4 compatibility renderer.
- Added **Copy diagnostics** with app/OS/ADB/scrcpy/device details.
- Added `%LOCALAPPDATA%\Autel\AutelVysor\AutelVysor.log` unhandled-exception logging.
- Removed the hard-coded release ZIP version from the packaging script; it is now read from the project version.
- Updated Inno Setup version injection and GitHub Actions artifact matching.
- Added packaged `AutelVysor.exe --self-test` and made the Windows build fail if bundled ADB/scrcpy cannot start.
- Kept `FileDescription=Autel` and `ProductName=Autel Vysor`.

## 0.4.0

### Stage 1 - embedded mirroring and control

- Added `NativeWindowHost` to host the native scrcpy SDL window inside WPF.
- Added `MirrorSessionControl` as a unified Android session UI.
- Android 5.0+ sessions now use embedded scrcpy instead of opening a separate top-level mirror window.
- Android 4.4 API 19/20 compatibility rendering is now hosted in the same session UI.
- Added common Back, Home, Recents, Power and mirror restart actions.
- Changed MP4 recording to use a no-window background scrcpy process.

### Stage 2 - multi-device and reconnect

- Added tabbed simultaneous mirror sessions.
- Added multi-select device opening and Open all.
- Added 2.5-second device monitor.
- Sessions remain present when devices disconnect and are restarted when connectivity returns.
- Added remembered Wi-Fi endpoints and throttled automatic reconnect.
- Added `%LOCALAPPDATA%\Autel\AutelVysor\settings.json` settings persistence.

### Stage 3 - Windows release

- Added `prepare-tools.ps1` to retrieve the latest official scrcpy Windows x64 package.
- Added self-contained Windows x64 publish and executable metadata validation.
- Added portable release ZIP generation.
- Added Inno Setup installer definition and installer metadata validation.
- Added GitHub Actions Windows build workflow.
- Kept `FileDescription=Autel`, `ProductName=Autel Vysor` and version 0.4.0.0.

### Cleanup

- Removed the old standalone `LegacyMirrorWindow`; legacy Android now shares the common tabbed mirror control.

## 0.3.0

- Added Android 4.4 / API 19 baseline.
- Added API/Android version/ABI detection.
- Added built-in ADB compatibility mirror and input handling for API 19/20.
- Kept scrcpy for Android 5.0+.

## 0.2.0

- Wireless pairing/connect/disconnect.
- APK install and file push/pull.

## 0.1.0

- Initial WPF project, ADB discovery and scrcpy integration.
