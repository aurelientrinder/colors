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
- **Ranking that knows where you opened it from.** Key the learned picks (`usage.rs`) on the
  foreground app as well as the typed prefix: `rep` opened over Excel finds the report workbook,
  over VS Code the repo. Same data, one more key.
- **A Home that guesses the next thing.** Time of day, weekday, the Wi-Fi network and whether the
  external display is plugged in say "at work, Monday morning" well enough. A small model over
  the usage log puts the likely next app or file at the top of Home.

## The window in front
Palette already sees the foreground window (it returns focus there) and, with UI Access, can
read and drive elevated windows too. Nothing uses what is in them yet.
- **Search the app's own commands.** Walk the foreground window's menus, ribbon and toolbar with
  UI Automation and list them as rows: in Paint, "resize" presses the Resize button; in an app
  with deep menus, "export pdf" finds File › Export › PDF. macOS's Help menu search, for every
  app. Cache per app and window class, since walking a ribbon is slow.
- **Selected text as the query.** Text selected when the palette opens (UIA `TextPattern`, else a
  copy kept off the clipboard history) fills the field, with actions for what it is: calculate,
  convert, define, open the URL, format the JSON, change case, find the files that mention it.
  Enter pastes the result back over the selection.
- **"This".** Requests that point at the window in front: "close this", "move this to the other
  screen", "find the file this is", "pin this". The plan model needs one more slot.
- **Click by keyboard.** Hold a key and every clickable thing on screen gets a letter (UI
  Automation's invokable elements), as Vimium does in a browser. Two keystrokes press any button
  in any app, elevated ones included.

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
- **Show the passage, not just the file.** The semantic index already knows which chunk
  matched; show it under the row with the matching words highlighted. "When does my lease end"
  shows the sentence, and Enter opens the PDF at that page.
- **Find similar** in a file's actions: its own vectors as the query. The same distances flag
  near-duplicate documents for a tidy-up.
- **File chores in plain words, shown before they run.** "Move last month's screenshots into
  Pictures\2026-09": the plan model writes the steps, the palette lists the files they would
  touch, Enter does it and Undo puts them back.

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
- **Resume yesterday.** Keep a local log of which windows and files were in front, and when
  (`ShellHooks` and the file watcher already see both). "What was I doing Tuesday afternoon"
  lists them; "resume yesterday" opens them again where they were. Stays on the PC.
- **Answers about the PC itself.** "port 3000" shows the process listening, with End task; "who
  has this file open" asks the Restart Manager and offers to close it; "what's using my camera"
  reads the consent store; "why is my fan loud" sorts the system card's processes by CPU.
- **Hold the Copilot key to talk.** It is already intercepted. Speech to text offline (Windows'
  recogniser, or Whisper on the NPU through the same OpenVINO setup) feeds the plan model.
- **Shell history.** `>` searches past PowerShell commands (PSReadLine's
  `ConsoleHost_history.txt`) before running anything new; Enter runs one again in Terminal.
- **Screen tools.** "Copy text from screen" (drag a box, `Windows.Media.Ocr` reads it) and "pick
  colour" (a magnifier, Enter copies the hex) as plain rows, without a separate PowerToys.
- **Expand as you type, anywhere.** The keyboard hook sees every key, so a snippet trigger
  (`;addr`) or `=2*3=` typed in any app can be replaced in place. Pairs with "Teach it in one
  sentence".

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
