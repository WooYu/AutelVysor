# v0.10.2 report corrections

> Execution: implement inline, using systematic debugging and regression tests.

**Goal:** Correct AV-001 through AV-005 without reclassifying untested hardware features as passed.
**Architecture:** Preserve WPF/scrcpy/ADB boundaries; execute one remote shell command, share actual-display rotation queries, and own recording processes until graceful shutdown.
**Tech stack:** .NET 8 WPF, Win32, scrcpy, ADB, Windows GitHub Actions.
**Spec:** User-supplied v0.10.2 functional coverage report, 2026-10-21.

## Constraints and review focus

- Preserve Android API 19 compatibility and FileDescription=Autel.
- Keep sensor mode and actual display angle separate; unknown is not zero.
- Shell quoting must preserve spaces, quotes, compound statements and nonzero exit codes.
- Block duplicate recordings including while starting; close hidden native windows gracefully before process termination.
- Do not claim Windows IME, physical rotation, audio routes, USB recovery, signing or untested features passed.
- Trusted company signing requires an actual trusted certificate; never invent one.

## Tasks

- [ ] Add shell-boundary regressions using a real POSIX shell after ADB argument joining; test arguments, quoting, compound commands and false.
- [ ] Add modern display rotation regressions with stale user_rotation, secondary displays and unavailable diagnostics.
- [ ] Assert embedded keyboard launch uses SDK raw key events; keep native focus regressions.
- [ ] Add duplicate-recording and graceful finalization/cleanup regressions with a native subprocess.
- [ ] Observe failing Windows regression run, then implement fixes in AndroidBridge, AdvancedAdbService, ScrcpyService, recording ownership and MainWindow.
- [ ] Run full Windows regression/build gates; document evidence and pending physical acceptance against all 18 report items.
- [ ] Push a focused GitHub branch and open a reviewable PR.
