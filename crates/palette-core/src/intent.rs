//! Requests in plain words, carried out as a few steps: "open notepad and weather side by side",
//! "show desktop", "close chrome", "snap edge left", "mute and lock", "next song".
//!
//! A request becomes a plan ([`Step`]s) shown as one row that says what Enter will do. The plan is
//! made of building blocks (open, snap, close, quick settings, ...); here rules read the request,
//! and a language model can later write the same plans for phrasings the rules miss.

use crate::model::{Action, Hit, Item, Kind};
use crate::wins::{self, OpenWindow};
use serde::{Deserialize, Serialize};
use std::collections::HashSet;
use std::time::{Duration, Instant};

/// An app as the catalog knows it: its name and what opens it.
#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub struct App {
    pub name: String,
    pub target: String,
}

/// Where windows go. `Side` puts them in columns, `Grid` in a grid.
#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub enum Layout {
    Side,
    Grid,
    Left,
    Right,
    Top,
    Bottom,
    Full,
}

#[derive(Clone, Copy, Debug, PartialEq, Serialize, Deserialize)]
pub enum Media {
    PlayPause,
    Next,
    Previous,
    Stop,
}

#[derive(Clone, Debug, PartialEq, Serialize, Deserialize)]
pub enum Step {
    Open(App),
    Focus(App),
    Close(App),
    Minimize(App),
    Maximize(App),
    /// Snap the apps' windows (opening any that is not open).
    Arrange { layout: Layout, apps: Vec<App> },
    ShowDesktop,
    Media(Media),
    /// A quick setting (see quick.rs), by its `quick:` id.
    Quick { id: String, title: String },
    Uri { uri: String, title: String },
    /// A built-in command (lock, sleep).
    Builtin { id: String, title: String },
    /// Close every window but those of `keep`.
    CloseAll { keep: Vec<App> },
}

/// The row leads, as a quick setting asked for does.
const SCORE: i32 = 1005;

/// The row for `query`, if it reads as a request.
pub fn hits(query: &str, items: &[Item]) -> Vec<Hit> {
    let Some(plan) = parse(query, items) else { return Vec::new() };
    vec![plan_hit(plan)]
}

/// The row for `plan`: says what Enter will do.
pub fn plan_hit(plan: Vec<Step>) -> Hit {
    let (title, parts) = describe(&plan);
    let subtitle = if parts.len() > 1 { format!("{} steps", parts.len()) } else { "Runs at once".into() };
    Hit { title, subtitle, kind: Kind::Plan, action: Action::Plan(plan), score: SCORE }
}

// ---------------------------------------------------------------- reading the request

#[derive(Clone, Copy, PartialEq)]
enum Verb {
    Open,
    Close,
    Minimize,
    Maximize,
    Focus,
    Snap,
}

const VERBS: &[(&str, Verb)] = &[
    ("open up", Verb::Open),
    ("open", Verb::Open),
    ("launch", Verb::Open),
    ("start up", Verb::Open),
    ("start", Verb::Open),
    ("run", Verb::Open),
    ("fire up", Verb::Open),
    ("bring up", Verb::Open),
    ("pull up", Verb::Open),
    ("close", Verb::Close),
    ("quit", Verb::Close),
    ("exit", Verb::Close),
    ("kill", Verb::Close),
    ("minimize", Verb::Minimize),
    ("minimise", Verb::Minimize),
    ("hide", Verb::Minimize),
    ("maximize", Verb::Maximize),
    ("maximise", Verb::Maximize),
    ("switch to", Verb::Focus),
    ("go to", Verb::Focus),
    ("focus on", Verb::Focus),
    ("focus", Verb::Focus),
    ("show", Verb::Focus),
    ("snap", Verb::Snap),
    ("put", Verb::Snap),
    ("move", Verb::Snap),
    ("place", Verb::Snap),
    ("arrange", Verb::Snap),
    ("tile", Verb::Snap),
];

/// How a group of windows is laid out, said at the end of the request.
const LAYOUTS: &[(&str, Layout)] = &[
    ("side by side", Layout::Side),
    ("next to each other", Layout::Side),
    ("beside each other", Layout::Side),
    ("snapped together", Layout::Side),
    ("snapped", Layout::Side),
    ("in split screen", Layout::Side),
    ("split screen", Layout::Side),
    ("split", Layout::Side),
    ("in halves", Layout::Side),
    ("in columns", Layout::Side),
    ("in thirds", Layout::Side),
    ("in a grid", Layout::Grid),
    ("in quarters", Layout::Grid),
    ("in the corners", Layout::Grid),
    ("tiled", Layout::Grid),
];

/// Where one window goes ("snap edge left"), said after its name.
const PLACES: &[(&str, Layout)] = &[
    ("on the left side", Layout::Left),
    ("on the left", Layout::Left),
    ("to the left", Layout::Left),
    ("left side", Layout::Left),
    ("left", Layout::Left),
    ("on the right side", Layout::Right),
    ("on the right", Layout::Right),
    ("to the right", Layout::Right),
    ("right side", Layout::Right),
    ("right", Layout::Right),
    ("on top", Layout::Top),
    ("to the top", Layout::Top),
    ("at the top", Layout::Top),
    ("at the bottom", Layout::Bottom),
    ("to the bottom", Layout::Bottom),
    ("full screen", Layout::Full),
    ("fullscreen", Layout::Full),
    ("maximized", Layout::Full),
];

/// Whole requests that need no app. `alone`: enough for a row by itself (bare "play" or "lock"
/// is too common a search to take over the list; in "open spotify and play" it is meant).
fn special(c: &str) -> Option<(Step, bool)> {
    let media = |m| Some((Step::Media(m), true));
    match c {
        "show desktop" | "show the desktop" | "show my desktop" | "go to desktop" | "go to the desktop"
        | "minimize all" | "minimize everything" | "minimize all windows" | "minimise all" | "minimise everything"
        | "minimise all windows" | "hide all windows" | "hide everything" | "hide all" => Some((Step::ShowDesktop, true)),
        "play music" | "pause music" | "resume music" | "play pause" | "play the music" | "pause the music"
        | "resume playback" | "pause playback" => media(Media::PlayPause),
        "next song" | "next track" | "skip song" | "skip track" | "skip this song" | "skip the song" => media(Media::Next),
        "previous song" | "previous track" | "last song" | "go back a song" | "play the last song" => media(Media::Previous),
        "stop music" | "stop the music" | "stop playback" => media(Media::Stop),
        "play" | "pause" | "resume" => Some((Step::Media(Media::PlayPause), false)),
        "next" | "skip" => Some((Step::Media(Media::Next), false)),
        "previous" => Some((Step::Media(Media::Previous), false)),
        "take a screenshot" | "take screenshot" | "take a screen shot" | "screen snip" | "snip the screen"
        | "capture the screen" | "capture screen" | "take a snip" => Some((shot(), true)),
        "screenshot" | "snip" => Some((shot(), false)),
        "lock" | "lock pc" | "lock the pc" | "lock my pc" | "lock computer" | "lock the computer" | "lock my computer"
        | "lock screen" | "lock the screen" => Some((Step::Builtin { id: "lock".into(), title: "Lock this PC".into() }, false)),
        "sleep" | "go to sleep" | "put the pc to sleep" | "put my pc to sleep" | "put pc to sleep" => {
            Some((Step::Builtin { id: "sleep".into(), title: "Put this PC to sleep".into() }, false))
        }
        _ => None,
    }
}

pub(crate) fn shot() -> Step {
    Step::Uri { uri: "ms-screenclip:".into(), title: "Take a screenshot".into() }
}

fn normalize(q: &str) -> String {
    let s: String = q
        .to_lowercase()
        .chars()
        .map(|c| if c.is_alphanumeric() || matches!(c, '\'' | '-' | '+' | '&' | '.') { c } else if c == ',' { '\u{1}' } else { ' ' })
        .collect();
    let mut s = s.replace('\u{1}', " and ").split_whitespace().collect::<Vec<_>>().join(" ");
    const LEAD: &[&str] = &[
        "please ", "can you ", "could you ", "would you ", "will you ", "hey ", "ok ", "i want to ", "i'd like to ",
        "id like to ", "i wanna ", "let's ", "lets ", "go ahead and ", "just ", "quickly ",
    ];
    const TRAIL: &[&str] = &[" please", " for me", " now", " thanks"];
    loop {
        let before = s.len();
        for p in LEAD {
            if let Some(r) = s.strip_prefix(p) {
                s = r.to_string();
            }
        }
        for p in TRAIL {
            if let Some(r) = s.strip_suffix(p) {
                s = r.to_string();
            }
        }
        if s.len() == before {
            return s;
        }
    }
}

fn strip_verb(c: &str) -> Option<(Verb, &str)> {
    VERBS.iter().find_map(|(w, v)| {
        if c == *w {
            Some((*v, ""))
        } else {
            c.strip_prefix(w).and_then(|r| r.strip_prefix(' ')).map(|r| (*v, r))
        }
    })
}

fn strip_suffix_of<'a>(c: &'a str, table: &[(&str, Layout)]) -> (&'a str, Option<Layout>) {
    for (p, l) in table {
        if c == *p {
            return ("", Some(*l));
        }
        if let Some(r) = c.strip_suffix(p).and_then(|r| r.strip_suffix(' ')) {
            return (r, Some(*l));
        }
    }
    (c, None)
}

fn is_them(s: &str) -> bool {
    matches!(s, "them" | "both" | "them all" | "all of them" | "both of them" | "these" | "those" | "it" | "all")
}

pub fn parse(query: &str, items: &[Item]) -> Option<Vec<Step>> {
    // One plain step ("open notepad", "mute", "lock") already has its own row.
    read(query, items).and_then(|(plan, alone)| (plan.len() > 1 || alone).then_some(plan))
}

/// The steps `query` asks for, and whether they are worth a row by themselves.
pub fn read(query: &str, items: &[Item]) -> Option<(Vec<Step>, bool)> {
    let q = normalize(query);
    if q.len() < 4 {
        return None;
    }
    // "... side by side" lays out every app the request names.
    let mut text = q.as_str();
    let mut group = None;
    loop {
        let (rest, l) = strip_suffix_of(text, LAYOUTS);
        let Some(l) = l else { break };
        group = Some(l);
        text = rest.trim_end();
        for tail in [" and snap them", " and snap", " snap them", " and", " then", " snap"] {
            if let Some(r) = text.strip_suffix(tail) {
                text = r;
            }
        }
    }

    let clauses: Vec<&str> = text
        .split(" and then ")
        .flat_map(|c| c.split(" then "))
        .flat_map(|c| c.split(" and "))
        .flat_map(|c| c.split(" & "))
        .flat_map(|c| c.split(" + "))
        .flat_map(|c| c.split(" plus "))
        .map(str::trim)
        .filter(|c| !c.is_empty())
        .collect();
    if clauses.is_empty() {
        return None;
    }

    let mut plan = Vec::new();
    let mut verb: Option<Verb> = None;
    let mut named: Vec<App> = Vec::new(); // every app named so far, for "them" and the layout
    let mut alone = false; // something in the plan is worth a row by itself
    for c in clauses {
        if let Some((step, a)) = special(c) {
            plan.push(step);
            alone |= a;
            verb = None;
            continue;
        }
        let (v, rest) = match strip_verb(c) {
            Some((v, r)) => (Some(v), r),
            None => (verb, c),
        };
        // A quick setting, whole: "dark mode", "turn wifi off", "volume 30".
        if let Some(h) = crate::quick::hits(c).into_iter().find(|h| h.score >= 1000) {
            if let Action::Builtin(id) = &h.action {
                plan.push(Step::Quick { id: id.trim_start_matches("quick:").to_string(), title: h.title.clone() });
                verb = None;
                continue;
            }
        }
        let v = v?;
        verb = Some(v);
        let (name, place) = strip_suffix_of(rest, PLACES);
        let name = name.trim();
        if v == Verb::Snap && is_them(name) {
            let apps = named.clone();
            if apps.is_empty() {
                return None;
            }
            plan.push(Step::Arrange { layout: place.unwrap_or(Layout::Side), apps });
            alone = true;
            group = None;
            continue;
        }
        if v == Verb::Open {
            if let Some(s) = find_setting(name, items) {
                plan.push(s);
                continue;
            }
        }
        let app = find_app(name, items)?;
        named.push(app.clone());
        let step = match (v, place) {
            (Verb::Open, Some(l)) | (Verb::Snap, Some(l)) | (Verb::Focus, Some(l)) => {
                if v == Verb::Open {
                    plan.push(Step::Open(app.clone()));
                }
                alone = true;
                Step::Arrange { layout: l, apps: vec![app] }
            }
            (Verb::Snap, None) => {
                alone = true;
                // "snap notepad and paint" with no place: side by side, below
                if group.is_none() {
                    group = Some(Layout::Side);
                }
                continue;
            }
            (Verb::Open, None) => Step::Open(app),
            (Verb::Focus, None) => Step::Focus(app),
            (Verb::Close, _) => Step::Close(app),
            (Verb::Minimize, _) => Step::Minimize(app),
            (Verb::Maximize, _) => Step::Maximize(app),
        };
        if !matches!(step, Step::Open(_) | Step::Focus(_)) {
            alone = true;
        }
        plan.push(step);
    }
    if let Some(l) = group {
        // Each app once, in the order named.
        let mut seen = HashSet::new();
        let apps: Vec<App> = named.into_iter().filter(|a| seen.insert(a.target.clone())).collect();
        if apps.is_empty() {
            return None;
        }
        plan.push(Step::Arrange { layout: l, apps });
        alone = true;
    }
    Some((plan, alone))
}

/// Words around an app's name that are not part of it ("the", "app", "window").
pub(crate) fn bare_name(s: &str) -> &str {
    let mut s = s.trim();
    for p in ["the ", "my ", "a ", "all ", "every ", "both "] {
        s = s.strip_prefix(p).unwrap_or(s);
    }
    for p in [" app", " apps", " application", " window", " windows", " program", " again"] {
        s = s.strip_suffix(p).unwrap_or(s);
    }
    s.trim()
}

/// What people call some apps.
pub(crate) fn alias(s: &str) -> &str {
    match s {
        "explorer" | "files" | "file manager" | "my files" => "file explorer",
        "cmd" | "command line" => "command prompt",
        "calc" => "calculator",
        "store" => "microsoft store",
        "vscode" | "vs code" => "visual studio code",
        "task manager" | "taskmgr" => "task manager",
        "browser" | "the browser" | "internet" => "microsoft edge",
        "music" => "media player",
        "photos app" => "photos",
        "mail" | "email" => "outlook",
        _ => s,
    }
}

pub(crate) fn word_starts(name: &str, words: &[&str]) -> bool {
    let parts: Vec<&str> = name.split(|c: char| !c.is_alphanumeric()).filter(|p| !p.is_empty()).collect();
    words.iter().all(|w| parts.iter().any(|p| p.starts_with(w)))
}

/// Edits between `a` and `b` (a swap of two letters is one), up to `max`; more is `max + 1`.
fn edits(a: &str, b: &str, max: usize) -> usize {
    let (a, b): (Vec<char>, Vec<char>) = (a.chars().collect(), b.chars().collect());
    if a.len().abs_diff(b.len()) > max {
        return max + 1;
    }
    let mut d = vec![vec![0usize; b.len() + 1]; a.len() + 1];
    for (i, row) in d.iter_mut().enumerate() {
        row[0] = i;
    }
    for j in 0..=b.len() {
        d[0][j] = j;
    }
    for i in 1..=a.len() {
        for j in 1..=b.len() {
            let cost = usize::from(a[i - 1] != b[j - 1]);
            d[i][j] = (d[i - 1][j] + 1).min(d[i][j - 1] + 1).min(d[i - 1][j - 1] + cost);
            if i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1] {
                d[i][j] = d[i][j].min(d[i - 2][j - 2] + 1);
            }
        }
    }
    d[a.len()][b.len()].min(max + 1)
}

/// As [`find_app`], and failing that a close spelling of the name or one of its words
/// ("crome", "spotfy", "outlok"). For names a model copied from the request, typos included.
pub fn find_app_loose(name: &str, items: &[Item]) -> Option<App> {
    if let Some(a) = find_app(name, items) {
        return Some(a);
    }
    let n = alias(bare_name(name));
    let n = n.to_lowercase();
    let len = n.chars().count();
    if len < 4 {
        return None;
    }
    let max = if len >= 7 { 2 } else { 1 };
    // An alias spelt close ("vscdoe")
    for key in ["vscode", "explorer", "calc", "taskmgr", "browser"] {
        if edits(&n, key, max) <= max {
            return find_app(key, items);
        }
    }
    let mut best: Option<(usize, usize, &Item)> = None;
    for it in items.iter().filter(|i| i.hit.kind == Kind::App) {
        let nm = it.name.to_lowercase();
        let whole = edits(&n, &nm, max);
        let word = nm.split(|c: char| !c.is_alphanumeric()).filter(|w| w.chars().count() >= 4).map(|w| edits(&n, w, max)).min();
        let d = whole.min(word.unwrap_or(max + 1));
        if d > max {
            continue;
        }
        if best.is_none_or(|(bd, bl, _)| d < bd || (d == bd && nm.len() < bl)) {
            best = Some((d, nm.len(), it));
        }
    }
    let (_, _, it) = best?;
    match &it.hit.action {
        Action::Open(t) => Some(App { name: it.name.clone(), target: t.clone() }),
        _ => None,
    }
}

/// The app `name` means: by its full name, its beginning, or the start of its words.
pub fn find_app(name: &str, items: &[Item]) -> Option<App> {
    let n = alias(bare_name(name));
    if n.len() < 2 || is_them(n) {
        return None;
    }
    let words: Vec<&str> = n.split_whitespace().collect();
    let mut best: Option<(i32, usize, &Item)> = None;
    for it in items.iter().filter(|i| i.hit.kind == Kind::App) {
        let nm = it.name.to_lowercase();
        let s = if nm == n {
            100
        } else if nm.starts_with(n) {
            80
        } else if n.len() >= 3 && word_starts(&nm, &words) {
            60
        } else {
            continue;
        };
        let better = match best {
            None => true,
            Some((bs, bl, _)) => s > bs || (s == bs && nm.len() < bl),
        };
        if better {
            best = Some((s, nm.len(), it));
        }
    }
    let (_, _, it) = best?;
    match &it.hit.action {
        Action::Open(t) => Some(App { name: it.name.clone(), target: t.clone() }),
        _ => None,
    }
}

/// "open bluetooth settings", "open display settings": a Settings page.
fn find_setting(name: &str, items: &[Item]) -> Option<Step> {
    let n = bare_name(name);
    let n = n.strip_suffix(" settings").or_else(|| n.strip_suffix(" setting")).or_else(|| n.strip_prefix("settings for "))?;
    let words: Vec<&str> = n.split_whitespace().collect();
    if words.is_empty() {
        return None;
    }
    let it = items
        .iter()
        .filter(|i| i.hit.kind == Kind::Setting && i.keywords.is_none())
        .filter(|i| word_starts(&i.name.to_lowercase(), &words))
        .min_by_key(|i| i.name.len())?;
    match &it.hit.action {
        Action::Open(uri) => Some(Step::Uri { uri: uri.clone(), title: format!("Open {}", it.name) }),
        _ => None,
    }
}

// ---------------------------------------------------------------- saying it

fn names(apps: &[&App]) -> String {
    match apps {
        [] => String::new(),
        [a] => a.name.clone(),
        [rest @ .., last] => format!("{} and {}", rest.iter().map(|a| a.name.as_str()).collect::<Vec<_>>().join(", "), last.name),
    }
}

fn place_words(l: Layout, n: usize) -> &'static str {
    match l {
        Layout::Side if n >= 4 => "in a grid",
        Layout::Side => "side by side",
        Layout::Grid => "in a grid",
        Layout::Left => "on the left",
        Layout::Right => "on the right",
        Layout::Top => "on top",
        Layout::Bottom => "at the bottom",
        Layout::Full => "full screen",
    }
}

/// The row's title ("Open Notepad and Weather side by side") and the steps in short.
fn describe(plan: &[Step]) -> (String, Vec<String>) {
    let mut parts: Vec<String> = Vec::new();
    let mut short: Vec<String> = Vec::new();
    let mut i = 0;
    while i < plan.len() {
        let step = &plan[i];
        if let Step::Open(_) = step {
            let mut opened = Vec::new();
            while let Some(Step::Open(a)) = plan.get(i) {
                opened.push(a);
                short.push(format!("Open {}", a.name));
                i += 1;
            }
            // Opened, then laid out together: one phrase.
            if let Some(Step::Arrange { layout, apps }) = plan.get(i) {
                if apps.iter().all(|a| opened.contains(&a)) {
                    parts.push(format!("Open {} {}", names(&opened), place_words(*layout, apps.len())));
                    short.push(format!("Snap {}", place_words(*layout, apps.len())));
                    i += 1;
                    continue;
                }
            }
            parts.push(format!("Open {}", names(&opened)));
            continue;
        }
        let (p, s) = match step {
            Step::Open(_) => unreachable!(),
            Step::Focus(a) => (format!("Switch to {}", a.name), format!("Switch to {}", a.name)),
            Step::Close(a) => (format!("Close {}", a.name), format!("Close {}", a.name)),
            Step::Minimize(a) => (format!("Minimize {}", a.name), format!("Minimize {}", a.name)),
            Step::Maximize(a) => (format!("Maximize {}", a.name), format!("Maximize {}", a.name)),
            Step::Arrange { layout, apps } => {
                let refs: Vec<&App> = apps.iter().collect();
                let t = format!("Snap {} {}", names(&refs), place_words(*layout, apps.len()));
                (t.clone(), t)
            }
            Step::ShowDesktop => ("Show the desktop".into(), "Show the desktop".into()),
            Step::Media(m) => {
                let t = match m {
                    Media::PlayPause => "Play or pause",
                    Media::Next => "Next track",
                    Media::Previous => "Previous track",
                    Media::Stop => "Stop playback",
                };
                (t.into(), t.into())
            }
            Step::Quick { title, .. } | Step::Uri { title, .. } | Step::Builtin { title, .. } => (title.clone(), title.clone()),
            Step::CloseAll { keep } => {
                let refs: Vec<&App> = keep.iter().collect();
                let t = if keep.is_empty() { "Close all windows".to_string() } else { format!("Close all windows except {}", names(&refs)) };
                (t.clone(), t)
            }
        };
        parts.push(p);
        short.push(s);
        i += 1;
    }
    let title = parts
        .iter()
        .enumerate()
        .map(|(i, p)| if i == 0 { p.clone() } else { lower_first(p) })
        .collect::<Vec<_>>()
        .join(", then ");
    (title, short)
}

/// The plan in the one-line form the language model writes (see ml/intent/plan.py):
/// "open notepad ; open weather ; snap side".
pub fn to_line(plan: &[Step]) -> String {
    let n = |a: &App| a.name.to_lowercase();
    let mut since: Vec<&App> = Vec::new(); // named since the previous snap
    let mut out: Vec<String> = Vec::new();
    for step in plan {
        let s = match step {
            Step::Open(a) | Step::Focus(a) | Step::Close(a) | Step::Minimize(a) | Step::Maximize(a) => {
                since.push(a);
                let verb = match step {
                    Step::Open(_) => "open",
                    Step::Focus(_) => "focus",
                    Step::Close(_) => "close",
                    Step::Minimize(_) => "min",
                    _ => "max",
                };
                format!("{verb} {}", n(a))
            }
            Step::Arrange { layout, apps } => {
                let l = format!("{layout:?}").to_lowercase();
                let mut named: Vec<&App> = Vec::new();
                for a in since.drain(..) {
                    if !named.contains(&a) {
                        named.push(a);
                    }
                }
                if *layout == Layout::Full && apps.len() == 1 {
                    format!("max {}", n(&apps[0]))
                } else if named.len() == apps.len() && named.iter().zip(apps).all(|(a, b)| *a == b) {
                    format!("snap {l}")
                } else {
                    format!("snap {l} {}", apps.iter().map(n).collect::<Vec<_>>().join(", "))
                }
            }
            Step::ShowDesktop => "desktop".into(),
            Step::Media(m) => format!("media {}", match m {
                Media::PlayPause => "play",
                Media::Next => "next",
                Media::Previous => "prev",
                Media::Stop => "stop",
            }),
            Step::Quick { id, .. } => {
                let (what, value) = id.split_once(':').unwrap_or((id, ""));
                format!("set {what} {value}")
            }
            Step::Uri { uri, title } if uri.starts_with("ms-settings:") => {
                format!("settings {}", title.trim_start_matches("Open ").to_lowercase())
            }
            Step::Uri { uri, .. } if uri == "ms-screenclip:" => "shot".into(),
            Step::Uri { uri, .. } => format!("url {uri}"),
            Step::Builtin { id, .. } => id.clone(),
            Step::CloseAll { keep } if keep.is_empty() => "closeall".into(),
            Step::CloseAll { keep } => format!("closeall except {}", keep.iter().map(n).collect::<Vec<_>>().join(", ")),
        };
        out.push(s);
    }
    out.join(" ; ")
}

/// "Turn dark mode on" after a comma; app names keep their capitals ("Open Notepad").
fn lower_first(s: &str) -> String {
    let mut c = s.chars();
    match c.next() {
        Some(f) => f.to_lowercase().chain(c).collect(),
        None => String::new(),
    }
}

// ---------------------------------------------------------------- carrying it out

/// Run `plan` in the background: opening apps and waiting for their windows takes seconds.
pub fn run(plan: &[Step]) {
    let plan = plan.to_vec();
    std::thread::spawn(move || {
        let before: HashSet<isize> = wins::list().iter().map(|w| w.hwnd).collect();
        let mut launched: Vec<App> = Vec::new();
        let mut claimed: HashSet<isize> = HashSet::new();
        for step in &plan {
            match step {
                Step::Open(a) => {
                    crate::actions::shell_open(&a.target);
                    launched.push(a.clone());
                }
                Step::Arrange { layout, apps } => {
                    let hw: Vec<isize> = apps
                        .iter()
                        .filter_map(|a| window_for(a, &mut launched, &before, &mut claimed))
                        .collect();
                    arrange(*layout, &hw);
                }
                Step::Focus(a) => match window_for(a, &mut launched, &before, &mut claimed) {
                    Some(h) => wins::focus(h),
                    None => crate::actions::shell_open(&a.target),
                },
                Step::Close(a) => each_window(a, |h| close_window(h.0 as isize)),
                Step::Minimize(a) => each_window(a, |h| unsafe {
                    use windows::Win32::UI::WindowsAndMessaging::{ShowWindow, SW_MINIMIZE};
                    let _ = ShowWindow(h, SW_MINIMIZE);
                }),
                Step::Maximize(a) => {
                    if let Some(h) = window_for(a, &mut launched, &before, &mut claimed) {
                        arrange(Layout::Full, &[h]);
                    }
                }
                Step::ShowDesktop => show_desktop(),
                Step::Media(m) => media_key(*m),
                Step::Quick { id, .. } => {
                    crate::quick::run(id);
                }
                Step::Uri { uri, .. } => crate::actions::shell_open(uri),
                Step::CloseAll { keep } => {
                    for w in wins::list() {
                        if !keep.iter().any(|a| belongs(&w, a)) {
                            close_window(w.hwnd);
                        }
                    }
                }
                Step::Builtin { id, title } => {
                    let hit = Hit { title: title.clone(), subtitle: String::new(), kind: Kind::Command, action: Action::Builtin(id.clone()), score: 0 };
                    crate::actions::execute(&hit, &|_| {});
                }
            }
        }
    });
}

/// Ask a window to close, as its close button does: it may still ask to save first.
fn close_window(hwnd: isize) {
    use windows::Win32::Foundation::{HWND, LPARAM, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{PostMessageW, WM_CLOSE};
    unsafe {
        let _ = PostMessageW(Some(HWND(hwnd as *mut _)), WM_CLOSE, WPARAM(0), LPARAM(0));
    }
}

fn file_name(p: &str) -> &str {
    p.rsplit(['\\', '/']).next().unwrap_or(p)
}

/// Is `w` a window of `a`?
fn belongs(w: &OpenWindow, a: &App) -> bool {
    let t = a.target.to_lowercase();
    let icon = w.icon.to_lowercase();
    if let Some(id) = t.strip_prefix("shell:appsfolder\\") {
        if icon.strip_prefix("shell:appsfolder\\") == Some(id) {
            return true;
        }
        // A desktop app listed by its exe ("{guid}\notepad++\notepad++.exe").
        if id.ends_with(".exe") && file_name(id) == file_name(&icon) {
            return true;
        }
    } else if t.ends_with(".exe") && file_name(&t) == file_name(&icon) {
        return true;
    }
    let n = a.name.to_lowercase();
    let app = w.app.to_lowercase();
    (!app.is_empty() && (app == n || app.contains(&n) || (app.len() >= 4 && n.contains(&app))))
        || w.title.to_lowercase().ends_with(&format!(" - {n}"))
}

fn each_window(a: &App, f: impl Fn(windows::Win32::Foundation::HWND)) {
    for w in wins::list().into_iter().filter(|w| belongs(w, a)) {
        f(windows::Win32::Foundation::HWND(w.hwnd as *mut _));
    }
}

/// `a`'s window: the new one if it was just opened, else one already open (opening the app if
/// it has none). Waits a few seconds for an app to start.
fn window_for(a: &App, launched: &mut Vec<App>, before: &HashSet<isize>, claimed: &mut HashSet<isize>) -> Option<isize> {
    let mut opened = launched.contains(a);
    let mut start = Instant::now();
    loop {
        let ws = wins::list();
        let free = |w: &&OpenWindow| !claimed.contains(&w.hwnd) && belongs(w, a);
        let fresh = ws.iter().filter(free).find(|w| !before.contains(&w.hwnd));
        // Apps that keep one window (Settings, Store apps) bring back the one they had.
        let any = || ws.iter().find(free);
        let pick = fresh.or_else(|| if !opened || start.elapsed() > Duration::from_millis(2500) { any() } else { None });
        if let Some(w) = pick {
            claimed.insert(w.hwnd);
            return Some(w.hwnd);
        }
        if !opened {
            crate::actions::shell_open(&a.target);
            launched.push(a.clone());
            opened = true;
            start = Instant::now();
        } else if start.elapsed() > Duration::from_secs(10) {
            return None;
        }
        std::thread::sleep(Duration::from_millis(100));
    }
}

type Rect = (i32, i32, i32, i32); // x, y, w, h

/// Where each of `n` windows goes in the work area `r`.
fn layout_rects(l: Layout, n: usize, r: Rect) -> Vec<Rect> {
    let (x, y, w, h) = r;
    let cols = |k: usize| -> Vec<Rect> {
        let k = k.max(1) as i32;
        (0..k).map(|i| (x + w * i / k, y, w * (i + 1) / k - w * i / k, h)).collect()
    };
    let (hw, hh) = (w / 2, h / 2);
    match l {
        Layout::Left => vec![(x, y, hw, h)],
        Layout::Right => vec![(x + hw, y, w - hw, h)],
        Layout::Top => vec![(x, y, w, hh)],
        Layout::Bottom => vec![(x, y + hh, w, h - hh)],
        Layout::Full => vec![r],
        Layout::Side if n < 4 => cols(n),
        Layout::Side | Layout::Grid => match n {
            0 | 1 => vec![r],
            2 => cols(2),
            // One on the left, two stacked on the right, as Snap layouts offer.
            3 => vec![(x, y, hw, h), (x + hw, y, w - hw, hh), (x + hw, y + hh, w - hw, h - hh)],
            4 => vec![(x, y, hw, hh), (x + hw, y, w - hw, hh), (x, y + hh, hw, h - hh), (x + hw, y + hh, w - hw, h - hh)],
            _ => cols(n),
        },
    }
}

fn work_area() -> Rect {
    use windows::Win32::Foundation::POINT;
    use windows::Win32::Graphics::Gdi::{GetMonitorInfoW, MonitorFromPoint, MONITORINFO, MONITOR_DEFAULTTONEAREST};
    use windows::Win32::UI::WindowsAndMessaging::GetCursorPos;
    unsafe {
        let mut p = POINT::default();
        let _ = GetCursorPos(&mut p);
        let m = MonitorFromPoint(p, MONITOR_DEFAULTTONEAREST);
        let mut mi = MONITORINFO { cbSize: std::mem::size_of::<MONITORINFO>() as u32, ..Default::default() };
        let _ = GetMonitorInfoW(m, &mut mi);
        let r = mi.rcWork;
        (r.left, r.top, r.right - r.left, r.bottom - r.top)
    }
}

/// Snap `hwnds` into `l`. Applied again a moment later: a starting app often restores its own
/// size and place after its window first appears.
fn arrange(l: Layout, hwnds: &[isize]) {
    if hwnds.is_empty() {
        return;
    }
    let rects = layout_rects(l, hwnds.len(), work_area());
    for pass in 0..3 {
        if pass > 0 {
            std::thread::sleep(Duration::from_millis(if pass == 1 { 400 } else { 900 }));
        }
        for (h, r) in hwnds.iter().zip(&rects) {
            place(*h, *r, l == Layout::Full);
        }
    }
    if let Some(&last) = hwnds.last() {
        for &h in hwnds {
            wins::focus(h);
        }
        wins::focus(last);
    }
}

fn place(hwnd: isize, r: Rect, maximize: bool) {
    use windows::Win32::Foundation::{HWND, RECT};
    use windows::Win32::Graphics::Dwm::{DwmGetWindowAttribute, DWMWA_EXTENDED_FRAME_BOUNDS};
    use windows::Win32::UI::WindowsAndMessaging::{
        GetWindowRect, IsIconic, IsWindow, IsZoomed, SetWindowPos, ShowWindow, SWP_NOACTIVATE, SWP_NOZORDER, SW_MAXIMIZE, SW_RESTORE,
    };
    let h = HWND(hwnd as *mut _);
    unsafe {
        if !IsWindow(Some(h)).as_bool() {
            return;
        }
        if maximize {
            let _ = ShowWindow(h, SW_MAXIMIZE);
            return;
        }
        if IsZoomed(h).as_bool() || IsIconic(h).as_bool() {
            let _ = ShowWindow(h, SW_RESTORE);
        }
        // Windows 10+ frames have invisible resize borders: size the visible frame, not the window.
        let mut outer = RECT::default();
        let mut vis = RECT::default();
        let _ = GetWindowRect(h, &mut outer);
        let ok = DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, &mut vis as *mut _ as *mut _, std::mem::size_of::<RECT>() as u32).is_ok();
        let (l, t, rr, b) = if ok {
            (vis.left - outer.left, vis.top - outer.top, outer.right - vis.right, outer.bottom - vis.bottom)
        } else {
            (0, 0, 0, 0)
        };
        let (x, y, w, hh) = r;
        let _ = SetWindowPos(h, None, x - l, y - t, w + l + rr, hh + t + b, SWP_NOZORDER | SWP_NOACTIVATE);
    }
}

fn show_desktop() {
    use windows::core::w;
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, PostMessageW, WM_COMMAND};
    // The taskbar's own "minimize all" command (what Win+M does).
    const MIN_ALL: usize = 419;
    unsafe {
        if let Ok(tray) = FindWindowW(w!("Shell_TrayWnd"), None) {
            let _ = PostMessageW(Some(tray), WM_COMMAND, WPARAM(MIN_ALL), LPARAM(0));
        }
    }
}

fn media_key(m: Media) {
    use windows::Win32::UI::Input::KeyboardAndMouse::{
        keybd_event, KEYEVENTF_EXTENDEDKEY, KEYEVENTF_KEYUP, VK_MEDIA_NEXT_TRACK, VK_MEDIA_PLAY_PAUSE, VK_MEDIA_PREV_TRACK, VK_MEDIA_STOP,
    };
    let vk = match m {
        Media::PlayPause => VK_MEDIA_PLAY_PAUSE,
        Media::Next => VK_MEDIA_NEXT_TRACK,
        Media::Previous => VK_MEDIA_PREV_TRACK,
        Media::Stop => VK_MEDIA_STOP,
    };
    unsafe {
        keybd_event(vk.0 as u8, 0, KEYEVENTF_EXTENDEDKEY, 0);
        keybd_event(vk.0 as u8, 0, KEYEVENTF_EXTENDEDKEY | KEYEVENTF_KEYUP, 0);
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn app(n: &str, t: &str) -> Item {
        Item::new(n, Hit { title: n.into(), subtitle: String::new(), kind: Kind::App, action: Action::Open(t.into()), score: 0 }, None)
    }

    fn items() -> Vec<Item> {
        vec![
            app("Notepad", "shell:AppsFolder\\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"),
            app("Weather", "shell:AppsFolder\\Microsoft.BingWeather_8wekyb3d8bbwe!App"),
            app("Google Chrome", "shell:AppsFolder\\Chrome"),
            app("Paint", "shell:AppsFolder\\Microsoft.Paint_8wekyb3d8bbwe!App"),
            app("Spotify", "shell:AppsFolder\\SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify"),
        ]
    }

    fn title(q: &str) -> Option<String> {
        hits(q, &items()).first().map(|h| h.title.clone())
    }

    #[test]
    fn reads_requests() {
        assert_eq!(title("open notepad and weather").as_deref(), Some("Open Notepad and Weather"));
        assert_eq!(title("open notepad and weather snapped").as_deref(), Some("Open Notepad and Weather side by side"));
        assert_eq!(title("Open Notepad, Weather and Paint side by side").as_deref(), Some("Open Notepad, Weather and Paint side by side"));
        assert_eq!(title("show desktop").as_deref(), Some("Show the desktop"));
        assert_eq!(title("close chrome").as_deref(), Some("Close Google Chrome"));
        assert_eq!(title("snap notepad left").as_deref(), Some("Snap Notepad on the left"));
        assert_eq!(title("open spotify and play").as_deref(), Some("Open Spotify, then play or pause"));
        assert_eq!(title("next song").as_deref(), Some("Next track"));
        assert_eq!(title("open notepad on the left and paint on the right").as_deref(), Some("Open Notepad on the left, then open Paint on the right"));
    }

    #[test]
    fn leaves_searches_alone() {
        assert_eq!(title("open notepad"), None);
        assert_eq!(title("notepad"), None);
        assert_eq!(title("play"), None);
        assert_eq!(title("lock"), None);
        assert_eq!(title("open notepad and"), None);
        assert_eq!(title("open notepad and xyzzy"), None);
        assert_eq!(title("budget report"), None);
    }

    #[test]
    fn grids() {
        let r = layout_rects(Layout::Side, 2, (0, 0, 100, 50));
        assert_eq!(r, vec![(0, 0, 50, 50), (50, 0, 50, 50)]);
        assert_eq!(layout_rects(Layout::Side, 4, (0, 0, 100, 50)).len(), 4);
    }
}
