//! What is in the window in front, read with UI Automation: the text selected in it, and the
//! commands it offers (ribbon and toolbar buttons, tabs, menu bar items, checkboxes).
//!
//! The shell calls `begin` when the palette opens, with the window that had the focus; a thread
//! reads both, off the keystroke path and at below-normal priority, and the results wait here:
//! `selection` for the Home view, `search` for typed queries. Nothing is kept past the next
//! `begin`, nothing is written to disk, and nothing is read from password fields, password
//! managers or the desktop itself.
//!
//! The scan is one cross-process call (`FindAllBuildCache`) for the whole window. Apps with huge
//! trees answer slowly and make the app itself work while they do, so a window is not scanned
//! again within `FRESH`, and a program whose last scan was slow only every `SLOW_AGAIN`.

use crate::model::{Action, Hit, Kind};
use crate::usage::{Picks, Usage};
use std::collections::HashMap;
use std::sync::{Condvar, Mutex};
use std::time::{Duration, Instant};
use windows::Win32::System::Variant::VARIANT;
use windows::Win32::Foundation::HWND;
use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_INPROC_SERVER, COINIT_MULTITHREADED};
use windows::Win32::UI::Accessibility::*;
use windows::Win32::System::Threading::{GetCurrentThread, SetThreadPriority, THREAD_PRIORITY_BELOW_NORMAL};
use windows::Win32::UI::WindowsAndMessaging::{GetClassNameW, GetWindowThreadProcessId};

/// A window scanned this lately keeps its commands.
const FRESH: Duration = Duration::from_secs(20);
/// A scan slower than this marks the program as heavy.
const SLOW: Duration = Duration::from_millis(1500);
const SLOW_AGAIN: Duration = Duration::from_secs(300);
/// Elements read from one window (each costs a call to learn what it sits in), and rows one
/// query may add.
const MAX_ELEMENTS: usize = 400;
pub const MAX_ROWS: usize = 3;
/// Longest selection kept, in characters (a paragraph is a fine query for the meaning search).
const MAX_SELECTION: usize = 400;
/// Longest selection kept as it is, to be asked about (chat.rs).
const RAW_SELECTION: usize = 6000;

/// Programs never read: what they show is not the palette's business.
const PRIVATE_PROGRAMS: &[&str] = &["keepass", "1password", "bitwarden", "lastpass", "dashlane", "enpass", "nordpass", "roboform"];

#[derive(Clone)]
struct El(IUIAutomationElement);
// Elements are used from the threads of the multithreaded apartment they were made in.
unsafe impl Send for El {}
unsafe impl Sync for El {}

#[derive(Clone)]
struct Cmd {
    name: String,
    /// The name, lowercased, for matching.
    lower: String,
    /// The toolbar, group or menu it sits in ("Image"), empty when unknown.
    group: String,
    /// Its shortcut when the name carries one ("Bold (Ctrl+B)" is `Bold`, `Ctrl+B`).
    keys: String,
    el: El,
}

#[derive(Default)]
struct Scan {
    seq: u64,
    hwnd: isize,
    /// The program's name ("Paint") and what to take its icon from.
    app: String,
    icon: String,
    selection: Option<String>,
    /// The same with its line breaks and up to a few thousand characters, for asking about it.
    selection_raw: Option<String>,
    /// The selection step has finished (with or without text).
    selection_done: bool,
    cmds: Vec<Cmd>,
    /// The command scan has finished (with or without commands).
    cmds_done: bool,
}

static STATE: (Mutex<Option<Scan>>, Condvar) = (Mutex::new(None), Condvar::new());

/// The commands of the window scanned last (and when), so the same window opened again within
/// `FRESH` has them at once; and the programs whose scan was slow, with when.
static PREVIOUS: Mutex<Option<(isize, Instant, Vec<Cmd>)>> = Mutex::new(None);
static SLOW_APPS: Mutex<Option<HashMap<String, Instant>>> = Mutex::new(None);

/// Start reading the window `hwnd` (0: none; the palette itself and the desktop are skipped).
/// Whatever was read before is dropped at once.
pub fn begin(hwnd: isize) {
    let seq = {
        let mut g = STATE.0.lock().unwrap();
        let seq = g.as_ref().map_or(0, |s| s.seq) + 1;
        let app = crate::wins::app_of(hwnd);
        let skip = hwnd == 0
            || app.is_none()
            || is_shell_window(hwnd)
            || app.as_ref().is_some_and(|(name, icon)| is_private(name) || is_private(icon));
        let (name, icon) = app.unwrap_or_default();
        *g = Some(Scan { seq, hwnd, app: name, icon, selection_done: skip, cmds_done: skip, ..Default::default() });
        if skip {
            STATE.1.notify_all();
            return;
        }
        seq
    };
    std::thread::spawn(move || unsafe {
        // Reading another program's tree is no urgent matter: it must not compete with typing.
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
        scan(seq, hwnd)
    });
}

fn is_private(s: &str) -> bool {
    let l = s.to_lowercase();
    PRIVATE_PROGRAMS.iter().any(|p| l.contains(p))
}

fn is_shell_window(hwnd: isize) -> bool {
    let mut buf = [0u16; 64];
    let n = unsafe { GetClassNameW(HWND(hwnd as *mut _), &mut buf) } as usize;
    let class = String::from_utf16_lossy(&buf[..n.min(buf.len())]);
    matches!(class.as_str(), "Progman" | "WorkerW" | "Shell_TrayWnd" | "Shell_SecondaryTrayWnd" | "NotifyIconOverflowWindow")
}

/// The text selected in the window `begin` was given, on one line, waiting up to `wait` for the
/// read to finish. None when nothing is selected (or nothing could be read).
pub fn selection(wait: Duration) -> Option<String> {
    let deadline = Instant::now() + wait;
    let mut g = STATE.0.lock().unwrap();
    loop {
        match g.as_ref() {
            None => return None,
            Some(s) if s.selection_done => return s.selection.clone(),
            Some(_) => {}
        }
        let left = deadline.checked_duration_since(Instant::now())?;
        g = STATE.1.wait_timeout(g, left).unwrap().0;
    }
}

/// (name, place) of each command found, waiting up to `wait` for the scan to finish: for
/// diagnostics (`examples/uia_probe.rs`).
pub fn commands(wait: Duration) -> Vec<(String, String)> {
    let deadline = Instant::now() + wait;
    let mut g = STATE.0.lock().unwrap();
    loop {
        match g.as_ref() {
            None => return Vec::new(),
            Some(s) if s.cmds_done => return s.cmds.iter().map(|c| (c.name.clone(), c.group.clone())).collect(),
            Some(_) => {}
        }
        let Some(left) = deadline.checked_duration_since(Instant::now()) else { return Vec::new() };
        g = STATE.1.wait_timeout(g, left).unwrap().0;
    }
}

/// Which opening of the palette the current read belongs to: it changes each time `begin` is called.
pub fn scan_id() -> u64 {
    STATE.0.lock().unwrap().as_ref().map_or(0, |s| s.seq)
}

/// Is some text selected in the window (read when the palette opened)? Does not wait.
pub fn has_selection() -> bool {
    STATE.0.lock().unwrap().as_ref().is_some_and(|s| s.selection_raw.is_some())
}

/// What a window shows, as text, for asking about it (chat.rs).
#[derive(Clone)]
pub struct WindowText {
    pub app: String,
    pub title: String,
    /// Its text, as the program offers it (a document's, else its labels in reading order).
    pub text: String,
    /// What was selected in it when the palette opened, with its line breaks.
    pub selection: Option<String>,
    /// A terminal: the newest text is at the end.
    pub terminal: bool,
}

/// The text of the window `begin` was given (and its selection). Reads the whole window through UI
/// Automation, which another program may be slow to answer: gives up after `wait`. Blocks: call from
/// a worker thread. None for no window, a private program, or a read that failed or took too long.
pub fn window_text(max_chars: usize, wait: Duration) -> Option<WindowText> {
    // The selection step is quick; wait for it so a selection is never missed.
    let _ = selection(Duration::from_millis(600));
    let (hwnd, app, selection) = {
        let g = STATE.0.lock().unwrap();
        let s = g.as_ref()?;
        (s.hwnd, s.app.clone(), s.selection_raw.clone())
    };
    if hwnd == 0 || app.is_empty() || is_private(&app) || is_shell_window(hwnd) {
        return None;
    }
    let (tx, rx) = std::sync::mpsc::channel();
    std::thread::spawn(move || {
        let r = unsafe {
            let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
            read_window_text(hwnd, max_chars)
        };
        let _ = tx.send(r);
    });
    let (title, text, terminal) = rx.recv_timeout(wait).ok()??;
    Some(WindowText { app, title, text, selection, terminal })
}

/// The program the selection was read from ("Notepad"), for the row that offers it.
pub fn app_name() -> String {
    STATE.0.lock().unwrap().as_ref().map(|s| s.app.clone()).unwrap_or_default()
}

unsafe fn scan(seq: u64, hwnd: isize) {
    let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    let Ok(ui) = CoCreateInstance::<_, IUIAutomation>(&CUIAutomation, None, CLSCTX_INPROC_SERVER) else {
        publish(seq, |s| (s.selection_done, s.cmds_done) = (true, true));
        return;
    };

    // The selection first: it is what the Home view waits for.
    let raw = read_selection(&ui, hwnd);
    publish(seq, |s| {
        s.selection = raw.as_deref().and_then(one_line);
        s.selection_raw = raw.filter(|t| t.chars().filter(|c| c.is_alphanumeric()).count() >= 2);
        s.selection_done = true;
    });

    // Then the commands, unless this window was read a moment ago or its program is a slow one.
    let app = STATE.0.lock().unwrap().as_ref().filter(|s| s.seq == seq).map(|s| s.app.clone()).unwrap_or_default();
    if let Some((h, at, cmds)) = PREVIOUS.lock().unwrap().as_ref() {
        if *h == hwnd && at.elapsed() < FRESH {
            let cmds = cmds.clone();
            publish(seq, |s| {
                s.cmds = cmds;
                s.cmds_done = true;
            });
            return;
        }
    }
    let slow = SLOW_APPS.lock().unwrap().as_ref().is_some_and(|m| m.get(&app).is_some_and(|t| t.elapsed() < SLOW_AGAIN));
    if slow {
        publish(seq, |s| s.cmds_done = true);
        return;
    }
    let started = Instant::now();
    let cmds = read_commands(&ui, hwnd);
    if started.elapsed() > SLOW {
        SLOW_APPS.lock().unwrap().get_or_insert_with(HashMap::new).insert(app, Instant::now());
    }
    *PREVIOUS.lock().unwrap() = Some((hwnd, Instant::now(), cmds.clone()));
    publish(seq, |s| {
        s.cmds = cmds;
        s.cmds_done = true;
    });
}

/// Change the current scan, if it is still the one `seq` started.
fn publish(seq: u64, f: impl FnOnce(&mut Scan)) {
    if let Some(s) = STATE.0.lock().unwrap().as_mut().filter(|s| s.seq == seq) {
        f(s);
    }
    STATE.1.notify_all();
}

unsafe fn read_selection(ui: &IUIAutomation, hwnd: isize) -> Option<String> {
    let focused = ui.GetFocusedElement().ok()?;
    // The palette may already have taken the focus, or the focus may be in another program.
    let mut pid = 0u32;
    GetWindowThreadProcessId(HWND(hwnd as *mut _), Some(&mut pid));
    if focused.CurrentProcessId().ok()? != pid as i32 {
        return None;
    }
    if focused.CurrentIsPassword().ok()?.as_bool() {
        return None;
    }
    let pattern: IUIAutomationTextPattern = focused.GetCurrentPatternAs(UIA_TextPatternId).ok()?;
    let ranges = pattern.GetSelection().ok()?;
    if ranges.Length().ok()? < 1 {
        return None;
    }
    let text = ranges.GetElement(0).ok()?.GetText(RAW_SELECTION as i32).ok()?.to_string();
    Some(text)
}

/// The text on one line, trimmed and cut; None when it holds nothing worth a query.
fn one_line(text: &str) -> Option<String> {
    let mut out = String::new();
    for w in text.split_whitespace() {
        if !out.is_empty() {
            out.push(' ');
        }
        out.push_str(w);
        if out.chars().count() >= MAX_SELECTION {
            break;
        }
    }
    let out: String = out.chars().take(MAX_SELECTION).collect();
    (out.chars().filter(|c| c.is_alphanumeric()).count() >= 2).then_some(out)
}

/// (title, text, is a terminal) of a window.
unsafe fn read_window_text(hwnd: isize, max_chars: usize) -> Option<(String, String, bool)> {
    let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
    let ui = CoCreateInstance::<_, IUIAutomation>(&CUIAutomation, None, CLSCTX_INPROC_SERVER).ok()?;
    let root = ui.ElementFromHandle(HWND(hwnd as *mut _)).ok()?;
    let title = root.CurrentName().map(|n| n.to_string()).unwrap_or_default();
    let mut class = [0u16; 64];
    let n = GetClassNameW(HWND(hwnd as *mut _), &mut class) as usize;
    let class = String::from_utf16_lossy(&class[..n.min(class.len())]);
    let terminal = matches!(class.as_str(), "CASCADIA_HOSTING_WINDOW_CLASS" | "ConsoleWindowClass" | "mintty" | "VirtualConsoleClass");

    // Documents, editors and terminals hand over their whole text through TextPattern.
    let mut parts: Vec<String> = Vec::new();
    // Only what is on screen: another tab's text, in a program with tabs, is not what is being looked at.
    let has_text = ui
        .CreateAndCondition(
            &ui.CreatePropertyCondition(UIA_IsTextPatternAvailablePropertyId, &VARIANT::from(true)).ok()?,
            &ui.CreatePropertyCondition(UIA_IsOffscreenPropertyId, &VARIANT::from(false)).ok()?,
        )
        .ok()?;
    if let Ok(found) = root.FindAll(TreeScope_Descendants, &has_text) {
        for i in 0..found.Length().unwrap_or(0).min(12) {
            let Ok(el) = found.GetElement(i) else { continue };
            if el.CurrentIsPassword().is_ok_and(|p| p.as_bool()) {
                continue;
            }
            let Ok(pattern) = el.GetCurrentPatternAs::<IUIAutomationTextPattern>(UIA_TextPatternId) else { continue };
            let Ok(range) = pattern.DocumentRange() else { continue };
            let Ok(text) = range.GetText(max_chars as i32 * 2) else { continue };
            let text = text.to_string();
            if text.trim().chars().count() >= 20 && !parts.iter().any(|p| p.contains(text.trim()) || text.contains(p.as_str())) {
                parts.push(text.trim().to_string());
            }
        }
    }
    parts.sort_by_key(|p| std::cmp::Reverse(p.chars().count()));
    let mut text = parts.join("\n\n");

    // Otherwise what is written on the screen: labels, list items, links, in reading order.
    if text.chars().filter(|c| !c.is_whitespace()).count() < 200 {
        text = read_labels(&ui, &root).unwrap_or(text);
    }
    (!text.trim().is_empty()).then_some((title, text, terminal))
}

/// The names of the visible text-bearing elements of a window, top to bottom, left to right.
unsafe fn read_labels(ui: &IUIAutomation, root: &IUIAutomationElement) -> Option<String> {
    let cache = ui.CreateCacheRequest().ok()?;
    cache.AddProperty(UIA_NamePropertyId).ok()?;
    cache.AddProperty(UIA_BoundingRectanglePropertyId).ok()?;
    cache.SetTreeScope(TreeScope_Element).ok()?;
    let is_type = |t: UIA_CONTROLTYPE_ID| ui.CreatePropertyCondition(UIA_ControlTypePropertyId, &VARIANT::from(t.0));
    let mut kinds = is_type(UIA_TextControlTypeId).ok()?;
    for t in [UIA_HyperlinkControlTypeId, UIA_ListItemControlTypeId, UIA_DataItemControlTypeId, UIA_TreeItemControlTypeId, UIA_HeaderItemControlTypeId] {
        kinds = ui.CreateOrCondition(&kinds, &is_type(t).ok()?).ok()?;
    }
    let cond = ui.CreateAndCondition(&kinds, &ui.CreatePropertyCondition(UIA_IsOffscreenPropertyId, &VARIANT::from(false)).ok()?).ok()?;
    let found = root.FindAllBuildCache(TreeScope_Descendants, &cond, &cache).ok()?;
    let mut items: Vec<(i32, i32, String)> = Vec::new();
    for i in 0..found.Length().ok()?.min(1500) {
        let Ok(el) = found.GetElement(i) else { continue };
        let Ok(name) = el.CachedName() else { continue };
        let name = name.to_string();
        let name = name.trim();
        if name.is_empty() {
            continue;
        }
        let r = el.CachedBoundingRectangle().unwrap_or_default();
        items.push((r.top / 8, r.left, name.to_string()));
    }
    items.sort_by_key(|(row, left, _)| (*row, *left));
    items.dedup_by(|a, b| a.2 == b.2);
    let mut out = String::new();
    let mut row = i32::MIN;
    for (r, _, name) in items {
        if !out.is_empty() {
            out.push(if r == row { ' ' } else { '\n' });
        }
        row = r;
        out.push_str(&name);
    }
    Some(out)
}

unsafe fn read_commands(ui: &IUIAutomation, hwnd: isize) -> Vec<Cmd> {
    match read_commands_inner(ui, hwnd) {
        Ok(c) => c,
        Err(_) => Vec::new(),
    }
}

unsafe fn read_commands_inner(ui: &IUIAutomation, hwnd: isize) -> windows::core::Result<Vec<Cmd>> {
    let root = ui.ElementFromHandle(HWND(hwnd as *mut _))?;

    let cache = ui.CreateCacheRequest()?;
    cache.AddProperty(UIA_NamePropertyId)?;
    cache.AddProperty(UIA_ControlTypePropertyId)?;
    cache.SetTreeScope(TreeScope_Element)?;

    let is_type = |t: UIA_CONTROLTYPE_ID| ui.CreatePropertyCondition(UIA_ControlTypePropertyId, &VARIANT::from(t.0));
    let mut kinds = is_type(UIA_ButtonControlTypeId)?;
    for t in [UIA_SplitButtonControlTypeId, UIA_MenuItemControlTypeId, UIA_TabItemControlTypeId, UIA_CheckBoxControlTypeId] {
        kinds = ui.CreateOrCondition(&kinds, &is_type(t)?)?;
    }
    let usable = ui.CreateAndCondition(
        &ui.CreatePropertyCondition(UIA_IsEnabledPropertyId, &VARIANT::from(true))?,
        &ui.CreatePropertyCondition(UIA_IsOffscreenPropertyId, &VARIANT::from(false))?,
    )?;
    let cond = ui.CreateAndCondition(&kinds, &usable)?;

    let walker = ui.ControlViewWalker()?;
    let found = root.FindAllBuildCache(TreeScope_Descendants, &cond, &cache)?;
    let n = found.Length()?.min(MAX_ELEMENTS as i32);
    let mut out: Vec<Cmd> = Vec::new();
    for i in 0..n {
        let Ok(el) = found.GetElement(i) else { continue };
        let Ok(name) = el.CachedName() else { continue };
        let name = name.to_string();
        let name = name.trim();
        if name.chars().count() < 2 || name.chars().count() > 60 || name.contains('\n') {
            continue;
        }
        let mut group = String::new();
        if let Ok(parent) = walker.GetParentElementBuildCache(&el, &cache) {
            let kind = parent.CachedControlType().unwrap_or_default();
            // Web pages and documents are content, not the program's commands; the title bar's
            // buttons (minimise, close) every window has.
            if kind == UIA_DocumentControlTypeId || kind == UIA_TitleBarControlTypeId || kind == UIA_HeaderControlTypeId {
                continue;
            }
            group = parent.CachedName().map(|n| n.to_string()).unwrap_or_default().trim().to_string();
            if group.chars().count() > 40 || group.eq_ignore_ascii_case(name) {
                group.clear();
            }
        }
        let (name, keys) = split_shortcut(name);
        // The window's own buttons are the window rows' business (and every window has them).
        if matches!(name.to_lowercase().as_str(), "minimize" | "maximize" | "restore" | "close" | "system") {
            continue;
        }
        let lower = name.to_lowercase();
        if out.iter().any(|c| c.lower == lower && c.group == group) {
            continue;
        }
        out.push(Cmd { name, lower, group, keys, el: El(el) });
    }
    Ok(out)
}

/// "Bold (Ctrl+B)" as ("Bold", "Ctrl+B"): many toolbars put the shortcut in the name.
fn split_shortcut(name: &str) -> (String, String) {
    if let Some(inner) = name.strip_suffix(')') {
        if let Some((head, keys)) = inner.rsplit_once(" (") {
            let first = keys.split('+').next().unwrap_or("");
            let key_like = matches!(first, "Ctrl" | "Alt" | "Shift" | "Win") || (first.starts_with('F') && first[1..].parse::<u8>().is_ok());
            if key_like && !head.trim().is_empty() {
                return (head.trim().to_string(), keys.to_string());
            }
        }
    }
    (name.to_string(), String::new())
}

/// Rows for the commands of the window in front that the typed `words` name: every word must
/// begin a word of the command. At most `MAX_ROWS`, best first; scores are on the scale of
/// `search::run`'s other rows.
pub fn search(lq: &str, words: &[&str], picks: Option<&Picks>) -> Vec<Hit> {
    if lq.chars().count() < 2 {
        return Vec::new();
    }
    let g = STATE.0.lock().unwrap();
    let Some(s) = g.as_ref() else { return Vec::new() };
    let mut found: Vec<(i32, &Cmd, usize)> = Vec::new();
    for (i, c) in s.cmds.iter().enumerate() {
        let cw: Vec<&str> = c.lower.split(|ch: char| !ch.is_alphanumeric()).filter(|w| !w.is_empty()).collect();
        if !words.iter().all(|w| cw.iter().any(|x| x.starts_with(w))) {
            continue;
        }
        let base = if c.lower == lq {
            CMD_EXACT
        } else if c.lower.starts_with(lq) {
            CMD_PREFIX
        } else {
            CMD_WORDS
        };
        // Short names are what people mean; "Copy" before "Copy formatting as plain text".
        let score = base - (c.lower.chars().count() as i32).min(40);
        found.push((score, c, i));
    }
    found.sort_by(|a, b| b.0.cmp(&a.0));
    found
        .into_iter()
        .take(MAX_ROWS)
        .map(|(score, c, i)| {
            // Where it is, or the keys that do it: the keys are the more useful.
            let place = match (c.keys.is_empty(), c.group.is_empty()) {
                (false, _) => format!("In {} \u{b7} {}", s.app, c.keys),
                (true, false) => format!("In {} \u{b7} {}", s.app, c.group),
                (true, true) => format!("In {}", s.app),
            };
            let mut hit = Hit {
                title: c.name.clone(),
                subtitle: place,
                kind: Kind::Control,
                action: Action::Ui { hwnd: s.hwnd, scan: s.seq, index: i, icon: s.icon.clone() },
                score: 0,
            };
            hit.score = score + Usage::pick_bonus(picks, &hit.key());
            hit
        })
        .collect()
}

/// Scores of a command named exactly, by its beginning, or by the beginnings of its words.
const CMD_EXACT: i32 = 330;
const CMD_PREFIX: i32 = 290;
const CMD_WORDS: i32 = 250;

/// Press command `index` of scan `scan`. The window is brought to the front first (the palette
/// still has the foreground, which lets it hand it on); the press follows a moment later on a
/// thread of its own, because some apps answer an invoke only when the call returns.
pub fn press(hwnd: isize, scan: u64, index: usize) {
    let el = {
        let g = STATE.0.lock().unwrap();
        match g.as_ref().filter(|s| s.seq == scan).and_then(|s| s.cmds.get(index)) {
            Some(c) => El(c.el.0.clone()),
            None => return,
        }
    };
    crate::wins::focus(hwnd);
    std::thread::spawn(move || unsafe {
        let _ = CoInitializeEx(None, COINIT_MULTITHREADED);
        std::thread::sleep(Duration::from_millis(90));
        // The whole wrapper moves into the thread (it is what is Send), then the element comes out.
        let wrapper = el;
        let el = wrapper.0;
        if let Ok(p) = el.GetCurrentPatternAs::<IUIAutomationInvokePattern>(UIA_InvokePatternId) {
            if p.Invoke().is_ok() {
                return;
            }
        }
        if let Ok(p) = el.GetCurrentPatternAs::<IUIAutomationTogglePattern>(UIA_TogglePatternId) {
            if p.Toggle().is_ok() {
                return;
            }
        }
        if let Ok(p) = el.GetCurrentPatternAs::<IUIAutomationSelectionItemPattern>(UIA_SelectionItemPatternId) {
            if p.Select().is_ok() {
                return;
            }
        }
        if let Ok(p) = el.GetCurrentPatternAs::<IUIAutomationExpandCollapsePattern>(UIA_ExpandCollapsePatternId) {
            let _ = p.Expand();
        }
    });
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_selection_is_one_short_line() {
        assert_eq!(one_line("  two\r\n  lines\t here ").as_deref(), Some("two lines here"));
        assert_eq!(one_line(" \n\t "), None);
        assert_eq!(one_line("?!"), None);
        assert!(one_line(&"word ".repeat(500)).unwrap().chars().count() <= MAX_SELECTION);
    }

    #[test]
    fn shortcuts_in_names_are_split_off() {
        assert_eq!(split_shortcut("Bold (Ctrl+B)"), ("Bold".into(), "Ctrl+B".into()));
        assert_eq!(split_shortcut("Save (F12)"), ("Save".into(), "F12".into()));
        assert_eq!(split_shortcut("Zoom (100%)"), ("Zoom (100%)".into(), String::new()));
        assert_eq!(split_shortcut("Paste"), ("Paste".into(), String::new()));
    }

    #[test]
    fn private_programs_are_not_read() {
        assert!(is_private("C:\\Program Files\\KeePass Password Safe 2\\KeePass.exe"));
        assert!(!is_private("C:\\Windows\\notepad.exe"));
    }
}
