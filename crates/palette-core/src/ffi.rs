//! C ABI for non-Rust shells. All strings are UTF-8, NUL-terminated.
//! Hits are returned as a JSON array and remembered so `pc_launch` /
//! `pc_icon` can refer to them by index.

use crate::actions;
use crate::index::Catalog;
use crate::model::{Action, Hit};
use crate::search::Searcher;
use crate::usage::Usage;
use crate::{iconsrc, scan, semantic};
use serde::Serialize;
use std::ffi::{c_char, c_void, CStr, CString};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::{Arc, Mutex, OnceLock, RwLock};
use std::time::Duration;

struct Engine {
    searcher: Searcher,
    usage: Arc<Mutex<Usage>>,
    sem: Arc<semantic::Client>,
    /// Last generation that already received its related rows (appending twice would duplicate them).
    sem_gen: AtomicU64,
    /// (generation, hits) of the most recent search.
    last: Mutex<(u64, Vec<Hit>)>,
    gen: AtomicU64,
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
}

fn hit_json(h: &Hit, related: bool) -> HitJson<'_> {
    HitJson {
        title: &h.title,
        subtitle: &h.subtitle,
        kind: h.kind.label(),
        icon_key: iconsrc::icon_key(h),
        copy_text: match &h.action {
            Action::Copy(t) => Some(t.as_str()),
            _ => None,
        },
        related,
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

        // Start its process a little after launch so the model load and the first indexing
        // pass never compete with the app's own startup.
        if semantic::available() {
            let s = sem.clone();
            std::thread::spawn(move || {
                std::thread::sleep(Duration::from_secs(20));
                s.ensure_started();
            });
        }

        Engine {
            searcher: Searcher::new(catalog, usage.clone()),
            usage,
            sem,
            sem_gen: AtomicU64::new(0),
            last: Mutex::new((0, Vec::new())),
            gen: AtomicU64::new(0),
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
    let hits = e.searcher.search(q);
    let json: Vec<HitJson> = hits.iter().map(|h| hit_json(h, false)).collect();
    let gen = e.gen.fetch_add(1, Ordering::SeqCst) + 1;
    let out = serde_json::to_string(&Results { gen, hits: json })
        .unwrap_or_else(|_| "{\"gen\":0,\"hits\":[]}".into());
    *e.last.lock().unwrap() = (gen, hits);
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

    let found = e.sem.query(q, Duration::from_secs(6));
    if found.is_empty() {
        return empty();
    }

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
    let norm = |s: &str| -> String { s.chars().filter(|c| c.is_alphanumeric()).flat_map(char::to_lowercase).collect() };
    let typed = norm(q);
    let instant_is_real = last.1.first().is_some_and(|h| match h.kind {
        crate::model::Kind::Calc => true, // a calculator result is the answer
        crate::model::Kind::Web => false, // the fallback row says nothing about relevance
        _ => norm(&h.title).contains(&typed),
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
        let json: Vec<HitJson> = last.1[base..].iter().map(|h| hit_json(h, true)).collect();
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

    let json: Vec<HitJson> = last.1.iter().enumerate().map(|(i, h)| hit_json(h, i < n)).collect();
    let out = serde_json::to_string(&Appended { gen, base: 0, replace: true, hits: json })
        .unwrap_or_else(|_| format!("{{\"gen\":{gen},\"base\":0,\"hits\":[]}}"));
    into_c(out)
}

/// Footer text describing the semantic index ("Indexing documents 120 / 2000", ...).
/// Empty if the tier is unavailable. Free with `pc_free`.
#[no_mangle]
pub extern "C" fn pc_semantic_status() -> *mut c_char {
    let text = ENGINE.get().map(|e| e.sem.status_text()).unwrap_or_default();
    into_c(text)
}

/// Launch hit `index` of search generation `gen` (ignored if stale). `reveal != 0` shows a file in
/// Explorer instead. Returns 1 if the shell should hide the palette, 2 if it should quit.
#[no_mangle]
pub extern "C" fn pc_launch(gen: u64, index: i32, reveal: i32) -> i32 {
    let Some(e) = ENGINE.get() else { return 0 };
    let hit: Option<Hit> = {
        let l = e.last.lock().unwrap();
        if l.0 == gen { l.1.get(index.max(0) as usize).cloned() } else { None }
    };
    let Some(hit) = hit else { return 0 };
    if reveal != 0 {
        if let Some(p) = hit.reveal_path() {
            actions::reveal(&p);
            return 1;
        }
    }
    if let Ok(mut u) = e.usage.lock() {
        u.record(&hit);
    }
    // Clipboard is the shell's job (it reads `copy_text` from the JSON).
    let hide = actions::execute(&hit, &|_| {});
    if actions::QUIT_REQUESTED.load(Ordering::SeqCst) {
        return 2;
    }
    hide as i32
}

/// Write the icon of hit `index` into `buf` as premultiplied RGBA
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
    match iconsrc::icon_rgba(&hit) {
        Some(px) if !buf.is_null() => {
            std::ptr::copy_nonoverlapping(px.as_ptr(), buf, px.len());
            1
        }
        _ => 0,
    }
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
