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
    /// What was picked for what was typed: lowercase query prefix -> (hit key -> times picked).
    #[serde(default)]
    picks: HashMap<String, HashMap<String, u32>>,
}

/// Longest query prefix remembered. Longer queries are rarely typed twice, and every shorter
/// prefix of one is stored anyway, so typing `n` finds what `notepad` was picked for.
const PICK_PREFIX: usize = 8;

/// Picks of one query: hit key -> count. See `Usage::picks_for`.
pub type Picks = HashMap<String, u32>;

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

    /// `record`, and remember that `hit` was what you picked for `query`.
    pub fn record_for(&mut self, hit: &Hit, query: &str) {
        self.remember_pick(&hit.key(), query);
        self.record(hit);
    }

    fn remember_pick(&mut self, key: &str, query: &str) {
        let q = query.trim().to_lowercase();
        if q.is_empty() || q.starts_with('>') || q.contains(['\\', '/']) {
            return;
        }
        let mut end = 0;
        for c in q.chars().take(PICK_PREFIX) {
            end += c.len_utf8();
            *self.picks.entry(q[..end].to_string()).or_default().entry(key.to_string()).or_insert(0) += 1;
        }
    }

    /// What was picked for the (lowercase, trimmed) `query`, to rank with `pick_bonus`.
    pub fn picks_for(&self, query: &str) -> Option<&Picks> {
        self.picks.get(query)
    }

    /// Boost for a hit you have picked for this query before: more than frecency alone gives,
    /// so what you always choose for `n` wins over a better-spelled neighbour.
    pub fn pick_bonus(picks: Option<&Picks>, key: &str) -> i32 {
        match picks.and_then(|p| p.get(key)) {
            Some(&n) => (60.0 + 40.0 * (n as f32).ln()).min(200.0) as i32,
            None => 0,
        }
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

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn picks_apply_to_every_prefix_of_what_was_typed() {
        let mut u = Usage::default();
        u.remember_pick("open:notepad", "Note");
        for q in ["n", "no", "not", "note"] {
            assert!(Usage::pick_bonus(u.picks_for(q), "open:notepad") >= 60, "{q}");
            assert_eq!(Usage::pick_bonus(u.picks_for(q), "open:notes"), 0);
        }
        assert!(u.picks_for("notep").is_none());
        u.remember_pick("open:notepad", "n");
        assert!(Usage::pick_bonus(u.picks_for("n"), "open:notepad") > Usage::pick_bonus(u.picks_for("no"), "open:notepad"));
    }

    #[test]
    fn paths_and_shell_commands_are_not_remembered() {
        let mut u = Usage::default();
        u.remember_pick("run:dir", "> dir");
        u.remember_pick("open:x", "C:\\Users");
        u.remember_pick("open:y", "  ");
        assert!(u.picks.is_empty());
    }
}
