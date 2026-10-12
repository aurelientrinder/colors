//! Shell icon extraction (shared by every UI shell).
//!
//! * apps      -> the icon of the shortcut / Store app
//! * folders   -> the Explorer folder icon
//! * files     -> the icon of the app associated with the extension
//!                (exe/lnk/ico/msi use their own icon)
//!
//! Extraction runs on one dedicated STA thread; callers block on a reply.

use crate::model::{Action, Hit, Kind};
use std::sync::mpsc::{channel, Sender};
use std::sync::{Mutex, OnceLock};
use windows::core::PCWSTR;
use windows::Win32::Foundation::HWND;
use windows::Win32::Graphics::Gdi::{
    CreateCompatibleDC, CreateDIBSection, DeleteDC, DeleteObject, GdiFlush, GetDC, ReleaseDC,
    SelectObject, BITMAPINFO, BITMAPINFOHEADER, BI_RGB, DIB_RGB_COLORS,
};
use windows::Win32::Storage::FileSystem::{FILE_ATTRIBUTE_DIRECTORY, FILE_ATTRIBUTE_NORMAL};
use windows::Win32::System::Com::{CoInitializeEx, CoTaskMemFree, COINIT_APARTMENTTHREADED};
use windows::Win32::UI::Controls::{IImageList, ILD_TRANSPARENT};
use windows::Win32::UI::Shell::{
    SHGetFileInfoW, SHGetImageList, SHParseDisplayName, SHFILEINFOW, SHGFI_PIDL,
    SHGFI_SYSICONINDEX, SHGFI_USEFILEATTRIBUTES, SHIL_EXTRALARGE,
};
use windows::Win32::UI::WindowsAndMessaging::{DestroyIcon, DrawIconEx, DI_NORMAL};

pub const ICON_SIZE: i32 = 48;
const SIZE: i32 = ICON_SIZE;

type Job = (Src, Sender<Option<Vec<u8>>>);

fn worker() -> &'static Mutex<Sender<Job>> {
    static W: OnceLock<Mutex<Sender<Job>>> = OnceLock::new();
    W.get_or_init(|| {
        let (tx, rx) = channel::<Job>();
        std::thread::spawn(move || unsafe {
            let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
            while let Ok((src, reply)) = rx.recv() {
                let _ = reply.send(extract_raw(&src));
            }
        });
        Mutex::new(tx)
    })
}

/// Stable cache key for this hit's icon (identical keys share one image).
pub fn icon_key(hit: &Hit) -> Option<String> {
    source_for(hit).map(|(k, _)| k)
}

/// Premultiplied RGBA, ICON_SIZE x ICON_SIZE. Blocks until extracted.
pub fn icon_rgba(hit: &Hit) -> Option<Vec<u8>> {
    let (_, src) = source_for(hit)?;
    let (tx, rx) = channel();
    worker().lock().ok()?.send((src, tx)).ok()?;
    rx.recv().ok().flatten()
}

const SETTINGS_APP: &str =
    "shell:AppsFolder\\windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

#[derive(Clone)]
pub enum Src {
    /// A real path (exe, lnk, ...).
    Path(String),
    /// A shell parsing name such as shell:AppsFolder\<AUMID>.
    Shell(String),
    /// Icon by extension / folder, without touching the disk.
    Attrs { name: String, dir: bool },
}

pub fn source_for(hit: &Hit) -> Option<(String, Src)> {
    match (hit.kind, &hit.action) {
        (Kind::Setting, _) => Some(("settings".into(), Src::Shell(SETTINGS_APP.into()))),
        (Kind::Folder, _) => Some(("dir".into(), Src::Attrs { name: "folder".into(), dir: true })),
        // Same keys as the app rows, so a window shares its app's cached icon.
        (Kind::Window, Action::Focus { icon, .. }) if icon.starts_with("shell:") => Some((icon.clone(), Src::Shell(icon.clone()))),
        (Kind::Window, Action::Focus { icon, .. }) if !icon.is_empty() => Some((format!("path:{icon}"), Src::Path(icon.clone()))),
        (Kind::App, Action::Open(t)) => {
            if t.starts_with("shell:") {
                Some((t.clone(), Src::Shell(t.clone())))
            } else {
                Some((format!("path:{t}"), Src::Path(resolve_bare(t))))
            }
        }
        (Kind::File, Action::Open(p)) => {
            let ext = std::path::Path::new(p)
                .extension()
                .and_then(|e| e.to_str())
                .map(|e| e.to_lowercase())
                .unwrap_or_default();
            if matches!(ext.as_str(), "exe" | "lnk" | "ico" | "msi" | "url" | "scr" | "cur") {
                Some((format!("path:{p}"), Src::Path(p.clone())))
            } else {
                Some((format!("ext:{ext}"), Src::Attrs { name: format!("file.{ext}"), dir: false }))
            }
        }
        _ => None,
    }
}

/// "taskmgr.exe" -> C:\Windows\System32\taskmgr.exe
fn resolve_bare(t: &str) -> String {
    if t.contains('\\') || t.contains('/') || t.contains(':') {
        return t.to_string();
    }
    let root = std::env::var("SystemRoot").unwrap_or_else(|_| "C:\\Windows".into());
    let p = format!("{root}\\System32\\{t}");
    if std::path::Path::new(&p).exists() {
        p
    } else {
        t.to_string()
    }
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

unsafe fn icon_index(src: &Src) -> Option<i32> {
    let mut sfi = SHFILEINFOW::default();
    let size = std::mem::size_of::<SHFILEINFOW>() as u32;
    let ok = match src {
        Src::Path(p) => {
            let w = wide(p);
            SHGetFileInfoW(PCWSTR(w.as_ptr()), Default::default(), Some(&mut sfi), size, SHGFI_SYSICONINDEX)
        }
        Src::Attrs { name, dir } => {
            let w = wide(name);
            let attr = if *dir { FILE_ATTRIBUTE_DIRECTORY } else { FILE_ATTRIBUTE_NORMAL };
            SHGetFileInfoW(
                PCWSTR(w.as_ptr()),
                attr,
                Some(&mut sfi),
                size,
                SHGFI_SYSICONINDEX | SHGFI_USEFILEATTRIBUTES,
            )
        }
        Src::Shell(name) => {
            let w = wide(name);
            let mut pidl = std::ptr::null_mut();
            SHParseDisplayName(PCWSTR(w.as_ptr()), None, &mut pidl, 0, None).ok()?;
            let r = SHGetFileInfoW(
                PCWSTR(pidl as *const u16),
                Default::default(),
                Some(&mut sfi),
                size,
                SHGFI_PIDL | SHGFI_SYSICONINDEX,
            );
            CoTaskMemFree(Some(pidl as *const _));
            r
        }
    };
    (ok != 0).then_some(sfi.iIcon)
}

/// Returns premultiplied RGBA at SIZE x SIZE.
unsafe fn extract_raw(src: &Src) -> Option<Vec<u8>> {
    let idx = icon_index(src)?;
    let list: IImageList = SHGetImageList(SHIL_EXTRALARGE as i32).ok()?;
    let hicon = list.GetIcon(idx, ILD_TRANSPARENT.0).ok()?;

    // Render on black, then on white; the difference recovers alpha.
    let screen = GetDC(None);
    let dc = CreateCompatibleDC(Some(screen));
    let mut bmi = BITMAPINFO::default();
    bmi.bmiHeader = BITMAPINFOHEADER {
        biSize: std::mem::size_of::<BITMAPINFOHEADER>() as u32,
        biWidth: SIZE,
        biHeight: -SIZE,
        biPlanes: 1,
        biBitCount: 32,
        biCompression: BI_RGB.0,
        ..Default::default()
    };
    let mut bits: *mut std::ffi::c_void = std::ptr::null_mut();
    let bmp = CreateDIBSection(Some(dc), &bmi, DIB_RGB_COLORS, &mut bits, None, 0);
    let result = match bmp {
        Ok(bmp) if !bits.is_null() => {
            let old = SelectObject(dc, bmp.into());
            let n = (SIZE * SIZE * 4) as usize;
            let buf = std::slice::from_raw_parts_mut(bits as *mut u8, n);

            buf.fill(0);
            let _ = DrawIconEx(dc, 0, 0, hicon, SIZE, SIZE, 0, None, DI_NORMAL);
            let _ = GdiFlush();
            let black = buf.to_vec();

            buf.fill(255);
            let _ = DrawIconEx(dc, 0, 0, hicon, SIZE, SIZE, 0, None, DI_NORMAL);
            let _ = GdiFlush();
            let white = buf.to_vec();

            let mut out = vec![0u8; n];
            for i in (0..n).step_by(4) {
                let d = (0..3).map(|c| white[i + c].saturating_sub(black[i + c])).max().unwrap_or(0);
                let a = 255 - d;
                out[i] = black[i + 2].min(a); // R (BGRA -> RGBA, premultiplied)
                out[i + 1] = black[i + 1].min(a);
                out[i + 2] = black[i].min(a);
                out[i + 3] = a;
            }
            SelectObject(dc, old);
            let _ = DeleteObject(bmp.into());
            Some(out)
        }
        _ => None,
    };
    let _ = DeleteDC(dc);
    ReleaseDC(None::<HWND>, screen);
    let _ = DestroyIcon(hicon);
    result
}
