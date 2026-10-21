# Windows / Android hardware validation plan

## Build gate

1. Run `scripts/build-release.ps1` on Windows 10/11 x64.
2. Verify `dist/AutelVysor/AutelVysor.exe` starts without a .NET runtime installation (self-contained publish).
3. Verify Properties -> Details:
   - File description: `Autel`
   - Product name: `Autel Vysor`
   - File version: `0.10.1.0`
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

## v0.10.0 functional acceptance

- [ ] Android 4.4: installed/selected ADBKeyBoard IME inserts UTF-8 Chinese; if IME absent, show a clear instruction rather than claiming success.
- [ ] Android 13+/16: scrcpy clipboard paste handles Chinese/VIN/URLs without changing device IME.
- [ ] Physically rotate Android; rotation status updates within 8-12 seconds, and Auto/Locked is accurate.
- [ ] Bluetooth headset audio + PC duplicated audio on Android 13+ using a real playback sample, not only dumpsys indicators.
- [ ] Repeated USB disconnect/reconnect and ADB server restart recover without stale tabs or runaway scrcpy processes.
- [ ] Device Debug App filter: all/user/system + package search; test grant/revoke/appops accepted and rejected operations.
- [ ] Logcat Tag and PID filters match threadtime columns and pause limits in-memory queue size.
- [ ] Network: compare IP/DNS/route/ping/port output to Android shell tools.
- [ ] Bugreport export with timeout, user cancellation, and no silent upload.
- [ ] Batch app install and shell command with multiple physical devices, one failed target, and cancellation.
- [ ] Screenshot and recording history opens and previews local files.
- [ ] GitHub update checker links only to official repository releases; does not run unsigned installers.
- [ ] CI signed build using real certificate: verify Authenticode on EXE + setup; unsigned build clearly labeled.


## v0.10.1 integrated rotation and responsiveness regressions

Run `dotnet run --project tests/RegressionTests/RegressionTests.csproj -c Release` on Windows.
The automated checks exercise 12 consecutive native-window resize events, six orientation toggles,
a mocked ADB process endpoint with metadata caching and reconnect invalidation, and dark selected styles.

On physical devices: perform at least 20 natural/landscape orientation changes (including rapid clicks),
check Fit / 1:1, resized windows, 100% / 150% / 200% DPI, and sensor auto-rotation.
Verify the actual screen orientation changes and touch mapping remains correct.
Confirm selected tabs, device rows, edit/dropdowns and buttons remain readable when focus moves to the mirror.
Check multi-device performance and Android 4.4 capture, and confirm Bluetooth headset audio remains available.
These physical-device checks are distinct from the Windows CI suite.

## v0.10.2 PC keyboard and real-device orientation

- Windows regression: validate native HWND keyboard focus, ASCII ADB input, rotation lock and auto-rotate restoration.
- After tapping an Android input field, click **Focus keyboard** and type on the PC keyboard in the scrcpy view. Check upper/lowercase, VIN, numbers, punctuation, Enter, Backspace and Ctrl+V.
- Use **Send text** to send a short ASCII VIN with ADB; verify it arrives in the focused Android field. For Chinese use scrcpy clipboard paste or the optional installed/selected ADBKeyBoard IME; do not claim universal Unicode typing with `adb shell input text`.
- Click **Switch orientation** and check the actual Android display rotation (not a capture-only transform). A device app may refuse requested rotation; the status line must say so instead of claiming success.
- Click **Auto rotate**, physically tilt the device and verify the Android sensor mode is restored. Android devices may suspend rotation sensors when their physical display is off.
- Run `powershell -ExecutionPolicy Bypass -File scripts/diagnose-connected-devices.ps1` locally on Windows with authorized USB devices. The default mode only reads state.
- Explicit `-TestRotation` exercises lock/unlock on connected devices and restores the previous settings in a finally block. Do not run on devices actively performing diagnostic operations.
- Physical tests are not performed by cloud Windows CI. Test real Android 4.4, Android 11 and Android 13+/16 hardware separately.
