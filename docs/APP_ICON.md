# Application icon

Introduced in 0.10.6. The mark shows a blue desktop display and a phone on a navy rounded tile. No official Autel corporate logo is implied.

- `src/AutelVysor/Assets/AutelVysor.png`: 256px RGBA source preview.
- `src/AutelVysor/Assets/AutelVysor.ico`: 16, 24, 32, 48, 64, 128 and 256px RGBA frames.
- The project ApplicationIcon embeds the ICO in the PE. Explicit WPF pack URIs provide window/taskbar icons. Inno Setup uses it for setup; installed shortcuts and uninstall entry use the executable icon.

Artwork generated with the built-in imagegen tool, then resized/encoded using Pillow for Windows compatibility. Creative prompt: a single centered professional Windows app icon for Autel Vysor; azure desktop display with white screen, overlapping upright phone and cyan mirroring motif, navy rounded-square tile, transparent outside, strong small-size silhouettes, no text, letters or existing brand logo.

If an existing pinned shortcut retains the old icon after updating, unpin the old shortcut and pin the new executable.
