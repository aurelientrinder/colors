using Microsoft.UI.Xaml;
using Microsoft.UI.Dispatching;

namespace Palette;

/// <summary>
/// Palette's own options, as a list in the palette itself ("Palette: Settings", or the tray icon):
/// a switch per option, flipped in place like Wi-Fi or dark mode, and typing filters them. Two rows
/// are not switches: the shortcut that opens Palette (Enter, then press the new keys) and re-indexing.
/// Esc goes back to Home.
/// </summary>
public sealed partial class MainWindow
{
    private sealed record SettingRow(string Key, string Title, string Subtitle, Func<bool>? Get = null, Action<bool>? Set = null);

    private Dictionary<string, SettingRow> _settingRows = new();

    private List<SettingRow> SettingRows()
    {
        var rows = new List<SettingRow>
        {
            new("hotkey", "Open Palette with", HotkeyText()),
            new("winkey", "Open with the Windows key", "A tap of the Windows key opens Palette instead of Start",
                () => _settings.WinKeyOpens, on => { _settings.WinKeyOpens = on; _settings.Save(); }),
            new("replacesearch", "Replace Windows Search", "Win+S and the taskbar search box open Palette",
                () => _settings.ReplaceSearch, on => { _settings.ReplaceSearch = on; _settings.Save(); }),
            new("taskbarsearch", "Hide the taskbar search box", "Takes Windows' own search box off the taskbar",
                Settings.TaskbarSearchHidden, on => _settings.ApplyTaskbarSearch(on)),
            new("autostart", "Start at sign-in", "Open Palette when you sign in to Windows", () => _autostart ?? false),
            new("keyhints", "Key hints", "The keys that apply to the selected row, under the search field",
                () => _settings.KeyHints),
            new("webanswers", "Answers from the web", "Weather, stocks, facts and currency, fetched only for a question that asks",
                () => _settings.WebAnswers, on => { _settings.WebAnswers = on; _settings.Save(); }),
            new("gcal", "Add Google Calendar", _settings.CalendarFeeds.Count > 0
                ? $"{_settings.CalendarFeeds.Count} connected. Copy another secret iCal address, then press Enter"
                : "Copy your calendar's secret iCal address (Google Calendar › Settings › Integrate calendar), then press Enter"),
            new("eventheadsup", "Heads-up before events", "The bubble above the taskbar says when an event starts in 10 minutes",
                () => _settings.EventHeadsUp, on => { _settings.EventHeadsUp = on; _settings.Save(); }),
            new("downloadnotices", "Say when a download lands", "The bubble offers to open it or show it in its folder",
                () => _settings.DownloadNotices, on => { _settings.DownloadNotices = on; _settings.Save(); WatchDownloads(on); }),
            new("morningbrief", "Morning brief", "The first time you use the PC in the morning: today's events and reminders",
                () => _settings.MorningBrief, on => { _settings.MorningBrief = on; _settings.Save(); }),
            new("chatmemory", "Chat memory", "The chat remembers what you tell it about yourself, on this PC only",
                () => _settings.ChatMemory, on => { _settings.ChatMemory = on; _settings.Save(); }),
            new("clipboard", "Remember what is copied", "Find it by typing clip. Kept in memory only",
                () => _settings.ClipboardHistory, on =>
                {
                    _settings.ClipboardHistory = on;
                    _settings.Save();
                    if (on) _ = ReadClipboardHistory();
                    else Native.ClipClear();
                }),
            new("readwindow", "Read the window in front", "Its selected text and commands. Nothing is saved or sent",
                () => _settings.ReadWindow, on =>
                {
                    _settings.ReadWindow = on;
                    _settings.Save();
                    if (!on) Native.ReadWindow(IntPtr.Zero);
                }),
            new("semantic", "Search by meaning", "Find documents and settings by what they mean. Applies when Palette restarts",
                () => _settings.SemanticSearch, on => { _settings.SemanticSearch = on; _settings.Save(); }),
            new("battery", "Index on battery", "Let the meaning index keep working unplugged. Applies when Palette restarts",
                () => _settings.IndexOnBattery, on => { _settings.IndexOnBattery = on; _settings.Save(); }),
            new("reindex", "Re-index files", "Refresh the file index now"),
        };
        if (_settings.CalendarFeeds.Count > 0)
            rows.Insert(rows.FindIndex(r => r.Key == "gcal") + 1,
                new("gcalremove", "Remove Google Calendars", "Forget the addresses of the calendars added"));
        // What the chat remembers, one row each (Enter forgets it), after its switch.
        var memories = MemoryStore.List();
        int at = rows.FindIndex(r => r.Key == "chatmemory") + 1;
        foreach (var m in memories)
            rows.Insert(at++, new("mem:" + m.Id, m.Text, $"Remembered {m.Updated.ToString("d MMMM", System.Globalization.CultureInfo.CurrentCulture)} · Enter to forget"));
        if (memories.Count > 0) rows.Insert(at, new("memclear", "Forget everything the chat remembers", $"{memories.Count} things"));
        _settingRows = rows.ToDictionary(r => r.Key);
        return rows;
    }

    private void ShowSettings(string filter = "")
    {
        StopCapture();
        string f = filter.Trim();
        Interlocked.Increment(ref _queryVersion);   // drop any search still on its way
        SetView(View.Settings, "Settings · type to filter");
        if (Input.Text != filter) SetInputText(filter);
        _latestQuery = "";
        Items.Clear();
        foreach (var r in SettingRows())
        {
            if (f.Length > 0 && !r.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                             && !r.Subtitle.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            Items.Add(MakeSettingVm(r));
        }
        if (Items.Count > 0) ResultsList.SelectedIndex = 0;
        _holdHeight = false;
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
    }

    private HitVm MakeSettingVm(SettingRow r)
    {
        var control = r.Get is { } get ? new Native.Control("switch", r.Key, get(), 0) : null;
        var vm = new HitVm(new Native.Hit(r.Title, r.Subtitle, "Command", null, null, Control: control, Does: r.Key), chip: "");
        vm.Describe(r.Subtitle, control == null ? ButtonFor(r.Key) : null);
        return vm;
    }

    /// <summary>The label of the row's button in the search card.</summary>
    private static string SettingButton(HitVm row) => row.ControlKey != null ? "Switch" : ButtonFor(row.Does);

    private static string ButtonFor(string? key) => key switch
    {
        "hotkey" => "Change", "gcal" => "Paste", "memclear" => "Forget",
        _ when key?.StartsWith("mem:") == true => "Forget",
        _ => "Run",
    };

    /// <summary>Enter on a row of the settings list.</summary>
    private void RunSetting(int index)
    {
        if (index < 0 || index >= Items.Count) return;
        var vm = Items[index];
        if (vm.ControlKey is { } key)
        {
            ApplySwitch(vm, key, !vm.SwitchOn);
            return;
        }
        switch (vm.Does)
        {
            case "hotkey":
                StartCapture(vm);
                break;
            case "gcal":
                _ = AddCalendarFromClipboard(vm);
                break;
            case "gcalremove":
                RemoveCalendars();
                break;
            case "memclear":
                MemoryStore.Clear();
                ShowSettings(Input.Text);
                break;
            case { } k when k.StartsWith("mem:"):
                MemoryStore.Remove(k[4..]);
                ShowSettings(Input.Text);
                break;
            case "reindex":
                Native.Reindex();
                ShowFileDone(new Native.FileOutcome("Re-indexing files in the background", true, false));
                break;
        }
    }

    /// <summary>A switch on the settings list was flipped (by click or Enter): apply it.</summary>
    private bool ApplySetting(string key, bool on)
    {
        if (!_settingRows.TryGetValue(key, out var row) || row.Set == null) return false;
        row.Set(on);
        return true;
    }

    // ------------------------------------------------------------ the shortcut

    private DispatcherQueueTimer? _capture;
    private HitVm? _captureRow;
    private readonly HashSet<int> _captureHeld = new();

    private static readonly int[] CaptureKeys = BuildCaptureKeys();

    private static int[] BuildCaptureKeys()
    {
        var keys = new List<int> { 0x08, 0x09, 0x0D, 0x1B, 0x20 };
        keys.AddRange(Enumerable.Range(0x21, 0x0E));   // page up/down, end, home, arrows, insert, delete
        keys.AddRange(Enumerable.Range(0x30, 10));
        keys.AddRange(Enumerable.Range(0x41, 26));
        keys.AddRange(Enumerable.Range(0x70, 24));     // F1..F24
        keys.AddRange(Enumerable.Range(0xBA, 7));      // ; = , - . / `
        keys.AddRange(Enumerable.Range(0xDB, 5));      // [ \ ] ' and the like
        return keys.ToArray();
    }

    private static bool Down(int vk) => (Win32.GetAsyncKeyState(vk) & 0x8000) != 0;

    /// <summary>Wait for the next key combination and make it the shortcut.</summary>
    private void StartCapture(HitVm row)
    {
        StopCapture();
        _captureRow = row;
        Input.IsReadOnly = true;   // the keys pressed are not typed
        row.Describe("Press the new keys. Backspace restores Alt+Space, Esc cancels");
        _captureHeld.Clear();
        foreach (int vk in CaptureKeys) if (Down(vk)) _captureHeld.Add(vk);   // the Enter that began this
        _capture = DispatcherQueue.CreateTimer();
        _capture.Interval = TimeSpan.FromMilliseconds(25);
        _capture.Tick += (_, _) => PollCapture();
        _capture.Start();
    }

    private void StopCapture()
    {
        if (_capture == null) return;
        _capture.Stop();
        _capture = null;
        _captureRow = null;
        Input.IsReadOnly = false;
    }

    private void PollCapture()
    {
        if (!_visible || _view != View.Settings)
        {
            StopCapture();
            return;
        }
        _captureHeld.RemoveWhere(vk => !Down(vk));
        uint mods = 0;
        if (Down(0x11)) mods |= Win32.MOD_CONTROL;
        if (Down(0x12)) mods |= Win32.MOD_ALT;
        if (Down(0x10)) mods |= Win32.MOD_SHIFT;
        if (Down(0x5B) || Down(0x5C)) mods |= Win32.MOD_WIN;
        foreach (int vk in CaptureKeys)
        {
            if (!Down(vk) || !_captureHeld.Add(vk)) continue;
            if (vk == 0x1B && mods == 0)
            {
                string keep = Input.Text;
                StopCapture();
                ShowSettings(keep);
                return;
            }
            if (vk == 0x08 && mods == 0)
            {
                _ = FinishCapture(0, 0);
                return;
            }
            if ((mods & (Win32.MOD_CONTROL | Win32.MOD_ALT | Win32.MOD_WIN)) == 0)
            {
                if (_captureRow != null) _captureRow.Describe("Hold Ctrl, Alt or the Windows key with it");
                return;
            }
            _ = FinishCapture(mods, (uint)vk);
            return;
        }
    }

    private async Task FinishCapture(uint mods, uint vk)
    {
        StopCapture();
        bool ok = await ChangeHotkey(mods, vk);
        ShowSettings(Input.Text);
        if (!ok && Items.FirstOrDefault(i => i.Does == "hotkey") is { } row)
            row.Describe($"Another program has that. Still {HotkeyText()}");
    }
}
