# Palette

A Spotlight-style launcher for Windows: a Rust engine with a WinUI 3 interface.
Resident, instant on `Alt+Space` (falls back to `Ctrl+Space` if taken) or the Copilot key
(Win+Shift+F23, intercepted with a low-level keyboard hook so Copilot does not open).

## Layout

```
crates/palette-core      Rust engine: index, search, usage, actions, icons (+ C ABI DLL)
crates/palette-mft       elevated NTFS MFT snapshot helper
crates/palette-semantic  semantic tier: EmbeddingGemma host + document indexer (own process)
winui/Palette.WinUI      WinUI 3 app (C#, PaletteWinUI.exe), calls palette_core.dll
scripts/get-model.ps1    downloads the embedding model into models\
```

## Build & run

```
.\scripts\get-model.ps1    # once: EmbeddingGemma 2 text, 4-bit ONNX, ~207 MB (optional)
.\build.ps1                # engine + helpers + WinUI app

winui\Palette.WinUI\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\PaletteWinUI.exe
```

The WinUI build copies `palette_core.dll`, `palette-mft.exe`, `palette-semantic.exe`
and the `models\` folder into its output folder automatically. Requires the .NET 10
SDK and Windows App SDK 2.3.1 (restored from NuGet). Without the model the launcher
works exactly as before, just without "Related" results.

## Usage

| Input | Does |
|---|---|
| type | fuzzy search apps, settings, commands, files/folders |
| `Up` / `Down` (or `Ctrl+N` / `Ctrl+P`) | move selection |
| `Enter` | open / run |
| `Ctrl+Enter` | reveal file in Explorer |
| `Esc` or click away | hide |
| `2*(3+4)` | calculator, Enter copies the result |
| `> ipconfig` | run a command in cmd |
| `Palette: Run at startup` | toggle start-at-sign-in (per-user logon task "Palette", no admin; starts right away, unlike Run-key apps that Windows delays) |
| `Palette: Reindex files` | refresh the file index now |
| `Palette: Quit` | exit |
| tray icon: click | show / hide the palette |
| tray icon: right-click | Open, Run at startup (checked when on), Re-index files, Quit |

New tray icons start in Windows' overflow area; drag it onto the taskbar to pin it.
Closing the palette with `Esc` or the hotkey returns focus to the window you were in.

Anything you launch is remembered and boosted next time (frecency).

## How it works

- **Instant tier:** all paths live in one string arena; each keystroke is a
  parallel fuzzy scan (nucleo) on a worker thread, narrowed by the previous
  query's matches. ~2-9 ms on ~200k files.
- **File index:** `palette-mft.exe` (admin) enumerates the NTFS MFT via
  `FSCTL_ENUM_USN_DATA` and writes `%LOCALAPPDATA%\palette\index\<drive>.txt`.
  First run prompts UAC once and registers a logon task (`PaletteMftRefresh`,
  highest privileges) so later refreshes need no prompt. Without the helper it
  falls back to walking your user profile.
- **Between snapshots:** a file watcher on your profile adds new files live.
- **Drives without an MFT** (FAT/exFAT, and virtual drives such as Google Drive, which
  report FAT32): when a drive's snapshot comes back empty, the app itself (unelevated,
  because the elevated helper often cannot see per-user virtual drives) walks it slowly
  at below-normal priority, capped at 600k entries / 6 minutes, and caches the result in
  `%LOCALAPPDATA%\palette\walk-<X>.txt`, refreshed every 6 hours.
- **Semantic tier ("Related" rows):** `palette-semantic.exe` hosts EmbeddingGemma 2
  (text model, 4-bit ONNX, 256-dim Matryoshka vectors; falls back to the original
  EmbeddingGemma-300m if that is the only model present) in its own process. Its index is
  per model and rebuilds itself when the model changes. It indexes apps, settings, commands and the
  text of documents (`.txt .md .rst .tex .docx .pptx .pdf` under your profile), most
  recently modified first, only while on AC power and at below-normal priority.
  After typing pauses (~180 ms) the launcher asks it for up to three matches and appends
  them under the instant results; it never sits in the keystroke path. When the typed
  text appears in no instant hit's name (natural-language queries like "take a
  screenshot") and the meaning match is strong (cosine >= 0.74), the related rows lead the
  list instead, and name matches made of scattered letters are dropped. The model is
  loaded on demand and released after 5 idle minutes, and the process exits after 20.
  Documents are read in full (up to 60 PDF pages, 80 slides, 150k characters) and cut into
  overlapping ~900-character chunks, each embedded with the file name as context and
  stored as its own vector; a document scores as its best chunk. Past 8 chunks the first 4
  are kept and the rest sampled evenly, so a long document is represented along its length
  (`PALETTE_SEM_MAX_CHUNKS`). PDFs are parsed in a killable child process (15 s limit,
  files up to 40 MB): lopdf first, then pdf-extract's font handling when lopdf returns
  almost nothing. Documents whose text cannot be read in time, and scans (no text layer),
  are indexed by file name. Backup copies (same name and size) and
  package boilerplate (LICENSE, CHANGELOG, `dist\`, `node_modules\`...) are skipped.
  The index file name carries a layout version; after an upgrade the previous index keeps
  answering while the new one is built, then is deleted.
  Index data lives in `%LOCALAPPDATA%\palette\semantic`; the footer shows its progress.
  Documents you create or save are picked up incrementally: the file watcher reports them,
  the launcher batches the notices (3 quiet seconds) and the helper embeds just those files,
  so a new document is searchable by meaning within seconds (measured: 8 s). That only
  happens while the helper is running and on AC power; a full rescan runs every 30 minutes.
- **Excluded from the index:** Windows, Program Files, AppData, ProgramData,
  dot-directories, node_modules, conda, and noise extensions (dll, sys, pdb, ...).
  Edit `SKIP_DIRS` / `SKIP_EXT` in `crates/palette-mft/src/main.rs`.

Set `PALETTE_LOG=1` to log per-search timings to `%LOCALAPPDATA%\palette\search.log`
and show/hide/activation diagnostics to `%LOCALAPPDATA%\palette\winui.log`.

Semantic tier knobs (environment variables): `PALETTE_SEMANTIC=0` disables it,
`PALETTE_SEM_MIN` overrides the similarity floor (EmbeddingGemma 2: 0.72 docs / 0.70
items; the original model's score band is lower, so its floors are 0.42),
`PALETTE_SEM_ON_BATTERY=1` lets indexing run on battery, `PALETTE_SEM_THREADS`,
`PALETTE_SEM_DOC_TOKENS` (tokens per chunk, default 224), `PALETTE_SEM_MAX_CHUNKS`
(default 8), `PALETTE_SEM_MAX_DOCS` (default 40000).
`palette-semantic.exe query "text"` / `index` (prints extraction vs embedding time) /
`embed-test` (prints chunks/s) are handy for debugging.

## Not yet done

- Semantic tier: source code is not indexed; scanned PDFs (no text layer) need OCR;
  embedding speed on CPU is ~3-4 chunks/s, so a full rebuild takes tens of minutes
- Configurable hotkey
- Non-NTFS drives (exFAT/FAT) are not in the MFT snapshot
