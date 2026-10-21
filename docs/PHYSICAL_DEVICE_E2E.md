# Real physical Android device E2E

There are TWO distinct test gates:

1. GitHub hosted Windows CI: compiles WPF and tests mocked ADB / native-window behavior. NOT real USB device testing.
2. Real Windows PC with authorized physical Android devices: executes scripts/run-physical-device-tests.ps1, and optionally scripts/run-physical-gui-tests.ps1 in an interactive desktop.

A hosted GitHub runner CANNOT see USB cables connected to the user's own Windows workstation. No all-PASS physical-device claim should be made until physical reports exist.

## Workstation

- Use an idle lab workstation and test devices; avoid active customer diagnosis or production apps.
- Connect Android 4.4/API 19, Android 5+, Android 11 and Android 13+/16 devices where available; enable USB debugging, approve the PC, confirm adb devices -l reports device.
- Use a dedicated Windows account, with SDK platform-tools/adb.exe and the v0.10.2 AutelVysor.exe installed or unzipped.
- The scripts refuse emulator- serials and ro.kernel.qemu=1 as real-device evidence.
- Keep the Windows desktop logged in and unlocked for GUI automation. Do not run WPF GUI tests from a session-0 Windows service.

## Connect local hardware to GitHub Actions

Repository Settings > Actions > Runners > New self-hosted runner.
Register the PC to WooYu/AutelVysor and give it the label autel-physical.
Use a restricted lab account and repository permissions.
A self-hosted runner executes repository workflows with access to real
attached devices: do NOT allow untrusted PR code, do not disclose runner
registration tokens, and keep sensitive production devices unplugged.
Only launch the Physical Android Devices workflow manually after confirming
the lab machine and devices are idle.

## One-click safe ADB physical tests

Open PowerShell from the repository root and run:

    powershell -ExecutionPolicy Bypass -File .\scripts\run-physical-device-tests.ps1 -AdbPath "C:\Android\platform-tools\adb.exe"

To exercise isolated app control/keyboard/grant/revoke, build the disposable
fixture from tests/AndroidFixture with Android SDK + Gradle 8.10.2 and pass:

    -FixtureApk ".\tests\AndroidFixture\app\build\outputs\apk\debug\app-debug.apk"

By default only a random /sdcard/Download/autel-vysor-physical-* directory
is created/deleted; the test does not clear or uninstall business apps.
If a fixture app is already installed, the harness refuses to overwrite it.

Other flags are explicit opt-ins:
- IncludeRotation: rotate the real Android display temporarily and restore sensor/WM settings.
- IncludeBugreport: export possibly sensitive bugreport ZIP.
- IncludeReboot: reboot each real device, then wait for authorized reconnect.
- IncludeAdbRestart: restart the host's shared ADB service.
- ScrcpyPath: record a short real Android video stream on API 21+.

## Real WPF UI + Android verification

On an interactive desktop (not a Windows service), run:

    powershell -ExecutionPolicy Bypass -File .\scripts\run-physical-gui-tests.ps1 -AppExePath "C:\Program Files\Autel Vysor\AutelVysor.exe" -DeviceSerial "YOUR_ADB_SERIAL" -AdbPath "C:\Android\platform-tools\adb.exe" -FixtureApk ".\tests\AndroidFixture\app\build\outputs\apk\debug\app-debug.apk"

This script launches the actual Autel Vysor WPF process, selects an authorized
device, opens the mirror, clicks Fit/1:1, Switch orientation, Auto rotate,
Focus keyboard, Send text, sidebar/full-screen, Device Debug tabs,
Captures and Batch operations. The fixture verifies Android EditText input
received through actual ADB and Windows keystrokes, rather than only
asserting that the sender returned success.

## Test report truthfulness

Reports have PASS, FAIL, SKIP and EXTERNAL_VALIDATION states.
ADB checks include discovery, properties, packages, processes, filesystem
push/pull/checksum/deletion, screenshot, logcat, network, rotation,
audio dumpsys and optional fixture AppOps/permissions/install/uninstall.
Do not call these WPF UI tests; GUI automation must run separately.

External hardware is required to verify actual headset audibility
(microphone or loopback), automatic tilt rotation (robotic actuator),
physical USB unplug/replug (USB relay), and visual quality assessment.
No ADB command can prove these happened. These checks must remain
EXTERNAL_VALIDATION until evidence is captured.

Evidence is stored locally in test-results/physical-e2e-*/summary.json
and results.csv, with per-device screenshots and diagnostic files.
GUI results are written to gui-results.json/csv. Screenshots, serials and
bugreports may be sensitive; review before sharing. Never auto-upload
unredacted physical-device reports to public GitHub artifacts.

Only declare comprehensive real-device PASS after reviewing reports
from EVERY required physical Android variant; cloud CI is insufficient.

The CI validation workflow intentionally reports only script syntax and fixture APK build status; a green result must never be presented as proof of connected physical phones.
