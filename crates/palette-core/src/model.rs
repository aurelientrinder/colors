use serde::{Deserialize, Serialize};
use std::path::PathBuf;

#[derive(Clone, Copy, PartialEq, Eq, Debug, Serialize, Deserialize)]
pub enum Kind {
    App,
    Folder,
    File,
    Setting,
    Command,
    Calc,
    Web,
    /// An open window (the switcher).
    Window,
    /// A quick setting changed in place: dark mode, Wi-Fi, volume, ...
    Toggle,
}

impl Kind {
    pub fn label(self) -> &'static str {
        match self {
            Kind::App => "App",
            Kind::Folder => "Folder",
            Kind::File => "File",
            Kind::Setting => "Setting",
            Kind::Command => "Command",
            Kind::Calc => "Calculator",
            Kind::Web => "Web",
            Kind::Window => "Window",
            Kind::Toggle => "Toggle",
        }
    }
    /// Ranking nudge so apps beat same-scoring files.
    pub fn bonus(self) -> i32 {
        match self {
            // Switching to the copy that is already open beats starting another, even one launched
            // often (app 150 plus up to ~200 of frecency).
            Kind::Window => 360,
            Kind::App => 150,
            Kind::Setting => 60,
            Kind::Command => 40,
            Kind::Folder => 20,
            Kind::File => 0,
            // Quick settings are scored by how well the query names them (see quick.rs).
            Kind::Calc | Kind::Web | Kind::Toggle => 0,
        }
    }
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub enum Action {
    /// ShellExecute "open" on a path, URI or shell:AppsFolder target.
    Open(String),
    /// Run a command line through cmd.exe.
    Run(String),
    /// Put text on the clipboard.
    Copy(String),
    /// Built-in palette command (lock, sleep, quit, ...).
    Builtin(String),
    /// Bring an open window to the front. `icon` is the app it belongs to (exe path or
    /// shell:AppsFolder\<AUMID>), for its icon.
    Focus { hwnd: isize, icon: String },
    /// Start a program with arguments, without a console (Control Panel pages and the like).
    Exec { file: String, args: String },
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct Hit {
    pub title: String,
    pub subtitle: String,
    pub kind: Kind,
    pub action: Action,
    #[serde(skip)]
    pub score: i32,
}

impl Hit {
    /// Stable identity used for usage statistics.
    pub fn key(&self) -> String {
        match &self.action {
            Action::Open(s) => format!("open:{s}"),
            Action::Run(s) => format!("run:{s}"),
            Action::Copy(_) => format!("copy:{}", self.title),
            Action::Builtin(s) => format!("builtin:{s}"),
            Action::Focus { hwnd, .. } => format!("window:{hwnd}"),
            Action::Exec { file, args } => format!("exec:{file} {args}"),
        }
    }

    /// Path to reveal in Explorer, if this hit is a real file or folder.
    pub fn reveal_path(&self) -> Option<PathBuf> {
        match (&self.action, self.kind) {
            (Action::Open(p), Kind::File | Kind::Folder) => Some(PathBuf::from(p)),
            _ => None,
        }
    }
}

/// A small, always-fully-scanned searchable entry (apps, settings, commands).
#[derive(Clone)]
pub struct Item {
    pub name: String,
    pub hit: Hit,
    /// Set for entries from Windows' settings index: they match by word prefixes over the name
    /// and these keywords instead of fuzzily (a fuzzy scan of 1,200 long setting names would
    /// find scattered letters of almost anything).
    pub keywords: Option<String>,
}
