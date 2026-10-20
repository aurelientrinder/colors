using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Palette;

/// <summary>
/// The window is see-through, and the panel is drawn in it the way Windows draws its own flyouts:
/// the rounded panel and its rim by XAML (Chrome, Rim), and its soft shadow by an image around it
/// (a key shadow below and a tight ambient one all round, the look the shadow window had), drawn
/// once and stretched by its middle (Image.NineGrid) to the panel's size. Moving or resizing the
/// panel is then a few cheap property changes per frame. Before, every frame of a resize also cut a
/// new window region and repainted and copied a shadow window, which held animations to every
/// other frame of a 120 Hz screen.
///
/// The window region is only there so that clicks beside the panel reach the window below: a
/// rectangle around the panel and its shadow, grown when an animation needs more room and fitted
/// again once it settles, rather than following every frame.
/// </summary>
public sealed partial class MainWindow
{
    private const double ShadowDip = 40;   // room around the panel for its shadow

    private (double l, double t, double w, double h) _shadowRect = (-1, -1, -1, -1);

    private void InitChrome()
    {
        DrawShadowImage();
        ClearWindowSurface();
        Root.ActualThemeChanged += (_, _) => DrawShadowImage();
    }

    /// <summary>
    /// The shadow of a panel just big enough for its corners, ShadowDip of room all round, one pixel
    /// per DIP: the image's middle row and column stretch to any panel (NineGrid). Deeper in dark,
    /// where it must read against dark. Clear under the panel itself.
    /// </summary>
    private void DrawShadowImage()
    {
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        int m = (int)ShadowDip, r = (int)CornerDip, n = 2 * (m + r) + 1;
        double keyBlur = 14, keyDy = 6, keyA = dark ? 0.32 : 0.15, ambBlur = 3, ambA = dark ? 0.24 : 0.09;
        double c = n / 2.0, half = n / 2.0 - m;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                double d = RoundedRectDistance(x + 0.5, y + 0.5, c, c, half, half, r);
                if (d <= -0.5) continue;   // under the panel
                double dk = RoundedRectDistance(x + 0.5, y + 0.5 - keyDy, c, c, half, half, r);
                double a1 = keyA * Falloff(dk, keyBlur), a2 = ambA * Falloff(d, ambBlur);
                byte a = (byte)Math.Round(Math.Clamp(1 - (1 - a1) * (1 - a2), 0, 1) * 255);
                px[(y * n + x) * 4 + 3] = a;   // premultiplied black
            }
        }
        var bmp = new WriteableBitmap(n, n);
        using (var stream = bmp.PixelBuffer.AsStream()) stream.Write(px, 0, px.Length);
        bmp.Invalidate();
        ShadowImage.Source = bmp;
        ShadowImage.NineGrid = new Thickness(m + r);
    }

    internal static double RoundedRectDistance(double px, double py, double cx, double cy, double hx, double hy, double r)
    {
        double qx = Math.Abs(px - cx) - (hx - r), qy = Math.Abs(py - cy) - (hy - r);
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    /// <summary>Gaussian-like edge: 1 at and inside the edge, fading over about 2.5 blur radii.</summary>
    internal static double Falloff(double d, double blur)
    {
        if (d <= 0) return 1;
        double u = d / blur;
        return Math.Exp(-0.5 * u * u);
    }

    /// <summary>Place the panel's shadow around the panel's own rectangle (DIPs).</summary>
    private void PlaceShadow(double l, double t, double w, double h, double below)
    {
        if ((l, t, w, h) == _shadowRect) return;
        _shadowRect = (l, t, w, h);
        ShadowImage.Width = w + 2 * ShadowDip;
        ShadowImage.Height = h + 2 * ShadowDip;
        ShadowImage.Margin = new Thickness(l - ShadowDip, 0, 0, below - ShadowDip);
    }

    // ------------------------------------------------------------ see-through

    private Win32.SubclassProc? _eraseProc;   // kept alive: Windows calls it for the window's life

    /// <summary>
    /// The window has a GDI surface under its composed content (WinUI creates it without
    /// WS_EX_NOREDIRECTIONBITMAP, and that cannot be changed afterwards), white wherever it is not
    /// painted. With the frame extended over the client area, DWM shows pure black on it as
    /// see-through: paint it black on every paint and every erase, not only when Windows asks to
    /// erase (it does not after every resize, and white showed behind the panel).
    /// </summary>
    private void ClearWindowSurface()
    {
        _eraseProc = (h, msg, w, l, id, data) =>
        {
            const uint WM_PAINT = 0xF, WM_ERASEBKGND = 0x14;
            if (msg == WM_ERASEBKGND)
            {
                Win32.GetClientRect(h, out var rc);
                Win32.FillRect(w, ref rc, Win32.GetStockObject(4 /* BLACK_BRUSH */));
                return 1;
            }
            if (msg == WM_PAINT)
            {
                // The content is composed, not painted: the surface only ever needs to be black.
                IntPtr dc = Win32.BeginPaint(h, out var ps);
                Win32.GetClientRect(h, out var rc);
                Win32.FillRect(dc, ref rc, Win32.GetStockObject(4 /* BLACK_BRUSH */));
                Win32.EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            return Win32.DefSubclassProc(h, msg, w, l);
        };
        Win32.SetWindowSubclass(_hwnd, _eraseProc, (UIntPtr)1, IntPtr.Zero);
        RepaintSurface();
    }

    /// <summary>Paint the whole surface black now (after the window or its region changed).</summary>
    private void RepaintSurface() =>
        Win32.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 /* INVALIDATE */ | 0x4 /* ERASE */ | 0x100 /* UPDATENOW */);

    // ------------------------------------------------------------ the region, for clicks

    private (int l, int t, int r, int b) _clickRect;   // the region last set, in window pixels

    /// <summary>
    /// Keep the window's region around the panel and its shadow (window pixels). While animating it
    /// only grows, with room to spare, so it is set once at the start of a motion and not per frame;
    /// once settled it is fitted to the panel again.
    /// </summary>
    private void FitClickRegion(int l, int t, int r, int b, bool settled)
    {
        var cur = _clickRect;
        bool covers = cur.r > cur.l && l >= cur.l && t >= cur.t && r <= cur.r && b <= cur.b;
        if (!settled)
        {
            if (covers) return;
            // Grow to the window's full width and well above: one change for the whole motion.
            var (ox, oy, cw, ch) = Win32.ClientArea(_hwnd);
            int slack = (int)Math.Round(200 * Scale);
            (l, t, r, b) = (ox, Math.Max(oy, Math.Min(t, cur.r > cur.l ? cur.t : t) - slack), ox + cw, Math.Max(b, cur.b));
        }
        if ((l, t, r, b) == cur) return;
        _clickRect = (l, t, r, b);
        Win32.SetWindowRgn(_hwnd, Win32.CreateRectRgn(l, t, r, b), false);
        RepaintSurface();
    }

    /// <summary>A click beside the panel, on its shadow: as a click anywhere outside the palette, it closes it.</summary>
    private void OnShadowPressed(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true;
        HidePalette(restoreFocus: false);
    }
}

/// <summary>
/// No backdrop at all: the window is see-through wherever the panel and its shadow are not drawn.
/// </summary>
internal sealed class ClearBackdrop : SystemBackdrop
{
    private Windows.UI.Composition.Compositor? _compositor;

    protected override void OnTargetConnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target, XamlRoot root)
    {
        base.OnTargetConnected(target, root);
        SystemDispatcherQueue.Ensure();
        _compositor ??= new Windows.UI.Composition.Compositor();
        target.SystemBackdrop = _compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
    }

    protected override void OnTargetDisconnected(Microsoft.UI.Composition.ICompositionSupportsSystemBackdrop target)
    {
        target.SystemBackdrop = null;
        base.OnTargetDisconnected(target);
    }

    /// <summary>The system compositor wants a Windows.System dispatcher queue on its thread.</summary>
    private static class SystemDispatcherQueue
    {
        private static object? _controller;

        [StructLayout(LayoutKind.Sequential)]
        private struct Options { public int dwSize, threadType, apartmentType; }

        [DllImport("CoreMessaging.dll")]
        private static extern int CreateDispatcherQueueController(Options options, [MarshalAs(UnmanagedType.IUnknown)] out object controller);

        public static void Ensure()
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() != null || _controller != null) return;
            var o = new Options { dwSize = Marshal.SizeOf<Options>(), threadType = 2 /* current */, apartmentType = 2 /* STA */ };
            CreateDispatcherQueueController(o, out _controller);
        }
    }
}
