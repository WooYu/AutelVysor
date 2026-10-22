# Desktop usability implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Implement the seven reported desktop usability corrections and deliver a verified Windows candidate.

**Architecture:** Preserve the WPF/ADB backends. Move the active session tool element into the main left sidebar; use a retained log document independent of its WPF rendering; keep captures and app/files corrections isolated for concurrent implementation.

**Tech Stack:** .NET 8 WPF, C#, Windows native HWND, ADB/scrcpy, Inno Setup, GitHub Actions.

**Spec:** ../specs/2026-10-22-experience.md

## Global constraints
- FileDescription=Autel; keep signing/publication gates intact and version the candidate 0.10.6.
- No device font/density changes; Fit preserves the whole frame.
- Do not claim physical-device, audio playback or codec success from command/UI fixtures.

## Review focus
- Switching/closing tabs must never leave controls attached to another device.
- Package lookups may finish out of order or fail; old summary/PID must not survive.
- Imported, multiline, malformed and large logs must remain filterable without UI hangs or silent truncation.
- Stopping/failed live streams and media selection/deletion must release owned resources.
- Fullscreen must have a mouse recovery action when the foreign SDL process owns keyboard focus.

### Task 1: Left controls, feedback and icon integration
**Files:** MainWindow.xaml(.cs), Controls/MirrorSessionControl.xaml(.cs), tests/RegressionTests/Program.cs; diagnostic window if needed.
- [x] Add LeftToolsLayout regression and observe failure: active controls are not in the left sidebar.
- [ ] Move global/session controls and status to the left; keep the viewport at full height, detach/rehost one active toolbar, retain per-device state.
- [ ] Entering fullscreen/hiding tools calls Fit; preserve sidebar/window restoration and provide a narrow mouse recovery rail.
- [ ] Add truthful focus, rotation and audio busy/result feedback and bounded audio diagnostics.
- [ ] Verify native paste/focus/resize and left-tools/fullscreen regressions; inspect Windows screenshots.

### Task 2: Capture right-side preview
**Files:** CaptureHistoryWindow.xaml(.cs), new tests/RegressionTests/CaptureRegressionCases.cs.
- [ ] Add fixtures for image/video selection, playback control enablement and media cleanup.
- [ ] Implement right-side image/video preview and useful selection/media error feedback.
- [ ] Register and run Windows fixture cases, then inspect the preview layout.

### Task 3: App details and file operations
**Files:** DeviceDebugWindow.xaml(.cs), new partial test file for app/files.
- [ ] Reproduce typed-package stale/empty summaries and dark file selection/navigation.
- [ ] Debounce refresh with version/cancellation guards, clear stale details/PID and expose loading/results.
- [ ] Make folder open/parent/root actions and applicable selection actions explicit; apply dark selected-row styling.
- [ ] Verify out-of-order lookup protection, errors, folder navigation and file action enablement.

### Task 4: Retained log document and Logs UI
**Files:** new Services/LogcatDocument.cs; new log regression test file; log-only sections of DeviceDebugWindow.xaml(.cs) after Task 3 releases them.
- [ ] Add parsing/filtering/retention/export regressions for threadtime, brief, continuation and malformed lines.
- [ ] Implement source-ordered bounded retention, composable filters and all/filtered export.
- [ ] Connect live/snapshot/import to the same document; virtualize colored rows and reapply filters to retained data, display source/match counts.
- [ ] Guard duplicate live starts, restore controls on stop/end/failure, preserve cancellation ownership and meaningful empty/error status.
- [ ] Run all existing/new tests and inspect representative imported/filter/color screenshots.

### Task 5: Integration and delivery
- [ ] Review disjoint agent diffs and register new cases without dropping previous regressions.
- [ ] Fix observed CI readiness/file-release fixture timing and authenticated public release metadata lookup without weakening process-termination assertions or leaking tokens to asset hosts.
- [ ] Run Windows regression/build/self-test/package/hash checks, review the final code and screenshots.
- [ ] Merge the verified tree and provide the unsigned candidate download with physical testing limits.
