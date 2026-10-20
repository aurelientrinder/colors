//! Asking about the window in front, or about your own documents: "summarise this", "what does this error mean", "translate
//! this". A language model that runs on this PC answers from the text the window shows (or the text
//! you selected), and the answer is streamed into the palette itself (the shell does the
//! streaming; this module readies everything it needs).
//!
//! Local, and visibly so: the row says "on this PC", the window's text goes to `llama-server`
//! listening on 127.0.0.1 and nowhere else, the model is loaded only when asked and let go after ten
//! idle minutes. Nothing is kept: the text is read when the question is asked and dropped once the
//! prompt is built.
//!
//! The model is any chat GGUF of a few billion parameters: `PALETTE_CHAT_MODEL`, or one in
//! `models\chat`, or one LM Studio already downloaded (Qwen3.5 4B is preferred, then Gemma 3 4B).
//! The runtime is llama.cpp's server, the GPU (Vulkan) build, in `models\chat` too
//! (`scripts\get-chat.ps1`).
//!
//! Speed, without taking the PC over:
//! * the long part of a question is the model reading the window (about 450 words a second on an
//!   Arc GPU, so six seconds for a long page). It is done ahead (`warm`): once a row offers an answer
//!   and typing pauses, the window is read and its text fed to the model, which keeps it in its cache.
//!   The text fed is exactly what comes before the question in the final prompt (the chat template is
//!   the server's own, from `/apply-template`), so Enter adds only the question's dozen tokens and the
//!   first words come in about a second. Qwen3.5 is a hybrid model whose cache can only go back to a
//!   saved point, and the end of that text is one. A second question about the same window reuses
//!   it all.
//! * the server runs below normal priority, which Windows also applies to its GPU work: whatever you
//!   are doing comes first, the model takes what is left. It uses two CPU threads (the GPU does the
//!   work) and flash attention (measured the quickest here).

use crate::model::{Action, Hit, Kind};
use std::ffi::OsString;
use std::path::{Path, PathBuf};
use std::process::Child;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// How long the model stays loaded after the last question.
const IDLE_STOP: Duration = Duration::from_secs(600);
/// Characters of window text sent: about three thousand tokens, which the GPU reads in ten seconds.
const WINDOW_CHARS: usize = 12_000;
/// Characters of a selection, and of the window around it when there is one.
const SELECTION_CHARS: usize = 6_000;
const AROUND_CHARS: usize = 3_000;
/// Longest a read of the window may take.
const READ_WAIT: Duration = Duration::from_secs(8);
/// A window read this lately is not read again for the next question.
const READ_FRESH: Duration = Duration::from_secs(60);
/// The pause in typing before the window is read ahead.
const WARM_AFTER: Duration = Duration::from_millis(400);

const SYSTEM_DOCS: &str = "You are Palette, an assistant built into the user's PC. The user asks a question about their own documents. \
Excerpts from the most relevant ones follow, numbered [1], [2], and so on; each is a piece of a longer file. \
Answer only from the excerpts, briefly and concretely, in the language of the question, and cite the excerpt behind each fact as [1] or [2]. \
If the excerpts do not answer it, say so plainly and say what they do cover. Never invent. \
Write in flowing prose, the way a person talks: never bullet points, numbered lists, headings, bold or tables, \
not even for steps, options or pros and cons (say them in sentences instead).";

const SYSTEM_CHAT: &str = "You are Palette, an assistant built into the user's PC, talking with them in a small chat box. \
Be brief and concrete: a few short sentences, unless asked for more. Answer in the language the user writes in. \
Write in flowing prose, the way a person talks: never bullet points, numbered lists, headings, bold or tables, \
not even for steps, options or pros and cons (say them in sentences instead). \
You may be told what is on the user's screen (text read from a window, which may hold interface noise) and, with a message, \
excerpts from the user's own documents (numbered [1], [2]; they may not be relevant: use them only when they help, and then cite them). \
Give your best answer straight away, warmly, without disclaimers about what you do not know of the user's situation; \
but about facts, if you do not know or cannot tell from what you have been given, say so instead of guessing.";

/// What the chat model may look up for itself, in the chat API's form. The shell runs the calls
/// (ChatTools.cs; find_files is tools.rs): files and documents become links in the answer's text, the
/// weather, the calendar and the PC's state cards under it.
pub const TOOLS: &str = r#"[
{"type":"function","function":{"name":"find_files","description":"Find files on the user's PC by name, by topic, or by when they were last opened or changed. Use it when the user asks for one of their files, e.g. the file they worked on last week, their CV, yesterday's photos. Each file you name in your answer becomes a link the user can click.","parameters":{"type":"object","properties":{"query":{"type":"string","description":"Words from the file's name or about its topic, not the kind of file (that is kind). Leave empty to list files by time alone."},"period":{"type":"string","enum":["today","yesterday","this_week","last_week","this_month","last_month","any"],"description":"When the file was opened or changed."},"kind":{"type":"string","enum":["any","document","spreadsheet","presentation","image","video","audio","code","3d"]}}}}},
{"type":"function","function":{"name":"search_documents","description":"Read the passages of the user's own documents (PDFs, Word files, notes) that answer a question about them: their contracts, invoices, manuals, papers, notes. Cite the passages as [1], [2]: each becomes a link to its document.","parameters":{"type":"object","properties":{"question":{"type":"string","description":"The question, in the user's words."}},"required":["question"]}}},
{"type":"function","function":{"name":"get_weather","description":"The weather now and today's high and low, shown to the user as a weather card.","parameters":{"type":"object","properties":{"place":{"type":"string","description":"A city. Leave empty for where the user is."}}}}},
{"type":"function","function":{"name":"get_system_status","description":"How the user's PC is doing now: processor and memory load, battery, free disk space, time since it started. Shown to the user as a card.","parameters":{"type":"object","properties":{}}}},
{"type":"function","function":{"name":"get_calendar","description":"The user's calendar events for a day or a week, shown to the user as a card.","parameters":{"type":"object","properties":{"period":{"type":"string","enum":["today","tomorrow","this_week","next_week"]},"date":{"type":"string","description":"A day as YYYY-MM-DD, instead of period."}}}}}
]"#;

/// How the model is to use its tools.
const TOOL_USE: &str = "Answer general questions and conversation from your own knowledge, and talk only about what the user asked. \
You also have tools to look things up on the user's PC (their files, their documents, the weather, their calendar, how their PC is doing): \
call one only when the request is about one of those, never otherwise, and never offer to look up what no tool reaches \
(recipes, the news, the web): answer from what you know. \
When you call a tool, first write one short sentence saying what you are about to do (I'll look through the files you opened \
last week.), with no claim about what you will find, then call it in the same turn; after its results, answer from them alone. \
When you mention a file a tool found, write its exact name with its extension (Report_Q3.docx) inside your sentence: the name becomes \
a link the user clicks to open it (they know: do not tell them). Mention only the files that answer the question, never paths, and never one per line. \
After a weather, calendar or PC tool, the user also sees what it found as a card: say in a sentence what matters to their question, without \
repeating every number. If a tool finds nothing, say so. When the user asks about something that may be written in their own papers \
(a lease, an invoice, a contract, a manual, notes), search their documents first instead of asking them where to look.";

/// The model's long-term memory: what it remembers comes with the instructions (the shell's MemoryStore,
/// which also notes what the user says about themselves, after each message, with a request of its own).
const MEMORY_USE: &str = "You have a long-term memory of the user: what you remember, if anything, is listed below, and what they tell you about themselves is noted for later conversations on its own, so you need do nothing to remember it. Use it when it helps; never recite it unasked.";

/// "Today is Monday 20 October 2026.", from the PC's clock.
fn today() -> String {
    use windows::Win32::System::SystemInformation::GetLocalTime;
    const DAYS: [&str; 7] = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    const MONTHS: [&str; 12] =
        ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];
    let t = unsafe { GetLocalTime() };
    format!(
        "Today is {} {} {} {}.",
        DAYS[(t.wDayOfWeek as usize).min(6)],
        t.wDay,
        MONTHS[(t.wMonth as usize).clamp(1, 12) - 1],
        t.wYear
    )
}

/// Names to prefer, best first, when several models are about.
/// Gemma 4 E4B first: in a comparison on the chat's kinds of messages (CHAT-PLAN.md, "Model choice") it wrote the
/// best answers and, with its MTP draft beside it, the quickest (about 25 words a second here against 19 for
/// Qwen3.5 4B), and picked its tools three times sooner.
const PREFERRED: [&str; 9] =
    ["gemma-4-e4b", "qwen3.5-4b", "qwen3-4b", "gemma-3-4b", "gemma-3n", "llama-3.2-3b", "phi-4-mini", "qwen3-8b", "gemma-3-12b"];

const SYSTEM: &str = "You are Palette, an assistant built into the user's PC. The user is looking at a window and asks about it. \
Answer from the text given; it is read from the screen, so it may hold interface noise (menus, buttons) around the content. \
Be brief and concrete: a few short sentences. Answer in the language of the question. \
Write in flowing prose, the way a person talks: never bullet points, numbered lists, headings, bold or tables, \
not even for steps, options or pros and cons (say them in sentences instead). \
If the text does not hold the answer, say what is missing instead of guessing.";

// ---------------------------------------------------------------------------------------------- the model

/// The chat model to use, if there is one.
pub fn model_path() -> Option<PathBuf> {
    if let Some(p) = std::env::var_os("PALETTE_CHAT_MODEL").map(PathBuf::from) {
        return p.exists().then_some(p);
    }
    let mut files: Vec<(PathBuf, u64)> = Vec::new();
    let mut add = |dir: &Path, depth: usize| gguf_in(dir, depth, &mut files);
    if let Some(d) = crate::paths::chat_dir() {
        add(&d, 1);
    }
    if let Some(home) = std::env::var_os("USERPROFILE").map(PathBuf::from) {
        add(&home.join(".lmstudio").join("models"), 3);
    }
    pick(files)
}

fn gguf_in(dir: &Path, depth: usize, out: &mut Vec<(PathBuf, u64)>) {
    let Ok(rd) = std::fs::read_dir(dir) else { return };
    for e in rd.flatten() {
        let p = e.path();
        let Ok(meta) = e.metadata() else { continue };
        if meta.is_dir() {
            if depth > 1 {
                gguf_in(&p, depth - 1, out);
            }
        } else if p.extension().is_some_and(|x| x.eq_ignore_ascii_case("gguf")) {
            out.push((p, meta.len()));
        }
    }
}

/// The best of `files`: a chat model of two to twelve gigabytes (not a vision projector), the
/// preferred names first.
fn pick(files: Vec<(PathBuf, u64)>) -> Option<PathBuf> {
    let mut ok: Vec<(PathBuf, u64)> = files
        .into_iter()
        .filter(|(p, size)| {
            let n = p.file_name().map(|n| n.to_string_lossy().to_lowercase()).unwrap_or_default();
            !n.contains("mmproj") && (1_500_000_000..=12_000_000_000).contains(size)
        })
        .collect();
    ok.sort_by_key(|(p, size)| {
        let n = p.file_name().map(|n| n.to_string_lossy().to_lowercase()).unwrap_or_default();
        (PREFERRED.iter().position(|k| n.contains(k)).unwrap_or(PREFERRED.len()), *size)
    });
    ok.into_iter().next().map(|(p, _)| p)
}

/// "Qwen3.5-4B-Q4_K_S" as "Qwen3.5 4B".
pub fn label(model: &Path) -> String {
    let stem = model.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    let parts: Vec<&str> = stem.split(['-', '_']).collect();
    // Everything from the quantisation on ("Q4_K_S") is left out.
    let end = parts
        .iter()
        .position(|p| {
            let l = p.to_lowercase();
            let rest = l.strip_prefix("iq").or_else(|| l.strip_prefix('q'));
            rest.is_some_and(|r| r.chars().next().is_some_and(|c| c.is_ascii_digit()))
        })
        .unwrap_or(parts.len());
    let mut parts = parts[..end].to_vec();
    while parts.last().is_some_and(|p| matches!(p.to_lowercase().as_str(), "it" | "instruct" | "gguf")) {
        parts.pop();
    }
    parts.join(" ")
}

/// The model's multi-token-prediction draft, when it sits beside it: `mtp-gemma-4-E4B-it.gguf` for
/// `gemma-4-E4B-it-Q4_0.gguf`. It guesses a few words ahead and the model checks them in one step,
/// which about doubles how fast the answer is written.
pub fn draft_path(model: &Path) -> Option<PathBuf> {
    let name = model.file_name()?.to_string_lossy().to_lowercase();
    std::fs::read_dir(model.parent()?).ok()?.flatten().map(|e| e.path()).find(|p| {
        let n = p.file_name().map(|n| n.to_string_lossy().to_lowercase()).unwrap_or_default();
        n.strip_prefix("mtp-").and_then(|rest| rest.strip_suffix(".gguf")).is_some_and(|stem| name.starts_with(stem))
    })
}

/// Is there a runtime and a model to ask?
pub fn available() -> bool {
    crate::paths::chat_dir().is_some() && model_path().is_some()
}

// ---------------------------------------------------------------------------------------------- the server

struct Server {
    child: Child,
    port: u16,
    model: String,
}

static SERVER: Mutex<Option<Server>> = Mutex::new(None);
static LAST_USED: Mutex<Option<Instant>> = Mutex::new(None);

fn touch() {
    *LAST_USED.lock().unwrap() = Some(Instant::now());
}

/// The server's port and the model's name, starting it (and loading the model) if it is not running.
fn ensure_server() -> Result<(u16, String), String> {
    let mut g = SERVER.lock().unwrap();
    if let Some(s) = g.as_mut() {
        if matches!(s.child.try_wait(), Ok(None)) {
            touch();
            return Ok((s.port, s.model.clone()));
        }
        *g = None;
    }
    let dir = crate::paths::chat_dir().ok_or("The chat runtime is missing (run scripts\\get-chat.ps1)")?;
    let model = model_path().ok_or("No chat model found (put a GGUF in models\\chat, or download one in LM Studio)")?;
    let draft = draft_path(&model);
    let args = |port: u16| -> Vec<OsString> {
        let mut a: Vec<OsString> = Vec::new();
        for s in ["-m"] {
            a.push(s.into());
        }
        a.push(model.clone().into_os_string());
        for s in [
            "--host", "127.0.0.1", "--port", &port.to_string(), "-c", "8192", "-ngl", "99", "-t", "2", "-fa", "on", "--jinja",
            "--no-webui", "--parallel", "1",
        ] {
            a.push(s.into());
        }
        if let Some(d) = &draft {
            for s in ["--spec-type", "draft-mtp", "--model-draft"] {
                a.push(s.into());
            }
            a.push(d.clone().into_os_string());
        }
        a
    };
    let (child, port) = crate::planner::launch_wait(&dir.join("llama-server.exe"), args, &dir, Duration::from_secs(90))
        .ok_or("The chat model did not start")?;
    let name = label(&model);
    behind_you(&child);
    *g = Some(Server { child, port, model: name.clone() });
    drop(g);
    touch();
    watch();
    Ok((port, name))
}

/// Below normal priority: Windows schedules the process's GPU work at the same class, so the model
/// waits for whatever else wants the CPU or the GPU.
fn behind_you(child: &Child) {
    use std::os::windows::io::AsRawHandle;
    use windows::Win32::Foundation::HANDLE;
    use windows::Win32::System::Threading::{SetPriorityClass, BELOW_NORMAL_PRIORITY_CLASS};
    unsafe {
        let _ = SetPriorityClass(HANDLE(child.as_raw_handle()), BELOW_NORMAL_PRIORITY_CLASS);
    }
}

/// Let go of the model after it has been idle for a while.
fn watch() {
    static STARTED: std::sync::Once = std::sync::Once::new();
    STARTED.call_once(|| {
        std::thread::spawn(|| loop {
            std::thread::sleep(Duration::from_secs(30));
            let idle = LAST_USED.lock().unwrap().is_some_and(|t| t.elapsed() > IDLE_STOP);
            if idle {
                stop();
            }
        });
    });
}

/// Stop the server (the model's memory is freed).
pub fn stop() {
    if let Some(mut s) = SERVER.lock().unwrap().take() {
        let _ = s.child.kill();
        let _ = s.child.wait();
    }
}

// ---------------------------------------------------------------------------------------------- asking

/// Which typed queries ask about the window: a "?" first, "ask ...", or a request that points at
/// what is on screen ("summarise this", "what does this error mean"). The question, without the lead.
pub fn wants(query: &str) -> Option<String> {
    let q = query.trim();
    if let Some(rest) = q.strip_prefix('?') {
        let rest = rest.trim();
        return (rest.chars().count() >= 2).then(|| rest.to_string());
    }
    let lower = q.to_lowercase();
    if let Some(rest) = lower.strip_prefix("ask ") {
        let rest = q[q.len() - rest.len()..].trim();
        return (rest.chars().count() >= 3).then(|| rest.to_string());
    }
    const LEADS: [&str; 24] = [
        "summarise", "summarize", "summary", "explain", "translate", "what", "why", "how", "who", "fix", "rewrite", "shorten",
        "proofread", "tldr", "tl;dr", "r\u{e9}sume", "r\u{e9}sumer", "resume", "explique", "traduis", "traduire", "pourquoi",
        "comment", "corrige",
    ];
    const POINTS: [&str; 20] = [
        "this", "that", "it", "here", "selected", "selection", "above", "these", "page", "error", "text", "ce", "cette", "ceci",
        "cela", "\u{e7}a", "ici", "s\u{e9}lection", "ces", "message",
    ];
    let words: Vec<String> = lower
        .split(|c: char| c.is_whitespace() || matches!(c, '?' | ',' | '!' | '.'))
        .filter(|w| !w.is_empty())
        .map(str::to_string)
        .collect();
    let first = words.first()?;
    (words.len() >= 2 && LEADS.contains(&first.as_str()) && words[1..].iter().any(|w| POINTS.contains(&w.as_str())))
        .then(|| q.trim_end_matches('?').trim().to_string())
}

/// The row that offers the answer, when the query asks, there is a window to ask about and a model.
pub fn hits(query: &str) -> Vec<Hit> {
    let Some(question) = wants(query) else { return Vec::new() };
    if !available() {
        return Vec::new();
    }
    if about_documents(&question) {
        if !crate::semantic::available() {
            return Vec::new();
        }
        return vec![Hit {
            title: question,
            subtitle: "Ask your documents  \u{b7}  on this PC".into(),
            kind: Kind::Ask,
            action: Action::Builtin("ask".into()),
            score: 9500,
        }];
    }
    let app = crate::uia::app_name();
    if app.is_empty() {
        return Vec::new();
    }
    let about = if crate::uia::has_selection() { format!("your selection in {app}") } else { format!("the {app} window") };
    vec![Hit {
        title: question,
        subtitle: format!("Ask about {about}  \u{b7}  on this PC"),
        kind: Kind::Ask,
        action: Action::Builtin("ask".into()),
        score: 9500,
    }]
}

/// The window's text by the palette opening it was read for, so a warm-up and the question, or two
/// questions, read it once.
static READ: Mutex<Option<(u64, Instant, crate::uia::WindowText)>> = Mutex::new(None);

fn window(scan: u64) -> Option<crate::uia::WindowText> {
    if let Some((s, at, w)) = READ.lock().unwrap().as_ref() {
        if *s == scan && at.elapsed() < READ_FRESH {
            return Some(w.clone());
        }
    }
    let w = crate::uia::window_text(WINDOW_CHARS, READ_WAIT)?;
    *READ.lock().unwrap() = Some((scan, Instant::now(), w.clone()));
    Some(w)
}

static WARM_SEQ: AtomicU64 = AtomicU64::new(0);
static WARMED: Mutex<Option<u64>> = Mutex::new(None);

/// A row offers an answer: once typing pauses, start the model and have it read the window, so the
/// answer starts at once when asked for. Returns at once; at most one warm-up per palette opening.
pub fn warm(question: &str) {
    let n = WARM_SEQ.fetch_add(1, Ordering::SeqCst) + 1;
    let q = question.to_string();
    std::thread::spawn(move || {
        std::thread::sleep(WARM_AFTER);
        if WARM_SEQ.load(Ordering::SeqCst) == n {
            warm_now(&q);
        }
    });
}

/// `warm`, without the wait; blocks until the model has read the window.
pub fn warm_now(question: &str) {
    let scan = crate::uia::scan_id();
    {
        let mut w = WARMED.lock().unwrap();
        if *w == Some(scan) {
            return;
        }
        *w = Some(scan);
    }
    let server = std::thread::spawn(ensure_server);
    let ctx = window(scan);
    let Ok(Ok((port, _))) = server.join() else { return };
    let _ = question;
    let Some((user, _)) = ctx.as_ref().and_then(|c| build_prompt(c, SENTINEL)) else { return };
    let Some(prompt) = template(port, SYSTEM, &user) else { return };
    let Some(at) = prompt.find(SENTINEL) else { return };
    // Everything before the question, and nothing written: the model reads it and keeps it.
    let prefix = prompt[..at].trim_end_matches(' ');
    let _ = crate::planner::http(port, "POST", "/completion", &completion(prefix, false, 0), Duration::from_secs(120));
}

/// Stands for the question while the text before it is found in the templated prompt.
const SENTINEL: &str = "\u{2063}Q\u{2063}";

/// The prompt the server makes of the conversation, with the model's own chat template.
fn template(port: u16, system: &str, user: &str) -> Option<String> {
    template_with(port, system, user, None)
}

/// `template`, with tools offered (they are written into the prompt, before the conversation).
fn template_with(port: u16, system: &str, user: &str, tools: Option<&str>) -> Option<String> {
    let mut body = serde_json::json!({
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ],
        "chat_template_kwargs": {"enable_thinking": false},
    });
    if let Some(t) = tools.and_then(|t| serde_json::from_str::<serde_json::Value>(t).ok()) {
        body["tools"] = t;
    }
    let reply = crate::planner::http(port, "POST", "/apply-template", &body.to_string(), Duration::from_secs(10))?;
    serde_json::from_str::<serde_json::Value>(&reply).ok()?["prompt"].as_str().map(str::to_string)
}

fn completion(prompt: &str, stream: bool, n_predict: i32) -> String {
    serde_json::json!({
        "prompt": prompt,
        "n_predict": n_predict,
        "stream": stream,
        "cache_prompt": true,
        "temperature": 0.3,
        "top_p": 0.9,
    })
    .to_string()
}

fn request(system: &str, user: &str, stream: bool, max_tokens: u32) -> String {
    serde_json::json!({
        "messages": [
            {"role": "system", "content": system},
            {"role": "user", "content": user},
        ],
        "stream": stream,
        "max_tokens": max_tokens,
        "temperature": 0.3,
        "top_p": 0.9,
        "cache_prompt": true,
        "chat_template_kwargs": {"enable_thinking": false},
    })
    .to_string()
}

/// Everything the shell needs to stream an answer: where the server is and the request to send it.
pub struct Prepared {
    pub port: u16,
    /// Where to post `body`: `/completion` (a prompt in the model's template), else the chat API.
    pub path: &'static str,
    /// The JSON body for `/v1/chat/completions` (streaming).
    pub body: String,
    pub app: String,
    pub model: String,
    /// "selection", "window" or "documents": what the answer is from.
    pub source: &'static str,
    /// For "documents": the files the excerpts came from, numbered as the answer cites them.
    pub sources: Vec<String>,
}

/// Read the window and ready the model, in parallel (the first takes a second or two, the second a
/// few seconds when the model has to load). Blocks: call from a worker thread.
pub fn prepare(question: &str, documents: &dyn Fn(&str) -> Vec<(String, String)>) -> Result<Prepared, String> {
    if about_documents(question) {
        return prepare_documents(question, documents);
    }
    let server = std::thread::spawn(ensure_server);
    let ctx = window(crate::uia::scan_id());
    let (port, model) = server.join().map_err(|_| "The chat model stopped".to_string())??;
    let ctx = ctx.ok_or("I couldn't read that window")?;
    let (user, source) = build_prompt(&ctx, question).ok_or_else(|| format!("There is no text to read in {}", ctx.app))?;
    // The same prompt the warm-up began, so the model only reads the question.
    let (path, body) = match template(port, SYSTEM, &user) {
        Some(prompt) => ("/completion", completion(&prompt, true, 700)),
        None => ("/v1/chat/completions", request(SYSTEM, &user, true, 700)),
    };
    Ok(Prepared { port, path, body, app: ctx.app, model, source, sources: Vec::new() })
}

/// Answer from the user's documents: the best chunk of each of the best few, found by the meaning
/// search and the word index, in the prompt; the answer cites them by number.
fn prepare_documents(question: &str, documents: &dyn Fn(&str) -> Vec<(String, String)>) -> Result<Prepared, String> {
    let server = std::thread::spawn(ensure_server);
    let found = documents(question);
    let (port, model) = server.join().map_err(|_| "The chat model stopped".to_string())??;
    if found.is_empty() {
        return Err("I found nothing about that in your documents".into());
    }
    let names: Vec<String> = found
        .iter()
        .map(|(p, _)| Path::new(p).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| p.clone()))
        .collect();
    *SOURCES.lock().unwrap() = found.iter().map(|(p, _)| p.clone()).collect();
    let mut user = String::from("Excerpts from the user's documents:\n\n");
    for (i, ((_, text), name)) in found.iter().zip(&names).enumerate() {
        user.push_str(&format!("[{}] {name}\n\"\"\"\n{}\n\"\"\"\n\n", i + 1, text.trim()));
    }
    user.push_str(&format!("Question: {question}"));
    let (path, body) = match template(port, SYSTEM_DOCS, &user) {
        Some(prompt) => ("/completion", completion(&prompt, true, 700)),
        None => ("/v1/chat/completions", request(SYSTEM_DOCS, &user, true, 700)),
    };
    Ok(Prepared { port, path, body, app: String::new(), model, source: "documents", sources: names })
}

/// A chat is open: what the shell needs to talk with the model.
pub struct Opened {
    pub port: u16,
    /// The system message: the instructions and the window's text, which every turn repeats (the
    /// model has read it already).
    pub system: String,
    pub app: String,
    pub model: String,
    /// "selection", "window" or "none" (no window to read: a plain chat).
    pub source: &'static str,
    /// The tools offered with every message (TOOLS).
    pub tools: &'static str,
}

/// Open a chat: start the model and read the window in front at once, then have the model read the
/// window's text ahead, so the first message is answered without waiting for it. Blocks until the model
/// has read it (a few seconds on a long page): call from a worker thread. `memory` is what the model
/// remembers about the user (the shell's MemoryStore), put before the window's text so it is read ahead too.
pub fn open(memory: &str) -> Result<Opened, String> {
    let server = std::thread::spawn(ensure_server);
    let ctx = window(crate::uia::scan_id());
    let (port, model) = server.join().map_err(|_| "The chat model stopped".to_string())??;
    let (mut system, mut source, mut app) = (format!("{SYSTEM_CHAT} {TOOL_USE} {MEMORY_USE} {}", today()), "none", String::new());
    if !memory.trim().is_empty() {
        system.push_str("\n\n");
        system.push_str(memory.trim());
    }
    if let Some(c) = &ctx {
        app = c.app.clone();
        let place = if c.title.is_empty() { c.app.clone() } else { format!("{} ({})", c.title, c.app) };
        if let Some(sel) = c.selection.as_deref().filter(|s| !s.trim().is_empty()) {
            system.push_str(&format!("\n\nThe user selected this text in {place}:\n\"\"\"\n{}\n\"\"\"", cut(sel, SELECTION_CHARS, false)));
            let around = cut(&c.text, AROUND_CHARS, c.terminal);
            if !around.trim().is_empty() {
                system.push_str(&format!("\n\nThe rest of the window, for context only:\n\"\"\"\n{around}\n\"\"\""));
            }
            source = "selection";
        } else if !c.text.trim().is_empty() {
            system.push_str(&format!("\n\nThe user is looking at {place}. Its text:\n\"\"\"\n{}\n\"\"\"", cut(&c.text, WINDOW_CHARS, c.terminal)));
            source = "window";
        }
    }
    // The model reads the system message now and keeps it (see `warm_now`).
    if let Some(prompt) = template_with(port, &system, SENTINEL, Some(TOOLS)) {
        if let Some(at) = prompt.find(SENTINEL) {
            let prefix = prompt[..at].trim_end_matches(' ');
            let _ = crate::planner::http(port, "POST", "/completion", &completion(prefix, false, 0), Duration::from_secs(120));
        }
    }
    touch();
    Ok(Opened { port, system, app, model, source, tools: TOOLS })
}

/// Excerpts of the user's documents for a chat message that is not about the screen: a block to put
/// before the message, and the files they came from; None when nothing matches well.
pub fn message_documents(
    question: &str,
    documents: &dyn Fn(&str) -> Vec<(String, String)>,
) -> Option<(String, Vec<String>)> {
    if points_at_screen(question) {
        return None;
    }
    let found = documents(question);
    if found.is_empty() {
        return None;
    }
    let names: Vec<String> = found
        .iter()
        .map(|(p, _)| Path::new(p).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| p.clone()))
        .collect();
    *SOURCES.lock().unwrap() = found.iter().map(|(p, _)| p.clone()).collect();
    let mut block = String::from("Excerpts from the user's documents, which may or may not be relevant:\n\n");
    for (i, ((_, text), name)) in found.iter().zip(&names).enumerate() {
        block.push_str(&format!("[{}] {name}\n\"\"\"\n{}\n\"\"\"\n\n", i + 1, text.trim()));
    }
    Some((block, names))
}

/// The files the last answer from documents cited, in the order it numbered them.
static SOURCES: Mutex<Vec<String>> = Mutex::new(Vec::new());

pub fn sources() -> Vec<String> {
    SOURCES.lock().unwrap().clone()
}

/// Does the question point at what is on screen ("this", "here", "the error"), rather than ask about
/// one's files? "? when does my lease end" is about documents; "? what does this mean" is the window.
pub fn points_at_screen(question: &str) -> bool {
    const POINTS: [&str; 24] = [
        "this", "that", "it", "here", "selected", "selection", "above", "these", "page", "error", "text", "window", "screen",
        "tab", "ce", "cette", "ceci", "cela", "\u{e7}a", "ici", "s\u{e9}lection", "ces", "\u{e9}cran", "message",
    ];
    question
        .to_lowercase()
        .split(|c: char| c.is_whitespace() || matches!(c, '?' | ',' | '!' | '.'))
        .any(|w| POINTS.contains(&w))
}

/// Is this question to be answered from the user's documents?
pub fn about_documents(question: &str) -> bool {
    !points_at_screen(question)
}

/// The user's message for the model, and what it is from; None when there is nothing to read.
fn build_prompt(ctx: &crate::uia::WindowText, question: &str) -> Option<(String, &'static str)> {
    let place = if ctx.title.is_empty() { ctx.app.clone() } else { format!("{} ({})", ctx.title, ctx.app) };
    if let Some(sel) = ctx.selection.as_deref().filter(|s| !s.trim().is_empty()) {
        let sel = cut(sel, SELECTION_CHARS, false);
        let around = cut(&ctx.text, AROUND_CHARS, ctx.terminal);
        let mut user = format!("In {place}, the user selected this text:\n\"\"\"\n{sel}\n\"\"\"\n");
        if !around.trim().is_empty() {
            user.push_str(&format!("\nThe rest of the window, for context only:\n\"\"\"\n{around}\n\"\"\"\n"));
        }
        user.push_str(&format!("\nQuestion about the selected text: {question}"));
        return Some((user, "selection"));
    }
    if ctx.text.trim().is_empty() {
        return None;
    }
    let text = cut(&ctx.text, WINDOW_CHARS, ctx.terminal);
    Some((format!("Window: {place}\nText on screen:\n\"\"\"\n{text}\n\"\"\"\n\nQuestion: {question}"), "window"))
}

/// `text` cut to `max` characters: its start, or (`tail`: a terminal's newest lines are at the end)
/// its end, with a note of the cut.
fn cut(text: &str, max: usize, tail: bool) -> String {
    let text = text.trim();
    let n = text.chars().count();
    if n <= max {
        return text.to_string();
    }
    if tail {
        let skip = text.chars().skip(n - max).collect::<String>();
        let from = skip.find('\n').map_or(0, |i| i + 1);
        format!("[earlier text left out]\n{}", &skip[from..])
    } else {
        let head: String = text.chars().take(max).collect();
        let upto = head.rfind(['\n', ' ']).unwrap_or(head.len());
        format!("{}\n[the rest is left out]", &head[..upto])
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::uia::WindowText;

    fn ctx(text: &str, selection: Option<&str>, terminal: bool) -> WindowText {
        WindowText {
            app: "Notepad".into(),
            title: "notes.txt - Notepad".into(),
            text: text.into(),
            selection: selection.map(str::to_string),
            terminal,
        }
    }

    #[test]
    fn requests_that_point_at_the_screen_ask() {
        assert_eq!(wants("summarise this").as_deref(), Some("summarise this"));
        assert_eq!(wants("what does this error mean?").as_deref(), Some("what does this error mean"));
        assert_eq!(wants("How do I fix this").as_deref(), Some("How do I fix this"));
        assert_eq!(wants("translate this to french").as_deref(), Some("translate this to french"));
        assert_eq!(wants("r\u{e9}sume \u{e7}a").as_deref(), Some("r\u{e9}sume \u{e7}a"));
        assert_eq!(wants("? who wrote it").as_deref(), Some("who wrote it"));
        assert_eq!(wants("ask what is on this page").as_deref(), Some("what is on this page"));
    }

    #[test]
    fn a_question_is_about_the_screen_or_about_files() {
        assert!(points_at_screen("what does this error mean"));
        assert!(points_at_screen("who wrote it"));
        assert!(!points_at_screen("when does my lease end"));
        assert!(about_documents("what voltage does the clutch motor need"));
        assert!(!about_documents("summarise this page"));
    }

    #[test]
    fn everything_else_is_a_search() {
        assert_eq!(wants("what is the capital of france"), None);
        assert_eq!(wants("how tall is the eiffel tower"), None);
        assert_eq!(wants("notepad"), None);
        assert_eq!(wants("summarise"), None);
        assert_eq!(wants("resume.docx"), None);
        assert_eq!(wants("?"), None);
        assert_eq!(wants("this is a file name"), None);
    }

    #[test]
    fn a_model_is_chosen_by_name_then_size() {
        let gb = |n: u64| n * 1_000_000_000;
        let files = vec![
            (PathBuf::from(r"C:\m\mmproj-F32.gguf"), gb(2)),
            (PathBuf::from(r"C:\m\tiny-0.5b.gguf"), 400_000_000),
            (PathBuf::from(r"C:\m\Llama-3.2-3B-Q4.gguf"), gb(2)),
            (PathBuf::from(r"C:\m\Qwen3.5-4B-Q4_K_S.gguf"), gb(3)),
            (PathBuf::from(r"C:\m\huge-70b.gguf"), gb(40)),
        ];
        assert_eq!(pick(files.clone()).as_deref(), Some(Path::new(r"C:\m\Qwen3.5-4B-Q4_K_S.gguf")));
        let mut with_gemma = files;
        with_gemma.push((PathBuf::from(r"C:\m\gemma-4-E4B-it-Q4_0.gguf"), gb(4)));
        with_gemma.push((PathBuf::from(r"C:\m\mtp-gemma-4-E4B-it.gguf"), 98_000_000));
        assert_eq!(pick(with_gemma).as_deref(), Some(Path::new(r"C:\m\gemma-4-E4B-it-Q4_0.gguf")));
        assert_eq!(pick(vec![(PathBuf::from("a.gguf"), 100)]), None);
        assert_eq!(label(Path::new(r"C:\m\Qwen3.5-4B-Q4_K_S.gguf")), "Qwen3.5 4B");
        assert_eq!(label(Path::new("gemma-3-4b-it-Q4_0.gguf")), "gemma 3 4b");
    }

    #[test]
    fn the_prompt_says_what_it_is_from() {
        let (p, src) = build_prompt(&ctx("Dear Bob, the lease ends in August.", None, false), "summarise this").unwrap();
        assert_eq!(src, "window");
        assert!(p.contains("Text on screen") && p.contains("lease ends") && p.ends_with("Question: summarise this"), "{p}");

        let (p, src) = build_prompt(&ctx("other text", Some("selected words"), false), "explain this").unwrap();
        assert_eq!(src, "selection");
        assert!(p.contains("selected this text") && p.contains("selected words") && p.contains("for context only"), "{p}");

        assert!(build_prompt(&ctx("  ", None, false), "summarise this").is_none());
    }

    #[test]
    fn long_text_is_cut_at_the_start_or_the_end() {
        let text = (1..=2000).map(|i| format!("line {i}")).collect::<Vec<_>>().join("\n");
        let head = cut(&text, 100, false);
        assert!(head.starts_with("line 1\n") && head.ends_with("[the rest is left out]"));
        assert!(head.chars().count() < 140);
        let tail = cut(&text, 100, true);
        assert!(tail.starts_with("[earlier text left out]\nline ") && tail.ends_with("line 2000"), "{tail}");
        assert_eq!(cut("short", 100, false), "short");
    }
}
