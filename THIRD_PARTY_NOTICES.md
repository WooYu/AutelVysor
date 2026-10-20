# Third-party notices

Autel Vysor uses third-party components under their respective licenses.

## scrcpy

- Project: Genymobile/scrcpy
- Source: https://github.com/Genymobile/scrcpy
- License: Apache License 2.0

`scripts/prepare-tools.ps1` downloads the current official Windows x64 release directly from the project's GitHub release endpoint. Keep the license and notice files supplied with that distribution when redistributing a built package.

## Android Debug Bridge (ADB)

The official scrcpy Windows distribution currently provides the ADB executable used by this project. Any redistribution must comply with the applicable terms and notices shipped with the downloaded toolchain.

## DeviceFarmer minicap

- Project: DeviceFarmer/minicap
- Source: https://github.com/DeviceFarmer/minicap
- Pinned release-build commit: f3d40d65da0cc7168846c4ae7466ada970441d4e
- License: Apache License 2.0
- Usage in Autel Vysor: optional accelerated screen capture for Android 4.4 / API 19.

Release preparation downloads the API 19 `armeabi-v7a` and `x86` minicap executable/shared-library pairs from the pinned commit. minicap relies on private Android APIs, so Autel Vysor treats it as an optional accelerator and automatically falls back to ADB screenshot capture when it cannot run.

## Android 4.4 fallback

The reliability fallback for API 19/20 uses ADB shell commands and does not require minicap. API 20 intentionally remains on this fallback until a compatible, validated native capture asset is available.
