using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;

namespace Palette;

/// <summary>What the user did with a notice in the bubble.</summary>
internal enum NoticeChoice { Primary, Secondary, Link, Dismiss }

/// <summary>
/// One thing for the bubble to say (a reminder due, an event about to start, a download landed, the
/// morning brief): its pill (<see cref="Pill"/>, <see cref="PillTime"/>), and its card (<see cref="Head"/>,
/// <see cref="Title"/>, <see cref="Body"/>, a link and up to two buttons; a null label leaves it out).
/// <see cref="Key"/> tells notices apart: one already announced is not announced again.
/// </summary>
internal sealed record Notice(string Key, string Kind, string Pill, string PillTime, string Head, string Title, string Body)
{
    public string? Link { get; init; }
    public string? Secondary { get; init; }
    public string Primary { get; init; } = "OK";
    /// <summary>Chime when it comes in (a reminder, an event); otherwise it only glows.</summary>
    public bool Chime { get; init; } = true;
    /// <summary>Gone by itself after this (an event that has started, a download long landed).</summary>
    public DateTime? Expires { get; init; }
    public Reminder? Reminder { get; init; }
    public string? Path { get; init; }
}

/// <summary>
/// The bubble, centred just above the taskbar: a reminder that has come due, or something a watcher
/// noticed (Watchers.cs). It comes in as a small pill (a breathing dot, what it is about, its time)
/// that glows a few times, with a soft chime for what has a time, and then waits; it never takes the
/// keyboard from what the user is doing. Under the pointer (or tapped) it opens into a card with the
/// notice's link and buttons (for a reminder: Open in chat, Snooze 10 min, Done). It stays until one
/// of those, or its ×.
///
/// The window is see-through like the palette's (Chrome.cs): the card and its shadow are drawn in it,
/// and its region keeps clicks beside the card for the windows below.
/// </summary>
internal sealed class Bubble : Window
{
    private const double WindowW = 460, WindowH = 300;   // room for the opened card and its shadow
    private const double ShadowM = 28;                   // room for the shadow around the card
    private const double Gap = 12;                       // between the card and the taskbar
    private const double OpenW = 400;
    private const double PillH = 44;

    private readonly IntPtr _hwnd;
    private readonly AppWindow _aw;
    private readonly Grid _root = new();
    private readonly Image _shadow = new() { Stretch = Stretch.Fill, IsHitTestVisible = false };
    private readonly Image _glow = new() { Stretch = Stretch.Fill, IsHitTestVisible = false, Opacity = 0 };
    private readonly Border _card = new();
    private readonly Grid _pill = new() { ColumnSpacing = 12, Height = PillH, Padding = new Thickness(16, 0, 8, 0) };
    private readonly StackPanel _open = new() { Spacing = 8, Width = OpenW, Padding = new Thickness(16, 12, 12, 12) };
    private readonly Grid _content = new();

    // The pill's parts, and the card's.
    private readonly Ellipse _dot = new() { Width = 10, Height = 10 };
    private readonly Ellipse _ring = new() { Width = 10, Height = 10, StrokeThickness = 2, RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5) };
    private readonly TextBlock _pillText = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 340, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _pillTime = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _pillClose = CloseButton();
    private readonly TextBlock _head = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Button _openClose = CloseButton();
    private readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, LineHeight = 20 };
    private readonly TextBlock _from = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly HyperlinkButton _link = new() { FontSize = 12, Padding = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _second = new() { FontSize = 13, Height = 32, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(6) };
    private readonly Button _first = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Height = 32, Padding = new Thickness(18, 0, 18, 0), CornerRadius = new CornerRadius(6) };

    private readonly Storyboard _pulse = new(), _glowing = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _closeLater;

    private Notice? _notice;
    private bool _expanded, _showing;
    private double _t = 1, _fromW, _fromH, _toW, _toH;   // the card's motion between pill and card
    private long _motionStart;

    /// <summary>The user chose something for the notice on show.</summary>
    public event Action<Notice, NoticeChoice>? Chose;

    public bool Showing => _showing;
    public Notice? Current => _notice;

    public Bubble()
    {
        _hwnd = WindowNative.GetWindowHandle(this);
        _aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));
        SystemBackdrop = new ClearBackdrop();
        ExtendsContentIntoTitleBar = true;
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _aw.SetPresenter(presenter);
        _aw.IsShownInSwitchers = false;
        _aw.Title = "Palette reminder";
        Win32.MakeNoActivate(_hwnd);
        ClearSurface();

        Build();
        Content = _root;
        _root.ActualThemeChanged += (_, _) => Paint();
        _closeLater = DispatcherQueue.CreateTimer();
        _closeLater.Interval = TimeSpan.FromMilliseconds(700);
        _closeLater.IsRepeating = false;
        _closeLater.Tick += (_, _) => Expand(false);
        _aw.MoveAndResize(new RectInt32(-32000, -32000, 100, 100));
        _aw.Hide();
    }

    // ------------------------------------------------------------------ building it

    private static Button CloseButton()
    {
        var b = new Button
        {
            Width = 28, Height = 28, Padding = new Thickness(0), CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Content = new FontIcon { Glyph = "", FontSize = 10 },
        };
        AutomationProperties.SetName(b, "Dismiss");
        ToolTipService.SetToolTip(b, "Dismiss");
        return b;
    }

    private void Build()
    {
        // The pill: a breathing dot, the reminder, its time, ×.
        var dot = new Grid { Width = 10, Height = 10, VerticalAlignment = VerticalAlignment.Center };
        _ring.RenderTransform = new ScaleTransform();
        dot.Children.Add(_ring);
        dot.Children.Add(_dot);
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pill.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _pill.Children.Add(dot);
        Grid.SetColumn(_pillText, 1);
        _pill.Children.Add(_pillText);
        Grid.SetColumn(_pillTime, 2);
        _pill.Children.Add(_pillTime);
        Grid.SetColumn(_pillClose, 3);
        _pill.Children.Add(_pillClose);
        _pill.HorizontalAlignment = HorizontalAlignment.Left;
        _pill.VerticalAlignment = VerticalAlignment.Top;

        // The card: what it is and when, the reminder, where it came from, and what to do with it.
        var head = new Grid { ColumnSpacing = 8 };
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var bellSlot = new Border { VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(bellSlot);
        _bellSlot = bellSlot;
        Grid.SetColumn(_head, 1);
        head.Children.Add(_head);
        Grid.SetColumn(_openClose, 2);
        head.Children.Add(_openClose);
        _open.Children.Add(head);
        _open.Children.Add(_title);
        _open.Children.Add(_from);
        var actions = new Grid { ColumnSpacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        actions.Children.Add(_link);
        Grid.SetColumn(_second, 1);
        actions.Children.Add(_second);
        Grid.SetColumn(_first, 2);
        actions.Children.Add(_first);
        _open.Children.Add(actions);
        _open.HorizontalAlignment = HorizontalAlignment.Left;
        _open.VerticalAlignment = VerticalAlignment.Top;
        _open.Opacity = 0;
        _open.IsHitTestVisible = false;

        _content.Children.Add(_open);
        _content.Children.Add(_pill);
        _content.SizeChanged += (_, e) => _content.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        _card.Child = _content;
        _card.BorderThickness = new Thickness(1);
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Bottom;
        _card.Margin = new Thickness(0, 0, 0, ShadowM);

        foreach (var img in new[] { _glow, _shadow })
        {
            img.HorizontalAlignment = HorizontalAlignment.Center;
            img.VerticalAlignment = VerticalAlignment.Bottom;
            _root.Children.Add(img);
        }
        _root.Children.Add(_card);
        _card.SizeChanged += (_, _) => FitShadow();

        _card.PointerEntered += (_, _) => { _closeLater.Stop(); Expand(true); };
        _card.PointerExited += (_, _) => { if (_expanded) _closeLater.Start(); };
        _pill.Tapped += (_, _) => Expand(true);
        _pillClose.Click += (_, _) => Choose(NoticeChoice.Dismiss);
        _openClose.Click += (_, _) => Choose(NoticeChoice.Dismiss);
        _first.Click += (_, _) => Choose(NoticeChoice.Primary);
        _second.Click += (_, _) => Choose(NoticeChoice.Secondary);
        _link.Click += (_, _) => Choose(NoticeChoice.Link);

        // The dot breathes out a ring, for as long as the bubble shows.
        AddTo(_pulse, _ring, "(UIElement.RenderTransform).(ScaleTransform.ScaleX)", (0, 1), (1.6, 2.4), (2.2, 2.4));
        AddTo(_pulse, _ring, "(UIElement.RenderTransform).(ScaleTransform.ScaleY)", (0, 1), (1.6, 2.4), (2.2, 2.4));
        AddTo(_pulse, _ring, "Opacity", (0, 0.7), (1.6, 0), (2.2, 0));
        _pulse.RepeatBehavior = RepeatBehavior.Forever;
        // The glow swells and settles three times when it comes in.
        AddTo(_glowing, _glow, "Opacity", (0, 0), (0.6, 1), (1.4, 0.15), (2.0, 1), (2.8, 0.15), (3.4, 1), (4.6, 0));
        Paint();
    }

    private Border? _bellSlot;

    private static void AddTo(Storyboard sb, DependencyObject target, string path, params (double s, double v)[] keys)
    {
        var a = new DoubleAnimationUsingKeyFrames { EnableDependentAnimation = false };
        foreach (var (s, v) in keys)
            a.KeyFrames.Add(new EasingDoubleKeyFrame { KeyTime = TimeSpan.FromSeconds(s), Value = v, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, path);
        sb.Children.Add(a);
    }

    // ------------------------------------------------------------------ colours (the palette's own)

    private bool Dark => _root.ActualTheme == ElementTheme.Dark;

    private static SolidColorBrush B(long argb) => new(Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));

    private void Paint()
    {
        bool dark = Dark;
        var accent = B(dark ? 0xFF60CDFF : 0xFF005FB8);
        var text = B(dark ? 0xFFFFFFFF : 0xFF1A1A1A);
        var muted = B(dark ? 0xFFA9B4BF : 0xFF5F6B76);
        var body = B(dark ? 0xFFC9D2DA : 0xFF3A4651);
        _card.Background = B(dark ? 0xFF27323D : 0xFFFFFFFF);
        _card.BorderBrush = B(dark ? 0x14FFFFFF : 0x14000000);
        _dot.Fill = accent;
        _ring.Stroke = accent;
        _pillText.Foreground = text;
        _title.Foreground = text;
        _pillTime.Foreground = muted;
        _head.Foreground = muted;
        _from.Foreground = body;
        _link.Foreground = accent;
        foreach (var close in new[] { _pillClose, _openClose })
        {
            close.Foreground = muted;
            Tint(close, B(dark ? 0x0FFFFFFF : 0x0A000000), B(dark ? 0x1FFFFFFF : 0x14000000), B(dark ? 0x14FFFFFF : 0x0F000000), muted);
        }
        Tint(_second, B(dark ? 0x0FFFFFFF : 0xFFF5F7FA), B(dark ? 0x1AFFFFFF : 0xFFEBEFF4), B(dark ? 0x0AFFFFFF : 0xFFE1E6EC), text);
        _second.BorderBrush = B(dark ? 0x1AFFFFFF : 0x1A000000);
        _second.BorderThickness = new Thickness(1);
        Tint(_first, accent, B(dark ? 0xE660CDFF : 0xE6005FB8), B(dark ? 0xCC60CDFF : 0xCC005FB8), B(dark ? 0xFF000000 : 0xFFFFFFFF));
        _first.BorderThickness = new Thickness(0);
        _accent = accent;
        PutIcon();
        DrawShadows(dark, accent.Color);
    }

    private SolidColorBrush? _accent;

    /// <summary>The card's mark: a bell for a reminder, else the kind's icon.</summary>
    private void PutIcon()
    {
        if (_bellSlot == null || _accent == null) return;
        string? glyph = _notice?.Kind switch
        {
            "event" => "",      // calendar
            "download" => "",   // download
            "brief" => "",      // sun
            _ => null,
        };
        _bellSlot.Child = glyph == null ? new BellIcon(16, _accent) : new FontIcon { Glyph = glyph, FontSize = 14, Foreground = _accent };
    }

    /// <summary>A button's colours at rest, under the pointer and pressed (the template's resources, so hover keeps them).</summary>
    private static void Tint(Button b, Brush rest, Brush over, Brush pressed, Brush fg)
    {
        b.Background = rest;
        b.Foreground = fg;
        b.Resources["ButtonBackground"] = rest;
        b.Resources["ButtonBackgroundPointerOver"] = over;
        b.Resources["ButtonBackgroundPressed"] = pressed;
        b.Resources["ButtonForeground"] = fg;
        b.Resources["ButtonForegroundPointerOver"] = fg;
        b.Resources["ButtonForegroundPressed"] = fg;
        b.Resources["ButtonBorderBrushPointerOver"] = b.BorderBrush ?? rest;
        b.Resources["ButtonBorderBrushPressed"] = b.BorderBrush ?? rest;
    }

    // ------------------------------------------------------------------ shadow and glow

    private const int ShadowR = 18;

    /// <summary>The card's shadow (below, soft) and its glow (the accent, all round), each a small image stretched by its middle.</summary>
    private void DrawShadows(bool dark, Color accent)
    {
        _shadow.Source = Halo(0, 0, 0, dark ? 0.42 : 0.16, blur: 12, dy: 5, ambient: dark ? 0.22 : 0.08);
        _glow.Source = Halo(accent.R, accent.G, accent.B, dark ? 0.55 : 0.4, blur: 9, dy: 0, ambient: 0);
        _shadow.NineGrid = _glow.NineGrid = new Thickness(ShadowM + ShadowR);
    }

    private static WriteableBitmap Halo(byte r, byte g, byte b, double alpha, double blur, double dy, double ambient)
    {
        int m = (int)ShadowM, rr = ShadowR, n = 2 * (m + rr) + 1;
        double c = n / 2.0, half = n / 2.0 - m;
        var px = new byte[n * n * 4];
        for (int y = 0; y < n; y++)
        {
            for (int x = 0; x < n; x++)
            {
                // Drawn under the card too (it is opaque, so none of it shows): a hole cut to one corner
                // radius left a hard, stair-stepped rim round the pill, whose ends are rounder.
                double d = MainWindow.RoundedRectDistance(x + 0.5, y + 0.5, c, c, half, half, rr);
                double dk = MainWindow.RoundedRectDistance(x + 0.5, y + 0.5 - dy, c, c, half, half, rr);
                double a1 = alpha * MainWindow.Falloff(dk, blur), a2 = ambient * MainWindow.Falloff(d, 3);
                double a = Math.Clamp(1 - (1 - a1) * (1 - a2), 0, 1);
                int i = (y * n + x) * 4;   // premultiplied BGRA
                px[i] = (byte)Math.Round(b * a);
                px[i + 1] = (byte)Math.Round(g * a);
                px[i + 2] = (byte)Math.Round(r * a);
                px[i + 3] = (byte)Math.Round(a * 255);
            }
        }
        var bmp = new WriteableBitmap(n, n);
        using (var s = bmp.PixelBuffer.AsStream()) s.Write(px, 0, px.Length);
        bmp.Invalidate();
        return bmp;
    }

    private void FitShadow()
    {
        double w = _card.ActualWidth, h = _card.ActualHeight;
        if (w <= 0 || h <= 0) return;
        foreach (var img in new[] { _shadow, _glow })
        {
            img.Width = w + 2 * ShadowM;
            img.Height = h + 2 * ShadowM;
        }
    }

    // ------------------------------------------------------------------ showing a reminder

    /// <summary>
    /// Show <paramref name="n"/> as a pill (<paramref name="more"/>: how many others wait after it).
    /// <paramref name="announce"/>: it is new, so it glows (and chimes, if it has a time); otherwise (back
    /// after the palette closed) it just reappears.
    /// </summary>
    public void Show(Notice n, int more, bool announce)
    {
        _notice = n;
        _pillText.Text = n.Pill;
        _pillTime.Text = n.PillTime + (more > 0 ? $" · +{more}" : "");
        _head.Text = n.Head + (more > 0 ? $" · {more} more" : "");
        _title.Text = n.Title;
        _from.Text = n.Body;
        _from.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _link.Content = n.Link;
        _link.Visibility = n.Link != null ? Visibility.Visible : Visibility.Collapsed;
        _second.Content = n.Secondary;
        _second.Visibility = n.Secondary != null ? Visibility.Visible : Visibility.Collapsed;
        _first.Content = n.Primary;
        PutIcon();

        bool wasShowing = _showing;
        _showing = true;
        _expanded = false;
        _pill.Opacity = 1;
        _pill.IsHitTestVisible = true;
        _open.Opacity = 0;
        _open.IsHitTestVisible = false;
        var pill = PillSize();
        _card.Width = pill.w;
        _card.Height = pill.h;
        _card.CornerRadius = new CornerRadius(PillH / 2);
        _t = 1;
        Place();
        FitRegion(pill.w, pill.h);
        if (!wasShowing)
        {
            _aw.Show(false);
            Frame();
        }
        _pulse.Begin();
        if (announce)
        {
            _glowing.Stop();
            _glowing.Begin();
            if (n.Chime) Chime();
        }
    }

    /// <summary>Put the bubble away (nothing left to say, or the palette is opening where it sits).</summary>
    public void HideBubble()
    {
        if (!_showing) return;
        _showing = false;
        _notice = null;
        _closeLater.Stop();
        _pulse.Stop();
        _glowing.Stop();
        _glow.Opacity = 0;
        _aw.Hide();
    }

    private void Choose(NoticeChoice choice)
    {
        if (_notice is { } n) Chose?.Invoke(n, choice);
    }

    private (double w, double h) PillSize()
    {
        _pill.Measure(new Windows.Foundation.Size(double.PositiveInfinity, PillH));
        return (Math.Ceiling(_pill.DesiredSize.Width) + 2, PillH + 2);   // + the card's border
    }

    private (double w, double h) OpenSize()
    {
        _open.Measure(new Windows.Foundation.Size(OpenW, double.PositiveInfinity));
        return (OpenW + 2, Math.Ceiling(_open.DesiredSize.Height) + 2);
    }

    // ------------------------------------------------------------------ opening and closing

    private void Expand(bool open)
    {
        if (!_showing || open == _expanded) return;
        _expanded = open;
        var (pw, ph) = PillSize();
        var (ow, oh) = OpenSize();
        _fromW = _card.ActualWidth > 0 ? _card.ActualWidth : (open ? pw : ow);
        _fromH = _card.ActualHeight > 0 ? _card.ActualHeight : (open ? ph : oh);
        (_toW, _toH) = open ? (ow, oh) : (pw, ph);
        _pill.IsHitTestVisible = !open;
        _open.IsHitTestVisible = open;
        if (open) FitRegion(ow, oh);   // room for the card before it grows; fitted back once it has shrunk
        if (open) _glowing.Stop();
        _glow.Opacity = 0;
        _motionStart = Environment.TickCount64;
        _t = 0;
        CompositionTarget.Rendering -= OnFrame;
        CompositionTarget.Rendering += OnFrame;
    }

    private const double MotionMs = 220;

    private void OnFrame(object? sender, object e)
    {
        _t = Math.Min(1, (Environment.TickCount64 - _motionStart) / MotionMs);
        double k = 1 - Math.Pow(1 - _t, 3);   // ease out
        _card.Width = _fromW + (_toW - _fromW) * k;
        _card.Height = _fromH + (_toH - _fromH) * k;
        double r = _expanded ? PillH / 2 + (16 - PillH / 2) * k : 16 + (PillH / 2 - 16) * k;
        _card.CornerRadius = new CornerRadius(r);
        // The pill fades out quickly as the card opens; the card's parts fade in behind it.
        double show = _expanded ? k : 1 - k;
        _open.Opacity = Math.Clamp((show - 0.3) / 0.7, 0, 1);
        _pill.Opacity = Math.Clamp(1 - show / 0.5, 0, 1);
        if (_t < 1) return;
        CompositionTarget.Rendering -= OnFrame;
        if (!_expanded) FitRegion(_toW, _toH);
    }

    // ------------------------------------------------------------------ the window

    private double Scale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    /// <summary>Centred above the taskbar of the main screen; its shadow may fall over the taskbar.</summary>
    private void Place()
    {
        var wa = DisplayArea.Primary.WorkArea;
        double s = Scale;
        int w = (int)Math.Round(WindowW * s), h = (int)Math.Round(WindowH * s);
        int x = wa.X + (wa.Width - w) / 2;
        int y = wa.Y + wa.Height - (int)Math.Round((Gap - ShadowM) * s) - h;
        _aw.MoveAndResize(new RectInt32(x, y, w, h));
        Repaint();
    }

    /// <summary>Clicks reach the bubble only on the card and its shadow: the rest of its window lets them through.</summary>
    private void FitRegion(double cardW, double cardH)
    {
        double s = Scale;
        var (ox, oy, cw, ch) = Win32.ClientArea(_hwnd);
        int w = (int)Math.Ceiling((cardW + 2 * ShadowM) * s), h = (int)Math.Ceiling((cardH + 2 * ShadowM) * s);
        int l = ox + (cw - w) / 2, t = oy + ch - h;
        Win32.SetWindowRgn(_hwnd, Win32.CreateRectRgn(l, t, l + w, oy + ch), true);
        Repaint();
    }

    /// <summary>No frame, border or system shadow: the bubble draws its own; the rest of the window is see-through.</summary>
    private void Frame()
    {
        Win32.StripFrame(_hwnd);
        Win32.MakeNoActivate(_hwnd);
        int round = 1, none = unchecked((int)0xFFFFFFFE), ncOff = 1;
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));
        Win32.DwmSetWindowAttribute(_hwnd, 34, ref none, sizeof(int));
        Win32.DwmSetWindowAttribute(_hwnd, 2, ref ncOff, sizeof(int));
        var glass = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(_hwnd, ref glass);
        Repaint();
    }

    private Win32.SubclassProc? _eraseProc;

    /// <summary>The window's surface under its content, black (see-through to DWM): as the palette's, Chrome.cs.</summary>
    private void ClearSurface()
    {
        _eraseProc = (h, msg, w, l, id, data) =>
        {
            const uint WM_PAINT = 0xF, WM_ERASEBKGND = 0x14, WM_MOUSEACTIVATE = 0x21, WM_NCCALCSIZE = 0x83, WM_NCPAINT = 0x85;
            if (msg == WM_MOUSEACTIVATE) return (IntPtr)3;   // MA_NOACTIVATE: a click does not take the keyboard
            // Showing it puts a thin dialog frame back (WS_DLGFRAME), drawn light down the sides of the
            // see-through window: whatever the styles say, the whole window is client, with no frame to paint.
            if (msg == WM_NCCALCSIZE && w != IntPtr.Zero) return IntPtr.Zero;
            if (msg == WM_NCPAINT) return IntPtr.Zero;
            if (msg == WM_ERASEBKGND)
            {
                Win32.GetClientRect(h, out var rc);
                Win32.FillRect(w, ref rc, Win32.GetStockObject(4));
                return 1;
            }
            if (msg == WM_PAINT)
            {
                IntPtr dc = Win32.BeginPaint(h, out var ps);
                Win32.GetClientRect(h, out var rc);
                Win32.FillRect(dc, ref rc, Win32.GetStockObject(4));
                Win32.EndPaint(h, ref ps);
                return IntPtr.Zero;
            }
            return Win32.DefSubclassProc(h, msg, w, l);
        };
        Win32.SetWindowSubclass(_hwnd, _eraseProc, (UIntPtr)1, IntPtr.Zero);
    }

    private void Repaint() => Win32.RedrawWindow(_hwnd, IntPtr.Zero, IntPtr.Zero, 0x1 | 0x4 | 0x100);

    // ------------------------------------------------------------------ the chime

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>Windows' own reminder sound (Settings › Sound), else the notification one.</summary>
    private static void Chime()
    {
        const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_ALIAS = 0x10000;
        if (!PlaySound("Notification.Reminder", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT))
            PlaySound("SystemNotification", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
    }
}

/// <summary>A small bell, drawn in one stroke: the reminders' mark, in the bubble and in the chat.</summary>
internal sealed class BellIcon : Grid
{
    public BellIcon(double size, Brush stroke)
    {
        Width = Height = size;
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry),
                "M4,11 L4,7 A4,4 0 0 1 12,7 L12,11 L13,12.5 L3,12.5 Z M6.5,14 A1.6,1.6 0 0 0 9.5,14"),
            Stroke = stroke, StrokeThickness = 1.3, StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round, Stretch = Stretch.Uniform, Margin = new Thickness(size / 16),
        };
        Children.Add(path);
    }
}
