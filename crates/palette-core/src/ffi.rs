//! C ABI for non-Rust shells. All strings are UTF-8, NUL-terminated.
//! Hits are returned as a JSON array and remembered so `pc_launch` /
//! `pc_icon` can refer to them by index.

use crate::actions;
use crate::index::Catalog;
use crate::model::{Action, Hit};
use crate::search::Searcher;
use crate::usage::Usage;
use crate::home::{self, Pins};
use crate::{iconsrc, scan, semantic};
use serde::Serialize;
use std::ffi::{c_char, c_void, CStr, CString};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock};
use std::time::Duration;

struct Engine {
    searcher: Searcher,
    catalog: Arc<RwLock<Catalog>>,
    usage: Arc<Mutex<Usage>>,
    pins: Mutex<Pins>,
    sem: Arc<semantic::Client>,
    /// Last generation that already received its related rows (appending twice would duplicate them).
    sem_gen: AtomicU64,
    /// (generation, hits) of the most recent search.
    last: Mutex<(u64, Vec<Hit>)>,
    /// (generation, query) of the most recent search: what you typed to get the list you pick from.
    last_query: Mutex<(u64, String)>,
    gen: AtomicU64,
    /// The plan the model wrote for generation N: (generation, request, line, row title). Run, it
    /// is remembered for the request.
    model_plan: Mutex<Option<(u64, String, String, String)>>,
}

static ENGINE: OnceLock<Engine> = OnceLock::new();

#[derive(Serialize)]
struct Results<'a> {
    gen: u64,
    hits: Vec<HitJson<'a>>,
}

/// Related (semantic) rows appended to generation `gen`; `base` is the index of the first.
#[derive(Serialize)]
struct Appended<'a> {
    gen: u64,
    base: usize,
    /// True when `hits` is the complete new list (the shell replaces everything it shows).
    replace: bool,
    hits: Vec<HitJson<'a>>,
}

#[derive(Serialize)]
struct HitJson<'a> {
    title: &'a str,
    subtitle: &'a str,
    kind: &'a str,
    icon_key: Option<String>,
    copy_text: Option<&'a str>,
    /// True for rows found by meaning rather than by name.
    related: bool,
    /// Full path of a file hit; the shell previews it.
    path: Option<&'a str>,
    /// Full path of a folder hit; the right arrow browses into it.
    folder: Option<&'a str>,
    /// Pinned to the home view.
    pinned: bool,
    /// A switch, slider or media buttons drawn on the row to change it in place.
    #[serde(skip_serializing_if = "Option::is_none")]
    control: Option<crate::quick::Control>,
    /// What a command or request row does, for its icon: a built-in's id ("lock") or the plan's
    /// first step in the model's words ("open notepad", "media play", "set wifi on").
    #[serde(skip_serializing_if = "Option::is_none")]
    does: Option<String>,
}

fn does_of(h: &Hit) -> Option<String> {
    match (&h.action, h.kind) {
        (Action::Builtin(id), crate::model::Kind::Command) => Some(id.clone()),
        (Action::Plan(steps), _) => crate::intent::to_line(steps).split(" ; ").next().map(str::to_string),
        _ => None,
    }
}

/// The control a row carries: its quick setting's, or media buttons for a play/pause request.
fn control_of(h: &Hit) -> Option<crate::quick::Control> {
    match &h.action {
        Action::Builtin(id) if h.kind == crate::model::Kind::Toggle => crate::quick::control(id.strip_prefix("quick:")?),
        Action::Plan(steps) if matches!(steps.as_slice(), [crate::intent::Step::Media(_)]) => {
            Some(crate::quick::Control { kind: "media", key: "media", on: false, value: 0 })
        }
        _ => None,
    }
}

/// Keys of the pinned items, to flag them in results.
fn pinned_keys(e: &Engine) -> std::collections::HashSet<String> {
    e.pins.lock().map(|p| p.list().iter().map(Hit::key).collect()).unwrap_or_default()
}

fn hit_json<'a>(h: &'a Hit, related: bool, pinned: &std::collections::HashSet<String>) -> HitJson<'a> {
    HitJson {
        pinned: pinned.contains(&h.key()),
        title: &h.title,
        subtitle: &h.subtitle,
        kind: h.kind.label(),
        icon_key: iconsrc::icon_key(h),
        copy_text: match &h.action {
            Action::Copy(t) => Some(t.as_str()),
            _ => None,
        },
        related,
        path: match (&h.action, h.kind) {
            (Action::Open(p), crate::model::Kind::File) => Some(p.as_str()),
            _ => None,
        },
        folder: match (&h.action, h.kind) {
            (Action::Open(p), crate::model::Kind::Folder) => Some(p.as_str()),
            _ => None,
        },
        control: control_of(h),
        does: does_of(h),
    }
}

/// Change a quick setting from its row's control: `<key>:<value>` ("volume:40", "wifi:on").
/// Returns 1 if it was done.
///
/// # Safety
/// `id` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_quick(id: *const c_char) -> i32 {
    if id.is_null() {
        return 0;
    }
    let id = CStr::from_ptr(id).to_str().unwrap_or("");
    crate::quick::run(id) as i32
}

/// The control for quick setting `key` as it is now (`{"kind","key","on","value"}`), or null.
/// Free with `pc_free`.
///
/// # Safety
/// `key` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_quick_control(key: *const c_char) -> *mut c_char {
    if key.is_null() {
        return std::ptr::null_mut();
    }
    let key = CStr::from_ptr(key).to_str().unwrap_or("");
    match crate::quick::control_for(key) {
        Some(c) => into_c(serde_json::to_string(&c).unwrap_or_default()),
        None => std::ptr::null_mut(),
    }
}

/// Start the engine (idempotent). Returns 1 on success.
#[no_mangle]
pub extern "C" fn pc_init() -> i32 {
    ENGINE.get_or_init(|| {
        let catalog = Arc::new(RwLock::new(Catalog::default()));
        let usage = Arc::new(Mutex::new(Usage::load()));
        let notify: crate::Notify = Arc::new(|| {});
        scan::spawn_manager(catalog.clone(), notify);

        // Semantic tier. The file watcher feeds it document changes so new and edited files
        // become searchable by meaning within seconds instead of at the next full scan.
        let sem = Arc::new(semantic::Client::new(catalog.clone()));
        sem.start_flusher();
        {
            let s = sem.clone();
            scan::spawn_watcher(catalog.clone(), Arc::new(move |docs| s.touch(docs)));
        }

        // Read the recent files and find the radios now, so neither the first home view nor the
        // first "wifi" typed waits on them.
        std::thread::spawn(|| {
            home::recent_cached();
            crate::quick::warm();
        });

        // Start its process a little after launch so the model load and the first indexing
        // pass never compete with the app's own startup.
        if semantic::available() {
            let s = sem.clone();
            std::thread::spawn(move || {
                std::thread::sleep(Duration::from_secs(20));
                s.ensure_started();
            });
        }

        // The plan model too, so the first request it reads does not wait for it to load.
        if crate::planner::available() {
            std::thread::spawn(|| {
                std::thread::sleep(Duration::from_secs(25));
                crate::planner::get().ensure_started();
            });
        }

        Engine {
            searcher: Searcher::new(catalog.clone(), usage.clone()),
            catalog,
            usage,
            pins: Mutex::new(Pins::load()),
            sem,
            sem_gen: AtomicU64::new(0),
            last: Mutex::new((0, Vec::new())),
            last_query: Mutex::new((0, String::new())),
            gen: AtomicU64::new(0),
            model_plan: Mutex::new(None),
        }
    });
    1
}

fn into_c(s: String) -> *mut c_char {
    CString::new(s.replace('\0', "")).map(CString::into_raw).unwrap_or(std::ptr::null_mut())
}

/// Search; returns `{"gen":N,"hits":[...]}`. Free with `pc_free`.
///
/// # Safety
/// `query` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_search(query: *const c_char) -> *mut c_char {
    let Some(e) = ENGINE.get() else { return into_c("{\"gen\":0,\"hits\":[]}".into()) };
    let q = if query.is_null() { "" } else { CStr::from_ptr(query).to_str().unwrap_or("") };
    // Nothing typed: the home view (pins, most used, recent files, All apps).
    let hits = if q.trim().is_empty() {
        // Text selected in the window behind the palette leads, if its read has finished: the
        // palette has been opening for a few ms by now, a read takes about as long.
        let selection = crate::uia::selection(Duration::from_millis(40)).map(|t| home::selection_row(&t, &crate::uia::app_name()));
        let pins = e.pins.lock().unwrap();
        let usage = e.usage.lock().unwrap();
        home::home(&pins, &usage, selection)
    } else {
        e.searcher.search(q)
    };
    let pinned = pinned_keys(e);
    let json: Vec<HitJson> = hits.iter().map(|h| hit_json(h, false, &pinned)).collect();
    let gen = e.gen.fetch_add(1, Ordering::SeqCst) + 1;
    let out = serde_json::to_string(&Results { gen, hits: json })
        .unwrap_or_else(|_| "{\"gen\":0,\"hits\":[]}".into());
    *e.last.lock().unwrap() = (gen, hits);
    *e.last_query.lock().unwrap() = (gen, q.to_string());
    into_c(out)
}

/// Semantic ("related") matches for `query`, appended to the hits of search generation
/// `gen`. Slow (tens of ms warm, seconds when the model process must start), so call it
/// from a worker thread after showing the instant results. Returns
/// `{"gen":N,"base":I,"hits":[...]}`; empty `hits` if nothing relevant or `gen` is stale.
/// Free with `pc_free`.
///
/// # Safety
/// `query` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_semantic(gen: u64, query: *const c_char) -> *mut c_char {
    let empty = || into_c(format!("{{\"gen\":{gen},\"base\":0,\"hits\":[]}}"));
    let Some(e) = ENGINE.get() else { return empty() };
    let q = if query.is_null() { "" } else { CStr::from_ptr(query).to_str().unwrap_or("") }.trim();
    if q.chars().count() < 3 || q.starts_with('>') {
        return empty();
    }
    if e.sem_gen.swap(gen, Ordering::SeqCst) == gen {
        return empty();
    }

    // "clip the tracking number": only what was copied, searched by meaning.
    let found = match crate::clip::ask(q) {
        Some(rest) if rest.chars().count() >= 3 => e.sem.query_clips(rest, Duration::from_secs(6)),
        Some(_) => Vec::new(),
        None => e.sem.query(q, Duration::from_secs(6)),
    };
    if found.is_empty() {
        return empty();
    }

    let pinned = pinned_keys(e);
    let mut last = e.last.lock().unwrap();
    if last.0 != gen {
        return empty();
    }
    let mut fresh: Vec<(Hit, f32)> = Vec::new();
    for (hit, score) in found {
        if fresh.len() >= semantic::MAX_RELATED {
            break;
        }
        let key = hit.key();
        if last.1.iter().any(|h| h.key() == key) {
            continue; // already shown as an instant match
        }
        fresh.push((hit, score));
    }
    if fresh.is_empty() {
        return empty();
    }

    // When the top instant hit does not actually contain what was typed, fuzzy matching has
    // only produced scattered-letter noise (typical for natural-language queries). If the
    // meaning matches are strong, they lead the list and become the default selection;
    // otherwise they are appended below the instant results.
    // Word by word: "volume 3" is real in "Set volume to 3%", though not as one run of letters.
    let norm = |s: &str| -> String { s.chars().filter(|c| c.is_alphanumeric()).flat_map(char::to_lowercase).collect() };
    let typed: Vec<String> = q.split_whitespace().map(&norm).filter(|w| !w.is_empty()).collect();
    let instant_is_real = last.1.first().is_some_and(|h| match h.kind {
        // A calculator result or a quick setting answers the query as typed.
        crate::model::Kind::Calc | crate::model::Kind::Toggle | crate::model::Kind::Plan | crate::model::Kind::Clip => true,
        crate::model::Kind::Web => false, // the fallback row says nothing about relevance
        _ => {
            let t = norm(&h.title);
            typed.iter().all(|w| t.contains(w.as_str()))
        }
    });
    let lead_min = semantic::thresholds().lead;
    let lead = !instant_is_real && fresh[0].1 >= lead_min;
    if lead {
        fresh.retain(|(_, s)| *s >= lead_min);
    }
    let n = fresh.len();
    let new_hits: Vec<Hit> = fresh.into_iter().map(|(h, _)| h).collect();

    if !lead {
        let base = last.1.len();
        last.1.extend(new_hits);
        let json: Vec<HitJson> = last.1[base..].iter().map(|h| hit_json(h, true, &pinned)).collect();
        let out = serde_json::to_string(&Appended { gen, base, replace: false, hits: json })
            .unwrap_or_else(|_| format!("{{\"gen\":{gen},\"base\":0,\"hits\":[]}}"));
        return into_c(out);
    }

    // Lead mode rebuilds the whole list: related rows first, then only the instant hits that
    // really contain a typed word (files matched by scattered letters are dropped), then the
    // web fallback, which fuzzy junk had pushed out of the list.
    let words: Vec<String> = q.split_whitespace().map(&norm).filter(|w| w.chars().count() >= 3).collect();
    let keep = |h: &Hit| match h.kind {
        crate::model::Kind::File | crate::model::Kind::Folder => {
            let t = norm(&h.title);
            words.iter().any(|w| t.contains(w))
        }
        _ => true,
    };
    let mut rebuilt: Vec<Hit> = new_hits;
    rebuilt.extend(last.1.iter().filter(|h| keep(h)).cloned());
    if !rebuilt.iter().any(|h| h.kind == crate::model::Kind::Web) {
        rebuilt.push(crate::search::web_hit(q));
    }
    rebuilt.truncate(crate::search::MAX_HITS + semantic::MAX_RELATED);
    last.1 = rebuilt;

    let json: Vec<HitJson> = last.1.iter().enumerate().map(|(i, h)| hit_json(h, i < n, &pinned)).collect();
    let out = serde_json::to_string(&Appended { gen, base: 0, replace: true, hits: json })
        .unwrap_or_else(|_| format!("{{\"gen\":{gen},\"base\":0,\"hits\":[]}}"));
    into_c(out)
}

/// A plan from the language model for a request the rules did not read, put first in the hits
/// of search generation `gen`. About 60 ms (a second or two if its process must start), so call
/// it from a worker thread after showing the instant results. Returns the whole new list as
/// `{"gen":N,"base":0,"replace":true,"hits":[...]}`, or empty `hits` when there is no plan or
/// `gen` is stale. Free with `pc_free`.
///
/// # Safety
/// `query` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_plan(gen: u64, query: *const c_char) -> *mut c_char {
    let empty = || into_c(format!("{{\"gen\":{gen},\"base\":0,\"hits\":[]}}"));
    let Some(e) = ENGINE.get() else { return empty() };
    let q = if query.is_null() { "" } else { CStr::from_ptr(query).to_str().unwrap_or("") }.trim();
    if !crate::planner::available() || !crate::planner::worth_asking(q) {
        return empty();
    }
    // Already answered as typed: a plan from the rules, a sum, a quick setting asked for.
    let answered = |hits: &[Hit]| {
        hits.first().is_some_and(|h| match h.kind {
            crate::model::Kind::Plan | crate::model::Kind::Calc => true,
            crate::model::Kind::Toggle => h.score >= 900,
            _ => false,
        })
    };
    {
        let last = e.last.lock().unwrap();
        if last.0 != gen || answered(&last.1) {
            return empty();
        }
    }
    let Some(line) = crate::planner::get().line(q) else { return empty() };
    let Some(hit) = ({
        let cat = e.catalog.read().unwrap();
        crate::planner::hit(&line, &cat.items)
    }) else {
        return empty();
    };

    let pinned = pinned_keys(e);
    let mut last = e.last.lock().unwrap();
    if last.0 != gen || answered(&last.1) {
        return empty();
    }
    *e.model_plan.lock().unwrap() = Some((gen, q.to_string(), line, hit.title.clone()));
    last.1.insert(0, hit);
    let json: Vec<HitJson> = last.1.iter().map(|h| hit_json(h, false, &pinned)).collect();
    let out = serde_json::to_string(&Appended { gen, base: 0, replace: true, hits: json })
        .unwrap_or_else(|_| format!("{{\"gen\":{gen},\"base\":0,\"hits\":[]}}"));
    into_c(out)
}

/// The passage of document `path` that `query` matched, as `{"text","marks":[[start,len],...]}`
/// (marks in UTF-16 units), or null. Slow for a PDF the helper has not read lately: call from a
/// worker thread. Free with `pc_free`.
///
/// # Safety
/// `path` and `query` must be valid NUL-terminated strings.
#[no_mangle]
pub unsafe extern "C" fn pc_passage(path: *const c_char, query: *const c_char) -> *mut c_char {
    let Some(e) = ENGINE.get() else { return std::ptr::null_mut() };
    if path.is_null() || query.is_null() {
        return std::ptr::null_mut();
    }
    let (Ok(path), Ok(query)) = (CStr::from_ptr(path).to_str(), CStr::from_ptr(query).to_str()) else {
        return std::ptr::null_mut();
    };
    match e.sem.passage(path, query.trim(), Duration::from_secs(20)) {
        Some(p) => into_c(serde_json::to_string(&p).unwrap_or_default()),
        None => std::ptr::null_mut(),
    }
}

/// Footer text describing the semantic index ("Indexing documents 120 / 2000", ...).
/// Empty if the tier is unavailable. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_semantic_status() -> *mut c_char {
    let text = ENGINE.get().map(|e| e.sem.status_text()).unwrap_or_default();
    into_c(text)
}

/// Start reading the window `hwnd`, the one that had the focus when the palette opened (0: none):
/// the text selected in it, for Home, and its commands, for typed queries (uia.rs). Returns at once.
#[no_mangle]
pub extern "C" fn pc_read_window(hwnd: isize) {
    crate::uia::begin(hwnd);
}

/// The text selected in that window on one line, waiting up to `wait_ms` for the read to finish,
/// or null. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_selection(wait_ms: i32) -> *mut c_char {
    match crate::uia::selection(Duration::from_millis(wait_ms.max(0) as u64)) {
        Some(t) => into_c(t),
        None => std::ptr::null_mut(),
    }
}

/// Hit `index` of generation `gen`, if that generation is still the latest.
fn hit_at(e: &Engine, gen: u64, index: i32) -> Option<Hit> {
    let l = e.last.lock().unwrap();
    if l.0 == gen { l.1.get(index.max(0) as usize).cloned() } else { None }
}

/// Launch hit `index` of search generation `gen` (ignored if stale). `reveal != 0` shows a file in
/// Explorer instead. Returns 1 if the shell should hide the palette, 2 if it should quit, 4 if it
/// should show the full app list (`pc_all_apps`).
#[no_mangle]
pub extern "C" fn pc_launch(gen: u64, index: i32, reveal: i32) -> i32 {
    let Some(e) = ENGINE.get() else { return 0 };
    let hit: Option<Hit> = {
        let l = e.last.lock().unwrap();
        if l.0 == gen { l.1.get(index.max(0) as usize).cloned() } else { None }
    };
    let Some(hit) = hit else { return 0 };
    if matches!(&hit.action, Action::Builtin(id) if id == home::ALL_APPS) {
        return 4;
    }
    // Selected text: the shell puts it in the search field (`pc_selection`). Never remembered.
    if matches!(&hit.action, Action::Builtin(id) if id == home::SELECTION) {
        return 5;
    }
    if reveal != 0 {
        if let Some(p) = hit.reveal_path() {
            actions::reveal(&p);
            return 1;
        }
    }
    // Windows come and go: remembering one would leave a dead row in the most-used list.
    // Quick settings say what they will do in the current state: remembering one would offer
    // "Turn Wi-Fi off" in the home view while it is already off.
    // Requests say what they will do with what was typed: remembering one would offer it again
    // on the home view, out of the blue.
    // A plan the model wrote, run: the same request gets it at once next time.
    if hit.kind == crate::model::Kind::Plan {
        if let Some((g, q, line, title)) = e.model_plan.lock().unwrap().clone() {
            if g == gen && title == hit.title {
                crate::planner::get().remember(&q, &line);
            }
        }
    }
    // A command of a window exists while that window does: what you typed to find it is learned,
    // but Home never lists it.
    // What was copied is never written down.
    if !matches!(
        hit.kind,
        crate::model::Kind::Window | crate::model::Kind::Toggle | crate::model::Kind::Plan | crate::model::Kind::Clip
    ) {
        if let Ok(mut u) = e.usage.lock() {
            let query = match &*e.last_query.lock().unwrap() {
                (g, q) if *g == gen => q.clone(),
                _ => String::new(),
            };
            if hit.kind == crate::model::Kind::Control {
                u.record_pick(&hit, &query);
            } else {
                u.record_for(&hit, &query);
            }
        }
    }
    // Clipboard is the shell's job (it reads `copy_text` from the JSON).
    let hide = actions::execute(&hit, &|_| {});
    if actions::QUIT_REQUESTED.load(Ordering::SeqCst) {
        return 2;
    }
    hide as i32
}

/// Every app, A to Z, as a new generation: `{"gen":N,"hits":[...]}`. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_all_apps() -> *mut c_char {
    let Some(e) = ENGINE.get() else { return into_c("{\"gen\":0,\"hits\":[]}".into()) };
    let mut apps: Vec<Hit> = e
        .catalog
        .read()
        .unwrap()
        .items
        .iter()
        .filter(|i| i.hit.kind == crate::model::Kind::App)
        .map(|i| i.hit.clone())
        .collect();
    apps.sort_by_cached_key(|h| h.title.to_lowercase());
    apps.dedup_by(|a, b| a.title.eq_ignore_ascii_case(&b.title));
    let pinned = pinned_keys(e);
    let gen = e.gen.fetch_add(1, Ordering::SeqCst) + 1;
    let json: Vec<HitJson> = apps.iter().map(|h| hit_json(h, false, &pinned)).collect();
    let out = serde_json::to_string(&Results { gen, hits: json }).unwrap_or_else(|_| "{\"gen\":0,\"hits\":[]}".into());
    *e.last.lock().unwrap() = (gen, apps);
    into_c(out)
}

/// The actions for hit `index` of generation `gen`, as a JSON array of
/// `{"id","title","hint","copy"}`, the default action first. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_actions(gen: u64, index: i32) -> *mut c_char {
    let Some(e) = ENGINE.get() else { return into_c("[]".into()) };
    let Some(hit) = hit_at(e, gen, index) else { return into_c("[]".into()) };
    let pin = {
        let p = e.pins.lock().unwrap();
        p.position(&hit.key()).map(|i| (i, p.list().len()))
    };
    into_c(serde_json::to_string(&home::actions_for(&hit, pin)).unwrap_or_else(|_| "[]".into()))
}

/// Run action `id` (from `pc_actions`) on hit `index` of generation `gen`. Returns -1 if it does
/// not apply, 1 to hide the palette, 3 to show the current list again (pins changed).
///
/// # Safety
/// `id` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_act(gen: u64, index: i32, id: *const c_char) -> i32 {
    let Some(e) = ENGINE.get() else { return -1 };
    let id = if id.is_null() { "" } else { CStr::from_ptr(id).to_str().unwrap_or("") };
    let Some(hit) = hit_at(e, gen, index) else { return -1 };
    let mut pins = e.pins.lock().unwrap();
    let mut usage = e.usage.lock().unwrap();
    match home::act(&hit, id, &mut pins, &mut usage) {
        home::After::NotApplicable => -1,
        home::After::Hide => 1,
        home::After::Refresh => 3,
        home::After::Done => 5,
    }
}

/// Rename the file or folder of hit `index` of generation `gen`. Returns 5 (`pc_file_outcome` says
/// how it went) or -1 when the row is not a file or folder.
///
/// # Safety
/// `name` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_rename(gen: u64, index: i32, name: *const c_char) -> i32 {
    let Some(e) = ENGINE.get() else { return -1 };
    let name = if name.is_null() { "" } else { CStr::from_ptr(name).to_str().unwrap_or("") };
    let Some(path) = hit_at(e, gen, index).and_then(|h| home::file_of(&h)) else { return -1 };
    crate::fileops::rename(&path, name);
    5
}

/// Folders hit `index` could be moved to, as a JSON array of `{"id":"moveto:<path>","title",...}`
/// action rows (pinned and often used folders after the usual ones). Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_destinations(gen: u64, index: i32) -> *mut c_char {
    #[derive(Serialize)]
    struct Dest {
        id: String,
        title: String,
        hint: &'static str,
        copy: Option<String>,
    }
    let none = || into_c("[]".into());
    let Some(e) = ENGINE.get() else { return none() };
    let Some(path) = hit_at(e, gen, index).and_then(|h| home::file_of(&h)) else { return none() };
    let mut favourites: Vec<std::path::PathBuf> = Vec::new();
    let mut folders = |hits: Vec<Hit>| {
        for h in hits {
            if let (crate::model::Kind::Folder, Action::Open(p)) = (h.kind, &h.action) {
                favourites.push(p.into());
            }
        }
    };
    folders(e.pins.lock().unwrap().list().to_vec());
    folders(e.usage.lock().unwrap().top(40));
    let rows: Vec<Dest> = crate::fileops::destinations(&path, &favourites)
        .into_iter()
        .map(|(p, title)| Dest { id: format!("moveto:{}", p.display()), title, hint: "", copy: None })
        .collect();
    into_c(serde_json::to_string(&rows).unwrap_or_else(|_| "[]".into()))
}

/// What the last file operation did: `{"message":"Created a.zip","undo":true}`. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_file_outcome() -> *mut c_char {
    into_c(serde_json::to_string(&crate::fileops::outcome()).unwrap_or_else(|_| "{}".into()))
}

/// Reverse the last file operation that can be; `pc_file_outcome` then says how it went.
#[no_mangle]
pub extern "C" fn pc_file_undo() {
    crate::fileops::undo();
}

/// Write the icon of hit `index` into `buf` as premultiplied BGRA
/// (`pc_icon_size()^2 * 4` bytes). Returns 1 on success.
///
/// # Safety
/// `buf` must point to at least `pc_icon_size()^2 * 4` writable bytes.
#[no_mangle]
pub unsafe extern "C" fn pc_icon(gen: u64, index: i32, buf: *mut u8) -> i32 {
    let Some(e) = ENGINE.get() else { return 0 };
    let hit: Option<Hit> = {
        let l = e.last.lock().unwrap();
        if l.0 == gen { l.1.get(index.max(0) as usize).cloned() } else { None }
    };
    let Some(hit) = hit else { return 0 };
    match iconsrc::icon_bgra(&hit) {
        Some(px) if !buf.is_null() => {
            std::ptr::copy_nonoverlapping(px.as_ptr(), buf, px.len());
            1
        }
        _ => 0,
    }
}

/// Write the icon for `key` (a hit's `icon_key`) into `buf` like `pc_icon`, from any generation.
///
/// # Safety
/// `key` must be a NUL-terminated UTF-8 string and `buf` as for `pc_icon`.
#[no_mangle]
pub unsafe extern "C" fn pc_icon_key(key: *const c_char, buf: *mut u8) -> i32 {
    if key.is_null() || buf.is_null() {
        return 0;
    }
    let Ok(key) = CStr::from_ptr(key).to_str() else { return 0 };
    match iconsrc::icon_bgra_for_key(key) {
        Some(px) => {
            std::ptr::copy_nonoverlapping(px.as_ptr(), buf, px.len());
            1
        }
        None => 0,
    }
}

/// The distinct icon keys of every app, setting and folder in the catalog, as a JSON array of
/// strings, for warming an icon cache. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_icon_keys() -> *mut c_char {
    let Some(e) = ENGINE.get() else { return into_c("[]".into()) };
    let mut seen = std::collections::HashSet::new();
    let keys: Vec<String> = e
        .catalog
        .read()
        .unwrap()
        .items
        .iter()
        .filter(|i| matches!(i.hit.kind, crate::model::Kind::App | crate::model::Kind::Setting | crate::model::Kind::Folder))
        .filter_map(|i| iconsrc::icon_key(&i.hit))
        .filter(|k| seen.insert(k.clone()))
        .collect();
    into_c(serde_json::to_string(&keys).unwrap_or_else(|_| "[]".into()))
}

/// 1 if Palette is registered to start at sign-in.
#[no_mangle]
pub extern "C" fn pc_autostart_get() -> i32 {
    actions::autostart_enabled() as i32
}

/// Register (`enable != 0`) or remove the sign-in autostart entry. Returns 1 on success.
#[no_mangle]
pub extern "C" fn pc_autostart_set(enable: i32) -> i32 {
    actions::set_autostart(enable != 0) as i32
}

/// Refresh the file index now (same as the "Palette: Reindex files" command).
#[no_mangle]
pub extern "C" fn pc_reindex() {
    std::thread::spawn(|| scan::request_mft_snapshot(true));
}

#[no_mangle]
pub extern "C" fn pc_icon_size() -> i32 {
    iconsrc::ICON_SIZE
}

/// # Safety
/// `p` must come from `pc_search` and not be freed twice.
#[no_mangle]
pub unsafe extern "C" fn pc_free(p: *mut c_void) {
    if !p.is_null() {
        drop(CString::from_raw(p as *mut c_char));
    }
}

/// Tell the engine something was copied (the shell reads Windows' clipboard history and what is
/// copied while it runs), at unix time `at`. Kept in memory only (see clip.rs).
///
/// # Safety
/// `text` must be a valid NUL-terminated string.
#[no_mangle]
pub unsafe extern "C" fn pc_clip_add(text: *const c_char, at: i64) {
    if !text.is_null() {
        crate::clip::add(CStr::from_ptr(text).to_str().unwrap_or(""), at);
    }
}

/// Forget everything copied (the shell's "remember what is copied" was turned off).
#[no_mangle]
pub extern "C" fn pc_clip_clear() {
    crate::clip::clear();
}
