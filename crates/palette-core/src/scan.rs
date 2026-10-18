use crate::index::{Catalog, FileIndex};
use crate::model::{Action, Hit, Item, Kind};
use crate::paths;
use crate::Notify;
use serde::Deserialize;
use std::collections::HashSet;
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::{Arc, RwLock};
use std::time::{Duration, SystemTime};

const NO_WINDOW: u32 = 0x0800_0000;

fn item(name: &str, subtitle: &str, kind: Kind, action: Action) -> Item {
    Item::new(name, Hit { title: name.to_string(), subtitle: subtitle.to_string(), kind, action, score: 0 }, None)
}

// ---------------------------------------------------------------- built-ins

pub fn builtins() -> Vec<Item> {
    let c = |n: &str, sub: &str, id: &str| item(n, sub, Kind::Command, Action::Builtin(id.into()));
    let s = |n: &str, uri: &str| item(n, "Windows Settings", Kind::Setting, Action::Open(uri.into()));
    vec![
        c("Start menu", "Open the Windows Start menu", "start"),
        c("Lock", "Lock this PC", "lock"),
        c("Sleep", "Put this PC to sleep", "sleep"),
        c("Sign out", "Sign out of Windows", "signout"),
        c("Restart", "Restart this PC", "restart"),
        c("Shut down", "Shut down this PC", "shutdown"),
        c("Palette: Reindex files", "Refresh the file index now", "reindex"),
        c("Palette: Run at startup", "Start Palette when you sign in", "autostart"),
        // The shell flips the setting itself (it owns the footer); this row only lists it.
        c("Palette: Hide or show key hints", "The keys shown under the search field", "keyhints"),
        c("Palette: Settings", "The shortcut, what Palette reads and remembers", "palettesettings"),
        c("Palette: Quit", "Exit the launcher", "quit"),
        s("Settings", "ms-settings:"),
        s("Display settings", "ms-settings:display"),
        s("Sound settings", "ms-settings:sound"),
        s("Bluetooth & devices", "ms-settings:bluetooth"),
        s("Wi-Fi settings", "ms-settings:network-wifi"),
        s("Network & internet", "ms-settings:network"),
        s("Apps & features", "ms-settings:appsfeatures"),
        s("Default apps", "ms-settings:defaultapps"),
        s("Startup apps", "ms-settings:startupapps"),
        s("Battery & power", "ms-settings:batterysaver"),
        s("Storage", "ms-settings:storagesense"),
        s("Windows Update", "ms-settings:windowsupdate"),
        s("Personalization", "ms-settings:personalization"),
        s("Dark mode & colors", "ms-settings:colors"),
        s("Privacy & security", "ms-settings:privacy"),
        s("About this PC", "ms-settings:about"),
        s("Date & time", "ms-settings:dateandtime"),
        s("Language & region", "ms-settings:regionlanguage"),
        s("Keyboard settings", "ms-settings:keyboard"),
        s("Mouse settings", "ms-settings:mouse"),
        s("Accounts", "ms-settings:yourinfo"),
        s("Notifications", "ms-settings:notifications"),
        item("Task Manager", "System tool", Kind::App, Action::Open("taskmgr.exe".into())),
        item("Control Panel", "System tool", Kind::App, Action::Open("control.exe".into())),
        item("Command Prompt", "System tool", Kind::App, Action::Open("cmd.exe".into())),
        item("Registry Editor", "System tool", Kind::App, Action::Open("regedit.exe".into())),
        item("Device Manager", "System tool", Kind::App, Action::Open("devmgmt.msc".into())),
        item("Services", "System tool", Kind::App, Action::Open("services.msc".into())),
    ]
}

// --------------------------------------------------------------------- apps

fn walk_lnks(dir: &Path, out: &mut Vec<(String, PathBuf)>, depth: u32) {
    if depth > 6 {
        return;
    }
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let p = e.path();
        if p.is_dir() {
            walk_lnks(&p, out, depth + 1);
        } else if p.extension().is_some_and(|x| x.eq_ignore_ascii_case("lnk")) {
            if let Some(stem) = p.file_stem().and_then(|s| s.to_str()) {
                out.push((stem.to_string(), p.clone()));
            }
        }
    }
}

#[derive(Deserialize)]
struct StartApp {
    #[serde(rename = "Name")]
    name: String,
    #[serde(rename = "AppID")]
    app_id: String,
}

/// Start Menu shortcuts (fast, offline) merged with Get-StartApps (UWP/Store).
pub fn scan_apps() -> Vec<Item> {
    let mut seen: HashSet<String> = HashSet::new();
    let mut apps: Vec<Item> = Vec::new();
    let skip = |n: &str| {
        let l = n.to_lowercase();
        l.contains("uninstall") || l.starts_with("remove ")
    };

    // UWP + classic apps via the shell's own app list.
    let out = Command::new("powershell")
        .args([
            "-NoProfile",
            "-Command",
            "[Console]::OutputEncoding=[Text.Encoding]::UTF8; ConvertTo-Json -Compress -InputObject @(Get-StartApps)",
        ])
        .creation_flags(NO_WINDOW)
        .output();
    if let Ok(o) = out {
        if let Ok(list) = serde_json::from_slice::<Vec<StartApp>>(&o.stdout) {
            for a in list {
                if skip(&a.name) || !seen.insert(a.name.to_lowercase()) {
                    continue;
                }
                apps.push(item(
                    &a.name,
                    "Application",
                    Kind::App,
                    Action::Open(format!("shell:AppsFolder\\{}", a.app_id)),
                ));
            }
        }
    }

    // Classic shortcuts fill any gaps.
    let mut lnks = Vec::new();
    if let Some(pd) = std::env::var_os("ProgramData") {
        walk_lnks(&PathBuf::from(pd).join(r"Microsoft\Windows\Start Menu\Programs"), &mut lnks, 0);
    }
    if let Some(ad) = std::env::var_os("APPDATA") {
        walk_lnks(&PathBuf::from(ad).join(r"Microsoft\Windows\Start Menu\Programs"), &mut lnks, 0);
    }
    for (name, path) in lnks {
        if skip(&name) || !seen.insert(name.to_lowercase()) {
            continue;
        }
        apps.push(item(&name, "Application", Kind::App, Action::Open(path.display().to_string())));
    }
    apps
}

fn apps_cache_path() -> PathBuf {
    paths::data_dir().join("apps.json")
}

fn load_apps_cache() -> Vec<Item> {
    #[derive(Deserialize)]
    struct Row(String, String);
    std::fs::read(apps_cache_path())
        .ok()
        .and_then(|b| serde_json::from_slice::<Vec<Row>>(&b).ok())
        .map(|rows| {
            rows.into_iter()
                .map(|Row(n, t)| item(&n, "Application", Kind::App, Action::Open(t)))
                .collect()
        })
        .unwrap_or_default()
}

fn save_apps_cache(apps: &[Item]) {
    let rows: Vec<(&str, &str)> = apps
        .iter()
        .filter_map(|i| match &i.hit.action {
            Action::Open(t) => Some((i.name.as_str(), t.as_str())),
            _ => None,
        })
        .collect();
    if let Ok(b) = serde_json::to_vec(&rows) {
        let _ = std::fs::write(apps_cache_path(), b);
    }
}

// -------------------------------------------------------------------- files

fn snapshot_files() -> Vec<PathBuf> {
    let Ok(rd) = std::fs::read_dir(paths::index_dir()) else { return vec![] };
    rd.flatten()
        .map(|e| e.path())
        .filter(|p| p.extension().is_some_and(|e| e == "txt") && p.file_stem().is_some_and(|s| s.len() == 1))
        .collect()
}

/// Caches of directory walks (`walk-<X>.txt`) for drives the MFT helper cannot read.
fn extra_walk_files() -> Vec<PathBuf> {
    std::fs::read_dir(paths::data_dir())
        .map(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .filter(|p| {
                    p.file_name().and_then(|n| n.to_str()).is_some_and(|n| n.starts_with("walk-") && n.ends_with(".txt"))
                })
                .collect()
        })
        .unwrap_or_default()
}

/// Drive roots whose MFT snapshot came back empty: FAT/exFAT volumes and virtual drives such as
/// Google Drive, which report FAT32 and have no MFT.
fn unreadable_drive_roots() -> Vec<String> {
    snapshot_files()
        .iter()
        .filter(|p| std::fs::metadata(p).is_ok_and(|m| m.len() == 0))
        .filter_map(|p| p.file_stem()?.to_str().map(|s| format!("{}:\\", s.to_uppercase())))
        .filter(|root| Path::new(root).exists())
        .collect()
}

fn newest_mtime(files: &[PathBuf]) -> Option<SystemTime> {
    files.iter().filter_map(|p| std::fs::metadata(p).ok()?.modified().ok()).max()
}

fn load_snapshots(files: &[PathBuf]) -> FileIndex {
    let mut idx = FileIndex::default();
    for p in files {
        if let Ok(bytes) = std::fs::read(p) {
            idx.push_snapshot(&String::from_utf8_lossy(&bytes));
        }
    }
    idx
}

const WALK_SKIP: &[&str] = &[
    "appdata", "node_modules", ".git", "__pycache__", "site-packages", ".venv", ".cargo", ".rustup",
    ".nuget", ".gradle", ".m2", ".cache", ".npm",
];

/// Fallback when no MFT snapshot exists: parallel walk of the user profile.
fn walk_profile() -> FileIndex {
    let mut idx = FileIndex::default();
    let Some(home) = std::env::var_os("USERPROFILE") else { return idx };
    let (tx, rx) = std::sync::mpsc::channel::<(String, bool)>();
    let walker = ignore::WalkBuilder::new(home)
        .standard_filters(false)
        .follow_links(false)
        .filter_entry(|e| {
            e.file_name().to_str().map_or(true, |n| !WALK_SKIP.contains(&n.to_lowercase().as_str()))
        })
        .build_parallel();
    let collector = std::thread::spawn(move || {
        let mut idx = FileIndex::default();
        for (p, d) in rx {
            idx.push(&p, d);
        }
        idx
    });
    walker.run(|| {
        let tx = tx.clone();
        Box::new(move |entry| {
            if let Ok(e) = entry {
                if e.depth() > 0 {
                    let is_dir = e.file_type().is_some_and(|t| t.is_dir());
                    let _ = tx.send((e.path().display().to_string(), is_dir));
                }
            }
            ignore::WalkState::Continue
        })
    });
    drop(tx);
    if let Ok(i) = collector.join() {
        idx = i;
    }
    idx
}

const EXTRA_WALK_MAX_ENTRIES: usize = 600_000;
const EXTRA_WALK_BUDGET: Duration = Duration::from_secs(6 * 60);
const EXTRA_WALK_REFRESH: Duration = Duration::from_secs(6 * 3600);

/// One slow, polite walk of a drive root: single-threaded, below-normal priority, capped by
/// entry count and wall-clock time so a huge cloud drive can't hog the machine.
fn walk_root(root: &str) -> Vec<(String, bool)> {
    let deadline = std::time::Instant::now() + EXTRA_WALK_BUDGET;
    let walker = ignore::WalkBuilder::new(root)
        .standard_filters(false)
        .follow_links(false)
        .filter_entry(|e| {
            let Some(n) = e.file_name().to_str() else { return true };
            let l = n.to_lowercase();
            if WALK_SKIP.contains(&l.as_str()) || l == "$recycle.bin" || l == "system volume information" {
                return false;
            }
            // Dot-directories are caches (.tmp.drivedownload, .Trash...), except Google Drive's
            // folder of shortcut targets, which holds real shared files.
            !(l.starts_with('.') && e.depth() > 0 && l != ".shortcut-targets-by-id")
        })
        .build();
    let mut out = Vec::new();
    for entry in walker.flatten() {
        if entry.depth() == 0 {
            continue;
        }
        out.push((entry.path().display().to_string(), entry.file_type().is_some_and(|t| t.is_dir())));
        if out.len() >= EXTRA_WALK_MAX_ENTRIES || (out.len() % 2048 == 0 && std::time::Instant::now() > deadline) {
            break;
        }
    }
    out
}

/// Mark the calling thread as background work: below-normal priority, and EcoQoS ("efficiency
/// mode"), which runs it on the efficient cores at low clocks. These threads live in the
/// launcher's process, so they must never compete with its UI or with the user's apps.
pub(crate) fn background_thread() {
    use windows::Win32::System::Threading::{
        GetCurrentThread, SetThreadInformation, SetThreadPriority, ThreadPowerThrottling, THREAD_POWER_THROTTLING_CURRENT_VERSION,
        THREAD_POWER_THROTTLING_EXECUTION_SPEED, THREAD_POWER_THROTTLING_STATE, THREAD_PRIORITY_BELOW_NORMAL,
    };
    unsafe {
        let _ = SetThreadPriority(GetCurrentThread(), THREAD_PRIORITY_BELOW_NORMAL);
        let state = THREAD_POWER_THROTTLING_STATE {
            Version: THREAD_POWER_THROTTLING_CURRENT_VERSION,
            ControlMask: THREAD_POWER_THROTTLING_EXECUTION_SPEED,
            StateMask: THREAD_POWER_THROTTLING_EXECUTION_SPEED,
        };
        let _ = SetThreadInformation(
            GetCurrentThread(),
            ThreadPowerThrottling,
            &state as *const _ as *const core::ffi::c_void,
            std::mem::size_of::<THREAD_POWER_THROTTLING_STATE>() as u32,
        );
    }
}

/// Keeps `walk-<X>.txt` fresh for every drive the MFT helper reported as empty.
fn spawn_extra_walker() {
    std::thread::spawn(|| {
        background_thread();
        std::thread::sleep(Duration::from_secs(30)); // let startup settle first
        loop {
            for root in unreadable_drive_roots() {
                let letter = root.chars().next().unwrap_or('X');
                let cache = paths::data_dir().join(format!("walk-{letter}.txt"));
                let fresh = std::fs::metadata(&cache)
                    .and_then(|m| m.modified())
                    .ok()
                    .and_then(|t| t.elapsed().ok())
                    .is_some_and(|age| age < EXTRA_WALK_REFRESH);
                if fresh {
                    continue;
                }
                let entries = walk_root(&root);
                if entries.is_empty() {
                    continue;
                }
                use std::io::Write;
                let tmp = cache.with_extension("tmp");
                if let Ok(f) = std::fs::File::create(&tmp) {
                    let mut w = std::io::BufWriter::new(f);
                    for (p, d) in &entries {
                        let _ = if *d { writeln!(w, "{p}\\") } else { writeln!(w, "{p}") };
                    }
                    if w.flush().is_ok() {
                        drop(w);
                        let _ = std::fs::rename(&tmp, &cache);
                    }
                }
            }
            std::thread::sleep(Duration::from_secs(3600));
        }
    });
}

fn walk_cache_path() -> PathBuf {
    paths::data_dir().join("walk.txt")
}

fn load_walk_cache() -> FileIndex {
    let mut idx = FileIndex::default();
    if let Ok(b) = std::fs::read(walk_cache_path()) {
        idx.push_snapshot(&String::from_utf8_lossy(&b));
    }
    idx
}

fn save_walk_cache(idx: &FileIndex) {
    use std::io::Write;
    if let Ok(f) = std::fs::File::create(walk_cache_path()) {
        let mut w = std::io::BufWriter::new(f);
        for i in 0..idx.len() {
            let _ = if idx.is_dir(i) {
                writeln!(w, "{}\\", idx.path(i))
            } else {
                writeln!(w, "{}", idx.path(i))
            };
        }
    }
}

// ------------------------------------------------------------------ manager

fn publish(cat: &Arc<RwLock<Catalog>>, f: impl FnOnce(&mut Catalog)) {
    let mut g = cat.write().unwrap();
    f(&mut g);
    g.version += 1;
}

/// `publish` for a change to the apps, settings or commands.
fn publish_items(cat: &Arc<RwLock<Catalog>>, f: impl FnOnce(&mut Catalog)) {
    publish(cat, |c| {
        f(c);
        c.items_version += 1;
    });
}

/// Start the helper: scheduled task if installed (no UAC), otherwise one UAC
/// prompt. Only prompts once per machine (recorded in `elevation-asked`).
pub fn request_mft_snapshot(force_prompt: bool) {
    let ran_task = Command::new("schtasks")
        .args(["/run", "/tn", "PaletteMftRefresh"])
        .creation_flags(NO_WINDOW)
        .output()
        .is_ok_and(|o| o.status.success());
    if ran_task {
        return;
    }
    let flag = paths::data_dir().join("elevation-asked");
    if flag.exists() && !force_prompt {
        return;
    }
    let Some(exe) = paths::helper_exe() else { return };
    let _ = std::fs::write(&flag, b"1");
    crate::actions::shell_execute_elevated(&exe, "--snapshot --install-task");
}

pub fn spawn_manager(catalog: Arc<RwLock<Catalog>>, notify: Notify) {
    std::thread::spawn(move || {
        // 1. Instant start from caches.
        let mut items = builtins();
        items.extend(load_apps_cache());
        spawn_extra_walker();
        let snaps = snapshot_files();
        // MFT snapshots plus the walk caches of drives the MFT cannot read (Google Drive...).
        let all: Vec<PathBuf> = snaps.iter().cloned().chain(extra_walk_files()).collect();
        let mut files = if snaps.is_empty() {
            let mut f = load_walk_cache();
            for p in extra_walk_files() {
                if let Ok(b) = std::fs::read(p) {
                    f.push_snapshot(&String::from_utf8_lossy(&b));
                }
            }
            f
        } else {
            load_snapshots(&all)
        };
        let mut last_mtime = newest_mtime(&all);
        publish_items(&catalog, |c| {
            c.items = items;
            c.files = std::mem::take(&mut files);
        });
        notify();
        // The index is up; everything from here on is background upkeep.
        background_thread();

        // 2. Every setting Windows' own search knows (cached; rebuilt after Windows updates).
        let settings = crate::settings_index::load();
        if !settings.is_empty() {
            publish_items(&catalog, |c| {
                // The hand-picked pages keep their friendlier names; the index adds the rest.
                let have: std::collections::HashSet<String> = c.items.iter().map(|i| i.name.to_lowercase()).collect();
                c.items.extend(settings.into_iter().filter(|i| !have.contains(&i.name.to_lowercase())));
            });
        }

        // 3. Refresh apps (again every 6 hours below, so apps installed meanwhile turn up).
        let refresh_apps = || {
            let apps = scan_apps();
            if !apps.is_empty() {
                save_apps_cache(&apps);
                publish_items(&catalog, |c| {
                    c.items.retain(|i| i.hit.kind != Kind::App || !matches!(&i.hit.action, Action::Open(t) if t.contains("shell:AppsFolder") || t.ends_with(".lnk")));
                    c.items.extend(apps);
                });
            }
        };
        refresh_apps();

        // 4. Make sure we have *some* file index, and ask for the fast one.
        if snapshot_files().is_empty() {
            if catalog.read().unwrap().files.len() == 0 {
                let walked = walk_profile();
                save_walk_cache(&walked);
                publish(&catalog, |c| c.files = walked);
            }
            request_mft_snapshot(false);
        } else if last_mtime
            .and_then(|t| t.elapsed().ok())
            .is_some_and(|age| age > Duration::from_secs(3600))
        {
            request_mft_snapshot(false);
        }

        // 4. Hot-reload snapshots when the helper rewrites them.
        let mut ticks = 0u32;
        loop {
            std::thread::sleep(Duration::from_secs(15));
            ticks += 1;
            let snaps = snapshot_files();
            let all: Vec<PathBuf> = snaps.iter().cloned().chain(extra_walk_files()).collect();
            let m = newest_mtime(&all);
            // Without MFT snapshots the profile-walk index is in use; don't replace it.
            if !snaps.is_empty() && m.is_some() && m != last_mtime {
                last_mtime = m;
                let idx = load_snapshots(&all);
                publish(&catalog, |c| c.files = idx);
            }
            if ticks % (6 * 60 * 4) == 0 {
                refresh_apps();
            }
        }
    });
}

/// Folders and file types the MFT snapshot leaves out (`SKIP_DIRS`, `SKIP_EXT` and dot-folders in
/// crates/palette-mft/src/main.rs; keep these in step), so that live additions follow the same rules.
const LIVE_SKIP_DIRS: &[&str] = &[
    "$recycle.bin", "system volume information", "$windows.~bt", "$windows.~ws", "node_modules", ".git",
    "__pycache__", "site-packages", ".venv", "appdata", "programdata", "windows", "program files",
    "program files (x86)", "recovery", "perflogs", "$sysreset", "windowsapps", "miniconda3", "anaconda3",
    "miniforge3", "vcpkg", "$recycle.bin.old",
];
const LIVE_SKIP_EXT: &[&str] = &[
    "dll", "sys", "mui", "pdb", "tmp", "cat", "manifest", "mum", "lib", "obj", "pyc", "pyo", "o", "d", "rlib", "rmeta",
    "etl", "log", "dat", "idx", "pack", "lock", "map", "cache",
];

/// Would the MFT snapshot list this path? `l` is the path, lowercased.
fn indexable(l: &str, dir: bool) -> bool {
    let mut parts = l.split('\\');
    let name = parts.next_back().unwrap_or("");
    let skipped = |p: &str| p.starts_with('.') || LIVE_SKIP_DIRS.contains(&p);
    if parts.any(skipped) {
        return false;
    }
    if dir {
        return !skipped(name);
    }
    !name.rsplit_once('.').is_some_and(|(_, ext)| LIVE_SKIP_EXT.contains(&ext))
}

/// Live additions between snapshots: new files under the user profile. `on_docs` receives
/// document files that were created, renamed or written, for incremental semantic indexing.
pub fn spawn_watcher(catalog: Arc<RwLock<Catalog>>, on_docs: Arc<dyn Fn(Vec<String>) + Send + Sync>) {
    use notify::{EventKind, RecursiveMode, Watcher};
    std::thread::spawn(move || {
        background_thread();
        let Some(home) = std::env::var_os("USERPROFILE") else { return };
        let (tx, rx) = std::sync::mpsc::channel();
        let Ok(mut w) = notify::recommended_watcher(tx) else { return };
        if w.watch(Path::new(&home), RecursiveMode::Recursive).is_err() {
            return;
        }
        while let Ok(first) = rx.recv() {
            // A build or an unzip makes thousands of events at once: take every one already waiting
            // and add them in one go, rather than taking the index from searches once per file.
            let mut batch: Vec<(String, bool)> = Vec::new();
            let mut docs: Vec<String> = Vec::new();
            for ev in std::iter::once(first).chain(rx.try_iter()) {
                let Ok(ev) = ev else { continue };
                let is_new = matches!(ev.kind, EventKind::Create(_) | EventKind::Modify(notify::event::ModifyKind::Name(_)));
                let is_write = matches!(ev.kind, EventKind::Modify(_));
                if !is_new && !is_write {
                    continue;
                }
                for p in ev.paths {
                    let s = p.display().to_string();
                    let l = s.to_lowercase();
                    if l.contains("\\appdata\\") || l.contains("\\node_modules\\") || l.contains("\\.git\\") {
                        continue;
                    }
                    if let Ok(md) = std::fs::symlink_metadata(&p) {
                        if md.is_file() && crate::semantic::is_doc_path(&s) {
                            docs.push(s.clone());
                        }
                        // Plain writes to existing files must not add duplicate index entries.
                        if is_new && indexable(&l, md.is_dir()) {
                            batch.push((s, md.is_dir()));
                        }
                    }
                }
            }
            if !docs.is_empty() {
                on_docs(docs);
            }
            if !batch.is_empty() {
                publish(&catalog, |c| {
                    for (p, d) in &batch {
                        c.files.push(p, *d);
                    }
                });
            }
        }
        drop(w);
    });
}

#[cfg(test)]
mod tests {
    use super::indexable;

    #[test]
    fn live_additions_follow_the_snapshot() {
        assert!(indexable(r"c:\users\me\documents\notes.txt", false));
        assert!(indexable(r"c:\users\me\documents\new folder", true));
        assert!(!indexable(r"c:\users\me\code\app\target\release\deps\app.rlib", false));
        assert!(!indexable(r"c:\users\me\.cache\pip\x.txt", false));
        assert!(!indexable(r"c:\users\me\code\app\__pycache__", true));
        assert!(!indexable(r"c:\users\me\code\.venv", true));
    }
}
