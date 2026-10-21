//! Every setting Windows' own search knows: the Settings app's ~1,200 pages and individual
//! settings ("Mouse pointer size", "Night light", "Show file extensions in File Explorer") and
//! the classic Control Panel tasks ("Edit the system environment variables", "Device Manager").
//!
//! The list is Windows' own: `%WINDIR%\ImmersiveControlPanel\Settings\AllSystemSettings_*.xml`.
//! Its names and keywords are resource references, resolved for the current display language.
//! Entries for the Settings app name an internal page ("SettingsPagePCSystemDisplay"), which is
//! turned into an `ms-settings:` link through the page table inside SystemSettings.dll (each page
//! name is stored next to the first link that opens it), plus a few pages the table does not
//! pair up. Building takes a few hundred ms, so the result is cached and rebuilt only when
//! Windows updates those files.

use crate::model::{Action, Hit, Item, Kind};
use serde::{Deserialize, Serialize};
use std::collections::{HashMap, HashSet};
use std::path::PathBuf;

#[derive(Serialize, Deserialize)]
struct Entry {
    title: String,
    keywords: String,
    control_panel: bool,
    action: Action,
}

#[derive(Serialize, Deserialize)]
struct Cache {
    stamp: String,
    entries: Vec<Entry>,
}

fn cpl_dir() -> PathBuf {
    let root = std::env::var_os("WINDIR").map(PathBuf::from).unwrap_or_else(|| PathBuf::from(r"C:\Windows"));
    root.join("ImmersiveControlPanel")
}

fn xml_files() -> Vec<PathBuf> {
    let mut v: Vec<PathBuf> = std::fs::read_dir(cpl_dir().join("Settings"))
        .map(|rd| {
            rd.flatten()
                .map(|e| e.path())
                .filter(|p| p.extension().is_some_and(|x| x.eq_ignore_ascii_case("xml")))
                .collect()
        })
        .unwrap_or_default();
    v.sort();
    v
}

/// Changes whenever Windows updates the index, its page table, or the display language.
fn stamp(files: &[PathBuf]) -> String {
    let mtime = |p: &PathBuf| {
        std::fs::metadata(p)
            .and_then(|m| m.modified())
            .ok()
            .and_then(|t| t.duration_since(std::time::UNIX_EPOCH).ok())
            .map(|d| d.as_secs())
            .unwrap_or(0)
    };
    let mut s: String = files.iter().map(|p| format!("{}:{};", p.display(), mtime(p))).collect();
    s.push_str(&format!("dll:{};", mtime(&cpl_dir().join("SystemSettings.dll"))));
    s.push_str(&ui_language());
    s
}

fn ui_language() -> String {
    use windows::Win32::Globalization::GetUserDefaultUILanguage;
    unsafe { GetUserDefaultUILanguage() }.to_string()
}

fn cache_path() -> PathBuf {
    crate::paths::data_dir().join("settings-index.json")
}

/// The settings as catalog items: from the cache when Windows has not changed them, else rebuilt.
pub fn load() -> Vec<Item> {
    let files = xml_files();
    if files.is_empty() {
        return Vec::new();
    }
    let stamp = stamp(&files);
    let cached: Option<Cache> = std::fs::read(cache_path()).ok().and_then(|b| serde_json::from_slice(&b).ok());
    let entries = match cached {
        Some(c) if c.stamp == stamp => c.entries,
        _ => {
            let c = Cache { stamp, entries: build(&files) };
            if let Ok(b) = serde_json::to_vec(&c) {
                let _ = std::fs::write(cache_path(), b);
            }
            c.entries
        }
    };
    entries.into_iter().map(into_item).collect()
}

fn into_item(e: Entry) -> Item {
    Item::new(
        e.title.clone(),
        Hit {
            title: e.title,
            subtitle: if e.control_panel { "Control Panel".into() } else { "Windows Settings".into() },
            kind: Kind::Setting,
            action: e.action,
            score: 0,
        },
        Some(e.keywords),
    )
}

// ---------------------------------------------------------------- building

/// Pages the DLL's table does not pair with a link, by the link that opens them.
const EXTRA_PAGES: &[(&str, &str)] = &[
    ("SettingsPageAudio", "ms-settings:sound"),
    ("SettingsPageStorageSenseStorageOverview", "ms-settings:storagesense"),
    ("SettingsPageOtherUsers", "ms-settings:otherusers"),
    ("SettingsPageAccountsUsers", "ms-settings:otherusers"),
    ("SettingsPageEaseOfAccessNarrator", "ms-settings:easeofaccess-narrator"),
    ("SettingsPageEaseOfAccessNarratorNS", "ms-settings:easeofaccess-narrator"),
    ("SettingsPageTimeRegionLanguage", "ms-settings:regionlanguage"),
    ("SettingsPageTimeRegionSpelling", "ms-settings:typing"),
    ("SettingsPageAdvancedSystemSettings", "ms-settings:developers"),
    ("SettingsPageEaseOfAccessKeyboard", "ms-settings:easeofaccess-keyboard"),
    ("SettingsPageStart2", "ms-settings:personalization-start"),
    ("SettingsPagePrivacyHumanPresence", "ms-settings:privacy-presencesensing"),
    ("SettingsPagePrivacyBroadFileSystemAccess", "ms-settings:privacy-broadfilesystemaccess"),
    ("SettingsPagePrivacyNotifications", "ms-settings:privacy-notifications"),
    ("SettingsPageSearchIndex", "ms-settings:cortana-windowssearch"),
    ("SettingsPageEaseOfAccessClosedCaptioning", "ms-settings:easeofaccess-closedcaptioning"),
    ("SettingsPageNetworkMobileHotspot", "ms-settings:network-mobilehotspot"),
    ("SettingsPageNetworkWorkplace", "ms-settings:workplace"),
    ("SettingsPageEaseOfAccessTextSize", "ms-settings:easeofaccess-display"),
    ("SettingsPagePrivacyAppDiagnostics2", "ms-settings:privacy-appdiagnostics"),
    ("SettingsPageSharedExperiences2", "ms-settings:crossdevice"),
    ("SettingsPagePCSystemBluetooth", "ms-settings:bluetooth"),
    ("SettingsPagePCSystemDevices", "ms-settings:devices"),
    ("SettingsPagePrivacyAppRequestedDownloads", "ms-settings:privacy-automaticfiledownloads"),
    ("SettingsPageSecurityVirusThreat", "ms-settings:windowsdefender"),
    ("SettingsPageSecurityDeviceProtection", "ms-settings:windowsdefender"),
];

/// Settings page name -> `ms-settings:` link, from the table in SystemSettings.dll. Page names
/// are pooled strings, so only the first link after each name is known to belong to it.
fn page_links() -> HashMap<String, String> {
    let mut map: HashMap<String, String> = HashMap::new();
    if let Ok(b) = std::fs::read(cpl_dir().join("SystemSettings.dll")) {
        let units: Vec<u16> = b.chunks_exact(2).map(|c| u16::from_le_bytes([c[0], c[1]])).collect();
        // NUL-separated UTF-16 strings that are page names or links, with where each ended.
        let mut toks: Vec<(usize, usize, String)> = Vec::new();
        let mut start = 0;
        for (i, &u) in units.iter().enumerate() {
            if u == 0 {
                if i > start {
                    let s = String::from_utf16_lossy(&units[start..i]);
                    if (s.starts_with("SettingsPage") || s.starts_with("ms-settings:"))
                        && s.chars().all(|c| c.is_ascii_alphanumeric() || c == '-' || c == ':')
                    {
                        toks.push((start, i, s));
                    }
                }
                start = i + 1;
            } else if !(0x20..0x7f).contains(&u) {
                start = i + 1;
            }
        }
        for w in toks.windows(2) {
            let ((_, a_end, a), (b_start, _, b)) = (&w[0], &w[1]);
            if a.starts_with("SettingsPage") && b.starts_with("ms-settings:") && b_start - a_end <= 4 {
                map.entry(a.clone()).or_insert_with(|| b.clone());
            }
        }
    }
    for (page, link) in EXTRA_PAGES {
        map.entry((*page).to_string()).or_insert_with(|| (*link).to_string());
    }
    map
}

/// `@{...?ms-resource://...}` -> text in the user's language.
fn resolve(r: &str) -> Option<String> {
    use windows::core::HSTRING;
    use windows::Win32::UI::Shell::SHLoadIndirectString;
    if !r.starts_with('@') {
        return Some(r.to_string()).filter(|s| !s.is_empty());
    }
    let mut buf = [0u16; 2048];
    unsafe { SHLoadIndirectString(&HSTRING::from(r), &mut buf, None).ok()? };
    let n = buf.iter().position(|&c| c == 0).unwrap_or(buf.len());
    Some(String::from_utf16_lossy(&buf[..n])).filter(|s| !s.is_empty())
}

fn tag<'a>(block: &'a str, name: &str) -> Option<&'a str> {
    let open = format!("<{name}>");
    let a = block.find(&open)? + open.len();
    let b = block[a..].find(&format!("</{name}>"))? + a;
    Some(block[a..b].trim())
}

fn first_page(block: &str) -> Option<&str> {
    let key = "<Node Type=\"Page\">";
    let a = block.find(key)? + key.len();
    let b = block[a..].find('<')? + a;
    Some(&block[a..b])
}

fn unescape(s: &str) -> String {
    s.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&apos;", "'")
}

/// What opens an entry, or None for the kinds we cannot open (Windows Security's own pages).
fn target(block: &str, pages: &HashMap<String, String>) -> Option<(Action, bool)> {
    // Control Panel tasks name the program to run or the Control Panel item and page.
    const CPL_COMMAND: &str = "{12B1697E-D3A0-4DBC-B568-CCF64A3F934D}";
    const CPL_NAME: &str = "{7E0522FC-1AC4-41CA-AFD0-3610417A9C41}";
    if let Some(host) = tag(block, "HostID") {
        let link = unescape(tag(block, "DeepLink")?);
        if host.eq_ignore_ascii_case(CPL_COMMAND) {
            let line = crate::home::expand_env(&link);
            let (file, args) = split_command(&line)?;
            return Some((Action::Exec { file, args }, true));
        }
        if host.eq_ignore_ascii_case(CPL_NAME) {
            let (name, page) = link.split_once('\\').unwrap_or((&link, ""));
            let mut args = format!("/name {name}");
            if !page.is_empty() && !page.starts_with('?') {
                args.push_str(&format!(" /page {page}"));
            }
            return Some((Action::Exec { file: "control.exe".into(), args }, true));
        }
        return None;
    }
    let link = pages.get(first_page(block)?)?;
    Some((Action::Open(link.clone()), false))
}

/// `"C:\x y\a.exe" args` or `C:\x\a.exe args` -> (program, arguments).
fn split_command(line: &str) -> Option<(String, String)> {
    let line = line.trim();
    if let Some(rest) = line.strip_prefix('"') {
        let (file, args) = rest.split_once('"')?;
        return Some((file.to_string(), args.trim().to_string()));
    }
    let end = line.as_bytes().windows(4).position(|w| w.eq_ignore_ascii_case(b".exe")).map(|i| i + 4).unwrap_or_else(|| line.find(' ').unwrap_or(line.len()));
    Some((line[..end].to_string(), line[end..].trim().to_string()))
}

fn build(files: &[PathBuf]) -> Vec<Entry> {
    use windows::Win32::System::Com::{CoInitializeEx, COINIT_APARTMENTTHREADED};
    unsafe {
        let _ = CoInitializeEx(None, COINIT_APARTMENTTHREADED);
    }
    let pages = page_links();
    let mut seen: HashSet<String> = HashSet::new();
    let mut out = Vec::new();
    for f in files {
        let Ok(text) = std::fs::read_to_string(f) else { continue };
        for block in text.split("<SearchableContent").skip(1) {
            let block = block.split("</SearchableContent>").next().unwrap_or("");
            let Some(title) = tag(block, "Description").and_then(|d| resolve(&unescape(d))) else { continue };
            if !seen.insert(title.to_lowercase()) {
                continue; // the three files, and feature-flag variants, repeat most entries
            }
            let Some((action, control_panel)) = target(block, &pages) else {
                seen.remove(&title.to_lowercase());
                continue;
            };
            let keywords = ["HighKeywords", "Keywords"]
                .iter()
                .filter_map(|k| tag(block, k).and_then(|r| resolve(&unescape(r))))
                .collect::<Vec<_>>()
                .join(";");
            out.push(Entry { title, keywords, control_panel, action });
        }
    }
    out
}
