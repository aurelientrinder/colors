use crate::index::Catalog;
use crate::Notify;
use crate::model::{Action, Hit, Kind};
use crate::usage::Usage;
use nucleo_matcher::pattern::{CaseMatching, Normalization, Pattern};
use nucleo_matcher::{Config, Matcher, Utf32Str};
use rayon::prelude::*;
use std::sync::mpsc::{Receiver, Sender};
use std::sync::{Arc, Mutex, RwLock};

pub struct Request {
    pub gen: u64,
    pub query: String,
}

pub struct Response {
    pub gen: u64,
    pub hits: Vec<Hit>,
}

const CHUNK: usize = 16 * 1024;
const TOP_FILES: usize = 40;
pub const MAX_HITS: usize = 8;

/// Search worker: owns the narrowing cache, runs off the UI thread, always
/// skips straight to the newest request when keystrokes pile up.
pub fn spawn(
    catalog: Arc<RwLock<Catalog>>,
    usage: Arc<Mutex<Usage>>,
    rx: Receiver<Request>,
    tx: Sender<Response>,
    notify: Notify,
) {
    std::thread::spawn(move || {
        let mut cache = Narrow::default();
        let log = std::env::var_os("PALETTE_LOG").is_some();
        while let Ok(mut req) = rx.recv() {
            while let Ok(newer) = rx.try_recv() {
                req = newer;
            }
            let t0 = std::time::Instant::now();
            let hits = run(&req.query, &catalog, &usage, &mut cache);
            if log {
                use std::io::Write;
                if let Ok(mut f) = std::fs::OpenOptions::new()
                    .create(true)
                    .append(true)
                    .open(crate::paths::data_dir().join("search.log"))
                {
                    let _ = writeln!(f, "{:?}	{:.2} ms	{} hits", req.query, t0.elapsed().as_secs_f64() * 1e3, hits.len());
                }
            }
            if tx.send(Response { gen: req.gen, hits }).is_err() {
                break;
            }
            notify();
        }
    });
}

/// Synchronous searcher for callers that manage their own threading (FFI).
pub struct Searcher {
    catalog: Arc<RwLock<Catalog>>,
    usage: Arc<Mutex<Usage>>,
    cache: Mutex<Narrow>,
}

impl Searcher {
    pub fn new(catalog: Arc<RwLock<Catalog>>, usage: Arc<Mutex<Usage>>) -> Self {
        Searcher { catalog, usage, cache: Mutex::new(Narrow::default()) }
    }

    pub fn search(&self, query: &str) -> Vec<Hit> {
        let mut cache = self.cache.lock().unwrap();
        run(query, &self.catalog, &self.usage, &mut cache)
    }
}

/// Matches of "ab" are a subset of matches of "a", so a query that extends the
/// previous one only needs to rescan the previous matches.
#[derive(Default)]
struct Narrow {
    query: String,
    version: u64,
    matches: Vec<u32>,
    valid: bool,
}

fn run(raw: &str, catalog: &RwLock<Catalog>, usage: &Mutex<Usage>, cache: &mut Narrow) -> Vec<Hit> {
    let q = raw.trim();
    if q.is_empty() {
        return usage.lock().map(|u| u.top(MAX_HITS)).unwrap_or_default();
    }

    let mut hits: Vec<Hit> = Vec::new();

    // `> cmd` runs a shell command directly.
    if let Some(cmd) = q.strip_prefix('>') {
        let cmd = cmd.trim();
        if !cmd.is_empty() {
            hits.push(Hit {
                title: format!("Run: {cmd}"),
                subtitle: "Execute in cmd.exe".into(),
                kind: Kind::Command,
                action: Action::Run(cmd.to_string()),
                score: 0,
            });
        }
        return hits;
    }

    if let Some(h) = calculator(q) {
        hits.push(h);
    }

    let cat = catalog.read().unwrap();
    let usage = usage.lock().unwrap();
    let pattern = Pattern::parse(q, CaseMatching::Smart, Normalization::Smart);
    let path_query = q.contains('\\') || q.contains('/');
    let lq = q.to_lowercase();

    // Small items: always a full scan, it's a few hundred entries.
    {
        let mut m = Matcher::new(Config::DEFAULT);
        let mut buf = Vec::new();
        for it in &cat.items {
            if let Some(s) = pattern.score(Utf32Str::new(&it.name, &mut buf), &mut m) {
                let mut h = it.hit.clone();
                let prefix = prefix_bonus(&it.name, &lq);
                h.score = s as i32 + prefix + h.kind.bonus() + usage.bonus(&h.key());
                hits.push(h);
            }
        }
    }

    // Files: parallel scan, narrowed by the previous query when possible.
    let n = cat.files.len();
    let can_narrow = cache.valid
        && cache.version == cat.version
        && !q.contains('!')
        && q.len() > cache.query.len()
        && q.starts_with(&cache.query);

    let scan = |m: &mut Matcher, buf: &mut Vec<char>, i: usize| -> Option<(u32, u32)> {
        let hay = if path_query { cat.files.path(i) } else { cat.files.name(i) };
        pattern.score(Utf32Str::new(hay, buf), m).map(|s| (i as u32, s))
    };
    let cfg = Config::DEFAULT.match_paths();

    let scored: Vec<(u32, u32)> = if can_narrow {
        cache
            .matches
            .par_chunks(CHUNK)
            .map_init(
                || (Matcher::new(cfg.clone()), Vec::new()),
                |(m, buf), chunk| {
                    chunk.iter().filter_map(|&i| scan(m, buf, i as usize)).collect::<Vec<_>>()
                },
            )
            .flatten()
            .collect()
    } else {
        (0..n)
            .step_by(CHUNK)
            .collect::<Vec<_>>()
            .into_par_iter()
            .map_init(
                || (Matcher::new(cfg.clone()), Vec::new()),
                |(m, buf), start| {
                    (start..(start + CHUNK).min(n)).filter_map(|i| scan(m, buf, i)).collect::<Vec<_>>()
                },
            )
            .flatten()
            .collect()
    };

    cache.query = q.to_string();
    cache.version = cat.version;
    cache.matches = scored.iter().map(|&(i, _)| i).collect();
    cache.valid = true;

    // Top-K by raw score, then re-rank with bonuses.
    let mut top = scored;
    if top.len() > TOP_FILES {
        top.select_nth_unstable_by(TOP_FILES, |a, b| b.1.cmp(&a.1));
        top.truncate(TOP_FILES);
    }
    // Score first, stat later: only verify existence for candidates that
    // actually make the visible list (the index can be a little stale).
    let mut cands: Vec<Hit> = Vec::with_capacity(top.len());
    for (i, s) in top {
        let i = i as usize;
        let path = cat.files.path(i);
        let dir = cat.files.is_dir(i);
        let name = cat.files.name(i);
        let parent = path.rsplit_once('\\').map(|(p, _)| p).unwrap_or("");
        let kind = if dir { Kind::Folder } else { Kind::File };
        let prefix = prefix_bonus(name, &lq);
        let mut h = Hit {
            title: name.to_string(),
            subtitle: parent.to_string(),
            kind,
            action: Action::Open(path.to_string()),
            score: 0,
        };
        // Shallow paths are usually more relevant than deeply nested ones.
        let depth_penalty = path.matches('\\').count() as i32;
        h.score = s as i32 + prefix + kind.bonus() - depth_penalty + usage.bonus(&h.key());
        cands.push(h);
    }
    cands.sort_by(|a, b| b.score.cmp(&a.score));
    let mut kept = 0usize;
    for h in cands {
        if kept >= MAX_HITS {
            break;
        }
        if let Action::Open(p) = &h.action {
            if std::fs::symlink_metadata(p).is_err() {
                continue;
            }
        }
        kept += 1;
        hits.push(h);
    }

    // Always offer a web search as the last resort.
    hits.push(web_hit(q));

    hits.sort_by(|a, b| b.score.cmp(&a.score));
    hits.truncate(MAX_HITS);
    hits
}

/// The "search the web" fallback row.
pub fn web_hit(q: &str) -> Hit {
    Hit {
        title: format!("Search the web for \u{201c}{q}\u{201d}"),
        subtitle: "Open in your default browser".into(),
        kind: Kind::Web,
        action: Action::Open(format!("https://www.google.com/search?q={}", urlencoding::encode(q))),
        score: i32::MIN / 2,
    }
}

/// 140 if the whole name starts with the query, 100 if any word does.
fn prefix_bonus(name: &str, lq: &str) -> i32 {
    let l = name.to_lowercase();
    if l.starts_with(lq) {
        return 140;
    }
    let mut prev_alnum = false;
    for (i, c) in l.char_indices() {
        let alnum = c.is_alphanumeric();
        if alnum && !prev_alnum && l[i..].starts_with(lq) {
            return 100;
        }
        prev_alnum = alnum;
    }
    0
}

fn calculator(q: &str) -> Option<Hit> {
    let has_digit = q.chars().any(|c| c.is_ascii_digit());
    let has_op = q.chars().any(|c| "+-*/^%()".contains(c));
    if !has_digit || !has_op {
        return None;
    }
    let v = meval::eval_str(q).ok()?;
    if !v.is_finite() {
        return None;
    }
    let text = if v.fract() == 0.0 && v.abs() < 1e15 {
        format!("{}", v as i64)
    } else {
        format!("{v}")
    };
    Some(Hit {
        title: text.clone(),
        subtitle: format!("{q} = {text}  \u{b7}  Enter to copy"),
        kind: Kind::Calc,
        action: Action::Copy(text),
        score: 10_000,
    })
}
