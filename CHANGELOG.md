# Changelog

## 0.9.1

- Simplified real-device rotation to a two-orientation toggle, with a shared busy guard and Fit reset.
- Track native mirror size changes and restore the embedded viewport after SDL resizes itself on rotation.
- Resize the external HWND asynchronously and skip identical bounds to avoid blocking the WPF UI or creating resize feedback loops.
- Unified dark selected, hover and focus states for device rows, tabs, dropdown items and buttons; improved device serial contrast.
- Cache stable Android metadata until disconnect or transport change, avoid duplicate device scans without reconnect attempts, and preserve unchanged device lists.
- Run Android 4.4 image capture and decoding loops off the UI thread.
- Added Windows regressions for repeated native-window resizing, orientation toggling and metadata cache invalidation.

## 0.9.0

- Added hide/show sidebar and F11 full-screen workspace modes.
- Added Fit and 1:1 mirror display modes.
- Added an interactive ADB Shell tab with command history.
- Added a remote Android file manager with browse, upload, download, mkdir and confirmed delete.
- Added logcat filtering by text, minimum priority and selected-app PID, plus pause.
- Added detailed device reporting for build, display, battery, memory, storage and network.
- Preserved the v0.8.1 real-device rotation, readable dark sidebar and Bluetooth/device-audio fixes.


## 0.8.1

- Fixed low-contrast left-sidebar controls with explicit dark CheckBox, ComboBox and Expander styling.
- Made the lower left settings and Files / utilities section vertically scrollable.
- Changed rotation to modify the real Android system display orientation instead of rotating only the PC mirror image.
- Added an Auto rotate action to restore Android sensor-based rotation.
- Preserved phone/Bluetooth audio by default; Android 13+ uses scrcpy audio duplication to keep both PC and device playback.
- Android 11/12 preserve device/Bluetooth audio when the new preference is enabled.


## 0.8.0

- Added a dedicated Device Debug window.
- Added app version/versionCode, PID, minSdk/targetSdk, install/update time and data-directory inspection.
- Added permission inspection and full `dumpsys package` output.
- Added device process-list inspection.
- Added live logcat, snapshot logcat and exported logcat files with bounded in-app log memory.
- Added APK installation and application uninstall with optional data retention.
- Added Android-device reboot and Windows ADB-server restart actions.
- Kept force-stop, start, restart and clear-data actions; destructive actions require confirmation.
- Moved ADB debugging out of the crowded mirror sidebar.


## 0.7.0

- Android 5.0+ mirrors now turn the physical device screen off by default while keeping the PC mirror and controls active.
- Added per-session clockwise display rotation in 90° increments for scrcpy-backed devices.
- Added ADB app tools: load package names, force-stop, start, restart and clear application data.
- Clear-app-data requires explicit confirmation.
- Redesigned the connected-device list as compact cards and removed its horizontal scrollbar.
- Android 4.4/4.4W keeps the physical display active for legacy capture compatibility.


## 0.6.0

- Added optional DeviceFarmer minicap acceleration for Android 4.4/API 19 armeabi-v7a and x86.
- Added automatic fallback to the existing ADB PNG capture path when minicap is unavailable or incompatible.
- Kept Android 4.4 input on the conservative ADB input path.
- Added Smooth / Standard / High mirror quality presets for scrcpy.
- Added audio forwarding and clipboard autosync settings.
- Added persisted legacy acceleration preference and diagnostics reporting.
- Added Windows 10 minimum installer requirement while continuing Windows 11 support.
- Updated release version metadata to 0.6.0 while keeping FileDescription=Autel.


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
