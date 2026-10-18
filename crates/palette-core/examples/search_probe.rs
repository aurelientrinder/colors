//! The rows an instant search gives on this machine's real index:
//!
//!     cargo run -p palette-core --release --example search_probe -- settings "dark mode"

use palette_core::index::Catalog;
use palette_core::model::{Action, Hit, Item, Kind};
use palette_core::search::Searcher;
use palette_core::usage::Usage;
use std::sync::{Arc, Mutex, RwLock};

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


fn main() {
    let cat = Arc::new(RwLock::new(catalog()));
    let s = Searcher::new(cat, Arc::new(Mutex::new(Usage::load())));
    for q in std::env::args().skip(1) {
        println!("{q}");
        for h in s.search(&q) {
            println!("  {:>5}  {:<10} {}  |  {}", h.score, h.kind.label(), h.title, h.subtitle);
        }
    }
}
