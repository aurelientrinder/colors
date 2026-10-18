//! Quick actions on a file or folder row, from its action list (Hold Tab): copy it, compress it to
//! a zip, rename it, move it to another folder, put it in the Recycle Bin.
//!
//! Nothing here deletes for good, and nothing overwrites: a name that is taken gets "(2)" added.
//! The last change that can be reversed is remembered, so the line shown afterwards can offer Undo
//! (a zip is recycled, a rename or move is put back). The Recycle Bin is its own undo.

use std::os::windows::process::CommandExt;
use std::path::{Path, PathBuf};
use std::process::Command;
use std::sync::Mutex;
use windows::core::PCWSTR;
use windows::Win32::Storage::FileSystem::{MoveFileExW, MOVEFILE_COPY_ALLOWED};
use windows::Win32::UI::Shell::{
    SHFileOperationW, FOF_ALLOWUNDO, FOF_NOCONFIRMATION, FOF_NOERRORUI, FOF_SILENT, FO_DELETE, SHFILEOPSTRUCTW,
};

const NO_WINDOW: u32 = 0x0800_0000;

/// What the shell shows after an operation: a line, and whether Undo applies.
#[derive(Clone, Default, serde::Serialize)]
pub struct Outcome {
    pub message: String,
    /// Done (false: it did not work, and `message` says why).
    pub ok: bool,
    pub undo: bool,
}

enum Undo {
    /// Move `to` back to `from`.
    Back { from: PathBuf, to: PathBuf },
    /// Recycle a file that was made.
    Recycle(PathBuf),
}

static LAST: Mutex<Option<Undo>> = Mutex::new(None);
static OUTCOME: Mutex<Outcome> = Mutex::new(Outcome { message: String::new(), ok: true, undo: false });

/// The line left by the last operation (`pc_file_outcome`).
pub fn outcome() -> Outcome {
    OUTCOME.lock().unwrap().clone()
}

fn finish(message: impl Into<String>, undo: Option<Undo>) {
    let can = undo.is_some();
    *LAST.lock().unwrap() = undo;
    *OUTCOME.lock().unwrap() = Outcome { message: message.into(), ok: true, undo: can };
}

fn fail(message: impl Into<String>) {
    *OUTCOME.lock().unwrap() = Outcome { message: message.into(), ok: false, undo: false };
}

fn name_of(p: &Path) -> String {
    p.file_name().map(|n| n.to_string_lossy().into_owned()).unwrap_or_else(|| p.display().to_string())
}

fn wide(p: &Path) -> Vec<u16> {
    use std::os::windows::ffi::OsStrExt;
    p.as_os_str().encode_wide().chain(std::iter::once(0)).collect()
}

/// `dir\name`, or `dir\stem (2).ext` and so on when that is taken.
fn free_name(dir: &Path, name: &str) -> PathBuf {
    let first = dir.join(name);
    if !first.exists() {
        return first;
    }
    let (stem, ext) = match name.rsplit_once('.') {
        Some((s, e)) if !s.is_empty() => (s, format!(".{e}")),
        _ => (name, String::new()),
    };
    (2..)
        .map(|n| dir.join(format!("{stem} ({n}){ext}")))
        .find(|p| !p.exists())
        .expect("an unused name")
}

fn move_file(from: &Path, to: &Path) -> Result<(), String> {
    let (f, t) = (wide(from), wide(to));
    unsafe { MoveFileExW(PCWSTR(f.as_ptr()), PCWSTR(t.as_ptr()), MOVEFILE_COPY_ALLOWED) }.map_err(|e| e.message())
}

fn recycle_path(p: &Path) -> Result<(), String> {
    let mut from = wide(p);
    from.push(0); // the list ends with an empty string
    let mut op = SHFILEOPSTRUCTW {
        wFunc: FO_DELETE,
        pFrom: PCWSTR(from.as_ptr()),
        fFlags: (FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_NOERRORUI | FOF_SILENT).0 as u16,
        ..Default::default()
    };
    let code = unsafe { SHFileOperationW(&mut op) };
    if code == 0 && !op.fAnyOperationsAborted.as_bool() {
        Ok(())
    } else {
        Err(format!("error {code}"))
    }
}

/// Put the file on the clipboard, to be pasted in Explorer or attached anywhere.
pub fn copy_file(p: &Path) -> bool {
    let q = p.display().to_string().replace('\'', "''");
    Command::new("powershell.exe")
        .args(["-NoProfile", "-NonInteractive", "-Command", &format!("Set-Clipboard -LiteralPath '{q}'")])
        .creation_flags(NO_WINDOW)
        .status()
        .is_ok_and(|s| s.success())
}

/// A zip next to it, named after it.
pub fn zip(p: &Path) {
    let (Some(dir), Some(name)) = (p.parent(), p.file_name()) else { return fail("Can't compress that") };
    let stem = if p.is_dir() { name.to_string_lossy().into_owned() } else { p.file_stem().unwrap_or(name).to_string_lossy().into_owned() };
    let dest = free_name(dir, &format!("{stem}.zip"));
    // Windows' own tar writes zip archives with -a.
    let ok = Command::new("tar.exe")
        .arg("-a")
        .arg("-c")
        .arg("-f")
        .arg(&dest)
        .arg("-C")
        .arg(dir)
        .arg(name)
        .creation_flags(NO_WINDOW)
        .output()
        .is_ok_and(|o| o.status.success());
    if ok && dest.exists() {
        finish(format!("Created {}", name_of(&dest)), Some(Undo::Recycle(dest)));
    } else {
        let _ = std::fs::remove_file(&dest);
        fail("Couldn't compress it");
    }
}

/// Is `name` fit to be a file name?
pub fn valid_name(name: &str) -> bool {
    let n = name.trim();
    !n.is_empty()
        && n == name
        && !n.ends_with('.')
        && !n.contains(['\\', '/', ':', '*', '?', '"', '<', '>', '|'])
        && n.chars().all(|c| !c.is_control())
}

pub fn rename(p: &Path, new_name: &str) {
    let new_name = new_name.trim();
    let Some(dir) = p.parent() else { return fail("Can't rename that") };
    if !valid_name(new_name) {
        return fail("A name can't be empty or contain \\ / : * ? \" < > |");
    }
    if new_name == name_of(p) {
        return finish("Kept the name", None);
    }
    let to = dir.join(new_name);
    // A name that differs only in case is the same file, not a clash.
    if to.exists() && !to.as_os_str().eq_ignore_ascii_case(p.as_os_str()) {
        return fail(format!("\u{201c}{new_name}\u{201d} already exists"));
    }
    match move_file(p, &to) {
        Ok(()) => finish(format!("Renamed to {new_name}"), Some(Undo::Back { from: p.to_path_buf(), to })),
        Err(e) => fail(format!("Couldn't rename it: {e}")),
    }
}

pub fn move_to(p: &Path, dir: &Path) {
    if !dir.is_dir() {
        return fail("That folder is gone");
    }
    if dir.starts_with(p) {
        return fail("Can't move a folder into itself");
    }
    if p.parent() == Some(dir) {
        return finish("It is already there", None);
    }
    let to = free_name(dir, &name_of(p));
    match move_file(p, &to) {
        Ok(()) => finish(format!("Moved to {}", name_of(dir)), Some(Undo::Back { from: p.to_path_buf(), to })),
        Err(e) => fail(format!("Couldn't move it: {e}")),
    }
}

pub fn recycle(p: &Path) {
    match recycle_path(p) {
        Ok(()) => finish(format!("Moved {} to the Recycle Bin", name_of(p)), None),
        Err(e) => fail(format!("Couldn't recycle it: {e}")),
    }
}

/// Reverse the last operation that can be. Leaves its line in `outcome`.
pub fn undo() {
    let last = LAST.lock().unwrap().take();
    match last {
        Some(Undo::Back { from, to }) => {
            if from.exists() {
                return fail("Something else is there now");
            }
            match move_file(&to, &from) {
                Ok(()) => finish(format!("Put {} back", name_of(&from)), None),
                Err(e) => fail(format!("Couldn't undo: {e}")),
            }
        }
        Some(Undo::Recycle(p)) => match recycle_path(&p) {
            Ok(()) => finish(format!("Removed {}", name_of(&p)), None),
            Err(e) => fail(format!("Couldn't undo: {e}")),
        },
        None => fail("Nothing to undo"),
    }
}

/// A well-known folder of this user (Documents, Downloads, ...), wherever it has been moved to.
fn known(id: &windows::core::GUID) -> Option<PathBuf> {
    use windows::Win32::System::Com::CoTaskMemFree;
    use windows::Win32::UI::Shell::{SHGetKnownFolderPath, KF_FLAG_DEFAULT};
    unsafe {
        let p = SHGetKnownFolderPath(id, KF_FLAG_DEFAULT, None).ok()?;
        let s = p.to_string().ok();
        CoTaskMemFree(Some(p.0 as *const _));
        s.map(PathBuf::from)
    }
}

/// Where a file could go: the folder above its own, the usual folders, then folders you pin or use.
/// (path, title). The folder it is in already is left out.
pub fn destinations(item: &Path, favourites: &[PathBuf]) -> Vec<(PathBuf, String)> {
    use windows::Win32::UI::Shell::{
        FOLDERID_Desktop, FOLDERID_Documents, FOLDERID_Downloads, FOLDERID_Music, FOLDERID_Pictures, FOLDERID_Videos,
    };
    let here = item.parent().map(Path::to_path_buf);
    let mut out: Vec<(PathBuf, String)> = Vec::new();
    let mut add = |p: PathBuf, title: String| {
        if Some(&p) != here.as_ref() && !p.starts_with(item) && p.is_dir() && !out.iter().any(|(o, _)| *o == p) {
            out.push((p, title));
        }
    };
    if let Some(up) = here.as_ref().and_then(|h| h.parent()) {
        add(up.to_path_buf(), format!("Up one level  \u{b7}  {}", name_of(up)));
    }
    for (id, name) in [
        (&FOLDERID_Desktop, "Desktop"),
        (&FOLDERID_Documents, "Documents"),
        (&FOLDERID_Downloads, "Downloads"),
        (&FOLDERID_Pictures, "Pictures"),
        (&FOLDERID_Videos, "Videos"),
        (&FOLDERID_Music, "Music"),
    ] {
        if let Some(p) = known(id) {
            add(p, name.to_string());
        }
    }
    for f in favourites {
        let parent = f.parent().map(|p| p.display().to_string()).unwrap_or_default();
        add(f.clone(), format!("{}  \u{b7}  {parent}", name_of(f)));
    }
    out
}

#[cfg(test)]
mod tests {
    use super::*;

    fn temp(name: &str) -> PathBuf {
        let d = std::env::temp_dir().join(format!("palette-fileops-{}-{name}", std::process::id()));
        let _ = std::fs::remove_dir_all(&d);
        std::fs::create_dir_all(&d).unwrap();
        d
    }

    #[test]
    fn names_are_checked() {
        assert!(valid_name("report 2026.pdf"));
        assert!(!valid_name(""));
        assert!(!valid_name(" x"));
        assert!(!valid_name("a/b"));
        assert!(!valid_name("what?"));
        assert!(!valid_name("dots."));
    }

    #[test]
    fn a_taken_name_gets_a_number() {
        let d = temp("free");
        std::fs::write(d.join("a.txt"), "x").unwrap();
        std::fs::write(d.join("a (2).txt"), "x").unwrap();
        assert_eq!(free_name(&d, "a.txt"), d.join("a (3).txt"));
        assert_eq!(free_name(&d, "b.txt"), d.join("b.txt"));
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn rename_move_and_undo() {
        // One test owns the shared undo slot.
        let d = temp("ops");
        let other = d.join("other");
        std::fs::create_dir_all(&other).unwrap();
        let f = d.join("one.txt");
        std::fs::write(&f, "hello").unwrap();
        std::fs::write(d.join("two.txt"), "x").unwrap();

        rename(&f, "two.txt");
        assert!(outcome().message.contains("already exists") && !outcome().undo);
        rename(&f, "bad/name");
        assert!(!outcome().undo);
        rename(&f, "three.txt");
        assert!(outcome().undo && d.join("three.txt").exists() && !f.exists());
        undo();
        assert!(f.exists() && !d.join("three.txt").exists());

        move_to(&f, &other);
        assert!(outcome().undo && other.join("one.txt").exists());
        undo();
        assert!(f.exists());

        move_to(&f, &d);
        assert_eq!(outcome().message, "It is already there");
        move_to(&d, &other);
        assert!(outcome().message.contains("into itself"));

        let dest = destinations(&f, &[other.clone()]);
        assert!(dest.iter().any(|(p, _)| *p == other));
        assert!(!dest.iter().any(|(p, _)| *p == d));
        let _ = std::fs::remove_dir_all(&d);
    }

    #[test]
    fn a_zip_is_made_and_unmade() {
        let d = temp("zip");
        let f = d.join("notes.txt");
        std::fs::write(&f, "some notes").unwrap();
        zip(&f);
        assert!(outcome().undo, "{}", outcome().message);
        assert!(d.join("notes.zip").exists());
        zip(&f);
        assert!(d.join("notes (2).zip").exists());
        undo();
        assert!(!d.join("notes (2).zip").exists());
        let _ = std::fs::remove_dir_all(&d);
    }
}
