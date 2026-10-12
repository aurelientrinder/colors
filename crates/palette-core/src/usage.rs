use crate::model::Hit;
use serde::{Deserialize, Serialize};
use std::collections::HashMap;
use std::time::{SystemTime, UNIX_EPOCH};

#[derive(Clone, Serialize, Deserialize)]
struct Entry {
    count: u32,
    last: u64,
    hit: Hit,
}

/// Frecency: remembers what you launch and boosts it in future rankings.
#[derive(Default, Serialize, Deserialize)]
pub struct Usage {
    map: HashMap<String, Entry>,
}

fn now() -> u64 {
    SystemTime::now().duration_since(UNIX_EPOCH).map(|d| d.as_secs()).unwrap_or(0)
}

impl Usage {
    pub fn load() -> Usage {
        std::fs::read(crate::paths::data_dir().join("usage.json"))
            .ok()
            .and_then(|b| serde_json::from_slice(&b).ok())
            .unwrap_or_default()
    }

    pub fn save(&self) {
        if let Ok(b) = serde_json::to_vec(self) {
            let _ = std::fs::write(crate::paths::data_dir().join("usage.json"), b);
        }
    }

    pub fn record(&mut self, hit: &Hit) {
        let e = self.map.entry(hit.key()).or_insert_with(|| Entry { count: 0, last: 0, hit: hit.clone() });
        e.count += 1;
        e.last = now();
        e.hit = hit.clone();
        self.save();
    }

    pub fn bonus(&self, key: &str) -> i32 {
        let Some(e) = self.map.get(key) else { return 0 };
        Self::score(e)
    }

    fn score(e: &Entry) -> i32 {
        let age = now().saturating_sub(e.last);
        let recency = match age {
            0..=3_600 => 40,
            3_601..=86_400 => 25,
            86_401..=604_800 => 12,
            _ => 0,
        };
        (35.0 * (1.0 + e.count as f32).ln()) as i32 + recency
    }

    /// Most-used hits, for the empty-query view.
    pub fn top(&self, n: usize) -> Vec<Hit> {
        let mut v: Vec<(&Entry, i32)> = self.map.values().map(|e| (e, Self::score(e))).collect();
        v.sort_by(|a, b| b.1.cmp(&a.1));
        v.into_iter().take(n).map(|(e, _)| e.hit.clone()).collect()
    }
}
