using System.Runtime.InteropServices;

namespace Palette;

/// <summary>
/// Notification-area icon. WinUI has no built-in one, so this is a plain Win32 message-only
/// window on its own thread. Left click toggles the palette; right click opens a small menu.
/// </summary>
internal sealed class TrayIcon : IDisposable
{
    // ---- Win32 -------------------------------------------------------------------------------
    private const uint WM_APP_TRAY = 0x8001;               // WM_APP + 1
    private const uint WM_DESTROY = 0x0002, WM_NULL = 0, WM_LBUTTONUP = 0x0202, WM_RBUTTONUP = 0x0205,
        WM_CONTEXTMENU = 0x007B, WM_CLOSE = 0x0010;
    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    private const uint MF_STRING = 0, MF_SEPARATOR = 0x800, MF_CHECKED = 8;
    private const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100;
    private const uint IMAGE_ICON = 1, LR_LOADFROMFILE = 0x10;
    private static readonly IntPtr HWND_MESSAGE = new(-3);

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public WndProcDelegate lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID, uFlags, uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState, dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG { public IntPtr hwnd; public uint message; public UIntPtr wParam; public IntPtr lParam; public uint time; public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX c);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern IntPtr DefWindowProc(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG m);
    [DllImport("user32.dll")] private static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] private static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool AppendMenu(IntPtr m, uint flags, UIntPtr id, string? text);
    [DllImport("user32.dll")] private static extern bool DestroyMenu(IntPtr m);
    [DllImport("user32.dll")] private static extern uint TrackPopupMenu(IntPtr m, uint flags, int x, int y, int reserved, IntPtr hWnd, IntPtr rect);
    [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr inst, string name, uint type, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] private static extern bool Shell_NotifyIcon(uint msg, ref NOTIFYICONDATA d);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);

    // ---- state -------------------------------------------------------------------------------
    private readonly Action _toggle;
    private readonly Func<bool> _isAutostart;
    private readonly Action<bool> _setAutostart;
    private readonly Action _reindex;
    private readonly Action _quit;
    private readonly Option[] _options;

    /// <summary>A checkable menu entry.</summary>
    public sealed record Option(string Label, Func<bool> Get, Action<bool> Set);

    private readonly WndProcDelegate _proc;     // kept alive: native code holds a pointer to it
    private readonly Thread _thread;
    private IntPtr _hwnd, _icon;
    private uint _taskbarCreated;
    private string _tip = "Palette";
    private bool _disposed;

    public TrayIcon(Action toggle, Func<bool> isAutostart, Action<bool> setAutostart, Action reindex, Action quit, Option[] options)
    {
        _options = options;
        _toggle = toggle;
        _isAutostart = isAutostart;
        _setAutostart = setAutostart;
        _reindex = reindex;
        _quit = quit;
        _proc = WndProc;
        _thread = new Thread(Run) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public void SetTooltip(string tip)
    {
        _tip = tip;
        if (_hwnd != IntPtr.Zero) Notify(NIM_MODIFY);
    }

    private void Run()
    {
        var inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _proc,
            hInstance = inst,
            lpszClassName = "PaletteTrayWindow",
        };
        RegisterClassEx(ref wc);
        _hwnd = CreateWindowEx(0, wc.lpszClassName, "PaletteTray", 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, inst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        LoadIconFile();
        Notify(NIM_ADD);

        while (GetMessage(out var m, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref m);
            DispatchMessage(ref m);
        }
    }

    private void LoadIconFile()
    {
        // palette.ico sits next to the exe; fall back to no icon (still shows a blank slot).
        string path = Path.Combine(AppContext.BaseDirectory, "palette.ico");
        int size = Math.Max(16, GetSystemMetrics(49 /* SM_CXSMICON */));
        if (File.Exists(path)) _icon = LoadImage(IntPtr.Zero, path, IMAGE_ICON, size, size, LR_LOADFROMFILE);
    }

    private void Notify(uint message)
    {
        var d = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_APP_TRAY,
            hIcon = _icon,
            szTip = _tip.Length > 120 ? _tip[..120] : _tip,
            szInfo = "",
            szInfoTitle = "",
        };
        Shell_NotifyIcon(message, ref d);
    }

    private IntPtr WndProc(IntPtr h, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_APP_TRAY)
        {
            uint ev = (uint)(lParam.ToInt64() & 0xFFFF);
            if (ev == WM_LBUTTONUP) SafeInvoke(_toggle);
            else if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU) ShowMenu();
            return IntPtr.Zero;
        }
        if (_taskbarCreated != 0 && msg == _taskbarCreated)
        {
            Notify(NIM_ADD);                      // Explorer restarted: put the icon back
            return IntPtr.Zero;
        }
        if (msg == WM_DESTROY)
        {
            Notify(NIM_DELETE);
            PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return DefWindowProc(h, msg, wParam, lParam);
    }

    private void ShowMenu()
    {
        bool autostart = false;
        try { autostart = _isAutostart(); } catch { }

        var menu = CreatePopupMenu();
        AppendMenu(menu, MF_STRING, (UIntPtr)1, "Open Palette");
        AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(menu, MF_STRING | (autostart ? MF_CHECKED : 0), (UIntPtr)2, "Run at startup");
        var on = new bool[_options.Length];
        for (int i = 0; i < _options.Length; i++)
        {
            try { on[i] = _options[i].Get(); } catch { }
            AppendMenu(menu, MF_STRING | (on[i] ? MF_CHECKED : 0), (UIntPtr)(100 + i), _options[i].Label);
        }
        AppendMenu(menu, MF_STRING, (UIntPtr)3, "Re-index files");
        AppendMenu(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenu(menu, MF_STRING, (UIntPtr)4, "Quit Palette");

        GetCursorPos(out var p);
        SetForegroundWindow(_hwnd);               // required, or the menu never dismisses on outside click
        uint cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD, p.X, p.Y, 0, _hwnd, IntPtr.Zero);
        PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
        DestroyMenu(menu);

        switch (cmd)
        {
            case 1: SafeInvoke(_toggle); break;
            case 2: SafeInvoke(() => _setAutostart(!autostart)); break;
            case 3: SafeInvoke(_reindex); break;
            case 4: SafeInvoke(_quit); break;
            case >= 100 when cmd - 100 < _options.Length:
                int o = (int)cmd - 100;
                SafeInvoke(() => _options[o].Set(!on[o]));
                break;
        }
    }

    private static void SafeInvoke(Action a)
    {
        try { a(); } catch (Exception ex) { App.Log("tray action failed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_hwnd != IntPtr.Zero)
        {
            Notify(NIM_DELETE);                   // synchronous: the icon is gone before we exit
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }
        if (_icon != IntPtr.Zero) DestroyIcon(_icon);
    }
}
