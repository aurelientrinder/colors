using System.Runtime.InteropServices;

namespace Palette;

/// <summary>
/// The palette's edge and drop shadow. Clipping the palette to its rounded shape (a window region)
/// costs it the system shadow and leaves a hard, stair-stepped edge, so this click-through,
/// per-pixel-alpha window, owned by the palette and so always just above it, paints both: a soft
/// two-layer shadow (a wide, offset key shadow plus a tight ambient one) outside the panel, and an
/// antialiased rim along its edge. The region is cut a pixel inside the rim, so the rim covers the
/// region's steps. Inside the rim the window is fully transparent.
/// </summary>
internal sealed unsafe class ShadowWindow : IDisposable
{
    private readonly IntPtr _hwnd;
    private IntPtr _memDc, _bmp, _oldBmp;
    private uint* _bits;
    private int _bw, _bh;                    // bitmap size
    private (int x0, int y0, int x1, int y1) _dirty;  // area painted last time
    private string _last = "";
    private byte _lastAlpha;
    private double[] _sideD = [], _sideS = [];

    public ShadowWindow()
    {
        IntPtr inst = GetModuleHandle(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = GetProcAddress(GetModuleHandle("user32.dll"), "DefWindowProcW"),
            hInstance = inst,
            lpszClassName = "PaletteShadow",
        };
        RegisterClassEx(ref wc);
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
        _hwnd = CreateWindowEx(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "PaletteShadow", "", WS_POPUP, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, inst, IntPtr.Zero);
    }

    /// <summary>Shadow margin around the palette window, in pixels at this scale.</summary>
    public static int Margin(double scale) => (int)Math.Ceiling(56 * scale);

    /// <summary>Rim width in pixels: wide enough to cover the region's steps a pixel inside it.</summary>
    public static int RimWidth(double scale) => Math.Max(2, (int)Math.Round(1.5 * scale));

    /// <summary>
    /// Paint the shadow and rim for a panel whose outer edges are (l, t, r, b) in palette-window
    /// pixels, with corner radius <paramref name="radius"/> px. <paramref name="win"/> is the
    /// palette window's screen rectangle.
    /// </summary>
    public void Draw(Windows.Graphics.RectInt32 win, double l, double t, double r, double b, double radius, double opacity, double scale, bool dark)
    {
        if (_hwnd == IntPtr.Zero) return;
        int m = Margin(scale);
        int bw = win.Width + 2 * m, bh = win.Height + 2 * m;
        // The fade is the window's constant alpha: a frame that only fades skips the repaint.
        string key = $"{win.X},{win.Y},{bw},{bh},{l:F1},{t:F1},{r:F1},{b:F1},{radius:F1},{dark}";
        byte alpha = (byte)Math.Clamp(opacity * 255, 0, 255);
        if (key == _last && alpha == _lastAlpha) return;
        if (key == _last)
        {
            // Same picture, new fade: no source DC, so nothing is copied again.
            _lastAlpha = alpha;
            var fade = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
            UpdateLayeredWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref fade, 2 /* ULW_ALPHA */);
            return;
        }
        {
            if (!EnsureBitmap(bw, bh)) return;
            Paint(l + m, t + m, r + m, b + m, radius, scale, dark);
            _last = key;
        }
        _lastAlpha = alpha;

        var dst = new POINT { X = win.X - m, Y = win.Y - m };
        var size = new SIZE { cx = bw, cy = bh };
        var src = new POINT();
        var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref dst, ref size, _memDc, ref src, 0, ref blend, 2 /* ULW_ALPHA */);
    }

    /// <summary>
    /// Paint shadow and rim for the shape at (l, t, r, b) in bitmap pixels. Runs every animation
    /// frame, so only the corners are computed pixel by pixel: above and below the straight top and
    /// bottom edges every pixel of a row is the same, and beside the straight sides every row is
    /// the same profile (bar the rim's top-to-bottom shade).
    /// </summary>
    private void Paint(double l, double t, double r, double b, double radius, double scale, bool dark)
    {
        // Clear what the previous frame painted.
        for (int y = _dirty.y0; y < _dirty.y1; y++)
            new Span<uint>(_bits + (long)y * _bw + _dirty.x0, _dirty.x1 - _dirty.x0).Clear();

        double hx = (r - l) / 2, hy = (b - t) / 2;
        var sh = new Shape((l + r) / 2, (t + b) / 2, hx, hy, Math.Min(radius, Math.Min(hx, hy)), scale, dark);
        // Rim grey from top to bottom: light catches the top edge.
        double rimTop = dark ? 112 : 226, rimMid = dark ? 74 : 206, rimBottom = dark ? 64 : 196;

        double reach = sh.KeyBlur * 2.6 + sh.KeyDy;
        int x0 = Math.Max(0, (int)(l - reach)), x1 = Math.Min(_bw, (int)Math.Ceiling(r + reach));
        int y0 = Math.Max(0, (int)(t - reach)), y1 = Math.Min(_bh, (int)Math.Ceiling(b + reach));
        _dirty = (x0, y0, x1, y1);

        // Columns between the corners (pixel centres within the straight top and bottom edges).
        int xm0 = Math.Clamp((int)Math.Ceiling(l + sh.R - 0.5), x0, x1), xm1 = Math.Clamp((int)Math.Floor(r - sh.R - 0.5) + 1, x0, x1);
        if (xm1 <= xm0) xm0 = xm1 = x1;
        // Rows beside the straight sides of both the panel and its offset key shadow, and the
        // distance and shadow profile across them.
        double sideTop = t + sh.R + sh.KeyDy, sideBottom = b - sh.R;
        if (_sideD.Length < x1 - x0) { _sideD = new double[x1 - x0]; _sideS = new double[x1 - x0]; }
        for (int x = x0; x < x1; x++)
        {
            if (x == xm0) x = xm1;
            if (x >= x1) break;
            double d = Math.Abs(x + 0.5 - sh.Cx) - hx;
            _sideD[x - x0] = d;
            _sideS[x - x0] = sh.ShadowAt(d, d);
        }

        for (int y = y0; y < y1; y++)
        {
            uint* row = _bits + (long)y * _bw;
            double py = y + 0.5;
            double f = Math.Clamp((py - t) / Math.Max(1, b - t), 0, 1);
            double grey = f < 0.25 ? rimTop + (rimMid - rimTop) * (f / 0.25) : rimMid + (rimBottom - rimMid) * ((f - 0.25) / 0.75);
            if (py > sideTop && py < sideBottom)
            {
                for (int x = x0; x < x1; x++)
                {
                    if (x == xm0) x = xm1;
                    if (x >= x1) break;
                    uint v = sh.Pack(_sideD[x - x0], _sideS[x - x0], grey);
                    if (v != 0) row[x] = v;
                }
                continue;
            }
            for (int x = x0; x < xm0; x++)
            {
                uint v = sh.At(x + 0.5, py, grey);
                if (v != 0) row[x] = v;
            }
            if (xm1 > xm0)
            {
                uint v = sh.At(xm0 + 0.5, py, grey);
                if (v != 0) new Span<uint>(row + xm0, xm1 - xm0).Fill(v);
            }
            for (int x = xm1; x < x1; x++)
            {
                uint v = sh.At(x + 0.5, py, grey);
                if (v != 0) row[x] = v;
            }
        }
    }

    /// <summary>The panel's shape and the look of its shadow and rim, at one size.</summary>
    private readonly struct Shape
    {
        public readonly double Cx, Cy, Hx, Hy, R, KeyBlur, KeyDy, KeyA, AmbBlur, AmbA;
        public readonly int Rim;

        public Shape(double cx, double cy, double hx, double hy, double r, double scale, bool dark)
        {
            Cx = cx; Cy = cy; Hx = hx; Hy = hy; R = r;
            KeyBlur = 14 * scale; KeyDy = 6 * scale; KeyA = dark ? 0.32 : 0.15;
            AmbBlur = 3 * scale; AmbA = dark ? 0.24 : 0.09;
            Rim = RimWidth(scale);
        }

        /// <summary>Shadow alpha at distance d from the panel and dk from the offset key shape.</summary>
        public double ShadowAt(double d, double dk)
        {
            if (d <= -0.5) return 0;                                       // under the panel
            double a1 = KeyA * Falloff(dk, KeyBlur);
            double a2 = AmbA * Falloff(d, AmbBlur);
            return 1 - (1 - a1) * (1 - a2);
        }

        /// <summary>Premultiplied pixel: the rim over the shadow; 0 inside the rim (clear glass).</summary>
        public uint Pack(double d, double sa, double grey)
        {
            if (d < -Rim - 1) return 0;
            // Rim: covers the pixel's share of the shape, fading out on its inner side.
            double ra = Math.Clamp(0.5 - d, 0, 1) * Math.Clamp(d + Rim + 0.5, 0, 1);
            double a = ra + sa * (1 - ra);
            uint ai = (uint)(a * 255 + 0.5);
            if (ai == 0) return 0;
            uint c = (uint)(grey * ra + 0.5);                              // the shadow is black
            return ai << 24 | c << 16 | c << 8 | c;
        }

        public uint At(double px, double py, double grey)
        {
            double d = Sdf(px, py, Cx, Cy, Hx, Hy, R);
            if (d < -Rim - 1) return 0;
            return Pack(d, ShadowAt(d, Sdf(px, py - KeyDy, Cx, Cy, Hx, Hy, R)), grey);
        }
    }

    /// <summary>Rounded-rectangle signed distance: negative inside.</summary>
    private static double Sdf(double px, double py, double cx, double cy, double hx, double hy, double r)
    {
        double qx = Math.Abs(px - cx) - (hx - r), qy = Math.Abs(py - cy) - (hy - r);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    /// <summary>Gaussian-like edge: 1 at and inside the edge, fading over about 2.5 blur radii.</summary>
    private static double Falloff(double d, double blur)
    {
        if (d <= 0) return 1;
        double u = d / blur;
        return Math.Exp(-0.5 * u * u);
    }

    private bool EnsureBitmap(int w, int h)
    {
        if (w == _bw && h == _bh && _bits != null) return true;
        FreeBitmap();
        var bi = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
        IntPtr screen = GetDC(IntPtr.Zero);
        _memDc = CreateCompatibleDC(screen);
        ReleaseDC(IntPtr.Zero, screen);
        _bmp = CreateDIBSection(_memDc, ref bi, 0, out IntPtr bits, IntPtr.Zero, 0);
        if (_bmp == IntPtr.Zero) return false;
        _oldBmp = SelectObject(_memDc, _bmp);
        _bits = (uint*)bits;
        _bw = w;
        _bh = h;
        _dirty = (0, 0, 0, 0);
        new Span<uint>(_bits, w * h).Clear();
        return true;
    }

    private void FreeBitmap()
    {
        if (_memDc != IntPtr.Zero)
        {
            SelectObject(_memDc, _oldBmp);
            DeleteDC(_memDc);
        }
        if (_bmp != IntPtr.Zero) DeleteObject(_bmp);
        _memDc = _bmp = _oldBmp = IntPtr.Zero;
        _bits = null;
        _bw = _bh = 0;
    }

    /// <summary>
    /// Show it over <paramref name="owner"/> without taking focus. An owned window always stays
    /// above its owner, so the rim stays on top when the palette is activated.
    /// </summary>
    public void ShowOver(IntPtr owner)
    {
        const int GWLP_HWNDPARENT = -8;
        const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
        SetWindowLongPtr(_hwnd, GWLP_HWNDPARENT, owner);
        SetWindowPos(_hwnd, IntPtr.Zero /* HWND_TOP */, 0, 0, 0, 0, SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    public void Hide() => ShowWindow(_hwnd, 0);

    public void Dispose()
    {
        if (_hwnd != IntPtr.Zero) DestroyWindow(_hwnd);
        FreeBitmap();
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize, style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern IntPtr GetProcAddress(IntPtr mod, string name);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(uint ex, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, ref POINT dst, ref SIZE size, IntPtr srcDc, ref POINT src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dstDc, IntPtr dst, IntPtr size, IntPtr srcDc, IntPtr src, uint key, ref BLENDFUNCTION blend, uint flags);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bi, uint usage, out IntPtr bits, IntPtr section, uint offset);
}
