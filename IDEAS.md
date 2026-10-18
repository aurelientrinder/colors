# Ideas

Things we could add or refine, so they are not forgotten. Roughly in the order worth doing.
Cross one off (or move it to the README) when it ships.

## Results that learn
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

## An AI hub that sees and acts
The direction: Palette understands what is on the screen and acts on it for you, quietly. The
parts are mostly in place (UI Automation reach into every window, UI Access, local models on the
NPU, the semantic index, the plan model). What is missing is a tool layer and a stronger brain.
See `ARCHITECTURE.md` for how the pieces fit; the rules of the experience come first: one
input field, answers only when asked or clearly useful, nothing sent or deleted without a clear
yes, and local unless you say otherwise.

Foundation:
- **Every action is a tool.** Open, snap, toggle, search, read a window, type, click: each with a
  typed description a model can call (`ARCHITECTURE.md`, "Tools").
- **A brain that steps up only when needed.** Rules, then the 270M plan model, then a 3-4B model
  on the NPU, then a cloud model for hard multi-step work, each step visible and the last one
  only with your yes.
- **A screen reader that is cheap first**: UI Automation text, then OCR, then a vision model only
  when the layout matters.
- **Plans shown before they run**: a card of steps, Enter to run, every step in an undo log;
  sending, deleting and paying always stop to ask.

Understanding the screen:
- **"What's this?"** One key explains the window in front: an error dialog and its likely fix, a
  confusing settings page, a chart, a clause in a contract.
- **Ask about the window.** "Summarise this", "what does this email want from me", "what changed
  in this diff"; follow-ups keep the window as context.
- **Errors you keep hitting.** Copy the same error twice, or meet an error dialog, and the
  palette offers what your files, past fixes and the web say.
- **Screen memory, text only, opt-in.** A Recall without screenshots: UI Automation text,
  searchable by meaning ("the price I saw on that site Thursday"). Password managers and banking
  apps are never read; a visible marker shows whenever it is reading.
- **Meetings.** Live captions into notes and action items; "what did they say about the budget?"
  mid-call.

Taking action:
- **Drive apps for you.** "Fill this form from that PDF", "copy these 30 rows into Excel": pressing
  buttons and typing through UI Automation, no integration needed per app.
- **Show it once.** Do a task while the palette watches; it becomes a named automation with
  blanks for what changes.
- **Triggers in plain words.** "When an invoice lands in Downloads, file it by vendor and date",
  "when I plug in the dock, set up my work layout".
- **Glue between apps.** "Send this to Marc in Teams", "make a calendar event from this email":
  always drafts, never sends unasked.

Helping with the work:
- **Write in place.** Select text anywhere: rewrite, shorten, change tone, translate, fix; or
  draft a reply to the email in front in your own voice.
- **Answers from your documents, with sources**, to the page.
- **Project packs.** "Open the Acme project" gathers its files, windows and notes, learned from
  what you use together.
- **End-of-day summary** from the activity log, for a standup note or timesheet.
- **Coding help** when the editor is in front: explain the selection, find uses across repos,
  "why is this failing?" from the terminal's text.
- **Quiet suggestions**: a single line in the footer, never a popup ("this looks like the PDF you
  filed under Taxes last year; file it there?").

A hub:
- **Palette uses MCP tools** (mail, calendar, GitHub, Notion) the way it uses its own.
- **Palette is an MCP server**: its file index, meaning search, window control and screen reader
  offered to Claude Code, Claude Desktop or any agent. Small, since it is packaging.
- **Conversations.** A request needing follow-ups opens the palette into a chat that keeps the
  screen context; Esc collapses it, the hotkey brings it back.
- **It remembers you** (people, projects, preferences), kept on the PC, readable and editable.
- **Trust**: per-app permissions (read, act, never), a log of everything read and done with undo,
  local by default.

## Files
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
- **Named window layouts.** "Work" opens the editor, browser and chat in saved positions. The
  plan model already understands snapping, so "set up my morning" is a small step.
- **Teach it in one sentence.** "When I type `inv`, open the invoices folder": user-defined
  aliases and snippets, written in plain words and read by the NPU model.
- **Undo for plans**, not only quick settings (`Views.cs`). Save window positions before a snap or
  arrange step so Undo can put them back.
- **Resume yesterday.** Keep a local log of which windows and files were in front, and when
  (`ShellHooks` and the file watcher already see both). "What was I doing Tuesday afternoon"
  lists them; "resume yesterday" opens them again where they were. Stays on the PC.
- **Answers about the PC itself, the rest.** "why is my fan loud" sorts the system card's
  processes by CPU; "what's using the network"; "what is using this drive"; "when did I install X".
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
- Answers about the PC itself (`pcinfo.rs`): "port 3000" shows the program listening (End task in its
  actions), "what's using my camera / microphone" reads Windows' privacy record, and a file's actions
  list the programs that have it open, each with End. Still to do: UDP ports, a graceful Close
  before End, the CPU and network questions.
- Quick actions on files (`fileops.rs`): copy, compress to ZIP, rename, move to, Recycle Bin, with
  Undo for the zip, rename and move. Still to do: a key from the results list (F2 renames), several
  files at once, extract a zip, send to / share, "copy to" beside "move to".
- Clipboard history by meaning (`clip.rs`, `Clipboard.cs`): `clip ...` lists and finds what was copied,
  by words and then by meaning, with the kind of each (URL, email, path, colour, phone, number, JSON).
  Still to do: more per-kind actions (copy a colour as rgb, format JSON, call a number), paste straight
  into the window behind, entries beyond Windows' 25 surviving a restart (opt-in, encrypted), and
  images.
- Conversions typed into the search field (`convert.rs`): units, dates and time zones offline,
  currency through the web answers. Still to do: more city names (any place through the geocoder),
  percentages ("15% of 80"), and "now in" for several zones at once.
- The window in front (`uia.rs`): selected text offered on Home, the app's own buttons, tabs and
  menu bar found by typing, and the matching passage under documents found by meaning. Still to do
  from that group: items inside closed menus, "this" in requests, click by keyboard, and the page
  number for PDF passages (the index drops page breaks).
- Arrow keys walk folders: Left on a file or folder shows where it is (and again goes up a level),
  Right goes down into a folder, Right on a file returns to the search. Typed paths (`C:\...`,
  `~\...`) list the folder.
- "Open file location" works for paths with spaces.
- Results learn what you pick for what you type: every prefix of the query (up to 8 characters)
  boosts what you opened, so `n` finds Notepad first once you have chosen it for `note`.
- Key-hint footer in the search card: the keys that apply to the selected row (`-> Browse`,
  `<- Show folder`, `Ctrl+Enter Location`, `Hold Tab Actions`). "Palette: Hide or show key hints"
  or the tray menu turns it off.
