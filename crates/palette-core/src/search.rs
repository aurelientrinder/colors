use crate::index::Catalog;
use crate::Notify;
use crate::model::{Action, Hit, Item, Kind, Words};
use crate::usage::Usage;
use nucleo_matcher::pattern::{AtomKind, CaseMatching, Normalization, Pattern};
use nucleo_matcher::{Config, Matcher, Utf32Str};
use rayon::prelude::*;
use std::collections::{HashMap, HashSet};
use std::sync::mpsc::{Receiver, Sender};
use std::sync::{Arc, Mutex, RwLock};
use std::time::{Duration, Instant};

pub struct Request {
    pub gen: u64,
    pub query: String,
}

pub struct Response {
    pub gen: u64,
    pub hits: Vec<Hit>,
}

/// Paths per parallel task: small enough that the efficient cores, which finish later, do not
/// leave the others waiting at the end of a scan.
const CHUNK: usize = 4 * 1024;
/// File matches ranked on their path as well (location, depth, recent use): enough that a
/// hundred equally good "Downloads" folders cannot crowd out yours.
const FILE_POOL: usize = 3000;
pub const MAX_HITS: usize = 5;

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
            let hits = run(&req.query, &catalog, &usage, &mut cache, MAX_HITS);
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
        run(query, &self.catalog, &self.usage, &mut cache, MAX_HITS)
    }

    /// The best `n` rows instead of the visible few (diagnostics).
    pub fn search_all(&self, query: &str, n: usize) -> Vec<Hit> {
        let mut cache = self.cache.lock().unwrap();
        run(query, &self.catalog, &self.usage, &mut cache, n)
    }
}

/// Matches of "ab" are a subset of matches of "a", so a query that extends the
/// previous one only needs to rescan the previous matches.
#[derive(Default)]
struct Narrow {
    query: String,
    version: u64,
    /// Whether `matches` were matched on whole paths rather than names.
    path_query: bool,
    matches: Vec<u32>,
    valid: bool,
}

fn run(raw: &str, catalog: &RwLock<Catalog>, usage: &Mutex<Usage>, cache: &mut Narrow, max: usize) -> Vec<Hit> {
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
    hits.extend(crate::quick::hits(q));

    let cat = catalog.read().unwrap();
    let usage = usage.lock().unwrap();
    let pattern = Pattern::parse(q, CaseMatching::Smart, Normalization::Smart);
    let path_query = q.contains('\\') || q.contains('/');
    let lq = q.to_lowercase();

    let words: Vec<&str> = lq.split_whitespace().collect();

    // "open notepad and weather side by side", "show desktop": one row that does it.
    hits.extend(crate::intent::hits(q, &cat.items));
    // A request the model wrote a plan for, and that plan was run: offer it again at once.
    if !hits.iter().any(|h| h.kind == Kind::Plan) {
        if let Some(h) = crate::planner::get().remembered(q).and_then(|l| crate::planner::hit(&l, &cat.items)) {
            hits.push(h);
        }
    }

    // Apps, commands and settings: a few thousand entries, always a full scan. Scored first, and
    // only the few that can make the list are copied out.
    {
        let mut m = Matcher::new(Config::DEFAULT);
        let mut buf = Vec::new();
        let mut found: Vec<(i32, &Item)> = Vec::new();
        for it in &cat.items {
            let scored = match &it.keywords {
                // Every word already begins a word: always a good match.
                Some(_) => word_prefix_score(&it.words, &words).map(|s| (s, GOOD)),
                None => pattern.score(Utf32Str::new(&it.name, &mut buf), &mut m).map(|s| {
                    let f = fit(&it.words.name, &lq, &words);
                    (s as i32 + f, f)
                }),
            };
            if let Some((base, f)) = scored {
                let mut score = base + kind_bonus(it.hit.kind, f) + coverage(&it.name, &lq) + usage.bonus(&it.hit.key());
                if it.hit.kind == Kind::App && secondary(&it.words.name, &lq) {
                    score -= 60;
                }
                found.push((score, it));
            }
        }
        found.sort_by(|a, b| b.0.cmp(&a.0));
        // One row per app (see the end): a second "Task Manager" never makes the list.
        let mut titles = HashSet::new();
        let mut kept = 0;
        for (score, it) in found {
            if kept >= max {
                break;
            }
            if matches!(it.hit.kind, Kind::App | Kind::Command) && !titles.insert(it.hit.title.to_lowercase()) {
                continue;
            }
            kept += 1;
            let mut h = it.hit.clone();
            h.score = score;
            hits.push(h);
        }
    }

    // Open windows, matched on their title and their app's name ("chrome" finds every Chrome
    // window). Listed fresh each time: windows open, close and retitle themselves constantly.
    // Every typed word must begin a word of either: a long page title contains the scattered
    // letters of nearly any query, and a window outranks almost everything else.
    {
        let mut m = Matcher::new(Config::DEFAULT);
        let mut buf = Vec::new();
        for w in crate::wins::list() {
            let hay = if w.app.is_empty() { w.title.clone() } else { format!("{} {}", w.title, w.app) };
            // One or two letters begin some word of nearly every page title: then the title or
            // the app's name has to begin with them.
            if !words_begin(&hay, &words) || (lq.len() < 3 && !starts_ci(&w.title, &lq) && prefix_bonus(&w.app, &lq) == 0) {
                continue;
            }
            if let Some(s) = pattern.score(Utf32Str::new(&hay, &mut buf), &mut m) {
                let prefix = prefix_bonus(&w.title, &lq).max(prefix_bonus(&w.app, &lq));
                // Found by its title, the window is what you mean. Found only by its app's name
                // ("chrome", which most titles end with), it comes after the app itself, which
                // leads as in Start.
                let own = strip_app(&w.title, &w.app);
                let bonus = if words_begin(own, &words) { Kind::Window.bonus() } else { BY_APP_NAME };
                hits.push(Hit {
                    subtitle: if w.app.is_empty() { "Open window".into() } else { format!("Open window \u{b7} {}", w.app) },
                    title: w.title,
                    kind: Kind::Window,
                    action: Action::Focus { hwnd: w.hwnd, icon: w.icon },
                    score: s as i32 + prefix + bonus,
                });
            }
        }
    }

    // Files: parallel scan, narrowed by the previous query when possible.
    let n = cat.files.len();
    // Not after a query ending in `$` (an exact ending: "foo$x" is no narrower than "foo$"), nor
    // across a switch between matching names and whole paths. The same query again ("foo" then
    // "foo ", trimmed) rescans just its own matches.
    let can_narrow = cache.valid
        && cache.version == cat.version
        && cache.path_query == path_query
        && !q.contains('!')
        && !cache.query.ends_with('$')
        && q.starts_with(&cache.query);

    // A path being typed ("c:\users\") matches nearly every path, and the best alignment of a
    // query in a path takes time in proportion to both lengths: tens of ms a keystroke. The greedy
    // one finds exactly the same matches in linear time; its scores are a little rougher, which
    // the ranking on location below evens out. (ASCII only: then each atom ignores case exactly
    // when its needle has no capitals, and is normalized.)
    let greedy = path_query && q.is_ascii() && pattern.atoms.iter().all(|a| a.kind == AtomKind::Fuzzy && !a.negative);
    let scan = |m: &mut Matcher, buf: &mut Vec<char>, i: usize| -> Option<(u32, u32)> {
        if !greedy {
            let hay = if path_query { cat.files.path(i) } else { cat.files.name(i) };
            return pattern.score(Utf32Str::new(hay, buf), m).map(|s| (i as u32, s));
        }
        let hay = Utf32Str::new(cat.files.path(i), buf);
        let mut score = 0;
        for a in &pattern.atoms {
            let needle = a.needle_text();
            m.config.ignore_case = !needle.chars().any(|c| c.is_ascii_uppercase());
            m.config.normalize = true;
            score += m.fuzzy_match_greedy(hay, needle)? as u32;
        }
        Some((i as u32, score))
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
    cache.path_query = path_query;
    cache.matches = scored.iter().map(|&(i, _)| i).collect();
    cache.valid = true;

    // The best matches by name, then ranked on their path too.
    let mut top = scored;
    if top.len() > FILE_POOL {
        top.select_nth_unstable_by(FILE_POOL, |a, b| b.1.cmp(&a.1));
        top.truncate(FILE_POOL);
    }
    // What you opened lately (Windows' Recent folder, newest first) counts for a lot.
    let recent = crate::home::recent_cached();
    let usage: &Usage = &usage;
    // Ranked without building a row: only the few shown need one. Paths picked by the greedy
    // scores are scored again properly: a few thousand cost a millisecond.
    let mut cands: Vec<(i32, Cand)> = top
        .par_iter()
        .map_init(
            || (Scratch::default(), Matcher::new(cfg.clone()), Vec::new()),
            |(sc, m, buf), &(i, raw)| {
                let path = cat.files.path(i as usize);
                let raw = if greedy { pattern.score(Utf32Str::new(path, buf), m).unwrap_or(raw) } else { raw };
                let score = file_score(path, cat.files.is_dir(i as usize), raw, &lq, usage, &recent.rank, sc);
                (score, Cand::Indexed(i))
            },
        )
        .collect();
    // Recent files the index has not caught up with yet.
    {
        let mut m = Matcher::new(cfg.clone());
        let mut buf = Vec::new();
        let mut sc = Scratch::default();
        for (j, h) in recent.hits.iter().enumerate() {
            let Action::Open(p) = &h.action else { continue };
            let hay = if path_query { p.as_str() } else { h.title.as_str() };
            if let Some(s) = pattern.score(Utf32Str::new(hay, &mut buf), &mut m) {
                cands.push((file_score(p, h.kind == Kind::Folder, s, &lq, usage, &recent.rank, &mut sc), Cand::Recent(j)));
            }
        }
    }
    cands.sort_by(|a, b| b.0.cmp(&a.0));
    // Score first, stat later: only verify existence for candidates that actually make the
    // visible list (the index can be a little stale).
    let mut kept = 0usize;
    let mut seen = HashSet::new();
    for (score, c) in cands {
        if kept >= max {
            break;
        }
        let (path, dir) = match c {
            Cand::Indexed(i) => (cat.files.path(i as usize), cat.files.is_dir(i as usize)),
            Cand::Recent(j) => match &recent.hits[j].action {
                Action::Open(p) => (p.as_str(), recent.hits[j].kind == Kind::Folder),
                _ => continue,
            },
        };
        if !seen.insert(path.to_lowercase()) || !exists(path) {
            continue;
        }
        kept += 1;
        hits.push(file_hit(path, dir, score));
    }

    // Always offer a web search as the last resort.
    hits.push(web_hit(q));

    hits.sort_by(|a, b| b.score.cmp(&a.score));
    // One row per app: "Task Manager" is both a built-in and a Start entry.
    let mut apps = std::collections::HashSet::new();
    hits.retain(|h| !matches!(h.kind, Kind::App | Kind::Command) || apps.insert(h.title.to_lowercase()));
    hits.truncate(max);
    hits
}

/// A file match before it becomes a row: an index entry, or one of the recent files.
#[derive(Clone, Copy)]
enum Cand {
    Indexed(u32),
    Recent(usize),
}

/// Reused buffers for `file_score`.
#[derive(Default)]
struct Scratch {
    lower: String,
    key: String,
}

/// How good a file match is: its name match, the query beginning its name, where it lives, how
/// often and lately it was opened.
fn file_score(path: &str, dir: bool, raw: u32, lq: &str, usage: &Usage, recent: &HashMap<String, i32>, sc: &mut Scratch) -> i32 {
    let lp = &mut sc.lower;
    lp.clear();
    if path.is_ascii() {
        lp.push_str(path);
        lp.make_ascii_lowercase();
    } else {
        lp.push_str(&path.to_lowercase());
    }
    let lname = &lp[lp.rfind('\\').map_or(0, |i| i + 1)..];
    sc.key.clear();
    sc.key.push_str("open:");
    sc.key.push_str(path);
    let kind = if dir { Kind::Folder } else { Kind::File };
    let mut s = raw as i32 + prefix_bonus_lower(lname, lq) + kind.bonus() + path_prior(lp) + usage.bonus(&sc.key);
    if let Some(r) = recent.get(lp.as_str()) {
        s += RECENT - r / 2;
    }
    s
}

fn file_hit(path: &str, dir: bool, score: i32) -> Hit {
    let (parent, name) = path.rsplit_once('\\').unwrap_or(("", path));
    Hit {
        title: name.to_string(),
        subtitle: parent.to_string(),
        kind: if dir { Kind::Folder } else { Kind::File },
        action: Action::Open(path.to_string()),
        score,
    }
}

/// Does `path` still exist? While typing, the same files make the list keystroke after
/// keystroke, so answers are kept for a few seconds instead of asking the disk each time.
fn exists(path: &str) -> bool {
    const FRESH: Duration = Duration::from_secs(5);
    static KNOWN: Mutex<Option<HashMap<String, (bool, Instant)>>> = Mutex::new(None);
    if let Some(&(e, at)) = KNOWN.lock().unwrap().get_or_insert_with(HashMap::new).get(path) {
        if at.elapsed() < FRESH {
            return e;
        }
    }
    let e = std::fs::symlink_metadata(path).is_ok();
    let mut known = KNOWN.lock().unwrap();
    let known = known.get_or_insert_with(HashMap::new);
    if known.len() >= 4096 {
        known.clear();
    }
    known.insert(path.to_string(), (e, Instant::now()));
    e
}

fn starts_ci(s: &str, lq: &str) -> bool {
    s.get(..lq.len()).is_some_and(|p| p.eq_ignore_ascii_case(lq))
}

/// A window title without the app name most apps append ("Inbox - Google Chrome").
fn strip_app<'a>(title: &'a str, app: &str) -> &'a str {
    if app.is_empty() || title.len() <= app.len() {
        return title;
    }
    match title.get(title.len() - app.len()..) {
        Some(tail) if tail.eq_ignore_ascii_case(app) => {
            title[..title.len() - app.len()].trim_end_matches(|c: char| c.is_whitespace() || "-\u{2013}\u{2014}|".contains(c))
        }
        _ => title,
    }
}

/// A match this good gets its kind's full bonus (see `fit`).
const GOOD: i32 = 90;
/// A window found only through its app's name: just below that app.
const BY_APP_NAME: i32 = 120;
/// The most recently opened file; older ones a little less.
const RECENT: i32 = 100;

/// How the query sits in a name, beyond nucleo's letter-by-letter score. Whole words are what
/// people type; letters scattered through a long name ("readme" in "Inventor Read-only Mode",
/// "teams" in "Steam Support Center") are a last resort.
/// `l` is the name, lowercased.
fn fit(l: &str, lq: &str, words: &[&str]) -> i32 {
    if l == lq {
        return 200;
    }
    let p = prefix_bonus_lower(l, lq);
    if p > 0 {
        return p;
    }
    let compact = |s: &str| -> String { s.chars().filter(|c| c.is_alphanumeric()).collect() };
    let cq = compact(lq);
    if cq.len() >= 2 && compact(l).starts_with(&cq) {
        return 120; // "wifi" for "Wi-Fi"
    }
    if words.len() > 1 && words_begin(l, words) {
        return GOOD; // "vis code" for "Visual Studio Code"
    }
    let initials: String = l.split(|c: char| !c.is_alphanumeric()).filter_map(|w| w.chars().next()).collect();
    if cq.len() >= 2 && initials.starts_with(&cq) {
        return GOOD; // "vsc" for "Visual Studio Code"
    }
    if l.contains(lq) {
        return 30;
    }
    0
}

/// An app, setting or command matched well gets its whole kind bonus; matched loosely, a
/// quarter of it, so a file named as typed beats an app that merely contains the letters.
fn kind_bonus(kind: Kind, fit: i32) -> i32 {
    if fit >= GOOD { kind.bonus() } else { kind.bonus() / 4 }
}

/// Up to 40 for how much of the name the query covers: "photo" is nearer to "Photos" than to
/// "Photometric Calc 2026 R1". Breaks the ties equally good prefixes leave.
fn coverage(name: &str, lq: &str) -> i32 {
    let n = name.chars().count().max(1) as i32;
    (40 * lq.chars().count() as i32 / n).min(40)
}

/// Start-menu entries that come along with an app rather than being one: uninstallers, help,
/// release notes. Still found, just after the app itself, unless asked for by name. `l` is the
/// name, lowercased.
fn secondary(l: &str, lq: &str) -> bool {
    const WORDS: &[&str] = &[
        "uninstall", "readme", "read me", "release notes", "help", "documentation", "faq", "license",
        "manual", "what's new", "website", "installer", "repair", "migrat", "support center",
    ];
    WORDS.iter().any(|w| l.contains(w) && !lq.contains(w.split(' ').next().unwrap_or(w)))
}

/// Where a file lives says a lot about whether it is the one you mean: your own folders over
/// system and program trees, other accounts, caches and build output; shallow over deep.
/// `lp` is the lowercased path.
fn path_prior(lp: &str) -> i32 {
    use std::sync::OnceLock;
    static PROFILE: OnceLock<String> = OnceLock::new();
    const KNOWN: &[&str] = &[
        "\\desktop", "\\documents", "\\downloads", "\\pictures", "\\videos", "\\music",
        "\\onedrive\\desktop", "\\onedrive\\documents", "\\onedrive\\pictures",
    ];
    const SYSTEM: &[&str] = &[
        "\\windows\\", "\\windows.old", "\\$recycle.bin", "\\program files", "\\programdata",
        "\\users\\default", "\\users\\public", "\\appdata\\", "\\xboxgames\\", "\\steamapps\\",
    ];
    const BUILD: &[&str] = &[
        "\\node_modules\\", "\\.git\\", "\\target\\", "\\obj\\", "\\bin\\debug", "\\bin\\release",
        "cache\\", "\\__pycache__", "\\site-packages\\", "\\.venv\\", "\\dist\\", "\\build\\",
    ];
    let profile = PROFILE.get_or_init(|| {
        std::env::var("USERPROFILE").map(|p| p.trim_end_matches('\\').to_lowercase()).unwrap_or_default()
    });
    let mut p = -3 * lp.matches('\\').count() as i32;
    if let Some(rest) = lp.strip_prefix(profile.as_str()).filter(|r| !profile.is_empty() && r.starts_with('\\')) {
        if rest.matches('\\').count() == 1 {
            p += 60; // C:\Users\you\Downloads itself
        } else if KNOWN.iter().any(|k| rest.starts_with(k)) {
            p += 20;
        }
    }
    if SYSTEM.iter().any(|n| lp.contains(n)) {
        p -= 90;
    } else if BUILD.iter().any(|n| lp.contains(n)) {
        p -= 40;
    }
    p
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

/// Does every one of `words` (lowercase) begin some word of `text`? Compared letter by letter,
/// without copies: it runs for every open window on every keystroke.
fn words_begin(text: &str, words: &[&str]) -> bool {
    words.iter().all(|w| {
        let letters = || w.chars().filter(|c| c.is_alphanumeric());
        letters().next().is_none()
            || text.split(|c: char| !c.is_alphanumeric()).any(|t| {
                let mut tc = t.chars().flat_map(char::to_lowercase);
                letters().all(|c| tc.next() == Some(c))
            })
    })
}

/// Settings-index entries: every typed word must begin a word of the name or of its keywords
/// ("mouse size" finds "Mouse pointer size", "batt" finds "Battery saver"). Words found in the
/// name count for more than words found only among the keywords.
fn word_prefix_score(w: &Words, words: &[&str]) -> Option<i32> {
    if words.is_empty() || words.iter().map(|w| w.len()).sum::<usize>() < 3 {
        return None;
    }
    let mut score = 120;
    for q in words {
        if w.name_words.split(' ').any(|n| n.starts_with(q)) {
            score += 40;
        } else if w.keywords.split(' ').any(|k| k.starts_with(q)) {
            score += 15;
        } else {
            return None;
        }
    }
    if w.name_words.split(' ').next().is_some_and(|n| n.starts_with(words[0])) {
        score += 60;
    }
    Some(score)
}

/// 140 if the whole name starts with the query, 100 if any word does.
fn prefix_bonus(name: &str, lq: &str) -> i32 {
    prefix_bonus_lower(&name.to_lowercase(), lq)
}

/// `prefix_bonus` of a name already lowercased.
fn prefix_bonus_lower(l: &str, lq: &str) -> i32 {
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn whole_words_beat_scattered_letters() {
        assert!(fit("readme.md", "readme", &["readme"]) > fit("inventor read-only mode", "readme", &["readme"]));
        assert_eq!(fit("inventor read-only mode", "readme", &["readme"]), 0);
        assert!(fit("wi-fi settings", "wifi", &["wifi"]) >= GOOD);
        assert!(fit("visual studio code", "vsc", &["vsc"]) >= GOOD);
        assert!(coverage("Photos", "photo") > coverage("Photometric Calc 2026 R1", "photo"));
    }

    #[test]
    fn your_folders_beat_system_copies() {
        std::env::set_var("USERPROFILE", r"C:\Users\me");
        let mine = path_prior(r"c:\users\me\downloads");
        assert!(mine > path_prior(r"c:\users\public\downloads"));
        assert!(mine > path_prior(r"c:\windows.old\users\me\downloads"));
        assert!(path_prior(r"c:\users\me\desktop\app\src") > path_prior(r"c:\users\me\desktop\app\node_modules\x\src"));
    }

    #[test]
    fn window_titles_lose_the_app_suffix() {
        assert_eq!(strip_app("Inbox - Google Chrome", "Google Chrome"), "Inbox");
        assert_eq!(strip_app("Claude", "Claude"), "Claude");
        assert_eq!(strip_app("notes.txt \u{2013} Notepad", "Notepad"), "notes.txt");
    }

    #[test]
    fn words_begin_by_word() {
        assert!(words_begin("Inbox - Google Chrome", &["goo", "inb"]));
        assert!(words_begin("Wi-Fi settings", &["fi"]));
        assert!(!words_begin("Inbox - Google Chrome", &["oogle"]));
        assert!(words_begin("Ünïcode Tïtle", &["ünï"]));
        assert!(words_begin("anything", &["--"]));
    }

    /// Matches of a longer query are found among the last query's matches only when they must be
    /// among them: not after switching from names to whole paths, nor after an exact ending (`$`).
    #[test]
    fn narrowing_never_loses_matches() {
        let dir = std::env::temp_dir().join(format!("palette-narrow-{}", std::process::id()));
        std::fs::create_dir_all(dir.join("foo")).unwrap();
        let bar = dir.join("foo").join("bar.txt");
        let foo = dir.join("foo.txt");
        let odd = dir.join("foo$x.txt");
        for f in [&bar, &foo, &odd] {
            std::fs::write(f, b"").unwrap();
        }
        let mut cat = Catalog::default();
        for f in [&bar, &foo, &odd] {
            cat.files.push(f.to_str().unwrap(), false);
        }
        let s = Searcher::new(Arc::new(RwLock::new(cat)), Arc::new(Mutex::new(Usage::default())));
        let titles = |q: &str| -> Vec<String> { s.search_all(q, 20).into_iter().map(|h| h.title).collect() };

        assert!(!titles("foo").contains(&"bar.txt".to_string()));
        assert!(titles("foo\\b").contains(&"bar.txt".to_string()), "by its path, after a search by name");
        // No name ends in "foo"; "foo$x" is no exact ending, so it matches "foo$x.txt" fuzzily.
        assert!(!titles("foo$").iter().any(|t| t.ends_with(".txt")));
        assert!(titles("foo$x").contains(&"foo$x.txt".to_string()), "after an exact ending");
        let _ = std::fs::remove_dir_all(&dir);
    }

    #[test]
    fn helpers_rank_after_their_app() {
        assert!(secondary("visual studio installer", "vis"));
        assert!(!secondary("visual studio installer", "installer"));
        assert!(!secondary("visual studio", "vis"));
    }
}
