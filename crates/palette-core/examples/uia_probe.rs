//! Reads a window the way the palette does when it opens: `uia_probe <part of its title> [query]`
//! prints the selected text, how long the scan took, the commands found and the rows a query gets.

use palette_core::uia;
use std::time::{Duration, Instant};

fn main() {
    let mut args = std::env::args().skip(1);
    let title = args.next().unwrap_or_default().to_lowercase();
    let query = args.next();
    let Some(w) = palette_core::wins::list().into_iter().find(|w| w.title.to_lowercase().contains(&title) || w.app.to_lowercase().contains(&title)) else {
        eprintln!("no open window matches {title:?}");
        return;
    };
    println!("window: {} ({})", w.title, w.app);
    let t = Instant::now();
    uia::begin(w.hwnd);
    println!("selection: {:?}  [{:?}]", uia::selection(Duration::from_secs(5)), t.elapsed());
    let cmds = uia::commands(Duration::from_secs(30));
    println!("{} commands  [{:?}]", cmds.len(), t.elapsed());
    for (name, group) in cmds.iter().take(400) {
        println!("  {name:<40} {group}");
    }
    if let Some(q) = query {
        let lq = q.to_lowercase();
        let words: Vec<&str> = lq.split_whitespace().collect();
        for h in uia::search(&lq, &words, None) {
            println!("row: {} | {} | score {}", h.title, h.subtitle, h.score);
        }
    }
}
