//! Finds candidate documents from the file snapshots the MFT helper already wrote
//! (or the profile-walk fallback), so the indexer never has to walk the disk itself.

use crate::extract::{is_doc_ext, MAX_FILE_BYTES};
use std::path::{Path, PathBuf};
use std::time::UNIX_EPOCH;

pub struct Candidate {
    pub path: String,
    pub mtime_ms: u64,
    pub size: u64,
}

/// Directories whose contents are tooling or build output, not the user's own documents.
const SKIP_FRAGMENTS: &[&str] = &[
    "\\node_modules\\", "\\.git\\", "\\site-packages\\", "\\appdata\\", "\\$recycle.bin\\",
    "\\windows\\", "\\program files", "\\programdata\\", "\\.cargo\\", "\\.rustup\\", "\\.nuget\\",
    "\\__pycache__\\", "\\.venv\\", "\\venv\\", "\\packagecache\\", "\\documentation~\\",
    "\\dist\\", "\\dist-new\\", "\\build\\", "\\_internal\\", "\\cmakefiles\\", "\\_vendor\\",
    "\\vendor\\", "\\third_party\\", "\\third-party\\", "\\licenses\\", "\\licences\\",
    "\\target\\", "\\obj\\", "\\.idea\\", "\\.vscode\\", ".egg-info\\", ".dist-info\\",
    "\\tests\\", "\\test\\", "\\testdata\\",
];

/// Boilerplate that ships with every package and says nothing about the user's work.
const SKIP_STEMS: &[&str] = &[
    "license", "licence", "copying", "changelog", "changes", "history", "notice", "notices",
    "third party notices", "thirdpartynotices", "authors", "contributors", "contributing",
    "code_of_conduct", "codeowners", "security", "requirements", "robots", "keywords",
    "top_level", "entry_points", "tableofcontents", "pull_request_template", "manifest",
];

fn is_boilerplate(path_lower: &str) -> bool {
    let name = path_lower.rsplit('\\').next().unwrap_or(path_lower);
    let stem = name.rsplit_once('.').map(|(s, _)| s).unwrap_or(name);
    SKIP_STEMS.iter().any(|s| stem == *s || stem.starts_with(&format!("{s}-")) || stem.starts_with(&format!("{s}_")))
}

fn snapshot_lines(data: &Path) -> Vec<String> {
    let mut files: Vec<PathBuf> = std::fs::read_dir(data.join("index"))
        .map(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .filter(|p| {
                    p.extension().is_some_and(|e| e == "txt") && p.file_stem().is_some_and(|s| s.len() == 1)
                })
                .collect()
        })
        .unwrap_or_default();
    if files.is_empty() {
        files.push(data.join("walk.txt"));
    }
    let mut lines = Vec::new();
    for f in files {
        if let Ok(b) = std::fs::read(&f) {
            lines.extend(String::from_utf8_lossy(&b).lines().map(str::to_owned));
        }
    }
    lines
}

/// Applies every indexing rule to one path: document type, under `home_l` (lower-cased),
/// not tooling/build output or boilerplate, and a readable file of sane size.
pub fn accept(path: &str, home_l: &str) -> Option<Candidate> {
    if path.is_empty() || path.ends_with('\\') {
        return None;
    }
    let ext = path.rsplit_once('.').map(|(_, e)| e)?;
    if !is_doc_ext(ext) {
        return None;
    }
    let l = path.to_lowercase();
    if !l.starts_with(home_l) || SKIP_FRAGMENTS.iter().any(|f| l.contains(f)) || is_boilerplate(&l) {
        return None;
    }
    let md = std::fs::metadata(path).ok()?;
    if !md.is_file() || md.len() == 0 || md.len() > MAX_FILE_BYTES {
        return None;
    }
    let mtime_ms = md
        .modified()
        .ok()
        .and_then(|t| t.duration_since(UNIX_EPOCH).ok())
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0);
    Some(Candidate { path: path.to_string(), mtime_ms, size: md.len() })
}

/// Documents under `home`, newest first, at most `limit`.
pub fn discover(data: &Path, home: &Path, limit: usize) -> Vec<Candidate> {
    let home_l = home.display().to_string().to_lowercase();
    let mut out = Vec::new();
    for line in snapshot_lines(data) {
        if let Some(c) = accept(&line, &home_l) {
            out.push(c);
        }
    }
    // Backups and project copies: same name and size means the same document. Keep one copy,
    // chosen by path (shallowest, then alphabetical) rather than by mtime: synced copies get
    // their timestamps touched, and a copy that flips between runs would be re-embedded each time.
    let mut best: std::collections::HashMap<(String, u64), Candidate> = std::collections::HashMap::new();
    for c in out {
        let name = c.path.rsplit('\\').next().unwrap_or(&c.path).to_lowercase();
        let rank = |c: &Candidate| (c.path.matches('\\').count(), c.path.to_lowercase());
        match best.entry((name, c.size)) {
            std::collections::hash_map::Entry::Occupied(mut e) => {
                if rank(&c) < rank(e.get()) {
                    e.insert(c);
                }
            }
            std::collections::hash_map::Entry::Vacant(e) => {
                e.insert(c);
            }
        }
    }
    let mut out: Vec<Candidate> = best.into_values().collect();
    out.sort_by(|a, b| b.mtime_ms.cmp(&a.mtime_ms)); // newest first: fresh documents become searchable first
    out.truncate(limit);
    out
}
