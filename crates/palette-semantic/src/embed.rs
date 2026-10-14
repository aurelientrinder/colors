//! EmbeddingGemma-300m (4-bit ONNX) through ONNX Runtime, or OpenVINO on the NPU.
//!
//! The export's `sentence_embedding` output already includes pooling, the two dense
//! layers and L2 normalisation. The model is trained with Matryoshka representation
//! learning, so keeping only the first `DIM` components (and re-normalising) trades a
//! little quality for 3x less memory and a 3x faster scan.

use ort::session::{builder::GraphOptimizationLevel, Session};
use ort::value::Tensor;
use std::path::Path;
use tokenizers::{Encoding, Tokenizer, TruncationParams};

pub const DIM: usize = 256;

/// Where the model runs. The GPU goes through DirectML, the NPU through OpenVINO.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Device {
    Cpu,
    Gpu,
    Npu,
}

impl Device {
    pub fn parse(s: &str) -> Option<Device> {
        match s.to_ascii_lowercase().as_str() {
            "cpu" => Some(Device::Cpu),
            "gpu" => Some(Device::Gpu),
            "npu" => Some(Device::Npu),
            _ => None,
        }
    }
}

enum Backend {
    Ort {
        session: Session,
        output: String,
        /// Extra graph inputs (EmbeddingGemma 2's image/video/audio features), fed empty for text.
        extra: Vec<(String, usize)>,
    },
    Npu(Npu),
}

pub struct Embedder {
    backend: Backend,
    /// Where the model actually runs (a device that fails to load falls back: NPU, GPU, CPU).
    pub device: Device,
    tok: Tokenizer,
}

impl Embedder {
    /// `budgets`: the token budgets `embed` will be called with (the NPU compiles one shape each).
    pub fn load(model_dir: &Path, threads: usize, device: Device, budgets: &[usize]) -> Result<Embedder, String> {
        let onnx = model_dir.join("model_q4.onnx");
        if !onnx.exists() {
            return Err(format!("model not found: {}", onnx.display()));
        }
        let tok = Tokenizer::from_file(model_dir.join("tokenizer.json")).map_err(|e| format!("tokenizer: {e}"))?;
        let mut device = device;
        if device == Device::Npu {
            match Npu::load(&model_dir.join(NPU_MODEL), budgets) {
                Ok(n) => return Ok(Embedder { backend: Backend::Npu(n), device, tok }),
                Err(e) => {
                    eprintln!("NPU not used, trying the GPU: {e}");
                    device = Device::Gpu;
                }
            }
        }
        let session = match device {
            Device::Gpu => match Self::directml(&onnx) {
                Ok(s) => Some(s),
                Err(e) => {
                    eprintln!("DirectML unavailable, using the CPU: {e}");
                    None
                }
            },
            _ => None,
        };
        let (session, device) = match session {
            Some(s) => (s, device),
            None => (
                Session::builder()
                    .map_err(|e| e.to_string())?
                    .with_optimization_level(GraphOptimizationLevel::Level3)
                    .map_err(|e| e.to_string())?
                    .with_intra_threads(threads)
                    .map_err(|e| e.to_string())?
                    .commit_from_file(&onnx)
                    .map_err(|e| format!("onnx: {e}"))?,
                Device::Cpu,
            ),
        };

        let outs: Vec<String> = session.outputs().iter().map(|o| o.name().to_string()).collect();
        let output = if outs.iter().any(|n| n == "sentence_embedding") {
            "sentence_embedding".to_string()
        } else {
            return Err(format!("model has no sentence_embedding output (outputs: {outs:?})"));
        };
        // The v2 text graph merges optional multimodal features; text-only use passes them
        // with zero rows, so remember each extra input and its feature width.
        let extra = session
            .inputs()
            .iter()
            .filter(|i| i.name() != "input_ids" && i.name() != "attention_mask")
            .map(|i| {
                let width = i
                    .dtype()
                    .tensor_shape()
                    .and_then(|s| s.iter().last().copied())
                    .filter(|d| *d > 0)
                    .unwrap_or(512) as usize;
                (i.name().to_string(), width)
            })
            .collect();
        Ok(Embedder { backend: Backend::Ort { session, output, extra }, device, tok })
    }

    /// A DirectML session on the GPU. DirectML needs sequential execution and no memory
    /// pattern; one host thread is enough to feed it, which leaves the CPU free.
    fn directml(onnx: &Path) -> Result<Session, String> {
        use ort::ep::directml::{DeviceFilter, PerformancePreference};
        let ep = ort::ep::DirectML::default()
            .with_device_filter(DeviceFilter::Gpu)
            .with_performance_preference(PerformancePreference::MinimumPower)
            .build()
            .error_on_failure();
        Session::builder()
            .map_err(|e| e.to_string())?
            .with_execution_providers([ep])
            .map_err(|e| e.to_string())?
            .with_memory_pattern(false)
            .map_err(|e| e.to_string())?
            .with_parallel_execution(false)
            .map_err(|e| e.to_string())?
            .with_optimization_level(GraphOptimizationLevel::Level3)
            .map_err(|e| e.to_string())?
            .with_intra_threads(1)
            .map_err(|e| e.to_string())?
            .commit_from_file(onnx)
            .map_err(|e| format!("onnx: {e}"))
    }

    /// Compile the NPU shapes now, waiting for them (`load` would compile them in the background
    /// and use the GPU meanwhile).
    pub fn compile_npu(model_dir: &Path, budgets: &[usize]) -> Result<(), String> {
        let mut npu = Npu::open(&model_dir.join(NPU_MODEL))?;
        for s in lens_for(budgets) {
            npu.request(s)?;
        }
        Ok(())
    }

    pub fn input_names(&self) -> Vec<String> {
        match &self.backend {
            Backend::Ort { session, .. } => session.inputs().iter().map(|i| format!("{} {:?}", i.name(), i.dtype())).collect(),
            Backend::Npu(n) => ["input_ids", "attention_mask"].iter().map(|s| s.to_string()).chain(n.extra.iter().cloned()).collect(),
        }
    }

    /// Embed a batch. Every vector is `DIM` long and unit length.
    pub fn embed(&mut self, texts: &[String], max_tokens: usize) -> Result<Vec<Vec<f32>>, String> {
        if texts.is_empty() {
            return Ok(Vec::new());
        }
        self.tok
            .with_truncation(Some(TruncationParams { max_length: max_tokens, ..Default::default() }))
            .map_err(|e| e.to_string())?;
        let enc = self.tok.encode_batch(texts.to_vec(), true).map_err(|e| e.to_string())?;
        let mut out = match &mut self.backend {
            Backend::Npu(n) => n.embed(&enc, max_tokens)?,
            Backend::Ort { session, output, extra } => ort_embed(session, output, extra, &enc, max_tokens, self.device)?,
        };
        out.iter_mut().for_each(|v| normalize(v));
        Ok(out)
    }
}

/// One ONNX Runtime run over the batch; the first `DIM` components of each row.
fn ort_embed(
    session: &mut Session,
    output: &str,
    extra: &[(String, usize)],
    enc: &[Encoding],
    max_tokens: usize,
    device: Device,
) -> Result<Vec<Vec<f32>>, String> {
    let n = enc.len();
    let longest = enc.iter().map(|e| e.get_ids().len()).max().unwrap_or(1).max(1);
    // DirectML compiles its kernels for each input shape it meets (over a second each), so
    // on the GPU every input is padded to a few fixed shapes: the full token budget, and the
    // batch rounded up to a power of two. Padding is masked out, and padded rows are dropped.
    let (b, s) = if device == Device::Cpu { (n, longest) } else { (n.next_power_of_two(), max_tokens.max(longest)) };
    let (ids, mask) = padded(enc, b, s);

    let ids = Tensor::from_array(([b, s], ids)).map_err(|e| e.to_string())?;
    let mask = Tensor::from_array(([b, s], mask)).map_err(|e| e.to_string())?;
    let mut inputs: Vec<(String, ort::session::SessionInputValue<'_>)> =
        vec![("input_ids".into(), ids.into()), ("attention_mask".into(), mask.into())];
    for (name, width) in extra {
        let empty = Tensor::<f32>::from_array(([0usize, *width], Vec::<f32>::new())).map_err(|e| e.to_string())?;
        inputs.push((name.clone(), empty.into()));
    }
    let outputs = session.run(inputs).map_err(|e| e.to_string())?;
    let (_, data) = outputs[output].try_extract_tensor::<f32>().map_err(|e| e.to_string())?;

    let width = data.len() / b;
    if width < DIM {
        return Err(format!("unexpected embedding width {width}"));
    }
    Ok((0..n).map(|r| data[r * width..r * width + DIM].to_vec()).collect())
}

/// Token ids and attention mask, `b` rows of `s`, zero-padded (0 is Gemma's pad id).
fn padded(enc: &[Encoding], b: usize, s: usize) -> (Vec<i64>, Vec<i64>) {
    let mut ids = vec![0i64; b * s];
    let mut mask = vec![0i64; b * s];
    for (r, e) in enc.iter().enumerate() {
        for (c, &id) in e.get_ids().iter().take(s).enumerate() {
            ids[r * s + c] = id as i64;
            mask[r * s + c] = 1;
        }
    }
    (ids, mask)
}

/// The model on the NPU through OpenVINO. The NPU runs fixed shapes only, so each token budget
/// gets its own compiled copy (one text at a time: on the NPU that is as fast as a batch, and a
/// short query does not wait behind a padded batch).
///
/// Compiling takes up to a minute per shape the first time; after that it is loaded from
/// palette_ov::cache_dir() in a fraction of a second. So the NPU is used only once every shape
/// has been compiled; until then a background thread compiles them and the GPU stands in.
struct Npu {
    core: palette_ov::Core,
    model: palette_ov::Model,
    xml: std::path::PathBuf,
    /// The multimodal inputs, fixed at zero rows.
    extra: Vec<String>,
    /// By sequence length.
    shapes: Vec<(usize, palette_ov::Compiled, palette_ov::Request)>,
}

/// The search model as OpenVINO IR, made by ml\npu\export.py.
const NPU_MODEL: &str = "model_npu.xml";

const NPU_PROPS: &[(&str, &str)] = &[("PERFORMANCE_HINT", "LATENCY"), ("NPU_COMPILER_TYPE", "DRIVER")];

impl Npu {
    fn load(xml: &Path, budgets: &[usize]) -> Result<Npu, String> {
        if !xml.exists() {
            return Err(format!("no {} (ml\\npu\\export.py)", xml.display()));
        }
        let lens = lens_for(budgets);
        let missing: Vec<usize> = lens.iter().copied().filter(|&s| !palette_ov::is_cached(&npu_key(xml, s))).collect();
        if !missing.is_empty() {
            Self::compile_in_background(xml, missing.clone());
            return Err(format!("compiling for {missing:?} tokens in the background"));
        }
        let mut npu = Self::open(xml)?;
        for s in lens {
            npu.request(s)?;
        }
        Ok(npu)
    }

    fn open(xml: &Path) -> Result<Npu, String> {
        let core = palette_ov::Core::new()?;
        if !core.has("NPU") {
            return Err("no NPU".into());
        }
        core.set(None, &[("CACHE_DIR", &palette_ov::cache_dir().to_string_lossy())])?;
        let model = core.read(xml)?;
        let extra = ["image_features", "video_features", "audio_features"]
            .into_iter()
            .filter(|n| model.reshape(&[(n, &[0, 512])]).is_ok())
            .map(String::from)
            .collect();
        Ok(Npu { core, model, xml: xml.to_path_buf(), extra, shapes: Vec::new() })
    }

    /// Fill the compile cache, one shape after another, at most once at a time per process.
    fn compile_in_background(xml: &Path, lens: Vec<usize>) {
        use std::sync::atomic::{AtomicBool, Ordering};
        static BUSY: AtomicBool = AtomicBool::new(false);
        if BUSY.swap(true, Ordering::SeqCst) {
            return;
        }
        let xml = xml.to_path_buf();
        std::thread::spawn(move || {
            match Self::open(&xml) {
                Ok(mut npu) => {
                    for s in lens {
                        if let Err(e) = npu.request(s) {
                            eprintln!("NPU: {s} tokens failed to compile: {e}");
                            break;
                        }
                    }
                }
                Err(e) => eprintln!("NPU unavailable: {e}"),
            }
            BUSY.store(false, Ordering::SeqCst);
        });
    }

    fn request(&mut self, s: usize) -> Result<&mut palette_ov::Request, String> {
        if let Some(i) = self.shapes.iter().position(|(l, ..)| *l == s) {
            return Ok(&mut self.shapes[i].2);
        }
        let t = std::time::Instant::now();
        let dims = [1, s as i64];
        self.model.reshape(&[("input_ids", &dims), ("attention_mask", &dims)])?;
        let c = self.core.compile(&self.model, "NPU", NPU_PROPS)?;
        let r = c.request()?;
        palette_ov::set_cached(&npu_key(&self.xml, s));
        eprintln!("NPU: {s} tokens ready in {} ms", t.elapsed().as_millis());
        self.shapes.push((s, c, r));
        Ok(&mut self.shapes.last_mut().unwrap().2)
    }

    fn embed(&mut self, enc: &[Encoding], max_tokens: usize) -> Result<Vec<Vec<f32>>, String> {
        let s = len_for(max_tokens);
        let extra = self.extra.clone();
        let req = self.request(s)?;
        let mut out = Vec::with_capacity(enc.len());
        for e in enc {
            let (ids, mask) = padded(std::slice::from_ref(e), 1, s);
            req.set_i64("input_ids", &[1, s as i64], &ids)?;
            req.set_i64("attention_mask", &[1, s as i64], &mask)?;
            for name in &extra {
                req.set_f32(name, &[0, 512], &[])?;
            }
            req.infer()?;
            let (_, v) = req.output_named("sentence_embedding")?;
            if v.len() < DIM {
                return Err(format!("unexpected embedding width {}", v.len()));
            }
            out.push(v[..DIM].to_vec());
        }
        Ok(out)
    }
}

/// The compiled sequence length for a token budget: few shapes, each compiled once.
fn len_for(max_tokens: usize) -> usize {
    max_tokens.max(64).next_multiple_of(32)
}

fn lens_for(budgets: &[usize]) -> Vec<usize> {
    let mut v: Vec<usize> = budgets.iter().map(|&b| len_for(b)).collect();
    v.sort_unstable();
    v.dedup();
    v
}

fn npu_key(xml: &Path, s: usize) -> String {
    palette_ov::cache_key(xml, &format!("embed-{s}"))
}

pub fn normalize(v: &mut [f32]) {
    let n = v.iter().map(|x| x * x).sum::<f32>().sqrt();
    if n > 1e-9 {
        v.iter_mut().for_each(|x| *x /= n);
    }
}

pub fn query_prompt(q: &str) -> String {
    format!("task: search result | query: {q}")
}

pub fn doc_prompt(title: &str, text: &str) -> String {
    format!("title: {title} | text: {text}")
}
