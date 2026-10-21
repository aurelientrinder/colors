//! The few parts of OpenVINO's C API the launcher's models need, to run them on the NPU: read a
//! model, give it fixed input shapes, compile it, run it.
//!
//! The runtime is loaded when first asked for, from `models\openvino` (scripts\get-openvino.ps1),
//! so nothing links against it: without it, or without an NPU, `Runtime::get` or `Core::has` says
//! so and callers keep their other paths. Only the C API's non-variadic calls are used.

use std::ffi::{c_char, c_void, CStr, CString};
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::sync::OnceLock;

type Status = i32;
type P = *mut c_void;

#[repr(C)]
#[derive(Clone, Copy)]
struct Dim {
    min: i64,
    max: i64,
}

#[repr(C)]
struct PartialShape {
    rank: Dim,
    dims: *const Dim,
}

#[repr(C)]
struct Shape {
    rank: i64,
    dims: *mut i64,
}

#[repr(C)]
struct Property {
    key: *const c_char,
    value: *const c_void,
}

#[repr(C)]
struct Devices {
    devices: *mut *mut c_char,
    size: usize,
}

/// Element types, as numbered in ov_common.h.
#[derive(Clone, Copy)]
#[repr(u32)]
enum Elem {
    F32 = 4,
    I32 = 9,
    I64 = 10,
}

/// The C API's functions this crate calls.
pub struct Runtime {
    core_create: unsafe extern "C" fn(*mut P) -> Status,
    core_free: unsafe extern "C" fn(P),
    core_get_available_devices: unsafe extern "C" fn(P, *mut Devices) -> Status,
    available_devices_free: unsafe extern "C" fn(*mut Devices),
    core_set_properties: unsafe extern "C" fn(P, *const c_char, usize, *const Property) -> Status,
    core_read_model_unicode: unsafe extern "C" fn(P, *const u16, *const u16, *mut P) -> Status,
    core_compile_model_props: unsafe extern "C" fn(P, P, *const c_char, usize, *const Property, *mut P) -> Status,
    model_free: unsafe extern "C" fn(P),
    model_reshape: unsafe extern "C" fn(P, *const *const c_char, *const PartialShape, usize) -> Status,
    compiled_model_free: unsafe extern "C" fn(P),
    compiled_model_create_infer_request: unsafe extern "C" fn(P, *mut P) -> Status,
    infer_request_free: unsafe extern "C" fn(P),
    infer_request_set_tensor: unsafe extern "C" fn(P, *const c_char, P) -> Status,
    infer_request_infer: unsafe extern "C" fn(P) -> Status,
    infer_request_get_output_tensor_by_index: unsafe extern "C" fn(P, usize, *mut P) -> Status,
    infer_request_get_tensor: unsafe extern "C" fn(P, *const c_char, *mut P) -> Status,
    tensor_create: unsafe extern "C" fn(Elem, Shape, *mut P) -> Status,
    tensor_free: unsafe extern "C" fn(P),
    tensor_set_shape: unsafe extern "C" fn(P, Shape) -> Status,
    tensor_data: unsafe extern "C" fn(P, *mut *mut c_void) -> Status,
    tensor_get_shape: unsafe extern "C" fn(P, *mut Shape) -> Status,
    shape_free: unsafe extern "C" fn(*mut Shape) -> Status,
    get_last_err_msg: unsafe extern "C" fn() -> *const c_char,
    get_error_info: unsafe extern "C" fn(Status) -> *const c_char,
}

fn wide(p: &Path) -> Vec<u16> {
    p.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
}

/// The `models` folders the launcher and its helpers look in, in order: beside the exe; in the repo
/// checkout the exe was built in, the nearest folder above it holding Cargo.toml; and in the per-user
/// data folder. Never just any folder above the exe: anyone signed in to the PC can make `C:\models`,
/// and what is found there (llama-server.exe, OpenVINO's DLLs) runs in this user's name. A drive root
/// does not count as a checkout for the same reason (`D:\Cargo.toml` may be anyone's).
pub fn model_bases() -> Vec<PathBuf> {
    let mut bases: Vec<PathBuf> = Vec::new();
    let exe_dir = std::env::current_exe().ok().and_then(|p| p.parent().map(Path::to_path_buf));
    if let Some(d) = &exe_dir {
        bases.push(d.join("models"));
        let checkout = d
            .ancestors()
            .skip(1)
            .take(8)
            .filter(|a| a.parent().is_some())
            .find(|a| a.join("Cargo.toml").is_file());
        if let Some(repo) = checkout {
            bases.push(repo.join("models"));
        }
    }
    if let Some(l) = std::env::var_os("LOCALAPPDATA") {
        bases.push(PathBuf::from(l).join("palette").join("models"));
    }
    bases
}

/// `models\<name>` holding `file`, in the folders of [`model_bases`].
pub fn find_models(name: &str, file: &str) -> Option<PathBuf> {
    model_bases().into_iter().map(|b| b.join(name)).find(|d| d.join(file).exists())
}

/// The OpenVINO runtime's folder, `models\openvino`.
pub fn find_dir() -> Option<PathBuf> {
    if let Some(p) = std::env::var_os("PALETTE_OPENVINO_DIR").map(PathBuf::from) {
        return p.join("openvino_c.dll").exists().then_some(p);
    }
    find_models("openvino", "openvino_c.dll")
}

impl Runtime {
    /// The runtime, loaded once per process.
    pub fn get() -> Result<&'static Runtime, String> {
        static RT: OnceLock<Result<Runtime, String>> = OnceLock::new();
        RT.get_or_init(|| {
            let dir = find_dir().ok_or("no OpenVINO runtime (scripts\\get-openvino.ps1)")?;
            unsafe { Runtime::load(&dir) }
        })
        .as_ref()
        .map_err(Clone::clone)
    }

    unsafe fn load(dir: &Path) -> Result<Runtime, String> {
        use windows::core::{PCSTR, PCWSTR};
        use windows::Win32::System::LibraryLoader::{GetProcAddress, LoadLibraryExW, LOAD_WITH_ALTERED_SEARCH_PATH};
        // Altered search path: openvino_c.dll's own dependencies are found beside it.
        let path = wide(&dir.join("openvino_c.dll"));
        let lib = LoadLibraryExW(PCWSTR(path.as_ptr()), None, LOAD_WITH_ALTERED_SEARCH_PATH)
            .map_err(|e| format!("load {}: {e}", dir.display()))?;
        macro_rules! f {
            ($name:literal) => {{
                let p = GetProcAddress(lib, PCSTR(concat!($name, "\0").as_ptr())).ok_or(concat!("missing ", $name))?;
                std::mem::transmute(p)
            }};
        }
        Ok(Runtime {
            core_create: f!("ov_core_create"),
            core_free: f!("ov_core_free"),
            core_get_available_devices: f!("ov_core_get_available_devices"),
            available_devices_free: f!("ov_available_devices_free"),
            core_set_properties: f!("ov_core_set_properties"),
            core_read_model_unicode: f!("ov_core_read_model_unicode"),
            core_compile_model_props: f!("ov_core_compile_model_props"),
            model_free: f!("ov_model_free"),
            model_reshape: f!("ov_model_reshape"),
            compiled_model_free: f!("ov_compiled_model_free"),
            compiled_model_create_infer_request: f!("ov_compiled_model_create_infer_request"),
            infer_request_free: f!("ov_infer_request_free"),
            infer_request_set_tensor: f!("ov_infer_request_set_tensor"),
            infer_request_infer: f!("ov_infer_request_infer"),
            infer_request_get_output_tensor_by_index: f!("ov_infer_request_get_output_tensor_by_index"),
            infer_request_get_tensor: f!("ov_infer_request_get_tensor"),
            tensor_create: f!("ov_tensor_create"),
            tensor_free: f!("ov_tensor_free"),
            tensor_set_shape: f!("ov_tensor_set_shape"),
            tensor_data: f!("ov_tensor_data"),
            tensor_get_shape: f!("ov_tensor_get_shape"),
            shape_free: f!("ov_shape_free"),
            get_last_err_msg: f!("ov_get_last_err_msg"),
            get_error_info: f!("ov_get_error_info"),
        })
    }

    fn check(&self, what: &str, s: Status) -> Result<(), String> {
        if s == 0 {
            return Ok(());
        }
        let msg = unsafe {
            let m = (self.get_last_err_msg)();
            let m = if m.is_null() || *m == 0 { (self.get_error_info)(s) } else { m };
            if m.is_null() { String::new() } else { CStr::from_ptr(m).to_string_lossy().trim().to_string() }
        };
        Err(format!("{what}: {msg} ({s})"))
    }
}

fn cstr(s: &str) -> CString {
    CString::new(s).unwrap_or_default()
}

/// Key/value properties as the C API wants them; the strings live as long as this.
struct Props {
    _strings: Vec<(CString, CString)>,
    raw: Vec<Property>,
}

impl Props {
    fn new(props: &[(&str, &str)]) -> Props {
        let owned: Vec<(CString, CString)> = props.iter().map(|(k, v)| (cstr(k), cstr(v))).collect();
        let raw = owned.iter().map(|(k, v)| Property { key: k.as_ptr(), value: v.as_ptr() as *const c_void }).collect();
        Props { _strings: owned, raw }
    }
}

pub struct Core {
    rt: &'static Runtime,
    p: P,
}

pub struct Model {
    rt: &'static Runtime,
    p: P,
}

pub struct Compiled {
    rt: &'static Runtime,
    p: P,
}

/// One inference at a time; move it between threads, share it behind a lock.
pub struct Request {
    rt: &'static Runtime,
    p: P,
}

unsafe impl Send for Core {}
unsafe impl Sync for Core {}
unsafe impl Send for Model {}
unsafe impl Send for Compiled {}
unsafe impl Sync for Compiled {}
unsafe impl Send for Request {}

impl Core {
    pub fn new() -> Result<Core, String> {
        let rt = Runtime::get()?;
        let mut p = std::ptr::null_mut();
        rt.check("core", unsafe { (rt.core_create)(&mut p) })?;
        Ok(Core { rt, p })
    }

    pub fn devices(&self) -> Vec<String> {
        let mut d = Devices { devices: std::ptr::null_mut(), size: 0 };
        if unsafe { (self.rt.core_get_available_devices)(self.p, &mut d) } != 0 {
            return Vec::new();
        }
        let out = (0..d.size)
            .map(|i| unsafe { CStr::from_ptr(*d.devices.add(i)).to_string_lossy().into_owned() })
            .collect();
        unsafe { (self.rt.available_devices_free)(&mut d) };
        out
    }

    /// Is `device` ("NPU", "GPU", "CPU") present?
    pub fn has(&self, device: &str) -> bool {
        self.devices().iter().any(|d| d == device || d.starts_with(&format!("{device}.")))
    }

    /// Properties for `device`, or for every device when it is None (e.g. CACHE_DIR).
    pub fn set(&self, device: Option<&str>, props: &[(&str, &str)]) -> Result<(), String> {
        let pr = Props::new(props);
        let dev = device.map(cstr);
        let dp = dev.as_ref().map_or(std::ptr::null(), |d| d.as_ptr());
        self.rt.check("set property", unsafe { (self.rt.core_set_properties)(self.p, dp, pr.raw.len(), pr.raw.as_ptr()) })
    }

    /// An IR (.xml beside its .bin) or ONNX file.
    pub fn read(&self, path: &Path) -> Result<Model, String> {
        let w = wide(path);
        let mut p = std::ptr::null_mut();
        self.rt.check("read model", unsafe { (self.rt.core_read_model_unicode)(self.p, w.as_ptr(), std::ptr::null(), &mut p) })?;
        Ok(Model { rt: self.rt, p })
    }

    pub fn compile(&self, model: &Model, device: &str, props: &[(&str, &str)]) -> Result<Compiled, String> {
        let pr = Props::new(props);
        let dev = cstr(device);
        let mut p = std::ptr::null_mut();
        self.rt.check(&format!("compile for {device}"), unsafe {
            (self.rt.core_compile_model_props)(self.p, model.p, dev.as_ptr(), pr.raw.len(), pr.raw.as_ptr(), &mut p)
        })?;
        Ok(Compiled { rt: self.rt, p })
    }
}

impl Model {
    /// Give inputs fixed shapes, all at once (inputs that must agree, such as ids and their mask,
    /// cannot be changed one by one).
    pub fn reshape(&self, inputs: &[(&str, &[i64])]) -> Result<(), String> {
        let names: Vec<CString> = inputs.iter().map(|(n, _)| cstr(n)).collect();
        let name_ptrs: Vec<*const c_char> = names.iter().map(|c| c.as_ptr()).collect();
        let dims: Vec<Vec<Dim>> = inputs.iter().map(|(_, d)| d.iter().map(|&x| Dim { min: x, max: x }).collect()).collect();
        let shapes: Vec<PartialShape> = dims
            .iter()
            .map(|d| PartialShape { rank: Dim { min: d.len() as i64, max: d.len() as i64 }, dims: d.as_ptr() })
            .collect();
        self.rt.check("reshape", unsafe { (self.rt.model_reshape)(self.p, name_ptrs.as_ptr(), shapes.as_ptr(), shapes.len()) })
    }
}

impl Compiled {
    pub fn request(&self) -> Result<Request, String> {
        let mut p = std::ptr::null_mut();
        self.rt.check("infer request", unsafe { (self.rt.compiled_model_create_infer_request)(self.p, &mut p) })?;
        Ok(Request { rt: self.rt, p })
    }
}

impl Request {
    /// Input `name` gets `shape` and `data`. The request's own tensor is reshaped and filled in
    /// place: handing the NPU a new tensor every step costs it a rebinding, which for one token
    /// of a small model is most of the step.
    fn set<T: Copy>(&mut self, name: &str, elem: Elem, shape: &[i64], data: &[T]) -> Result<(), String> {
        let rt = self.rt;
        let n: i64 = shape.iter().product();
        if n as usize != data.len() {
            return Err(format!("{name}: {} values for shape {shape:?}", data.len()));
        }
        let mut dims = shape.to_vec();
        let c = cstr(name);
        let mut t = std::ptr::null_mut();
        let own = unsafe { (rt.infer_request_get_tensor)(self.p, c.as_ptr(), &mut t) } == 0
            && unsafe { (rt.tensor_set_shape)(t, Shape { rank: dims.len() as i64, dims: dims.as_mut_ptr() }) } == 0;
        if !own {
            if !t.is_null() {
                unsafe { (rt.tensor_free)(t) };
            }
            rt.check(&format!("tensor {name}"), unsafe {
                (rt.tensor_create)(elem, Shape { rank: dims.len() as i64, dims: dims.as_mut_ptr() }, &mut t)
            })?;
        }
        // The request holds its own reference to the tensor, so ours is freed here.
        let r = (|| {
            if !data.is_empty() {
                let mut ptr = std::ptr::null_mut();
                rt.check("tensor data", unsafe { (rt.tensor_data)(t, &mut ptr) })?;
                unsafe { std::ptr::copy_nonoverlapping(data.as_ptr(), ptr as *mut T, data.len()) };
            }
            if own {
                return Ok(());
            }
            rt.check(&format!("set {name}"), unsafe { (rt.infer_request_set_tensor)(self.p, c.as_ptr(), t) })
        })();
        unsafe { (rt.tensor_free)(t) };
        r
    }

    pub fn set_i64(&mut self, name: &str, shape: &[i64], data: &[i64]) -> Result<(), String> {
        self.set(name, Elem::I64, shape, data)
    }

    pub fn set_i32(&mut self, name: &str, shape: &[i64], data: &[i32]) -> Result<(), String> {
        self.set(name, Elem::I32, shape, data)
    }

    pub fn set_f32(&mut self, name: &str, shape: &[i64], data: &[f32]) -> Result<(), String> {
        self.set(name, Elem::F32, shape, data)
    }

    pub fn infer(&mut self) -> Result<(), String> {
        self.rt.check("infer", unsafe { (self.rt.infer_request_infer)(self.p) })
    }

    /// Output `idx` (f32) and its shape.
    pub fn output(&self, idx: usize) -> Result<(Vec<i64>, Vec<f32>), String> {
        let mut t = std::ptr::null_mut();
        self.rt.check("output", unsafe { (self.rt.infer_request_get_output_tensor_by_index)(self.p, idx, &mut t) })?;
        self.read_f32(t)
    }

    /// The output named `name` (f32) and its shape.
    pub fn output_named(&self, name: &str) -> Result<(Vec<i64>, Vec<f32>), String> {
        let c = cstr(name);
        let mut t = std::ptr::null_mut();
        self.rt.check(&format!("output {name}"), unsafe { (self.rt.infer_request_get_tensor)(self.p, c.as_ptr(), &mut t) })?;
        self.read_f32(t)
    }

    /// Copies tensor `t` out and frees our handle to it.
    fn read_f32(&self, t: P) -> Result<(Vec<i64>, Vec<f32>), String> {
        let rt = self.rt;
        let r = (|| {
            let mut s = Shape { rank: 0, dims: std::ptr::null_mut() };
            rt.check("output shape", unsafe { (rt.tensor_get_shape)(t, &mut s) })?;
            let shape: Vec<i64> = (0..s.rank as usize).map(|i| unsafe { *s.dims.add(i) }).collect();
            unsafe { (rt.shape_free)(&mut s) };
            let n = shape.iter().product::<i64>() as usize;
            let mut ptr = std::ptr::null_mut();
            rt.check("output data", unsafe { (rt.tensor_data)(t, &mut ptr) })?;
            let v = if n == 0 { Vec::new() } else { unsafe { std::slice::from_raw_parts(ptr as *const f32, n) }.to_vec() };
            Ok((shape, v))
        })();
        unsafe { (rt.tensor_free)(t) };
        r
    }
}

impl Drop for Core {
    fn drop(&mut self) {
        unsafe { (self.rt.core_free)(self.p) }
    }
}
impl Drop for Model {
    fn drop(&mut self) {
        unsafe { (self.rt.model_free)(self.p) }
    }
}
impl Drop for Compiled {
    fn drop(&mut self) {
        unsafe { (self.rt.compiled_model_free)(self.p) }
    }
}
impl Drop for Request {
    fn drop(&mut self) {
        unsafe { (self.rt.infer_request_free)(self.p) }
    }
}

/// Where compiled models are kept between runs: compiling for the NPU takes seconds to a minute,
/// loading a compiled one a fraction of a second.
pub fn cache_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_else(std::env::temp_dir);
    let d = base.join("palette").join("npu-cache");
    let _ = std::fs::create_dir_all(&d);
    d
}

/// A name for `model` compiled as `variant`: changes when the model file does.
pub fn cache_key(model: &Path, variant: &str) -> String {
    let m = std::fs::metadata(model).ok();
    let len = m.as_ref().map_or(0, |m| m.len());
    let modified = m
        .and_then(|m| m.modified().ok())
        .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
        .map_or(0, |d| d.as_secs());
    let stem = model.file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or_default();
    format!("{stem}-{len}-{modified}-{variant}")
}

/// Was `key` compiled before (so loading it now is quick)?
pub fn is_cached(key: &str) -> bool {
    cache_dir().join(format!("{key}.ok")).exists()
}

pub fn set_cached(key: &str) {
    let _ = std::fs::write(cache_dir().join(format!("{key}.ok")), b"");
}
