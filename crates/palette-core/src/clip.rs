//! What was copied lately, searchable. The shell reads Windows' clipboard history (Win+V) and keeps
//! adding what is copied while it runs; entries are handed to `add` and live only in memory, so
//! nothing copied is ever written to disk by Palette.
//!
//! Quiet by default: copied text can be anything, so it never shows among ordinary results or on
//! Home. It answers only when asked: a query that starts with `clip`, `clipboard`, `copied` or
//! `paste` ("clip", "clip invoice", "clipboard the tracking number"). Words that appear in an entry
//! find it at once; if none do, the entries are searched by meaning (the helper embeds them like
//! apps and settings, under keys that begin with `clip:`).

use crate::model::{Action, Hit, Kind};
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;

/// Entries kept, newest first.
const KEEP: usize = 200;
/// Longer text is cut here.
const MAX_CHARS: usize = 20_000;
/// Rows shown for a bare "clip".
const SHOWN: usize = 8;
/// Entries offered to the meaning search.
const EMBEDDED: usize = 120;
const TRIGGERS: [&str; 5] = ["clip", "clipboard", "copied", "paste", "pasted"];
pub const KEY_PREFIX: &str = "clip:";

struct Entry {
    id: u64,
    text: String,
    /// Unix seconds when it was copied.
    at: i64,
}

static STORE: Mutex<Vec<Entry>> = Mutex::new(Vec::new());
static NEXT_ID: AtomicU64 = AtomicU64::new(1);
static VERSION: AtomicU64 = AtomicU64::new(0);

/// Changes whenever the set of entries does (the helper's copy is refreshed then).
pub fn version() -> u64 {
    VERSION.load(Ordering::Relaxed)
}

/// Remember `text`, copied at unix time `at`. Copying the same text again moves it to the front.
pub fn add(text: &str, at: i64) {
    if text.trim().is_empty() {
        return;
    }
    let text: String = text.chars().take(MAX_CHARS).collect();
    let mut store = STORE.lock().unwrap();
    let id = match store.iter().position(|e| e.text == text) {
        Some(i) => {
            let old = store.remove(i);
            if i == 0 && old.at >= at {
                store.insert(0, old);
                return;
            }
            old.id
        }
        None => NEXT_ID.fetch_add(1, Ordering::Relaxed),
    };
    store.insert(0, Entry { id, text, at });
    store.truncate(KEEP);
    VERSION.fetch_add(1, Ordering::Relaxed);
}

/// Forget everything.
pub fn clear() {
    STORE.lock().unwrap().clear();
    VERSION.fetch_add(1, Ordering::Relaxed);
}

/// Drop `text` from the list.
pub fn remove(text: &str) {
    let mut store = STORE.lock().unwrap();
    let before = store.len();
    store.retain(|e| e.text != text);
    if store.len() != before {
        VERSION.fetch_add(1, Ordering::Relaxed);
    }
}

/// What follows the trigger word in a query that asks for the clipboard ("" for a bare "clip").
pub fn ask(query: &str) -> Option<&str> {
    let q = query.trim_start();
    let (first, rest) = q.split_once(char::is_whitespace).unwrap_or((q, ""));
    TRIGGERS.iter().any(|t| first.eq_ignore_ascii_case(t)).then(|| rest.trim())
}

/// Rows for the entries a clipboard query matches by words, newest first.
pub fn hits(query: &str) -> Vec<Hit> {
    let Some(rest) = ask(query) else { return Vec::new() };
    let words: Vec<String> = rest.split_whitespace().map(str::to_lowercase).collect();
    let now = now();
    STORE
        .lock()
        .unwrap()
        .iter()
        .filter(|e| {
            let l = e.text.to_lowercase();
            words.iter().all(|w| l.contains(w.as_str()))
        })
        .take(SHOWN)
        .enumerate()
        .map(|(i, e)| hit(e, now, 6000 - i as i32))
        .collect()
}

/// The row for the entry a semantic hit's key (`clip:<id>`) names.
pub fn hit_for_key(key: &str) -> Option<Hit> {
    let id: u64 = key.strip_prefix(KEY_PREFIX)?.parse().ok()?;
    let now = now();
    STORE.lock().unwrap().iter().find(|e| e.id == id).map(|e| hit(e, now, 0))
}

/// (key, title, text) of the newest entries, for the meaning search.
pub fn items() -> Vec<(String, String, String)> {
    STORE
        .lock()
        .unwrap()
        .iter()
        .take(EMBEDDED)
        .map(|e| {
            let body: String = e.text.split_whitespace().collect::<Vec<_>>().join(" ").chars().take(400).collect();
            (format!("{KEY_PREFIX}{}", e.id), preview(&e.text), format!("Copied {}: {body}", kind_of(&e.text).to_lowercase()))
        })
        .collect()
}

fn hit(e: &Entry, now: i64, score: i32) -> Hit {
    let kind = kind_of(&e.text);
    let lines = e.text.trim().lines().count();
    let what = if kind == "Text" && lines > 1 { format!("Text, {lines} lines") } else { kind.to_string() };
    Hit {
        title: preview(&e.text),
        subtitle: format!("{what}  \u{b7}  {}", ago(now - e.at)),
        kind: Kind::Clip,
        action: Action::Copy(e.text.clone()),
        score,
    }
}

/// The text on one line, cut to fit a row.
fn preview(text: &str) -> String {
    let one: String = text.split_whitespace().collect::<Vec<_>>().join(" ");
    if one.chars().count() > 100 {
        let cut: String = one.chars().take(99).collect();
        format!("{}\u{2026}", cut.trim_end())
    } else {
        one
    }
}

fn now() -> i64 {
    std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

fn ago(secs: i64) -> String {
    match secs {
        s if s < 45 => "just now".into(),
        s if s < 90 * 60 => format!("{} min ago", ((s + 30) / 60).max(1)),
        s if s < 22 * 3600 => format!("{} h ago", (s + 1800) / 3600),
        s if s < 36 * 3600 => "yesterday".into(),
        s => format!("{} days ago", (s + 43_200) / 86_400),
    }
}

/// What a copied text is, in a word: URL, Email, Path, Colour, Phone, Number, JSON or Text.
pub fn kind_of(text: &str) -> &'static str {
    let t = text.trim();
    let one_word = !t.is_empty() && !t.contains(char::is_whitespace);
    let l = t.to_lowercase();
    if one_word && (l.starts_with("http://") || l.starts_with("https://") || l.starts_with("www.")) {
        return "URL";
    }
    if one_word && is_email(t) {
        return "Email";
    }
    let b = t.as_bytes();
    if !t.contains('\n') && (t.starts_with("\\\\") || (b.len() > 3 && b[0].is_ascii_alphabetic() && &b[1..3] == b":\\")) {
        return "Path";
    }
    if is_colour(&l) {
        return "Colour";
    }
    if !t.is_empty() && t.chars().all(|c| c.is_ascii_digit() || " +-().".contains(c)) {
        let digits = t.chars().filter(char::is_ascii_digit).count();
        if (7..=15).contains(&digits) && (t.starts_with('+') || t.contains([' ', '-', '(']) || digits >= 9) {
            return "Phone";
        }
    }
    if t.replace([' ', ','], "").parse::<f64>().is_ok() {
        return "Number";
    }
    if (t.starts_with('{') || t.starts_with('[')) && serde_json::from_str::<serde_json::Value>(t).is_ok() {
        return "JSON";
    }
    "Text"
}

fn is_email(t: &str) -> bool {
    match t.split_once('@') {
        Some((local, domain)) => {
            !local.is_empty() && domain.contains('.') && !domain.starts_with('.') && !domain.ends_with('.') && !domain.contains('@')
        }
        None => false,
    }
}

fn is_colour(l: &str) -> bool {
    if let Some(hex) = l.strip_prefix('#') {
        return matches!(hex.len(), 3 | 4 | 6 | 8) && hex.chars().all(|c| c.is_ascii_hexdigit());
    }
    (l.starts_with("rgb(") || l.starts_with("rgba(") || l.starts_with("hsl(") || l.starts_with("hsla(")) && l.ends_with(')')
}

/// The text of a clipboard row's action, when it is a link that can be opened.
pub fn link_of(hit: &Hit) -> Option<String> {
    match (&hit.action, hit.kind) {
        (Action::Copy(t), Kind::Clip) if kind_of(t) == "URL" => {
            let t = t.trim();
            Some(if t.to_lowercase().starts_with("www.") { format!("https://{t}") } else { t.to_string() })
        }
        _ => None,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn what_was_copied_is_recognised() {
        assert_eq!(kind_of("https://example.com/a?b=1"), "URL");
        assert_eq!(kind_of("www.example.com"), "URL");
        assert_eq!(kind_of("see https://example.com"), "Text");
        assert_eq!(kind_of("me@example.com"), "Email");
        assert_eq!(kind_of(r"C:\Users\me\file.txt"), "Path");
        assert_eq!(kind_of(r"\\server\share"), "Path");
        assert_eq!(kind_of("#1e90ff"), "Colour");
        assert_eq!(kind_of("rgb(30, 144, 255)"), "Colour");
        assert_eq!(kind_of("+33 6 12 34 56 78"), "Phone");
        assert_eq!(kind_of("06 12 34 56 78"), "Phone");
        assert_eq!(kind_of("1234"), "Number");
        assert_eq!(kind_of("3.14"), "Number");
        assert_eq!(kind_of("{\"a\": [1, 2]}"), "JSON");
        assert_eq!(kind_of("{not json"), "Text");
        assert_eq!(kind_of("hello there"), "Text");
    }

    #[test]
    fn only_a_clipboard_word_asks_for_the_clipboard() {
        assert_eq!(ask("clip"), Some(""));
        assert_eq!(ask("Clipboard invoice number"), Some("invoice number"));
        assert_eq!(ask("copied   the link "), Some("the link"));
        assert_eq!(ask("clipchamp"), None);
        assert_eq!(ask("notepad"), None);
        assert_eq!(ask(""), None);
    }

    #[test]
    fn times_read_naturally() {
        assert_eq!(ago(10), "just now");
        assert_eq!(ago(120), "2 min ago");
        assert_eq!(ago(3 * 3600), "3 h ago");
        assert_eq!(ago(30 * 3600), "yesterday");
        assert_eq!(ago(3 * 86_400), "3 days ago");
    }

    #[test]
    fn the_list_keeps_newest_first_without_repeats() {
        // One test owns the shared store.
        STORE.lock().unwrap().clear();
        add("first note about tax", 100);
        add("https://example.com/second", 200);
        add("first note about tax", 300);
        add("   ", 400);
        let all = hits("clip");
        assert_eq!(all.len(), 2);
        assert_eq!(all[0].title, "first note about tax");
        assert_eq!(all[1].subtitle.split("  \u{b7}  ").next(), Some("URL"));
        assert_eq!(hits("clip tax").len(), 1);
        assert_eq!(hits("clip nothing like it").len(), 0);
        assert!(hits("tax").is_empty());
        let (key, _, text) = items().remove(1);
        assert!(text.starts_with("Copied url: "), "{text}");
        assert_eq!(hit_for_key(&key).map(|h| h.title), Some("https://example.com/second".into()));
        remove("first note about tax");
        assert_eq!(hits("clip").len(), 1);
        let long = "word ".repeat(100);
        add(&long, 500);
        assert!(hits("clip")[0].title.chars().count() <= 100);
    }
}
