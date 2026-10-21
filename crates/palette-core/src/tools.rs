//! What the chat model can look up for itself (chat.rs offers these as tools; the shell runs the calls).
//!
//! `find_files`: the user's files by what they are called, what they are about, and when they were
//! last opened or changed ("the file I was working on last week"). Three sources, merged:
//! * Windows' Recent folder: a shortcut for every file the user opened, dated when it was opened;
//! * the file index (search.rs), by name;
//! * the meaning search of documents (semantic.rs), by topic;
//! * for a period ("this month's photos"), the user's own folders walked for files changed in it: a
//!   photo from the phone or a download is never in the Recent folder.
//!
//! The net is wide (see `find_files`), and each file comes with what is known of it (`about`): the
//! shell has the chat model rank them by what they are, so the app the user describes ("something like
//! xflr5", "for analysing wings") comes before a file that merely shares a word with the description.
//! A query may hold several guesses, separated by commas ("xflr5, flow5, airfoil").

use crate::model::{Action, Kind};
use serde::Serialize;
use std::collections::HashMap;
use std::path::Path;
use std::time::{SystemTime, UNIX_EPOCH};

/// A file found for the model (and shown to the user as a card).
#[derive(Serialize, Clone, Debug)]
pub struct FileFound {
    pub path: String,
    pub name: String,
    pub folder: String,
    /// Last changed, unix seconds.
    pub modified: i64,
    /// Last opened (from the Recent folder), unix seconds; 0 when unknown.
    pub opened: i64,
    /// Why it was found: "opened", "changed", "name", "topic", or several joined by "+".
    pub why: String,
    /// What is known of it beyond its name (see `about`); "" when nothing is.
    pub about: String,
}

/// What kind of file is wanted.
fn kind_matches(kind: &str, path: &str) -> bool {
    let ext = Path::new(path).extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
    let any = |list: &[&str]| list.contains(&ext.as_str());
    match kind {
        "document" => any(&["pdf", "doc", "docx", "odt", "rtf", "txt", "md", "pages", "epub", "tex"]),
        "spreadsheet" => any(&["xls", "xlsx", "xlsm", "ods", "csv", "tsv", "numbers"]),
        "presentation" => any(&["ppt", "pptx", "odp", "key"]),
        "image" => any(&["png", "jpg", "jpeg", "gif", "webp", "heic", "bmp", "tif", "tiff", "svg", "raw", "cr2", "nef"]),
        "video" => any(&["mp4", "mov", "mkv", "avi", "webm", "wmv"]),
        "audio" => any(&["mp3", "wav", "flac", "m4a", "ogg", "aac"]),
        "code" => any(&[
            "rs", "cs", "py", "js", "ts", "tsx", "jsx", "java", "kt", "c", "cpp", "h", "hpp", "go", "rb", "php", "swift", "sql",
            "html", "css", "json", "toml", "yaml", "yml", "xml", "ps1", "sh", "ipynb", "xaml",
        ]),
        "3d" => any(&["step", "stp", "stl", "3mf", "obj", "ply", "glb", "gltf", "f3d", "sldprt"]),
        // Apps, their installers, and the archives most downloaded apps come in.
        "app" => any(&["exe", "msi", "msix", "msixbundle", "appx", "appxbundle", "appinstaller", "zip", "7z", "rar"]),
        _ => true,
    }
}

fn unix(t: SystemTime) -> i64 {
    t.duration_since(UNIX_EPOCH).map(|d| d.as_secs() as i64).unwrap_or(0)
}

/// Files opened lately, newest first: (path, when opened). Folders are left out.
pub fn recently_opened(max: usize) -> Vec<(String, i64)> {
    let Some(appdata) = std::env::var_os("APPDATA") else { return Vec::new() };
    let dir = Path::new(&appdata).join(r"Microsoft\Windows\Recent");
    let Ok(rd) = std::fs::read_dir(&dir) else { return Vec::new() };
    let mut lnks: Vec<(SystemTime, std::path::PathBuf)> = rd
        .flatten()
        .filter(|e| e.path().extension().is_some_and(|x| x.eq_ignore_ascii_case("lnk")))
        .filter_map(|e| Some((e.metadata().ok()?.modified().ok()?, e.path())))
        .collect();
    lnks.sort_by(|a, b| b.0.cmp(&a.0));
    let mut out = Vec::new();
    let mut seen = std::collections::HashSet::new();
    for (at, lnk) in lnks {
        if out.len() >= max {
            break;
        }
        let Some(target) = crate::home::link_target(&lnk) else { continue };
        if target.starts_with(r"\\") || !seen.insert(target.to_lowercase()) {
            continue;
        }
        if std::fs::metadata(&target).is_ok_and(|m| m.is_file()) {
            out.push((target, unix(at)));
        }
    }
    out
}

const OWN: [&str; 6] = ["Desktop", "Documents", "Downloads", "Pictures", "Videos", "Music"];

/// The folders a person keeps their own files in: Desktop, Documents, Downloads, Pictures, Videos, Music,
/// and OneDrive's; only those called `only` ("downloads") when it names one.
fn own_folders(only: &str) -> Vec<std::path::PathBuf> {
    let only = OWN.iter().find(|s| s.eq_ignore_ascii_case(only));
    let mut out = Vec::new();
    for base in ["USERPROFILE", "OneDrive"].iter().filter_map(std::env::var_os) {
        for sub in OWN.iter().filter(|s| only.is_none_or(|o| o == *s)) {
            let p = Path::new(&base).join(sub);
            if p.is_dir() && !out.contains(&p) {
                out.push(p);
            }
        }
    }
    out
}

/// Files under `roots` changed in `from..to` (unix seconds), newest first, at most `max`. Folders that
/// hold tools rather than the user's own files (dot folders, node_modules, build output) are skipped,
/// and the walk stops after `budget` entries so a huge tree cannot hold the answer up.
fn changed_in(roots: &[std::path::PathBuf], from: i64, to: i64, max: usize, budget: usize) -> Vec<(String, i64)> {
    const SKIP: [&str; 9] = ["node_modules", "target", "bin", "obj", "__pycache__", ".git", ".venv", "venv", "AppData"];
    let mut found: Vec<(String, i64)> = Vec::new();
    let mut seen = 0usize;
    let mut stack: Vec<(std::path::PathBuf, usize)> = roots.iter().map(|r| (r.clone(), 0)).collect();
    while let Some((dir, depth)) = stack.pop() {
        let Ok(rd) = std::fs::read_dir(&dir) else { continue };
        for e in rd.flatten() {
            seen += 1;
            if seen > budget {
                break;
            }
            let name = e.file_name().to_string_lossy().into_owned();
            let Ok(ft) = e.file_type() else { continue };
            if ft.is_dir() {
                if depth < 6 && !name.starts_with('.') && !SKIP.iter().any(|s| s.eq_ignore_ascii_case(&name)) {
                    stack.push((e.path(), depth + 1));
                }
            } else if ft.is_file() && !name.starts_with('~') && !name.starts_with('.') {
                let t = e.metadata().ok().and_then(|m| m.modified().ok()).map(unix).unwrap_or(0);
                if t >= from && (to == 0 || t < to) {
                    found.push((e.path().display().to_string(), t));
                }
            }
        }
    }
    found.sort_by(|a, b| b.1.cmp(&a.1));
    found.truncate(max);
    found
}

/// Words that say what kind of file is wanted rather than what it is called ("my photos", "the pdf"):
/// left out of the query (the kind is its own argument), or no file would match "photos" by name.
const KIND_WORDS: [&str; 41] = [
    "file", "files", "photo", "photos", "picture", "pictures", "pic", "pics", "image", "images", "document", "documents",
    "doc", "docs", "video", "videos", "my", "the", "a", "an", "some", "fichier", "fichiers", "mes", "les", "des", "took",
    "taken", "recent", "latest", "any", "all", "every", "anything", "worked", "opened", "tous", "download", "downloaded",
    "something", "thing",
];

/// Words for an app ("the application I downloaded"): left out of the query too, and they make the kind "app".
const APP_WORDS: [&str; 16] = [
    "app", "apps", "application", "applications", "program", "programs", "programme", "programmes", "software",
    "installer", "installers", "setup", "tool", "tools", "logiciel", "logiciels",
];

fn bare(w: &str) -> String {
    w.to_lowercase().trim_matches(|c: char| !c.is_alphanumeric()).to_string()
}

/// `query` without the words of KIND_WORDS and APP_WORDS.
fn strip_kind_words(query: &str) -> String {
    query
        .split_whitespace()
        .filter(|w| {
            let w = bare(w);
            !KIND_WORDS.contains(&w.as_str()) && !APP_WORDS.contains(&w.as_str())
        })
        .collect::<Vec<_>>()
        .join(" ")
}

/// The guesses in a query, "xflr5, flow5 or airfoil" -> ["xflr5", "flow5", "airfoil"], each without its
/// kind words; empty when nothing is left.
fn guesses(query: &str) -> Vec<String> {
    query
        .split([',', ';', '|'])
        .flat_map(|g| g.split(" or ").collect::<Vec<_>>())
        .map(strip_kind_words)
        .filter(|g| !g.is_empty())
        .take(6)
        .collect()
}

/// Words too common to tell a file apart when a name holds one of them.
const STOP: [&str; 14] = ["for", "and", "with", "from", "about", "that", "this", "pour", "avec", "les", "des", "une", "the", "new"];

/// How well `name` matches the guesses: 2 when it holds every word of one, 1 when it holds a telling word
/// of one (three letters or more), 0 otherwise.
fn name_score(name: &str, guesses: &[String]) -> u8 {
    let n = name.to_lowercase();
    let mut best = 0;
    for g in guesses {
        let words: Vec<String> = g.split_whitespace().map(bare).filter(|w| w.chars().count() >= 2).collect();
        if words.is_empty() {
            continue;
        }
        if words.iter().all(|w| n.contains(w.as_str())) {
            return 2;
        }
        if words.iter().any(|w| w.chars().count() >= 3 && !STOP.contains(&w.as_str()) && n.contains(w.as_str())) {
            best = 1;
        }
    }
    best
}

/// Recently opened files and recent downloads given as candidates beside the matches, when no period
/// is asked: the file meant may share no word with how the user describes it.
const CONTEXT: usize = 15;

/// Find candidates for the file the user means, best guess first, at most `limit` (the shell has the
/// model judge them: chat ranks them by what each is, not by its name). `query` may be empty or hold
/// several guesses separated by commas; `from` and `to` are unix seconds (0: open-ended); `kind` is one
/// of the kinds in `kind_matches` or "any" (an app word in the query, "the application", makes it
/// "app"), and files of that kind come first, without leaving the others out; `folder` is one of the
/// user's own folders ("downloads") to look in alone, or "" for anywhere.
/// `by_name` and `by_topic` look the query up in the index and in the meaning search.
///
/// The net is wide on purpose: files that match by name, wholly or by one telling word; by topic; every
/// file of the period (a few per project); and, with no period, the files opened lately and the month's
/// downloads. Each comes with what is known of it (`about`).
#[allow(clippy::too_many_arguments)]
pub fn find_files(
    query: &str,
    from: i64,
    to: i64,
    kind: &str,
    folder: &str,
    limit: usize,
    by_name: &dyn Fn(&str) -> Vec<String>,
    by_topic: &(dyn Fn(&str) -> Vec<String> + Sync),
) -> Vec<FileFound> {
    let kind = if kind == "any" && query.split_whitespace().any(|w| APP_WORDS.contains(&bare(w).as_str())) { "app" } else { kind };
    let guesses = guesses(query);
    let guesses = guesses.as_slice();
    // The meaning search takes the longest (it asks another process): it runs while the folders are read.
    std::thread::scope(|scope| {
        let topic = scope.spawn(|| if guesses.is_empty() { Vec::new() } else { by_topic(&guesses.join(", ")) });
        gather(guesses, from, to, kind, folder, limit, by_name, || topic.join().unwrap_or_default())
    })
}

/// Extensions that mark a file as one an app needs rather than one a person means: its libraries, translations,
/// caches, compiled leftovers. Left out unless their name is what was asked for.
const SUPPORT: [&str; 20] = [
    "dll", "pyd", "so", "qm", "pak", "dat", "bin", "cache", "tmp", "log", "lock", "mui", "pdb", "lib", "obj", "o", "class",
    "pyc", "manifest", "etl",
];

#[allow(clippy::too_many_arguments)]
fn gather(
    guesses: &[String],
    from: i64,
    to: i64,
    kind: &str,
    folder: &str,
    limit: usize,
    by_name: &dyn Fn(&str) -> Vec<String>,
    by_topic: impl FnOnce() -> Vec<String>,
) -> Vec<FileFound> {
    let in_period = |t: i64| t > 0 && (from == 0 || t >= from) && (to == 0 || t < to);
    let timed = from != 0 || to != 0;
    let folder = if folder == "any" { "" } else { folder };
    let roots = own_folders(folder);
    let in_folder = |p: &str| folder.is_empty() || roots.iter().any(|r| Path::new(p).starts_with(r));

    // path (lowercased) -> (path, opened, reasons)
    let mut found: HashMap<String, (String, i64, Vec<&'static str>)> = HashMap::new();
    let mut add = |path: String, opened: i64, why: &'static str| {
        let e = found.entry(path.to_lowercase()).or_insert_with(|| (path, 0, Vec::new()));
        e.1 = e.1.max(opened);
        if !e.2.contains(&why) {
            e.2.push(why);
        }
    };

    let recent = recently_opened(400);
    let opened_at: HashMap<String, i64> = recent.iter().map(|(p, t)| (p.to_lowercase(), *t)).collect();
    let opened = |p: &str| opened_at.get(&p.to_lowercase()).copied().unwrap_or(0);
    for (i, (p, t)) in recent.iter().enumerate() {
        let name = Path::new(p).file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
        let keep = if timed { in_period(*t) } else { guesses.is_empty() || i < CONTEXT || name_score(&name, guesses) > 0 };
        if keep {
            add(p.clone(), *t, "opened");
        }
    }
    let walked = if timed {
        changed_in(&roots, from, to, 400, 60_000)
    } else if !folder.is_empty() {
        changed_in(&roots, 0, 0, 200, 20_000)
    } else if !guesses.is_empty() {
        // A download is never in the Recent folder: the month's are candidates too.
        changed_in(&own_folders("downloads"), now() - 30 * 86_400, 0, 60, 20_000)
    } else {
        Vec::new()
    };
    for (p, _) in walked {
        let t = opened(&p);
        add(p, t, "changed");
    }
    for g in guesses {
        for p in by_name(g) {
            let t = opened(&p);
            add(p, t, "name");
        }
    }
    // One meaning search for all the guesses: it reads them as one topic, and each takes seconds.
    for p in by_topic() {
        let t = opened(&p);
        add(p, t, "topic");
    }

    let mut out: Vec<(u32, FileFound)> = found
        .into_values()
        .filter(|(p, _, _)| in_folder(p))
        .filter_map(|(path, opened, why)| {
            let md = std::fs::metadata(&path).ok()?;
            if !md.is_file() {
                return None;
            }
            let modified = md.modified().map(unix).unwrap_or(0);
            // Asked about a period: opened or changed in it.
            if timed && !in_period(opened) && !in_period(modified) {
                return None;
            }
            let p = Path::new(&path);
            let name = p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_default();
            let ext = p.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
            if SUPPORT.contains(&ext.as_str()) && name_score(&name, guesses) < 2 {
                return None;
            }
            // A guess at the order, for when the model cannot rank them: by name first, then by topic,
            // then opened in the period.
            // The kind is the assistant's guess ("something for analysing wings" taken for a document): it
            // ranks files, it does not leave the others out.
            let score = (kind != "any" && kind_matches(kind, &path)) as u32 * 4
                + name_score(&name, guesses) as u32 * 3
                + why.contains(&"topic") as u32 * 2
                + (timed && in_period(opened)) as u32;
            Some((
                score,
                FileFound {
                    name,
                    folder: p.parent().map(|f| f.display().to_string()).unwrap_or_default(),
                    path,
                    modified,
                    opened,
                    why: why.join("+"),
                    about: String::new(),
                },
            ))
        })
        .collect();
    out.sort_by_key(|(score, f)| (std::cmp::Reverse(*score), std::cmp::Reverse(f.opened.max(f.modified))));

    // At most three from one project (a folder in Desktop, Downloads..., however deep): a day of work on
    // one, or an unzipped app, would hide the one download among its files.
    let all_roots = own_folders("");
    let project = |p: &str| -> Option<String> {
        let p = Path::new(p);
        let rest = all_roots.iter().find_map(|r| p.strip_prefix(r).ok().map(|rest| (r, rest)));
        match rest {
            Some((r, rest)) if rest.components().count() > 1 => Some(r.join(rest.components().next()?).display().to_string().to_lowercase()),
            Some(_) => None, // loose in Downloads or Desktop: each its own
            None => p.parent().map(|f| f.display().to_string().to_lowercase()),
        }
    };
    let mut per: HashMap<String, usize> = HashMap::new();
    out.retain(|(_, f)| {
        let Some(key) = project(&f.path) else { return true };
        let n = per.entry(key).or_default();
        *n += 1;
        *n <= 3
    });
    out.truncate(limit.clamp(1, 40));
    out.into_iter()
        .map(|(_, mut f)| {
            f.about = about(&f.path);
            f
        })
        .collect()
}

/// What is known of a file beyond its name, in a few words for the model: what a program says it is and
/// who made it, what an archive holds (and the programs in it once unzipped beside it), and the site it
/// was downloaded from. "" when nothing is.
pub fn about(path: &str) -> String {
    let p = Path::new(path);
    let ext = p.extension().map(|e| e.to_string_lossy().to_lowercase()).unwrap_or_default();
    let mut parts: Vec<String> = Vec::new();
    match ext.as_str() {
        "exe" => {
            if let Some(v) = program(path) {
                parts.push(v);
            }
            if let Some(dir) = p.parent() {
                let beside = std::fs::read_dir(dir).into_iter().flatten().flatten().take(400);
                let libs = telling_libraries(beside.map(|e| e.file_name().to_string_lossy().into_owned()));
                if !libs.is_empty() {
                    parts.push(format!("beside the libraries {}", libs.join(", ")));
                }
            }
        }
        "zip" => {
            if let Some(names) = zip_names(path, 5000) {
                let mut top: Vec<String> = Vec::new();
                for n in &names {
                    let first = n.split(['/', '\\']).next().unwrap_or("").to_string();
                    if !first.is_empty() && !top.contains(&first) {
                        top.push(first);
                    }
                }
                let mut exes: Vec<&String> = names.iter().filter(|n| n.to_lowercase().ends_with(".exe")).collect();
                exes.sort_by_key(|n| (n.matches('/').count(), n.len()));
                let short = |n: &str| n.rsplit(['/', '\\']).next().unwrap_or(n).to_string();
                let mut s = format!("a zip of {} files", names.len());
                if !top.is_empty() {
                    s += &format!(" in {}", top.iter().take(4).cloned().collect::<Vec<_>>().join(", "));
                }
                if !exes.is_empty() {
                    s += &format!(", with the programs {}", exes.iter().take(3).map(|n| short(n)).collect::<Vec<_>>().join(", "));
                }
                // The app's own libraries sit at its top; plugin folders (Qt's imageformats...) hold the generic ones.
                let mut by_depth: Vec<&String> = names.iter().filter(|n| !PLUGIN_DIRS.iter().any(|d| n.to_lowercase().contains(d))).collect();
                by_depth.sort_by_key(|n| n.matches('/').count());
                let libs = telling_libraries(by_depth.into_iter().map(|n| short(n)));
                if !libs.is_empty() {
                    s += &format!(" and the libraries {}", libs.join(", "));
                }
                parts.push(s);
            }
            // Unzipped beside it: what its main program says it is.
            if let (Some(dir), Some(stem)) = (p.parent(), p.file_stem()) {
                let unzipped = dir.join(stem);
                if unzipped.is_dir() {
                    let mut exes: Vec<(usize, String)> = Vec::new();
                    let mut stack = vec![(unzipped, 0usize)];
                    while let Some((d, depth)) = stack.pop() {
                        for e in std::fs::read_dir(&d).into_iter().flatten().flatten().take(400) {
                            let ep = e.path();
                            if ep.is_dir() && depth < 2 {
                                stack.push((ep, depth + 1));
                            } else if ep.extension().is_some_and(|x| x.eq_ignore_ascii_case("exe")) {
                                exes.push((depth, ep.display().to_string()));
                            }
                        }
                    }
                    exes.sort();
                    if let Some(v) = exes.iter().take(3).find_map(|(_, e)| program(e)) {
                        parts.push(format!("unzipped beside it: {v}"));
                    }
                }
            }
        }
        _ => {}
    }
    if let Some(site) = downloaded_from(path) {
        parts.push(format!("downloaded from {site}"));
    }
    parts.join("; ")
}

/// Folders of an app's plugins, whose libraries say nothing of what the app does.
const PLUGIN_DIRS: [&str; 10] = [
    "/platforms/", "/imageformats/", "/styles/", "/iconengines/", "/generic/", "/tls/", "/networkinformation/", "/sqldrivers/",
    "/plugins/", "/translations/",
];

/// The libraries an app ships that tell what it does ("XFoil1" is an airfoil solver, "opencv_world" vision),
/// from its file names: the runtimes every app carries left out, one name for each family (Qt6Core,
/// Qt6Gui...), at most eight.
fn telling_libraries(names: impl Iterator<Item = String>) -> Vec<String> {
    const COMMON: [&str; 24] = [
        "api-ms-", "vcruntime", "msvcp", "ucrtbase", "concrt", "vccorlib", "libiomp", "libgcc", "libstdc", "libwinpthread",
        "d3dcompiler", "opengl32sw", "libegl", "libgles", "qt5", "qt6", "icu", "libcrypto", "libssl", "zlib", "vulkan-1", "mfc", "vcomp", "mkl_",
    ];
    let mut out: Vec<String> = Vec::new();
    let mut families: Vec<String> = Vec::new();
    for n in names {
        let lower = n.to_lowercase();
        let Some(stem) = lower.strip_suffix(".dll").or_else(|| lower.strip_suffix(".pyd")) else { continue };
        if COMMON.iter().any(|c| stem.starts_with(c)) {
            continue;
        }
        // A family by its first letters: an acronym's capitals (TKBO, TKBRep... are one, OpenCASCADE's), or
        // the first four letters (libboost_...).
        let caps = n.chars().take_while(|c| c.is_ascii_uppercase()).count();
        let take = if caps >= 2 { 2 } else { 4 };
        let family: String = stem.chars().take_while(|c| c.is_alphabetic()).take(take).collect();
        let family = if family.len() >= 2 { family } else { stem.to_string() };
        if families.contains(&family) {
            continue;
        }
        families.push(family);
        // The name less its 4-byte extension: cut from `n`, not by `stem`'s length (lowercasing can
        // change it, and a name inside a downloaded zip can be anything).
        out.push(n.get(..n.len().saturating_sub(4)).unwrap_or(&n).to_string());
        if out.len() == 8 {
            break;
        }
    }
    out
}

/// What a program's version information says: "flow5 (product flow5, by techwinder)".
fn program(exe: &str) -> Option<String> {
    let v = crate::wins::version_strings(exe, &["FileDescription", "ProductName", "CompanyName"]);
    let (desc, product, company) = (v[0].clone(), v[1].clone(), v[2].clone());
    let mut s = desc.clone().or(product.clone())?;
    if let Some(pr) = product.filter(|pr| Some(pr) != desc.as_ref()) {
        s += &format!(" (product {pr})");
    }
    if let Some(c) = company {
        s += &format!(", by {c}");
    }
    Some(s)
}

/// The page a file was downloaded from, from the mark Windows leaves on downloads (its Zone.Identifier):
/// the site and the start of its path, which often names the project ("github.com/techwinder/flow5").
fn downloaded_from(path: &str) -> Option<String> {
    let zone = std::fs::read_to_string(format!("{path}:Zone.Identifier")).ok()?;
    let url = ["ReferrerUrl=", "HostUrl="]
        .iter()
        .find_map(|k| zone.lines().find_map(|l| l.trim().strip_prefix(k)).filter(|u| u.starts_with("http")))?;
    let rest = url.split("//").nth(1)?.split(['?', '#']).next()?;
    let mut segs = rest.split('/').filter(|s| !s.is_empty());
    let host = segs.next()?.split(':').next()?;
    let host = host.strip_prefix("www.").unwrap_or(host);
    let page: Vec<&str> = segs.take(2).collect();
    let s = std::iter::once(host).chain(page).collect::<Vec<_>>().join("/");
    (!host.is_empty()).then(|| s.chars().take(80).collect())
}

/// The names of the entries of a zip, from its central directory; None for a file that is not one (or a
/// zip64, too big to matter here).
fn zip_names(path: &str, max: usize) -> Option<Vec<String>> {
    use std::io::{Read, Seek, SeekFrom};
    let mut f = std::fs::File::open(path).ok()?;
    let len = f.metadata().ok()?.len();
    let tail = len.min(66_000);
    f.seek(SeekFrom::Start(len - tail)).ok()?;
    let mut buf = vec![0u8; tail as usize];
    f.read_exact(&mut buf).ok()?;
    let at = (0..buf.len().saturating_sub(21)).rev().find(|&i| buf[i..i + 4] == [0x50, 0x4b, 0x05, 0x06])?;
    let u16_at = |b: &[u8], i: usize| u16::from_le_bytes([b[i], b[i + 1]]) as usize;
    let u32_at = |b: &[u8], i: usize| u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]]) as u64;
    let (size, offset) = (u32_at(&buf, at + 12), u32_at(&buf, at + 16));
    if size == 0xFFFF_FFFF || offset == 0xFFFF_FFFF || size > 8 << 20 || offset + size > len {
        return None;
    }
    let mut cd = vec![0u8; size as usize];
    f.seek(SeekFrom::Start(offset)).ok()?;
    f.read_exact(&mut cd).ok()?;
    let mut names = Vec::new();
    let mut i = 0usize;
    while i + 46 <= cd.len() && cd[i..i + 4] == [0x50, 0x4b, 0x01, 0x02] && names.len() < max {
        let (n, x, c) = (u16_at(&cd, i + 28), u16_at(&cd, i + 30), u16_at(&cd, i + 32));
        let name = cd.get(i + 46..i + 46 + n)?;
        names.push(String::from_utf8_lossy(name).into_owned());
        i += 46 + n + x + c;
    }
    Some(names)
}

/// The paths of files among `hits` (for `find_files`'s `by_name` and `by_topic`).
pub fn file_paths(hits: impl IntoIterator<Item = crate::model::Hit>) -> Vec<String> {
    hits.into_iter()
        .filter(|h| h.kind == Kind::File)
        .filter_map(|h| match h.action {
            Action::Open(p) => Some(p),
            _ => None,
        })
        .collect()
}

/// Unix seconds now (for callers that turn "last week" into a range).
pub fn now() -> i64 {
    unix(SystemTime::now())
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn a_library_name_that_lowercases_longer_is_kept_whole() {
        // İ lowercases to two characters: the name must not be cut by the lowercase copy's length.
        let found = telling_libraries(["\u{130}\u{130}\u{130}Engine.DLL".to_string(), "\u{212a}elvin.dll".to_string()].into_iter());
        assert_eq!(found, vec!["\u{130}\u{130}\u{130}Engine".to_string(), "\u{212a}elvin".to_string()]);
    }

    #[test]
    fn kinds_go_by_extension() {
        assert!(kind_matches("document", r"C:\a\lease.PDF"));
        assert!(!kind_matches("document", r"C:\a\photo.jpg"));
        assert!(kind_matches("code", r"C:\src\main.rs"));
        assert!(kind_matches("any", r"C:\a\whatever.xyz"));
    }

    #[test]
    fn a_walk_finds_files_changed_in_the_period() {
        let dir = std::env::temp_dir().join(format!("palette-tools-{}", std::process::id()));
        let _ = std::fs::create_dir_all(dir.join("sub").join("node_modules"));
        std::fs::write(dir.join("sub").join("photo.jpg"), b"x").unwrap();
        std::fs::write(dir.join("sub").join("node_modules").join("skipped.js"), b"x").unwrap();
        let now = now();
        let found = changed_in(&[dir.clone()], now - 3600, 0, 10, 1000);
        let _ = std::fs::remove_dir_all(&dir);
        assert_eq!(found.len(), 1, "{found:?}");
        assert!(found[0].0.ends_with("photo.jpg"));
        assert!(changed_in(&[dir], now + 3600, 0, 10, 1000).is_empty());
    }

    #[test]
    fn words_for_the_kind_of_file_are_not_looked_for_in_names() {
        assert_eq!(strip_kind_words("photos"), "");
        assert_eq!(strip_kind_words("my holiday photos"), "holiday");
        assert_eq!(strip_kind_words("the Q3 report"), "Q3 report");
        assert_eq!(strip_kind_words("any"), "");
        assert_eq!(strip_kind_words("the application I downloaded"), "I");
    }

    #[test]
    fn a_query_may_hold_several_guesses() {
        assert_eq!(guesses("xflr5, flow5 or airfoil"), ["xflr5", "flow5", "airfoil"]);
        assert_eq!(guesses("photos, "), Vec::<String>::new());
        let g = guesses("xflr5, flow5");
        assert_eq!(name_score("flow5_v7.57_win64.zip", &g), 2);
        assert_eq!(name_score("acc2026.1_win64.exe", &g), 0);
    }

    #[test]
    fn apps_go_by_extension_and_come_zipped() {
        assert!(kind_matches("app", r"C:\Downloads\flow5_v7.57_win64.zip"));
        assert!(kind_matches("app", r"C:\Downloads\setup.MSI"));
        assert!(!kind_matches("app", r"C:\Downloads\notes.txt"));
    }

    #[test]
    fn a_name_matches_wholly_or_by_a_telling_word() {
        let g = |q: &str| guesses(q);
        assert_eq!(name_score("Quarterly Report Q3.docx", &g("report q3")), 2);
        assert_eq!(name_score("Quarterly Report Q3.docx", &g("report q4")), 1);
        assert_eq!(name_score("Quarterly Report Q3.docx", &g("q4 for")), 0);
        assert_eq!(name_score("wing_geometry.txt", &g("wing analysis")), 1);
    }

    #[test]
    fn a_zip_tells_what_it_holds() {
        // A stored (uncompressed) zip with one entry, "app/run.exe", written by hand.
        let name = b"app/run.exe";
        let mut z = Vec::new();
        z.extend_from_slice(&[0x50, 0x4b, 0x03, 0x04]);
        z.extend_from_slice(&[0u8; 22]);
        z.extend_from_slice(&(name.len() as u16).to_le_bytes());
        z.extend_from_slice(&[0, 0]);
        z.extend_from_slice(name);
        let cd_at = z.len() as u32;
        z.extend_from_slice(&[0x50, 0x4b, 0x01, 0x02]);
        z.extend_from_slice(&[0u8; 24]);
        z.extend_from_slice(&(name.len() as u16).to_le_bytes());
        z.extend_from_slice(&[0u8; 16]);
        z.extend_from_slice(name);
        let cd_size = z.len() as u32 - cd_at;
        z.extend_from_slice(&[0x50, 0x4b, 0x05, 0x06, 0, 0, 0, 0, 1, 0, 1, 0]);
        z.extend_from_slice(&cd_size.to_le_bytes());
        z.extend_from_slice(&cd_at.to_le_bytes());
        z.extend_from_slice(&[0, 0]);
        let path = std::env::temp_dir().join(format!("palette-zip-{}.zip", std::process::id()));
        std::fs::write(&path, &z).unwrap();
        let p = path.display().to_string();
        let names = zip_names(&p, 10);
        let said = about(&p);
        let _ = std::fs::remove_file(&path);
        assert_eq!(names, Some(vec!["app/run.exe".to_string()]));
        assert!(said.contains("in app") && said.contains("run.exe"), "{said}");
    }
}
