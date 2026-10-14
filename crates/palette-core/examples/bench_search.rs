//! Keystroke latency on this machine's real index (%LOCALAPPDATA%\palette):
//!
//!     cargo run -p palette-core --release --example bench_search
//!
//! Types a few queries letter by letter, as the launcher does, and prints the median and worst
//! time per keystroke, then the cost of the parts that do not depend on the file index.

use palette_core::index::Catalog;
use palette_core::model::{Action, Hit, Item, Kind};
use palette_core::search::Searcher;
use palette_core::usage::Usage;
use std::sync::{Arc, Mutex, RwLock};
use std::time::Instant;

fn catalog() -> Catalog {
    let mut c = Catalog::default();
    c.items = palette_core::scan::builtins();
    #[derive(serde::Deserialize)]
    struct Row(String, String);
    if let Ok(b) = std::fs::read(palette_core::paths::data_dir().join("apps.json")) {
        for Row(n, t) in serde_json::from_slice::<Vec<Row>>(&b).unwrap_or_default() {
            let hit = Hit { title: n.clone(), subtitle: "Application".into(), kind: Kind::App, action: Action::Open(t), score: 0 };
            c.items.push(Item::new(n, hit, None));
        }
    }
    c.items.extend(palette_core::settings_index::load());
    for e in std::fs::read_dir(palette_core::paths::index_dir()).into_iter().flatten().flatten() {
        let p = e.path();
        if p.extension().is_some_and(|x| x == "txt") && p.file_stem().is_some_and(|s| s.len() == 1) {
            c.files.push_snapshot(&String::from_utf8_lossy(&std::fs::read(&p).unwrap()));
        }
    }
    c.version = 1;
    c
}

fn stats(name: &str, mut ms: Vec<f64>) {
    ms.sort_by(|a, b| a.partial_cmp(b).unwrap());
    let med = ms[ms.len() / 2];
    let p90 = ms[ms.len() * 9 / 10];
    println!("{name:<28} median {med:6.2} ms   p90 {p90:6.2} ms   worst {:6.2} ms", ms[ms.len() - 1]);
}

fn time(mut f: impl FnMut()) -> f64 {
    let t = Instant::now();
    f();
    t.elapsed().as_secs_f64() * 1e3
}

fn main() {
    let t = Instant::now();
    let cat = catalog();
    println!(
        "catalog: {} items, {} files, loaded in {:.0} ms",
        cat.items.len(),
        cat.files.len(),
        t.elapsed().as_secs_f64() * 1e3
    );
    let cat = Arc::new(RwLock::new(cat));
    let s = Searcher::new(cat.clone(), Arc::new(Mutex::new(Usage::load())));
    palette_core::home::recent_cached();

    let queries = [
        "visual studio code", "notepad", "downloads", "readme", "cat", "mouse size", "wifi", "volume 30",
        "chrome", "settings", "main.rs", "photos", "env var", "open notepad and calculator", "c:\\users\\",
    ];
    // Warm-up: first touches of the arena, lazy statics.
    for q in queries {
        s.search(q);
    }
    let mut first = Vec::new();
    let mut narrowed = Vec::new();
    for _ in 0..5 {
        for q in queries {
            for (n, _) in q.char_indices().skip(1).chain([(q.len(), ' ')]) {
                let typed = &q[..n];
                let ms = time(|| drop(s.search(typed)));
                if typed.chars().count() == 1 { first.push(ms) } else { narrowed.push(ms) }
            }
            s.search(""); // a new search starts from scratch
        }
    }
    stats("keystroke: first letter", first);
    stats("keystroke: later letters", narrowed);

    let items = cat.read().unwrap().items.clone();
    stats("open windows (wins::list)", (0..200).map(|_| time(|| drop(palette_core::wins::list()))).collect());
    stats("quick settings (\"vol\")", (0..50).map(|_| time(|| drop(palette_core::quick::hits("vol")))).collect());
    stats(
        "requests (intent::hits)",
        (0..200).map(|_| time(|| drop(palette_core::intent::hits("open notepad and calc", &items)))).collect(),
    );
    stats("recent files (cached)", (0..200).map(|_| time(|| drop(palette_core::home::recent_cached()))).collect());
}
