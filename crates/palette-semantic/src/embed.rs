//! EmbeddingGemma-300m (4-bit ONNX) through ONNX Runtime.
//!
//! The export's `sentence_embedding` output already includes pooling, the two dense
//! layers and L2 normalisation. The model is trained with Matryoshka representation
//! learning, so keeping only the first `DIM` components (and re-normalising) trades a
//! little quality for 3x less memory and a 3x faster scan.

use ort::session::{builder::GraphOptimizationLevel, Session};
use ort::value::Tensor;
use std::path::Path;
use tokenizers::{Tokenizer, TruncationParams};

pub const DIM: usize = 256;

pub struct Embedder {
    session: Session,
    tok: Tokenizer,
    output: String,
    /// Extra graph inputs (EmbeddingGemma 2's image/video/audio features), fed empty for text.
    extra: Vec<(String, usize)>,
}

impl Embedder {
    pub fn load(model_dir: &Path, threads: usize) -> Result<Embedder, String> {
        let onnx = model_dir.join("model_q4.onnx");
        if !onnx.exists() {
            return Err(format!("model not found: {}", onnx.display()));
        }
        let tok = Tokenizer::from_file(model_dir.join("tokenizer.json")).map_err(|e| format!("tokenizer: {e}"))?;
        let session = Session::builder()
            .map_err(|e| e.to_string())?
            .with_optimization_level(GraphOptimizationLevel::Level3)
            .map_err(|e| e.to_string())?
            .with_intra_threads(threads)
            .map_err(|e| e.to_string())?
            .commit_from_file(&onnx)
            .map_err(|e| format!("onnx: {e}"))?;

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
        Ok(Embedder { session, tok, output, extra })
    }

    pub fn input_names(&self) -> Vec<String> {
        self.session.inputs().iter().map(|i| format!("{} {:?}", i.name(), i.dtype())).collect()
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

        let b = enc.len();
        let s = enc.iter().map(|e| e.get_ids().len()).max().unwrap_or(1).max(1);
        let mut ids = vec![0i64; b * s]; // 0 is Gemma's pad id
        let mut mask = vec![0i64; b * s];
        for (r, e) in enc.iter().enumerate() {
            for (c, &id) in e.get_ids().iter().enumerate() {
                ids[r * s + c] = id as i64;
                mask[r * s + c] = 1;
            }
        }

        let ids = Tensor::from_array(([b, s], ids)).map_err(|e| e.to_string())?;
        let mask = Tensor::from_array(([b, s], mask)).map_err(|e| e.to_string())?;
        let mut inputs: Vec<(String, ort::session::SessionInputValue<'_>)> =
            vec![("input_ids".into(), ids.into()), ("attention_mask".into(), mask.into())];
        for (name, width) in &self.extra {
            let empty = Tensor::<f32>::from_array(([0usize, *width], Vec::<f32>::new())).map_err(|e| e.to_string())?;
            inputs.push((name.clone(), empty.into()));
        }
        let outputs = self.session.run(inputs).map_err(|e| e.to_string())?;
        let (_, data) = outputs[self.output.as_str()].try_extract_tensor::<f32>().map_err(|e| e.to_string())?;

        let width = data.len() / b;
        if width < DIM {
            return Err(format!("unexpected embedding width {width}"));
        }
        Ok((0..b)
            .map(|r| {
                let mut v = data[r * width..r * width + DIM].to_vec();
                normalize(&mut v);
                v
            })
            .collect())
    }
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
