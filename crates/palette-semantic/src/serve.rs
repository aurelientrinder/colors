//! Shared state, the background indexing pass and the stdin/stdout query daemon.
//!
//! Protocol: one JSON object per line.
//!   in : {"op":"query","id":1,"q":"...","k":6}
//!        {"op":"items","items":[{"key":"...","title":"...","text":"..."}]}
//!        {"op":"ping"} | {"op":"shutdown"}
//!   out: {"id":1,"ms":23,"hits":[{"src":"doc"|"item","key":"...","score":0.53}]}

use crate::discover::{self, Candidate};
use crate::embed::{self, Embedder};
use crate::extract;
use crate::store::Store;
use serde::Deserialize;
use std::collections::HashSet;
use std::io::BufRead;
use std::path::{Path, PathBuf};
use std::sync::atomic::{AtomicBool, AtomicU64, AtomicUsize, Ordering};
use std::sync::{Arc, Condvar, Mutex, RwLock};
use std::time::{Duration, Instant, SystemTime, UNIX_EPOCH};

const BATCH: usize = 4;
const QUERY_TOKENS: usize = 64;
const ITEM_TOKENS: usize = 48;
/// Chunks per model call: bounds how long a query can wait behind the background indexer.
const EMBED_GROUP: usize = 8;
/// Bump when how documents are cut into vectors changes; the index file name carries it, so
/// an older layout is dropped and rebuilt.
const SCHEME: &str = "c1";
/// Chunk `n > 0` of a document is stored under `<path>\u{1}<n>`; chunk 0 under the bare path.
const CHUNK_SEP: char = '\u{1}';

fn base_path(key: &str) -> &str {
    key.split(CHUNK_SEP).next().unwrap_or(key)
}

fn chunk_key(path: &str, n: usize) -> String {
    if n == 0 {
        path.to_string()
    } else {
        format!("{path}{CHUNK_SEP}{n}")
    }
}
const RESCAN_EVERY: Duration = Duration::from_secs(30 * 60);
const UNLOAD_AFTER_SECS: u64 = 300;
const EXIT_AFTER_SECS: u64 = 20 * 60;

#[derive(Clone)]
pub struct Cfg {
    pub data: PathBuf,
    pub model: PathBuf,
    pub home: PathBuf,
    pub threads: usize,
    pub device: crate::embed::Device,
    /// Tokens per chunk (the model truncates beyond this).
    pub doc_tokens: usize,
    /// Chunks kept per document.
    pub max_chunks: usize,
    pub max_docs: usize,
    /// Stop a pass after this many documents (benchmarking).
    pub pass_limit: Option<usize>,
    pub allow_battery: bool,
}

impl Cfg {
    pub fn dir(&self) -> PathBuf {
        let d = self.data.join("semantic");
        let _ = std::fs::create_dir_all(&d);
        d
    }
}

#[derive(Default, Clone)]
struct Status {
    phase: &'static str,
    indexed: usize,
    total: usize,
    skipped: usize,
}

pub struct Shared {
    pub cfg: Cfg,
    emb: Mutex<Option<Embedder>>,
    waiting: AtomicUsize,
    used_at: AtomicU64,
    activity: AtomicU64,
    docs: RwLock<Store>,
    /// One vector per document from the previous index layout; consulted for documents the
    /// new index has not reached yet, then dropped.
    legacy: RwLock<Option<Store>>,
    items: RwLock<Store>,
    status: Mutex<Status>,
    stop: AtomicBool,
    /// Paths the launcher's file watcher reported as created or modified.
    touched: Mutex<Vec<String>>,
    touched_cv: Condvar,
    /// Indexing cost accounting (reported by `palette-semantic index`).
    t_extract_ms: AtomicU64,
    t_embed_ms: AtomicU64,
    n_chunks: AtomicU64,
}

pub fn now_secs() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

struct Waiter<'a>(&'a AtomicUsize);
impl Drop for Waiter<'_> {
    fn drop(&mut self) {
        self.0.fetch_sub(1, Ordering::SeqCst);
    }
}

fn fnv(s: &str) -> u64 {
    let mut h = 0xcbf29ce484222325u64;
    for b in s.bytes() {
        h ^= b as u64;
        h = h.wrapping_mul(0x100000001b3);
    }
    h
}

impl Shared {
    pub fn new(cfg: Cfg) -> Arc<Shared> {
        let dir = cfg.dir();
        // One index per model: vectors from different models are not comparable, so files
        // that belong to another model are dropped and rebuilt.
        let tag = cfg.model.file_name().and_then(|n| n.to_str()).unwrap_or("model").to_string();
        let docs_file = format!("docs-{tag}-{SCHEME}.psm");
        let items_file = format!("items-{tag}.psm");
        // The same model's index from before chunking stays readable until the new one has
        // caught up, so semantic document search keeps working during the rebuild.
        let legacy_file = format!("docs-{tag}.psm");
        if let Ok(rd) = std::fs::read_dir(&dir) {
            for e in rd.flatten() {
                let n = e.file_name().to_string_lossy().to_string();
                if (n.starts_with("docs") || n.starts_with("items"))
                    && n.ends_with(".psm")
                    && n != docs_file
                    && n != items_file
                    && n != legacy_file
                {
                    let _ = std::fs::remove_file(e.path());
                }
            }
        }
        let legacy = dir.join(&legacy_file);
        let legacy = legacy.exists().then(|| Store::open(&legacy));
        Arc::new(Shared {
            legacy: RwLock::new(legacy),
            docs: RwLock::new(Store::open(&dir.join(docs_file))),
            items: RwLock::new(Store::open(&dir.join(items_file))),
            cfg,
            emb: Mutex::new(None),
            waiting: AtomicUsize::new(0),
            used_at: AtomicU64::new(now_secs()),
            activity: AtomicU64::new(now_secs()),
            status: Mutex::new(Status { phase: "starting", ..Default::default() }),
            stop: AtomicBool::new(false),
            touched: Mutex::new(Vec::new()),
            touched_cv: Condvar::new(),
            t_extract_ms: AtomicU64::new(0),
            t_embed_ms: AtomicU64::new(0),
            n_chunks: AtomicU64::new(0),
        })
    }

    /// Run `f` with the model, loading it first if needed. Interactive callers (queries)
    /// jump ahead of the background indexer, which yields between batches.
    pub fn with_embedder<R>(&self, interactive: bool, f: impl FnOnce(&mut Embedder) -> R) -> Result<R, String> {
        let _w = if interactive {
            self.waiting.fetch_add(1, Ordering::SeqCst);
            Some(Waiter(&self.waiting))
        } else {
            while self.waiting.load(Ordering::SeqCst) > 0 {
                std::thread::sleep(Duration::from_millis(10));
            }
            None
        };
        let mut g = self.emb.lock().unwrap_or_else(|e| e.into_inner());
        if g.is_none() {
            let mut e = Embedder::load(&self.cfg.model, self.cfg.threads, self.cfg.device)?;
            if e.device != embed::Device::Cpu {
                // DirectML compiles kernels for each new input shape; do the query shape now, so
                // the first real query does not wait for it.
                let _ = e.embed(&[embed::query_prompt("warm up")], QUERY_TOKENS);
            }
            *g = Some(e);
        }
        let r = f(g.as_mut().unwrap());
        self.used_at.store(now_secs(), Ordering::Relaxed);
        Ok(r)
    }

    fn set_status(&self, f: impl FnOnce(&mut Status)) {
        let snapshot = {
            let mut s = self.status.lock().unwrap();
            f(&mut s);
            s.clone()
        };
        let json = serde_json::json!({
            "phase": snapshot.phase,
            "indexed": snapshot.indexed,
            "total": snapshot.total,
            "skipped": snapshot.skipped,
            "items": self.items.read().map(|i| i.len()).unwrap_or(0),
            "updated": now_secs(),
        });
        let _ = std::fs::write(self.cfg.dir().join("status.json"), json.to_string());
    }

    fn phase_is_idle(&self) -> bool {
        self.status.lock().unwrap().phase == "idle"
    }

    // ------------------------------------------------------------ indexing

    fn bad_path(&self) -> PathBuf {
        self.cfg.dir().join("bad.txt")
    }

    /// Files that crashed or hung an earlier run are never touched again.
    fn load_bad(&self) -> HashSet<String> {
        let journal = self.cfg.dir().join("current.txt");
        if let Ok(text) = std::fs::read_to_string(&journal) {
            use std::io::Write;
            if let Ok(mut f) = std::fs::OpenOptions::new().create(true).append(true).open(self.bad_path()) {
                for p in text.lines().map(str::trim).filter(|p| !p.is_empty()) {
                    let _ = writeln!(f, "{p}");
                }
            }
            let _ = std::fs::remove_file(&journal);
        }
        std::fs::read_to_string(self.bad_path())
            .map(|s| s.lines().map(str::to_owned).collect())
            .unwrap_or_default()
    }

    /// Extract and embed one batch of documents. `skipped` counts those indexed by name only.
    fn index_batch(&self, chunk: &[&Candidate], skipped: &mut usize) -> Result<(), String> {
        let journal = self.cfg.dir().join("current.txt");

        // Extract the whole batch in parallel; PDFs already run in their own (killable)
        // child processes. The journal names the in-process files so a crash blames them.
        let in_process: Vec<&str> = chunk
            .iter()
            .map(|c| c.path.as_str())
            .filter(|p| !p.to_ascii_lowercase().ends_with(".pdf"))
            .collect();
        let _ = std::fs::write(&journal, in_process.join("\n"));
        let t_ext = Instant::now();
        let extracted: Vec<Option<String>> = std::thread::scope(|s| {
            let handles: Vec<_> = chunk
                .iter()
                .map(|c| s.spawn(move || extract::extract(Path::new(&c.path))))
                .collect();
            handles.into_iter().map(|h| h.join().ok().flatten()).collect()
        });
        let _ = std::fs::remove_file(&journal);
        self.t_extract_ms.fetch_add(t_ext.elapsed().as_millis() as u64, Ordering::Relaxed);

        // Every document is searchable. When the body could not be read in time (big or
        // scanned PDFs), the file name alone is embedded: papers are usually named by title.
        // Otherwise the text is cut into chunks, each embedded with the title as context.
        let mut texts: Vec<String> = Vec::new();
        let mut owner: Vec<(usize, usize)> = Vec::new(); // (doc in this batch, chunk number)
        for (d, (c, text)) in chunk.iter().zip(&extracted).enumerate() {
            let stem = Path::new(&c.path).file_stem().and_then(|s| s.to_str()).unwrap_or("none");
            let title = stem.replace(['_', '-'], " ");
            let pieces = match text {
                Some(t) => extract::chunk(t, self.cfg.max_chunks),
                None => {
                    *skipped += 1;
                    vec!["none".to_string()]
                }
            };
            for (n, piece) in pieces.iter().enumerate() {
                texts.push(embed::doc_prompt(&title, piece));
                owner.push((d, n));
            }
        }
        if texts.is_empty() {
            return Ok(());
        }

        let t_emb = Instant::now();
        let mut vecs: Vec<Vec<f32>> = Vec::with_capacity(texts.len());
        for group in texts.chunks(EMBED_GROUP) {
            vecs.extend(self.with_embedder(false, |e| e.embed(group, self.cfg.doc_tokens))??);
        }
        self.t_embed_ms.fetch_add(t_emb.elapsed().as_millis() as u64, Ordering::Relaxed);
        self.n_chunks.fetch_add(texts.len() as u64, Ordering::Relaxed);

        // Chunk 0 is written last: it carries the stamp that marks the document as done, so a
        // crash half-way leaves the document pending rather than half-indexed.
        let mut docs = self.docs.write().unwrap();
        for (d, c) in chunk.iter().enumerate() {
            for (i, &(od, n)) in owner.iter().enumerate().rev() {
                if od == d && n != 0 {
                    docs.upsert(&chunk_key(&c.path, n), c.mtime_ms, c.size, &vecs[i]);
                }
            }
            if let Some(i) = owner.iter().position(|&(od, n)| od == d && n == 0) {
                docs.upsert(&c.path, c.mtime_ms, c.size, &vecs[i]);
            }
        }
        Ok(())
    }

    /// Queue paths the launcher saw change; the indexer picks them up within a second.
    pub fn touch(&self, paths: Vec<String>) {
        let mut q = self.touched.lock().unwrap();
        q.extend(paths);
        self.touched_cv.notify_one();
    }

    /// Wait up to `wait` for touched paths and take them all.
    fn take_touched(&self, wait: Duration) -> Vec<String> {
        let mut q = self.touched.lock().unwrap();
        if q.is_empty() {
            q = self.touched_cv.wait_timeout(q, wait).unwrap().0;
        }
        std::mem::take(&mut *q)
    }

    /// Incremental indexing: only the given files, filtered by the same rules as a full pass.
    fn index_paths(&self, paths: Vec<String>) {
        let bad = std::fs::read_to_string(self.bad_path()).unwrap_or_default();
        let bad: HashSet<&str> = bad.lines().collect();
        let home_l = self.cfg.home.display().to_string().to_lowercase();

        let mut seen = HashSet::new();
        let mut todo: Vec<Candidate> = Vec::new();
        {
            let docs = self.docs.read().unwrap();
            for p in paths {
                if !seen.insert(p.to_lowercase()) || bad.contains(p.as_str()) {
                    continue;
                }
                if let Some(c) = discover::accept(&p, &home_l) {
                    if docs.stamp(&c.path) != Some((c.mtime_ms, c.size)) {
                        todo.push(c);
                    }
                }
            }
        }
        if todo.is_empty() {
            return;
        }
        // On battery the next full pass (on AC power) picks these up instead.
        if !self.cfg.allow_battery && crate::power::on_battery() {
            return;
        }
        let refs: Vec<&Candidate> = todo.iter().collect();
        let mut skipped = 0;
        for chunk in refs.chunks(BATCH) {
            if self.stop.load(Ordering::Relaxed) {
                return;
            }
            if let Err(e) = self.index_batch(chunk, &mut skipped) {
                eprintln!("incremental embed failed: {e}");
                return;
            }
        }
        let mut docs = self.docs.write().unwrap();
        docs.flush();
        let n = docs.len();
        drop(docs);
        self.set_status(|s| {
            if s.phase == "idle" {
                s.indexed = n;
                s.total = s.total.max(n);
            }
        });
    }

    pub fn run_pass(&self, verbose: bool) {
        self.set_status(|s| s.phase = "scanning");
        let bad = self.load_bad();
        let cands = discover::discover(&self.cfg.data, &self.cfg.home, self.cfg.max_docs);
        // Keep a chunk only while its file exists and the chunk comes from the file's current
        // version (a file that shrank leaves higher-numbered chunks of an older version behind).
        let live: std::collections::HashMap<&str, (u64, u64)> =
            cands.iter().map(|c| (c.path.as_str(), (c.mtime_ms, c.size))).collect();
        self.docs.write().unwrap().retain(|k, stamp| {
            let base = base_path(k);
            if k.len() == base.len() {
                live.contains_key(k)
            } else {
                live.get(base) == Some(&stamp)
            }
        });

        let todo: Vec<&Candidate> = {
            let docs = self.docs.read().unwrap();
            cands
                .iter()
                .filter(|c| !bad.contains(&c.path) && docs.stamp(&c.path) != Some((c.mtime_ms, c.size)))
                .collect()
        };
        let total = cands.len();
        let mut indexed = total - todo.len();
        let mut skipped = 0usize;
        self.set_status(|s| {
            s.phase = if todo.is_empty() { "idle" } else { "indexing" };
            s.total = total;
            s.indexed = indexed;
        });
        if verbose {
            eprintln!("{total} documents, {} to index", todo.len());
        }

        let t_start = Instant::now();
        let mut last_status = Instant::now();
        let limit = self.cfg.pass_limit.unwrap_or(usize::MAX);
        let mut done_this_pass = 0usize;

        let mut completed = true;
        for chunk in todo.chunks(BATCH) {
            if self.stop.load(Ordering::Relaxed) || done_this_pass >= limit {
                completed = false;
                break;
            }
            // Battery friendly: wait for AC power.
            while !self.cfg.allow_battery && crate::power::on_battery() && !self.stop.load(Ordering::Relaxed) {
                self.set_status(|s| s.phase = "paused");
                std::thread::sleep(Duration::from_secs(20));
            }
            self.set_status(|s| s.phase = "indexing");

            if let Err(e) = self.index_batch(chunk, &mut skipped) {
                eprintln!("embed failed: {e}");
                completed = false;
                if verbose {
                    return;
                }
                std::thread::sleep(Duration::from_secs(5));
            }
            indexed += chunk.len();
            done_this_pass += chunk.len();
            if last_status.elapsed() > Duration::from_secs(2) {
                last_status = Instant::now();
                self.docs.write().unwrap().flush();
                self.set_status(|s| {
                    s.indexed = indexed;
                    s.skipped = skipped;
                });
                if verbose {
                    let rate = done_this_pass as f64 / t_start.elapsed().as_secs_f64();
                    eprintln!("  {indexed}/{total}  {rate:.1} docs/s");
                }
            }
        }
        self.docs.write().unwrap().flush();
        if completed {
            // Every document now has chunk vectors; the old single-vector index is obsolete.
            if let Some(old) = self.legacy.write().unwrap().take() {
                drop(old);
                let tag = self.cfg.model.file_name().and_then(|n| n.to_str()).unwrap_or("model");
                let _ = std::fs::remove_file(self.cfg.dir().join(format!("docs-{tag}.psm")));
            }
        }
        self.set_status(|s| {
            s.phase = "idle";
            s.indexed = indexed;
            s.skipped = skipped;
        });
        if verbose {
            let secs = t_start.elapsed().as_secs_f64().max(0.001);
            eprintln!(
                "pass done: {done_this_pass} documents in {secs:.1}s ({:.1}/s), {skipped} by name only",
                done_this_pass as f64 / secs
            );
            let chunks = self.n_chunks.load(Ordering::Relaxed);
            let ext = self.t_extract_ms.load(Ordering::Relaxed) as f64 / 1000.0;
            let emb = self.t_embed_ms.load(Ordering::Relaxed) as f64 / 1000.0;
            eprintln!(
                "  {chunks} chunks ({:.1}/doc); extraction {ext:.1}s (parallel x{BATCH}), embedding {emb:.1}s ({:.1} chunks/s)",
                chunks as f64 / done_this_pass.max(1) as f64,
                chunks as f64 / emb.max(0.001)
            );
        }
    }

    // --------------------------------------------------------------- items

    pub fn index_items(&self, items: &[ItemIn]) {
        let todo: Vec<&ItemIn> = {
            let store = self.items.read().unwrap();
            items.iter().filter(|i| store.stamp(&i.key).map(|s| s.0) != Some(fnv(&i.text))).collect()
        };
        for chunk in todo.chunks(16) {
            if self.stop.load(Ordering::Relaxed) {
                return;
            }
            let texts: Vec<String> = chunk.iter().map(|i| embed::doc_prompt(&i.title, &i.text)).collect();
            match self.with_embedder(false, |e| e.embed(&texts, ITEM_TOKENS)) {
                Ok(Ok(vecs)) => {
                    let mut store = self.items.write().unwrap();
                    for (i, v) in chunk.iter().zip(vecs) {
                        store.upsert(&i.key, fnv(&i.text), 0, &v);
                    }
                }
                Ok(Err(e)) | Err(e) => {
                    eprintln!("item embed failed: {e}");
                    return;
                }
            }
        }
        let keys: HashSet<&str> = items.iter().map(|i| i.key.as_str()).collect();
        let mut store = self.items.write().unwrap();
        store.retain(|k, _| keys.contains(k));
        store.flush();
    }

    // --------------------------------------------------------------- query

    pub fn query(&self, q: &str, k: usize) -> Result<Vec<(String, &'static str, f32)>, String> {
        let v = self
            .with_embedder(true, |e| e.embed(&[embed::query_prompt(q)], QUERY_TOKENS))?
            .map_err(|e| e)?
            .pop()
            .ok_or("empty embedding")?;
        let mut out: Vec<(String, &'static str, f32)> = Vec::new();
        {
            // A document is as relevant as its best chunk. Ask for several times `k` chunks so
            // one long document's chunks cannot crowd out every other file.
            let docs = self.docs.read().unwrap();
            let mut best: Vec<(String, f32)> = Vec::new();
            for (key, s) in docs.search(&v, k * 8) {
                let base = base_path(key);
                // Chunks left over from an older version of the file do not count.
                if base.len() != key.len() && docs.stamp(base) != docs.stamp(key) {
                    continue;
                }
                if !best.iter().any(|(b, _)| b == base) {
                    best.push((base.to_string(), s)); // results arrive best-first
                }
            }
            if let Some(old) = self.legacy.read().unwrap().as_ref() {
                for (key, s) in old.search(&v, k * 2) {
                    if docs.stamp(key).is_none() && !best.iter().any(|(b, _)| b == key) {
                        best.push((key.to_string(), s));
                    }
                }
                best.sort_by(|a, b| b.1.total_cmp(&a.1));
            }
            for (path, s) in best.into_iter().take(k) {
                out.push((path, "doc", s));
            }
        }
        for (key, s) in self.items.read().unwrap().search(&v, k) {
            out.push((key.to_string(), "item", s));
        }
        out.sort_by(|a, b| b.2.total_cmp(&a.2));
        out.truncate(k);
        Ok(out)
    }
}

#[derive(Deserialize)]
pub struct ItemIn {
    pub key: String,
    pub title: String,
    pub text: String,
}

#[derive(Deserialize)]
struct Req {
    op: String,
    id: Option<u64>,
    q: Option<String>,
    k: Option<usize>,
    items: Option<Vec<ItemIn>>,
    paths: Option<Vec<String>>,
}

pub fn serve(sh: Arc<Shared>) {
    crate::power::lower_priority();

    // Background indexer: a full scan now and every RESCAN_EVERY, plus incremental updates for
    // files the launcher's watcher reports as created or changed in between.
    {
        let sh = sh.clone();
        std::thread::spawn(move || {
            sh.run_pass(false);
            let mut next_pass = Instant::now() + RESCAN_EVERY;
            loop {
                if sh.stop.load(Ordering::Relaxed) {
                    return;
                }
                let batch = sh.take_touched(Duration::from_secs(1));
                if !batch.is_empty() {
                    sh.index_paths(batch);
                }
                if Instant::now() >= next_pass {
                    sh.run_pass(false);
                    next_pass = Instant::now() + RESCAN_EVERY;
                }
            }
        });
    }

    // Watchdog: free the model when unused, exit when the app has gone quiet.
    {
        let sh = sh.clone();
        std::thread::spawn(move || loop {
            std::thread::sleep(Duration::from_secs(15));
            let now = now_secs();
            if now.saturating_sub(sh.used_at.load(Ordering::Relaxed)) > UNLOAD_AFTER_SECS
                && sh.waiting.load(Ordering::SeqCst) == 0
            {
                if let Ok(mut g) = sh.emb.try_lock() {
                    *g = None;
                }
            }
            if now.saturating_sub(sh.activity.load(Ordering::Relaxed)) > EXIT_AFTER_SECS && sh.phase_is_idle() {
                sh.docs.write().unwrap().flush();
                std::process::exit(0);
            }
        });
    }

    let stdin = std::io::stdin();
    for line in stdin.lock().lines() {
        let Ok(line) = line else { break };
        let Ok(req) = serde_json::from_str::<Req>(&line) else { continue };
        // File-change notices are background noise: they must not keep an idle process alive.
        if req.op != "touch" {
            sh.activity.store(now_secs(), Ordering::Relaxed);
        }
        match req.op.as_str() {
            "touch" => {
                if let Some(paths) = req.paths {
                    sh.touch(paths);
                }
            }
            "query" => {
                let t0 = Instant::now();
                let id = req.id.unwrap_or(0);
                let reply = match sh.query(req.q.as_deref().unwrap_or(""), req.k.unwrap_or(6)) {
                    Ok(hits) => serde_json::json!({
                        "id": id,
                        "ms": t0.elapsed().as_millis() as u64,
                        "hits": hits.iter().map(|(k, src, s)| serde_json::json!({"src": src, "key": k, "score": s})).collect::<Vec<_>>(),
                    }),
                    Err(e) => serde_json::json!({"id": id, "error": e, "hits": []}),
                };
                println!("{reply}");
            }
            "items" => {
                if let Some(items) = req.items {
                    let sh = sh.clone();
                    std::thread::spawn(move || sh.index_items(&items));
                }
            }
            "ping" => println!("{}", serde_json::json!({"pong": true})),
            "shutdown" => break,
            _ => {}
        }
    }
    sh.stop.store(true, Ordering::Relaxed);
    sh.docs.write().unwrap().flush();
}
