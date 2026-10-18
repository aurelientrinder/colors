//! Plans for requests the rules in intent.rs do not read, written by a small language model
//! (Gemma 3 270M fine-tuned on ml/intent): "get me outlook and teams", "make it quieter",
//! "close everything but spotify", "opne notepad and wether side by side".
//!
//! The model runs in a helper process: `palette-plan.exe` on the NPU (crates/palette-plan, about
//! 30 ms a plan) when the PC has one, else `llama-server.exe` (llama.cpp, on the CPU, about 60 ms).
//! Both answer the same HTTP calls. The helper is started a little after launch, stopped after a
//! long quiet spell, and killed with the launcher (a job object). The NPU's model is compiled
//! once, in the background, the first time (about a minute); llama.cpp answers until then.
//!
//! The model writes one line in the plan format of ml/intent/plan.py (llama.cpp is held to it by
//! a grammar, palette-plan by scoring only plan tokens); here the names in that line are matched
//! to real apps, Settings pages and folders. A plan naming anything that cannot be found is
//! dropped: the model never invents a step.
//!
//! Plans that were run are remembered, so the same request is a plan as soon as it is typed.

use crate::intent::{self, App, Layout, Media, Step};
use crate::model::{Action, Hit, Item, Kind};
use crate::paths;
use std::collections::HashMap;
use std::ffi::OsString;
use std::io::{Read, Write};
use std::net::{SocketAddr, TcpListener, TcpStream};
use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::{Child, Command, Stdio};
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};

const NO_WINDOW: u32 = 0x0800_0000;
const GRAMMAR: &str = include_str!("../../../ml/intent/plan.gbnf");
/// The helper is stopped after this long without a request (restarting it takes about a second).
const IDLE: Duration = Duration::from_secs(30 * 60);
/// Plans remembered, most recent first.
const REMEMBERED: usize = 300;

struct Proc {
    child: Child,
    port: u16,
    /// palette-plan on the NPU, not llama-server.
    npu: bool,
}

/// palette-plan stopped working this session (no NPU after all, a driver error): llama.cpp only.
static NPU_FAILED: AtomicBool = AtomicBool::new(false);

pub struct Planner {
    proc: Mutex<Option<Proc>>,
    last_used: Mutex<Instant>,
    /// Lines the model wrote, by request, for this session.
    lines: Mutex<HashMap<String, String>>,
    /// Requests whose plan was run: request -> line. Saved in plans.json.
    remembered: Mutex<Vec<(String, String)>>,
}

pub fn available() -> bool {
    paths::planner_dir().is_some() || npu_parts().is_some()
}

/// palette-plan.exe and its model folder.
fn npu_parts() -> Option<(PathBuf, PathBuf)> {
    Some((paths::plan_exe()?, paths::planner_npu_dir()?))
}

/// palette-plan and its model, if the model is compiled for the NPU. If it is not, it is
/// compiled now in the background (once a session; on a PC without an NPU that ends at once).
fn npu_ready() -> Option<(PathBuf, PathBuf)> {
    static COMPILING: AtomicBool = AtomicBool::new(false);
    if NPU_FAILED.load(Ordering::Relaxed) {
        return None;
    }
    let (exe, dir) = npu_parts()?;
    if palette_ov::is_cached(&palette_ov::cache_key(&dir.join("openvino_model.xml"), "plan")) {
        return Some((exe, dir));
    }
    if !COMPILING.swap(true, Ordering::SeqCst) {
        let child = Command::new(&exe)
            .arg("compile")
            .arg("--model")
            .arg(&dir)
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::null())
            .creation_flags(NO_WINDOW)
            .spawn();
        if let Ok(child) = child {
            kill_with_us(&child); // and otherwise left to finish
        }
    }
    None
}

/// Start `exe` with the arguments `args(port)` and wait until it answers `/health`.
fn launch(exe: &Path, args: impl FnOnce(u16) -> Vec<OsString>, dir: &Path) -> Option<(Child, u16)> {
    launch_wait(exe, args, dir, Duration::from_secs(20))
}

/// As `launch`, waiting up to `wait` for the server (a big model takes longer to load).
pub(crate) fn launch_wait(exe: &Path, args: impl FnOnce(u16) -> Vec<OsString>, dir: &Path, wait: Duration) -> Option<(Child, u16)> {
    // A free port on this PC only.
    let port = TcpListener::bind("127.0.0.1:0").ok()?.local_addr().ok()?.port();
    let mut child = Command::new(exe)
        .args(args(port))
        .current_dir(dir)
        .stdin(Stdio::null())
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .creation_flags(NO_WINDOW)
        .spawn()
        .ok()?;
    kill_with_us(&child);
    let start = Instant::now();
    while start.elapsed() < wait {
        if http(port, "GET", "/health", "", Duration::from_millis(500)).is_some_and(|b| b.contains("\"ok\"")) {
            return Some((child, port));
        }
        if !matches!(child.try_wait(), Ok(None)) {
            return None;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
    let _ = child.kill();
    None
}

pub fn get() -> &'static Planner {
    static P: OnceLock<Planner> = OnceLock::new();
    P.get_or_init(|| {
        let p = Planner {
            proc: Mutex::new(None),
            last_used: Mutex::new(Instant::now()),
            lines: Mutex::new(HashMap::new()),
            remembered: Mutex::new(load()),
        };
        std::thread::spawn(|| loop {
            std::thread::sleep(Duration::from_secs(60));
            get().stop_if_idle();
        });
        p
    })
}

/// The request as a key: case and spacing do not matter.
fn key(q: &str) -> String {
    q.split_whitespace().collect::<Vec<_>>().join(" ").to_lowercase()
}

fn store() -> std::path::PathBuf {
    paths::data_dir().join("plans.json")
}

fn load() -> Vec<(String, String)> {
    std::fs::read_to_string(store()).ok().and_then(|s| serde_json::from_str(&s).ok()).unwrap_or_default()
}

impl Planner {
    /// Start the helper if it is not running. Returns its port.
    pub fn ensure_started(&self) -> Option<u16> {
        let mut g = self.proc.lock().ok()?;
        let npu = npu_ready();
        if let Some(p) = g.as_mut() {
            let alive = matches!(p.child.try_wait(), Ok(None));
            if alive && (p.npu || npu.is_none()) {
                return Some(p.port);
            }
            if p.npu {
                NPU_FAILED.store(true, Ordering::Relaxed); // it ended on its own
            }
            // Gone, or llama.cpp while the NPU's model has become ready.
            let _ = p.child.kill();
            *g = None;
        }
        if let Some((exe, dir)) = npu {
            let model = dir.clone();
            let args = move |port: u16| -> Vec<OsString> {
                vec!["serve".into(), "--port".into(), port.to_string().into(), "--model".into(), model.into()]
            };
            match launch(&exe, args, &dir) {
                Some((child, port)) => {
                    // The NPU's first run takes half a second; take it now, not on a request.
                    let _ = http(port, "POST", "/completion", r#"{"prompt":"open notepad\n","n_predict":8}"#, Duration::from_secs(5));
                    *g = Some(Proc { child, port, npu: true });
                    *self.last_used.lock().ok()? = Instant::now();
                    return Some(port);
                }
                None => NPU_FAILED.store(true, Ordering::Relaxed),
            }
        }
        let dir = paths::planner_dir()?;
        let threads = std::thread::available_parallelism().map(|n| n.get().clamp(2, 8) / 2).unwrap_or(4).max(2);
        let model = dir.join("plan.gguf");
        let args = move |port: u16| -> Vec<OsString> {
            let mut a: Vec<OsString> = vec!["-m".into(), model.into()];
            for s in ["--host", "127.0.0.1", "--port", &port.to_string(), "-c", "512", "-np", "1", "-ngl", "0"] {
                a.push(s.into());
            }
            a.extend(["-t".into(), threads.to_string().into(), "--no-webui".into()]);
            a
        };
        let (child, port) = launch(&dir.join("llama-server.exe"), args, &dir)?;
        *g = Some(Proc { child, port, npu: false });
        *self.last_used.lock().ok()? = Instant::now();
        Some(port)
    }

    fn stop_if_idle(&self) {
        let idle = self.last_used.lock().map(|t| t.elapsed() > IDLE).unwrap_or(false);
        if idle {
            if let Ok(mut g) = self.proc.lock() {
                if let Some(mut p) = g.take() {
                    let _ = p.child.kill();
                }
            }
        }
    }

    /// The line the model writes for `q` ("none" when it is not a request). Blocks for about
    /// 30 ms on the NPU, 60 on the CPU; a second or two when the helper has to start.
    pub fn line(&self, q: &str) -> Option<String> {
        let k = key(q);
        if let Some(l) = self.lines.lock().ok()?.get(&k) {
            return Some(l.clone());
        }
        let port = self.ensure_started()?;
        *self.last_used.lock().ok()? = Instant::now();
        let body = serde_json::json!({
            "prompt": format!("{}\n", q.trim()),
            "n_predict": 48,
            "temperature": 0,
            "top_k": 1,
            "cache_prompt": true,
            "grammar": GRAMMAR,
        })
        .to_string();
        let reply = http(port, "POST", "/completion", &body, Duration::from_secs(5))?;
        let v: serde_json::Value = serde_json::from_str(&reply).ok()?;
        let line = v["content"].as_str()?.trim().to_string();
        let mut lines = self.lines.lock().ok()?;
        if lines.len() > 2000 {
            lines.clear();
        }
        lines.insert(k, line.clone());
        Some(line)
    }

    /// The plan run before for `q`, if any.
    pub fn remembered(&self, q: &str) -> Option<String> {
        let k = key(q);
        self.remembered.lock().ok()?.iter().find(|(r, _)| *r == k).map(|(_, l)| l.clone())
    }

    /// `q`'s plan was run: offer it at once next time.
    pub fn remember(&self, q: &str, line: &str) {
        let k = key(q);
        let Ok(mut r) = self.remembered.lock() else { return };
        r.retain(|(x, _)| *x != k);
        r.insert(0, (k, line.to_string()));
        r.truncate(REMEMBERED);
        if let Ok(s) = serde_json::to_string(&*r) {
            let _ = std::fs::write(store(), s);
        }
    }
}

/// Put `child` in a job that ends when this process does: the helper never outlives the launcher.
fn kill_with_us(child: &Child) {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JobObjectExtendedLimitInformation, SetInformationJobObject,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
    };
    static JOB: OnceLock<isize> = OnceLock::new();
    let job = *JOB.get_or_init(|| unsafe {
        let Ok(job) = CreateJobObjectW(None, None) else { return 0 };
        let mut info = JOBOBJECT_EXTENDED_LIMIT_INFORMATION::default();
        info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        let _ = SetInformationJobObject(
            job,
            JobObjectExtendedLimitInformation,
            &info as *const _ as *const _,
            std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
        );
        job.0 as isize // kept open for the life of the process
    });
    if job != 0 {
        unsafe {
            let _ = AssignProcessToJobObject(HANDLE(job as *mut _), HANDLE(child.as_raw_handle()));
        }
    }
}

/// One HTTP/1.1 request to the helper; the body of a 200 reply.
fn http(port: u16, method: &str, path: &str, body: &str, timeout: Duration) -> Option<String> {
    let addr = SocketAddr::from(([127, 0, 0, 1], port));
    let mut s = TcpStream::connect_timeout(&addr, Duration::from_millis(300)).ok()?;
    s.set_read_timeout(Some(timeout)).ok()?;
    s.set_nodelay(true).ok()?;
    let req = format!(
        "{method} {path} HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
        body.len()
    );
    s.write_all(req.as_bytes()).ok()?;
    let mut out = Vec::new();
    s.read_to_end(&mut out).ok()?;
    let text = String::from_utf8(out).ok()?;
    let (head, rest) = text.split_once("\r\n\r\n")?;
    if !head.starts_with("HTTP/1.1 200") {
        return None;
    }
    if head.to_ascii_lowercase().contains("transfer-encoding: chunked") {
        // size line, data, ... 0
        let mut body = String::new();
        let mut r = rest;
        while let Some((size, tail)) = r.split_once("\r\n") {
            let n = usize::from_str_radix(size.trim(), 16).ok()?;
            if n == 0 {
                break;
            }
            body.push_str(tail.get(..n)?);
            r = tail.get(n + 2..)?;
        }
        return Some(body);
    }
    Some(rest.to_string())
}

// ---------------------------------------------------------------- the line, as steps

/// Settings pages by the names the model uses (ml/intent/apps.json).
const PAGES: &[(&str, &str, &[&str])] = &[
    ("Bluetooth & devices", "ms-settings:bluetooth", &["bluetooth", "devices"]),
    ("Display", "ms-settings:display", &["display", "screen", "monitor", "resolution"]),
    ("Sound", "ms-settings:sound", &["sound", "audio", "speakers"]),
    ("Wi-Fi", "ms-settings:network-wifi", &["wi-fi", "wifi", "wireless"]),
    ("Network & internet", "ms-settings:network", &["network", "internet", "network & internet"]),
    ("Personalization", "ms-settings:personalization", &["personalization"]),
    ("Background", "ms-settings:personalization-background", &["background", "wallpaper", "desktop background"]),
    ("Colors", "ms-settings:colors", &["colors", "color", "accent color"]),
    ("Themes", "ms-settings:themes", &["themes", "theme"]),
    ("Lock screen", "ms-settings:lockscreen", &["lock screen"]),
    ("Taskbar", "ms-settings:taskbar", &["taskbar"]),
    ("Start", "ms-settings:personalization-start", &["start", "start menu"]),
    ("Installed apps", "ms-settings:appsfeatures", &["installed apps", "apps", "uninstall", "apps & features"]),
    ("Default apps", "ms-settings:defaultapps", &["default apps", "default browser"]),
    ("Startup apps", "ms-settings:startupapps", &["startup apps", "startup"]),
    ("Accounts", "ms-settings:yourinfo", &["accounts", "account", "password"]),
    ("Date & time", "ms-settings:dateandtime", &["date & time", "date", "time", "time zone"]),
    ("Language & region", "ms-settings:regionlanguage", &["language & region", "language", "region"]),
    ("Game Mode", "ms-settings:gaming-gamemode", &["gaming", "game mode"]),
    ("Accessibility", "ms-settings:easeofaccess", &["accessibility"]),
    ("Privacy & security", "ms-settings:privacy", &["privacy & security", "privacy", "security"]),
    ("Windows Update", "ms-settings:windowsupdate", &["windows update", "update", "updates"]),
    ("Power & battery", "ms-settings:batterysaver", &["power & battery", "power", "battery"]),
    ("Storage", "ms-settings:storagesense", &["storage", "disk space"]),
    ("Notifications", "ms-settings:notifications", &["notifications"]),
    ("Mouse", "ms-settings:mousetouchpad", &["mouse"]),
    ("Typing", "ms-settings:typing", &["typing", "keyboard"]),
    ("Printers & scanners", "ms-settings:printers", &["printers & scanners", "printer", "printers", "scanner"]),
    ("Multitasking", "ms-settings:multitasking", &["multitasking", "snap layouts", "snap"]),
    ("Night light", "ms-settings:nightlight", &["night light"]),
    ("VPN", "ms-settings:network-vpn", &["vpn"]),
    ("Mobile hotspot", "ms-settings:network-mobilehotspot", &["mobile hotspot", "hotspot"]),
    ("Microphone", "ms-settings:privacy-microphone", &["microphone", "mic"]),
    ("Location", "ms-settings:privacy-location", &["location"]),
    ("Clipboard", "ms-settings:clipboard", &["clipboard"]),
    ("About", "ms-settings:about", &["about", "system info", "pc info"]),
    ("Recovery", "ms-settings:recovery", &["recovery"]),
    ("Activation", "ms-settings:activation", &["activation"]),
    ("Touchpad", "ms-settings:devices-touchpad", &["touchpad"]),
];

const FOLDERS: &[(&str, &str, &str)] = &[
    ("downloads", "Downloads", "shell:Downloads"),
    ("documents", "Documents", "shell:Personal"),
    ("desktop", "Desktop", "shell:Desktop"),
    ("pictures", "Pictures", "shell:My Pictures"),
    ("music", "Music", "shell:My Music"),
    ("videos", "Videos", "shell:My Video"),
    ("home", "your user folder", "shell:Profile"),
    ("recycle bin", "the Recycle Bin", "shell:RecycleBinFolder"),
    ("this pc", "This PC", "shell:MyComputerFolder"),
    ("onedrive", "OneDrive", "shell:OneDrive"),
    ("appdata", "AppData", "shell:AppData"),
    ("temp", "the temp folder", "%TEMP%"),
    ("program files", "Program Files", "shell:ProgramFiles"),
];

fn page(name: &str, items: &[Item]) -> Option<Step> {
    let n = intent::bare_name(name).trim_end_matches(" settings").trim();
    if let Some((title, uri, _)) = PAGES.iter().find(|(t, _, says)| t.eq_ignore_ascii_case(n) || says.contains(&n)) {
        return Some(Step::Uri { uri: uri.to_string(), title: format!("Open {title} settings") });
    }
    // Windows' own settings index: every word of the name begins a word of the page.
    let words: Vec<&str> = n.split_whitespace().collect();
    if words.is_empty() {
        return None;
    }
    let it = items
        .iter()
        .filter(|i| i.hit.kind == Kind::Setting)
        .filter(|i| intent::word_starts(&i.name.to_lowercase(), &words))
        .min_by_key(|i| (i.keywords.is_some(), i.name.len()))?;
    match &it.hit.action {
        Action::Open(uri) => Some(Step::Uri { uri: uri.clone(), title: format!("Open {}", it.name) }),
        _ => None,
    }
}

fn quick(what: &str, value: &str) -> Option<Step> {
    let q = |id: String, title: &str| Some(Step::Quick { id, title: title.to_string() });
    let label = match what {
        "dark" => "dark mode",
        "wifi" => "Wi-Fi",
        "bluetooth" => "Bluetooth",
        _ => "",
    };
    match (what, value) {
        ("mute", "on") => q("mute:on".into(), "Mute"),
        ("mute", "off") => q("mute:off".into(), "Unmute"),
        // Toggles say what they will do in the current state, as their own rows do.
        (_, "toggle") => {
            let name = match what {
                "dark" => "dark mode",
                "mute" => "mute",
                w => w,
            };
            let h = crate::quick::hits(name).into_iter().find(|h| matches!(&h.action, Action::Builtin(id) if id.starts_with(&format!("quick:{what}:")) || (what == "mute" && id.starts_with("quick:mute:"))))?;
            let Action::Builtin(id) = &h.action else { return None };
            q(id.trim_start_matches("quick:").to_string(), &h.title)
        }
        ("dark" | "wifi" | "bluetooth", "on" | "off") => q(format!("{what}:{value}"), &format!("Turn {label} {value}")),
        ("volume", "up") => q("volume:up".into(), "Turn the volume up"),
        ("volume", "down") => q("volume:down".into(), "Turn the volume down"),
        ("brightness", "up") => q("brightness:up".into(), "Turn the brightness up"),
        ("brightness", "down") => q("brightness:down".into(), "Turn the brightness down"),
        ("volume" | "brightness", n) => {
            let n: u32 = n.parse().ok()?;
            let n = n.min(100);
            q(format!("{what}:{n}"), &format!("Set {what} to {n}%"))
        }
        _ => None,
    }
}

fn layout(s: &str) -> Option<Layout> {
    Some(match s {
        "side" => Layout::Side,
        "grid" => Layout::Grid,
        "left" => Layout::Left,
        "right" => Layout::Right,
        "top" => Layout::Top,
        "bottom" => Layout::Bottom,
        "full" => Layout::Full,
        _ => return None,
    })
}

/// The steps `line` asks for, with every name found; None if it is "none", is not a plan, or
/// names anything that is not there.
pub fn steps(line: &str, items: &[Item]) -> Option<Vec<Step>> {
    let line = line.trim();
    if line.is_empty() || line == "none" {
        return None;
    }
    let app = |n: &str| intent::find_app_loose(n, items);
    let mut plan = Vec::new();
    let mut since: Vec<App> = Vec::new(); // named since the previous snap
    for part in line.split(" ; ") {
        let (verb, rest) = part.trim().split_once(' ').unwrap_or((part.trim(), ""));
        let rest = rest.trim();
        let step = match verb {
            "open" | "focus" | "close" | "min" | "max" => {
                let a = app(rest)?;
                since.push(a.clone());
                match verb {
                    "open" => Step::Open(a),
                    "focus" => Step::Focus(a),
                    "close" => Step::Close(a),
                    "min" => Step::Minimize(a),
                    _ => Step::Maximize(a),
                }
            }
            "snap" => {
                let (l, names) = rest.split_once(' ').unwrap_or((rest, ""));
                let l = layout(l)?;
                let mut apps: Vec<App> = Vec::new();
                for a in names.split(',').map(str::trim).filter(|s| !s.is_empty()) {
                    apps.push(app(a)?);
                }
                if apps.is_empty() {
                    for a in since.drain(..) {
                        if !apps.contains(&a) {
                            apps.push(a);
                        }
                    }
                }
                since.clear();
                match (l, apps.len()) {
                    (_, 0) => return None,
                    (Layout::Full, 1) => Step::Maximize(apps.remove(0)),
                    _ => Step::Arrange { layout: l, apps },
                }
            }
            "desktop" => Step::ShowDesktop,
            "shot" => intent::shot(),
            "lock" => Step::Builtin { id: "lock".into(), title: "Lock this PC".into() },
            "sleep" => Step::Builtin { id: "sleep".into(), title: "Put this PC to sleep".into() },
            "media" => Step::Media(match rest {
                "play" => Media::PlayPause,
                "next" => Media::Next,
                "prev" => Media::Previous,
                "stop" => Media::Stop,
                _ => return None,
            }),
            "set" => {
                let (what, value) = rest.split_once(' ')?;
                quick(what, value)?
            }
            "settings" => page(rest, items)?,
            "folder" => {
                let n = intent::bare_name(rest).trim_end_matches(" folder");
                let (_, title, uri) = FOLDERS.iter().find(|(k, _, _)| *k == n)?;
                let uri = if *uri == "%TEMP%" { std::env::temp_dir().to_string_lossy().into_owned() } else { uri.to_string() };
                Step::Uri { uri, title: format!("Open {title}") }
            }
            "url" => {
                let d = rest.trim_start_matches("https://").trim_start_matches("http://").trim_end_matches('/');
                // A site, not words: one token with a dot and only address characters.
                let ok = d.contains('.') && !d.starts_with('.') && d.chars().all(|c| c.is_alphanumeric() || "-._~/?#=&%+:".contains(c));
                if !ok {
                    return None;
                }
                Step::Uri { uri: format!("https://{d}"), title: format!("Open {}", d.trim_start_matches("www.")) }
            }
            "closeall" => {
                let mut keep = Vec::new();
                if let Some(names) = rest.strip_prefix("except ") {
                    for a in names.split(',').map(str::trim).filter(|s| !s.is_empty()) {
                        keep.push(app(a)?);
                    }
                } else if !rest.is_empty() {
                    return None;
                }
                Step::CloseAll { keep }
            }
            _ => return None,
        };
        plan.push(step);
    }
    // Opening or switching to one app already has its own row.
    match plan.as_slice() {
        [] | [Step::Open(_) | Step::Focus(_)] => None,
        _ => Some(plan),
    }
}

/// The plan row for `line`, if it is one.
pub fn hit(line: &str, items: &[Item]) -> Option<Hit> {
    steps(line, items).map(intent::plan_hit)
}

/// Does the plan the model wrote have anything to do with what was typed? A model trained on English
/// requests writes a plan for any words it is given ("pour resoudre cette equation" became "open about
/// this pc"), and that row would push the real matches out of sight. A request starts with what to do or
/// names what it is about, so one of its words is in the plan.
pub fn grounded(q: &str, line: &str) -> bool {
    const VERBS: [&str; 40] = [
        "open", "close", "snap", "set", "turn", "switch", "toggle", "play", "pause", "stop", "mute", "unmute", "show",
        "hide", "minimize", "minimise", "maximize", "maximise", "launch", "start", "go", "put", "move", "make", "lower",
        "raise", "increase", "decrease", "skip", "next", "previous", "lock", "quit", "exit", "arrange", "tile", "enable",
        "disable", "kill", "run",
    ];
    let lq = q.to_lowercase();
    if lq.split_whitespace().next().is_some_and(|w| VERBS.contains(&w)) {
        return true;
    }
    let lline = line.to_lowercase();
    let plan: Vec<&str> = lline.split(|c: char| !c.is_alphanumeric()).filter(|w| w.chars().count() >= 3).collect();
    lq.split(|c: char| !c.is_alphanumeric())
        .filter(|w| w.chars().count() >= 3)
        .any(|w| plan.iter().any(|p| p.starts_with(w) || w.starts_with(p)))
}

/// Is `q` worth asking the model about? Words, not a sum, a path or a single name.
pub fn worth_asking(q: &str) -> bool {
    let q = q.trim();
    let words = q.split_whitespace().count();
    words >= 2
        && q.chars().count() >= 6
        && q.chars().count() <= 160
        && q.chars().filter(|c| c.is_alphabetic()).count() >= 4
        && !q.starts_with(['>', '=', '/', '\\', '.'])
        && !q.contains(":\\")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn app(n: &str) -> Item {
        Item::new(n, Hit { title: n.into(), subtitle: String::new(), kind: Kind::App, action: Action::Open(format!("shell:AppsFolder\\{n}")), score: 0 }, None)
    }

    fn items() -> Vec<Item> {
        ["Notepad", "Weather", "Google Chrome", "Spotify", "Discord", "Visual Studio Code", "Outlook"].map(app).to_vec()
    }

    fn title(line: &str) -> Option<String> {
        hit(line, &items()).map(|h| h.title)
    }

    #[test]
    fn a_plan_must_have_something_to_do_with_the_request() {
        assert!(!grounded("pour r\u{e9}soudre cette \u{e9}quation", "open about this pc"));
        assert!(grounded("put chrome and spotify side by side", "open chrome ; open spotify ; snap side"));
        assert!(grounded("could you get weather up", "open weather"));
        assert!(grounded("close all but discord", "closeall except discord"));
        assert!(!grounded("the lease ends in august", "open outlook"));
    }

    #[test]
    fn lines_become_plans() {
        assert_eq!(title("open notepad ; open wether ; snap side").as_deref(), Some("Open Notepad and Weather side by side"));
        assert_eq!(title("close crome ; close spotfy").as_deref(), Some("Close Google Chrome, then close Spotify"));
        assert_eq!(title("snap left vscdoe").as_deref(), Some("Snap Visual Studio Code on the left"));
        assert_eq!(title("closeall except spotify, discord").as_deref(), Some("Close all windows except Spotify and Discord"));
        assert_eq!(title("set volume down").as_deref(), Some("Turn the volume down"));
        assert_eq!(title("url github.com ; open vs code").as_deref(), Some("Open github.com, then open Visual Studio Code"));
        assert_eq!(title("settings wallpaper").as_deref(), Some("Open Background settings"));
        assert_eq!(title("folder downloads").as_deref(), Some("Open Downloads"));
        assert_eq!(title("open chrome ; max chrome").as_deref(), Some("Open Google Chrome, then maximize Google Chrome"));
    }

    #[test]
    fn nothing_made_up() {
        assert_eq!(title("none"), None);
        assert_eq!(title("open notepad"), None); // has its own row
        assert_eq!(title("open notepad ; open xyzzyplorp"), None); // no such app
        assert_eq!(title("url not a site"), None);
        assert_eq!(title("folder secrets"), None);
        assert_eq!(title("explode chrome"), None);
    }
}
