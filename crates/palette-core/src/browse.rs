//! Browsing a folder: a typed folder path ("C:\Users\me\Projects\", "~\Downloads\") lists what is
//! in it, read from the disk, folders first in Explorer's order. What follows the last backslash
//! filters the list ("...\Projects\pal"); when nothing in the folder matches it, the query is left
//! to the index search, so a deep path typed in one go still finds its file.
//!
//! The shell walks in and out of folders with the arrow keys by writing these paths.

use crate::model::{Action, Hit, Kind};
use nucleo_matcher::pattern::{CaseMatching, Normalization, Pattern};
use nucleo_matcher::{Config, Matcher, Utf32Str};
use std::os::windows::fs::MetadataExt;
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};
use windows::core::PCWSTR;
use windows::Win32::Storage::FileSystem::{FILE_ATTRIBUTE_HIDDEN, FILE_ATTRIBUTE_SYSTEM};
use windows::Win32::UI::Shell::StrCmpLogicalW;

/// Rows a folder shows at most; the list scrolls.
pub const MAX_ROWS: usize = 500;
/// Entries read from one folder at most, so a folder of 100k files still answers at once.
const MAX_ENTRIES: usize = 20_000;
/// A listing is read again after this long: typing a filter reuses it, a new file soon shows.
const FRESH: Duration = Duration::from_secs(2);

struct Entry {
    name: String,
    /// NUL-terminated, for Explorer's ordering (StrCmpLogicalW: "file2" before "file10").
    wide: Vec<u16>,
    dir: bool,
    hidden: bool,
    size: u64,
}

/// The folder `q` points into (with its trailing backslash) and the filter after it, when `q` is
/// a path into a folder: a drive ("C:\...") or the profile ("~\...").
fn split(q: &str) -> Option<(String, &str)> {
    let at = q.rfind(['\\', '/'])?;
    let (dir, filter) = (&q[..=at], &q[at + 1..]);
    let dir = dir.replace('/', "\\");
    let dir = if let Some(rest) = dir.strip_prefix("~\\") {
        let home = std::env::var("USERPROFILE").ok()?;
        format!("{}\\{rest}", home.trim_end_matches('\\'))
    } else {
        let b = dir.as_bytes();
        if b.len() < 3 || !b[0].is_ascii_alphabetic() || b[1] != b':' || b[2] != b'\\' {
            return None;
        }
        dir
    };
    // "C:\a\\b": a doubled separator is no folder anyone means.
    if dir.get(2..).is_none_or(|s| s.contains("\\\\")) {
        return None;
    }
    Some((dir, filter))
}

/// Is `q` a path into a folder (whether or not it exists)? The shell skips the slow tiers for these.
pub fn is_browse_query(q: &str) -> bool {
    split(q.trim()).is_some()
}

/// The folder's entries, read again once [`FRESH`] has passed.
fn listing(dir: &str) -> Option<Arc<Vec<Entry>>> {
    static LAST: Mutex<Option<(String, Instant, Arc<Vec<Entry>>)>> = Mutex::new(None);
    if let Some((d, at, list)) = LAST.lock().unwrap().as_ref() {
        if d.eq_ignore_ascii_case(dir) && at.elapsed() < FRESH {
            return Some(list.clone());
        }
    }
    let mut list = Vec::new();
    for e in std::fs::read_dir(dir).ok()?.take(MAX_ENTRIES).flatten() {
        // On Windows both come from the directory listing itself: no extra disk access.
        let Ok(md) = e.metadata() else { continue };
        let name = e.file_name().to_string_lossy().into_owned();
        let attrs = md.file_attributes();
        list.push(Entry {
            wide: name.encode_utf16().chain(Some(0)).collect(),
            name,
            dir: md.is_dir(),
            hidden: attrs & (FILE_ATTRIBUTE_HIDDEN.0 | FILE_ATTRIBUTE_SYSTEM.0) != 0,
            size: md.len(),
        });
    }
    list.sort_by(|a, b| {
        b.dir.cmp(&a.dir).then_with(|| unsafe { StrCmpLogicalW(PCWSTR(a.wide.as_ptr()), PCWSTR(b.wide.as_ptr())) }.cmp(&0))
    });
    let list = Arc::new(list);
    *LAST.lock().unwrap() = Some((dir.to_string(), Instant::now(), list.clone()));
    Some(list)
}

/// The rows for a path into a folder; `None` when `q` is not one, the folder does not exist, or
/// nothing in it matches the filter (the index search then takes the query).
pub fn hits(q: &str) -> Option<Vec<Hit>> {
    let (dir, filter) = split(q)?;
    let list = listing(&dir)?;
    let mut hits = Vec::new();

    if filter.is_empty() {
        // As Explorer shows it: hidden and system entries left out.
        for (i, e) in list.iter().filter(|e| !e.hidden).take(MAX_ROWS).enumerate() {
            hits.push(entry_hit(&dir, e, -(i as i32)));
        }
        if hits.is_empty() {
            let name = dir.trim_end_matches('\\').rsplit('\\').next().unwrap_or(&dir);
            hits.push(Hit {
                title: name.to_string(),
                subtitle: "Empty folder".into(),
                kind: Kind::Folder,
                action: Action::Open(dir.clone()),
                score: 0,
            });
        }
        return Some(hits);
    }

    let pattern = Pattern::parse(filter, CaseMatching::Smart, Normalization::Smart);
    let mut m = Matcher::new(Config::DEFAULT);
    let mut buf = Vec::new();
    let lf = filter.to_lowercase();
    let mut found: Vec<(i32, usize, &Entry)> = Vec::new();
    for (i, e) in list.iter().enumerate() {
        let prefix = e.name.to_lowercase().starts_with(&lf);
        // A hidden entry shows only when its name is typed.
        if e.hidden && !prefix {
            continue;
        }
        if let Some(s) = pattern.score(Utf32Str::new(&e.name, &mut buf), &mut m) {
            found.push((s as i32 + if prefix { 200 } else { 0 }, i, e));
        }
    }
    if found.is_empty() {
        return None;
    }
    // Best first; ties keep the folder's own order.
    found.sort_by(|a, b| b.0.cmp(&a.0).then(a.1.cmp(&b.1)));
    hits.extend(found.into_iter().take(MAX_ROWS).map(|(s, _, e)| entry_hit(&dir, e, s)));
    Some(hits)
}

fn entry_hit(dir: &str, e: &Entry, score: i32) -> Hit {
    Hit {
        title: e.name.clone(),
        subtitle: if e.dir { String::new() } else { size_text(e.size) },
        kind: if e.dir { Kind::Folder } else { Kind::File },
        action: Action::Open(format!("{dir}{}", e.name)),
        score,
    }
}

/// "0 bytes", "12 KB", "3.4 MB", as Explorer writes sizes.
fn size_text(n: u64) -> String {
    const UNITS: [&str; 4] = ["KB", "MB", "GB", "TB"];
    if n < 1024 {
        return format!("{n} bytes");
    }
    let mut v = n as f64 / 1024.0;
    let mut u = 0;
    while v >= 1024.0 && u < UNITS.len() - 1 {
        v /= 1024.0;
        u += 1;
    }
    if v < 10.0 { format!("{v:.1} {}", UNITS[u]) } else { format!("{v:.0} {}", UNITS[u]) }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn paths_into_folders() {
        assert_eq!(split("C:\\Users\\"), Some(("C:\\Users\\".into(), "")));
        assert_eq!(split("c:/users/tr"), Some(("c:\\users\\".into(), "tr")));
        assert!(split("~\\Desk").is_some_and(|(d, f)| d.ends_with('\\') && !d.starts_with('~') && f == "Desk"));
        assert_eq!(split("foo\\bar"), None);
        assert_eq!(split("C:"), None);
        assert_eq!(split("C:\\a\\\\b"), None);
    }

    #[test]
    fn lists_a_real_folder() {
        let dir = std::env::temp_dir().join(format!("palette-browse-{}", std::process::id()));
        std::fs::create_dir_all(dir.join("sub")).unwrap();
        std::fs::write(dir.join("file10.txt"), "x").unwrap();
        std::fs::write(dir.join("file2.txt"), "x").unwrap();
        let base = format!("{}\\", dir.display());
        let titles = |q: &str| hits(q).map(|h| h.into_iter().map(|h| h.title).collect::<Vec<_>>());
        assert_eq!(titles(&base).unwrap(), ["sub", "file2.txt", "file10.txt"], "folders first, then 2 before 10");
        assert_eq!(titles(&format!("{base}f10")).unwrap()[0], "file10.txt");
        assert_eq!(titles(&format!("{base}zzz")), None, "no match: left to the index search");
        std::fs::remove_dir_all(&dir).unwrap();
    }

    #[test]
    fn sizes_read_like_explorer() {
        assert_eq!(size_text(12), "12 bytes");
        assert_eq!(size_text(1536), "1.5 KB");
        assert_eq!(size_text(50 * 1024 * 1024), "50 MB");
    }
}
