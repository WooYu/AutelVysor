# Autel Vysor

Windows Android mirror/control client for **Android 4.4 (API 19) and newer**.

Current source version: **0.10.11**

## Desktop interaction improvements

- Startup and rediscovery select the first online supported device while preserving an existing selection. Mirror startup and tab activation automatically focus the keyboard when the application is active.
- Screenshot buttons show capture/saved/failure states; recordings show a red recording state, unexpected exits and finalization results. Capture actions use the active mirror device when one is open. The saved path stays visible beside the capture tools.
- Captures shows image-only Fit/Actual size controls for screenshots; video playback controls appear only for recordings.
- Device Debug Files adds Back/Forward, storage/Downloads/DCIM shortcuts, name filter, sort order, context actions, rename and folder/multiple-item download. Enter opens folders, Backspace goes up, Alt+Left/Right navigates history, F5 refreshes, F2 fills the rename field, and Delete asks for confirmation. Refresh preserves selected items in the same directory.
- App search matches both package identifiers and readable APK application labels, with selectable results and a match count. Names load in the background with progress and are reused during the current application session. Unreadable or split-only labels fall back to package search; no Android helper is installed.
- Debug operations show a busy indicator; repeated actions and concurrent shell submissions are guarded. Closing the window cancels name indexing, file operations and shell commands. File shell paths preserve spaces, apostrophes and Unicode, and operation errors retain Android exit diagnostics.

See [validation details and remaining device checks](docs/USABILITY_FIXES.md).

## Compatibility

| Android | Backend | Status |
|---|---|---|
| Android 4.4 / API 19 | DeviceFarmer minicap acceleration, automatic ADB screenshot fallback | Implemented in v0.6.0; hardware validation still required |
| Android 4.4W / API 20 | Built-in ADB compatibility mirror | Implemented; Android Wear may have input limitations |
| Android 5.0+ / API 21+ | Official scrcpy Windows toolchain | Implemented |
| Android 11+ | ADB wireless pairing + scrcpy | Implemented |

scrcpy itself requires API 21+, therefore API 19/20 never attempts to start scrcpy.

## Company ADB and PowerShell physical testing

For company PCs that allow only an existing approved adb.exe and Windows PowerShell,
run [scripts/run-company-adb-e2e.ps1](scripts/run-company-adb-e2e.ps1) directly.
It does NOT require or download a GitHub Runner, scrcpy executable, emulator or
Android fixture APK. It generates local HTML, JSON and CSV real-device reports.

ADB backend test success is **not** equivalent to Windows UI/scrcpy validation.
Features blocked by company executable policy are explicitly marked BLOCKED_POLICY.
See [Chinese usage guide](docs/COMPANY_ADB_ONLY_TESTING.md).

## v0.10.6 desktop usability candidate

Uses left-side controls and full-height Fit/fullscreen, right-side capture playback, automatic app details, navigable files and imported/retained colored logs with all/filtered exports. Includes a blue phone/display icon for the executable, windows, installer and shortcuts. See [icon asset details](docs/APP_ICON.md).

## v0.10.5 input / readability candidate

- Android 7+: **Paste text** explicitly sends PC-composed Chinese/Unicode through scrcpy clipboard into the focused phone field. ADBKeyBoard remains an optional advanced channel. A submitted paste is not an assertion of Android insertion.
- **Keyboard: Auto** selects UHID on Android 9+; **Compatible** retains SDK raw key events. Hardware typing uses the phone's input method/layout; use Paste text for text composed by the PC IME.
- New **125% / 150% / 200% / 300%** viewing magnification and edge scrollbars; **Fit** resets magnification/pan. Zoom enlarges the view, rather than changing Android density or fonts. Select High mirror quality and restart if a larger picture needs more source detail.
- Native viewport size is reconciled after WPF layout changes, fixing a reproduced bottom-cropping case. Multiline diagnostics stay in a compact status line with a full tooltip.
- See [input and zoom guide](docs/INPUT_AND_ZOOM.md). Real device text insertion and visual acceptance still require a connected phone.

## v0.10.4 correction candidate

- **AV-006:** explicit BOM-free UTF-8 for redirected command output/error/input and live logcat; raw binary capture remains byte-preserving.
- Shell UI/batch output separates stdout/stderr and displays process/remote exit status. Batch operations do not count unverified remote status as success.
- DNS diagnostics query legacy properties and modern resolver/connectivity services separately, distinguish unsupported/permission/query failures, and retain command evidence. Reported DNS configuration is not a successful hostname-resolution test.
- **Unicode setup:** explicit trusted-local-APK install, temporary ADBKeyBoard activation, saved original IME recovery, and exact focused-field readback. Broadcast completion alone is not insertion proof. The setup window provides action-driven PC→Android and Android→PC clipboard acceptance; physical acceptance remains pending.
- **Public release verification:** signed and unsigned packages can be published. Both require pinned build provenance, hashes and exact ZIP executable bytes. Signed releases additionally require trusted timestamped app and installer signatures; unsigned releases are explicitly labelled.
- `FileDescription=Autel` remains unchanged. See [signing setup](docs/SIGNING.md) and [Unicode/clipboard acceptance](docs/UNICODE_AND_CLIPBOARD.md). A trusted certificate is optional for publication and required only for signed releases.

## v0.10.3 report corrections

- Shell, device details, network diagnostics and batch shell commands preserve the complete remote command (AV-001).
- SDK raw key events avoid the embedded SDL text-event dependency for ordinary keyboard characters (AV-004). Chinese/Windows IME still requires physical acceptance; explicit clipboard/optional Android IME helpers remain available.
- Rotation status and orientation commands share actual display-0 telemetry, with legacy input diagnostics as fallback. Unavailable telemetry is reported explicitly; user_rotation is never substituted for the live angle (AV-005).
- Recording has a per-device duplicate guard and **Stop recordings**. App exit waits for owned recorders to close and finalize MP4; forced termination is reported as a possible incomplete recording (AV-002/003).
- These are code corrections with automated regression coverage, not a declaration that all 18 hardware features passed. See [report follow-up](docs/V0103_REPORT_FOLLOWUP.md).
- Unsigned builds remain unsigned until an actual trusted company certificate is configured. FileDescription remains **Autel**.

## v0.10.2 keyboard and rotation fixes

- **Focus keyboard** moves keyboard focus into the embedded scrcpy window, which is a separate Win32 process reparented into WPF.
- **Send text** sends ASCII/VIN text through ADB to the focused Android field. For Chinese/Unicode, use the modern scrcpy clipboard or explicitly install/select the optional ADBKeyBoard IME.
- **Switch orientation** now disables sensor rotation before applying the real Android WindowManager rotation lock and checks the WM policy; apps or vendor ROMs may still enforce an orientation lock.
- **Auto rotate** releases the WM rotation lock, re-enables sensor mode, and prompts the user to physically tilt the device (the phone may suppress sensors while its display is off).
- **Windows hardware diagnostics:** Run `scripts/diagnose-connected-devices.ps1` on a Windows PC connected to the phones, optionally with `-TestRotation`. Diagnostics remain local and do not automatically upload personal device data.
- CI simulates ADB and Win32 host behavior but cannot claim direct physical device testing.

## v0.10.1 integration

- Merged the v0.9.1 rotation-cropping and UI-performance branch (PR #1) into the v0.10.x development line.
- Embedded scrcpy child HWND is kept within its WPF host as the device rotates, using guarded, asynchronous resizing.
- Device rotation uses a guarded portrait/landscape toggle rather than endlessly rotating the computer image; Fit view is restored.
- Shared dark hover/focus/selection templates improve the device list, active/inactive tabs, editable dropdowns and debug controls.
- Device polling reuses stable metadata and skips redundant list rebuilds; legacy capture/decode is moved away from the WPF UI thread.
- New Windows regression checks cover repeated resize, orientation toggles, metadata cache invalidation and selection rendering.
- Existing v0.10.0 unicode, batch, network, diagnostics, capture-management, update-check and optional signing features are retained.

## v0.10.0 additions

- Unicode helper in mirror tabs: copy Chinese/VIN/URL text and paste into Android through scrcpy; optional ADBKeyBoard IME can send Base64 Unicode broadcasts to older Android. This IME must be installed and selected by the user; no keyboard is silently installed or activated.
- Mirror reliability: reconnect status, short ADB interruption tolerance, and bounded retry backoff.
- Rotation display synchronizes the Android rotation value and auto/lock mode. Audio route view shows configured scrcpy output policy and limited Android diagnostics; a Bluetooth audio playback test remains necessary.
- Device Debug: permission grant/revoke, AppOps, installed-app filter (user/system/all + package search), logcat tag/PID filters, network tools and ping, bugreport export, saved ADB shell favorites.
- Multi-device batch APK install and ADB shell commands with confirmation and per-device status.
- Capture history for screenshots and recordings, with local image preview.
- Optional startup/interactive GitHub Release checks. The application **never silently downloads or executes installer binaries**.
- Optional trusted Authenticode signing infrastructure, documented in docs/SIGNING.md.

**Limitations:** Stock ADB package lists do not include display names. Name search uses background APK label indexing; labels supplied only by split APKs, overlays or inaccessible resources may be unavailable. AppOps and permission changes may be denied by Android or vendor restrictions. True Bluetooth audio route verification, Android 4.4 Unicode IME compatibility, and physical USB/Wi-Fi recovery still require hardware validation. Automatic installation of unsigned updates is intentionally not enabled.

## v0.9.0 additions

- Mirror workspace controls: hide/show sidebar, F11 full screen, Fit and 1:1 display modes.
- Device Debug **Shell** tab with command history (Enter to run, Up/Down for history).
- Device Debug **Files** tab for browsing Android paths, upload, download, new folders and confirmed deletion.
- Logcat filters for text/tag/message, minimum priority and selected-app PID, plus pause.
- Rich device details covering build properties, display, battery, memory, storage and network.
- Existing App / Processes / Logs / Device tooling remains available.

## v0.8.1 fixes

- Reworked the left sidebar into a readable dark theme with explicit CheckBox/ComboBox/Expander colors.
- Made the lower Wi-Fi / mirror / file settings area vertically scrollable so Files / utilities is never clipped.
- Changed Rotate from a scrcpy display-only transform to real Android device rotation through ADB; added Auto rotate.
- Added "Keep phone / Bluetooth audio" behavior. Android 13+ uses scrcpy audio duplication so PC and device/Bluetooth can play simultaneously. Android 11/12 prioritize device/Bluetooth audio when this option is enabled.
- Kept Android 5.0+ physical-screen-off behavior during mirroring.

## v0.8.0 additions

- Added a dedicated **Device debug** window instead of continuing to overload the mirror sidebar.
- App tab: installed-package list, version/versionCode, PID, minSdk/targetSdk, install/update time, data directory, permissions and full `dumpsys package`.
- App actions: force-stop, start, restart, clear data, install APK and uninstall with optional `-k` data retention.
- Process tab: device process list plus selected-package PID.
- Logs tab: live logcat, stop, snapshot, bounded in-memory view and logcat export.
- Device tab: reboot the selected Android device and restart the Windows ADB server with impact warnings.
- Destructive actions require explicit confirmation.

## v0.7.0 additions

- Android 5.0+ physical screen turns off by default while mirroring; the PC image/control session remains active.
- Added per-session clockwise 90° display rotation for scrcpy-backed devices.
- Redesigned connected devices into compact cards and removed the horizontal device-list scrollbar.

## v0.6.0 additions

- Added optional Android 4.4/API 19 minicap JPEG-stream acceleration for armeabi-v7a and x86, with automatic ADB screenshot fallback.
- Added Smooth / Standard / High scrcpy quality presets.
- Added audio forwarding toggle; scrcpy handles unsupported pre-Android-11 devices by continuing video-only.
- Added clipboard autosync toggle.
- Added a persisted legacy acceleration preference.
- Installer now requires Windows 10 or newer.
- Version advanced to 0.6.0 while keeping `FileDescription=Autel` and `ProductName=Autel Vysor`.

## Existing features

- Embedded Android mirror tabs inside the Autel Vysor WPF window.
- Multiple devices can be mirrored at the same time.
- USB device discovery and authorization status.
- Automatic Android version/API/ABI detection.
- Android 4.4 compatibility mirror using `adb screencap` + Android `input` commands.
- Mouse click/drag and keyboard input for the Android 4.4 backend.
- Back / Home / Recents / Volume / Power controls for all backends.
- ADB over TCP/IP connection/disconnection, with best-effort Wi-Fi IP detection after USB → TCP/IP.
- Android 11+ wireless debugging pairing.
- Remembered Wi-Fi endpoints and automatic reconnect.
- APK installation.
- Push/pull files. Android 4.4 compatibility tabs accept drag-and-drop APKs and files.
- Screenshot capture.
- Background MP4 recording for Android 5.0+ through scrcpy.
- Windows x64 self-contained publish script.
- Portable ZIP packaging.
- Inno Setup installer configuration.
- Build-time validation that `AutelVysor.exe` has **FileDescription = Autel**.
- GitHub Actions Windows build workflow.
- Packaged EXE `--self-test` gate validates x64 execution plus bundled ADB/scrcpy startup before release packaging.
- One-click diagnostic report copied to the clipboard (app/OS/ADB/scrcpy/device information).
- Local crash log under `%LOCALAPPDATA%\Autel\AutelVysor\AutelVysor.log`.

## Repository layout

```text
src/AutelVysor/
  Controls/
    MirrorSessionControl.*   unified device session UI
    NativeWindowHost.cs      embeds the scrcpy native window in WPF
  Models/
  Services/
  MainWindow.*

scripts/
  prepare-tools.ps1          downloads official latest scrcpy Windows x64 release
  build-windows.ps1          builds + validates AutelVysor.exe
  build-release.ps1          ZIP + optional Inno Setup installer

installer/
  AutelVysor.iss
```

## Development requirements

- Windows 10/11 x64
- .NET 8 SDK
- USB debugging enabled on the Android device
- Internet access the first time `prepare-tools.ps1` downloads the official scrcpy release
- Optional: Inno Setup 6 to create `AutelVysor-Setup.exe`

## Prepare Android tools

From a PowerShell terminal in the project root:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\prepare-tools.ps1
```

The script obtains the current official scrcpy Windows x64 release from the Genymobile GitHub release API. The release contains `scrcpy.exe`, its runtime libraries and `adb.exe`. The prepared files are placed under `src\AutelVysor\tools` and are copied into build output.

## Build Windows executable

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-windows.ps1
```

Expected output:

```text
dist\AutelVysor\AutelVysor.exe
```

The build also runs `AutelVysor.exe --self-test`; the release fails if the packaged x64 EXE cannot start its bundled ADB/scrcpy tools.

The build fails if the executable metadata does not contain:

```text
FileDescription : Autel
ProductName     : Autel Vysor
FileVersion     : 0.10.6.0
```

## Build portable ZIP and installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1 -RequireSigning
```

Expected artifacts after a successfully signed build:

```text
dist\AutelVysor-v0.10.6-windows-x64.zip
dist\AutelVysor-Setup.exe           # when Inno Setup 6 is installed
```

The installer is also configured with `VersionInfoDescription=Autel`. For deliberately unsigned internal testing, use `scripts/build-release.ps1 -AllowUnsignedInternal`. Public releases accept this explicit unsigned mode or `-RequireSigning` with a trusted certificate. Both require an independently verified manifest. Signed builds never silently downgrade to unsigned.

## Android 4.4 behavior

API 19 first tries the optional DeviceFarmer minicap backend when enabled and when matching packaged assets are available:

```text
minicap -> JPEG socket stream -> WPF Image
mouse/keyboard -> adb shell input -> Android
```

If the minicap compatibility probe or stream fails, Autel Vysor automatically falls back to:

```text
ADB screencap -> PNG -> WPF Image
```

API 20 remains on the conservative ADB fallback because the pinned upstream minicap tree does not provide an Android-20 shared library. Minicap relies on private Android APIs, so physical-device validation remains mandatory.

## Multi-device / reconnect behavior

- Select several entries and choose **Open selected**, or choose **Open all**.
- Each Android device gets its own tab and its own mirror process/backend.
- USB sessions are kept in the workspace when a device disconnects and resume when the same serial returns.
- Wi-Fi endpoints connected by the user are saved to `%LOCALAPPDATA%\Autel\AutelVysor\settings.json`.
- When **Auto reconnect Wi-Fi** is enabled, remembered endpoints are retried with throttling.

## Current validation status

The Windows x64 GitHub Actions release gate performs self-contained publish, executable self-test, metadata checks, portable ZIP and Inno Setup installer packaging. A green v0.9.0 run validates the packaged Windows build only; Windows 10/11 desktop hardware and Android 4.4+ physical devices still require the hardware matrix below before a Beta compatibility claim.

## Third-party components

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

