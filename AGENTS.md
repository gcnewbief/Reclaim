# Reclaim — agent notes

Windows disk/data recovery tool. WPF desktop app, `net10.0-windows`, C#.
Motivating case: a 1 TB Crucial BX500 whose first ~1 GB was wiped by a drive
test — partition table + boot sector gone, but the NTFS MFT (~3–5 GB onward)
survived. Design goal is a *generalist* tool, not just this failure mode.

## Hard rules

- **Never write to the source disk.** All recovery paths are read-only; the
  app refuses output folders on the same physical disk (see `Recover_Click`).
- **Never advise initializing a disk.** "Initialize" prompts mean a missing
  partition table, not a dead drive — data is usually intact.
- Physical drive access (`\\.\PhysicalDriveN`) needs elevation — the manifest
  declares `requireAdministrator`.
- Keep diagnostic detail high — this tool's selling point is narrating what
  it finds (tier choices, bitmap stats, bad regions), not hiding it.

## Architecture

Tiered scan, auto-fallback:

| Tier | What | Trigger |
|------|------|---------|
| 0 | `Volumes.Triage` — MBR/GPT + boot-sector probe per partition | every scan |
| 1 | Quick scan — read `$MFT` directly via a healthy NTFS boot sector | healthy boot sector |
| 2 | Smart scan — hunt `FILE` records until the MFT can be self-located (record 0's `$DATA` runs → volume offset + cluster size), then read MFT extents directly (~35 s on the test drive) | default when boot sector is gone |
| 2x | Exhaustive deep scan — every-sector record hunt + signature carve | "DEEP SCAN ONLY" button |

Key mechanics:

- `RawDisk` — sector-sized buffered reads, tolerates bad regions (returns
  zeros + records `BadRanges` instead of throwing).
- `NtfsScanner` — MFT record parser (1/4 KB records, fixup applied),
  `$Bitmap` cluster-health scoring (Good/Partial/Poor), parent-ref path
  rebuild, resident/non-resident data via run lists.
- `Carver` — signature table; video sigs (MKV/MP4-any-`ftyp`/AVI/WMV/TS)
  capped at 32 GB.
- `Smart` — ATA pass-through (`SMART_RCV_DRIVE_DATA`) → falls back to
  `Get-StorageReliabilityCounter` via PowerShell; `Report.TrafficLight`
  gives the green/amber/red drive-health verdict.

## File map

`Native.cs` P/Invoke · `RawDisk.cs` physical drive IO + enumeration ·
`Volumes.cs` partition/boot triage · `Ntfs.cs` MFT engine · `Carver.cs`
signature carving · `Smart.cs` drive health · `Models.cs` `RecoveredEntry`
(grid rows, `Checked` for recovery selection) · `FolderTree.cs` folder-tree
model (`FolderNode`, tri-state check propagation to descendants) ·
`MainWindow.*` UI (incl. code-built `RecoveryHud` progress window) ·
`make_icon.py` regenerates `icon.ico` (exe + window icon) ·
`cli/` headless engine harness · `uitest/` FlaUI UI harness.

## Build / publish / test

The .NET SDK is per-user at `~/tools/dotnet` — not on PATH:

```bash
export PATH="/c/Users/Gcnewbief/tools/dotnet:$PATH"
dotnet build                       # compile check
dotnet publish -c Release -o publish   # self-contained ~72 MB single exe
```

`Reclaim.csproj` bakes in `win-x64` + self-contained + single-file +
compression — don't remove; the user wants zero-runtime installs.

Test harnesses (all need elevation):

- `cli_run.bat` — headless engine test vs Disk 1 (`--tree` dumps folder-tree
  stats: folder count, build ms, top folders) — console to `cli_out.txt`,
  diagnostics to `cli_log.txt`
- `smart_run.bat` — SMART read test
- `uitest_run.bat` — FlaUI end-to-end: picks Disk 1, scans, screenshots
  (`uitest_*.png`), toggles checkboxes, exercises SMART window

## Known quirks (don't rediscover)

- WPF `GridViewColumn.Width` has **no star sizing** — fixed/Auto only.
- Restyled `ComboBox` needs `SelectionBoxItem` bound `ContentPresenter` as a
  *sibling* of the ToggleButton, and items need `ToString()`/`DisplayMemberPath`
  (we do `RawDisk.ToString() = Label`).
- Never touch UI elements inside `Task.Run` — the triage bug pattern. Worker
  methods must return log lines; the UI thread emits them.
- FlaUI can't enumerate *owned* (non-modal) WPF child windows — assert on
  the app's own "report window shown; windows=N" log line instead.
- MFT records are 512-byte aligned to *volume start*, not absolute disk —
  alignment checks use `% 512`, and chunk reads must carry a tail overlap.
- `System.Windows.Forms` is imported (FolderBrowserDialog) — qualify
  `Application`/`MessageBox` etc. to avoid ambiguity.
- Check-state sync flows through the static `RecoveredEntry.CheckedChanged`
  hook, NOT grid events — UIA `TogglePattern` (uitest) never fires `Click`,
  and `Checked`/`Unchecked` event setters fire spuriously on row recycling.
  Bulk ops set `_suppressTreeCount` and recount once.
- Tree checkboxes handle `PreviewMouseLeftButtonDown` with `e.Handled=true`
  to kill WPF's tri-state cycling — partial always goes to fully-checked.
- WPF binds to **properties only** — public fields silently fail (this is why
  the folder tree once showed just the 2 root pseudo-nodes). `FolderNode`
  members are properties for this reason.
- `Window.Resources` styles don't flow into child windows — `RecoveryHud` and
  report windows set brushes explicitly instead of restyling.
- `Progress<T>.Report` is explicit `IProgress<T>` — declare progress vars as
  `IProgress<…>` or you can't call it.

## Test drive reference (Disk 1, CT1000BX500SSD1)

Volume offset `0x56D00000`, cluster 4096 B, ~1,165,518 MFT records,
~203,876 deleted, `$Bitmap` ~15.5% free, SMART amber (8 uncorrected reads).
~700 GB of user data, mostly films — large non-resident video recovery is
the primary workload.

## Git / GitHub / releases

Remote: `https://github.com/gcnewbief/Reclaim` (public, MIT). Push via
`py push.py` — it reads `GITHUB_TOKEN`/`GITHUB_REPO` from `.env`
(gitignored; fine-grained PAT scoped to VoltTest + Reclaim).

Release flow: bump `<Version>` in `Reclaim.csproj` → `dotnet publish
-c Release -o publish` → commit → `git tag -a vX.Y.Z` → `py push.py`
→ `py release.py` (creates the GitHub release + uploads the exe).
Both commit AND tag need the `-c user.name/-c user.email` identity flags
(no global git identity). The version shown in the title bar / header reads
`<Version>` via `AssemblyInformationalVersion`, so bumping the csproj is the
only place it needs to change.

`publish/`, `bin/`, `obj/`, `*_out.txt`, `uitest_*.png`, `.env` are
gitignored.
