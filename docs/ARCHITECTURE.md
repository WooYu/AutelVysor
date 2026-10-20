# Autel Vysor architecture (v0.5.0)

## Runtime flow

```text
MainWindow
  |
  +-- AndroidBridge ------------------------------ adb.exe
  |     +-- device discovery / properties
  |     +-- Wi-Fi pairing/connect
  |     +-- install / push / pull
  |     +-- screencap / input (API 19/20)
  |
  +-- MirrorSessionControl (one per device tab)
        |
        +-- API 19/20 -> ADB compatibility renderer
        |
        +-- API 21+ -> ScrcpyService
                         |
                         +-- scrcpy.exe process
                         +-- NativeWindowLocator finds SDL HWND
                         +-- NativeWindowHost reparents HWND into WPF
```

## Why the backend is split at API 21

Current scrcpy releases require Android API 21 or newer. Android 4.4 is API 19 (4.4W is API 20), therefore old devices need a different capture/control path.

## Embedded scrcpy design

The application does not copy or modify scrcpy source. It launches the official Windows binary and reparents its native window into a WPF `HwndHost`. scrcpy continues to handle its video decoder and input protocol. This keeps the first implementation smaller and preserves upstream behavior while providing an integrated Autel Vysor workspace.

A future native protocol implementation could replace this adapter if tighter renderer integration is required.

## Android 4.4 path

The compatibility backend repeatedly calls `adb exec-out screencap -p`. If old adbd does not support reliable `exec-out`, it falls back to device-side PNG + `adb pull`. Mouse input is mapped to device pixels and sent using `adb shell input tap/swipe`; supported keyboard actions use `input keyevent` / `input text`.

This design emphasizes broad compatibility, not frame rate.


## Diagnostics and field support

The main window can copy a diagnostic snapshot containing application version, OS/process architecture, ADB version, scrcpy version and the currently discovered device API/ABI/backend information. Unhandled WPF/task exceptions are also appended to `%LOCALAPPDATA%\Autel\AutelVysor\AutelVysor.log` for field troubleshooting.

## Android 4.4 file-drop behavior

The compatibility renderer accepts file drops. `.apk` files are installed with `adb install -r`; other files are pushed to `/sdcard/Download/`. Modern scrcpy sessions retain scrcpy's native drag-and-drop behavior inside its embedded SDL child window.
