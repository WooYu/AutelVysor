# Windows / Android hardware validation plan

## Build gate

1. Run `scripts/build-release.ps1` on Windows 10/11 x64.
2. Verify `dist/AutelVysor/AutelVysor.exe` starts without a .NET runtime installation (self-contained publish).
3. Verify Properties -> Details:
   - File description: `Autel`
   - Product name: `Autel Vysor`
   - File version: `0.6.0.0`
4. Verify the installer Properties -> Details also reports file description `Autel`.

## Device matrix

Recommended minimum real-device coverage:

- Android 4.4 / API 19, ARMv7
- Android 5.x / API 21-22
- Android 8.x / API 26-27
- Android 11 / API 30 (wireless pairing)
- Android 13 / API 33
- Android 15 / API 35
- Android 16 / API 36

For each device verify discovery, mirror, click, drag, keyboard, Home/Back/Recents, screenshot, file push/pull and reconnect.

## Multi-device

1. Connect at least one USB and one Wi-Fi device.
2. Select both and open mirror tabs.
3. Interact with each device independently.
4. Disconnect one device; verify the other session remains usable.
5. Reconnect the removed device; verify its existing tab restarts rather than creating a duplicate.

## Android 4.4

1. Test API 19 armeabi-v7a with **Prefer Android 4.4 accelerated capture** enabled.
2. Confirm compatible devices report `minicap accelerated`; confirm scrcpy is never launched.
3. Rotate portrait/landscape and verify video orientation plus tap/swipe coordinate mapping.
4. Disable acceleration and confirm the same device falls back to `ADB compatibility`.
5. Force minicap startup failure and confirm automatic fallback without closing the tab.
6. Test an API 19 x86 device/emulator only if minicap supports that physical target; upstream explicitly does not promise emulator support.
7. Test API 20 separately and confirm it remains on ADB fallback.
8. Confirm Back/Home/Power and basic ASCII keyboard input.
9. Measure minicap FPS/latency and ADB fallback frame time; record device-specific limitations.

## Wi-Fi persistence

1. Connect to `IP:port` once.
2. Restart Autel Vysor.
3. With Auto reconnect enabled, verify the endpoint reconnects.
4. Click Disconnect and restart the app; verify the explicitly removed endpoint is no longer retried.


## v0.6 diagnostics / legacy usability

1. On an Android 4.4 USB device choose **USB → TCP/IP**; verify the app fills a detected Wi-Fi address when the firmware exposes one.
2. Drop an APK onto the Android 4.4 mirror area and verify `adb install -r` succeeds.
3. Drop a normal file onto the Android 4.4 mirror area and verify it appears under `/sdcard/Download/`.
4. Right-click the Android 4.4 mirror and verify it performs Back.
5. Verify Volume +/- toolbar actions on both legacy and scrcpy sessions.
6. Choose **Copy diagnostics** and verify app version, OS, adb/scrcpy versions and device API/ABI are present.
7. Force a test exception in a debug build and verify `%LOCALAPPDATA%\Autel\AutelVysor\AutelVysor.log` is created.
# 0.9.1 rotation and responsiveness regressions

Run `dotnet run --project tests/RegressionTests/RegressionTests.csproj -c Release` on Windows.
The runner uses real Win32 windows and real subprocess calls to a test ADB endpoint.
It checks 12 consecutive child-window resize events, six orientation toggles, and metadata refresh on reconnect.

On physical devices: repeat horizontal/vertical switching at least 20 times, including rapid clicks, Fit/1:1, resized windows, 100%/150%/200% DPI, and sensor auto-rotation.
Verify the complete display and touch alignment. Check natural-landscape tablets and an app that locks orientation.
Confirm selected tabs, device rows and dropdowns stay readable when focus moves to the mirror.
Verify four connected devices with high-quality mirroring and Android 4.4 capture, and check Bluetooth audio remains available.
These hardware and visual checks are separate from automated Windows regression coverage.
