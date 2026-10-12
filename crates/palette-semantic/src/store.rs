//! Append-only vector store: `[u32 keylen][key][u64 stamp][u64 size][DIM x f32]` records
//! after an 8-byte header. Later records for a key supersede earlier ones; the file is
//! compacted at open when too many records are dead.

use crate::embed::DIM;
use std::collections::HashMap;
use std::fs::File;
use std::io::{BufWriter, Read, Write};
use std::path::{Path, PathBuf};

const MAGIC: &[u8; 4] = b"PSM1";

pub struct Store {
    path: PathBuf,
    keys: Vec<String>,
    meta: Vec<(u64, u64)>,
    vecs: Vec<f32>,
    map: HashMap<String, usize>,
    out: Option<BufWriter<File>>,
}

impl Store {
    pub fn open(path: &Path) -> Store {
        let mut s = Store {
            path: path.to_path_buf(),
            keys: Vec::new(),
            meta: Vec::new(),
            vecs: Vec::new(),
            map: HashMap::new(),
            out: None,
        };
        let mut dead = 0usize;
        if let Ok(mut f) = File::open(path) {
            let mut buf = Vec::new();
            if f.read_to_end(&mut buf).is_ok() {
                dead = s.parse(&buf);
            }
        }
        if dead > s.keys.len() / 4 + 16 {
            s.rewrite();
        }
        s
    }

    /// Returns the number of superseded records. Stops quietly at a torn tail.
    fn parse(&mut self, b: &[u8]) -> usize {
        let header = 8;
        if b.len() < header || &b[..4] != MAGIC || u32::from_le_bytes(b[4..8].try_into().unwrap()) as usize != DIM {
            return 0;
        }
        let mut p = header;
        let mut dead = 0;
        while p + 4 <= b.len() {
            let kl = u32::from_le_bytes(b[p..p + 4].try_into().unwrap()) as usize;
            let end = p + 4 + kl + 16 + DIM * 4;
            if kl > 4096 || end > b.len() {
                break;
            }
            let key = String::from_utf8_lossy(&b[p + 4..p + 4 + kl]).into_owned();
            let q = p + 4 + kl;
            let stamp = u64::from_le_bytes(b[q..q + 8].try_into().unwrap());
            let size = u64::from_le_bytes(b[q + 8..q + 16].try_into().unwrap());
            let v: Vec<f32> = b[q + 16..end]
                .chunks_exact(4)
                .map(|c| f32::from_le_bytes(c.try_into().unwrap()))
                .collect();
            if self.put(&key, stamp, size, &v) {
                dead += 1;
            }
            p = end;
        }
        dead
    }

    /// In-memory insert/replace. Returns true if it replaced an existing key.
    fn put(&mut self, key: &str, stamp: u64, size: u64, v: &[f32]) -> bool {
        match self.map.get(key) {
            Some(&i) => {
                self.meta[i] = (stamp, size);
                self.vecs[i * DIM..(i + 1) * DIM].copy_from_slice(v);
                true
            }
            None => {
                self.map.insert(key.to_string(), self.keys.len());
                self.keys.push(key.to_string());
                self.meta.push((stamp, size));
                self.vecs.extend_from_slice(v);
                false
            }
        }
    }

    fn write_record(w: &mut impl Write, key: &str, stamp: u64, size: u64, v: &[f32]) -> std::io::Result<()> {
        w.write_all(&(key.len() as u32).to_le_bytes())?;
        w.write_all(key.as_bytes())?;
        w.write_all(&stamp.to_le_bytes())?;
        w.write_all(&size.to_le_bytes())?;
        for x in v {
            w.write_all(&x.to_le_bytes())?;
        }
        Ok(())
    }

    /// Rewrite the file from memory (drops dead records).
    fn rewrite(&mut self) {
        self.out = None;
        let tmp = self.path.with_extension("tmp");
        if let Ok(f) = File::create(&tmp) {
            let mut w = BufWriter::new(f);
            let ok = (|| -> std::io::Result<()> {
                w.write_all(MAGIC)?;
                w.write_all(&(DIM as u32).to_le_bytes())?;
                for i in 0..self.keys.len() {
                    let (stamp, size) = self.meta[i];
                    Self::write_record(&mut w, &self.keys[i], stamp, size, &self.vecs[i * DIM..(i + 1) * DIM])?;
                }
                w.flush()
            })()
            .is_ok();
            drop(w);
            if ok {
                let _ = std::fs::rename(&tmp, &self.path);
            }
        }
    }

    fn writer(&mut self) -> Option<&mut BufWriter<File>> {
        if self.out.is_none() {
            let fresh = !self.path.exists() || std::fs::metadata(&self.path).map(|m| m.len() == 0).unwrap_or(true);
            let f = std::fs::OpenOptions::new().create(true).append(true).open(&self.path).ok()?;
            let mut w = BufWriter::new(f);
            if fresh {
                w.write_all(MAGIC).ok()?;
                w.write_all(&(DIM as u32).to_le_bytes()).ok()?;
            }
            self.out = Some(w);
        }
        self.out.as_mut()
    }

    pub fn len(&self) -> usize {
        self.keys.len()
    }

    pub fn stamp(&self, key: &str) -> Option<(u64, u64)> {
        self.map.get(key).map(|&i| self.meta[i])
    }

    pub fn upsert(&mut self, key: &str, stamp: u64, size: u64, v: &[f32]) {
        self.put(key, stamp, size, v);
        if let Some(w) = self.writer() {
            let _ = Self::write_record(w, key, stamp, size, v);
        }
    }

    pub fn flush(&mut self) {
        if let Some(w) = self.out.as_mut() {
            let _ = w.flush();
        }
    }

    /// Drop every entry the predicate rejects and compact the file.
    pub fn retain(&mut self, keep: impl Fn(&str, (u64, u64)) -> bool) {
        if self.keys.iter().zip(&self.meta).all(|(k, m)| keep(k, *m)) {
            return;
        }
        let old_keys = std::mem::take(&mut self.keys);
        let old_meta = std::mem::take(&mut self.meta);
        let old_vecs = std::mem::take(&mut self.vecs);
        self.map.clear();
        for (i, k) in old_keys.iter().enumerate() {
            if keep(k, old_meta[i]) {
                self.put(k, old_meta[i].0, old_meta[i].1, &old_vecs[i * DIM..(i + 1) * DIM]);
            }
        }
        self.rewrite();
    }

    /// Top-`k` by cosine similarity (vectors are unit length, so a dot product).
    pub fn search(&self, q: &[f32], k: usize) -> Vec<(&str, f32)> {
        let mut scored: Vec<(usize, f32)> = (0..self.keys.len())
            .map(|i| {
                let v = &self.vecs[i * DIM..(i + 1) * DIM];
                (i, v.iter().zip(q).map(|(a, b)| a * b).sum::<f32>())
            })
            .collect();
        let k = k.min(scored.len());
        if k == 0 {
            return Vec::new();
        }
        scored.select_nth_unstable_by(k - 1, |a, b| b.1.total_cmp(&a.1));
        scored.truncate(k);
        scored.sort_by(|a, b| b.1.total_cmp(&a.1));
        scored.into_iter().map(|(i, s)| (self.keys[i].as_str(), s)).collect()
    }
}
