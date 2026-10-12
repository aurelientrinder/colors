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
        }
    }
    /// Ranking nudge so apps beat same-scoring files.
    pub fn bonus(self) -> i32 {
        match self {
            Kind::App => 150,
            Kind::Setting => 60,
            Kind::Command => 40,
            Kind::Folder => 20,
            Kind::File => 0,
            Kind::Calc | Kind::Web => 0,
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
}
