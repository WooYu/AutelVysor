# Changelog

## 0.10.11

- Unify application windows, native title bars, controls, scrollbars and diagnostics in a subdued dark palette.
- Move file and capture utilities up the sidebar, simplify navigation controls and retain automatic aspect-aware window sizing.
- Add a verified Android hardware-keyboard soft-keyboard visibility setting and Enter submission for PC-composed text.
- Report unexpected recorder exits with guidance to close extra mirror sessions; preserve failure and finalization checks.
- Real Samsung Android 12 testing covered ADB, screenshots, recording, temporary files and fixture app operations. Multi-device, audible audio and physical reconnection remain separate acceptance checks.

## 0.10.10

- Show device tabs, preserve native mirror sessions across tab switches and open the sole supported device automatically on discovery.
- Add a compact window that adapts to device rotation, with mouse-accessible restore and minimize controls.
- Serialize legacy Android keyboard events and protect local editors from legacy input handling; permit whitespace-only text submissions.
- Physical Android input and multi-device acceptance remain pending; this release does not claim to resolve every reported input issue.

## 0.10.9

- Automatically focus the foreground mirror on startup, tab selection and activation, with short bounded retries and native focus confirmation.
- Reuse existing focus without repeated input-thread attachment; preserve PC text entry and mode selection.
- Confirm Keyboard ready on the non-focusable recovery button instead of reporting Focus sent.
- Cover automatic focus, editor protection, explicit recovery and repeated native focus in regression tests. Physical Android acceptance remains separate.

## 0.10.8

- Select the first online supported device on startup and rediscovery; automatically focus the active mirror keyboard after startup and tab activation.
- Show screenshot capture/saved/error states, recording/finalization status and saved paths; target captures at the active mirror.
- Separate screenshot Fit/Actual size controls from video playback controls.
- Add file history, storage shortcuts, filtering, sorting, context actions, rename, folder/batch download and keyboard shortcuts; preserve selection on refresh.
- Preserve quoted Android paths and remote exit diagnostics, prevent rename overwrite, and guard overlapping file/shell/debug operations.
- Search application names and package identifiers with visible results, background APK label indexing, cancellation and session caching.
- Add usability regression and visual checks. Two native SendInput checks remain unverified in the local Windows session and also fail on the unmodified baseline; physical input/recording acceptance remains pending.

## 0.10.7

- Automatically refresh AppOps alongside typed package summaries and permissions, with cancellation and stale-response protection.
- Preserve loaded summaries when Android denies AppOps access and show the diagnostic failure explicitly.
- Add Windows regression coverage for automatic AppOps loading, package changes during loading, invalid input and permission failures.

## 0.10.6 (desktop usability candidate)

- Move session/global controls to the left and maximize full-height Fit; fullscreen resets cropped zoom and provides mouse-accessible Tools/Exit recovery.
- Add right-side capture video playback controls and clear unsupported-media feedback.
- Automatically refresh typed app details with stale-response guards; improve dark file selection and explicit folder navigation.
- Import logs, reapply combined filters to retained live/snapshot/import records, color severity rows, and export all retained or filtered data. Bound retention and disclose loss.
- Expose running/results for focus, rotation, audio and live logging; show audio evidence in a bounded copyable report.
- Add a custom blue desktop/phone application icon, with 16–256px ICO frames for Windows Explorer and the taskbar.
- Embed the icon in the executable and WPF windows; use the same mark for the installer, shortcuts and installed-app entry.
- Preserve FileDescription=Autel and the unsigned internal-candidate signing gate.

## 0.10.5 (input / readability candidate)

- Route modern toolbar text through explicit native scrcpy clipboard paste, including Chinese/Unicode without optional ADBKeyBoard. Guard active/visible window and modifier state; report a request rather than fabricated insertion success.
- Default Auto keyboard to UHID on Android 9+, with a per-session SDK compatibility selector and phone keyboard settings shortcut.
- Add view magnification and panning; preserve view geometry through renderer and WPF layout changes. Keep long error diagnostics in a compact status line with full tooltip.
- Native Windows tests exercise real cross-process digit delivery, clipboard shortcut payload, zoom/pan and WPF host resizes. Physical Android acceptance remains pending.

## 0.10.4 (candidate; signed publication gated)

- AV-006: explicitly decode redirected ADB text and live logcat as UTF-8, write BOM-free UTF-8 stdin, preserve binary streams.
- Preserve separate stdout/stderr plus process and verified remote shell exit status; display command evidence in terminal/batch and classify unavailable status without false PASS.
- Diagnose DNS with legacy properties and modern resolver/connectivity queries, retaining evidence and distinguishing empty configuration, permission denial, unsupported commands/services and query failure.
- Add opt-in ADBKeyBoard installation/setup, durable original-IME recovery, receiver validation and actual focused-field Unicode verification. Add guided two-way clipboard acceptance with actual Android source readback and a fresh PC marker.
- Require trusted valid timestamped application/installer signatures for final release; calculate package hashes after signing and reverify the signed ZIP executable and pinned manifest before publication.
- Windows/Android physical Unicode and clipboard acceptance, and a real trusted signed build, are separate from simulated/negative regression checks. No self-signed substitute is used.

## 0.10.3

- AV-001: send a complete command to adb shell instead of an unquoted nested sh -c, preserving arguments across shell, batch, device and network diagnostics; exit status coverage applies to modern shell_v2. Legacy adbd exit-status limitations remain.
- AV-004: replace prefer-text with SDK raw-key-events for scrcpy keyboard input; keep focus handling and explicit clipboard/Unicode helpers. Physical Windows/Android IME acceptance remains pending.
- AV-005: share actual default-display rotation telemetry across status and orientation actions, with older SurfaceOrientation fallback; report unavailable telemetry instead of stale user_rotation.
- AV-002/003: own recorder processes, reject duplicate device recordings, expose Stop recordings, and wait for graceful MP4 finalization on stop/exit. Report abnormal exits/timeouts explicitly.
- Add portable bridge regressions and Windows recorder/native-window regressions. Extend real-device GUI harness with native virtual-key Backspace/6/7 reproduction and actual Auto angle comparison.
- Preserve FileDescription=Autel. Trusted signing and untested physical feature acceptance remain open.

## 0.10.2

- Fixed cross-process keyboard focus transfer from the WPF native host to scrcpy's SDL window.
- Added explicit **Focus keyboard** and **Send text** actions inside mirror sessions.
- Prefer scrcpy SDK text events for PC typing; ASCII text can be sent directly through ADB without requiring native keyboard focus.
- Preserved optional ADBKeyBoard/clipboard pathways for Chinese and other Unicode text.
- Rotation now disables sensor rotation before locking Android orientation, uses WM command plus OEM compatibility fallback, and checks system rotation policy.
- Auto rotate releases WindowManager's rotation lock and verifies sensor mode.
- Updated status descriptions to distinguish an accepted rotation command from a display that refuses to rotate.
- Added regression tests and a Windows physical-device ADB diagnostic script.


## 0.10.1

- Integrated PR #1 (v0.9.1) into v0.10.x without losing the v0.10.0 device-debug, Unicode, batch-operation, networking, capture-management and update-check capabilities.
- Fixed repeated real-device rotation cropping by tracking native child-window size/position changes and returning to Fit after rotation.
- Rotation switches between natural and 90-degree device orientations, with a busy guard against rapid repeated clicks.
- Unified dark UI selection/hover/focus styles for main and debug windows, including editable ComboBoxes.
- Reduced device polling and UI rebuilding by caching Android metadata and updating device lists only when data changes.
- Moved legacy capture/decode work away from the UI thread with session cancellation guards.
- Added automated Windows regressions for 12 native resize cycles, 6 rotation toggles, metadata refresh on reconnect and inactive dark-theme selections.
- Retained the optional trusted Authenticode signing pipeline; unsigned test builds remain explicitly marked.


## 0.10.0

- Added a Unicode text helper to mirror tabs. scrcpy sessions can copy text to Windows clipboard and paste into Android; optional ADBKeyBoard IME enables Base64 Unicode input for legacy Android.
- Added periodic Android rotation-state sync (including automatic vs locked orientation) and an audio routing diagnostic view.
- Improved session resilience with transient ADB disconnect tolerance and bounded exponential scrcpy restart delays.
- Expanded Device Debug: system/user app package filtering, permission grant/revoke and AppOps management.
- Added Logcat filtering by tag and explicit PID, alongside existing text/priority filters.
- Added network diagnostics for interfaces, routes, DNS, sockets, Wi-Fi and ping.
- Added bugreport export and locally saved shell-command favorites.
- Added batch ADB shell and APK installation across selected devices with per-device results and cancellation.
- Added screenshot/recording history browser with local image preview and file actions.
- Added GitHub Release update checks (manual and optional on startup); no unsigned automatic installer execution.
- Added optional trusted Authenticode signing for the app EXE and installer. Builds are explicitly unsigned without a configured certificate.


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

