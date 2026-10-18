//! Every word of every document, so a sentence copied from anywhere in a file finds the file.
//!
//! The meaning index embeds a few chunks of each document (eight of a hundred and fifty, for a long
//! PDF), which is right for "the paper about heat transfer" and hopeless for one exact sentence from
//! page 40. This is the other half: for each document a Bloom filter of its words (about ten bits a
//! distinct word, three probes), small enough to hold for thousands of documents. A query's words
//! are looked up in each filter; a document that has all of them (or nearly all, for a long query)
//! is a match, and a sentence of ten words or more is, in practice, only ever in the file it came
//! from. A filter can say yes wrongly (about 1 in 60 per word, so almost never for several words)
//! but never no.

use std::collections::{HashMap, HashSet};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};
use std::time::{Duration, Instant};

const MAGIC: &[u8; 4] = b"PLX1";
const BITS_PER_WORD: usize = 10;
const PROBES: u64 = 3;
/// Fewer words than this and the filters would match half the documents: left to the other tiers.
pub const MIN_QUERY_WORDS: usize = 3;
const FLUSH_EVERY: Duration = Duration::from_secs(30);

struct Entry {
    stamp: (u64, u64),
    /// Empty: the document has no readable text (it is not tried again until it changes).
    bits: Vec<u64>,
}

pub struct Lex {
    path: PathBuf,
    map: HashMap<String, Entry>,
    dirty: bool,
    flushed: Instant,
}

/// The distinct words of `text` worth indexing: lowercase, three letters or more.
pub fn words(text: &str) -> HashSet<String> {
    let mut out = HashSet::new();
    for w in text.split(|c: char| !c.is_alphanumeric()) {
        if w.chars().count() >= 3 {
            out.insert(w.to_lowercase());
        }
    }
    out
}

fn hashes(word: &str) -> (u64, u64) {
    let mut a = 0xcbf29ce484222325u64;
    let mut b = 0x84222325cbf29ce4u64;
    for byte in word.bytes() {
        a = (a ^ byte as u64).wrapping_mul(0x100000001b3);
        b = (b.rotate_left(5) ^ byte as u64).wrapping_mul(0x9e3779b97f4a7c15);
    }
    (a, b | 1)
}

fn probe(word: &str, nbits: usize) -> impl Iterator<Item = usize> {
    let (a, b) = hashes(word);
    (0..PROBES).map(move |i| (a.wrapping_add(i.wrapping_mul(b)) % nbits as u64) as usize)
}

fn build(text: &str) -> Vec<u64> {
    let set = words(text);
    if set.is_empty() {
        return Vec::new();
    }
    let nbits = (set.len() * BITS_PER_WORD).div_ceil(64).max(1) * 64;
    let mut bits = vec![0u64; nbits / 64];
    for w in &set {
        for p in probe(w, nbits) {
            bits[p / 64] |= 1 << (p % 64);
        }
    }
    bits
}

fn has(bits: &[u64], word: &str) -> bool {
    let nbits = bits.len() * 64;
    nbits > 0 && probe(word, nbits).all(|p| bits[p / 64] >> (p % 64) & 1 == 1)
}

impl Lex {
    pub fn open(path: &Path) -> Lex {
        let map = Self::read(path).unwrap_or_default();
        Lex { path: path.to_path_buf(), map, dirty: false, flushed: Instant::now() }
    }

    fn read(path: &Path) -> Option<HashMap<String, Entry>> {
        let mut buf = Vec::new();
        std::fs::File::open(path).ok()?.read_to_end(&mut buf).ok()?;
        if buf.len() < 8 || &buf[..4] != MAGIC {
            return None;
        }
        let mut at = 4usize;
        let take = |at: &mut usize, n: usize| -> Option<&[u8]> {
            let s = buf.get(*at..*at + n)?;
            *at += n;
            Some(s)
        };
        let u32_of = |s: &[u8]| u32::from_le_bytes(s.try_into().unwrap());
        let u64_of = |s: &[u8]| u64::from_le_bytes(s.try_into().unwrap());
        let count = u32_of(take(&mut at, 4)?) as usize;
        let mut map = HashMap::with_capacity(count);
        for _ in 0..count {
            let len = u32_of(take(&mut at, 4)?) as usize;
            let key = String::from_utf8(take(&mut at, len)?.to_vec()).ok()?;
            let stamp = (u64_of(take(&mut at, 8)?), u64_of(take(&mut at, 8)?));
            let n = u32_of(take(&mut at, 4)?) as usize;
            let raw = take(&mut at, n * 8)?;
            let bits = raw.chunks_exact(8).map(u64_of).collect();
            map.insert(key, Entry { stamp, bits });
        }
        Some(map)
    }

    /// The (modified, size) the document was read at, if it has been.
    pub fn stamp(&self, path: &str) -> Option<(u64, u64)> {
        self.map.get(path).map(|e| e.stamp)
    }

    /// Record the words of a document read at `stamp` (`None`: its text could not be read).
    pub fn set(&mut self, path: &str, stamp: (u64, u64), text: Option<&str>) {
        let bits = text.map(build).unwrap_or_default();
        self.map.insert(path.to_string(), Entry { stamp, bits });
        self.dirty = true;
    }

    /// Forget documents `keep` does not name (deleted files).
    pub fn retain(&mut self, keep: impl Fn(&str) -> bool) {
        let before = self.map.len();
        self.map.retain(|k, _| keep(k));
        self.dirty |= self.map.len() != before;
    }

    /// Write to disk, at most every half minute unless `force`: the file is a few megabytes.
    pub fn flush(&mut self, force: bool) {
        if !self.dirty || (!force && self.flushed.elapsed() < FLUSH_EVERY) {
            return;
        }
        let mut out = Vec::with_capacity(self.map.values().map(|e| e.bits.len() * 8 + 80).sum::<usize>() + 8);
        out.extend_from_slice(MAGIC);
        out.extend_from_slice(&(self.map.len() as u32).to_le_bytes());
        for (key, e) in &self.map {
            out.extend_from_slice(&(key.len() as u32).to_le_bytes());
            out.extend_from_slice(key.as_bytes());
            out.extend_from_slice(&e.stamp.0.to_le_bytes());
            out.extend_from_slice(&e.stamp.1.to_le_bytes());
            out.extend_from_slice(&(e.bits.len() as u32).to_le_bytes());
            for w in &e.bits {
                out.extend_from_slice(&w.to_le_bytes());
            }
        }
        let tmp = self.path.with_extension("tmp");
        if std::fs::File::create(&tmp).and_then(|mut f| f.write_all(&out)).is_ok() && std::fs::rename(&tmp, &self.path).is_ok() {
            self.dirty = false;
            self.flushed = Instant::now();
        }
    }

    /// Documents that have most of the words in `terms` (see `extract::query_terms`), best first: the
    /// words are weighed by how rare they are among the documents, so "plan" and "NPU" count for more
    /// than "model". At least two words, and three fifths of the weight, must be there. For a question
    /// that is not one exact sentence.
    pub fn rank(&self, terms: &[String], k: usize) -> Vec<String> {
        if terms.len() < 2 || self.map.is_empty() {
            return Vec::new();
        }
        let n = self.map.len() as f32;
        let df: Vec<f32> = terms.iter().map(|t| self.map.values().filter(|e| has(&e.bits, t)).count() as f32).collect();
        let weight: Vec<f32> = df.iter().map(|d| ((n + 1.0) / (d + 0.5)).ln()).collect();
        let total: f32 = weight.iter().sum();
        let mut scored: Vec<(f32, usize, &str)> = Vec::new();
        for (path, e) in &self.map {
            let mut got = 0.0;
            let mut count = 0;
            for (t, w) in terms.iter().zip(&weight) {
                if has(&e.bits, t) {
                    got += w;
                    count += 1;
                }
            }
            if count >= 2 && got >= 0.6 * total {
                scored.push((got, e.bits.len(), path));
            }
        }
        scored.sort_by(|a, b| b.0.total_cmp(&a.0).then(a.1.cmp(&b.1)));
        scored.into_iter().take(k).map(|(_, _, p)| p.to_string()).collect()
    }

    /// Documents that have the words of `query`, best first, as (path, score): 1.0 for all of its
    /// words, 0.9 for all but one of a long query's. A shorter document ranks above a longer one that
    /// has the same words (the sentence is more of it).
    pub fn search(&self, query: &str, k: usize) -> Vec<(String, f32)> {
        let mut q: Vec<String> = words(query).into_iter().collect();
        q.sort();
        if q.len() < MIN_QUERY_WORDS {
            return Vec::new();
        }
        let allowed_missing = if q.len() >= 8 { 1 } else { 0 };
        let mut found: Vec<(usize, usize, &str)> = Vec::new(); // (missing, filter size, path)
        for (path, e) in &self.map {
            if e.bits.is_empty() {
                continue;
            }
            let mut missing = 0;
            for w in &q {
                if !has(&e.bits, w) {
                    missing += 1;
                    if missing > allowed_missing {
                        break;
                    }
                }
            }
            if missing <= allowed_missing {
                found.push((missing, e.bits.len(), path));
            }
        }
        found.sort();
        found
            .into_iter()
            .take(k)
            .map(|(missing, _, path)| (path.to_string(), if missing == 0 { 1.0 } else { 0.9 }))
            .collect()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    const LEASE: &str = "The tenant shall pay the monthly rent of nine hundred euros on the first day of each month. \
        The lease ends on 31 August 2027 unless renewed in writing by both parties.";

    #[test]
    fn a_sentence_finds_the_document_that_has_it() {
        let mut lex = Lex { path: PathBuf::new(), map: HashMap::new(), dirty: false, flushed: Instant::now() };
        lex.set("lease.pdf", (1, 1), Some(LEASE));
        lex.set("recipe.pdf", (1, 1), Some("Whisk the eggs with sugar until pale, then fold in the flour and bake for forty minutes."));
        lex.set("empty.pdf", (1, 1), None);
        let hits = lex.search("The lease ends on 31 August 2027 unless renewed in writing", 5);
        assert_eq!(hits, vec![("lease.pdf".to_string(), 1.0)]);
        assert!(lex.search("whisk eggs sugar", 5).iter().all(|(p, _)| p == "recipe.pdf"));
        // Too few words: left to the other tiers.
        assert!(lex.search("lease ends", 5).is_empty());
        assert!(lex.search("zebra giraffe antelope", 5).is_empty());
    }

    #[test]
    fn a_question_ranks_documents_by_the_rare_words_they_have() {
        let mut lex = Lex { path: PathBuf::new(), map: HashMap::new(), dirty: false, flushed: Instant::now() };
        lex.set("lease.pdf", (1, 1), Some(LEASE));
        lex.set("notes.txt", (1, 1), Some("The tenant and the landlord met about the garden and the monthly newsletter."));
        lex.set("recipe.pdf", (1, 1), Some("Whisk the eggs with sugar until pale, then bake for forty minutes."));
        let terms: Vec<String> = ["tenant", "rent", "lease", "ends"].map(String::from).to_vec();
        let ranked = lex.rank(&terms, 5);
        assert_eq!(ranked.first().map(String::as_str), Some("lease.pdf"));
        assert!(!ranked.iter().any(|p| p == "recipe.pdf"));
        assert!(lex.rank(&["rent".to_string()], 5).is_empty());
    }

    #[test]
    fn one_wrong_word_in_a_long_sentence_still_finds_it() {
        let mut lex = Lex { path: PathBuf::new(), map: HashMap::new(), dirty: false, flushed: Instant::now() };
        lex.set("lease.pdf", (1, 1), Some(LEASE));
        let hits = lex.search("the tenant shall pay the monthly rent of nine hundred dollars on the first day", 5);
        assert_eq!(hits, vec![("lease.pdf".to_string(), 0.9)]);
    }

    #[test]
    fn the_index_survives_a_restart() {
        let path = std::env::temp_dir().join(format!("palette-lex-{}.plx", std::process::id()));
        let mut lex = Lex::open(&path);
        lex.set("lease.pdf", (5, 6), Some(LEASE));
        lex.flush(true);
        let again = Lex::open(&path);
        assert_eq!(again.stamp("lease.pdf"), Some((5, 6)));
        assert_eq!(again.search("monthly rent of nine hundred euros", 3).len(), 1);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn a_long_unrelated_document_rarely_matches() {
        // Ten thousand different words in one filter must not make it match everything.
        let text: String = (0..10_000).map(|i| format!("word{i} ")).collect();
        let mut lex = Lex { path: PathBuf::new(), map: HashMap::new(), dirty: false, flushed: Instant::now() };
        lex.set("big.txt", (1, 1), Some(&text));
        assert!(lex.search("quick brown fox jumps over lazy dog", 3).is_empty());
    }
}
