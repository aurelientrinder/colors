//! palette-semantic: the semantic search tier, kept in its own process so the model's
//! memory and any crash while parsing a hostile PDF stay out of the launcher.
//!
//!   palette-semantic serve                 daemon used by the launcher (stdin/stdout JSON)
//!   palette-semantic embed-test            sanity check of the model and tokenizer
//!   palette-semantic index [--limit N]     run one indexing pass in the foreground
//!   palette-semantic query "text"          search the existing index
//!
//! Options: --data DIR  --model DIR  --home DIR

mod discover;
mod embed;
mod extract;
mod power;
mod serve;
mod store;

use serve::{Cfg, Shared};
use std::path::PathBuf;

fn arg(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned()
}

fn model_dir(args: &[String]) -> PathBuf {
    if let Some(p) = arg(args, "--model").or_else(|| std::env::var("PALETTE_MODEL_DIR").ok()) {
        return PathBuf::from(p);
    }
    // Newest first; the index is rebuilt automatically when the model changes.
    const NAMES: [&str; 2] = ["embeddinggemma-2-text-q4", "embeddinggemma-300m-q4"];
    let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(|p| p.to_path_buf()));
    let mut bases: Vec<PathBuf> = Vec::new();
    if let Some(d) = &exe_dir {
        bases.push(d.join("models"));
        // dev layout: <repo>\target\release\palette-semantic.exe
        bases.push(d.join("..").join("..").join("models"));
    }
    if let Some(l) = std::env::var_os("LOCALAPPDATA") {
        bases.push(PathBuf::from(l).join("palette").join("models"));
    }
    for name in NAMES {
        for base in &bases {
            let c = base.join(name);
            if c.join("model_q4.onnx").exists() {
                return c;
            }
        }
    }
    bases.first().map(|b| b.join(NAMES[0])).unwrap_or_default()
}

fn config(args: &[String]) -> Cfg {
    let data = arg(args, "--data").map(PathBuf::from).unwrap_or_else(|| {
        PathBuf::from(std::env::var_os("LOCALAPPDATA").unwrap_or_default()).join("palette")
    });
    let home = arg(args, "--home")
        .or_else(|| std::env::var("USERPROFILE").ok())
        .map(PathBuf::from)
        .unwrap_or_default();
    let cores = std::thread::available_parallelism().map(|n| n.get()).unwrap_or(4);
    let env_usize = |k: &str| std::env::var(k).ok().and_then(|v| v.parse::<usize>().ok());
    Cfg {
        data,
        model: model_dir(args),
        home,
        // Below-normal priority keeps this out of the way; 6 of 8 threads measured ~30% faster
        // than 4 on the dev machine, and indexing is a one-off background rebuild.
        threads: env_usize("PALETTE_SEM_THREADS").unwrap_or((cores * 3 / 4).max(2)),
        doc_tokens: env_usize("PALETTE_SEM_DOC_TOKENS").unwrap_or(224),
        max_chunks: env_usize("PALETTE_SEM_MAX_CHUNKS").unwrap_or(8),
        max_docs: env_usize("PALETTE_SEM_MAX_DOCS").unwrap_or(40_000),
        pass_limit: arg(args, "--limit").and_then(|v| v.parse().ok()),
        allow_battery: std::env::var_os("PALETTE_SEM_ON_BATTERY").is_some(),
    }
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let cmd = args.first().map(String::as_str).unwrap_or("serve");
    let cfg = config(&args);

    match cmd {
        "serve" => serve::serve(Shared::new(cfg)),
        "embed-test" => embed_test(&cfg),
        // Internal: PDF text extraction in a killable child process (see extract::pdf).
        "extract-pdf" => {
            if let Some(text) = args.get(1).and_then(|p| extract::pdf_in_process(std::path::Path::new(p))) {
                // The parent re-collapses and caps this; the limit only bounds the pipe.
                print!("{}", text.chars().take(400_000).collect::<String>());
            }
        }
        "index" => {
            eprintln!("model: {}", cfg.model.display());
            let sh = Shared::new(cfg);
            sh.run_pass(true);
        }
        "query" => {
            let q = args.get(1).cloned().unwrap_or_default();
            let sh = Shared::new(cfg);
            let t = std::time::Instant::now();
            match sh.query(&q, 10) {
                Ok(hits) => {
                    println!("({} ms)", t.elapsed().as_millis());
                    for (key, src, score) in hits {
                        println!("{score:.3}  [{src}] {key}");
                    }
                }
                Err(e) => eprintln!("error: {e}"),
            }
        }
        _ => eprintln!("usage: palette-semantic serve | embed-test | index [--limit N] | query \"text\""),
    }
}

fn embed_test(cfg: &Cfg) {
    use std::time::Instant;
    eprintln!("model dir: {}  threads: {}", cfg.model.display(), cfg.threads);
    let t = Instant::now();
    let mut e = match embed::Embedder::load(&cfg.model, cfg.threads) {
        Ok(e) => e,
        Err(err) => {
            eprintln!("load failed: {err}");
            std::process::exit(1);
        }
    };
    eprintln!("loaded in {} ms; inputs: {:?}", t.elapsed().as_millis(), e.input_names());

    let q = embed::query_prompt("how do I change the screen brightness");
    let docs = [
        ("brightness", "Display settings: adjust the brightness and colour of your screen"),
        ("recipe", "Recipe for chocolate chip cookies: flour, butter, sugar and eggs"),
        ("invoice", "Invoice number 2043 for consulting services, payable within 30 days"),
    ]
    .map(|(t, x)| embed::doc_prompt(t, x));

    let t = Instant::now();
    let qv = e.embed(&[q], 64).expect("embed query").pop().unwrap();
    eprintln!("query: {} ms, dim {}", t.elapsed().as_millis(), qv.len());
    let t = Instant::now();
    let dv = e.embed(&docs, 160).expect("embed docs");
    eprintln!("3 docs: {} ms", t.elapsed().as_millis());
    for (d, v) in docs.iter().zip(&dv) {
        let s: f32 = v.iter().zip(&qv).map(|(a, b)| a * b).sum();
        println!("{s:.3}  {}", &d[..d.len().min(60)]);
    }

    // Throughput on chunk-sized input: 16 chunks of ~900 characters, in groups of 8.
    let para = "The cooling system of the turbine uses air bled from the compressor stage to protect \
                the blades from the hot gas stream, and the flow is regulated by a series of valves. ";
    let chunks: Vec<String> = (0..16).map(|i| embed::doc_prompt("bench", &format!("{i} {}", para.repeat(5)))).collect();
    let _ = e.embed(&chunks[..2], 224); // warm-up
    for group in [4usize, 8, 16] {
        let t = Instant::now();
        for g in chunks.chunks(group) {
            e.embed(g, 224).expect("embed chunks");
        }
        let s = t.elapsed().as_secs_f64();
        eprintln!("groups of {group}: {:.1} chunks/s", chunks.len() as f64 / s);
    }
}
