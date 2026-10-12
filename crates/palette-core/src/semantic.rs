//! Client for `palette-semantic.exe`, the process that hosts EmbeddingGemma.
//!
//! The model lives in its own process: its memory, its CPU use while indexing and any
//! crash while parsing an odd PDF never touch the launcher. The client starts it lazily,
//! restarts it if it exits (it quits on its own after a quiet period) and treats every
//! failure as "no semantic results".

use crate::index::Catalog;
use crate::model::{Action, Hit, Kind};
use crate::paths;
use serde::Deserialize;
use std::collections::{HashMap, HashSet};
use std::io::{BufRead, BufReader, Write};
use std::os::windows::process::CommandExt;
use std::process::{Child, ChildStdin, Command, Stdio};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::mpsc::{channel, Sender};
use std::sync::{Arc, Mutex, RwLock};
use std::time::{Duration, Instant};

const NO_WINDOW: u32 = 0x0800_0000;

/// Cosine-similarity cut-offs. Score bands differ per model, so they are calibrated per model
/// (256-dim Matryoshka vectors on this machine's documents):
/// - EmbeddingGemma 1 (300m): related 0.45-0.65, unrelated 0.10-0.28
/// - EmbeddingGemma 2:        related 0.72-0.82; unrelated up to ~0.63, vague phrasings pull
///                            in noise documents up to ~0.72, hence the higher floors
#[derive(Clone, Copy)]
pub struct Thresholds {
    /// Minimum score for a document to be shown as related.
    pub doc: f32,
    /// Minimum score for an app / setting / command.
    pub item: f32,
    /// A match this strong may lead the list when the instant tier found nothing real.
    pub lead: f32,
}

pub fn thresholds() -> Thresholds {
    static T: std::sync::OnceLock<Thresholds> = std::sync::OnceLock::new();
    *T.get_or_init(|| {
        let v2 = paths::semantic_model_dir()
            .and_then(|d| d.file_name().map(|n| n.to_string_lossy().contains("embeddinggemma-2")))
            .unwrap_or(false);
        let mut t = if v2 {
            Thresholds { doc: 0.72, item: 0.70, lead: 0.74 }
        } else {
            Thresholds { doc: 0.42, item: 0.42, lead: 0.48 }
        };
        if let Some(m) = std::env::var("PALETTE_SEM_MIN").ok().and_then(|v| v.parse::<f32>().ok()) {
            t.doc = m;
            t.item = m;
        }
        t
    })
}

/// Rows the semantic tier may add under the instant results.
pub const MAX_RELATED: usize = 3;

#[derive(Deserialize)]
struct RawReply {
    id: u64,
    #[serde(default)]
    hits: Vec<RawHit>,
}

#[derive(Deserialize)]
struct RawHit {
    src: String,
    key: String,
    score: f32,
}

type Pending = Arc<Mutex<HashMap<u64, Sender<RawReply>>>>;

struct Proc {
    child: Child,
    stdin: ChildStdin,
}

/// File-change notices waiting to be batched and sent to the helper.
struct Touched {
    paths: HashSet<String>,
    first: Option<Instant>,
    last: Instant,
}

/// Document types the semantic indexer handles (keep in sync with palette-semantic's extract.rs).
const DOC_EXTS: [&str; 8] = ["txt", "md", "markdown", "rst", "tex", "docx", "pptx", "pdf"];

pub fn is_doc_path(path: &str) -> bool {
    path.rsplit_once('.').is_some_and(|(_, e)| DOC_EXTS.iter().any(|d| e.eq_ignore_ascii_case(d)))
}

pub struct Client {
    catalog: Arc<RwLock<Catalog>>,
    proc: Mutex<Option<Proc>>,
    pending: Pending,
    next_id: AtomicU64,
    items_version: AtomicU64,
    disabled: bool,
    touched: Mutex<Touched>,
}

/// Plain-language descriptions of built-ins and well-known apps. Names are only a few words,
/// which embeds far less distinctively than a paragraph; this is what lets "change screen
/// brightness" land on Display settings. Only the embedded text uses it, never the UI.
fn describe(name: &str) -> Option<&'static str> {
    Some(match name.to_lowercase().as_str() {
        "display settings" => "change screen brightness, resolution, scaling, night light, multiple monitors, refresh rate",
        "sound settings" => "change volume, speakers, microphone, audio output devices, headphones",
        "bluetooth & devices" => "pair bluetooth headphones, mouse, keyboard, printers and other connected devices",
        "wi-fi settings" => "connect to wifi, wireless network, internet connection",
        "network & internet" => "ethernet, VPN, proxy, airplane mode, mobile hotspot, internet problems",
        "apps & features" => "uninstall programs, installed applications, remove software",
        "default apps" => "choose the default browser, default program for file types",
        "startup apps" => "programs that run when the computer starts, boot, login, make the computer faster",
        "battery & power" => "battery saver, sleep settings, power mode, energy usage, screen timeout",
        "storage" => "disk space, free up space, clean temporary files, drives, storage sense",
        "windows update" => "update windows, check for updates, security patches, upgrade",
        "personalization" => "wallpaper, desktop background, themes, lock screen, taskbar, start menu",
        "dark mode & colors" => "dark theme, light theme, accent color, appearance, night mode",
        "privacy & security" => "app permissions, camera and microphone access, location, firewall, antivirus",
        "about this pc" => "computer name, specifications, processor, memory, windows version, serial number",
        "date & time" => "change time zone, clock, calendar, set time automatically",
        "language & region" => "keyboard layout, display language, country, regional format",
        "keyboard settings" => "typing, autocorrect, text suggestions, keyboard shortcuts",
        "mouse settings" => "pointer speed, cursor, scrolling, mouse buttons, touchpad",
        "accounts" => "sign in options, password, PIN, user accounts, email accounts",
        "notifications" => "alerts, focus assist, do not disturb, notification banners",
        "settings" => "open Windows settings, configure the computer",
        "lock" => "lock the screen, secure the computer when leaving the desk",
        "sleep" => "suspend, standby, save power, put the computer to sleep",
        "sign out" => "log off, switch user, end the session",
        "restart" => "reboot the computer",
        "shut down" => "power off, turn off the computer",
        "task manager" => "end task, kill a frozen app, force quit, CPU and memory usage, performance, processes",
        "control panel" => "classic settings, programs and features, system and security",
        "command prompt" => "terminal, cmd, console, command line, shell",
        "registry editor" => "regedit, edit registry keys",
        "device manager" => "drivers, hardware, update device drivers, troubleshoot devices",
        "services" => "windows services, background services, start and stop services",
        "snipping tool" => "take a screenshot, capture the screen, screen clip, annotate",
        "calculator" => "do math, arithmetic, calculations, convert units",
        "notepad" => "edit plain text files, quick notes, write text",
        "paint" => "draw, sketch, edit images, paint pictures",
        "camera" => "take photos, record video, webcam",
        "clock" => "alarm, timer, stopwatch, world clock",
        "photos" => "view pictures, image gallery, photo viewer",
        "microsoft store" => "download apps, install software, get games",
        "sound recorder" | "voice recorder" => "record audio, microphone recording, voice memo",
        "windows terminal" | "terminal" => "command line, powershell, shell, console",
        "powershell" | "windows powershell" => "command line, scripts, shell, administration",
        "file explorer" => "browse files and folders, open documents, drives",
        "microsoft edge" => "web browser, browse the internet, websites",
        "google chrome" => "web browser, browse the internet, websites",
        "firefox" | "mozilla firefox" => "web browser, browse the internet, websites",
        "word" | "microsoft word" => "write documents, word processor, letters, reports",
        "excel" | "microsoft excel" => "spreadsheets, formulas, tables, charts, data",
        "powerpoint" | "microsoft powerpoint" => "presentations, slides, slideshow",
        "outlook" | "microsoft outlook" => "email, mail, calendar, contacts",
        "onenote" | "microsoft onenote" => "take notes, notebooks, handwritten notes",
        "teams" | "microsoft teams" => "video calls, meetings, chat, collaboration",
        "spotify" => "listen to music, songs, playlists, podcasts",
        "discord" => "chat with friends, voice calls, gaming communities",
        "steam" => "play games, game library, buy games",
        "visual studio code" | "vs code" => "write code, programming, code editor, develop software",
        "vlc media player" => "play videos and music, media player, watch movies",
        _ => return None,
    })
}

pub fn available() -> bool {
    paths::semantic_exe().is_some() && paths::semantic_model_dir().is_some()
}

impl Client {
    pub fn new(catalog: Arc<RwLock<Catalog>>) -> Client {
        Client {
            catalog,
            proc: Mutex::new(None),
            pending: Arc::new(Mutex::new(HashMap::new())),
            next_id: AtomicU64::new(1),
            items_version: AtomicU64::new(u64::MAX),
            disabled: std::env::var("PALETTE_SEMANTIC").is_ok_and(|v| v == "0"),
            touched: Mutex::new(Touched { paths: HashSet::new(), first: None, last: Instant::now() }),
        }
    }

    /// Record documents the file watcher saw created or modified. They are forwarded to the
    /// helper in batches once things go quiet (editors save in bursts).
    pub fn touch(&self, paths: Vec<String>) {
        if self.disabled || paths.is_empty() {
            return;
        }
        let mut t = self.touched.lock().unwrap();
        t.paths.extend(paths);
        t.first.get_or_insert_with(Instant::now);
        t.last = Instant::now();
    }

    /// Start the thread that forwards batched file-change notices.
    pub fn start_flusher(self: &Arc<Self>) {
        let me = self.clone();
        std::thread::spawn(move || loop {
            std::thread::sleep(Duration::from_secs(2));
            let batch: Vec<String> = {
                let mut t = me.touched.lock().unwrap();
                let Some(first) = t.first else { continue };
                // Wait for 3 quiet seconds, but never hold a notice longer than 20 s.
                if t.last.elapsed() < Duration::from_secs(3) && first.elapsed() < Duration::from_secs(20) {
                    continue;
                }
                t.first = None;
                t.paths.drain().collect()
            };
            me.send_touch(batch);
        });
    }

    /// Only talks to a helper that is already running: file changes must never wake it up
    /// (its next start does a full scan anyway).
    fn send_touch(&self, batch: Vec<String>) {
        {
            let mut g = self.proc.lock().unwrap();
            let running = g.as_mut().is_some_and(|p| matches!(p.child.try_wait(), Ok(None)));
            if !running {
                return;
            }
        }
        for chunk in batch.chunks(200) {
            let msg = serde_json::json!({"op": "touch", "paths": chunk}).to_string();
            if !self.send(&msg) {
                return;
            }
        }
    }

    /// Start the helper if it is not running. Returns whether it is running.
    pub fn ensure_started(&self) -> bool {
        if self.disabled {
            return false;
        }
        let mut guard = self.proc.lock().unwrap();
        if let Some(p) = guard.as_mut() {
            if matches!(p.child.try_wait(), Ok(None)) {
                return true;
            }
            *guard = None;
        }
        let (Some(exe), Some(model)) = (paths::semantic_exe(), paths::semantic_model_dir()) else {
            return false;
        };
        let mut cmd = Command::new(exe);
        cmd.arg("serve")
            .arg("--data")
            .arg(paths::data_dir())
            .arg("--model")
            .arg(model)
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .creation_flags(NO_WINDOW);
        let Ok(mut child) = cmd.spawn() else { return false };
        let (Some(stdin), Some(stdout)) = (child.stdin.take(), child.stdout.take()) else {
            let _ = child.kill();
            return false;
        };

        let pending = self.pending.clone();
        std::thread::spawn(move || {
            for line in BufReader::new(stdout).lines() {
                let Ok(line) = line else { break };
                if let Ok(r) = serde_json::from_str::<RawReply>(&line) {
                    if let Some(tx) = pending.lock().unwrap().remove(&r.id) {
                        let _ = tx.send(r);
                    }
                }
            }
        });

        *guard = Some(Proc { child, stdin });
        drop(guard);
        self.items_version.store(u64::MAX, Ordering::Relaxed);
        self.sync_items();
        true
    }

    /// Send apps, settings and commands so they are searchable by meaning too.
    fn sync_items(&self) {
        let (version, items): (u64, Vec<serde_json::Value>) = {
            let cat = self.catalog.read().unwrap();
            let items = cat
                .items
                .iter()
                .map(|i| {
                    let base = match i.hit.kind {
                        Kind::App => format!("{} (application)", i.name),
                        _ => format!("{} - {}", i.name, i.hit.subtitle),
                    };
                    let text = match describe(&i.name) {
                        Some(d) => format!("{base}. {d}"),
                        None => base,
                    };
                    serde_json::json!({"key": i.hit.key(), "title": i.name, "text": text})
                })
                .collect();
            (cat.version, items)
        };
        if items.is_empty() {
            return;
        }
        let msg = serde_json::json!({"op": "items", "items": items}).to_string();
        if self.send(&msg) {
            self.items_version.store(version, Ordering::Relaxed);
        }
    }

    fn send(&self, line: &str) -> bool {
        let mut guard = self.proc.lock().unwrap();
        let Some(p) = guard.as_mut() else { return false };
        let ok = writeln!(p.stdin, "{line}").and_then(|_| p.stdin.flush()).is_ok();
        if !ok {
            *guard = None;
        }
        ok
    }

    /// Semantic matches for `q`, best first, already mapped to launchable hits.
    /// Blocks up to `timeout`; call from a worker thread.
    pub fn query(&self, q: &str, timeout: Duration) -> Vec<(Hit, f32)> {
        if !self.ensure_started() {
            return Vec::new();
        }
        if self.catalog.read().unwrap().version != self.items_version.load(Ordering::Relaxed) {
            self.sync_items();
        }

        let id = self.next_id.fetch_add(1, Ordering::Relaxed);
        let (tx, rx) = channel();
        self.pending.lock().unwrap().insert(id, tx);
        let msg = serde_json::json!({"op": "query", "id": id, "q": q, "k": 8}).to_string();
        if !self.send(&msg) {
            self.pending.lock().unwrap().remove(&id);
            return Vec::new();
        }
        let Ok(reply) = rx.recv_timeout(timeout) else {
            self.pending.lock().unwrap().remove(&id);
            return Vec::new();
        };

        let cat = self.catalog.read().unwrap();
        let t = thresholds();
        let mut out = Vec::new();
        for h in reply.hits {
            match h.src.as_str() {
                "item" if h.score >= t.item => {
                    if let Some(it) = cat.items.iter().find(|i| i.hit.key() == h.key) {
                        out.push((it.hit.clone(), h.score));
                    }
                }
                "doc" if h.score >= t.doc => {
                    if std::fs::symlink_metadata(&h.key).is_err() {
                        continue;
                    }
                    let (parent, name) = h.key.rsplit_once('\\').unwrap_or(("", h.key.as_str()));
                    out.push((
                        Hit {
                            title: name.to_string(),
                            subtitle: parent.to_string(),
                            kind: Kind::File,
                            action: Action::Open(h.key.clone()),
                            score: 0,
                        },
                        h.score,
                    ));
                }
                _ => {}
            }
        }
        out
    }

    /// One-line human status for the footer, from the helper's status file.
    pub fn status_text(&self) -> String {
        if self.disabled || !available() {
            return String::new();
        }
        let path = paths::data_dir().join("semantic").join("status.json");
        let Ok(text) = std::fs::read_to_string(path) else {
            return "Semantic search starting\u{2026}".into();
        };
        let Ok(v) = serde_json::from_str::<serde_json::Value>(&text) else { return String::new() };
        let n = |k: &str| v.get(k).and_then(|x| x.as_u64()).unwrap_or(0);
        match v.get("phase").and_then(|p| p.as_str()).unwrap_or("") {
            "indexing" => format!("Indexing {} / {}", n("indexed"), n("total")),
            "paused" => format!("Paused on battery \u{b7} {} / {}", n("indexed"), n("total")),
            "scanning" | "starting" => "Scanning documents\u{2026}".into(),
            _ => format!("{} docs searchable", n("indexed")),
        }
    }
}

impl Drop for Client {
    fn drop(&mut self) {
        if let Some(mut p) = self.proc.lock().unwrap().take() {
            let _ = writeln!(p.stdin, "{{\"op\":\"shutdown\"}}");
            let _ = p.child.kill();
        }
    }
}
