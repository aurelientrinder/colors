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

    // Apps, commands and settings: a few thousand entries, always a full scan.
    {
        let mut m = Matcher::new(Config::DEFAULT);
        let mut buf = Vec::new();
        for it in &cat.items {
            let scored = match &it.keywords {
                // Every word already begins a word: always a good match.
                Some(kw) => word_prefix_score(&it.name, kw, &words).map(|s| (s, GOOD)),
                None => pattern.score(Utf32Str::new(&it.name, &mut buf), &mut m).map(|s| {
                    let f = fit(&it.name, &lq, &words);
                    (s as i32 + f, f)
                }),
            };
            if let Some((base, f)) = scored {
                let mut h = it.hit.clone();
                h.score = base + kind_bonus(h.kind, f) + coverage(&it.name, &lq) + usage.bonus(&h.key());
                if h.kind == Kind::App && secondary(&it.name, &lq) {
                    h.score -= 60;
                }
                hits.push(h);
            }
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

    // The best matches by name, then ranked on their path too.
    let mut top = scored;
    if top.len() > FILE_POOL {
        top.select_nth_unstable_by(FILE_POOL, |a, b| b.1.cmp(&a.1));
        top.truncate(FILE_POOL);
    }
    // What you opened lately (Windows' Recent folder, newest first) counts for a lot.
    let recent = crate::home::recent_cached();
    let recent_rank: std::collections::HashMap<String, i32> = recent
        .iter()
        .enumerate()
        .filter_map(|(i, h)| match &h.action {
            Action::Open(p) => Some((p.to_lowercase(), i as i32)),
            _ => None,
        })
        .collect();
    let file_hit = |path: &str, dir: bool, raw: u32| -> Hit {
        let (parent, name) = path.rsplit_once('\\').unwrap_or(("", path));
        let kind = if dir { Kind::Folder } else { Kind::File };
        let lp = path.to_lowercase();
        let mut h = Hit {
            title: name.to_string(),
            subtitle: parent.to_string(),
            kind,
            action: Action::Open(path.to_string()),
            score: 0,
        };
        h.score = raw as i32 + prefix_bonus(name, &lq) + kind.bonus() + path_prior(&lp) + usage.bonus(&h.key());
        if let Some(r) = recent_rank.get(&lp) {
            h.score += RECENT - r / 2;
        }
        h
    };
    let mut cands: Vec<Hit> =
        top.into_iter().map(|(i, s)| file_hit(cat.files.path(i as usize), cat.files.is_dir(i as usize), s)).collect();
    // Recent files the index has not caught up with yet.
    {
        let mut m = Matcher::new(cfg.clone());
        let mut buf = Vec::new();
        for h in &recent {
            let Action::Open(p) = &h.action else { continue };
            let hay = if path_query { p.as_str() } else { h.title.as_str() };
            if let Some(s) = pattern.score(Utf32Str::new(hay, &mut buf), &mut m) {
                cands.push(file_hit(p, h.kind == Kind::Folder, s));
            }
        }
    }
    cands.sort_by(|a, b| b.score.cmp(&a.score));
    // Score first, stat later: only verify existence for candidates that actually make the
    // visible list (the index can be a little stale).
    let mut kept = 0usize;
    let mut seen = std::collections::HashSet::new();
    for h in cands {
        if kept >= max {
            break;
        }
        if let Action::Open(p) = &h.action {
            if !seen.insert(p.to_lowercase()) || std::fs::symlink_metadata(p).is_err() {
                continue;
            }
        }
        kept += 1;
        hits.push(h);
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
fn fit(name: &str, lq: &str, words: &[&str]) -> i32 {
    let l = name.to_lowercase();
    if l == lq {
        return 200;
    }
    let p = prefix_bonus(&l, lq);
    if p > 0 {
        return p;
    }
    let compact = |s: &str| -> String { s.chars().filter(|c| c.is_alphanumeric()).collect() };
    let cq = compact(lq);
    if cq.len() >= 2 && compact(&l).starts_with(&cq) {
        return 120; // "wifi" for "Wi-Fi"
    }
    if words.len() > 1 && words_begin(&l, words) {
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
/// release notes. Still found, just after the app itself, unless asked for by name.
fn secondary(name: &str, lq: &str) -> bool {
    const WORDS: &[&str] = &[
        "uninstall", "readme", "read me", "release notes", "help", "documentation", "faq", "license",
        "manual", "what's new", "website", "installer", "repair", "migrat", "support center",
    ];
    let l = name.to_lowercase();
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

/// Does every one of `words` (lowercase) begin some word of `text`?
fn words_begin(text: &str, words: &[&str]) -> bool {
    let tw: Vec<String> =
        text.split(|c: char| !c.is_alphanumeric()).filter(|w| !w.is_empty()).map(str::to_lowercase).collect();
    words.iter().all(|w| {
        let w: String = w.chars().filter(|c| c.is_alphanumeric()).collect();
        w.is_empty() || tw.iter().any(|t| t.starts_with(&w))
    })
}

/// Settings-index entries: every typed word must begin a word of the name or of its keywords
/// ("mouse size" finds "Mouse pointer size", "batt" finds "Battery saver"). Words found in the
/// name count for more than words found only among the keywords.
fn word_prefix_score(name: &str, keywords: &str, words: &[&str]) -> Option<i32> {
    if words.is_empty() || words.iter().map(|w| w.len()).sum::<usize>() < 3 {
        return None;
    }
    let split = |s: &str| -> Vec<String> {
        s.split(|c: char| !c.is_alphanumeric()).filter(|w| !w.is_empty()).map(str::to_lowercase).collect()
    };
    let name_words = split(name);
    let kw_words = split(keywords);
    let mut score = 120;
    for w in words {
        if name_words.iter().any(|n| n.starts_with(w)) {
            score += 40;
        } else if kw_words.iter().any(|k| k.starts_with(w)) {
            score += 15;
        } else {
            return None;
        }
    }
    if name_words.first().is_some_and(|n| n.starts_with(words[0])) {
        score += 60;
    }
    Some(score)
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn whole_words_beat_scattered_letters() {
        assert!(fit("README.md", "readme", &["readme"]) > fit("Inventor Read-only Mode", "readme", &["readme"]));
        assert_eq!(fit("Inventor Read-only Mode", "readme", &["readme"]), 0);
        assert!(fit("Wi-Fi settings", "wifi", &["wifi"]) >= GOOD);
        assert!(fit("Visual Studio Code", "vsc", &["vsc"]) >= GOOD);
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
    fn helpers_rank_after_their_app() {
        assert!(secondary("Visual Studio Installer", "vis"));
        assert!(!secondary("Visual Studio Installer", "installer"));
        assert!(!secondary("Visual Studio", "vis"));
    }
}
