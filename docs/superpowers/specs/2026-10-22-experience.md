# Desktop usability corrections

User requirements: keep the complete phone frame visible; use the existing fullscreen action, Fit and left-side tools. Add the generated app icon. Improve capture previews, app details, feedback, files and logs as reported in the seven-item review.

- One left sidebar contains global actions, session navigation and active-device tools. The right viewport uses the available height. Fullscreen resets Fit and hides controls, with a mouse-accessible recovery/exit rail. Advanced cropped zoom remains optional.
- Captures selects images or recordings on the left and previews them on the right. Video playback has explicit play/pause/stop/seek and actionable codec/media failure feedback; selecting/deleting/closing releases the media file.
- Valid typed/selected package names refresh app summary and permissions with debounce and stale-response protection. Invalid/changed packages clear obsolete detail and selected-app PID state. App operations show running/results.
- Audio diagnostics distinguish configured policy, reported Android indicators and untested actual playback. Show long evidence in a bounded scrollable/copyable window. Focus/rotation/audio actions have visible busy/result feedback without claiming unverified success.
- File selection stays visible in the dark theme; open folders explicitly or by double-click, parent/root/path navigation remains usable; enable file actions only for applicable selections.
- One log document serves live, snapshot and imported logs. Text/Tag/PID/minimum-level filters combine and reapply to retained data. Support common threadtime/brief formats and multiline records. Show severity colors and result/source counts, no false success on an empty/error stream. Starting a live stream disables duplicate starts; stop/end/failure restores controls. Export all retained records or the currently filtered records, preserving source order.
- Keep UI responsive and retained log memory bounded; disclose retention/truncation. Do not alter device font/density. FileDescription remains Autel. Trusted signing stays gated; only unsigned internal candidates are available without credentials.

Physical Android and real audio/media codec acceptance remain separate from Windows fixture and regression evidence.
