# Desktop usability changes

The changes address device selection, automatic keyboard focus, capture feedback,
image preview, file browsing and application search. They also guard overlapping
debug actions, stale app/category replies, shell submissions and file operations.

## File browser

The address field accepts absolute Android paths. History is recorded after a
successful listing. Sorting keeps folders first; the filter applies to the current
directory. The name field is shared by New folder and Rename selected; F2 fills it
with the selected item's name. Rename refuses an existing destination.

Download supports one file through Save As, and folders or multiple items through
a destination-folder chooser. Upload and download ask before replacing existing
items. Android permissions still govern which locations can be accessed. Transfer
feedback reports the current item, rather than a byte-level percentage.

## Application names

Labels are resolved from compiled AndroidManifest.xml and resources.arsc. The
reader handles UTF-8/UTF-16 string pools, string references, localized resources,
sparse/16-bit offset tables and compact entries. It prefers the Windows UI
language when available. APKs are pulled to temporary local files and removed
after parsing. Indexing is sequential, cancellable and limited to 30 seconds per
app; readable labels are cached for the application session using device, APK path
and language. Names may appear progressively after opening Device Debug.

Resources only present in split APKs, runtime overlays, inaccessible APKs or
unsupported resource forms can leave a package without a readable name. Such
packages remain searchable by identifier. This does not install or activate an
Android input method or helper app.

## Validation

- Debug and Release builds succeed with zero warnings/errors.
- Regression coverage includes selection/reconnection, Chinese app-label parsing
  and result selection, file history/filtering, stale replies, batch/folder action
  availability, shell path quoting, rename overwrite refusal, and image/video
  control separation and file release.
- A connected Samsung SM-G9730 was used for read-only inspection: its Settings
  APK label decoded as `Settings`, matching Android SDK `aapt dump badging`.
- Windows rendering checks save captures under ignored `test-results/`.
- Two existing native SendInput tests (Chinese clipboard paste and cross-process
  digit delivery) fail in this Windows session. Both failures also reproduce on
  the unmodified repository in an isolated baseline run. They remain unverified,
  rather than being treated as passing keyboard acceptance.

Physical acceptance is still needed for automatic typing into a focused Android
field, Windows Chinese IME composition, real screenshot/MP4 success and failure,
USB interruptions and vendor-specific filesystem permissions. The device checks
above did not delete files, install apps or change phone settings.

Run Windows UI regressions with:

```powershell
$env:AUTEL_TEST_SHELL = 'path\to\Git\bin\bash.exe' # optional for non-default Git installations
dotnet run --project tests/RegressionTests/RegressionTests.csproj
```

`AUTEL_TEST_FILTER` optionally selects test names by substring, separated by `|`.
