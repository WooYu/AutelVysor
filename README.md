# Autel Vysor

Windows Android mirror/control client for **Android 4.4 (API 19) and newer**.

Current source version: **0.6.0**.

## Compatibility

| Android | Backend | Status |
|---|---|---|
| Android 4.4 / API 19 | DeviceFarmer minicap acceleration, automatic ADB screenshot fallback | Implemented in v0.6.0; hardware validation still required |
| Android 4.4W / API 20 | Built-in ADB compatibility mirror | Implemented; Android Wear may have input limitations |
| Android 5.0+ / API 21+ | Official scrcpy Windows toolchain | Implemented |
| Android 11+ | ADB wireless pairing + scrcpy | Implemented |

scrcpy itself requires API 21+, therefore API 19/20 never attempts to start scrcpy.

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
FileVersion     : 0.6.0.0
```

## Build portable ZIP and installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Expected artifacts:

```text
dist\AutelVysor-v0.6.0-windows-x64.zip
dist\AutelVysor-Setup.exe           # when Inno Setup 6 is installed
```

The installer is also configured with `VersionInfoDescription=Autel`.

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

The Windows x64 GitHub Actions release gate performs self-contained publish, executable self-test, metadata checks, portable ZIP and Inno Setup installer packaging. A green v0.6.0 run validates the packaged Windows build only; Windows 10/11 desktop hardware and Android 4.4+ physical devices still require the hardware matrix below before a Beta compatibility claim.

## Third-party components

See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).
