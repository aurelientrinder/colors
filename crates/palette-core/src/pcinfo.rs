//! Answers about the PC itself, asked in plain words and read from Windows when asked:
//!
//! * `port 3000`, `what's using port 3000`, `:3000`: the program listening on that port (Enter shows
//!   it in its folder; End task is in its actions);
//! * `what's using my camera`, `is my microphone on`: the apps using it now, from the privacy
//!   settings' own record;
//! * on a file's actions, the programs that have it open (Restart Manager), each with End.
//!
//! Nothing here ends a program unless that action is chosen from the list: Enter only looks.

use crate::model::{Action, Hit, Kind};
use std::path::{Path, PathBuf};
use windows::core::{PCWSTR, PWSTR};
use windows::Win32::Foundation::CloseHandle;
use windows::Win32::NetworkManagement::IpHelper::{GetExtendedTcpTable, TCP_TABLE_OWNER_PID_LISTENER};
use windows::Win32::System::Registry::{
    RegCloseKey, RegEnumKeyExW, RegGetValueW, RegOpenKeyExW, HKEY, HKEY_CURRENT_USER, KEY_READ, RRF_RT_REG_QWORD,
};
use windows::Win32::System::RestartManager::{
    RmEndSession, RmGetList, RmRegisterResources, RmStartSession, RM_PROCESS_INFO,
};
use windows::Win32::System::Threading::{
    OpenProcess, QueryFullProcessImageNameW, TerminateProcess, PROCESS_NAME_WIN32, PROCESS_QUERY_LIMITED_INFORMATION,
    PROCESS_TERMINATE,
};

/// Rows for a question about the PC, best first.
pub fn hits(query: &str) -> Vec<Hit> {
    let q = query.trim();
    if q.len() < 4 || q.len() > 60 {
        return Vec::new();
    }
    if let Some(port) = port_of(q) {
        return port_hits(port);
    }
    if let Some(device) = device_of(q) {
        return device_hits(device);
    }
    Vec::new()
}

// ---------------------------------------------------------------------------------------------- ports

/// The port a question names: "port 3000", "what's using port 3000", ":3000", "localhost:3000".
fn port_of(q: &str) -> Option<u16> {
    let l = q.to_lowercase();
    let l = l.trim_end_matches('?');
    let words: Vec<&str> = l.split_whitespace().collect();
    if let [one] = words.as_slice() {
        if let Some((host, p)) = one.rsplit_once(':') {
            if matches!(host, "" | "localhost" | "127.0.0.1" | "0.0.0.0") {
                return p.parse().ok().filter(|p| *p > 0);
            }
        }
    }
    let at = words.iter().position(|w| *w == "port")?;
    let port: u16 = words.get(at + 1)?.parse().ok().filter(|p| *p > 0)?;
    const FILLER: [&str; 17] = [
        "what", "whats", "what's", "who", "whos", "who's", "is", "using", "uses", "on", "the", "listening", "kill", "free",
        "process", "has", "port",
    ];
    // Only a question: "port 80 forwarding guide" is a search.
    words
        .iter()
        .enumerate()
        .all(|(i, w)| i == at + 1 || FILLER.contains(w))
        .then_some(port)
}

struct Listener {
    pid: u32,
    address: String,
}

fn u32_at(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}

/// The programs listening (TCP) on `port`, IPv4 and IPv6.
fn listeners(port: u16) -> Vec<Listener> {
    let mut out: Vec<Listener> = Vec::new();
    for (af, row) in [(2u32, 24usize), (23u32, 56usize)] {
        let mut size = 0u32;
        unsafe { GetExtendedTcpTable(None, &mut size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0) };
        let mut buf = vec![0u8; size as usize + 4096];
        let mut size = buf.len() as u32;
        let err = unsafe {
            GetExtendedTcpTable(Some(buf.as_mut_ptr().cast()), &mut size, false, af, TCP_TABLE_OWNER_PID_LISTENER, 0)
        };
        if err != 0 || buf.len() < 4 {
            continue;
        }
        let n = u32_at(&buf, 0) as usize;
        for i in 0..n {
            let at = 4 + i * row;
            if at + row > buf.len() {
                break;
            }
            // v4: state, local addr, local port, remote addr, remote port, pid
            // v6: local addr[16], scope, local port, remote addr[16], scope, remote port, state, pid
            let (addr, lport, pid) = if af == 2 {
                let a = u32_at(&buf, at + 4).to_le_bytes();
                (format!("{}.{}.{}.{}", a[0], a[1], a[2], a[3]), u32_at(&buf, at + 8), u32_at(&buf, at + 20))
            } else {
                let mut a = [0u8; 16];
                a.copy_from_slice(&buf[at..at + 16]);
                (std::net::Ipv6Addr::from(a).to_string(), u32_at(&buf, at + 20), u32_at(&buf, at + 52))
            };
            let local = ((lport & 0xff) << 8 | (lport >> 8 & 0xff)) as u16;
            if local == port && !out.iter().any(|l| l.pid == pid) {
                let address = match addr.as_str() {
                    "0.0.0.0" | "::" => "any address".to_string(),
                    "127.0.0.1" | "::1" => "this PC only".to_string(),
                    other => other.to_string(),
                };
                out.push(Listener { pid, address });
            }
        }
    }
    out
}

fn port_hits(port: u16) -> Vec<Hit> {
    let found = listeners(port);
    if found.is_empty() {
        return vec![Hit {
            title: format!("Nothing is listening on port {port}"),
            subtitle: "It is free to use".into(),
            kind: Kind::Info,
            action: Action::Copy(port.to_string()),
            score: 9000,
        }];
    }
    found
        .into_iter()
        .enumerate()
        .map(|(i, l)| {
            let name = process_name(l.pid);
            Hit {
                title: format!("{name} is listening on port {port}"),
                subtitle: format!("PID {}  \u{b7}  {}", l.pid, l.address),
                kind: Kind::Proc,
                action: Action::Builtin(format!("proc:{}", l.pid)),
                score: 9000 - i as i32,
            }
        })
        .collect()
}

// ---------------------------------------------------------------------------------------------- processes

fn wide(p: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    p.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
}

/// The program file of a running process, when Windows lets us see it.
pub fn exe_of(pid: u32) -> Option<PathBuf> {
    unsafe {
        let h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid).ok()?;
        let mut buf = [0u16; 1024];
        let mut len = buf.len() as u32;
        let ok = QueryFullProcessImageNameW(h, PROCESS_NAME_WIN32, PWSTR(buf.as_mut_ptr()), &mut len).is_ok();
        let _ = CloseHandle(h);
        ok.then(|| PathBuf::from(String::from_utf16_lossy(&buf[..len as usize])))
    }
}

pub fn process_name(pid: u32) -> String {
    match pid {
        0 => "System Idle".into(),
        4 => "System".into(),
        _ => exe_of(pid)
            .and_then(|p| p.file_name().map(|n| n.to_string_lossy().into_owned()))
            .unwrap_or_else(|| format!("Process {pid}")),
    }
}

/// The pid a process row (`proc:<pid>`) stands for.
pub fn pid_of(hit: &Hit) -> Option<u32> {
    match (&hit.action, hit.kind) {
        (Action::Builtin(id), Kind::Proc) => id.strip_prefix("proc:")?.parse().ok(),
        _ => None,
    }
}

/// Show the program in its folder.
pub fn reveal(pid: u32) {
    if let Some(exe) = exe_of(pid) {
        crate::actions::reveal(&exe);
    }
}

/// End the process, and leave a line saying how it went (`fileops::outcome`).
pub fn end(pid: u32) {
    let name = process_name(pid);
    if pid <= 4 || pid == std::process::id() {
        return crate::fileops::report(format!("Won't end {name}"), false);
    }
    let ok = unsafe {
        OpenProcess(PROCESS_TERMINATE, false, pid)
            .ok()
            .is_some_and(|h| {
                let done = TerminateProcess(h, 1).is_ok();
                let _ = CloseHandle(h);
                done
            })
    };
    if ok {
        crate::fileops::report(format!("Ended {name}"), true);
    } else {
        crate::fileops::report(format!("Couldn't end {name} (it may need administrator rights)"), false);
    }
}

// ---------------------------------------------------------------------------------------------- open files

/// The programs that have `path` open: (pid, name), at most a few.
pub fn locks_of(path: &Path) -> Vec<(u32, String)> {
    if path.to_string_lossy().starts_with(r"\\") || !path.is_file() {
        return Vec::new();
    }
    let mut out = Vec::new();
    unsafe {
        let mut session = 0u32;
        let mut key = [0u16; 33];
        if RmStartSession(&mut session, None, PWSTR(key.as_mut_ptr())).0 != 0 {
            return out;
        }
        let file = wide(path);
        let files = [PCWSTR(file.as_ptr())];
        if RmRegisterResources(session, Some(&files), None, None).0 == 0 {
            let (mut needed, mut count, mut reasons) = (0u32, 0u32, 0u32);
            let _ = RmGetList(session, &mut needed, &mut count, None, &mut reasons);
            if needed > 0 {
                let mut info: Vec<RM_PROCESS_INFO> = vec![std::mem::zeroed(); needed as usize];
                count = needed;
                if RmGetList(session, &mut needed, &mut count, Some(info.as_mut_ptr()), &mut reasons).0 == 0 {
                    for p in info.iter().take(count as usize) {
                        let name_len = p.strAppName.iter().position(|c| *c == 0).unwrap_or(p.strAppName.len());
                        let name = String::from_utf16_lossy(&p.strAppName[..name_len]);
                        let pid = p.Process.dwProcessId;
                        if pid > 4 && pid != std::process::id() && !out.iter().any(|(o, _)| *o == pid) {
                            out.push((pid, if name.is_empty() { process_name(pid) } else { name }));
                        }
                    }
                }
            }
        }
        let _ = RmEndSession(session);
    }
    out.truncate(3);
    out
}

// ---------------------------------------------------------------------------------------------- camera, microphone

struct Device {
    /// The privacy record's name for it.
    store: &'static str,
    noun: &'static str,
    page: &'static str,
}

const CAMERA: Device = Device { store: "webcam", noun: "camera", page: "ms-settings:privacy-webcam" };
const MICROPHONE: Device = Device { store: "microphone", noun: "microphone", page: "ms-settings:privacy-microphone" };

/// "what's using my camera", "is my microphone on": a question, not the word alone (that finds the app).
fn device_of(q: &str) -> Option<&'static Device> {
    let l = q.to_lowercase();
    let words: Vec<&str> = l.trim_end_matches('?').split_whitespace().collect();
    let device = if words.iter().any(|w| matches!(*w, "camera" | "webcam")) {
        &CAMERA
    } else if words.iter().any(|w| matches!(*w, "microphone" | "mic")) {
        &MICROPHONE
    } else {
        return None;
    };
    const ASKS: [&str; 12] =
        ["using", "uses", "use", "used", "on", "active", "access", "accessing", "watching", "listening", "spying", "in"];
    const FILLER: [&str; 20] = [
        "what", "whats", "what's", "who", "whos", "who's", "is", "are", "my", "the", "has", "have", "right", "now", "being",
        "this", "pc", "camera", "webcam", "microphone",
    ];
    let known = |w: &&str| ASKS.contains(w) || FILLER.contains(w) || matches!(*w, "mic" | "using");
    (words.iter().all(|w| known(&w)) && words.iter().any(|w| ASKS.contains(w))).then_some(device)
}

const CONSENT: &str = r"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

fn subkeys(path: &str) -> Vec<String> {
    let mut out = Vec::new();
    unsafe {
        let p: Vec<u16> = path.encode_utf16().chain(std::iter::once(0)).collect();
        let mut key = HKEY::default();
        if RegOpenKeyExW(HKEY_CURRENT_USER, PCWSTR(p.as_ptr()), None, KEY_READ, &mut key).0 != 0 {
            return out;
        }
        for i in 0.. {
            let mut name = [0u16; 512];
            let mut len = name.len() as u32;
            if RegEnumKeyExW(key, i, Some(PWSTR(name.as_mut_ptr())), &mut len, None, None, None, None).0 != 0 {
                break;
            }
            out.push(String::from_utf16_lossy(&name[..len as usize]));
        }
        let _ = RegCloseKey(key);
    }
    out
}

fn qword(path: &str, name: &str) -> u64 {
    let (p, n): (Vec<u16>, Vec<u16>) = (
        path.encode_utf16().chain(std::iter::once(0)).collect(),
        name.encode_utf16().chain(std::iter::once(0)).collect(),
    );
    let mut value = 0u64;
    let mut size = 8u32;
    let err = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            PCWSTR(p.as_ptr()),
            PCWSTR(n.as_ptr()),
            RRF_RT_REG_QWORD,
            None,
            Some((&mut value as *mut u64).cast()),
            Some(&mut size),
        )
    };
    if err.0 == 0 {
        value
    } else {
        0
    }
}

/// An app as the privacy record names it: "Zoom" from a program path, "WindowsCamera" from a package.
fn app_name(key: &str) -> String {
    if key.contains('#') {
        let path = key.replace('#', "\\");
        return Path::new(&path).file_stem().map(|s| s.to_string_lossy().into_owned()).unwrap_or(path);
    }
    let family = key.split('_').next().unwrap_or(key);
    family.rsplit('.').next().unwrap_or(family).to_string()
}

fn filetime_to_unix(ft: u64) -> i64 {
    (ft / 10_000_000) as i64 - 11_644_473_600
}

/// (app, started, stopped) as FILETIMEs for every app that has ever used the device.
fn consent(store: &str) -> Vec<(String, u64, u64)> {
    let base = format!(r"{CONSENT}\{store}");
    let mut entries = Vec::new();
    for sub in subkeys(&base) {
        let paths = if sub == "NonPackaged" {
            subkeys(&format!(r"{base}\NonPackaged")).into_iter().map(|s| format!(r"NonPackaged\{s}")).collect()
        } else {
            vec![sub]
        };
        for key in paths {
            let full = format!(r"{base}\{key}");
            let (start, stop) = (qword(&full, "LastUsedTimeStart"), qword(&full, "LastUsedTimeStop"));
            if start > 0 {
                entries.push((app_name(key.strip_prefix(r"NonPackaged\").unwrap_or(&key)), start, stop));
            }
        }
    }
    entries
}

fn device_hits(d: &Device) -> Vec<Hit> {
    let now = std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map(|t| t.as_secs() as i64).unwrap_or(0);
    let mut entries = consent(d.store);
    entries.sort_by_key(|(_, start, stop)| std::cmp::Reverse((*start).max(*stop)));
    let page = d.page.to_string();
    let using: Vec<&(String, u64, u64)> = entries.iter().filter(|(_, start, stop)| start > stop).collect();
    if using.is_empty() {
        let last = entries.first().map(|(name, start, stop)| {
            format!("Last used by {name}, {}", crate::clip::ago(now - filetime_to_unix((*start).max(*stop))))
        });
        return vec![Hit {
            title: format!("Nothing is using your {} right now", d.noun),
            subtitle: last.unwrap_or_else(|| "No app has used it yet".into()),
            kind: Kind::Setting,
            action: Action::Open(page),
            score: 9000,
        }];
    }
    using
        .into_iter()
        .enumerate()
        .map(|(i, (name, start, _))| Hit {
            title: format!("{name} is using your {}", d.noun),
            subtitle: format!("Since {}", crate::clip::ago(now - filetime_to_unix(*start))),
            kind: Kind::Setting,
            action: Action::Open(page.clone()),
            score: 9000 - i as i32,
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn ports_are_read_from_questions() {
        assert_eq!(port_of("port 3000"), Some(3000));
        assert_eq!(port_of("what's using port 8080?"), Some(8080));
        assert_eq!(port_of("who is using port 443"), Some(443));
        assert_eq!(port_of(":3000"), Some(3000));
        assert_eq!(port_of("localhost:5173"), Some(5173));
        assert_eq!(port_of("kill port 3000"), Some(3000));
        assert_eq!(port_of("port 80 forwarding guide"), None);
        assert_eq!(port_of("port"), None);
        assert_eq!(port_of("port 0"), None);
        assert_eq!(port_of("port 70000"), None);
        assert_eq!(port_of("notepad"), None);
        assert_eq!(port_of("c:\\temp"), None);
    }

    #[test]
    fn a_listener_is_found_on_a_real_port() {
        let l = std::net::TcpListener::bind("127.0.0.1:0").unwrap();
        let port = l.local_addr().unwrap().port();
        let found = listeners(port);
        assert!(found.iter().any(|f| f.pid == std::process::id()), "{:?}", found.iter().map(|f| f.pid).collect::<Vec<_>>());
        let rows = port_hits(port);
        assert_eq!(rows[0].kind, Kind::Proc);
        assert!(rows[0].title.contains(&format!("port {port}")));
        assert_eq!(pid_of(&rows[0]), Some(std::process::id()));
        drop(l);
        assert!(listeners(port).iter().all(|f| f.pid != std::process::id()));
    }

    #[test]
    fn devices_need_a_question() {
        assert!(device_of("what's using my camera").is_some());
        assert!(device_of("is my microphone on").is_some());
        assert!(device_of("who is using the mic?").is_some());
        assert!(device_of("camera").is_none());
        assert!(device_of("camera settings").is_none());
        assert!(device_of("use camera app to scan").is_none());
    }

    #[test]
    fn apps_are_named_as_people_know_them() {
        assert_eq!(app_name("C:#Program Files#Zoom#bin#Zoom.exe"), "Zoom");
        assert_eq!(app_name("Microsoft.WindowsCamera_8wekyb3d8bbwe"), "WindowsCamera");
        assert_eq!(filetime_to_unix(116_444_736_000_000_000), 0);
    }

    #[test]
    fn an_open_file_names_its_holder() {
        use std::io::Write;
        let path = std::env::temp_dir().join(format!("palette-lock-{}.txt", std::process::id()));
        let mut f = std::fs::File::create(&path).unwrap();
        writeln!(f, "held open").unwrap();
        // This process holds it; locks_of leaves out our own pid, so nothing is listed.
        assert!(locks_of(&path).iter().all(|(pid, _)| *pid != std::process::id()));
        drop(f);
        let _ = std::fs::remove_file(&path);
    }

    #[test]
    fn another_program_holding_a_file_is_found() {
        let path = std::env::temp_dir().join(format!("palette-held-{}.txt", std::process::id()));
        std::fs::write(&path, "x").unwrap();
        let script = format!("$f=[IO.File]::Open('{}','Open','Read','None'); Start-Sleep 6", path.display());
        let mut child = std::process::Command::new("powershell.exe").args(["-NoProfile", "-Command", &script]).spawn().unwrap();
        std::thread::sleep(std::time::Duration::from_millis(2500));
        let found = locks_of(&path);
        let _ = child.kill();
        let _ = child.wait();
        let _ = std::fs::remove_file(&path);
        assert!(found.iter().any(|(pid, _)| *pid == child.id()), "{found:?}");
    }

    #[test]
    fn camera_and_microphone_records_are_read() {
        for d in [&CAMERA, &MICROPHONE] {
            let rows = device_hits(d);
            assert!(!rows.is_empty());
            println!("{} -> {} | {}", d.noun, rows[0].title, rows[0].subtitle);
        }
    }

    #[test]
    fn nothing_is_ended_by_mistake() {
        end(0);
        assert!(!crate::fileops::outcome().ok);
        end(std::process::id());
        assert!(crate::fileops::outcome().message.starts_with("Won't end"));
    }
}
