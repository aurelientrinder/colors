using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Palette;

/// <summary>
/// What Palette notices on its own and says in the bubble (Bubble.cs, Reminders.cs), each with its own
/// switch in Settings:
/// <list type="bullet">
/// <item>An event on the calendar starting within ten minutes.</item>
/// <item>A download that has landed in Downloads (once the browser has finished writing it): open it, or
/// show it in its folder. A program or script is only shown, never opened from the bubble.</item>
/// <item>The morning brief: the first time the PC is used between 5 and noon, the day's events and reminders.</item>
/// </list>
/// They only look: nothing is changed, nothing leaves the PC. Each notice is said once.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly TimeSpan HeadsUp = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(3);   // a download unchanged this long is done

    /// <summary>Every notice ever raised, by key: one dismissed is not raised again.</summary>
    private readonly HashSet<string> _noticed = new();
    private FileSystemWatcher? _downloads;
    /// <summary>Files appearing in Downloads, and when they last changed: said once they have settled.</summary>
    private readonly Dictionary<string, DateTime> _landing = new(StringComparer.OrdinalIgnoreCase);
    private DateTime _lastSlowWatch;
    private Task? _calLoading;
    private DateTime _briefWaitSince;

    private void InitWatchers() => WatchDownloads(_settings.DownloadNotices);

    /// <summary>Every few seconds: downloads settle; every half minute, the calendar and the brief.</summary>
    private void Watch()
    {
        try
        {
            SettleDownloads();
            if (DateTime.Now - _lastSlowWatch < TimeSpan.FromSeconds(30)) return;
            _lastSlowWatch = DateTime.Now;
            KeepCalendarFresh();
            WatchCalendar();
            WatchMorning();
        }
        catch (Exception ex) { App.Log("watch failed: " + ex.Message); }
    }

    private void RaiseNotice(Notice n)
    {
        if (_noticed.Add(n.Key)) Notify(n);
    }

    // ------------------------------------------------------------------ the calendar

    /// <summary>Load the events once at start (Windows' calendars are found then), and again when stale.</summary>
    private void KeepCalendarFresh()
    {
        if (!_settings.EventHeadsUp && !_settings.MorningBrief) return;
        if (_calLoading is { IsCompleted: false }) return;
        bool never = CalendarData.LoadedAt == DateTime.MinValue;
        bool any = _settings.CalendarFeeds.Count > 0 || CalendarData.Sources > 0;
        if (never || (any && CalendarData.IsStale)) _calLoading = LoadCalendar();
    }

    private void WatchCalendar()
    {
        if (!_settings.EventHeadsUp) return;
        var now = DateTime.Now;
        foreach (var e in CalendarData.Events)
        {
            if (e.AllDay || e.Start <= now) continue;
            if (e.Start - now > HeadsUp) break;   // sorted by start
            string start = e.Start.ToString("t", CultureInfo.CurrentCulture), end = e.End.ToString("t", CultureInfo.CurrentCulture);
            RaiseNotice(new Notice($"event:{e.Title}:{e.Start.Ticks}", "event", e.Title, start,
                $"Coming up · {start}–{end}", e.Title, e.Location.Length > 0 ? e.Location : "")
            {
                Link = "See today",
                Expires = e.Start.AddMinutes(5),
            });
        }
    }

    // ------------------------------------------------------------------ the morning brief

    private void WatchMorning()
    {
        var now = DateTime.Now;
        string today = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        if (!_settings.MorningBrief || now.Hour < 5 || now.Hour >= 12 || _settings.LastBrief == today) return;
        if (IdleFor() > TimeSpan.FromMinutes(1)) return;   // said when someone is there to read it
        // The day's events first, if there is a calendar: a minute at most.
        bool calendar = _settings.CalendarFeeds.Count > 0 || CalendarData.Sources > 0;
        if (calendar && (CalendarData.IsStale || _calLoading is { IsCompleted: false }))
        {
            if (_briefWaitSince == default) _briefWaitSince = now;
            if (now - _briefWaitSince < TimeSpan.FromMinutes(1)) return;
        }
        _settings.LastBrief = today;
        _settings.Save();
        RaiseNotice(Brief(now, calendar));
    }

    private static Notice Brief(DateTime now, bool calendar)
    {
        var events = CalendarData.On(now.Date).Where(e => e.AllDay || e.End > now).ToList();
        var reminders = ReminderStore.List().Where(r => r.At.Date == now.Date && r.At > now).ToList();
        string T(DateTime t) => t.ToString("t", CultureInfo.CurrentCulture);

        var counts = new List<string>();
        if (events.Count > 0) counts.Add(events.Count == 1 ? "1 event" : $"{events.Count} events");
        if (reminders.Count > 0) counts.Add(reminders.Count == 1 ? "1 reminder" : $"{reminders.Count} reminders");
        string title = counts.Count > 0 ? string.Join(" and ", counts) + " today."
            : calendar ? "Nothing on your calendar today, and no reminders." : "No reminders today.";

        var parts = new List<string>();
        if (events.Count > 0)
            parts.Add(Joined(events.Select(e => e.AllDay ? $"{e.Title} (all day)" : $"{e.Title} at {T(e.Start)}").ToList()) + ".");
        if (reminders.Count > 0)
            parts.Add((reminders.Count == 1 ? "Reminder: " : "Reminders: ") + Joined(reminders.Select(r => $"{r.Text} at {T(r.At)}").ToList()) + ".");

        return new Notice($"brief:{now:yyyy-MM-dd}", "brief", "Good morning",
            counts.Count > 0 ? string.Join(" · ", counts) : "a clear day",
            "Your day · " + now.ToString("dddd d MMMM", CultureInfo.CurrentCulture), title, string.Join(" ", parts))
        {
            Link = events.Count > 0 ? "See today" : null,
            Chime = false,
            Expires = now.Date.AddHours(12),
        };
    }

    /// <summary>"a, b and c", the first four and then how many more.</summary>
    private static string Joined(List<string> items)
    {
        if (items.Count > 4) items = items.Take(3).Append($"{items.Count - 3} more").ToList();
        return items.Count == 1 ? items[0] : string.Join(", ", items.Take(items.Count - 1)) + " and " + items[^1];
    }

    // ------------------------------------------------------------------ downloads

    /// <summary>What a browser writes while a download is on its way, renamed when it is done.</summary>
    private static readonly HashSet<string> Partial = new(StringComparer.OrdinalIgnoreCase)
        { ".crdownload", ".part", ".partial", ".download", ".tmp", ".opdownload", ".!ut", ".td", ".temp" };

    /// <summary>Not opened from the bubble: shown in their folder instead.</summary>
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
        { ".exe", ".msi", ".msix", ".appx", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".scr", ".com", ".lnk", ".reg", ".hta" };

    private void WatchDownloads(bool on)
    {
        _downloads?.Dispose();
        _downloads = null;
        _landing.Clear();
        if (!on) return;
        try
        {
            string dir = DownloadsFolder();
            if (!Directory.Exists(dir)) return;
            var w = new FileSystemWatcher(dir) { NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.LastWrite };
            w.Created += (_, e) => DispatcherQueue.TryEnqueue(() => Landing(e.FullPath, isNew: true));
            w.Renamed += (_, e) => DispatcherQueue.TryEnqueue(() => Landing(e.FullPath, isNew: Partial.Contains(Path.GetExtension(e.OldFullPath)) || Fresh(e.FullPath)));
            w.Changed += (_, e) => DispatcherQueue.TryEnqueue(() => { if (_landing.ContainsKey(e.FullPath)) _landing[e.FullPath] = DateTime.Now; });
            w.EnableRaisingEvents = true;
            _downloads = w;
        }
        catch (Exception ex) { App.Log("downloads not watched: " + ex.Message); }
    }

    /// <summary>Made in the last few minutes (a file the user only renamed is older).</summary>
    private static bool Fresh(string path)
    {
        try { return DateTime.Now - File.GetCreationTime(path) < TimeSpan.FromMinutes(5); }
        catch { return false; }
    }

    private void Landing(string path, bool isNew)
    {
        string name = Path.GetFileName(path);
        if (!isNew || Partial.Contains(Path.GetExtension(path)) || name.StartsWith("~$") || name.StartsWith('.')
            || name.StartsWith("Unconfirmed ", StringComparison.OrdinalIgnoreCase)
            || name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("thumbs.db", StringComparison.OrdinalIgnoreCase)) return;
        _landing[path] = DateTime.Now;
    }

    /// <summary>Files that have stopped changing and can be read: said, together when several landed at once.</summary>
    private void SettleDownloads()
    {
        if (_landing.Count == 0) return;
        var now = DateTime.Now;
        var done = new List<FileInfo>();
        foreach (var (path, at) in _landing.ToList())
        {
            if (now - at < Settle) continue;
            var f = new FileInfo(path);
            if (!f.Exists || (f.Attributes & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Directory)) != 0)
            {
                _landing.Remove(path);
                continue;
            }
            try { using (f.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { } }
            catch (IOException) { continue; }   // still being written
            catch (UnauthorizedAccessException) { _landing.Remove(path); continue; }
            if (f.Length == 0 && now - at < TimeSpan.FromMinutes(1)) continue;
            _landing.Remove(path);
            done.Add(f);
        }
        if (done.Count == 0) return;
        string time = now.ToString("t", CultureInfo.CurrentCulture);
        if (done.Count == 1)
        {
            var f = done[0];
            bool runnable = Runnable.Contains(f.Extension);
            RaiseNotice(new Notice($"download:{f.FullName}:{f.LastWriteTime.Ticks}", "download", f.Name, "downloaded",
                "Downloaded · " + time, f.Name, $"In Downloads · {Size(f.Length)}" + (runnable ? " · a program, so it is only shown" : ""))
            {
                Link = runnable ? null : "Show in folder",
                Primary = runnable ? "Show in folder" : "Open",
                Chime = false,
                Expires = now.AddMinutes(30),
                Path = f.FullName,
            });
        }
        else
        {
            RaiseNotice(new Notice($"downloads:{now.Ticks}", "download", $"{done.Count} downloads", "landed",
                "Downloaded · " + time, $"{done.Count} files landed in Downloads", Joined(done.Select(f => f.Name).ToList()))
            {
                Primary = "Show in folder",
                Chime = false,
                Expires = now.AddMinutes(30),
                Path = done[0].FullName,
            });
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(in Guid id, uint flags, IntPtr token, out IntPtr path);

    /// <summary>Where the browser saves (moved in Explorer, it is no longer under the profile).</summary>
    private static string DownloadsFolder()
    {
        var id = new Guid("374DE290-123F-4565-9164-39C4925E467B");
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) == 0)
        {
            try { return Marshal.PtrToStringUni(p)!; }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    // ------------------------------------------------------------------ answers

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    private void WatcherChosen(Notice n, NoticeChoice choice)
    {
        if (choice == NoticeChoice.Dismiss) return;
        try
        {
            switch (n.Kind)
            {
                case "download" when n.Path is { } path:
                    // The click was ours: let what opens come to the front.
                    AllowSetForegroundWindow(-1);
                    bool open = choice == NoticeChoice.Primary && n.Primary == "Open" && File.Exists(path);
                    if (open) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                    else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
                    else Process.Start(new ProcessStartInfo(DownloadsFolder()) { UseShellExecute = true });
                    break;
                case "event" or "brief" when choice == NoticeChoice.Link:
                    if (!_visible) ShowPalette();
                    Input.Text = "today";
                    Input.SelectionStart = Input.Text.Length;
                    break;
            }
        }
        catch (Exception ex) { App.Log("notice action failed: " + ex.Message); }
    }

    // ------------------------------------------------------------------ when not to

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    /// <summary>A full-screen game or video, or a presentation, is in front: the bubble waits.</summary>
    private static bool FullScreenInFront()
    {
        const int Busy = 2, D3dFullScreen = 3, Presentation = 4, App = 7;
        return SHQueryUserNotificationState(out int s) == 0 && s is Busy or D3dFullScreen or Presentation or App;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    private static TimeSpan IdleFor()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        return GetLastInputInfo(ref info) ? TimeSpan.FromMilliseconds(unchecked((uint)Environment.TickCount - info.dwTime)) : TimeSpan.Zero;
    }
}
