//! Ask about a window from the command line, as the palette does:
//!
//!     cargo run -p palette-core --release --example ask_probe -- "<part of the window's title>" "summarise this"
//!
//! Reads the window's text, starts the chat model, and prints the answer as it streams.

use palette_core::{chat, uia};
use std::io::{Read, Write};
use std::net::TcpStream;
use std::time::{Duration, Instant};
use windows::core::BOOL;
use windows::Win32::Foundation::{HWND, LPARAM};
use windows::Win32::UI::WindowsAndMessaging::{EnumWindows, GetWindowTextW, IsWindowVisible};

fn find(part: &str) -> Option<isize> {
    struct S {
        part: String,
        found: Option<isize>,
    }
    unsafe extern "system" fn each(h: HWND, l: LPARAM) -> BOOL {
        let s = &mut *(l.0 as *mut S);
        let mut b = [0u16; 256];
        let n = GetWindowTextW(h, &mut b) as usize;
        let t = String::from_utf16_lossy(&b[..n]);
        if IsWindowVisible(h).as_bool() && t.to_lowercase().contains(&s.part) {
            s.found = Some(h.0 as isize);
            return BOOL(0);
        }
        BOOL(1)
    }
    let mut s = S { part: part.to_lowercase(), found: None };
    unsafe {
        let _ = EnumWindows(Some(each), LPARAM(&mut s as *mut S as isize));
    }
    s.found
}

fn main() {
    let args: Vec<String> = std::env::args().skip(1).collect();
    let (title, question) = (args.first().cloned().unwrap_or_default(), args.get(1).cloned().unwrap_or("summarise this".into()));
    println!("model: {:?}, available: {}", chat::model_path(), chat::available());
    let Some(hwnd) = find(&title) else { return println!("no window with '{title}'") };
    uia::begin(hwnd);
    let t = Instant::now();
    let p = match chat::prepare(&question) {
        Ok(p) => p,
        Err(e) => return println!("error: {e}"),
    };
    println!("prepared in {:.1}s: app {}, from the {}, model {}, request {} bytes", t.elapsed().as_secs_f32(), p.app, p.source, p.model, p.body.len());

    let mut s = TcpStream::connect(("127.0.0.1", p.port)).unwrap();
    s.set_read_timeout(Some(Duration::from_secs(120))).unwrap();
    let req = format!(
        "POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1\r\nContent-Type: application/json\r\nContent-Length: {}\r\nConnection: close\r\n\r\n{}",
        p.body.len(),
        p.body
    );
    s.write_all(req.as_bytes()).unwrap();
    let t = Instant::now();
    let mut first = None;
    let mut buf = Vec::new();
    let mut chunk = [0u8; 4096];
    let mut answer = String::new();
    let mut seen = 0usize;
    loop {
        let n = match s.read(&mut chunk) {
            Ok(0) | Err(_) => break,
            Ok(n) => n,
        };
        buf.extend_from_slice(&chunk[..n]);
        let text = String::from_utf8_lossy(&buf).to_string();
        for part in text.split("data: ").skip(1 + seen) {
            let Some(line) = part.lines().next() else { continue };
            if line.trim() == "[DONE]" {
                continue;
            }
            if let Ok(v) = serde_json::from_str::<serde_json::Value>(line) {
                seen += 1;
                if let Some(c) = v["choices"][0]["delta"]["content"].as_str() {
                    first.get_or_insert(t.elapsed());
                    answer.push_str(c);
                }
            } else {
                break; // an event cut in two: wait for the rest
            }
        }
    }
    println!("first token after {:.1}s, done in {:.1}s\n---\n{answer}", first.unwrap_or_default().as_secs_f32(), t.elapsed().as_secs_f32());
}
