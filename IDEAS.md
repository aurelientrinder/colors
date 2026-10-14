# Ideas

Things we could add or refine, so they are not forgotten. Roughly in the order worth doing.
Cross one off (or move it to the README) when it ships.

## Results that learn
- **Conversions in the calculator.** It is plain `meval` (`search.rs`, `calculator`). Add units
  (`10 km in mi`, `72f to c`), dates (`3 weeks from today`, `days until 25 dec`), time zones
  (`time in tokyo`) offline, and currency (`50 usd to eur`) with the quiet-fail fetch pattern of
  `WebAnswers`.
- **Browser tabs and bookmarks.** Window search finds the browser window, not the tab. Edge and
  Chrome keep bookmarks in a `Bookmarks` JSON file; history is SQLite. The semantic tier could rank
  them by meaning too.
- **Recent files per app.** "word" shows its recent documents, "code" its recent workspaces (jump
  lists, VS Code's `storage.json`). Extends Home's Recent folder idea to each app.

## Files
- **Quick actions on files.** Select a file, press a key: zip, copy as path, rename, move to,
  send to. With folder browsing the palette is already a small file manager; this finishes it.
- **Drop zone.** Drag a file onto the palette; it offers where it could go (recent and pinned
  folders, "send to" apps).
- **Drag a result out** into an email or chat. Nothing handles dragging in the WinUI code yet.
- **"The pdf from last week about taxes".** Time and app context on top of the semantic index and
  the Recent folder: what you had open Tuesday, what you were editing in Word yesterday.
- **OCR for scanned PDFs and images**, with the offline `Windows.Media.Ocr`. Closes a "Not yet
  done" item and makes screenshots searchable by their text.

## Everyday features
- **Clipboard history**, searchable from the palette (`Clipboard.GetHistoryItemsAsync` gives
  Win+V's history). Search it by meaning with the embedder; recognise what an entry holds (a
  colour, a URL, a tracking number, a phone number) and give each its own actions.
- **Named window layouts.** "Work" opens the editor, browser and chat in saved positions. The
  plan model already understands snapping, so "set up my morning" is a small step.
- **Teach it in one sentence.** "When I type `inv`, open the invoices folder": user-defined
  aliases and snippets, written in plain words and read by the NPU model.
- **Undo for plans**, not only quick settings (`Views.cs`). Save window positions before a snap or
  arrange step so Undo can put them back.

## Polish and reach
- **Teach the invisible parts**: a rotating tip on Home and a short first-run card. The footer
  covers the selected row's keys; Ctrl+Shift+Enter, requests in plain words, `vol 30` and `>` are
  still invisible unless you have read the README.
- **A settings page** (`Palette: Settings`): the hotkey (still on the README's "Not yet done"
  list), the semantic tier on or off, indexing on battery, excluded folders. Replaces the tray
  menu plus about ten environment variables.
- **Accessibility.** No `AutomationProperties` anywhere, so Narrator cannot announce the selected
  row; high contrast is not handled. It replaces Start, so this matters.
- **The "turn off wifi and bluetooth" gap** in the plan model's training data (`ml/intent`).
- **An installer with auto-update** (MSIX or winget), if anyone else is going to use it.

## Done
- Arrow keys walk folders: Left on a file or folder shows where it is (and again goes up a level),
  Right goes down into a folder, Right on a file returns to the search. Typed paths (`C:\...`,
  `~\...`) list the folder.
- "Open file location" works for paths with spaces.
- Results learn what you pick for what you type: every prefix of the query (up to 8 characters)
  boosts what you opened, so `n` finds Notepad first once you have chosen it for `note`.
- Key-hint footer in the search card: the keys that apply to the selected row (`-> Browse`,
  `<- Show folder`, `Ctrl+Enter Location`, `Hold Tab Actions`). "Palette: Hide or show key hints"
  or the tray menu turns it off.
