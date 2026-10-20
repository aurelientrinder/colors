//! What the chat model can look up for itself (chat.rs offers these as tools; the shell runs the calls).
//!
//! `find_files`: the user's files by what they are called, what they are about, and when they were
//! last opened or changed ("the file I was working on last week"). Three sources, merged:
//! * Windows' Recent folder: a shortcut for every file the user opened, dated when it was opened;
//! * the file index (search.rs), by name;
//! * the meaning search of documents (semantic.rs), by topic;
//! * for a period ("this month's photos"), the user's own folders walked for files changed in it: a
//!   photo from the phone or a download is never in the Recent folder.
//!
//! A file the user opened in the period asked about comes first, then files changed in it; a query
//! narrows to files that match it by name or by meaning.

use crate::model::{Action, Kind};
use serde::Serialize;
use std::collections::HashMap;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

/// A file found for the model (and shown to the user as a card).
#[derive(Serialize, Clone, Debug)]
pub struct FileFound {
    pub path: String,
    pub name: String,
    pub folder: String,
    /// Last changed, unix seconds.
    pub modified: i64,
    /// Last opened (from the Recent folder), unix seconds; 0 when unknown.
    pub opened: i64,
    /// Why it was found: "opened", "name", "topic", or two of them joined by "+".
    pub why: String,
}

/// What kind of file is wanted.
fn kind_matches(kind: &str, path: &str) -> bool {
    let ext = Path::new(path).extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
    let any = |list: &[&str]| list.contains(&ext.as_str());
    match kind {
        "document" => any(&["pdf", "doc", "docx", "odt", "rtf", "txt", "md", "pages", "epub", "tex"]),
        "spreadsheet" => any(&["xls", "xlsx", "xlsm", "ods", "csv", "tsv", "numbers"]),
        "presentation" => any(&["ppt", "pptx", "odp", "key"]),
        "image" => any(&["png", "jpg", "jpeg", "gif", "webp", "heic", "bmp", "tif", "tiff", "svg", "raw", "cr2", "nef"]),
        "video" => any(&["mp4", "mov", "mkv", "avi", "webm", "wmv"]),
        "audio" => any(&["mp3", "wav", "flac", "m4a", "ogg", "aac"]),
        "code" => any(&[
            "rs", "cs", "py", "js", "ts", "tsx", "jsx", "java", "kt", "c", "cpp", "h", "hpp", "go", "rb", "php", "swift", "sql",
            "html", "css", "json", "toml", "yaml", "yml", "xml", "ps1", "sh", "ipynb", "xaml",
        ]),
        "3d" => any(&["step", "stp", "stl", "3mf", "obj", "ply", "glb", "gltf", "f3d", "sldprt"]),
        _ => true,
    }
}

fn unix(t: SystemTime) -> i64 {
    t.duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

/// Files opened lately, newest first: (path, when opened). Folders are left out.
pub fn recently_opened(max: usize) -> Vec<(String, i64)> {
    let Some(appdata) = std::env::var_os("APPDATA") else { return Vec::new() };
    let dir = Path::new(&appdata).join(r"Microsoft\Windows\Recent");
    let Ok(rd) = std::fs::read_dir(&dir) else { return Vec::new() };
    let mut lnks: Vec<(SystemTime, std::path::PathBuf)> = rd
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("lnk")))
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .collect();
    lnks.sort_by(|a, b| b.0.cmp(&a.0));
    let mut out = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for (at, lnk) in lnks {
        if out.len() >= max {
            break;
        }
        let Some(target) = crate::home::link_target(&lnk) else { continue };
        if target.starts_with(r"\\") || !seen.insert(target.to_lowercase()) {
            continue;
        }
        if std::fs::metadata(&target).is_ok_and(|m| m.is_file()) {
            out.push((target, unix(at)));
        }
    }
    out
}

/// The folders a person keeps their own files in: Desktop, Documents, Downloads, Pictures, Videos, Music,
/// and OneDrive's.
fn own_folders() -> Vec<std::path::PathBuf> {
    let mut out = Vec::new();
    for base in ["USERPROFILE", "OneDrive"].iter().filter_map(std::env::var_os) {
        for sub in ["Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music"] {
            let p = Path::new(&base).join(sub);
            if p.is_dir() && !out.contains(&p) {
                out.push(p);
            }
        }
    }
    out
}

/// Files under `roots` changed in `from..to` (unix seconds), newest first, at most `max`. Folders that
/// hold tools rather than the user's own files (dot folders, node_modules, build output) are skipped,
/// and the walk stops after `budget` entries so a huge tree cannot hold the answer up.
fn changed_in(roots: &[std::path::PathBuf], from: i64, to: i64, max: usize, budget: usize) -> Vec<(String, i64)> {
    const SKIP: [&str; 9] = ["node_modules", "target", "bin", "obj", "__pycache__", ".git", ".venv", "venv", "AppData"];
    let mut found: Vec<(String, i64)> = Vec::new();
    let mut seen = 0usize;
    let mut stack: Vec<(std::path::PathBuf, usize)> = roots.iter().map(|r| (r.clone(), 0)).collect();
    while let Some((dir, depth)) = stack.pop() {
        let Ok(rd) = std::fs::read_dir(&dir) else { continue };
        for e in rd.flatten() {
            seen += 1;
            if seen > budget {
                break;
            }
            let name = e.file_name().to_string_lossy().into_owned();
            let Ok(ft) = e.file_type() else { continue };
            if ft.is_dir() {
                if depth < 6 && !name.starts_with('.') && !SKIP.iter().any(|s| s.eq_ignore_ascii_case(&name)) {
                    stack.push((e.path(), depth + 1));
                }
            } else if ft.is_file() && !name.starts_with('~') && !name.starts_with('.') {
                let t = e.metadata().ok().and_then(|m| m.modified().ok()).map(unix).unwrap_or(0);
                if t >= from && (to == 0 || t < to) {
                    found.push((e.path().display().to_string(), t));
                }
            }
        }
    }
    found.sort_by(|a, b| b.1.cmp(&a.1));
    found.truncate(max);
    found
}

/// Words that say what kind of file is wanted rather than what it is called ("my photos", "the pdf"):
/// left out of the query (the kind is its own argument), or no file would match "photos" by name.
const KIND_WORDS: [&str; 30] = [
    "file", "files", "photo", "photos", "picture", "pictures", "pic", "pics", "image", "images", "document", "documents",
    "doc", "docs", "video", "videos", "my", "the", "a", "an", "some", "fichier", "fichiers", "mes", "les", "des", "took",
    "taken", "recent", "latest",
];

/// `query` without the words of KIND_WORDS.
fn strip_kind_words(query: &str) -> String {
    query
        .split_whitespace()
        .filter(|w| !KIND_WORDS.contains(&w.to_lowercase().trim_matches(|c: char| !c.is_alphanumeric())))
        .collect::<Vec<_>>()
        .join(" ")
}

/// Does `name` hold every word of `query` (case-insensitively)?
fn name_matches(name: &str, query: &str) -> bool {
    let n = name.to_lowercase();
    query.split_whitespace().filter(|w| w.chars().count() >= 2).all(|w| n.contains(&w.to_lowercase()))
}

/// Find files. `query` may be empty (then only `from..to` and `kind` choose); `from` and `to` are unix
/// seconds (0: open-ended); `kind` is one of the kinds in `kind_matches` or "any".
/// `by_name` and `by_topic` look the query up in the index and in the meaning search.
pub fn find_files(
    query: &str,
    from: i64,
    to: i64,
    kind: &str,
    limit: usize,
    by_name: &dyn Fn(&str) -> Vec<String>,
    by_topic: &dyn Fn(&str) -> Vec<String>,
) -> Vec<FileFound> {
    let query = strip_kind_words(query);
    let query = query.as_str();
    let in_period = |t: i64| t > 0 && (from == 0 || t >= from) && (to == 0 || t < to);
    let timed = from != 0 || to != 0;

    // path (lowercased) -> (path, opened, reasons)
    let mut found: HashMap<String, (String, i64, Vec<&'static str>)> = HashMap::new();
    let mut add = |path: String, opened: i64, why: &'static str| {
        let e = found.entry(path.to_lowercase()).or_insert_with(|| (path, 0, Vec::new()));
        e.1 = e.1.max(opened);
        if !e.2.contains(&why) {
            e.2.push(why);
        }
    };

    let recent = recently_opened(400);
    let opened_at: HashMap<String, i64> = recent.iter().map(|(p, t)| (p.to_lowercase(), *t)).collect();
    for (p, t) in &recent {
        let name = Path::new(p).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        if (query.is_empty() || name_matches(&name, query)) && (!timed || in_period(*t)) {
            add(p.clone(), *t, "opened");
        }
    }
    if timed {
        for (p, _) in changed_in(&own_folders(), from, to, 400, 60_000) {
            let name = Path::new(&p).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            if (query.is_empty() || name_matches(&name, query)) && kind_matches(kind, &p) {
                let t = opened_at.get(&p.to_lowercase()).copied().unwrap_or(0);
                add(p, t, "changed");
            }
        }
    }
    if !query.is_empty() {
        for p in by_name(query) {
            let t = opened_at.get(&p.to_lowercase()).copied().unwrap_or(0);
            add(p, t, "name");
        }
        for p in by_topic(query) {
            let t = opened_at.get(&p.to_lowercase()).copied().unwrap_or(0);
            add(p, t, "topic");
        }
    }

    let mut out: Vec<FileFound> = found
        .into_values()
        .filter(|(p, _, _)| kind_matches(kind, p))
        .filter_map(|(path, opened, why)| {
            let md = std::fs::metadata(&path).ok()?;
            if !md.is_file() {
                return None;
            }
            let modified = md.modified().map(unix).unwrap_or(0);
            // Asked about a period: opened or changed in it.
            if timed && !in_period(opened) && !in_period(modified) {
                return None;
            }
            let p = Path::new(&path);
            Some(FileFound {
                name: p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default(),
                folder: p.parent().map(|f| f.display().to_string()).unwrap_or_default(),
                path,
                modified,
                opened,
                why: why.join("+"),
            })
        })
        .collect();
    // Found more than one way first, then opened in the period, then newest.
    out.sort_by_key(|f| {
        let ways = f.why.matches('+').count();
        let opened_then = timed && in_period(f.opened);
        (std::cmp::Reverse(ways), std::cmp::Reverse(opened_then), std::cmp::Reverse(f.opened.max(f.modified)))
    });
    out.truncate(limit.clamp(1, 12));
    out
}

/// The paths of files among `hits` (for `find_files`'s `by_name` and `by_topic`).
pub fn file_paths(hits: impl IntoIterator<Item = crate::model::Hit>) -> Vec<String> {
    hits.into_iter()
        .filter(|h| h.kind == Kind::File)
        .filter_map(|h| match h.action {
            Action::Open(p) => Some(p),
            _ => None,
        })
        .collect()
}

/// Unix seconds now (for callers that turn "last week" into a range).
pub fn now() -> i64 {
    unix(SystemTime::now())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn kinds_go_by_extension() {
        assert!(kind_matches("document", r"C:\a\lease.PDF"));
        assert!(!kind_matches("document", r"C:\a\photo.jpg"));
        assert!(kind_matches("code", r"C:\src\main.rs"));
        assert!(kind_matches("any", r"C:\a\whatever.xyz"));
    }

    #[test]
    fn a_walk_finds_files_changed_in_the_period() {
        let dir = std::env::temp_dir().join(format!("palette-tools-{}", std::process::id()));
        let _ = std::fs::create_dir_all(dir.join("sub").join("node_modules"));
        std::fs::write(dir.join("sub").join("photo.jpg"), b"x").unwrap();
        std::fs::write(dir.join("sub").join("node_modules").join("skipped.js"), b"x").unwrap();
        let now = now();
        let found = changed_in(&[dir.clone()], now - 3600, 0, 10, 1000);
        let _ = std::fs::remove_dir_all(&dir);
        assert_eq!(found.len(), 1, "{found:?}");
        assert!(found[0].0.ends_with("photo.jpg"));
        assert!(changed_in(&[dir], now + 3600, 0, 10, 1000).is_empty());
    }

    #[test]
    fn words_for_the_kind_of_file_are_not_looked_for_in_names() {
        assert_eq!(strip_kind_words("photos"), "");
        assert_eq!(strip_kind_words("my holiday photos"), "holiday");
        assert_eq!(strip_kind_words("the Q3 report"), "Q3 report");
    }

    #[test]
    fn a_name_matches_when_it_holds_every_word() {
        assert!(name_matches("Quarterly Report Q3.docx", "report q3"));
        assert!(!name_matches("Quarterly Report Q3.docx", "report q4"));
        assert!(name_matches("anything.txt", ""));
    }
}
