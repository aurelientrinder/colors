using System.Runtime.InteropServices;

namespace Palette;

internal static class Win32
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    public const uint VK_SPACE = 0x20;
    public const uint WM_HOTKEY = 0x0312;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    public static extern uint MapVirtualKey(uint code, uint mapType);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG msg);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr pid);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT p);

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr h, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>
    /// Make the window a bare popup: no caption, resize frame or edges, so Windows draws no border
    /// line of any kind around it. Returns the (style, exstyle) it had before, for diagnostics.
    /// </summary>
    public static (long style, long ex) StripFrame(IntPtr h)
    {
        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const long WS_POPUP = 0x80000000, WS_CAPTION = 0x00C00000, WS_THICKFRAME = 0x00040000, WS_SYSMENU = 0x00080000,
            WS_MINIMIZEBOX = 0x00020000, WS_MAXIMIZEBOX = 0x00010000, WS_BORDER = 0x00800000, WS_DLGFRAME = 0x00400000;
        const long WS_EX_DLGMODALFRAME = 0x1, WS_EX_WINDOWEDGE = 0x100, WS_EX_CLIENTEDGE = 0x200, WS_EX_STATICEDGE = 0x20000;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20;

        long style = GetWindowLongPtr(h, GWL_STYLE).ToInt64();
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        long newStyle = (style & ~(WS_CAPTION | WS_THICKFRAME | WS_SYSMENU | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_BORDER | WS_DLGFRAME)) | WS_POPUP;
        long newEx = ex & ~(WS_EX_DLGMODALFRAME | WS_EX_WINDOWEDGE | WS_EX_CLIENTEDGE | WS_EX_STATICEDGE);
        if (newStyle != style || newEx != ex)
        {
            SetWindowLongPtr(h, GWL_STYLE, new IntPtr(newStyle));
            SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(newEx));
            SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
        return (style, ex);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr h, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr h, ref POINT p);

    /// <summary>
    /// Where the client area (the XAML content) sits inside the window, and its size. A window
    /// region is in window coordinates, so shapes drawn to match the content must add the offset.
    /// </summary>
    public static (int ox, int oy, int cw, int ch) ClientArea(IntPtr h)
    {
        GetWindowRect(h, out var w);
        GetClientRect(h, out var c);
        var p = new POINT();
        ClientToScreen(h, ref p);
        return (p.X - w.Left, p.Y - w.Top, c.Right - c.Left, c.Bottom - c.Top);
    }

    [DllImport("user32.dll")]
    private static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);

    /// <summary>
    /// Fade the whole window, backdrop included. DWM applies the alpha when composing, so changing
    /// it every frame costs next to nothing. Makes the window layered on first use.
    /// </summary>
    public static void SetWindowAlpha(IntPtr h, byte alpha)
    {
        const int GWL_EXSTYLE = -20;
        const long WS_EX_LAYERED = 0x80000;
        long ex = GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_LAYERED) == 0) SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex | WS_EX_LAYERED));
        SetLayeredWindowAttributes(h, 0, alpha, 0x2 /* LWA_ALPHA */);
    }

    [DllImport("gdi32.dll")]
    public static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseW, int ellipseH);

    /// <summary>The system owns the region afterwards.</summary>
    [DllImport("user32.dll")]
    public static extern int SetWindowRgn(IntPtr hWnd, IntPtr rgn, bool redraw);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern bool SetPriorityClass(IntPtr h, uint cls);

    [DllImport("kernel32.dll")]
    private static extern unsafe bool SetProcessInformation(IntPtr h, int cls, void* info, uint size);

    [DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSizeEx(IntPtr h, nint min, nint max, uint flags);

    [DllImport("ntdll.dll")]
    private static extern int NtSetInformationProcess(IntPtr h, int cls, ref int info, int size);

    /// <summary>
    /// Undo what a background launch does to the process, so the hotkey answers at once however
    /// long it sat idle. A logon task (Task Scheduler priority 7) starts it at below-normal CPU,
    /// low I/O and low memory priority: its pages are trimmed first and each page-in then queues
    /// behind other disk work, which is what made the palette take seconds to appear. Windows can
    /// also put a process with no visible window into EcoQoS (efficiency mode).
    /// </summary>
    public static unsafe void StayResponsive()
    {
        IntPtr me = GetCurrentProcess();
        SetPriorityClass(me, 0x20 /* NORMAL_PRIORITY_CLASS */);   // apps launched from here inherit it too

        int io = 2;                                                // IoPriorityNormal
        NtSetInformationProcess(me, 33 /* ProcessIoPriority */, ref io, sizeof(int));

        uint mem = 5;                                              // MEMORY_PRIORITY_NORMAL
        SetProcessInformation(me, 0 /* ProcessMemoryPriority */, &mem, sizeof(uint));

        // PROCESS_POWER_THROTTLING_STATE { Version = 1, ControlMask, StateMask = 0 }: opt out of
        // EcoQoS and timer-resolution throttling. The indexing threads still throttle themselves.
        uint* pt = stackalloc uint[3] { 1, 0x1 | 0x4, 0 };
        SetProcessInformation(me, 4 /* ProcessPowerThrottling */, pt, 3 * sizeof(uint));

        // A soft working-set floor: under memory pressure Windows trims other processes' pages
        // before the palette's, so the code and data a show touches stay resident.
        SetProcessWorkingSetSizeEx(me, 160 << 20, 1024 << 20, 0x2 | 0x8 /* HARDWS_MIN_DISABLE | HARDWS_MAX_DISABLE */);
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const byte VK_MENU = 0x12;
    public const uint KEYEVENTF_KEYUP = 0x2;

    // ---- low-level keyboard hook (the Copilot key sends Win+Shift+F23, which the shell owns,
    // so RegisterHotKey cannot get it; a hook sees it first and can swallow it).
    public const uint VK_F23 = 0x86;
    public const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    private const int WH_KEYBOARD_LL = 13;
    private const byte VK_UNASSIGNED = 0xE8;

    [StructLayout(LayoutKind.Sequential)]
    public struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public UIntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc fn, IntPtr hMod, uint threadId);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    /// <summary>Installs a global low-level keyboard hook on the calling thread (which must pump messages).</summary>
    public static IntPtr HookKeyboard(LowLevelKeyboardProc proc) =>
        SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(null), 0);

    /// <summary>
    /// Tap an unassigned key so the shell sees "Win + something" and does not open the Start menu
    /// when the (swallowed) Win+Shift+F23 chord is released.
    /// </summary>
    public static void MaskStartMenu()
    {
        keybd_event(VK_UNASSIGNED, 0, 0, InjectMarker);
        keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP, InjectMarker);
    }

    /// <summary>Press and release <paramref name="vk"/>, tagged as our own.</summary>
    public static void TapKey(byte vk)
    {
        keybd_event(vk, 0, 0, InjectMarker);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, InjectMarker);
    }

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vk);

    /// <summary>Tags keystrokes this process injects, so its own keyboard hook lets them through.</summary>
    public static readonly UIntPtr InjectMarker = (UIntPtr)0x50414C45;   // "PALE"

    public const uint KEYEVENTF_EXTENDEDKEY = 0x1;

    /// <summary>INPUT for x64: a 4-byte type, then the union (MOUSEINPUT, the largest, is 32 bytes).</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    public struct INPUT
    {
        [FieldOffset(0)] public uint type;
        [FieldOffset(8)] public ushort wVk;
        [FieldOffset(10)] public ushort wScan;
        [FieldOffset(12)] public uint dwFlags;
        [FieldOffset(16)] public uint time;
        [FieldOffset(24)] public UIntPtr dwExtraInfo;
    }

    public static INPUT Key(ushort vk, ushort scan, uint flags) =>
        new() { type = 1 /* INPUT_KEYBOARD */, wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = InjectMarker };

    [DllImport("user32.dll")]
    public static extern uint SendInput(uint count, INPUT[] inputs, int size);

    [DllImport("user32.dll")]
    public static extern bool PostThreadMessage(uint thread, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    public static uint ProcessOf(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr h, uint flags, System.Text.StringBuilder name, ref uint size);

    public static string? ProcessImageName(uint pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new System.Text.StringBuilder(1024);
            uint n = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref n) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, UIntPtr w, string l, uint flags, uint timeout, out UIntPtr result);

    /// <summary>Tell Explorer (and everyone else) that a setting in area <paramref name="area"/> changed.</summary>
    public static void BroadcastSettingChange(string area) =>
        SendMessageTimeout((IntPtr)0xFFFF /* HWND_BROADCAST */, 0x001A /* WM_SETTINGCHANGE */, UIntPtr.Zero, area,
            0x2 /* SMTO_ABORTIFHUNG */, 200, out _);

    /// <summary>
    /// Move keyboard focus to hwnd even when another app (or the desktop) is foreground.
    /// Returns whether hwnd ended up in the foreground.
    /// </summary>
    public static bool ForceForeground(IntPtr hwnd)
    {
        if (GetForegroundWindow() == hwnd) return true;

        // A hotkey press normally grants the right to take the foreground, so try plainly first.
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return true;

        // Any synthetic keystroke from this process lifts the foreground lock.
        keybd_event(VK_MENU, 0, 0, InjectMarker);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, InjectMarker);
        SetForegroundWindow(hwnd);
        if (GetForegroundWindow() == hwnd) return true;

        // Last resort: share input state with the current foreground thread.
        uint me = GetCurrentThreadId();
        uint fg = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        if (fg != 0 && fg != me)
        {
            AttachThreadInput(me, fg, true);
            SetForegroundWindow(hwnd);
            AttachThreadInput(me, fg, false);
        }
        return GetForegroundWindow() == hwnd;
    }
}
