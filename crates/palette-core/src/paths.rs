use std::path::PathBuf;

pub fn data_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    let d = base.join("palette");
    let _ = std::fs::create_dir_all(&d);
    d
}

pub fn index_dir() -> PathBuf {
    data_dir().join("index")
}

fn exe_dir() -> Option<PathBuf> {
    std::env::current_exe().ok()?.parent().map(|p| p.to_path_buf())
}

/// palette-mft.exe sits next to the shell exe (PaletteWinUI.exe).
pub fn helper_exe() -> Option<PathBuf> {
    let p = exe_dir()?.join("palette-mft.exe");
    p.exists().then_some(p)
}

/// palette-semantic.exe (embedding model host) sits next to the shell exe.
pub fn semantic_exe() -> Option<PathBuf> {
    let p = exe_dir()?.join("palette-semantic.exe");
    p.exists().then_some(p)
}

/// Folder holding `llama-server.exe` and `plan.gguf`, the model that writes plans (fetched by
/// scripts\get-planner.ps1). Looked for as `models\planner` beside the exe or above it.
pub fn planner_dir() -> Option<PathBuf> {
    if let Some(p) = std::env::var_os("PALETTE_PLANNER_DIR").map(PathBuf::from) {
        return p.join("plan.gguf").exists().then_some(p);
    }
    let mut dir = exe_dir();
    let mut bases: Vec<PathBuf> = Vec::new();
    for _ in 0..9 {
        let Some(d) = dir else { break };
        bases.push(d.join("models"));
        dir = d.parent().map(|p| p.to_path_buf());
    }
    bases.push(data_dir().join("models"));
    bases
        .into_iter()
        .map(|b| b.join("planner"))
        .find(|c| c.join("plan.gguf").exists() && c.join("llama-server.exe").exists())
}

/// Folder holding `llama-server.exe` (the GPU build) for the chat model, as `models\chat` beside the
/// exe or above it (scripts\get-chat.ps1).
pub fn chat_dir() -> Option<PathBuf> {
    if let Some(p) = std::env::var_os("PALETTE_CHAT_DIR").map(PathBuf::from) {
        return p.join("llama-server.exe").exists().then_some(p);
    }
    let mut dir = exe_dir();
    let mut bases: Vec<PathBuf> = Vec::new();
    for _ in 0..9 {
        let Some(d) = dir else { break };
        bases.push(d.join("models"));
        dir = d.parent().map(|p| p.to_path_buf());
    }
    bases.push(data_dir().join("models"));
    bases.into_iter().map(|b| b.join("chat")).find(|c| c.join("llama-server.exe").exists())
}

/// palette-plan.exe (the plan model on the NPU) sits next to the shell exe.
pub fn plan_exe() -> Option<PathBuf> {
    let p = exe_dir()?.join("palette-plan.exe");
    p.exists().then_some(p)
}

/// Folder holding the plan model for the NPU (`openvino_model.xml`, `plan.json`, made by
/// ml\npu\export.py), as `models\planner-npu` beside the exe or above it.
pub fn planner_npu_dir() -> Option<PathBuf> {
    palette_ov::find_models("planner-npu", "plan.json").filter(|d| d.join("openvino_model.xml").exists())
}

/// Newest first. The semantic index is per model, so switching rebuilds it.
const MODEL_NAMES: [&str; 2] = ["embeddinggemma-2-text-q4", "embeddinggemma-300m-q4"];

/// Folder holding `model_q4.onnx` + `tokenizer.json`. Looks beside the exe, in the
/// per-user data dir, and in a `models` folder of any ancestor (the repo checkout).
pub fn semantic_model_dir() -> Option<PathBuf> {
    if let Some(p) = std::env::var_os("PALETTE_MODEL_DIR").map(PathBuf::from) {
        return p.join("model_q4.onnx").exists().then_some(p);
    }
    let mut bases: Vec<PathBuf> = Vec::new();
    let mut dir = exe_dir();
    for _ in 0..9 {
        let Some(d) = dir else { break };
        bases.push(d.join("models"));
        dir = d.parent().map(|p| p.to_path_buf());
    }
    bases.push(data_dir().join("models"));
    for name in MODEL_NAMES {
        for base in &bases {
            let c = base.join(name);
            if c.join("model_q4.onnx").exists() && c.join("tokenizer.json").exists() {
                return Some(c);
            }
        }
    }
    None
}
