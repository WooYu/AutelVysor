# Third-party notices

Autel Vysor is designed to use the official **scrcpy** Windows distribution for Android 5.0+ mirroring/control.

## scrcpy

- Project: Genymobile/scrcpy
- Source: https://github.com/Genymobile/scrcpy
- License: Apache License 2.0

`scripts/prepare-tools.ps1` downloads the current official Windows x64 release directly from the project's GitHub release endpoint instead of embedding a third-party binary in this source archive. Keep the license and notice files supplied with that distribution when redistributing a built package.

## Android Debug Bridge (ADB)

The official scrcpy Windows distribution currently provides the ADB executable used by this project. Any redistribution must comply with the applicable terms and notices shipped with the downloaded toolchain.

## Android 4.4 compatibility path

The built-in API 19/20 fallback does not include minicap/minitouch binaries. It uses ADB shell commands available on the connected Android device.
