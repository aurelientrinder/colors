# Architecture: from launcher to AI hub

A short note on how the pieces fit, so the next features have somewhere to go. It describes what
exists (marked **now**) and what is meant to be built on it (**next**).

## The experience rules

These decide what gets built and how it shows. Capability that breaks them does not ship.

1. **One input field.** Everything starts as typing (or a held key). No second window, no
   toolbar, no mode switch to learn. A conversation, if there is one, grows out of the same box.
2. **Quiet by default.** The palette shows nothing it was not asked for, except one line when a
   suggestion is clearly useful (a selection to work on, a likely next step). Never a popup,
   never a badge, never two suggestions at once.
3. **Answers beat menus.** The best result is the answer or the finished action, not a list that
   leads to it. Rows stay two lines; detail opens under the selected row and closes with it.
4. **Plans are shown, then run.** Anything with more than one effect lists its steps first. One
   key runs it, one key undoes it. Sending, deleting, paying and anything outward-facing always
   stops for a clear yes.
5. **Local first, and visible when it is not.** Reading the screen, searching files and running
   the small models all stay on the PC. Anything that leaves it is named on the row and needs a
   yes each time.
6. **Fast is a feature.** Keystrokes are never blocked on a model. Slow things (a model, a
   cross-process read of another window) run beside the typing and arrive in place, without the
   list jumping.

## Layers

```
 you ── type / select / hold a key
        │
 WinUI shell (winui/)      input, rows, previews, motion; asks, never decides
        │  C ABI (ffi.rs)
 palette-core (Rust)       the router: turns input into hits and plans
   ├─ instant tier         names: apps, settings, files, windows        (search.rs)
   ├─ rules                calculator, quick settings, requests         (quick.rs, intent.rs)
   ├─ senses               what is on the screen and in the window      (uia.rs)   ← now
   ├─ plan model           small model on the NPU for requests          (planner.rs)
   ├─ meaning              semantic index of documents and items        (semantic.rs)
   └─ tools                every action as a typed call                 (next)
        │
 helpers (own processes)   palette-semantic (embeddings), palette-plan (plan model), palette-mft
```

The router tries the cheapest thing that can answer and only then the next:
**rules → plan model (270M, NPU) → a larger local model (3–4B, NPU) → a cloud model**. Each step
up happens after typing pauses, shows in place when it lands, and the last one needs a yes.

## Senses: reading the window in front

**now** (`uia.rs`, UI Automation, in-process, unelevated, works over elevated windows with UI
Access):

- *Selection.* The text selected in the focused control when the palette opens. Read through
  TextPattern with a short timeout; never through the clipboard, which would show up in
  clipboard history.
- *Commands.* The invokable controls of the window in front (ribbon and toolbar buttons, tabs,
  menu bar items, checkboxes), read in one cached call when the palette opens, matched by name as
  you type, pressed through Invoke / Toggle / ExpandCollapse / SelectionItem.

- *Window text.* **now** (`uia.rs`, `window_text`): read when a question is asked, not before. The
  on-screen elements that offer a TextPattern (documents, editors, terminals) give their whole text;
  failing that, the visible labels in reading order. Off-screen elements (another tab's) are left
  out. Capped, and cut at the start (or, for a terminal, the end).

**next:** OCR when UI Automation has nothing (`Windows.Media.Ocr`), a vision model only when layout is
the question. Cheap first, always.

## The brain: asking about the window

**now** (`chat.rs`, `AskView.cs`): the first model above the 270M plan model. A chat GGUF (Qwen3.5 4B
here) runs behind llama.cpp's server on the GPU, on 127.0.0.1. The engine readies the question (reads
the window and starts the server in parallel, builds the prompt) and returns the port and the request;
the shell streams the reply into a card under the row. Local only by decision: no key, no account,
nothing sent. It loads on demand and is released after ten idle minutes (rule 6: keystrokes are never
blocked on it, the row is offered by rules and the model starts only on Enter).

The router's rung above this, a cloud model, is not built; when it is, it is a second row that says
so and asks for a yes each time (rule 5).

## Tools (next)

Every action becomes a typed call a model can make: a name, a JSON schema for its arguments, what
it reads and what it changes, and whether it can be undone. The plan model's `Step`s
(`intent.rs`) are the first such tools; quick settings, window control, file operations, the
senses above and (via MCP) mail, calendar and the like follow the same shape. A tool declares one
of three effects, which decides how the palette treats it:

| Effect | Examples | Treatment |
|---|---|---|
| reads | search files, read a window, selection | runs freely |
| changes, undoable | snap windows, toggle a setting, move files | runs on Enter, listed in the undo log |
| outward or permanent | send, delete, pay, post | always asks; never part of an unattended plan |

The same registry is what an MCP server would publish, so Claude Code, Claude Desktop or any agent
can use the file index, meaning search, window control and screen reader.

## Memory and trust (next)

- A local, readable, editable store for what the palette learns about you (people, projects,
  preferences), next to the usage data it already keeps (`usage.rs`).
- Per-app permissions: allowed to read, allowed to act, never. Password managers and banking
  apps default to never.
- A log of everything read and done, with undo; a visible mark whenever something is being read.

## Order of work

1. Senses and the passage (now): selection, app commands, the matching passage under a result.
2. The tool registry, with `intent.rs`'s steps moved onto it, and plans shown as a card.
3. "Ask about this window": window text into a local model, answers in the same field. (**done**;
   follow-up questions, the answer as a conversation, and "write in place" come next on the same
   model.)
4. A larger local model behind the router; MCP in both directions.
5. Driving apps ("show it once"), triggers, memory.
