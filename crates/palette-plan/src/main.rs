//! palette-plan: the plan model (Gemma 3 270M fine-tuned in ml/intent) on the NPU.
//!
//! It answers the part of llama-server's HTTP interface that planner.rs in palette-core uses,
//! so either can be its helper: `GET /health`, and `POST /completion` with
//! `{"prompt": "request\n", "n_predict": 48}`, answered with `{"content": "plan line"}`.
//!
//!   palette-plan serve --port N [--model DIR]   serve on 127.0.0.1:N
//!   palette-plan compile [--model DIR]          compile for the NPU once (about a minute), cached
//!   palette-plan try "request" [--model DIR]    one plan, timed
//!
//! The model comes from ml\npu\export.py without its output layer: the NPU returns the last
//! hidden state, and only the tokens a plan can contain are scored here (every token of a
//! training plan, and the request's own, since names are written as typed): about 2.5k dot
//! products on the CPU instead of a 262k-row matrix on the NPU. Per token, the NPU then takes
//! about 3 ms, against 8 ms with the full output layer.

use std::fs::File;
use std::io::{BufRead, BufReader, Read, Write};
use std::net::TcpListener;
use std::os::windows::fs::FileExt;
use std::path::{Path, PathBuf};
use std::time::Instant;
use tokenizers::Tokenizer;

/// Longest request, in tokens (the launcher asks about 160 characters at most).
const MAX_PROMPT: usize = 64;
/// Longest plan, in tokens.
const MAX_PLAN: usize = 48;

/// What the NPU's LLM pipeline needs: the KV cache layout of the export, fixed prompt and reply
/// lengths, and the faster (slower to compile) build of the one-token step.
fn npu_props() -> Vec<(&'static str, String)> {
    vec![
        ("NPU_USE_NPUW", "YES".into()),
        ("NPUW_LLM", "YES".into()),
        ("NPUW_LLM_BATCH_DIM", "0".into()),
        ("NPUW_LLM_SEQ_LEN_DIM", "2".into()),
        ("NPUW_LLM_MAX_PROMPT_LEN", MAX_PROMPT.to_string()),
        ("NPUW_LLM_MIN_RESPONSE_LEN", MAX_PLAN.to_string()),
        ("NPUW_LLM_GENERATE_HINT", "BEST_PERF".into()),
        // The driver's compiler, so the 100 MB one in OpenVINO need not be shipped.
        ("NPU_COMPILER_TYPE", "DRIVER".into()),
    ]
}

/// The embedding table in the model's .bin: uint8 rows with a zero point and an f16 scale each.
/// It doubles as the output layer.
struct Table {
    bin: File,
    weight: u64,
    zero_point: u64,
    scale: u64,
    cols: usize,
}

impl Table {
    fn row(&self, id: u32) -> Result<Vec<f32>, String> {
        let id = id as u64;
        let mut w = vec![0u8; self.cols];
        let mut zp = [0u8; 1];
        let mut sc = [0u8; 2];
        let read = |buf: &mut [u8], at: u64| self.bin.seek_read(buf, at).map_err(|e| e.to_string());
        read(&mut w, self.weight + id * self.cols as u64)?;
        read(&mut zp, self.zero_point + id)?;
        read(&mut sc, self.scale + id * 2)?;
        let (zp, sc) = (zp[0] as f32, f16(u16::from_le_bytes(sc)));
        Ok(w.into_iter().map(|x| (x as f32 - zp) * sc).collect())
    }
}

fn f16(b: u16) -> f32 {
    let (s, e, m) = ((b >> 15) as u32, ((b >> 10) & 0x1f) as u32, (b & 0x3ff) as u32);
    let v = match e {
        0 => m as f32 * (1.0 / 16_777_216.0), // subnormal: m * 2^-24
        31 => f32::from_bits(0x7f80_0000 | (m << 13)),
        _ => f32::from_bits(((e + 112) << 23) | (m << 13)),
    };
    if s == 1 { -v } else { v }
}

fn dot(a: &[f32], b: &[f32]) -> f32 {
    // Eight running sums, so the loop vectorises.
    let mut acc = [0f32; 8];
    for (x, y) in a.chunks_exact(8).zip(b.chunks_exact(8)) {
        for i in 0..8 {
            acc[i] += x[i] * y[i];
        }
    }
    let tail: f32 = a.chunks_exact(8).remainder().iter().zip(b.chunks_exact(8).remainder()).map(|(x, y)| x * y).sum();
    acc.iter().sum::<f32>() + tail
}

struct Planner {
    tok: Tokenizer,
    table: Table,
    /// The plan tokens and their rows, one after another.
    ids: Vec<u32>,
    rows: Vec<f32>,
    eos: u32,
    newline: Vec<u32>,
    req: palette_ov::Request,
    _compiled: palette_ov::Compiled,
    _core: palette_ov::Core,
}

struct Plan {
    line: String,
    first_ms: f64,
    rest_ms: f64,
    tokens: usize,
}

fn key(xml: &Path) -> String {
    palette_ov::cache_key(xml, "plan")
}

impl Planner {
    fn load(dir: &Path) -> Result<Planner, String> {
        let xml = dir.join("openvino_model.xml");
        let meta: serde_json::Value = serde_json::from_str(
            &std::fs::read_to_string(dir.join("plan.json")).map_err(|e| format!("plan.json: {e}"))?,
        )
        .map_err(|e| format!("plan.json: {e}"))?;
        let tok = Tokenizer::from_file(dir.join("tokenizer.json")).map_err(|e| format!("tokenizer: {e}"))?;
        let t = &meta["table"];
        let at = |k: &str| t[k]["offset"].as_u64().ok_or(format!("plan.json: no {k}"));
        let cols = t["weight"]["shape"][1].as_u64().ok_or("plan.json: no width")? as usize;
        let bin = File::open(dir.join("openvino_model.bin")).map_err(|e| format!("model: {e}"))?;
        let table = Table { bin, weight: at("weight")?, zero_point: at("zero_point")?, scale: at("scale")?, cols };
        let ids: Vec<u32> = meta["tokens"].as_array().ok_or("plan.json: no tokens")?.iter().filter_map(|v| v.as_u64()).map(|v| v as u32).collect();
        let mut rows = Vec::with_capacity(ids.len() * cols);
        for &id in &ids {
            rows.extend(table.row(id)?);
        }
        let eos = meta["eos"].as_u64().unwrap_or(1) as u32;
        let newline = meta["newline"].as_array().map(|a| a.iter().filter_map(|v| v.as_u64()).map(|v| v as u32).collect()).unwrap_or_default();

        let core = palette_ov::Core::new()?;
        if !core.has("NPU") {
            return Err("no NPU".into());
        }
        core.set(None, &[("CACHE_DIR", &palette_ov::cache_dir().to_string_lossy())])?;
        let model = core.read(&xml)?;
        let props = npu_props();
        let props: Vec<(&str, &str)> = props.iter().map(|(k, v)| (*k, v.as_str())).collect();
        let compiled = core.compile(&model, "NPU", &props)?;
        let req = compiled.request()?;
        palette_ov::set_cached(&key(&xml));
        Ok(Planner { tok, table, ids, rows, eos, newline, req, _compiled: compiled, _core: core })
    }

    /// The line the model writes after `prompt` (the request and a newline), greedily.
    fn plan(&mut self, prompt: &str, max_new: usize) -> Result<Plan, String> {
        let t0 = Instant::now();
        let enc = self.tok.encode(prompt, true).map_err(|e| e.to_string())?;
        let mut ids: Vec<u32> = enc.get_ids().to_vec();
        if ids.len() > MAX_PROMPT {
            // Keep the first token (BOS) and the end, which ends with the newline the model answers.
            let cut = ids.len() - (MAX_PROMPT - 1);
            ids.drain(1..1 + cut);
        }
        // The request's own tokens are candidates too.
        let cols = self.table.cols;
        let mut extra_ids: Vec<u32> = Vec::new();
        let mut extra_rows: Vec<f32> = Vec::new();
        for &id in &ids {
            if self.ids.binary_search(&id).is_err() && !extra_ids.contains(&id) {
                extra_ids.push(id);
                extra_rows.extend(self.table.row(id)?);
            }
        }

        let mut out: Vec<u32> = Vec::new();
        let mut first_ms = 0.0;
        let mut pos = 0usize;
        let mut feed: Vec<i64> = ids.iter().map(|&i| i as i64).collect();
        for step in 0..max_new.min(MAX_PLAN) {
            let n = feed.len();
            let positions: Vec<i64> = (pos..pos + n).map(|p| p as i64).collect();
            self.req.set_i64("input_ids", &[1, n as i64], &feed)?;
            self.req.set_i64("attention_mask", &[1, (pos + n) as i64], &vec![1; pos + n])?;
            self.req.set_i64("position_ids", &[1, n as i64], &positions)?;
            self.req.set_i32("beam_idx", &[1], &[0])?;
            self.req.infer()?;
            let (_, h) = self.req.output(0)?;
            let h = h.get(h.len().saturating_sub(cols)..).ok_or("empty output")?;
            if step == 0 {
                first_ms = t0.elapsed().as_secs_f64() * 1000.0;
            }

            let mut best = (f32::NEG_INFINITY, self.eos);
            for (id, row) in self.ids.iter().zip(self.rows.chunks_exact(cols)).chain(extra_ids.iter().zip(extra_rows.chunks_exact(cols))) {
                let s = dot(row, h);
                if s > best.0 {
                    best = (s, *id);
                }
            }
            let next = best.1;
            pos += n;
            if next == self.eos || self.newline.contains(&next) {
                break;
            }
            out.push(next);
            feed = vec![next as i64];
        }
        let line = self.tok.decode(&out, true).map_err(|e| e.to_string())?.trim().to_string();
        let total = t0.elapsed().as_secs_f64() * 1000.0;
        Ok(Plan { line, first_ms, rest_ms: total - first_ms, tokens: out.len() })
    }
}

// ---------------------------------------------------------------- HTTP, as llama-server

fn serve(planner: &mut Planner, port: u16) -> Result<(), String> {
    let listener = TcpListener::bind(("127.0.0.1", port)).map_err(|e| e.to_string())?;
    for stream in listener.incoming() {
        let Ok(mut s) = stream else { continue };
        let _ = s.set_nodelay(true);
        let Some((method, path, body)) = read_request(&mut s) else { continue };
        let (status, reply) = match (method.as_str(), path.as_str()) {
            ("GET", "/health") => ("200 OK", serde_json::json!({"status": "ok"})),
            ("POST", "/completion") => {
                let v: serde_json::Value = serde_json::from_str(&body).unwrap_or_default();
                let prompt = v["prompt"].as_str().unwrap_or("");
                let n = v["n_predict"].as_u64().unwrap_or(MAX_PLAN as u64) as usize;
                match planner.plan(prompt, n) {
                    Ok(p) => (
                        "200 OK",
                        serde_json::json!({
                            "content": p.line,
                            "tokens_predicted": p.tokens,
                            "timings": {"prompt_ms": p.first_ms, "predicted_ms": p.rest_ms},
                        }),
                    ),
                    Err(e) => ("500 Internal Server Error", serde_json::json!({"error": e})),
                }
            }
            _ => ("404 Not Found", serde_json::json!({"error": "not found"})),
        };
        let body = reply.to_string();
        // One write, then the end of the stream: the client reads until it.
        let msg = format!(
            "HTTP/1.1 {status}\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{body}",
            body.len()
        );
        let _ = s.write_all(msg.as_bytes());
        let _ = s.shutdown(std::net::Shutdown::Write);
    }
    Ok(())
}

fn read_request(s: &mut std::net::TcpStream) -> Option<(String, String, String)> {
    let mut r = BufReader::new(s.try_clone().ok()?);
    let mut line = String::new();
    r.read_line(&mut line).ok()?;
    let mut parts = line.split_whitespace();
    let (method, path) = (parts.next()?.to_string(), parts.next()?.to_string());
    let mut len = 0usize;
    loop {
        let mut h = String::new();
        if r.read_line(&mut h).ok()? == 0 || h == "\r\n" || h == "\n" {
            break;
        }
        if let Some((k, v)) = h.split_once(':') {
            if k.trim().eq_ignore_ascii_case("content-length") {
                len = v.trim().parse().ok()?;
            }
        }
    }
    let mut body = vec![0u8; len.min(1 << 20)];
    r.read_exact(&mut body).ok()?;
    Some((method, path, String::from_utf8_lossy(&body).into_owned()))
}

// ---------------------------------------------------------------- command line

fn arg(args: &[String], name: &str) -> Option<String> {
    args.iter().position(|a| a == name).and_then(|i| args.get(i + 1)).cloned()
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let dir = arg(&args, "--model")
        .map(PathBuf::from)
        .or_else(|| palette_ov::find_models("planner-npu", "openvino_model.xml"))
        .unwrap_or_default();
    let t = Instant::now();
    let mut planner = match Planner::load(&dir) {
        Ok(p) => p,
        Err(e) => {
            eprintln!("palette-plan: {e}");
            std::process::exit(2);
        }
    };
    eprintln!("ready on the NPU in {} ms", t.elapsed().as_millis());
    match args.first().map(String::as_str) {
        Some("compile") => {}
        Some("serve") => {
            let port = arg(&args, "--port").and_then(|p| p.parse().ok()).unwrap_or(8089);
            if let Err(e) = serve(&mut planner, port) {
                eprintln!("palette-plan: {e}");
                std::process::exit(1);
            }
        }
        Some("try") => {
            let q = args.get(1).cloned().unwrap_or_default();
            for _ in 0..5 {
                match planner.plan(&format!("{}\n", q.trim()), MAX_PLAN) {
                    Ok(p) => println!("{}   ({:.1} ms + {:.1} ms, {} tokens)", p.line, p.first_ms, p.rest_ms, p.tokens),
                    Err(e) => eprintln!("error: {e}"),
                }
            }
        }
        _ => eprintln!("usage: palette-plan serve --port N | compile | try \"request\"  [--model DIR]"),
    }
}
