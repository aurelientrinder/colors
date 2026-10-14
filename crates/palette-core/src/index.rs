use crate::model::Item;

/// One path in the arena. 12 bytes; a million files is ~12 MB plus the text.
#[derive(Clone, Copy)]
pub struct Rec {
    start: u32,
    len: u16,
    name_off: u16,
    pub dir: bool,
}

/// Every file/folder path packed into one string arena. Cache-friendly and
/// allocation-free to scan, which is what keeps keystroke latency flat.
#[derive(Default)]
pub struct FileIndex {
    blob: String,
    recs: Vec<Rec>,
}

impl FileIndex {
    pub fn len(&self) -> usize {
        self.recs.len()
    }

    pub fn push(&mut self, path: &str, dir: bool) {
        if path.len() > u16::MAX as usize || self.blob.len() + path.len() >= u32::MAX as usize {
            return;
        }
        let name_off = path.rfind('\\').map(|i| i + 1).unwrap_or(0) as u16;
        self.recs.push(Rec { start: self.blob.len() as u32, len: path.len() as u16, name_off, dir });
        self.blob.push_str(path);
    }

    /// Parse a snapshot: one path per line, directories end in `\`.
    pub fn push_snapshot(&mut self, text: &str) {
        for line in text.lines() {
            if line.is_empty() {
                continue;
            }
            match line.strip_suffix('\\') {
                Some(p) => self.push(p, true),
                None => self.push(line, false),
            }
        }
    }

    #[inline]
    pub fn path(&self, i: usize) -> &str {
        let r = self.recs[i];
        &self.blob[r.start as usize..r.start as usize + r.len as usize]
    }

    #[inline]
    pub fn name(&self, i: usize) -> &str {
        let r = self.recs[i];
        let s = r.start as usize;
        &self.blob[s + r.name_off as usize..s + r.len as usize]
    }

    #[inline]
    pub fn is_dir(&self, i: usize) -> bool {
        self.recs[i].dir
    }
}

/// Everything searchable. Swapped atomically behind an RwLock.
#[derive(Default)]
pub struct Catalog {
    pub items: Vec<Item>,
    pub files: FileIndex,
    /// Bumped on every mutation so search can invalidate its narrowing cache.
    pub version: u64,
    /// Bumped only when `items` change (files change far more often), so the semantic tier is
    /// sent the items again only then.
    pub items_version: u64,
}
