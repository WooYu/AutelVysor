# Autel Vysor

Windows Android mirror/control client for **Android 4.4 (API 19) and newer**.

Current source version: **0.5.0**.

## Compatibility

| Android | Backend | Status |
|---|---|---|
| Android 4.4 / API 19 | Built-in ADB compatibility mirror | Implemented (low frame-rate fallback) |
| Android 4.4W / API 20 | Built-in ADB compatibility mirror | Implemented; Android Wear may have input limitations |
| Android 5.0+ / API 21+ | Official scrcpy Windows toolchain | Implemented |
| Android 11+ | ADB wireless pairing + scrcpy | Implemented |

scrcpy itself requires API 21+, therefore API 19/20 never attempts to start scrcpy.

## v0.5.0 features

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
FileVersion     : 0.5.0.0
```

## Build portable ZIP and installer

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
```

Expected artifacts:

```text
dist\AutelVysor-v0.5.0-windows-x64.zip
dist\AutelVysor-Setup.exe           # when Inno Setup 6 is installed
```

The installer is also configured with `VersionInfoDescription=Autel`.

## Android 4.4 behavior

API 19/20 uses the built-in compatibility backend:

```text
ADB screencap -> PNG -> WPF Image
mouse/keyboard -> adb shell input -> Android
```

This is intentionally a reliability-first fallback. It is much slower than the H.264/MediaCodec path used by scrcpy. A minicap-based acceleration backend can be added later, but it must be treated as device-dependent because minicap relies on private Android APIs.

## Multi-device / reconnect behavior

- Select several entries and choose **Open selected**, or choose **Open all**.
- Each Android device gets its own tab.
- Device state is refreshed every 2.5 seconds.
- A disconnected session stays open and shows a reconnect state.
- When the same serial becomes online again, its mirror is restarted.
- TCP/IP endpoints are remembered in `%LOCALAPPDATA%\Autel\AutelVysor\settings.json`.
- Remembered endpoints are retried periodically after the client restarts.

## Verification status

The source project and packaging files have been structurally checked in a non-Windows environment. A real WPF publish requires Windows because the project targets `net8.0-windows` and hosts Win32 native windows.

The authoritative release gate is `scripts/build-windows.ps1`: it performs a self-contained x64 publish, checks the EXE's Windows version resource and executes the packaged `--self-test`.

See `docs/TEST_PLAN.md` for the Windows/Android validation matrix.
