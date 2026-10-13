//! Quick settings changed in place, without opening Settings: "dark", "wifi", "bluetooth",
//! "mute", "volume 30", "brightness 70". Rows say what Enter will do given the current state
//! ("Turn Wi-Fi off"), so the state is read when the query names one of them.

use crate::model::{Action, Hit, Kind};
use std::sync::OnceLock;

struct Quick {
    id: &'static str,
    /// What it answers to; the query matches when one of these starts with it (or the other way round).
    names: &'static [&'static str],
    /// Takes a number ("volume 30").
    level: bool,
}

const QUICK: &[Quick] = &[
    Quick { id: "dark", names: &["dark mode", "dark theme", "light mode", "light theme", "theme"], level: false },
    Quick { id: "wifi", names: &["wifi", "wi-fi", "wireless", "wlan"], level: false },
    Quick { id: "bluetooth", names: &["bluetooth"], level: false },
    Quick { id: "mute", names: &["mute", "unmute", "sound off", "sound on"], level: false },
    Quick { id: "volume", names: &["volume", "vol"], level: true },
    Quick { id: "brightness", names: &["brightness"], level: true },
];

/// Strong enough to lead the list when the query clearly names a quick setting.
const STRONG: i32 = 1000;
const WEAK: i32 = 300;

/// Rows for the quick settings `query` names, best first.
pub fn hits(query: &str) -> Vec<Hit> {
    let q = query.trim().to_lowercase();
    if q.len() < 2 {
        return Vec::new();
    }
    // "volume 30" / "vol 30%" / "brightness 70"
    let (text, number) = match q.rsplit_once(' ') {
        Some((t, n)) => match n.trim_end_matches('%').parse::<u32>() {
            Ok(v) => (t.trim().to_string(), Some(v.min(100))),
            Err(_) => (q.clone(), None),
        },
        None => (q.clone(), None),
    };

    let mut out = Vec::new();
    for qk in QUICK {
        let best = qk
            .names
            .iter()
            .filter_map(|n| {
                if *n == text || (text.len() > n.len() && text.starts_with(n)) {
                    Some(STRONG)
                } else if n.starts_with(text.as_str()) {
                    Some(if text.len() >= 3 { STRONG - 100 } else { WEAK })
                } else {
                    None
                }
            })
            .max();
        let Some(score) = best else { continue };
        if number.is_some() && !qk.level {
            continue;
        }
        if let Some(h) = row(qk.id, number, score) {
            out.push(h);
        }
    }
    out
}

fn hit(title: String, subtitle: String, id: String, score: i32) -> Hit {
    Hit { title, subtitle, kind: Kind::Toggle, action: Action::Builtin(format!("quick:{id}")), score }
}

fn on_off(b: bool) -> &'static str {
    if b { "on" } else { "off" }
}

fn row(id: &str, number: Option<u32>, score: i32) -> Option<Hit> {
    match id {
        "dark" => {
            let dark = dark_mode()?;
            Some(hit(
                format!("Turn dark mode {}", on_off(!dark)),
                format!("Apps and Windows are {}", if dark { "dark" } else { "light" }),
                format!("dark:{}", on_off(!dark)),
                score,
            ))
        }
        "wifi" | "bluetooth" => {
            let label = if id == "wifi" { "Wi-Fi" } else { "Bluetooth" };
            let on = radio_on(id)?;
            Some(hit(
                format!("Turn {label} {}", on_off(!on)),
                format!("{label} is {}", on_off(on)),
                format!("{id}:{}", on_off(!on)),
                score,
            ))
        }
        "mute" | "volume" => {
            let (level, muted) = volume()?;
            let now = format!("Volume {}%{}", (level * 100.0).round(), if muted { ", muted" } else { "" });
            match number {
                Some(n) => Some(hit(format!("Set volume to {n}%"), now, format!("volume:{n}"), score)),
                None => Some(hit(
                    if muted { "Unmute".into() } else { "Mute".into() },
                    now,
                    format!("mute:{}", on_off(!muted)),
                    score,
                )),
            }
        }
        "brightness" => {
            let n = number?;
            Some(hit(format!("Set brightness to {n}%"), "Built-in display".into(), format!("brightness:{n}"), score))
        }
        _ => None,
    }
}

/// Carry out `quick:<what>:<value>` (from a row's action). Returns false if it did nothing.
pub fn run(id: &str) -> bool {
    let Some((what, value)) = id.split_once(':') else { return false };
    let on = value == "on";
    match what {
        "dark" => set_dark_mode(on),
        "wifi" | "bluetooth" => set_radio(what, on),
        "mute" => set_mute(on),
        "volume" => value.parse::<u32>().is_ok_and(set_volume),
        "brightness" => value.parse::<u32>().is_ok_and(set_brightness),
        _ => false,
    }
}

// ---------------------------------------------------------------- dark mode

const PERSONALIZE: &str = r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

fn dark_mode() -> Option<bool> {
    use windows::core::{w, HSTRING};
    use windows::Win32::System::Registry::{RegGetValueW, HKEY_CURRENT_USER, RRF_RT_REG_DWORD};
    let mut v: u32 = 1;
    let mut size = 4u32;
    let r = unsafe {
        RegGetValueW(
            HKEY_CURRENT_USER,
            &HSTRING::from(PERSONALIZE),
            w!("AppsUseLightTheme"),
            RRF_RT_REG_DWORD,
            None,
            Some(&mut v as *mut u32 as *mut _),
            Some(&mut size),
        )
    };
    Some(if r.is_ok() { v == 0 } else { false })
}

/// Both halves of Windows' "Choose your mode": apps, and the taskbar and Start.
fn set_dark_mode(dark: bool) -> bool {
    use windows::core::{w, HSTRING};
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::System::Registry::{RegSetKeyValueW, HKEY_CURRENT_USER, REG_DWORD};
    use windows::Win32::UI::WindowsAndMessaging::{SendMessageTimeoutW, HWND_BROADCAST, SMTO_ABORTIFHUNG, WM_SETTINGCHANGE};
    let light: u32 = if dark { 0 } else { 1 };
    let key = HSTRING::from(PERSONALIZE);
    let ok = [w!("AppsUseLightTheme"), w!("SystemUsesLightTheme")].iter().all(|name| unsafe {
        RegSetKeyValueW(HKEY_CURRENT_USER, &key, *name, REG_DWORD.0, Some(&light as *const u32 as *const _), 4).is_ok()
    });
    // Running apps and the shell redraw on this.
    let area = w!("ImmersiveColorSet");
    unsafe {
        let _ = SendMessageTimeoutW(
            HWND_BROADCAST,
            WM_SETTINGCHANGE,
            WPARAM(0),
            LPARAM(area.as_ptr() as isize),
            SMTO_ABORTIFHUNG,
            200,
            None,
        );
    }
    ok
}

// ---------------------------------------------------------------- radios

/// Wi-Fi and Bluetooth radios, asked for once (access is granted to desktop apps without a prompt).
fn radios() -> &'static [windows::Devices::Radios::Radio] {
    static RADIOS: OnceLock<Vec<windows::Devices::Radios::Radio>> = OnceLock::new();
    RADIOS.get_or_init(|| {
        use windows::Devices::Radios::Radio;
        let get = || -> windows::core::Result<Vec<Radio>> {
            let _ = Radio::RequestAccessAsync()?.join()?;
            Ok(Radio::GetRadiosAsync()?.join()?.into_iter().collect())
        };
        get().unwrap_or_default()
    })
}

fn radio(kind: &str) -> Option<&'static windows::Devices::Radios::Radio> {
    use windows::Devices::Radios::RadioKind;
    let want = if kind == "wifi" { RadioKind::WiFi } else { RadioKind::Bluetooth };
    radios().iter().find(|r| r.Kind().is_ok_and(|k| k == want))
}

fn radio_on(kind: &str) -> Option<bool> {
    use windows::Devices::Radios::RadioState;
    radio(kind)?.State().ok().map(|s| s == RadioState::On)
}

fn set_radio(kind: &str, on: bool) -> bool {
    use windows::Devices::Radios::{RadioAccessStatus, RadioState};
    let Some(r) = radio(kind) else { return false };
    let state = if on { RadioState::On } else { RadioState::Off };
    r.SetStateAsync(state).and_then(|op| op.join()).is_ok_and(|s| s == RadioAccessStatus::Allowed)
}

// ---------------------------------------------------------------- volume

fn endpoint() -> Option<windows::Win32::Media::Audio::Endpoints::IAudioEndpointVolume> {
    use windows::Win32::Media::Audio::{eMultimedia, eRender, IMMDeviceEnumerator, MMDeviceEnumerator};
    use windows::Win32::System::Com::{CoCreateInstance, CoInitializeEx, CLSCTX_ALL, COINIT_APARTMENTTHREADED};
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
        let devices: IMMDeviceEnumerator = CoCreateInstance(&MMDeviceEnumerator, None, CLSCTX_ALL).ok()?;
        let speakers = devices.GetDefaultAudioEndpoint(eRender, eMultimedia).ok()?;
        speakers.Activate(CLSCTX_ALL, None).ok()
    }
}

/// (level 0..1, muted) of the default output.
fn volume() -> Option<(f32, bool)> {
    let ep = endpoint()?;
    unsafe { Some((ep.GetMasterVolumeLevelScalar().ok()?, ep.GetMute().ok()?.as_bool())) }
}

fn set_mute(mute: bool) -> bool {
    endpoint().is_some_and(|ep| unsafe { ep.SetMute(mute, std::ptr::null()).is_ok() })
}

/// Setting a level also unmutes, as dragging the slider does.
fn set_volume(percent: u32) -> bool {
    endpoint().is_some_and(|ep| unsafe {
        ep.SetMasterVolumeLevelScalar(percent as f32 / 100.0, std::ptr::null()).is_ok()
            && (percent == 0 || ep.SetMute(false, std::ptr::null()).is_ok())
    })
}

// ---------------------------------------------------------------- brightness

/// Built-in displays only (external monitors do not take brightness over WMI). Runs in the
/// background: WMI through PowerShell takes a few hundred ms.
fn set_brightness(percent: u32) -> bool {
    use std::os::windows::process::CommandExt;
    let script = format!(
        "Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods | \
         Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{{Timeout=1; Brightness={percent}}} | Out-Null"
    );
    std::process::Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .creation_flags(0x0800_0000)
        .spawn()
        .is_ok()
}
