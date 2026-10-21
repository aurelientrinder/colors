//! palette-mft: elevated helper that snapshots every NTFS volume's file list.
//!
//! It enumerates the master file table through FSCTL_ENUM_USN_DATA (needs
//! admin), resolves full paths, drops noise (system dirs, build junk, binaries)
//! and writes one text file per drive: `<LOCALAPPDATA>\palette\index\<X>.txt`,
//! one path per line, directories ending in `\`.
//!
//! Usage:
//!   palette-mft --snapshot        write snapshots for all fixed NTFS drives
//!   palette-mft --install-task    register a logon task so no UAC is needed later
//!
//! It runs elevated, so it is careful about two things an unelevated program could otherwise turn
//! into admin rights. The logon task runs a copy kept in Program Files (only admins can change it),
//! never the exe it was started as, which may sit in a folder the user can write to. And the
//! snapshots are written with the admin rights dropped (as the user, at medium integrity): the index
//! folder is the user's, so a link planted there must not let an elevated write land anywhere else.
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

use std::collections::HashMap;
use std::fs;
use std::io::{BufWriter, Write};
use std::os::windows::process::CommandExt;
use std::path::PathBuf;
use std::process::Command;

use windows::core::PCWSTR;
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::Security::{
    CreateRestrictedToken, CreateWellKnownSid, ImpersonateLoggedOnUser, RevertToSelf, SetTokenInformation,
    TokenIntegrityLevel, WinBuiltinAdministratorsSid, WinMediumLabelSid, CREATE_RESTRICTED_TOKEN_FLAGS, PSID,
    SID_AND_ATTRIBUTES, TOKEN_ADJUST_DEFAULT, TOKEN_ASSIGN_PRIMARY, TOKEN_DUPLICATE, TOKEN_IMPERSONATE, TOKEN_MANDATORY_LABEL, TOKEN_QUERY,
    WELL_KNOWN_SID_TYPE,
};
use windows::Win32::System::SystemInformation::GetSystemWindowsDirectoryW;
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::Win32::Storage::FileSystem::{
    CreateFileW, GetDriveTypeW, FILE_FLAGS_AND_ATTRIBUTES, FILE_SHARE_READ, FILE_SHARE_WRITE,
    OPEN_EXISTING,
};
use windows::Win32::System::IO::DeviceIoControl;

const FSCTL_ENUM_USN_DATA: u32 = 0x0009_00B3;
const DRIVE_FIXED: u32 = 3;
const GENERIC_READ: u32 = 0x8000_0000;
const ATTR_DIR: u32 = 0x10;
const ATTR_SYSTEM: u32 = 0x4;
const NO_WINDOW: u32 = 0x0800_0000;

/// Directory names (lowercase) never descended into, at any depth.
const SKIP_DIRS: &[&str] = &[
    "$recycle.bin",
    "system volume information",
    "$windows.~bt",
    "$windows.~ws",
    "node_modules",
    ".git",
    "__pycache__",
    "site-packages",
    ".venv",
    "appdata",
    "programdata",
    "windows",
    "program files",
    "program files (x86)",
    "recovery",
    "perflogs",
    "$sysreset",
    "windowsapps",
    "miniconda3",
    "anaconda3",
    "miniforge3",
    "vcpkg",
    "$recycle.bin.old",
];

/// File extensions (lowercase) that are pure noise for a launcher.
const SKIP_EXT: &[&str] = &[
    "dll", "sys", "mui", "pdb", "tmp", "cat", "manifest", "mum", "lib", "obj", "pyc", "pyo", "o",
    "d", "rlib", "rmeta", "etl", "log", "dat", "idx", "pack", "lock", "map", "cache",
];

struct Rec {
    parent: u64,
    name: String,
    attrs: u32,
}

fn out_dir() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA").map(PathBuf::from).unwrap_or_default();
    base.join("palette").join("index")
}

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

fn enumerate_volume(letter: char) -> Option<HashMap<u64, Rec>> {
    let path = wide(&format!("\\\\.\\{letter}:"));
    let handle: HANDLE = unsafe {
        CreateFileW(
            PCWSTR(path.as_ptr()),
            GENERIC_READ,
            FILE_SHARE_READ | FILE_SHARE_WRITE,
            None,
            OPEN_EXISTING,
            FILE_FLAGS_AND_ATTRIBUTES(0),
            None,
        )
        .ok()?
    };

    // MFT_ENUM_DATA_V0 { StartFileReferenceNumber, LowUsn, HighUsn }
    let mut input = [0u8; 24];
    input[16..24].copy_from_slice(&i64::MAX.to_le_bytes());
    let mut buf = vec![0u8; 1 << 20];
    let mut map: HashMap<u64, Rec> = HashMap::with_capacity(1 << 20);

    loop {
        let mut returned = 0u32;
        let res = unsafe {
            DeviceIoControl(
                handle,
                FSCTL_ENUM_USN_DATA,
                Some(input.as_ptr() as *const _),
                input.len() as u32,
                Some(buf.as_mut_ptr() as *mut _),
                buf.len() as u32,
                Some(&mut returned),
                None,
            )
        };
        if res.is_err() || returned < 8 {
            break; // ERROR_HANDLE_EOF ends the enumeration
        }
        // First 8 bytes: next start FRN. Then packed USN_RECORD_V2 entries.
        input[0..8].copy_from_slice(&buf[0..8]);
        let mut off = 8usize;
        let end = returned as usize;
        while off + 60 <= end {
            let len = u32::from_le_bytes(buf[off..off + 4].try_into().unwrap()) as usize;
            if len == 0 || off + len > end {
                break;
            }
            let major = u16::from_le_bytes(buf[off + 4..off + 6].try_into().unwrap());
            if major == 2 {
                let frn = u64::from_le_bytes(buf[off + 8..off + 16].try_into().unwrap());
                let parent = u64::from_le_bytes(buf[off + 16..off + 24].try_into().unwrap());
                let attrs = u32::from_le_bytes(buf[off + 52..off + 56].try_into().unwrap());
                let nlen = u16::from_le_bytes(buf[off + 56..off + 58].try_into().unwrap()) as usize;
                let noff = u16::from_le_bytes(buf[off + 58..off + 60].try_into().unwrap()) as usize;
                if off + noff + nlen <= end {
                    let units: Vec<u16> = buf[off + noff..off + noff + nlen]
                        .chunks_exact(2)
                        .map(|c| u16::from_le_bytes([c[0], c[1]]))
                        .collect();
                    map.insert(
                        frn,
                        Rec { parent, name: String::from_utf16_lossy(&units), attrs },
                    );
                }
            }
            off += len;
        }
    }
    unsafe {
        let _ = CloseHandle(handle);
    }
    Some(map)
}

const ROOT_MASK: u64 = 0x0000_FFFF_FFFF_FFFF;

/// Resolve the directory path for `frn`; `None` means excluded or orphaned.
fn dir_path(
    frn: u64,
    map: &HashMap<u64, Rec>,
    memo: &mut HashMap<u64, Option<String>>,
    root: &str,
) -> Option<String> {
    // Walk up until we hit a memoised dir or the volume root.
    let mut chain: Vec<u64> = Vec::new();
    let mut cur = frn;
    let mut base: Option<String>;
    loop {
        if let Some(m) = memo.get(&cur) {
            base = m.clone();
            if base.is_none() {
                // Excluded ancestor: poison the whole chain.
                for c in chain {
                    memo.insert(c, None);
                }
                return None;
            }
            break;
        }
        if cur & ROOT_MASK == 5 {
            base = Some(root.to_string());
            break;
        }
        match map.get(&cur) {
            Some(r) if !chain.contains(&cur) && chain.len() < 256 => {
                chain.push(cur);
                cur = r.parent;
            }
            _ => {
                for c in chain {
                    memo.insert(c, None);
                }
                return None;
            }
        }
    }
    // Build downward.
    for &c in chain.iter().rev() {
        let r = &map[&c];
        let lname = r.name.to_lowercase();
        let p = if SKIP_DIRS.contains(&lname.as_str()) || lname.starts_with('.') {
            None
        } else {
            base.as_ref().map(|b| format!("{b}\\{}", r.name))
        };
        memo.insert(c, p.clone());
        base = p;
        if base.is_none() {
            // Remaining chain entries (descendants) are excluded too.
        }
    }
    base
}

const DISABLE_MAX_PRIVILEGE: u32 = 0x1;
const LUA_TOKEN: u32 = 0x4;
const SE_GROUP_INTEGRITY: u32 = 0x20;

/// A well-known SID, in a buffer large enough for any.
fn well_known_sid(kind: WELL_KNOWN_SID_TYPE) -> Option<Vec<u8>> {
    let mut buf = vec![0u8; 68];
    let mut len = buf.len() as u32;
    unsafe { CreateWellKnownSid(kind, None, Some(PSID(buf.as_mut_ptr() as *mut _)), &mut len).ok()? };
    Some(buf)
}

/// This process's token with Administrators made deny-only, every privilege but the basic ones gone,
/// and medium integrity: what the user has unelevated. (The new token has the access of the handle it
/// is made from, hence adjust-default, to set its integrity, and impersonate.)
fn unelevated_token() -> Option<HANDLE> {
    unsafe {
        let mut own = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ASSIGN_PRIMARY | TOKEN_ADJUST_DEFAULT | TOKEN_IMPERSONATE, &mut own).ok()?;
        let mut admins = well_known_sid(WinBuiltinAdministratorsSid)?;
        let disable = [SID_AND_ATTRIBUTES { Sid: PSID(admins.as_mut_ptr() as *mut _), Attributes: 0 }];
        let mut low = HANDLE::default();
        let made = CreateRestrictedToken(
            own,
            CREATE_RESTRICTED_TOKEN_FLAGS(DISABLE_MAX_PRIVILEGE | LUA_TOKEN),
            Some(&disable),
            None,
            None,
            &mut low,
        );
        let _ = CloseHandle(own);
        made.ok()?;
        let mut medium = well_known_sid(WinMediumLabelSid)?;
        let label = TOKEN_MANDATORY_LABEL {
            Label: SID_AND_ATTRIBUTES { Sid: PSID(medium.as_mut_ptr() as *mut _), Attributes: SE_GROUP_INTEGRITY },
        };
        let set = SetTokenInformation(
            low,
            TokenIntegrityLevel,
            &label as *const _ as *const _,
            (std::mem::size_of::<TOKEN_MANDATORY_LABEL>() + medium.len()) as u32,
        );
        if set.is_err() {
            let _ = CloseHandle(low);
            return None;
        }
        Some(low)
    }
}

/// Runs `f` as the unelevated user; not at all (an error) if the rights cannot be dropped.
fn as_user<T>(f: impl FnOnce() -> std::io::Result<T>) -> std::io::Result<T> {
    let Some(low) = unelevated_token() else { return Err(std::io::Error::other("could not drop admin rights")) };
    let imp = unsafe { ImpersonateLoggedOnUser(low) };
    unsafe {
        let _ = CloseHandle(low);
    }
    imp.map_err(|e| std::io::Error::other(e.to_string()))?;
    let r = f();
    if unsafe { RevertToSelf() }.is_err() {
        // Neither who it should be nor safely back to admin: stop here.
        std::process::exit(1);
    }
    r
}

/// `X:\Users\<someone else>\...`: an account folder this user cannot list (asked once per folder, while
/// running as the user), whose files must not be listed for them here either.
fn other_profile(path: &str, root: &str, seen: &mut HashMap<String, bool>) -> bool {
    let Some(rest) = path.get(root.len()..) else { return false };
    let lower = rest.to_lowercase();
    let Some(after) = lower.strip_prefix("\\users\\") else { return false };
    let name = after.split('\\').next().unwrap_or("");
    if name.is_empty() {
        return false;
    }
    *seen.entry(name.to_string()).or_insert_with(|| fs::read_dir(format!("{root}\\Users\\{name}")).is_err())
}

fn snapshot_drive(letter: char, dir: &PathBuf) -> std::io::Result<usize> {
    let Some(map) = enumerate_volume(letter) else { return Ok(0) };
    as_user(|| write_snapshot(letter, dir, &map))
}

fn write_snapshot(letter: char, dir: &PathBuf, map: &HashMap<u64, Rec>) -> std::io::Result<usize> {
    let root = format!("{letter}:");
    let mut seen: HashMap<String, bool> = HashMap::new();
    let mut memo: HashMap<u64, Option<String>> = HashMap::new();
    let tmp = dir.join(format!("{letter}.txt.tmp"));
    let mut w = BufWriter::with_capacity(1 << 20, fs::File::create(&tmp)?);
    let mut count = 0usize;

    for (&frn, r) in map.iter() {
        if r.attrs & ATTR_SYSTEM != 0 {
            continue;
        }
        let is_dir = r.attrs & ATTR_DIR != 0;
        if is_dir {
            if let Some(p) = dir_path(frn, map, &mut memo, &root).filter(|p| !other_profile(p, &root, &mut seen)) {
                writeln!(w, "{p}\\")?;
                count += 1;
            }
        } else {
            if let Some(ext) = r.name.rsplit_once('.').map(|(_, e)| e.to_lowercase()) {
                if SKIP_EXT.contains(&ext.as_str()) {
                    continue;
                }
            }
            if let Some(parent) = dir_path(r.parent, map, &mut memo, &root).filter(|p| !other_profile(p, &root, &mut seen)) {
                writeln!(w, "{parent}\\{}", r.name)?;
                count += 1;
            }
        }
    }
    w.flush()?;
    drop(w);
    fs::rename(&tmp, dir.join(format!("{letter}.txt")))?;
    Ok(count)
}

fn snapshot_all() {
    let dir = out_dir();
    let _ = as_user(|| fs::create_dir_all(&dir));
    let mut log = String::new();
    for letter in 'A'..='Z' {
        let root = wide(&format!("{letter}:\\"));
        let kind = unsafe { GetDriveTypeW(PCWSTR(root.as_ptr())) };
        if kind != DRIVE_FIXED {
            continue;
        }
        match snapshot_drive(letter, &dir) {
            Ok(n) => log.push_str(&format!("{letter}: {n} entries\n")),
            Err(e) => log.push_str(&format!("{letter}: error {e}\n")),
        }
    }
    let _ = as_user(|| fs::write(dir.join("last-run.log"), log));
}

/// The Windows folder, from the system rather than %WINDIR%, which the user's environment could change.
fn windows_dir() -> Option<PathBuf> {
    let mut buf = [0u16; 260];
    let n = unsafe { GetSystemWindowsDirectoryW(Some(&mut buf)) } as usize;
    (n > 0 && n < buf.len()).then(|| PathBuf::from(String::from_utf16_lossy(&buf[..n])))
}

/// Registers the elevated logon task, on a copy of this exe in Program Files (on the Windows drive):
/// a folder only admins can write to, so nothing unelevated can swap what the task runs.
fn install_task() {
    let Some(win) = windows_dir() else { return };
    let Some(drive) = win.to_str().and_then(|w| w.get(..2)) else { return };
    let home = PathBuf::from(format!("{drive}\\Program Files\\Palette Index"));
    let exe = home.join("palette-mft.exe");
    let Ok(me) = std::env::current_exe() else { return };
    if fs::create_dir_all(&home).is_err() || (me != exe && fs::copy(&me, &exe).is_err()) {
        return;
    }
    let ps = win.join("System32\\WindowsPowerShell\\v1.0\\powershell.exe");
    let module = win.join("System32\\WindowsPowerShell\\v1.0\\Modules\\ScheduledTasks");
    // The module by its full path and the account from the token: nothing in the user's environment
    // (PSModulePath, USERNAME) decides what this elevated PowerShell runs, or for whom.
    let script = format!(
        "Import-Module '{module}'; \
         $u = [Security.Principal.WindowsIdentity]::GetCurrent().Name; \
         $a = New-ScheduledTaskAction -Execute '{exe}' -Argument '--snapshot'; \
         $t = New-ScheduledTaskTrigger -AtLogOn -User $u; \
         $s = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries \
              -StartWhenAvailable -ExecutionTimeLimit (New-TimeSpan -Minutes 10); \
         $p = New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Highest; \
         Register-ScheduledTask -TaskName PaletteMftRefresh -Action $a -Trigger $t -Settings $s \
              -Principal $p -Force | Out-Null",
        module = module.display(),
        exe = exe.display(),
    );
    let _ = Command::new(ps)
        .args(["-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", &script])
        .creation_flags(NO_WINDOW)
        .status();
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let has = |f: &str| args.iter().any(|a| a == f);
    if has("--install-task") {
        install_task();
    }
    if has("--snapshot") || args.is_empty() {
        snapshot_all();
    }
}
