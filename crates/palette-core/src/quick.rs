//! Quick settings changed in place, without opening Settings: "dark mode", "wifi off",
//! "bluetooth", "mute", "volume 30", "brightness 70". Rows say what Enter will do given the current state
//! ("Turn Wi-Fi off"), so the state is read when the query names one of them.

use crate::model::{Action, Hit, Kind};
use std::sync::OnceLock;

struct Quick {
    id: &'static str,
    /// The setting's names ("wifi"): typed alone they offer the toggle, after the Settings page,
    /// since you may only want to look.
    names: &'static [&'static str],
    /// Names that are themselves a request ("mute", "dark mode"): typed, the toggle leads.
    verbs: &'static [&'static str],
    /// Takes a number ("volume 30").
    level: bool,
}

const QUICK: &[Quick] = &[
    Quick { id: "dark", names: &["theme"], verbs: &["dark mode", "dark theme", "light mode", "light theme"], level: false },
    Quick { id: "wifi", names: &["wifi", "wi-fi", "wireless", "wlan"], verbs: &[], level: false },
    Quick { id: "bluetooth", names: &["bluetooth"], verbs: &[], level: false },
    Quick { id: "mute", names: &["sound", "audio"], verbs: &["mute", "unmute"], level: false },
    Quick { id: "volume", names: &["volume", "vol"], verbs: &[], level: true },
    Quick { id: "brightness", names: &["brightness"], verbs: &[], level: true },
];

/// The query asks for the change ("mute", "wifi off", "volume 30"): first row.
const ASKED: i32 = 1000;
/// The query names the setting ("wifi"): offered just after the best Settings pages.
const NAMED: i32 = 290;

/// Rows for the quick settings `query` names, best first.
pub fn hits(query: &str) -> Vec<Hit> {
    let q = query.trim().to_lowercase();
    if q.len() < 2 {
        return Vec::new();
    }
    // "turn wifi off", "wifi on", "turn off bluetooth", "volume 30", "vol 30%"
    let mut words: Vec<&str> =
        q.split_whitespace().filter(|w| !matches!(*w, "turn" | "toggle" | "switch" | "set")).collect();
    let state = |w: &str| match w {
        "on" => Some(true),
        "off" => Some(false),
        _ => None,
    };
    let mut desired = words.last().and_then(|w| state(w));
    let mut number = None;
    if desired.is_some() {
        words.pop();
    } else if let Some(n) = words.last().and_then(|w| w.trim_end_matches('%').parse::<u32>().ok()) {
        number = Some(n.min(100));
        words.pop();
    } else if let Some(d) = words.first().and_then(|w| state(w)) {
        desired = Some(d);
        words.remove(0);
    }
    let text = words.join(" ");
    if text.len() < 2 {
        return Vec::new();
    }
    let asked = desired.is_some() || number.is_some();

    // 2: typed in full, 1: the start of it.
    let matched = |list: &[&str]| -> Option<u8> {
        list.iter().filter_map(|n| if *n == text { Some(2) } else if n.starts_with(text.as_str()) { Some(1) } else { None }).max()
    };
    let mut out = Vec::new();
    for qk in QUICK {
        let verb = matched(qk.verbs);
        let noun = matched(qk.names);
        if verb.is_none() && noun.is_none() {
            continue;
        }
        if (number.is_some() && !qk.level) || (desired.is_some() && qk.level) {
            continue;
        }
        let score = if verb == Some(2) || (asked && noun == Some(2)) {
            ASKED
        } else if text.len() >= 3 && (verb.is_some() || asked) {
            ASKED - 100
        } else if noun == Some(2) {
            NAMED
        } else if text.len() >= 3 {
            NAMED - 40
        } else {
            NAMED - 90
        };
        // "light mode on" means dark mode off; "sound off" means muted.
        let target = match qk.id {
            "dark" if text.starts_with("light") => desired.map(|d| !d),
            "mute" if !text.contains("mute") => desired.map(|d| !d),
            _ => desired,
        };
        if let Some(h) = row(qk.id, number, target, score) {
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

/// `target` is the state asked for (dark mode on, Wi-Fi on, muted); without one the row flips
/// the current state.
fn row(id: &str, number: Option<u32>, target: Option<bool>, score: i32) -> Option<Hit> {
    match id {
        "dark" => {
            let dark = dark_mode()?;
            let to = target.unwrap_or(!dark);
            Some(hit(
                format!("Turn dark mode {}", on_off(to)),
                format!("Apps and Windows are {}", if dark { "dark" } else { "light" }),
                format!("dark:{}", on_off(to)),
                score,
            ))
        }
        "wifi" | "bluetooth" => {
            let label = if id == "wifi" { "Wi-Fi" } else { "Bluetooth" };
            let on = radio_on(id)?;
            let to = target.unwrap_or(!on);
            Some(hit(
                format!("Turn {label} {}", on_off(to)),
                format!("{label} is {}", on_off(on)),
                format!("{id}:{}", on_off(to)),
                score,
            ))
        }
        "mute" | "volume" => {
            let (level, muted) = volume()?;
            let now = format!("Volume {}%{}", (level * 100.0).round(), if muted { ", muted" } else { "" });
            match number {
                Some(n) => Some(hit(format!("Set volume to {n}%"), now, format!("volume:{n}"), score)),
                None => {
                    let mute = target.unwrap_or(!muted);
                    Some(hit(if mute { "Mute".into() } else { "Unmute".into() }, now, format!("mute:{}", on_off(mute)), score))
                }
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
        "volume" => match value {
            "up" | "down" => volume().is_some_and(|(l, _)| {
                let now = (l * 100.0).round() as i32;
                set_volume((now + if value == "up" { STEP } else { -STEP }).clamp(0, 100) as u32)
            }),
            _ => value.parse::<u32>().is_ok_and(set_volume),
        },
        "brightness" => match value {
            "up" => change_brightness(STEP),
            "down" => change_brightness(-STEP),
            _ => value.parse::<u32>().is_ok_and(set_brightness),
        },
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

/// How far "volume up" and "dim the screen" go.
const STEP: i32 = 10;

/// The built-in display's brightness, `by` points up or down.
fn change_brightness(by: i32) -> bool {
    use std::os::windows::process::CommandExt;
    let script = format!(
        "$b = (Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightness).CurrentBrightness;          $n = [Math]::Max(0, [Math]::Min(100, $b + ({by})));          Get-CimInstance -Namespace root/WMI -ClassName WmiMonitorBrightnessMethods |          Invoke-CimMethod -MethodName WmiSetBrightness -Arguments @{{Timeout=1; Brightness=$n}} | Out-Null"
    );
    std::process::Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", &script])
        .creation_flags(0x0800_0000)
        .spawn()
        .is_ok()
}

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
