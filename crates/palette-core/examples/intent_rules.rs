//! The rules' plans for the test requests, to score them as the model is scored:
//!
//!     cargo run -p palette-core --example intent_rules > rules.jsonl
//!     python ml/intent/score.py rules.jsonl
//!
//! The catalog is ml/intent/apps.json, so both are judged on the same apps.

use palette_core::intent;
use palette_core::model::{Action, Hit, Item, Kind};
use serde_json::Value;

fn item(name: &str, kind: Kind, target: String) -> Item {
    Item {
        name: name.into(),
        hit: Hit { title: name.into(), subtitle: String::new(), kind, action: Action::Open(target), score: 0 },
        keywords: None,
    }
}

fn main() {
    let dir = concat!(env!("CARGO_MANIFEST_DIR"), "/../../ml/intent/");
    let cat: Value = serde_json::from_str(&std::fs::read_to_string(format!("{dir}apps.json")).unwrap()).unwrap();
    let mut items = Vec::new();
    for key in ["apps", "holdout_apps"] {
        for g in cat[key].as_array().unwrap() {
            let name = g[0].as_str().unwrap();
            items.push(item(name, Kind::App, format!("shell:AppsFolder\\{name}")));
        }
    }
    for g in cat["settings"].as_array().unwrap() {
        let name = g[0].as_str().unwrap();
        items.push(item(name, Kind::Setting, format!("ms-settings:{}", name.to_lowercase().replace(' ', "-"))));
    }
    for line in std::fs::read_to_string(format!("{dir}test.jsonl")).unwrap().lines().filter(|l| !l.trim().is_empty()) {
        let t: Value = serde_json::from_str(line).unwrap();
        let q = t["q"].as_str().unwrap();
        let plan = intent::read(q, &items).map(|(p, _)| intent::to_line(&p)).unwrap_or_else(|| "none".into());
        println!("{}", serde_json::json!({ "q": q, "plan": plan }));
    }
}
