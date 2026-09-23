# Version 1.1 — automatic Explorer following

Start with **הוראות-עדכון.txt** for Hebrew upgrade instructions. Existing users should exit the old tray app and run **Update.cmd**, then sign out/in and launch the new Start menu entry. New installations use Install.cmd. The new installation directory is Program Files/FolderSizeMeter/1.1.0. Other applications' overlay registrations are untouched.

Automatic mode is ON by default, including when loading version 1.0 settings. It follows local filesystem locations exposed by Shell.Application/IShellWindows, checks for navigation approximately every two seconds between calculations, scans one direct child at a time on a below-normal-priority background STA, and publishes results progressively to the existing shared-memory cache. No scanner or discovery code runs inside Explorer. Active-tab coverage must be verified on the installed Windows build; hidden tabs, Home, search and virtual folders are not promised.

FileSystemWatcher invalidates affected child jobs, with a short debounce; error/overflow invalidates all children. A periodic 1–5 minute rescan catches missed changes. Watchers are removed when locations close; recent results are retained for up to eight minutes. Root-drive watchers do not recursively watch the whole volume. Normalization and all COM object lifetimes are kept outside Explorer.

Workload caps: 16 exposed locations, first 512 directory entries per location, one scan at a time, eight-second cancellation budget per child, and the original 16,000-folder/depth/path limits. There is a short throttle every 128 filesystem entries. Oversized or unreadable jobs remain unmarked and are retried later. OS filesystem or COM calls themselves are not cancellable, so discovery/exit can be delayed by a stalled OS call. No assertion of zero disk impact is made. Manual mode is retained by unchecking automatic mode and saving. Windows login startup remains opt-in, not installed automatically.

The indicator diameter changed from 43% to 29% of the overlay canvas (about one third smaller), with a thinner white outline. The module's new versioned install path allows Windows to load fresh icon resources after sign-out.

Verified: release compilation, automatic child discovery through an injected location provider, Hebrew paths, change notifications, rename and deletion, overflow invalidation, navigation to another location, clean worker termination, native icon extraction and COM unload, and Settings rendering. See Automatic-verification.txt. A live read-only Explorer COM probe was denied by this agent's sandbox, so **live automatic window discovery and the upgrade script have not been validated end-to-end on this PC**. Run Check-Automatic.cmd from the package for a non-admin, read-only list of detected Explorer locations.

The sections below describe the original 1.0 MVP and its earlier verification; where they differ, the 1.1 notes above apply.

---
# Folder Size Meter — Windows 11 MVP

A tray application that scans selected local folders and supplies orange/red size overlays to Explorer through Microsoft's documented native `IShellIconOverlayIdentifier` interface. It does not replace folder icons, edit desktop.ini, modify thumbnails, patch Explorer, or load .NET into Explorer.

## Try it

1. Extract the whole ZIP to a folder. Run **Launch.cmd**, or `app/FolderSizeMeter.exe`, to use Settings and the scanner immediately.
2. Add one or two small local folders and choose **Save & scan**. Closing Settings keeps the scanner running; use its tray menu to reopen Settings or exit.
3. For Explorer indicators, run **Install.cmd** and accept the Windows administrator prompt. This copies the app to `C:\Program Files\FolderSizeMeter\1.0.0`, registers two native handlers, and adds a Start menu shortcut.
4. Sign out and back in, then open **Folder Size Meter** from Start. Configure folders and leave the tray app running. Explorer may need a refresh to redraw cached icons.

Requires x64 Windows 11 and the Microsoft **.NET 8 Desktop Runtime (x64)**. The runtime is already installed on the development PC. This is a framework-dependent, unsigned prototype; it does not bundle or download a runtime. ARM64 and x86 Explorer are not supported.

**Important on this PC:** 23 overlay handlers were already registered before this app was installed. Windows supports only 15 overlay slots, some used by the system. These new markers may therefore not appear. Registration is not proof that Explorer selected a handler. The app reports the registered count, does not rename handlers to jump the queue, and does not disable other applications. The Settings size table works independently of overlay availability. Explorer shows at most one overlay on an item; a cloud-sync or other overlay can take precedence.

## Thresholds and scanning

Defaults use decimal units, matching the requested MB/GB labels:

| Folder size | Marker |
| --- | --- |
| Below 50,000,000 bytes | Normal folder |
| 50,000,000 through 1,000,000,000 bytes inclusive | Orange |
| Above 1,000,000,000 bytes | Red |

Settings accepts MB with two decimal places. Changing thresholds saves them and starts a fresh scan. Totals are recursive logical file sizes, not allocated disk space. Hard-linked files count once per directory entry. Scans are eventually consistent, not filesystem snapshots: changes during traversal settle on the next scan.

Only folders explicitly chosen by the user are scanned. Default refresh is every two minutes after completion, adjustable to 1–5 minutes. There is one cancellable worker; scans do not overlap. A three-minute cancellation budget, 16,000-folder budget, maximum depth of 128, and paths shorter than 1,024 UTF-16 code units bound the MVP's workload. Cancellation is checked between filesystem operations; an individual Windows filesystem call cannot be interrupted by that token. Choose smaller roots if a scan exceeds the time budget.

Junctions, symbolic links, offline files and cloud reparse points are skipped to avoid loops and hydration. A skipped or unreadable entry makes its ancestor total incomplete. Incomplete totals appear in Settings but receive no marker. Network and removable roots are excluded. A removed file is reflected on the next completed scan. The table shows the largest 500 scanned folders; the overlay cache includes all complete results within the budget.

The scanner must be running for live updates. Automatic startup is not enabled by the installer. If desired, place a shortcut to the installed EXE with argument `--tray` in your own Windows Startup folder.

## Architecture

- **C# / .NET 8 WinForms app:** Settings, tray menu, cancellation, periodic scanning, saved settings and last scan report in `%LOCALAPPDATA%\FolderSizeMeter`. The disk snapshot is a report; it is not replayed into Explorer on startup because it may be stale.
- **Shared-memory cache:** a session-local, pagefile-backed mapping with a 32-byte header and up to 16,384 fixed-size records (about 32 MiB). Normalized full paths are sorted for binary search. The writer publishes with an odd/even sequence; readers reject a concurrent or malformed publication. Cache data expires after ten minutes if the scanner stops publishing.
- **Native C++ DLL:** two COM classes, reference-counted factories, embedded transparent multiresolution icons, read-only mapping access, and bounded binary search. No directory enumeration, disk-cache reads, blocking RPC, background worker, or managed runtime inside Explorer. Mapping initialization uses a try-lock and retries at most once per five seconds. A missed, expired, incomplete or changing cache returns no marker.
- **Updates:** the app sends asynchronous Shell notifications for changed classifications and selected roots. Explorer retains its own icon cache, so redraw timing remains controlled by Windows; expiry affects future handler queries rather than forcibly repainting every open window.

Dominant-content detection is intentionally deferred. It can later extend scanner results without moving filesystem work into Explorer; extra overlay combinations would consume additional scarce slots.

## Installation and removal

Installation modifies only the two application CLSIDs and their two `ShellIconOverlayIdentifiers` entries under HKLM, plus the application files and Start menu shortcut. It does not restart Explorer or alter existing handlers. Run **Uninstall.cmd** to remove these registrations and known installed files. Exit the tray app first. If Explorer holds the DLL open, sign out and rerun the uninstaller from this package afterward. Settings and scan reports are retained. Remove any Startup shortcut you added yourself.

The installer refuses an existing registration rather than overwriting an active installation. Administrative installation/removal scripts were syntax-checked but have **not been executed on this PC**.

## Build and verification

Run `Build.ps1` with .NET SDK 8 and Visual Studio C++ Build Tools plus a Windows SDK installed. No third-party runtime packages are required. The native DLL uses the static C++ runtime and builds with warnings treated as errors.

Run `app\FolderSizeMeter.exe --self-test <absolute-test-directory>` while the tray app is not running. Results are written to `test-results.txt` in that directory. The native probe loads the actual DLL without registration and tests both COM classes, icon extraction, lookup behavior and clean unloading.

Verified here: release compilation; recursive totals; overlapping roots; cancellation; missing folders; corrupt settings; threshold boundaries; persistence; deletion/rescan; native lookups; invalid count; writer-in-progress rejection; stale-cache rejection; cache clearing; threshold changes; Hebrew paths; and a 16,001-record cache. Warm native queries in that larger synthetic cache measured about 1–2 microseconds per call on this PC. This is a microbenchmark, not a guarantee of Explorer's overall performance. The Settings window was rendered and visually checked.

**Not yet verified:** administrator installation/uninstallation, live Explorer overlay selection and rendering, cloud-provider combinations, or long-running operation. The app is a built and component-tested MVP ready for a local trial, not a claim of completed end-to-end Explorer validation.

Microsoft references:
- [Overlay interface and preservation of the normal icon](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ishelliconoverlayidentifier)
- [Documented registration](https://learn.microsoft.com/en-us/windows/win32/shell/how-to-register-icon-overlay-handlers)
- [Native in-process extension guidance](https://learn.microsoft.com/en-us/windows/win32/shell/shell-and-managed-code)
- [Why Windows limits overlays to 15](https://devblogs.microsoft.com/oldnewthing/20190313-00/?p=101094)

