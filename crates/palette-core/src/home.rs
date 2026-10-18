//! What Start offers besides search: the home view shown before anything is typed (pinned
//! items, most used, Windows' recent files, a way into the full app list) and the actions on a
//! single result (run as administrator, open file location, pin, uninstall, ...).

use crate::actions;
use crate::model::{Action, Hit, Kind};
use crate::usage::Usage;
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::path::{Path, PathBuf};

// ---------------------------------------------------------------- pins

/// Items pinned to the home view, in the order shown.
#[derive(Default, Serialize, Deserialize)]
pub struct Pins {
    list: Vec<Hit>,
}

impl Pins {
    fn file() -> PathBuf {
        crate::paths::data_dir().join("pins.json")
    }

    pub fn load() -> Pins {
        std::fs::read(Self::file()).ok().and_then(|b| serde_json::from_slice(&b).ok()).unwrap_or_default()
    }

    fn save(&self) {
        if let Ok(b) = serde_json::to_vec_pretty(self) {
            let _ = std::fs::write(Self::file(), b);
        }
    }

    pub fn list(&self) -> &[Hit] {
        &self.list
    }

    pub fn position(&self, key: &str) -> Option<usize> {
        self.list.iter().position(|h| h.key() == key)
    }

    pub fn pin(&mut self, hit: &Hit) {
        if self.position(&hit.key()).is_none() {
            let mut h = hit.clone();
            h.score = 0;
            self.list.push(h);
            self.save();
        }
    }

    pub fn unpin(&mut self, key: &str) {
        self.list.retain(|h| h.key() != key);
        self.save();
    }

    /// Move a pin one place toward the search field (`toward_top == false`) or away from it.
    pub fn shift(&mut self, key: &str, later: bool) {
        let Some(i) = self.position(key) else { return };
        let j = if later { i + 1 } else { i.wrapping_sub(1) };
        if j < self.list.len() {
            self.list.swap(i, j);
            self.save();
        }
    }
}

/// Can this hit be pinned? Windows come and go; calculator and web rows echo the query.
pub fn pinnable(hit: &Hit) -> bool {
    !matches!(hit.kind, Kind::Window | Kind::Calc | Kind::Web | Kind::Toggle | Kind::Plan | Kind::Control)
        && !matches!(&hit.action, Action::Builtin(id) if id == "allapps" || id == SELECTION)
}

// ---------------------------------------------------------------- home

pub const ALL_APPS: &str = "allapps";
/// The row offering the text selected in the window behind the palette (see uia.rs).
pub const SELECTION: &str = "selection";

/// The Home row for selected text `text`: Enter puts it in the search field.
pub fn selection_row(text: &str, app: &str) -> Hit {
    let shown: String = if text.chars().count() > 70 { text.chars().take(69).chain(['\u{2026}']).collect() } else { text.to_string() };
    Hit {
        title: format!("\u{201c}{shown}\u{201d}"),
        subtitle: if app.is_empty() { "Selected text".into() } else { format!("Selected in {app}") },
        kind: Kind::Command,
        action: Action::Builtin(SELECTION.into()),
        score: 0,
    }
}
/// Rows on the home view, "All apps" included: as many as a search shows (`search::MAX_HITS`), so
/// the panel keeps its height when typing starts.
const HOME_ROWS: usize = crate::search::MAX_HITS;
/// Of the rows left after the pins, how many go to the most used items (recent files get the rest).
const MOST_USED: usize = 2;
/// Recent files read for search ranking as well (a recently opened file outranks its namesakes).
const RECENT_RANKED: usize = 150;

fn all_apps_row() -> Hit {
    Hit {
        title: "All apps".into(),
        subtitle: "Every installed app, A to Z".into(),
        kind: Kind::Command,
        action: Action::Builtin(ALL_APPS.into()),
        score: 0,
    }
}

/// The list shown before anything is typed, nearest the search field first: pinned items, the
/// few things you open most, files Windows saw you open recently, then "All apps", in
/// `HOME_ROWS` rows. When one kind runs short the other fills its place.
pub fn home(pins: &Pins, usage: &Usage, selection: Option<Hit>) -> Vec<Hit> {
    // The last row is "All apps"; selected text, when there is some, takes the first (nearest the field).
    let room = HOME_ROWS - 1 - selection.is_some() as usize;
    let mut out: Vec<Hit> = Vec::new();
    let mut seen: HashSet<String> = HashSet::new();
    let mut add = |out: &mut Vec<Hit>, list: &mut dyn Iterator<Item = Hit>, until: usize| {
        while out.len() < until {
            let Some(h) = list.next() else { break };
            if seen.insert(h.key()) {
                out.push(h);
            }
        }
    };
    let mut pinned = pins.list().iter().cloned();
    let mut used = usage.top(room + pins.list().len()).into_iter();
    let recent = recent_cached();
    let mut recent = recent.hits.iter().cloned();
    add(&mut out, &mut pinned, room);
    let until = (out.len() + MOST_USED).min(room);
    add(&mut out, &mut used, until);
    add(&mut out, &mut recent, room);
    add(&mut out, &mut used, room); // few recent files: more of the most used
    if let Some(s) = selection {
        out.insert(0, s);
    }
    out.push(all_apps_row());
    out
}

/// The recent files, newest first, and each one's place in that order by lowercased path (for
/// ranking search results).
pub struct Recent {
    pub hits: Vec<Hit>,
    pub rank: std::collections::HashMap<String, i32>,
}

impl Recent {
    fn new(hits: Vec<Hit>) -> Recent {
        let rank = hits
            .iter()
            .enumerate()
            .filter_map(|(i, h)| match &h.action {
                Action::Open(p) => Some((p.to_lowercase(), i as i32)),
                _ => None,
            })
            .collect();
        Recent { hits, rank }
    }
}

/// Recent files without waiting on the disk: reading the Recent folder takes ~15 ms, so the home
/// view uses the last reading and refreshes it in the background when it is over 30 s old. Shared,
/// not copied: every keystroke's search reads it.
pub fn recent_cached() -> std::sync::Arc<Recent> {
    use std::sync::{Arc, Mutex};
    use std::time::{Duration, Instant};
    static CACHE: Mutex<Option<(Instant, Arc<Recent>)>> = Mutex::new(None);
    static REFRESHING: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);
    let read = || Arc::new(Recent::new(recent_files(RECENT_RANKED)));

    let mut c = CACHE.lock().unwrap();
    match &*c {
        Some((at, list)) => {
            if at.elapsed() > Duration::from_secs(30) && !REFRESHING.swap(true, std::sync::atomic::Ordering::SeqCst) {
                std::thread::spawn(move || {
                    let fresh = read();
                    *CACHE.lock().unwrap() = Some((Instant::now(), fresh));
                    REFRESHING.store(false, std::sync::atomic::Ordering::SeqCst);
                });
            }
            list.clone()
        }
        None => {
            let list = read();
            *c = Some((Instant::now(), list.clone()));
            list
        }
    }
}

/// Files and folders Windows recorded as recently opened (the Recent folder Start's
/// "Recommended" draws from), newest first. Targets that are gone are skipped, and network
/// paths are not checked (that could stall on an offline share).
pub fn recent_files(n: usize) -> Vec<Hit> {
    let Some(appdata) = std::env::var_os("APPDATA") else { return Vec::new() };
    let dir = PathBuf::from(appdata).join(r"Microsoft\Windows\Recent");
    let Ok(rd) = std::fs::read_dir(&dir) else { return Vec::new() };
    let mut lnks: Vec<(std::time::SystemTime, PathBuf)> = rd
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("lnk")))
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .collect();
    lnks.sort_by(|a, b| b.0.cmp(&a.0));

    let mut out = Vec::new();
    let mut seen = HashSet::new();
    for (_, lnk) in lnks {
        if out.len() >= n {
            break;
        }
        let Some(target) = link_target(&lnk) else { continue };
        if target.starts_with(r"\\") || !seen.insert(target.to_lowercase()) {
            continue;
        }
        let p = Path::new(&target);
        let Ok(md) = std::fs::metadata(p) else { continue };
        let name = p.file_name().map(|s| s.to_string_lossy().into_owned()).unwrap_or_else(|| target.clone());
        let parent = p.parent().map(|s| s.display().to_string()).unwrap_or_default();
        out.push(Hit {
            title: name,
            subtitle: format!("Recent \u{b7} {parent}"),
            kind: if md.is_dir() { Kind::Folder } else { Kind::File },
            action: Action::Open(target),
            score: 0,
        });
    }
    out
}

/// Target path of a .lnk shortcut, if it points at the file system.
pub fn link_target(lnk: &Path) -> Option<String> {
    use windows::core::{Interface, HSTRING};
    use windows::Win32::Storage::FileSystem::WIN32_FIND_DATAW;
    use windows::Win32::System::Com::{
        CoCreateInstance, CoInitializeEx, IPersistFile, CLSCTX_INPROC_SERVER, COINIT_APARTMENTTHREADED, STGM_READ,
    };
    use windows::Win32::UI::Shell::{IShellLinkW, ShellLink, SLGP_RAWPATH};
    unsafe {
        // Already initialised on this thread (either model) is fine: ShellLink works in both.
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let link: IShellLinkW = CoCreateInstance(&ShellLink, None, CLSCTX_INPROC_SERVER).ok()?;
        link.cast::<IPersistFile>().ok()?.Load(&HSTRING::from(lnk.as_os_str()), STGM_READ).ok()?;
        let mut buf = [0u16; 1024];
        let mut fd = WIN32_FIND_DATAW::default();
        link.GetPath(&mut buf, &mut fd, SLGP_RAWPATH.0 as u32).ok()?;
        let len = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
        let raw = String::from_utf16_lossy(&buf[..len]);
        if raw.is_empty() {
            return None;
        }
        Some(expand_env(&raw))
    }
}

/// `%WINDIR%\x` -> `C:\Windows\x`; unknown variables are left as they are.
pub fn expand_env(s: &str) -> String {
    if !s.contains('%') {
        return s.to_string();
    }
    let mut out = String::new();
    let mut rest = s;
    while let Some(a) = rest.find('%') {
        out.push_str(&rest[..a]);
        let after = &rest[a + 1..];
        match after.find('%') {
            Some(b) => {
                let name = &after[..b];
                match std::env::var(name) {
                    Ok(v) if !name.is_empty() => out.push_str(&v),
                    _ => {
                        out.push('%');
                        out.push_str(name);
                        out.push('%');
                    }
                }
                rest = &after[b + 1..];
            }
            None => {
                out.push('%');
                rest = after;
            }
        }
    }
    out.push_str(rest);
    out
}

// ---------------------------------------------------------------- actions

/// One entry in a result's action list.
#[derive(Serialize)]
pub struct ActionRow {
    pub id: &'static str,
    pub title: String,
    /// Keyboard shortcut that does the same from the results list, if any.
    pub hint: &'static str,
    /// Set for copy actions: the shell puts this on the clipboard itself.
    pub copy: Option<String>,
}

fn row(id: &'static str, title: impl Into<String>, hint: &'static str) -> ActionRow {
    ActionRow { id, title: title.into(), hint, copy: None }
}

/// The file behind an app row, when there is one: a Start Menu shortcut, an exe, or the program
/// a desktop app in the shell's app list starts. Store apps (`Package_id!App`) have none.
pub fn app_file(hit: &Hit) -> Option<PathBuf> {
    let Action::Open(t) = &hit.action else { return None };
    let Some(id) = t.strip_prefix("shell:AppsFolder\\") else {
        return Some(PathBuf::from(t)).filter(|p| p.is_absolute() && p.exists());
    };
    if id.contains('!') {
        return None;
    }
    shell_target(t).map(PathBuf::from).filter(|p| p.exists())
}

/// The file a shell:AppsFolder item launches, as the shell reports it. Most desktop apps are
/// listed under IDs that do not contain it (Microsoft.AutoGenerated.{...}, explicit app IDs).
fn shell_target(parsing_name: &str) -> Option<String> {
    use windows::core::{GUID, HSTRING};
    use windows::Win32::System::Com::{CoInitializeEx, CoTaskMemFree, COINIT_APARTMENTTHREADED};
    use windows::Win32::Foundation::PROPERTYKEY;
    use windows::Win32::UI::Shell::{IShellItem2, SHCreateItemFromParsingName};
    // PKEY_Link_TargetParsingPath
    const TARGET: PROPERTYKEY = PROPERTYKEY { fmtid: GUID::from_u128(0xB9B4B3FC_2B51_4A42_B5D8_324146AFCF25), pid: 2 };
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let item: IShellItem2 = SHCreateItemFromParsingName(&HSTRING::from(parsing_name), None).ok()?;
        let p = item.GetString(&TARGET).ok()?;
        let s = p.to_string().ok();
        CoTaskMemFree(Some(p.0 as *const _));
        s.filter(|s| !s.is_empty())
    }
}

fn path_of(hit: &Hit) -> Option<PathBuf> {
    match (hit.kind, &hit.action) {
        (Kind::File | Kind::Folder, Action::Open(p)) => Some(PathBuf::from(p)),
        (Kind::App, _) => app_file(hit),
        _ => None,
    }
}

fn elevatable(hit: &Hit) -> bool {
    match hit.kind {
        Kind::App => app_file(hit).is_some() || matches!(&hit.action, Action::Open(t) if !t.starts_with("shell:")),
        Kind::File => path_of(hit)
            .and_then(|p| p.extension().map(|e| e.to_string_lossy().to_lowercase()))
            .is_some_and(|e| matches!(e.as_str(), "exe" | "lnk" | "bat" | "cmd" | "msi" | "msc")),
        _ => false,
    }
}

fn terminal() -> Option<PathBuf> {
    let p = PathBuf::from(std::env::var_os("LOCALAPPDATA")?).join(r"Microsoft\WindowsApps\wt.exe");
    p.exists().then_some(p)
}

/// The actions for `hit`, the default one first. `pin` is its place among the pins, and how
/// many there are, when it is pinned.
pub fn actions_for(hit: &Hit, pin: Option<(usize, usize)>) -> Vec<ActionRow> {
    let mut v = Vec::new();
    let open = match hit.kind {
        Kind::Window => "Switch to",
        Kind::Command => "Run",
        Kind::Calc => "Copy result",
        Kind::Web => "Search the web",
        Kind::Toggle => "Apply",
        Kind::Plan => "Run",
        _ => "Open",
    };
    v.push(row("open", open, "Enter"));
    if elevatable(hit) {
        v.push(row("admin", "Run as administrator", "Ctrl+Shift+Enter"));
    }
    if hit.kind == Kind::File {
        v.push(row("openwith", "Open with\u{2026}", ""));
    }
    if let Some(p) = path_of(hit) {
        v.push(row("location", "Open file location", "Ctrl+Enter"));
        if hit.kind == Kind::Folder && terminal().is_some() {
            v.push(row("terminal", "Open in Terminal", ""));
        }
        let mut c = row("copypath", "Copy path", "");
        c.copy = Some(p.display().to_string());
        v.push(c);
    }
    if pinnable(hit) {
        match pin {
            Some((i, n)) => {
                v.push(row("unpin", "Unpin from Home", ""));
                // Home lists pins upward from the search field: "earlier" is lower on screen.
                if i + 1 < n {
                    v.push(row("pinlater", "Move pin up", ""));
                }
                if i > 0 {
                    v.push(row("pinearlier", "Move pin down", ""));
                }
            }
            None => v.push(row("pin", "Pin to Home", "")),
        }
    }
    if matches!(hit.kind, Kind::File | Kind::Folder) {
        v.push(row("properties", "Properties", ""));
    }
    if hit.kind == Kind::App {
        v.push(row("uninstall", "Uninstall\u{2026}", ""));
    }
    if hit.kind == Kind::Window {
        v.push(row("minimize", "Minimize window", "Ctrl+M"));
        v.push(row("close", "Close window", "Ctrl+W"));
    }
    v
}

/// What the shell should do after an action ran.
pub enum After {
    /// Not an action for this hit.
    NotApplicable,
    Hide,
    /// The list changed (pins): show it again.
    Refresh,
}

/// Runs action `id` on `hit`. Opening plainly is `pc_launch`'s job (it records usage).
pub fn act(hit: &Hit, id: &str, pins: &mut Pins, usage: &mut Usage) -> After {
    let path = path_of(hit);
    match id {
        "admin" if elevatable(hit) => {
            usage.record(hit);
            let target = match (&hit.action, &path) {
                (_, Some(p)) => p.clone(),
                (Action::Open(t), None) => PathBuf::from(t),
                _ => return After::NotApplicable,
            };
            actions::shell_execute_elevated(&target, "");
            After::Hide
        }
        "openwith" => match path {
            Some(p) => {
                spawn("rundll32.exe", &["shell32.dll,OpenAs_RunDLL", &p.display().to_string()]);
                After::Hide
            }
            None => After::NotApplicable,
        },
        "location" => match path {
            Some(p) => {
                actions::reveal(&p);
                After::Hide
            }
            None => After::NotApplicable,
        },
        "terminal" => match (path, terminal()) {
            (Some(p), Some(wt)) => {
                let _ = std::process::Command::new(wt).arg("-d").arg(&p).spawn();
                After::Hide
            }
            _ => After::NotApplicable,
        },
        "properties" => match path {
            Some(p) => {
                properties(&p);
                After::Hide
            }
            None => After::NotApplicable,
        },
        "uninstall" if hit.kind == Kind::App => {
            actions::shell_open("ms-settings:appsfeatures");
            After::Hide
        }
        "close" => match &hit.action {
            Action::Focus { hwnd, .. } => {
                use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
                use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_CLOSE};
                unsafe {
                    let _ = PostMessageW(Some(HWND(*hwnd as *mut _)), WM_CLOSE, WPARAM(0), LPARAM(0));
                }
                After::Refresh
            }
            _ => After::NotApplicable,
        },
        // Down to the taskbar without activating anything: the palette keeps the keyboard.
        "minimize" => match &hit.action {
            Action::Focus { hwnd, .. } => {
                use windows::Win32::Foundation::HWND;
                use windows::Win32::UI::WindowsAndMessaging::{ShowWindow, SW_SHOWMINNOACTIVE};
                unsafe {
                    let _ = ShowWindow(HWND(*hwnd as *mut _), SW_SHOWMINNOACTIVE);
                }
                After::Refresh
            }
            _ => After::NotApplicable,
        },
        "pin" if pinnable(hit) => {
            pins.pin(hit);
            After::Refresh
        }
        "unpin" => {
            pins.unpin(&hit.key());
            After::Refresh
        }
        "pinlater" | "pinearlier" => {
            pins.shift(&hit.key(), id == "pinlater");
            After::Refresh
        }
        _ => After::NotApplicable,
    }
}

fn spawn(program: &str, args: &[&str]) {
    let _ = std::process::Command::new(program).args(args).spawn();
}

/// The Explorer properties sheet. It runs on a thread of its own inside this process.
fn properties(p: &Path) {
    use windows::core::{HSTRING, PCWSTR};
    use windows::Win32::UI::Shell::{SHObjectProperties, SHOP_FILEPATH};
    let w = HSTRING::from(p.as_os_str());
    unsafe {
        let _ = SHObjectProperties(None, SHOP_FILEPATH, &w, PCWSTR::null());
    }
}
