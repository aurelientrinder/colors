using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Palette;

/// <summary>
/// Everything that puts the palette where Windows' own search was. Runs on the hotkey thread,
/// which pumps messages for both hooks:
/// <list type="bullet">
/// <item>A tap of the Windows key opens the palette instead of Start (chords like Win+E are left
/// alone; Ctrl+Esc, the Start button and the palette's "Start menu" row still open Start).</item>
/// <item>Win+S and Win+Q, Windows' search shortcuts, open the palette.</item>
/// <item>The Copilot key (Win+Shift+F23), which RegisterHotKey cannot have.</item>
/// <item>Typing into Start or the taskbar search flyout (one window, SearchHost's "Search", on
/// current Windows 11) closes it and continues in the palette, typed text included. Start itself,
/// opened with the mouse or Ctrl+Esc, stays Start for browsing; a Windows key tap closes it.</item>
/// <item>Keys typed between the trigger and the palette having focus are held back and replayed
/// into it, so typing straight after the Windows key loses nothing, as with Start.</item>
/// </list>
/// A low-level hook that answers slowly is silently removed by Windows, so the hook only does
/// bookkeeping and hands the actual work to the UI thread.
/// </summary>
internal sealed class ShellHooks
{
    public const uint WM_FLUSH = 0x8000 + 7;              // WM_APP + 7, posted to the hook thread

    private const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_S = 0x53, VK_Q = 0x51, VK_ESCAPE = 0x1B, VK_CONTROL = 0x11, VK_MENU = 0x12;
    private const uint LLKHF_EXTENDED = 0x1;
    private const long TapMs = 1000, BufferMs = 1500;

    private readonly Settings _settings;
    private readonly Func<bool> _isVisible;
    private readonly Action _toggle, _show;

    private Win32.LowLevelKeyboardProc? _kbdProc;          // referenced so the GC cannot collect them
    private WinEventProc? _fgProc;
    private uint _threadId;

    // Windows key
    private bool _winDown, _winChord;
    private long _winDownAt;
    private readonly HashSet<uint> _swallowedUp = new();   // keys whose down we ate; eat their up too
    private bool _copilotDown;

    // Typeahead
    private bool _buffering;
    private long _bufferSince;
    private readonly List<(Win32.KBDLLHOOKSTRUCT k, bool up)> _buffer = new();
    private readonly HashSet<uint> _bufferedDown = new();

    // Start / the search flyout
    private bool _searchFg;                                // it has the foreground
    private bool _handoff;                                 // it is being closed; the palette opens once it is gone
    private long _handoffAt;

    public ShellHooks(Settings settings, Func<bool> isVisible, Action toggle, Action show)
    {
        _settings = settings;
        _isVisible = isVisible;
        _toggle = toggle;
        _show = show;
    }

    /// <summary>Install both hooks on the calling thread, which must pump messages. Returns whether the keyboard hook is in.</summary>
    public bool Install()
    {
        _threadId = Win32.GetCurrentThreadId();
        _kbdProc = OnKey;
        bool ok = Win32.HookKeyboard(_kbdProc) != IntPtr.Zero;
        _fgProc = OnForeground;
        SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0,
            WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
        return ok;
    }

    /// <summary>The palette is about to open: hold keystrokes until it has focus.</summary>
    public void BeginBuffering()
    {
        _buffering = true;
        _bufferSince = Stopwatch.GetTimestamp();
    }

    /// <summary>Called from the UI thread once the palette has focus: replay what was typed.</summary>
    public void RequestFlush()
    {
        if (_threadId != 0) Win32.PostThreadMessage(_threadId, WM_FLUSH, UIntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Runs on the hook thread (as WM_FLUSH), so no hook call can interleave with it: the replayed
    /// keys enter the input stream ahead of anything typed after this point.
    /// </summary>
    public void Flush()
    {
        _buffering = false;
        if (_buffer.Count > 0)
        {
            var inputs = new Win32.INPUT[_buffer.Count];
            for (int i = 0; i < inputs.Length; i++)
            {
                var (k, up) = _buffer[i];
                inputs[i] = Win32.Key((ushort)k.vkCode, (ushort)k.scanCode,
                    ((k.flags & LLKHF_EXTENDED) != 0 ? Win32.KEYEVENTF_EXTENDEDKEY : 0) | (up ? Win32.KEYEVENTF_KEYUP : 0));
            }
            Win32.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Win32.INPUT>());
            App.Log($"replayed {inputs.Length} buffered key events");
        }
        _buffer.Clear();
        _bufferedDown.Clear();
    }

    private void Trigger(bool toggle)
    {
        bool opening = !_isVisible();
        if (opening) BeginBuffering();
        if (opening && _searchFg)
        {
            // Start keeps the foreground from the palette, so close it first (Escape goes to it
            // ahead of anything else) and open once it is gone: see OnForeground.
            _handoff = true;
            _handoffAt = Stopwatch.GetTimestamp();
            Win32.TapKey((byte)VK_ESCAPE);
            return;
        }
        if (toggle) _toggle();
        else if (opening) _show();
    }

    /// <summary>Keys that type text: letters, digits, numpad, punctuation.</summary>
    private static bool Types(uint vk) =>
        vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A or >= 0x60 and <= 0x6F or >= 0xBA and <= 0xC0 or >= 0xDB and <= 0xDF or 0xE2;

    private static bool Held(uint vk) => (Win32.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    /// <summary>A registered hotkey (Alt+Space) arrived on this thread.</summary>
    public void OnHotkey() => Trigger(toggle: true);

    private static long MsSince(long ts) => (Stopwatch.GetTimestamp() - ts) * 1000 / Stopwatch.Frequency;

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0) return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
        var k = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);
        if (k.dwExtraInfo == Win32.InjectMarker) return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);

        int m = (int)wParam;
        bool down = m == Win32.WM_KEYDOWN || m == Win32.WM_SYSKEYDOWN;
        uint vk = k.vkCode;

        // Copilot key: Win+Shift+F23, owned by the shell.
        if (vk == Win32.VK_F23)
        {
            if (down && !_copilotDown)
            {
                App.Log("copilot key");
                Win32.MaskStartMenu();
                Trigger(toggle: true);
            }
            _copilotDown = down;
            if (_winDown) _winChord = true;
            return (IntPtr)1;                              // Copilot / Microsoft 365 never sees it
        }

        if (vk == VK_LWIN || vk == VK_RWIN)
        {
            if (down)
            {
                if (!_winDown)
                {
                    _winDown = true;
                    _winChord = false;
                    _winDownAt = Stopwatch.GetTimestamp();
                }
            }
            else if (_winDown)
            {
                _winDown = false;
                // A lone, quick tap. The time limit also covers Win+L, whose L Windows keeps from hooks.
                // With Start open the tap is left to close it, as it always has.
                if (!_winChord && _settings.WinKeyOpens && !_searchFg && MsSince(_winDownAt) < TapMs)
                {
                    App.Log("windows key tap");
                    Win32.MaskStartMenu();                 // the shell now sees a chord: no Start menu
                    Trigger(toggle: true);
                }
            }
            return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);   // the Windows key itself always passes
        }

        if (down && _winDown)
        {
            _winChord = true;
            if (_settings.ReplaceSearch && (vk == VK_S || vk == VK_Q))
            {
                App.Log("win+" + (char)vk);
                Win32.MaskStartMenu();
                _swallowedUp.Add(vk);
                Trigger(toggle: false);
                return (IntPtr)1;
            }
        }
        if (!down && _swallowedUp.Remove(vk)) return (IntPtr)1;

        // Typing into Start: carry on in the palette. The key is buffered below and replayed there.
        if (down && _searchFg && !_handoff && !_buffering && _settings.ReplaceSearch && Types(vk)
            && !_winDown && !Held(VK_CONTROL) && !Held(VK_MENU))
        {
            App.Log("typing into Start: handing over");
            Trigger(toggle: false);
        }

        if (_buffering)
        {
            if (MsSince(_bufferSince) > BufferMs)
            {
                Flush();                                   // the palette never took focus: let go
            }
            else if (down)
            {
                _bufferedDown.Add(vk);
                _buffer.Add((k, false));
                return (IntPtr)1;
            }
            else if (_bufferedDown.Remove(vk))
            {
                _buffer.Add((k, true));
                return (IntPtr)1;
            }
        }
        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // ---- system search flyout ----------------------------------------------------------------

    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003, WINEVENT_OUTOFCONTEXT = 0, WINEVENT_SKIPOWNPROCESS = 2;

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);

    // Windows 11, Windows 10 (2004+), Windows 10 (Cortana era).
    private static readonly string[] SearchHosts = { "SearchHost.exe", "SearchApp.exe", "SearchUI.exe" };
    private uint _lastPid;
    private bool _lastPidIsSearch;

    private void OnForeground(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (hwnd == IntPtr.Zero) return;
        uint pid = Win32.ProcessOf(hwnd);
        if (pid != _lastPid)
        {
            _lastPid = pid;
            string? exe = Win32.ProcessImageName(pid);
            _lastPidIsSearch = exe != null && SearchHosts.Contains(Path.GetFileName(exe), StringComparer.OrdinalIgnoreCase);
        }
        _searchFg = _lastPidIsSearch;
        if (_handoff && !_searchFg)
        {
            _handoff = false;
            if (MsSince(_handoffAt) < BufferMs) _show();       // Start has closed: open in its place
        }
    }
}
