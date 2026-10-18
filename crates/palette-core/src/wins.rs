//! Open windows, for the window switcher: the same set Alt+Tab offers, listed fresh on every
//! search (enumerating a few dozen windows takes well under a millisecond).

use std::collections::HashMap;
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, Instant};
use windows::core::{GUID, PWSTR};
use windows::Win32::Foundation::{CloseHandle, HWND, LPARAM, PROPERTYKEY};
use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_CLOAKED};
use windows::Win32::System::Com::StructuredStorage::PropVariantToStringAlloc;
use windows::Win32::System::Com::CoTaskMemFree;
use windows::Win32::System::Threading::{
    GetCurrentProcessId, OpenProcess, QueryFullProcessImageNameW, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
};
use windows::Win32::UI::Shell::PropertiesSystem::{IPropertyStore, SHGetPropertyStoreForWindow};
use windows::Win32::UI::WindowsAndMessaging::{
    EnumWindows, GetWindow, GetWindowLongPtrW, GetWindowTextLengthW, GetWindowTextW, GetWindowThreadProcessId, IsIconic,
    IsWindow, IsWindowVisible, SetForegroundWindow, ShowWindow, GWL_EXSTYLE, GW_OWNER, SW_RESTORE, WS_EX_APPWINDOW,
    WS_EX_NOACTIVATE, WS_EX_TOOLWINDOW,
};

pub struct OpenWindow {
    pub hwnd: isize,
    pub title: String,
    /// The app's name ("Google Chrome"), empty when unknown.
    pub app: String,
    /// What to take the icon from: `shell:AppsFolder\<AUMID>` for packaged apps, else the exe.
    pub icon: String,
}

/// Switchable top-level windows, front to back (Z order, as Alt+Tab lists them).
pub fn list() -> Vec<OpenWindow> {
    let mut hwnds: Vec<HWND> = Vec::new();
    unsafe {
        let _ = EnumWindows(Some(collect), LPARAM(&mut hwnds as *mut _ as isize));
    }
    let me = unsafe { GetCurrentProcessId() };
    let mut owners = OWNERS.get_or_init(Default::default).lock().unwrap();
    // Forget windows that have closed (their handles may be reused).
    owners.retain(|h, _| hwnds.iter().any(|w| w.0 as isize == *h));
    hwnds
        .into_iter()
        .filter_map(|h| unsafe { describe(h, me, &mut owners) })
        .collect()
}

/// What a window says about its program: its process, exe and AppUserModelID. Opening the process
/// and asking the shell for the ID are most of what listing windows costs, and a search lists them
/// on every keystroke, so they are kept per window for a while (an app can set its ID late).
struct Owner {
    pid: u32,
    exe: String,
    aumid: Option<String>,
    at: Instant,
}

const OWNER_FRESH: Duration = Duration::from_secs(30);

static OWNERS: OnceLock<Mutex<HashMap<isize, Owner>>> = OnceLock::new();

unsafe extern "system" fn collect(h: HWND, l: LPARAM) -> windows::core::BOOL {
    let v = &mut *(l.0 as *mut Vec<HWND>);
    v.push(h);
    true.into()
}

unsafe fn describe(h: HWND, me: u32, owners: &mut HashMap<isize, Owner>) -> Option<OpenWindow> {
    if !IsWindowVisible(h).as_bool() {
        return None;
    }
    let ex = GetWindowLongPtrW(h, GWL_EXSTYLE) as u32;
    let app_window = ex & WS_EX_APPWINDOW.0 != 0;
    // Owned windows (dialogs, palettes) and tool windows are not in Alt+Tab unless they ask to be.
    let owned = GetWindow(h, GW_OWNER).is_ok_and(|o| !o.is_invalid());
    if !app_window && (owned || ex & WS_EX_TOOLWINDOW.0 != 0) {
        return None;
    }
    if ex & WS_EX_NOACTIVATE.0 != 0 {
        return None;
    }
    // Cloaked: on another virtual desktop, or a suspended Store app's leftover frame.
    let mut cloaked = 0u32;
    if DwmGetWindowAttribute(h, DWMWA_CLOAKED, &mut cloaked as *mut _ as *mut _, 4).is_ok() && cloaked != 0 {
        return None;
    }
    let len = GetWindowTextLengthW(h);
    if len <= 0 {
        return None;
    }
    let mut buf = vec![0u16; len as usize + 1];
    let n = GetWindowTextW(h, &mut buf);
    let title = String::from_utf16_lossy(&buf[..n.max(0) as usize]).trim().to_string();
    if title.is_empty() {
        return None;
    }
    let mut pid = 0u32;
    GetWindowThreadProcessId(h, Some(&mut pid));
    if pid == me {
        return None; // the palette itself
    }
    let key = h.0 as isize;
    if !owners.get(&key).is_some_and(|o| o.pid == pid && o.at.elapsed() < OWNER_FRESH) {
        let o = Owner { pid, exe: exe_path(pid).unwrap_or_default(), aumid: app_user_model_id(h), at: Instant::now() };
        owners.insert(key, o);
    }
    let owner = &owners[&key];
    let exe = &owner.exe;
    if exe.is_empty() && title == "Program Manager" {
        return None;
    }
    let frame_host = exe.to_lowercase().ends_with("\\applicationframehost.exe");
    let app = if frame_host { String::new() } else { app_name(exe) };
    let icon = match &owner.aumid {
        Some(id) => format!("shell:AppsFolder\\{id}"),
        None => exe.clone(),
    };
    Some(OpenWindow { hwnd: h.0 as isize, title, app, icon })
}

fn exe_path(pid: u32) -> Option<String> {
    unsafe {
        let p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 1024];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(p, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
        let _ = CloseHandle(p);
        ok.then(|| String::from_utf16_lossy(&buf[..len as usize]))
    }
}

/// The window's explicit AppUserModelID (Store apps, and desktop apps that set one).
fn app_user_model_id(h: HWND) -> Option<String> {
    const PKEY_APPUSERMODEL_ID: PROPERTYKEY = PROPERTYKEY {
        fmtid: GUID::from_u128(0x9F4C2855_9F79_4B39_A8D0_E1D42DE1D5F3),
        pid: 5,
    };
    unsafe {
        let store: IPropertyStore = SHGetPropertyStoreForWindow(h).ok()?;
        let v = store.GetValue(&PKEY_APPUSERMODEL_ID).ok()?;
        let p = PropVariantToStringAlloc(&v).ok()?;
        let s = p.to_string().ok();
        CoTaskMemFree(Some(p.0 as *const _));
        s.filter(|s| !s.is_empty())
    }
}

/// The exe's product description ("Google Chrome"), falling back to its file name. Cached.
fn app_name(exe: &str) -> String {
    static CACHE: OnceLock<Mutex<HashMap<String, String>>> = OnceLock::new();
    let cache = CACHE.get_or_init(|| Mutex::new(HashMap::new()));
    if let Some(n) = cache.lock().unwrap().get(exe) {
        return n.clone();
    }
    let name = file_description(exe).unwrap_or_else(|| {
        std::path::Path::new(exe).file_stem().and_then(|s| s.to_str()).unwrap_or("").to_string()
    });
    cache.lock().unwrap().insert(exe.to_string(), name.clone());
    name
}

fn file_description(exe: &str) -> Option<String> {
    use windows::core::PCWSTR;
    use windows::Win32::Storage::FileSystem::{GetFileVersionInfoSizeW, GetFileVersionInfoW, VerQueryValueW};
    let w: Vec<u16> = exe.encode_utf16().chain(std::iter::once(0)).collect();
    unsafe {
        let size = GetFileVersionInfoSizeW(PCWSTR(w.as_ptr()), None);
        if size == 0 {
            return None;
        }
        let mut data = vec![0u8; size as usize];
        GetFileVersionInfoW(PCWSTR(w.as_ptr()), None, size, data.as_mut_ptr() as *mut _).ok()?;
        // The first language/code page in the translation table.
        let mut ptr: *mut core::ffi::c_void = std::ptr::null_mut();
        let mut len = 0u32;
        let tr: Vec<u16> = "\\VarFileInfo\\Translation\0".encode_utf16().collect();
        if !VerQueryValueW(data.as_ptr() as *const _, PCWSTR(tr.as_ptr()), &mut ptr, &mut len).as_bool() || len < 4 {
            return None;
        }
        let lang = *(ptr as *const u16);
        let cp = *(ptr as *const u16).add(1);
        let key: Vec<u16> = format!("\\StringFileInfo\\{lang:04x}{cp:04x}\\FileDescription\0").encode_utf16().collect();
        if !VerQueryValueW(data.as_ptr() as *const _, PCWSTR(key.as_ptr()), &mut ptr, &mut len).as_bool() || len == 0 {
            return None;
        }
        let s = std::slice::from_raw_parts(ptr as *const u16, len as usize);
        let s = String::from_utf16_lossy(s).trim_end_matches('\0').trim().to_string();
        (!s.is_empty()).then_some(s)
    }
}

/// Bring a window to the front, restoring it if it is minimised. Works because the palette is
/// the foreground window when this runs, which allows it to hand the foreground on.
pub fn focus(hwnd: isize) {
    let h = HWND(hwnd as *mut _);
    unsafe {
        if !IsWindow(Some(h)).as_bool() {
            return;
        }
        if IsIconic(h).as_bool() {
            let _ = ShowWindow(h, SW_RESTORE);
        }
        let _ = SetForegroundWindow(h);
    }
}
