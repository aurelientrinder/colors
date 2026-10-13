use crate::model::{Action, Hit};
use std::os::windows::process::CommandExt;
use std::path::Path;
use std::process::Command;
use windows::core::{w, PCWSTR};
use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;

const NO_WINDOW: u32 = 0x0800_0000;

fn wide(s: &str) -> Vec<u16> {
    s.encode_utf16().chain(std::iter::once(0)).collect()
}

pub fn shell_open(target: &str) {
    let t = wide(target);
    unsafe {
        ShellExecuteW(None, w!("open"), PCWSTR(t.as_ptr()), None, None, SW_SHOWNORMAL);
    }
}

pub fn shell_execute_elevated(exe: &Path, args: &str) {
    let e = wide(&exe.display().to_string());
    let a = wide(args);
    unsafe {
        ShellExecuteW(None, w!("runas"), PCWSTR(e.as_ptr()), PCWSTR(a.as_ptr()), None, SW_SHOWNORMAL);
    }
}

pub fn reveal(path: &Path) {
    let _ = Command::new("explorer.exe").arg(format!("/select,{}", path.display())).spawn();
}

fn quiet(program: &str, args: &[&str]) {
    let _ = Command::new(program).args(args).creation_flags(NO_WINDOW).spawn();
}

/// Set by the "Palette: Quit" command; `pc_launch` reports it to the shell.
pub static QUIT_REQUESTED: std::sync::atomic::AtomicBool = std::sync::atomic::AtomicBool::new(false);

const RUN_KEY: &str = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run";

/// Scheduled task that starts Palette at sign-in. A task rather than a Run-key entry because
/// Windows holds Run-key apps back for a minute or two after sign-in; a logon task starts at once.
const TASK: &str = "Palette";

/// Is Palette registered to start when you sign in?
pub fn autostart_enabled() -> bool {
    Command::new("schtasks.exe")
        .args(["/query", "/tn", TASK])
        .creation_flags(NO_WINDOW)
        .output()
        .is_ok_and(|o| o.status.success())
}

/// Register or remove the per-user logon task (no admin needed through the ScheduledTasks
/// module; `schtasks /create /sc onlogon` would demand elevation).
pub fn set_autostart(enable: bool) -> bool {
    // The old Run-key entry, from earlier versions, would start a second (immediately exiting) copy.
    let _ = Command::new("reg.exe").args(["delete", RUN_KEY, "/v", "Palette", "/f"]).creation_flags(NO_WINDOW).output();
    let out = if enable {
        let Ok(exe) = std::env::current_exe() else { return false };
        let exe = exe.display().to_string().replace('\'', "''");
        let dir = std::path::Path::new(&exe).parent().map(|p| p.display().to_string()).unwrap_or_default();
        let script = format!(
            "$u = \"$env:USERDOMAIN\\$env:USERNAME\"; \
             Register-ScheduledTask -TaskName '{TASK}' -Force -ErrorAction Stop \
             -Action (New-ScheduledTaskAction -Execute '{exe}' -WorkingDirectory '{dir}') \
             -Trigger (New-ScheduledTaskTrigger -AtLogOn -User $u) \
             -Principal (New-ScheduledTaskPrincipal -UserId $u -LogonType Interactive -RunLevel Limited) \
             -Settings (New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit 0 -MultipleInstances IgnoreNew -Priority 4) | Out-Null"
        );
        Command::new("powershell.exe")
            .args(["-NoProfile", "-NonInteractive", "-Command", &script])
            .creation_flags(NO_WINDOW)
            .output()
    } else {
        Command::new("schtasks.exe")
            .args(["/delete", "/tn", TASK, "/f"])
            .creation_flags(NO_WINDOW)
            .output()
    };
    out.is_ok_and(|o| o.status.success())
}

/// Open Start the way Ctrl+Esc does. The Windows key itself may be bound to the palette, so this
/// is how Start stays one search away.
fn open_start_menu() {
    use windows::core::w;
    use windows::Win32::Foundation::{LPARAM, WPARAM};
    use windows::Win32::UI::WindowsAndMessaging::{FindWindowW, PostMessageW, SC_TASKLIST, WM_SYSCOMMAND};
    unsafe {
        if let Ok(tray) = FindWindowW(w!("Shell_TrayWnd"), None) {
            let _ = PostMessageW(Some(tray), WM_SYSCOMMAND, WPARAM(SC_TASKLIST as usize), LPARAM(0));
        }
    }
}

/// Executes a hit. Returns true if the palette should hide afterwards.
pub fn execute(hit: &Hit, copy: &dyn Fn(&str)) -> bool {
    match &hit.action {
        Action::Open(t) => shell_open(t),
        Action::Run(cmd) => {
            let _ = Command::new("cmd.exe").args(["/c", "start", "", "cmd.exe", "/k", cmd]).spawn();
        }
        Action::Copy(text) => copy(text),
        Action::Focus { hwnd, .. } => crate::wins::focus(*hwnd),
        Action::Exec { file, args } => {
            let (f, a) = (wide(file), wide(args));
            unsafe {
                ShellExecuteW(None, w!("open"), PCWSTR(f.as_ptr()), PCWSTR(a.as_ptr()), None, SW_SHOWNORMAL);
            }
        }
        Action::Builtin(id) if id.starts_with("quick:") => {
            crate::quick::run(&id["quick:".len()..]);
        }
        Action::Builtin(id) => match id.as_str() {
            "start" => open_start_menu(),
            "lock" => quiet("rundll32.exe", &["user32.dll,LockWorkStation"]),
            "sleep" => quiet("rundll32.exe", &["powrprof.dll,SetSuspendState", "0,1,0"]),
            "signout" => quiet("shutdown.exe", &["/l"]),
            "restart" => quiet("shutdown.exe", &["/r", "/t", "0"]),
            "shutdown" => quiet("shutdown.exe", &["/s", "/t", "0"]),
            "reindex" => {
                std::thread::spawn(|| crate::scan::request_mft_snapshot(true));
            }
            "autostart" => {
                // A toggle: running the command again turns it back off.
                std::thread::spawn(|| {
                    set_autostart(!autostart_enabled());
                });
            }
            // The shell does the actual exit so it can remove its tray icon first.
            "quit" => QUIT_REQUESTED.store(true, std::sync::atomic::Ordering::SeqCst),
            _ => {}
        },
    }
    true
}
