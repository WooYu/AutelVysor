# Unicode input and real clipboard acceptance

`adb shell input text` is an ASCII path. Chinese, accented characters and emoji use the optional ADBKeyBoard IME through **Unicode / clipboard setup** in each mirror. The setup window is modeless so you can still focus and operate the mirrored Android screen.

## Install, activate and restore

1. Obtain an organization-approved local APK or build reviewed source from the [ADBKeyBoard project](https://github.com/senzhk/ADBKeyBoard). Verify its provenance and signer using your normal software approval process. Autel Vysor does not download or silently install a keyboard. If policy forbids third-party APKs or changing the input method, this optional input route is unavailable; record the restriction instead of marking a pass.
2. Click **Unicode / clipboard setup**, confirm the device serial, then **Install trusted local APK**. Installation alone does not activate the keyboard. Device policy errors appear in the window.
3. Click **Activate temporarily**. The current default IME is saved durably per device before enabling or selecting `com.android.adbkeyboard/.AdbIME`. If ADBKeyBoard was already active without a saved original, first select your normal keyboard on Android. An unknown original cannot be recovered automatically.
4. Focus an empty, non-password Android test field. Enter `诊断 café 😀 VIN123` or other test text in the setup window and click **Send Unicode and verify Android field**. The app reads the actual field before sending and requires it to be empty. Existing contents (including text already equal to the requested payload) or unavailable before-state readback block the send. A verified result requires exact text equality with an actual UIAutomator readback after sending. Custom widgets, protected fields and unavailable after-state data produce an unverified result. Inspect the field before retrying to avoid duplicate insertion.
5. Click **Restore original keyboard**. Closing the setup window also restores an activation started in that window, while the device is available. Restoration only switches if the current keyboard is still ADBKeyBoard. A later user keyboard selection is preserved. The helper remains installed/enabled; remove or disable it explicitly in Android Settings if desired.

A disconnect, app termination or failed switch/restore leaves recovery in `%LOCALAPPDATA%\AutelVysor\ime-recovery`, keyed by device serial. After restart, the mirror shows a recovery notice. Open setup and click **Restore original keyboard**; restart never silently changes Android's input method. A failed restore retains recovery for retry. If the original IME was removed or disabled, use Android Settings to select an available keyboard. Do not use `ime reset` to claim restoration of the exact original selection.

## What delivery evidence means

The upstream [AdbIME implementation](https://github.com/senzhk/ADBKeyBoard/blob/master/keyboardservice/src/main/java/com/android/adbkeyboard/AdbIME.java), inspected on 2026-10-21, dynamically registers `ADB_INPUT_B64`, decodes UTF-8 Base64, and attempts `commitText` on the current input connection. It does not return a receiver acknowledgement or report the commit result. Consequently `Broadcast completed: result=0` proves neither delivery nor insertion.

Autel Vysor validates the installed package/IME, active input method and registered receiver associated with that package before sending a package-scoped broadcast. A missing receiver or inaccessible diagnostic blocks the send. Receiver presence is a prerequisite, not an acknowledgement. A successful exact Android field readback is the insertion evidence; an unavailable or different readback remains **NOT verified**. Supporting a true receiver acknowledgement would require an independently reviewed modified keyboard protocol, which is not bundled or claimed here.

## Real bidirectional clipboard acceptance

Use a disposable, standard non-password EditText in an approved Android test fixture, preferably `tests/AndroidFixture`. Do not use customer or vehicle data. Keep a modern scrcpy mirror running with clipboard sync enabled. The Android 4.4 compatibility backend has no scrcpy clipboard protocol; IME input is separately testable on supported/approved keyboards. Android policy, foreground clipboard restrictions or ROM behavior may prevent clipboard sync. Record blocked or failed outcomes honestly.

The setup window contains explicit actions for both directions and does not poll the PC clipboard:

1. **PC → Android:** click **Copy PC sample**. The sample contains CJK, accents, emoji and a fresh suffix. Click an empty fixture field inside the scrcpy mirror and press **Ctrl+V**. Click **Verify PC → Android**. PASS requires the exact generated sample in the actual focused Android field, read via UIAutomator. Merely setting the Windows clipboard is not a pass.
2. **Android → PC:** enter different nonempty test text on Android and focus its fixture field. Click **Capture Android source + clear PC clipboard**. This reads the actual Android field and replaces the Windows clipboard with a fresh waiting marker. Select and copy the full Android source using Android's **Copy** action or scrcpy **Ctrl+C**. Click **Verify Android → PC**. PASS requires the Windows clipboard to equal the captured Android source, replacing the marker. A comparison to a prefilled Windows value is not used.
3. Record each direction independently, including device model, serial, Android/API level, scrcpy version, keyboard version/signer, expected and actual test text, screenshots of the field/results, and any policy restriction. Repeat with an ASCII VIN, CJK/accented text, emoji, spaces/punctuation and multiline text in an appropriate multiline field. Record unsupported field behavior.
4. Confirm restore from temporary activation, restore after a disconnect/reconnect, restart recovery, and preservation of a different keyboard selected manually after activation. Observe the actual Android keyboard selection after each action.

Clipboard PASS labels are results of these operator-driven checks in the current setup window, not a release-wide certification or automated scrcpy protocol test. The portable regression runner below does not run scrcpy or emulate its clipboard protocol.

## Portable regression checks

From a supported .NET 8 environment:

```text
dotnet run --project tests/UnicodeRegressionTests/UnicodeRegressionTests.csproj -c Release
```

The tests exercise the real recovery, receiver-validation and encoding/readback service using a simulated ADB runner. They cover missing/wrong receivers, ambiguous broadcast success without text insertion, already-matching and other nonempty field contents, Unicode UTF-8 Base64, original-IME persistence before switching, repeat activation, restart recovery, device isolation, safe preservation of a later user selection and failed activation. They do not establish APK trust, Android IME behavior, UI rendering, physical-device Unicode insertion or real clipboard transfer.

Physical-device and Windows GUI acceptance remain required. No hardware PASS is asserted by this change.
