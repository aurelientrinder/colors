//! Text extraction for the document types the semantic tier indexes, and the splitting of
//! that text into chunks that each get their own vector.

use std::io::Read;
use std::path::Path;

pub const DOC_EXTS: &[&str] = &["txt", "md", "markdown", "rst", "tex", "docx", "pptx", "pdf"];
pub const MAX_FILE_BYTES: u64 = 15 * 1024 * 1024;

/// How much of a document's text is kept before chunking.
const MAX_CHARS: usize = 150_000;
/// How much of a plain-text file is read.
const PLAIN_BYTES: usize = 512 * 1024;
/// Pages read from a PDF / slides from a deck.
const MAX_PAGES: usize = 60;
const MAX_SLIDES: usize = 80;
/// PDFs above this are almost always scans or image-only decks; not worth the CPU.
const MAX_PDF_BYTES: u64 = 40 * 1024 * 1024;
const PDF_TIMEOUT_SECS: u64 = 15;

pub fn is_doc_ext(ext: &str) -> bool {
    DOC_EXTS.iter().any(|e| ext.eq_ignore_ascii_case(e))
}

pub fn extract(path: &Path) -> Option<String> {
    let ext = path.extension()?.to_str()?.to_ascii_lowercase();
    let raw = match ext.as_str() {
        "docx" => office(path, &["word/document.xml"])?,
        "pptx" => {
            let names: Vec<String> = (1..=MAX_SLIDES).map(|n| format!("ppt/slides/slide{n}.xml")).collect();
            let refs: Vec<&str> = names.iter().map(String::as_str).collect();
            office(path, &refs)?
        }
        "pdf" => pdf(path)?,
        _ => plain(path)?,
    };
    let text = collapse(&raw, MAX_CHARS);
    (text.chars().filter(|c| c.is_alphanumeric()).count() >= 20).then_some(text)
}

fn plain(path: &Path) -> Option<String> {
    let mut buf = Vec::new();
    std::fs::File::open(path).ok()?.take(PLAIN_BYTES as u64).read_to_end(&mut buf).ok()?;
    // UTF-16 with BOM (Notepad's "Unicode" saves).
    if buf.starts_with(&[0xFF, 0xFE]) || buf.starts_with(&[0xFE, 0xFF]) {
        let le = buf[0] == 0xFF;
        let units: Vec<u16> = buf[2..]
            .chunks_exact(2)
            .map(|c| if le { u16::from_le_bytes([c[0], c[1]]) } else { u16::from_be_bytes([c[0], c[1]]) })
            .collect();
        return Some(String::from_utf16_lossy(&units));
    }
    if buf.iter().take(4096).any(|&b| b == 0) {
        return None; // binary
    }
    Some(String::from_utf8_lossy(&buf).into_owned())
}

fn office(path: &Path, parts: &[&str]) -> Option<String> {
    let mut zip = zip::ZipArchive::new(std::fs::File::open(path).ok()?).ok()?;
    let mut out = String::new();
    for part in parts {
        let Ok(f) = zip.by_name(part) else { continue };
        let mut xml = String::new();
        f.take(8 * 1024 * 1024).read_to_string(&mut xml).ok()?;
        strip_xml(&xml, &mut out);
        out.push(' ');
        if out.len() > MAX_CHARS * 2 {
            break;
        }
    }
    (!out.trim().is_empty()).then_some(out)
}

fn strip_xml(xml: &str, out: &mut String) {
    let mut in_tag = false;
    let mut tag = String::new();
    let mut text = String::new();
    for c in xml.chars() {
        match c {
            '<' => {
                in_tag = true;
                tag.clear();
            }
            '>' if in_tag => {
                in_tag = false;
                if tag.starts_with("/w:p") || tag.starts_with("/a:p") || tag.starts_with("w:br") || tag.starts_with("w:tab") {
                    text.push(' ');
                }
            }
            _ if in_tag => {
                if tag.len() < 16 {
                    tag.push(c);
                }
            }
            _ => text.push(c),
        }
    }
    out.push_str(&text.replace("&amp;", "&").replace("&lt;", "<").replace("&gt;", ">").replace("&quot;", "\"").replace("&apos;", "'"));
}

/// PDF parsing runs in a child process: some files make the parser spin or crash, and a
/// child can simply be killed. Anything that takes longer than the timeout is skipped.
fn pdf(path: &Path) -> Option<String> {
    use std::os::windows::process::CommandExt;
    use std::process::{Command, Stdio};
    use std::time::{Duration, Instant};

    if std::fs::metadata(path).ok()?.len() > MAX_PDF_BYTES {
        return None;
    }
    let mut child = Command::new(std::env::current_exe().ok()?)
        .arg("extract-pdf")
        .arg(path)
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::null())
        .creation_flags(0x0800_0000) // CREATE_NO_WINDOW
        .spawn()
        .ok()?;
    let mut stdout = child.stdout.take()?;
    // Drain on a thread: a full pipe would otherwise block the child before it can exit.
    let reader = std::thread::spawn(move || {
        let mut s = String::new();
        let _ = stdout.read_to_string(&mut s);
        s
    });

    let start = Instant::now();
    let finished = loop {
        match child.try_wait() {
            Ok(Some(status)) => break status.success(),
            Ok(None) if start.elapsed() > Duration::from_secs(PDF_TIMEOUT_SECS) => {
                let _ = child.kill();
                let _ = child.wait();
                break false;
            }
            Ok(None) => std::thread::sleep(Duration::from_millis(10)),
            Err(_) => break false,
        }
    };
    let text = reader.join().ok()?;
    (finished && !text.is_empty()).then_some(text)
}

fn enough_text(s: &str) -> bool {
    s.chars().filter(|c| c.is_alphanumeric()).take(300).count() >= 300
}

/// The actual parse, run by `palette-semantic extract-pdf <file>`.
///
/// Two readers: lopdf is quick and covers most files; some PDFs (Type0 fonts without a
/// usable encoding, odd xref layouts) come back nearly empty from it, and pdf-extract's
/// font handling reads those. Only the first `MAX_PAGES` pages are used.
pub fn pdf_in_process(path: &Path) -> Option<String> {
    let mut text = String::new();
    if let Ok(doc) = lopdf::Document::load(path) {
        for (n, _) in doc.get_pages().into_iter().take(MAX_PAGES) {
            // Page by page: one undecodable page must not discard the rest.
            if let Ok(t) = doc.extract_text(&[n]) {
                text.push_str(&t);
                text.push('\n');
            }
            if text.len() > MAX_CHARS * 2 {
                break;
            }
        }
    }
    if !enough_text(&text) {
        if let Ok(pages) = pdf_extract::extract_text_by_pages(path) {
            let mut alt = String::new();
            for p in pages.into_iter().take(MAX_PAGES) {
                alt.push_str(&p);
                alt.push('\n');
                if alt.len() > MAX_CHARS * 2 {
                    break;
                }
            }
            if alt.chars().filter(|c| c.is_alphanumeric()).count() > text.chars().filter(|c| c.is_alphanumeric()).count() {
                text = alt;
            }
        }
    }
    (!text.trim().is_empty()).then_some(text)
}

fn collapse(s: &str, max_chars: usize) -> String {
    let mut out = String::with_capacity(max_chars.min(s.len()));
    let mut chars = 0;
    for w in s.split_whitespace() {
        if chars > 0 {
            out.push(' ');
            chars += 1;
        }
        for c in w.chars() {
            if c == '\u{FFFD}' || c.is_control() {
                continue;
            }
            out.push(c);
            chars += 1;
        }
        if chars >= max_chars {
            break;
        }
    }
    out
}

// ---------------------------------------------------------------------------- chunking

/// Characters per chunk (about 200 tokens) and how far consecutive chunks overlap.
const CHUNK_CHARS: usize = 900;
const CHUNK_OVERLAP: usize = 120;
/// The first few chunks are always kept (title, abstract, introduction).
const LEAD_CHUNKS: usize = 4;

/// Split whitespace-collapsed text into overlapping windows that end at a sentence or word
/// boundary. When there are more than `max_chunks`, keep the first `LEAD_CHUNKS` and sample
/// the rest evenly, so a long document is represented along its whole length.
pub fn chunk(text: &str, max_chunks: usize) -> Vec<String> {
    let max_chunks = max_chunks.max(1);
    let mut windows: Vec<&str> = Vec::new();
    let mut start = 0usize;
    while start < text.len() {
        let mut end = (start + CHUNK_CHARS).min(text.len());
        while !text.is_char_boundary(end) {
            end -= 1;
        }
        if end < text.len() {
            // Prefer a sentence end, then a space, in the last quarter of the window.
            let floor = (start + CHUNK_CHARS * 3 / 4).min(end);
            let mut floor_b = floor;
            while !text.is_char_boundary(floor_b) {
                floor_b += 1;
            }
            let tail = &text[floor_b..end];
            if let Some(p) = tail.rfind(". ").or_else(|| tail.rfind("? ")).or_else(|| tail.rfind("! ")) {
                end = floor_b + p + 1;
            } else if let Some(p) = tail.rfind(' ') {
                end = floor_b + p;
            }
        }
        let piece = text[start..end].trim();
        // A tiny trailing fragment adds nothing; a leading one is the whole document.
        if piece.chars().filter(|c| c.is_alphanumeric()).count() >= 20 || windows.is_empty() {
            windows.push(piece);
        }
        if end >= text.len() {
            break;
        }
        let mut next = end.saturating_sub(CHUNK_OVERLAP).max(start + 1);
        while !text.is_char_boundary(next) {
            next += 1;
        }
        // Begin on a word boundary.
        if let Some(p) = text[next..end].find(' ') {
            next += p + 1;
        }
        start = next.max(start + 1);
    }

    if windows.len() <= max_chunks {
        return windows.into_iter().map(str::to_owned).collect();
    }
    let lead = LEAD_CHUNKS.min(max_chunks);
    let rest = &windows[lead..];
    let take = max_chunks - lead;
    let mut out: Vec<String> = windows[..lead].iter().map(|s| s.to_string()).collect();
    for i in 0..take {
        out.push(rest[i * rest.len() / take].to_string());
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn short_text_is_one_chunk() {
        let c = chunk("A short note about the weekly budget meeting and what we decided there.", 8);
        assert_eq!(c.len(), 1);
    }

    #[test]
    fn long_text_is_capped_and_covers_the_end() {
        let sentence = "The turbine blade is cooled by air bled from the compressor stage. ";
        let text: String = (0..400).map(|i| format!("{sentence}Section {i} ends here. ")).collect();
        let c = chunk(&text, 12);
        assert_eq!(c.len(), 12);
        assert!(c.iter().all(|x| x.len() <= CHUNK_CHARS + 4));
        assert!(c[11].contains("Section"), "tail sample present");
        // the last sample comes from the final eighth of the document
        let last_pos = text.find(&c[11][..40]).unwrap();
        assert!(last_pos > text.len() * 7 / 8);
    }

    #[test]
    fn multibyte_text_does_not_panic() {
        let text = "Résumé des modèles thermodynamiques à très haute température. ".repeat(80);
        let c = chunk(&text, 6);
        assert!(!c.is_empty() && c.len() <= 6);
    }
}
