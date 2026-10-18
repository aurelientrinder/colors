namespace Palette;

/// <summary>
/// The shortcut that opens Palette. It is registered on the hotkey thread, so a change made in the
/// settings is posted there (<see cref="WM_REHOTKEY"/>) and answered once it has been tried.
/// Alt+Space is the default, with Ctrl+Space and Ctrl+Alt+Space tried when another program has it.
/// </summary>
public sealed partial class MainWindow
{
    private const uint WM_REHOTKEY = 0x8002;   // WM_APP + 2 (the tray uses + 1)

    private uint _hotkeyThread;
    private readonly ManualResetEventSlim _hotkeyTried = new(false);
    private bool _hotkeyOk = true;
    private bool _hooked;
    private string _hotkeyLabel = "";

    /// <summary>The shortcut as it is now, for the settings list.</summary>
    private string HotkeyText() => _hotkeyLabel.Length > 0 ? _hotkeyLabel : "No shortcut (use the tray icon)";

    /// <summary>On the hotkey thread: (re)register the shortcut the settings name, else the defaults.</summary>
    private void RegisterHotkey()
    {
        Win32.UnregisterHotKey(IntPtr.Zero, 1);
        _hotkeyLabel = "";
        _hotkeyOk = true;
        if (_settings.HotkeyVk != 0)
        {
            if (Win32.RegisterHotKey(IntPtr.Zero, 1, _settings.HotkeyMods | Win32.MOD_NOREPEAT, _settings.HotkeyVk))
            {
                _hotkeyLabel = HotkeyName(_settings.HotkeyMods, _settings.HotkeyVk);
                UpdateTip();
                return;
            }
            _hotkeyOk = false;   // taken: fall back to the defaults, and say so
        }
        uint[] combos = { Win32.MOD_ALT, Win32.MOD_CONTROL, Win32.MOD_CONTROL | Win32.MOD_ALT };
        string[] names = { "Alt+Space", "Ctrl+Space", "Ctrl+Alt+Space" };
        for (int i = 0; i < combos.Length && _hotkeyLabel.Length == 0; i++)
        {
            if (Win32.RegisterHotKey(IntPtr.Zero, 1, combos[i] | Win32.MOD_NOREPEAT, Win32.VK_SPACE)) _hotkeyLabel = names[i];
        }
        if (_settings.HotkeyVk == 0) _hotkeyOk = _hotkeyLabel.Length > 0;
        UpdateTip();
    }

    private void UpdateTip()
    {
        string label = _hotkeyLabel;
        if (_hooked) label = label.Length > 0 ? label + ", Windows key or Copilot key" : "Windows key or Copilot key";
        _tray.SetTooltip(label.Length > 0 ? $"Palette ({label})" : "Palette (no hotkey available; use this icon)");
    }

    /// <summary>The hotkey thread was asked to register again (called from its message loop).</summary>
    private void OnRehotkey()
    {
        RegisterHotkey();
        _hotkeyTried.Set();
    }

    /// <summary>Make (<paramref name="mods"/>, <paramref name="vk"/>) the shortcut, or 0, 0 for the default. False if it is taken.</summary>
    private async Task<bool> ChangeHotkey(uint mods, uint vk)
    {
        var (oldMods, oldVk) = (_settings.HotkeyMods, _settings.HotkeyVk);
        _settings.HotkeyMods = mods;
        _settings.HotkeyVk = vk;
        bool Try()
        {
            _hotkeyTried.Reset();
            if (_hotkeyThread == 0 || !Win32.PostThreadMessage(_hotkeyThread, WM_REHOTKEY, UIntPtr.Zero, IntPtr.Zero)) return false;
            return _hotkeyTried.Wait(2000) && _hotkeyOk;
        }
        bool ok = await Task.Run(Try);
        if (ok)
        {
            _settings.Save();
            return true;
        }
        _settings.HotkeyMods = oldMods;   // keep what worked
        _settings.HotkeyVk = oldVk;
        await Task.Run(Try);
        return false;
    }

    private static string HotkeyName(uint mods, uint vk)
    {
        var parts = new List<string>();
        if ((mods & Win32.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((mods & Win32.MOD_ALT) != 0) parts.Add("Alt");
        if ((mods & Win32.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((mods & Win32.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(vk switch
        {
            0x08 => "Backspace",
            0x09 => "Tab",
            0x0D => "Enter",
            0x20 => "Space",
            0x21 => "Page Up",
            0x22 => "Page Down",
            0x23 => "End",
            0x24 => "Home",
            0x25 => "Left",
            0x26 => "Up",
            0x27 => "Right",
            0x28 => "Down",
            0x2D => "Insert",
            0x2E => "Delete",
            >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
            _ => ((char)Win32.MapVirtualKey(vk, 2)).ToString().ToUpperInvariant(),
        });
        return string.Join("+", parts);
    }
}
