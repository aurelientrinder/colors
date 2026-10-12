using System.Runtime.InteropServices;

namespace Palette;

internal static class Win32
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_NOREPEAT = 0x4000;
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

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG msg, IntPtr hWnd, uint min, uint max);

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

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    private const byte VK_MENU = 0x12;
    private const uint KEYEVENTF_KEYUP = 0x2;

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
        keybd_event(VK_UNASSIGNED, 0, 0, UIntPtr.Zero);
        keybd_event(VK_UNASSIGNED, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

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
        keybd_event(VK_MENU, 0, 0, UIntPtr.Zero);
        keybd_event(VK_MENU, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
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
