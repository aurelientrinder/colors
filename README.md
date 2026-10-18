# Palette

A Spotlight-style launcher for Windows: a Rust engine with a WinUI 3 interface.
Resident, instant on a tap of the Windows key, `Alt+Space` (falls back to `Ctrl+Space` if taken; any combination can be chosen in Settings) or the Copilot key
(Win+Shift+F23, intercepted with a low-level keyboard hook so Copilot does not open).

## Layout

```
crates/palette-core      Rust engine: index, search, usage, actions, icons, window reading (+ C ABI DLL)
crates/palette-mft       elevated NTFS MFT snapshot helper
crates/palette-semantic  semantic tier: EmbeddingGemma host + document indexer (own process)
crates/palette-plan      the plan model on the NPU (own process; llama-server's interface)
crates/palette-ov        the parts of OpenVINO's C API both use, loaded at run time
winui/Palette.WinUI      WinUI 3 app (C#, PaletteWinUI.exe), calls palette_core.dll
scripts/get-model.ps1    downloads the embedding model into models\
scripts/get-openvino.ps1 sets up the OpenVINO runtime (NPU) in models\openvino
ml/npu/export.py         makes the NPU versions of both models
```

## Build & run

```
.\scripts\get-model.ps1    # once: EmbeddingGemma 2 text, 4-bit ONNX, ~207 MB (optional)
.\scripts\get-openvino.ps1 # once, Intel Core Ultra: OpenVINO for the NPU, ~25 MB (optional)
ml\intent\.venv\Scripts\python ml\npu\export.py   # once: the NPU versions of the models
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
| type | fuzzy search apps, settings, commands, files/folders, and open windows |
| `Up` / `Down` (or `Ctrl+N` / `Ctrl+P`) | move selection |
| `Enter` | open / run |
| `Ctrl+Enter` | reveal file in Explorer |
| `Left` on a file or folder | shows the folder it is in, with it selected; `Left` again goes up another folder |
| `Right` on a folder | goes into it: lists what is in it (folders first, as Explorer orders them); typing filters the list |
| `Right` on a file, while browsing | back to the search you started from, with its row selected |
| `C:\path\` or `~\path\` | lists that folder; text after the last `\` filters it (no match: the usual index search) |
| `Esc` or click away | hide |
| part of a window title or app name | switch to that open window (restored if minimised); an open window ranks above launching its app again |
| `2*(3+4)` | calculator, Enter copies the result |
| `10 km in mi`, `72f to c`, `2 h to min` | unit conversion (length, weight, volume, time, speed, area, data, temperature), offline |
| `days until 25 dec`, `3 weeks from today`, `2 months ago` | date arithmetic, offline |
| `time in tokyo`, `3pm london to new york` | time zones with daylight saving, offline |
| `clip`, `clip invoice`, `clipboard the tracking number` | what you copied lately (Windows' clipboard history plus copies made while Palette runs): words find it at once, otherwise it is searched by meaning. Enter copies it again; Hold Tab offers Open link for a URL and Remove. Kept in memory only, never on Home or among ordinary results; copies a password manager marks private are skipped; Settings turns it off |
| `port 3000`, `what's using port 8080`, `:5173` | the program listening on that port, with its icon; Enter shows it in its folder, Hold Tab › End task ends it |
| `what's using my camera`, `is my microphone on` | the apps using it now, from Windows' own privacy record (or who used it last); Enter opens its privacy settings |
| `50 usd to eur` | currency, a card with the European Central Bank's daily rate (needs the web; "Answers from the web" in Settings turns it off) |
| `> ipconfig` | run a command in cmd |
| `Palette: Run at startup` | toggle start-at-sign-in (per-user logon task "Palette", no admin; starts right away, unlike Run-key apps that Windows delays) |
| `Palette: Settings` | the options below, as a list in the palette: a switch each, typing filters, Esc goes back |
| `Palette: Reindex files` | refresh the file index now |
| `Palette: Quit` | exit |
| launch `PaletteWinUI.exe` again | opens the palette of the running copy; `--query "text"` opens it with that text typed |
| tray icon: click | show / hide the palette |
| tray icon: right-click | Open, Settings, Run at startup (checked when on), Quit |

New tray icons start in Windows' overflow area; drag it onto the taskbar to pin it.
Closing the palette with `Esc` or the hotkey returns focus to the window you were in.

## In place of Windows Search

Palette takes over the ways into Windows' own search (each switchable in `Palette: Settings`,
saved in `%LOCALAPPDATA%\palette\settings.json`):

| Input | Does |
|---|---|
| tap the Windows key | opens Palette instead of Start. Win+E, Win+D and other chords are untouched |
| `Win+S`, `Win+Q` | open Palette instead of Windows Search |
| type into Start or the taskbar search box | Start closes and Palette carries on with what you typed |
| Start button, `Ctrl+Esc`, or the `Start menu` row | still open Start, for browsing pinned apps |
| Settings: Hide the taskbar search box | removes Windows' search box from the taskbar; unchecking puts back the style you had |

Keys typed between the trigger and the palette taking focus are held back by the keyboard
hook and replayed into it, so typing straight after the Windows key loses nothing. On
current Windows 11 Start and search are one window (SearchHost.exe's "Search"), so the
hand-off happens on the first typed key rather than when the window opens.

### Over elevated windows (UI Access)

Windows keeps keys typed into an elevated window (Task Manager, an admin terminal) from
unelevated apps, so there a tap of the Windows key opens Start. The installed copy has
UI Access, which lets its hook see those keys too while it stays unelevated:

```
cd winui\Palette.WinUI
dotnet build -c Install                      # bin\x64\Install, with app.uiaccess.manifest
..\..\scripts\install-uiaccess.ps1           # as administrator
```

The script copies the build to `C:\Program Files\Palette`, signs `PaletteWinUI.exe` with a
new self-signed certificate it trusts on this PC (its private key is deleted straight after,
so nothing else can be signed with it) and checks the signature. Windows starts a UI Access
exe only when it is signed that way and sits in Program Files, which is why the Debug and
Release builds do not have it. Run both steps again after a rebuild; `-Uninstall` removes
the folder and the certificate. Notes:
- Task Scheduler cannot start a UI Access exe, so the "Run at startup" task starts it
  through `rundll32 shell32.dll,ShellExec_RunDLL`, as the shell would.
- An unelevated process cannot stop it (Access denied): quit it from the tray.
- Apps it launches inherit UI Access (not elevation).
- `PALETTE_LOG` does not reach it; use a Debug build for logs.

Selecting a file widens the palette with a preview: images, the first page of PDFs, the
opening text of text, code and Word files, otherwise Windows' thumbnail, plus size and date.
Online-only cloud files are not read (that would download them). Double-click it to open.

Anything you launch is remembered and boosted next time (frecency).

## Settings and quick toggles

Every setting Windows' own search knows is searchable: about 1,100 Settings pages and individual
settings ("mouse size" finds Mouse pointer size, "file ext" finds Show file extensions) and the
classic Control Panel tasks ("env var" finds Edit the system environment variables). The list is
read from Windows itself (`%WINDIR%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml`,
names in your display language, pages linked through the table in SystemSettings.dll) and
cached in `%LOCALAPPDATA%\palette\settings-index.json` until Windows updates it. These entries
match by word beginnings over their name and keywords rather than fuzzily.

Some settings change in place, without opening anything; the row says what Enter will do now:

| Type | Does |
|---|---|
| `dark`, `light mode`, `theme` | turn dark mode on or off (apps and Windows) |
| `wifi`, `bluetooth` | turn the radio on or off |
| `mute` | mute or unmute (shows the volume) |
| `volume 30`, `vol 30` | set the volume |
| `brightness 70` | set the built-in display's brightness |

Open windows now also need every typed word to begin a word of their title or app, so a long
page title no longer matches unrelated searches.

## Asking about the window in front

With a window open behind the palette, a request that points at it gets a row of its own, and Enter
puts the answer under the row as the model writes it, in the same field. A model that runs on this PC
reads the window's text (or the text you had selected) and answers; nothing leaves the machine.

| You type | Palette |
|---|---|
| `summarise this`, `explain this`, `what does this error mean`, `translate this to french`, `résume ça` | a row "Ask about the Notepad window · on this PC"; Enter asks |
| `? what is the open question here`, `ask who wrote it` | the same for any question |

The answer streams into a card (up to a few lines, then it scrolls); Enter again copies it, typing or
`Esc` stops it. Selected text is what it answers about when there is some (the row says "your
selection"), with the window around it for context; otherwise the window's own text (a document's or
a page's text; for a terminal the newest lines). Only what is on screen is read, never password
fields or password managers. Questions that do not point at the screen ("what is the capital of
France") stay web answers.

### Chat

A tap on `Tab` turns the list into a conversation with the model, in the same field (a question already
typed becomes the first message). The model has read the window in front before you type, so the first
reply starts at once. Your messages sit on the right, its answers on the left; the footer says what it
has read. Enter sends, `Tab` or `Esc` leaves (a held `Tab` still opens a result's actions).

- About the window: just ask ("and who decides?"); follow-ups keep the whole conversation.
- About your files: start the message with `?`, or say "my documents" / "mes fichiers"; the answer
  cites them [1] [2] and the card names the files. With no window to read, every message looks in your
  files first.

### Asking your documents

A question that does not point at the screen is answered from your own files:

| You type | Palette |
|---|---|
| `? when does my lease end`, `? what voltage does the clutch motor need`, `ask which supplier was late` | a row "Ask your documents · on this PC"; Enter writes the answer under it, citing the files as [1], [2] |

It finds the passages that answer (the meaning index for which documents are near the question, the
word index for which ones have its words, then every chunk of the best few is scored by how many of the
question's words it has) and gives the model about four short excerpts, never whole files. The card's
footer lists the files by the numbers the answer cites; Hold Tab on the row has "Open [1] file" for
each. If nothing in your documents matches well enough it says so, instead of guessing. Only files
the semantic index covers (PDF, Word, PowerPoint, text, Markdown) can answer.

Setup: `scripts\get-chat.ps1` fetches llama.cpp's server (the Vulkan build, 32 MB) into `models\chat`;
the model is any chat GGUF of 2 to 12 GB: `PALETTE_CHAT_MODEL`, a file in `models\chat`, or one LM
Studio already has (Qwen3.5 4B preferred, then Gemma 3 4B). The model loads on the first question
(a few seconds) and is let go after ten idle minutes. On an Intel Arc GPU a 4B model reads about 450
tokens a second and writes about 19-25, so reading a long page takes six seconds: that is done while
you type. Once the row appears and typing pauses, the window's text, exactly as it comes before the
question in the final prompt, is fed to the model, which keeps it; Enter adds only the question, and
the first words come in about a second (8 s without). The server runs below normal priority, which
Windows applies to its GPU work too, with two CPU threads and flash attention. `examples\ask_probe.rs` asks about any window from the command line.

## The window in front

When the palette opens it reads the window you were in (UI Automation, in memory only; Settings'
"Read the window in front" turns it off, and password managers, the desktop and password fields are
never read):

- **Selected text.** If something was selected there, Home leads with a row, "Selected in Notepad",
  and Enter puts the text in the search field (calculate it, find files about it, search for it).
  Nothing is read from the clipboard.
- **The app's own commands.** Typing a name that matches a button, tab or menu of that window ("bold",
  "resize" in Paint) adds up to three rows, "In Notepad · Ctrl+B". Enter brings the window forward and
  presses it. Only what the window shows is found: items inside a closed drop-down are not.
  Rows are learned by what you typed but never listed on Home.
- **Passages.** A document found by meaning shows the sentence that matched under its row, the
  matching words in bold ("when does my lease end"). With no word in common it shows the opening of
  the best-matching part.

See ARCHITECTURE.md for where this is heading.

## Home, All apps and actions

Opening the palette without typing shows **Home**: your pinned items, the few things you open
most, files Windows saw you open recently (its Recent folder, as Start's "Recommended" uses),
and **All apps**, every installed app A to Z (scrolls; Esc or Backspace goes back).

`Tab` (or a right click, the menu key, `Shift+F10`) opens the **actions** for the selected
result; typing filters them, `Esc` goes back:

| Action | For | Shortcut from the results |
|---|---|---|
| Open / Switch to / Run | everything | `Enter` |
| Run as administrator | desktop apps, exe/bat/cmd/msi files | `Ctrl+Shift+Enter` |
| Open file location | files, folders, desktop apps | `Ctrl+Enter` |
| Open with…, Properties | files (Properties for folders too) | |
| Open in Terminal | folders | |
| Copy path | files, folders, desktop apps | |
| End *program* (it has this open) | files that a program has open (asked of Windows' Restart Manager when the list opens) | |
| Copy file, Compress to ZIP, Rename…, Move to…, Move to Recycle Bin | files, folders | |
| Pin to Home / Unpin / Move pin up, down | anything but windows, calculator and web rows | |
| Uninstall… | apps (opens Settings › Installed apps) | |
| Close window | open windows | |

The file actions never overwrite or delete for good: a name that is taken gets "(2)" added, and
Recycle Bin is the only way out. Afterwards a line says what happened, with **Undo** for the zip
(recycled), rename and move (put back). Rename opens the name in the search field, the part before
the extension selected. Move to… lists the folder above, Desktop, Documents, Downloads, Pictures,
Videos, Music, then your pinned and most used folders; typing filters.

Pins live in `%LOCALAPPDATA%\palette\pins.json`. A desktop app's file is the one the shell
itself reports for it (`System.Link.TargetParsingPath`); Store apps have none, so they get no
file actions.

## How it works

- **Instant tier:** all paths live in one string arena; each keystroke is a
  parallel fuzzy scan (nucleo) on a worker thread, narrowed by the previous
  query's matches. The best 3,000 file matches are ranked on their path without
  building a row; only the few shown become rows and have their existence checked
  (answers kept for a few seconds). Typed paths (`c:\users\...`) match greedily, in
  linear time, and the 3,000 picked are scored again properly. ~2-6 ms a keystroke
  on ~200k files; `cargo run -p palette-core --release --example bench_search`
  measures it on your own index.
- **File index:** `palette-mft.exe` (admin) enumerates the NTFS MFT via
  `FSCTL_ENUM_USN_DATA` and writes `%LOCALAPPDATA%\palette\index\<drive>.txt`.
  First run prompts UAC once and registers a logon task (`PaletteMftRefresh`,
  highest privileges) so later refreshes need no prompt. Without the helper it
  falls back to walking your user profile.
- **Between snapshots:** a file watcher on your profile adds new files live, by the snapshot's
  own rules (no build output, dot-folders or noise extensions), a burst of them at once. It and the other
  upkeep threads (snapshot reloads, drive walks) run in efficiency mode at below-normal priority,
  so only searching itself runs at full speed.
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
  recently modified first, only while on AC power. The model runs on the NPU through OpenVINO
  when there is one (Intel Core Ultra; `model_npu.xml` from ml\npu\export.py): a query takes
  ~15 ms and indexing ~35 chunks/s, against ~55 ms and ~5 chunks/s on the GPU. The NPU runs fixed
  shapes, one compiled per token budget, the first time in the background (about a minute
  each, then cached in `%LOCALAPPDATA%\palette\npu-cache`); the GPU stands in until then.
  Without an NPU it runs on the GPU through DirectML
  when one is available (any DirectX 12 GPU, integrated ones included), which takes ~7x less CPU
  time than the CPU path at the same or better speed; otherwise on half the CPU cores. Either way
  the helper runs at below-normal priority in Windows' efficiency mode (EcoQoS), so a rebuild
  stays on the efficient cores and keeps the machine quiet. On the GPU, inputs are padded to a
  few fixed shapes, because DirectML compiles kernels for every new shape it meets.
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
  (`PALETTE_SEM_MAX_CHUNKS`). Every word of every document is also kept, as one small Bloom
  filter per document (`lex.rs`, `lex-c1.plx`, about a megabyte for two thousand documents):
  a query of three words or more that all appear in a document finds it, however far in the
  text they are, which is what a sentence copied from page 40 needs (the chunks above
  cover only a few percent of a long PDF). The row's passage is then the chunk that has the
  words. Documents indexed before this are read once more in the background, after the
  vectors are done and not on battery. PDFs are parsed in a killable child process (15 s limit,
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
`PALETTE_SEM_ON_BATTERY=1` lets indexing run on battery, `PALETTE_SEM_DEVICE` (`npu`
default, falls back to the GPU, then the CPU; `gpu`; `cpu`),
`PALETTE_SEM_THREADS` (CPU path, default half the cores),
`PALETTE_SEM_DOC_TOKENS` (tokens per chunk, default 224), `PALETTE_SEM_MAX_CHUNKS`
(default 8), `PALETTE_SEM_MAX_DOCS` (default 40000).
`palette-semantic.exe query "text"` / `index` (prints extraction vs embedding time) /
`embed-test` (prints chunks/s) are handy for debugging.

## Not yet done

- Semantic tier: source code is not indexed; scanned PDFs (no text layer) need OCR;
  embedding runs at ~4-5 chunks/s (GPU or CPU), so a full rebuild takes tens of minutes
- Non-NTFS drives (exFAT/FAT) are not in the MFT snapshot
