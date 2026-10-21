using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.System;
using WinRT.Interop;

namespace Palette;

public sealed partial class MainWindow : Window
{
    // Measured on the new Windows Search: a 500-wide panel, 56-high rows 4 apart (RowDip is the
    // pitch), the list 10 above and 4 below its rows (PadDip), and a 100-high search card with 6
    // around it (SearchDip). The list never grows past MaxBodyDip; it scrolls instead, to bring a
    // preview opened low in a long list into view. It is room for a search's rows and a file
    // preview. The window is that size whole, with room around it for the panel's shadow
    // (Chrome.cs); the panel is WidthDip wide, or WideDip while the month card shows.
    private const double WidthDip = 500, WideDip = 680, WindowDip = WideDip, SearchBaseDip = 112, RowDip = 60, PadDip = 14,
        MaxBodyDip = InstantRows * RowDip + PadDip + FilePaneDip;

    /// <summary>The search card and the room around it: taller by the lines a long chat message wraps onto (ChatInput.cs).</summary>
    private double SearchDip => SearchBaseDip + _inputExtraDip;

    private readonly AppWindow _aw;
    private readonly IntPtr _hwnd;

    public ObservableCollection<HitVm> Items { get; } = new();

    private volatile bool _visible;           // read by the hook thread too
    private DateTime _shownAt = DateTime.MinValue;
    private ulong _gen;                       // generation of the results on screen
    private int _queryVersion;                // bumped on every keystroke
    private string _latestQuery = "";
    private readonly AutoResetEvent _wake = new(false);

    private readonly Dictionary<string, WriteableBitmap> _iconCache = new();
    private readonly HashSet<string> _iconPending = new();
    private readonly HashSet<string> _iconFailed = new();      // no icon to be had (after three tries): rows keep their glyph
    private readonly Dictionary<string, int> _iconTries = new();
    private readonly HashSet<string> _iconWarmOnly = new();    // pending in the warm-up queue only
    private readonly ConcurrentDictionary<string, bool> _iconFetched = new();   // icon threads skip these
    // Icons for rows on screen come first; the warm-up of every app's icon fills in behind them.
    private readonly BlockingCollection<string> _iconJobs = new(), _iconWarm = new();
    private const int IconThreads = 4;

    public MainWindow()
    {
        InitializeComponent();

        StartClipboard();
        _hwnd = WindowNative.GetWindowHandle(this);
        _aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        SystemBackdrop = new ClearBackdrop();   // see-through: the panel draws itself (Chrome.cs)
        ExtendsContentIntoTitleBar = true;

        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(false, false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _aw.SetPresenter(presenter);
        _aw.IsShownInSwitchers = false;

        int round = 2; // DWMWCP_ROUND
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));

        InitMotion();
        InitControls();
        InitInputGrowth();
        InitAnswers();
        InitChrome();
        InitReminders();
        Activated += OnActivated;
        Input.Loaded += (_, _) => HideClearButton();

        // Created off-screen and hidden; the hotkey brings it up.
        _aw.MoveAndResize(new RectInt32(-32000, -32000, 100, 100));
        _aw.Hide();

        _hooks = new ShellHooks(_settings, () => _visible,
            toggle: () => DispatcherQueue.TryEnqueue(() => { if (_visible) HidePalette(restoreFocus: true); else ShowPalette(); }),
            show: () => DispatcherQueue.TryEnqueue(() => { if (!_visible) ShowPalette(); }));

        _tray = new TrayIcon(
            toggle: () => DispatcherQueue.TryEnqueue(ToggleFromTray),
            isAutostart: Native.AutostartEnabled,
            setAutostart: enable => Native.SetAutostart(enable),
            reindex: Native.Reindex,
            quit: () => DispatcherQueue.TryEnqueue(QuitApp),
            settings: () => DispatcherQueue.TryEnqueue(() =>
            {
                if (!_visible) ShowPalette();
                ShowSettings();
            }),
            options: Array.Empty<TrayIcon.Option>());   // every option lives in the settings list (SettingsView.cs)

        new Thread(SearchLoop) { IsBackground = true, Name = "search" }.Start();
        for (int i = 0; i < IconThreads; i++) new Thread(IconLoop) { IsBackground = true, Name = "icons" }.Start();
        new Thread(WarmIcons) { IsBackground = true, Name = "icon-warmup", Priority = ThreadPriority.BelowNormal }.Start();
        new Thread(SemanticLoop) { IsBackground = true, Name = "semantic" }.Start();
        new Thread(PlanLoop) { IsBackground = true, Name = "plan" }.Start();
        // Highest: it runs the keyboard hook, which Windows silently unhooks if it answers late.
        new Thread(HotkeyLoop) { IsBackground = true, Name = "hotkey", Priority = ThreadPriority.Highest }.Start();
        new Thread(ShowRequestLoop) { IsBackground = true, Name = "show-requests" }.Start();
    }

    internal const string ShowEventName = "Local\\PaletteWinUIShow";
    internal static string ShowRequestFile =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "show-query.txt");

    /// <summary>A second launch signals this: open the palette, optionally with a query typed in.</summary>
    private void ShowRequestLoop()
    {
        using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        while (ev.WaitOne())
        {
            string? q = null;
            try
            {
                if (File.Exists(ShowRequestFile))
                {
                    q = File.ReadAllText(ShowRequestFile);
                    File.Delete(ShowRequestFile);
                }
            }
            catch { }
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_visible) ShowPalette();
                if (q != null)
                {
                    Input.Text = q;
                    Input.SelectionStart = q.Length;
                }
            });
        }
    }

    /// <summary>The stock TextBox clear button has no off switch; neutralise its template part.</summary>
    private void HideClearButton()
    {
        Input.ApplyTemplate();
        if (Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(Input) == 0) return;
        var stack = new Stack<DependencyObject>();
        stack.Push(Input);
        while (stack.Count > 0)
        {
            var o = stack.Pop();
            if (o is Button b && b.Name == "DeleteButton")
            {
                b.Width = 0;
                b.Opacity = 0;
                b.IsHitTestVisible = false;
                b.IsTabStop = false;
                return;
            }
            int n = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(o);
            for (int i = 0; i < n; i++) stack.Push(Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(o, i));
        }
    }

    // ------------------------------------------------------------ show / hide

    private double Scale => Win32.GetDpiForWindow(_hwnd) / 96.0;

    private void Resize(bool recenter)
    {
        double s = Scale;
        int rows = Items.Count;
        // More rows scroll; a leading answer shows on its own with the "more results" line.
        double need = _condensedRows > 0 ? _condensedRows * RowDip + ControlsDip(_condensedRows) + PadDip + HintDip
            : rows > 0 ? Math.Min(rows, MaxRows) * RowDip + ControlsDip(MaxRows) + PadDip : 0;
        // While typing the list does not shrink below the instant results' rows: their count can
        // dip for a keystroke, and following it exactly makes the panel bounce. It shrinks to fit
        // once typing settles (HoldHeight). Room for the Related rows is not held: they leave when
        // typing resumes and come back after the next pause, and holding their place in between
        // just shows empty space.
        double held = Math.Min(_listBodyDip, InstantRows * RowDip + PadDip);
        _listBodyDip = _holdHeight && rows > 0 && _condensedRows == 0 ? Math.Max(need, held) : need;
        // Rows arriving or leaving while the list is up (Related rows, a narrower match, no match at
        // all) glide the panel to its new height; a jump would show the window shape a frame ahead
        // of the rows. Only the first rows after opening appear at once: the pop covers them.
        if (_visible && AnimationsOn && _listSettled && _listShownDip != _listBodyDip) EnsureFrames();
        else _listShownDip = _listBodyDip;
        if (rows > 0) _listSettled = true;

        if (recenter)
        {
            // The window keeps one full size (the widest panel, all rows and a preview, and the
            // shadow's room around them); the panel is drawn in it at its current size. Growing a
            // window shows unpainted black for a frame; drawing more of a see-through one does not.
            Win32.GetCursorPos(out var c);
            var area = DisplayArea.GetFromPoint(new PointInt32(c.X, c.Y), DisplayAreaFallback.Nearest);
            var wa = area.WorkArea;
            // The client area (XAML) is smaller than the window by what is left of its frame: add it.
            var (ox, oy, cw, ch) = Win32.ClientArea(_hwnd);
            int ncw = Math.Max(0, _aw.Size.Width - cw) + ox, nch = Math.Max(0, _aw.Size.Height - ch) + oy;
            int m = (int)Math.Round(ShadowDip * s);
            int w = (int)(WindowDip * s) + 2 * m + ncw;
            int ph = (int)((SearchDip + MaxBodyDip) * s) + 2 * m + nch;
            // The panel is anchored to the bottom of the work area (above the taskbar) and grows
            // upward; its shadow may fall over the taskbar, as the old shadow window's did.
            int gap = (int)(12 * s);
            ph = Math.Min(ph, wa.Height - 2 * gap + 2 * m);
            int x = wa.X + (wa.Width - w) / 2;
            int y = wa.Y + wa.Height - gap + m - ph;
            _aw.MoveAndResize(new RectInt32(x, y, w, ph));
            RepaintSurface();   // a resized window's new surface is white until painted
        }
        if (!_animating) ApplyShape();
    }

    private const int MaxRows = 9;            // rows the panel shows; longer lists (All apps) scroll
    private const int InstantRows = 5;        // rows a search returns before Related ones (the engine's MAX_HITS)
    private bool _holdHeight;                 // typing: the list height may grow but not shrink
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _settle;

    /// <summary>Keep the list height from shrinking until results have stopped changing for a moment.</summary>
    private void HoldHeight()
    {
        if (_settle == null)
        {
            _settle = DispatcherQueue.CreateTimer();
            _settle.Interval = TimeSpan.FromMilliseconds(700);
            _settle.IsRepeating = false;
            _settle.Tick += (_, _) =>
            {
                _holdHeight = false;
                if (_visible) Resize(recenter: false);
            };
        }
        _holdHeight = true;
        _settle.Stop();
        _settle.Start();
    }

    /// <summary>
    /// Select the first row, so Enter opens the best match (on the home view too, as Windows does).
    /// Touches the selection only when it is not already as wanted.
    /// </summary>
    private void SelectFirst()
    {
        int want = Items.Count > 0 ? 0 : -1;
        if (ResultsList.SelectedIndex != want) ResultsList.SelectedIndex = want;
        UpdateOpenButton();   // the selected row may have been replaced in place
    }
    private double _listBodyDip;              // height the result rows need
    private double _listShownDip;             // height the list currently has on screen (follows _listBodyDip)
    private bool _listSettled;                // rows have been shown since opening: height changes glide

    // ------------------------------------------------------------ shape + animations
    //
    // The window is clipped to a rounded rectangle with a window region. That gives corners far
    // rounder than DWM's fixed 8 px and hides any system border; DWM's own rounding is switched
    // off. Opening "pops": the window fades in while the content grows from 92% to full size with
    // an ease-out. Selecting a file opens its preview inside its row's card: the card grows, pushing
    // the rows below down, and shrinks back when the selection moves to something else.

    private const double CornerDip = 24, PopMs = 280, PreviewMs = 200, ListMs = 45, HideMs = 110;
    private readonly System.Diagnostics.Stopwatch _popClock = new();
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private double _popT = 1;                 // pop progress, 0..1
    private readonly System.Diagnostics.Stopwatch _hideClock = new();
    private double _hideT;                    // fade-out progress, 0..1 (the window is hidden at 1)
    private double _pvU, _pvTarget;           // preview progress (linear) and where it is heading
    private double _lastFrameMs;
    private bool _animating;

    // The panel widens for the month card and narrows back when it is left, on the same curve and
    // clock as the card unrolling in its row, so the two read as one motion. A change of direction
    // part-way carries on from where the width is, with the time left scaled to the distance left.
    private const double WidthMs = PreviewMs;
    private double _panelDip = WidthDip, _wFrom = WidthDip, _wTo = WidthDip, _wMs = WidthMs;
    private readonly System.Diagnostics.Stopwatch _wClock = new();

    private double StepPanelWidth()
    {
        double to = CalendarWide && _pvTarget > 0 ? WideDip : WidthDip;
        if (to != _wTo)
        {
            _wFrom = _panelDip;
            _wTo = to;
            if (AnimationsOn && _visible)
            {
                _wMs = WidthMs * Math.Clamp(Math.Abs(to - _panelDip) / (WideDip - WidthDip), 0.35, 1);
                _wClock.Restart();
                EnsureFrames();
            }
            else
            {
                _wClock.Reset();
                _panelDip = to;
            }
        }
        if (_wClock.IsRunning)
        {
            double t = Math.Min(1, _wClock.Elapsed.TotalMilliseconds / _wMs);
            _panelDip = _wFrom + (_wTo - _wFrom) * EaseInOut(t);
            if (t >= 1)
            {
                _wClock.Stop();
                _panelDip = _wTo;
            }
        }
        return _panelDip;
    }

    private void StartPop()
    {
        if (!AnimationsOn)
        {
            _popT = 1;
            ApplyShape();
            return;
        }
        _popT = 0;
        _popClock.Restart();
        ApplyShape();
        EnsureFrames();
    }

    private void SetPreviewOpen(bool open)
    {
        double target = open ? 1 : 0;
        if (open && _pvU == 0) StartFromListRoom();
        if (!open && _pvBase > 0)
        {
            // Closing a card that opened part-way: carry on down the plain curve from where it is.
            double p = _pvBase + (1 - _pvBase) * EaseInOut(_pvU);
            _pvBase = 0;
            _pvU = EaseInOutInverse(p);
        }
        _pvTarget = target;
        if (!AnimationsOn || !_visible)
        {
            _pvU = target;
            _pvBase = 0;
            ApplyShape();
            return;
        }
        if (_pvU != target) EnsureFrames();
    }

    // The panel's height stays continuous when rows and a card trade places, as when "calendar"
    // replaces a list of files with one row and its card: the list giving up room and the card
    // taking it would otherwise run on different clocks, and the panel would dip and come back.
    private double _pvBase;   // the share of the card already open when it began to unroll
    private double _pvShownDip;   // how much of the card the last frame showed
    private bool _pvCollapsed;   // a closing row's room was given back since the last frame
    private bool _rowFxWas;   // the last frame shifted or clipped rows, or changed the panel's width
    private double _pvCarry;  // room of a card whose row was replaced, for the card that takes its place

    /// <summary>
    /// A card begins to open: it starts with the room the list is giving up and the room of a card
    /// whose row it replaces, as much as it can take; the list keeps any more and glides it away.
    /// </summary>
    private void StartFromListRoom()
    {
        _pvBase = 0;
        double room = Math.Max(0, _listShownDip - _listBodyDip) + _pvCarry;
        _pvCarry = 0;
        if (!AnimationsOn || !_visible || _pvFull <= 0 || room <= 1) return;
        double take = Math.Min(room, 0.95 * _pvFull);
        _pvBase = take / _pvFull;
        _listShownDip = _listBodyDip + (room - take);
        EnsureFrames();
    }

    /// <summary>
    /// The card's row left the list (a new search replaced it): its room passes to the list, which
    /// glides from there to its own height, and the card is closed at once, not after the pause
    /// that typing gets: there is no row to keep it for.
    /// </summary>
    private void HandOverOrphanedCard(double pvDip)
    {
        if (_pvVm == null || _pvTarget == 0 && _pvU == 0 || Items.Contains(_pvVm)) return;
        _listShownDip += pvDip;
        _pvU = _pvTarget = 0;
        _pvBase = 0;
        _pvVersion++;   // a pending ClosePreviewSoon has nothing left to do
        if (AnimationsOn && _visible) EnsureFrames();
        else _listShownDip = _listBodyDip;
    }

    private static double EaseInOutInverse(double p)
    {
        double lo = 0, hi = 1;
        for (int i = 0; i < 30; i++)
        {
            double mid = (lo + hi) / 2;
            if (EaseInOut(mid) < p) lo = mid; else hi = mid;
        }
        return (lo + hi) / 2;
    }

    private void EnsureFrames()
    {
        if (_animating) return;
        _animating = true;
        _lastFrameMs = _frameClock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering += OnFrame;
    }

    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();
    private static bool AnimationsOn
    {
        get { try { return Ui.AnimationsEnabled; } catch { return true; } }
    }

    private void OnFrame(object? sender, object e)
    {
        double now = _frameClock.Elapsed.TotalMilliseconds;
        double dt = Math.Min(now - _lastFrameMs, 50);
        _lastFrameMs = now;
        bool busy = false;

        if (_popClock.IsRunning)
        {
            _popT = Math.Min(1, _popClock.Elapsed.TotalMilliseconds / PopMs);
            if (_popT >= 1) _popClock.Stop();
            else busy = true;
        }
        if (_pvU != _pvTarget)
        {
            double step = dt / PreviewMs;
            _pvU = _pvTarget > _pvU ? Math.Min(_pvTarget, _pvU + step) : Math.Max(_pvTarget, _pvU - step);
            busy |= _pvU != _pvTarget;
            // Fully open: a row near the bottom of a long list has its preview below the fold.
            if (_pvU == _pvTarget && _pvTarget > 0) DispatcherQueue.TryEnqueue(ScrollToSelection);
        }
        if (_listShownDip != _listBodyDip)
        {
            _listShownDip += (_listBodyDip - _listShownDip) * (1 - Math.Exp(-dt / ListMs));
            if (Math.Abs(_listBodyDip - _listShownDip) < 0.5) _listShownDip = _listBodyDip;
            else busy = true;
        }
        busy |= StepClosingPreviews(dt);
        bool hidden = false;
        if (_hideClock.IsRunning)
        {
            _hideT = Math.Min(1, _hideClock.Elapsed.TotalMilliseconds / HideMs);
            hidden = _hideT >= 1;
            busy |= !hidden;
        }

        ApplyShape();
        busy |= _wClock.IsRunning;
        busy |= StepHighlight(dt);
        if (hidden) FinishHide();
        if (!busy)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
            SettleClickRegion();
            if (_calRedraw)
            {
                _calRedraw = false;
                if (CalendarOnCard) DrawCalendar();
            }
        }
    }

    private void ApplyShape()
    {
        double s = Scale;
        // Shapes are computed in client (XAML) pixels, then shifted by the client area's offset
        // inside the window for the region, which is in window coordinates.
        var (ox, oy, w, wh) = Win32.ClientArea(_hwnd);
        if (w <= 0 || wh <= 0) return;

        // Preview: ease-in-out cubic on its linear progress, from the share it started with.
        double u = _pvU;
        double e = EaseInOut(u);
        double p = _pvBase + (1 - _pvBase) * e;
        bool rows = Items.Count > 0;
        // The pane is laid out at full height inside its row from the start, and the part not yet
        // open is clipped off and the rows below shifted up by it (ListMotion.cs), so it unrolls
        // without the list being laid out again at each frame.
        double pvDip = rows && _pvVm != null ? _pvFull * p : 0;
        if (_pvVm != null && !Items.Contains(_pvVm))
        {
            HandOverOrphanedCard(pvDip);
            pvDip = 0;
            p = e = 0;
        }
        double pvLayout = _pvVm != null && Items.Contains(_pvVm) && (_pvU > 0 || _pvTarget > 0) ? _pvFull : 0;
        bool relaid = _pvCollapsed;   // a closing row gave its room back this frame
        _pvCollapsed = false;
        if (_pvVm != null && _pvVm.PreviewHeight != pvLayout)
        {
            _pvVm.PreviewHeight = pvLayout;
            relaid = true;
        }
        _pvShownDip = pvDip;
        // A card that started part-way open fades in over its own time rather than appearing at once;
        // while the panel widens for it, it shows as the panel uncovers it, not cut by the edge.
        double fade = _pvBase > 0 ? e : p * p;
        if (_wClock.IsRunning && _wTo > _wFrom) fade *= Math.Pow(Math.Clamp((_panelDip - _wFrom) / (_wTo - _wFrom), 0, 1), 2);
        PreviewPane.Opacity = fade;

        var opening = new List<(int index, double hidden)>();
        if (pvLayout - pvDip > 0.25) opening.Add((Items.IndexOf(_pvVm!), pvLayout - pvDip));
        double closingShown = 0, closingLayout = 0;
        foreach (var (cvm, from, cu) in _pvClosing)
        {
            int j = Items.IndexOf(cvm);
            if (j < 0) continue;
            double shown = from * EaseInOut(cu);
            closingShown += shown;
            closingLayout += from;
            if (from - shown > 0.25) opening.Add((j, from - shown));
        }

        // Never taller than the window: the panel grows upward from the bottom, and the search
        // field must stay on screen.
        double roomDip = Math.Max(0, wh / s - SearchDip - 2 * ShadowDip);
        double cap = Math.Min(MaxBodyDip, roomDip);
        double bodyDip = Math.Min(_listShownDip + pvDip + closingShown, cap);
        // Whole physical pixels. The height glides through fractions, and layout rounds the panel
        // and each child to pixels on its own: the search field, at the bottom, landed a pixel
        // higher or lower on alternate frames and shook after almost any interaction.
        bodyDip = Math.Round(bodyDip * s) / s;
        if (Body.Height != bodyDip) Body.Height = bodyDip;

        // Pop: the panel fades in and grows from 94%, its shadow with it, all on the GPU. It swells
        // a hair past full size and settles back (ease-out-back, about 1.2% over): a soft bubble
        // pop rather than a plain zoom.
        const double Overshoot = 3.0;
        double u1 = _popT - 1;
        double ease = 1 + (Overshoot + 1) * u1 * u1 * u1 + Overshoot * u1 * u1;
        double k = 0.94 + 0.06 * ease;
        double alpha = Math.Clamp(_popT / 0.45, 0, 1);
        // Hiding fades out and settles back a little, the pop in reverse but quicker.
        double hide = _hideT * _hideT;
        alpha *= 1 - hide;
        k *= 1 - 0.03 * hide;

        // The panel in whole physical pixels: its width even, so that it stays centred on a whole
        // pixel, and the stage, the panel and its rim laid out on exactly those pixels (left edge by
        // margin, not by centring), so nothing rounds to one side or the other of a half pixel as
        // the size glides. It sits ShadowDip above the window's bottom, room for its shadow.
        int m = (int)Math.Round(ShadowDip * s);
        int hPx = (int)Math.Round(Math.Min((SearchDip + bodyDip) * s, wh - 2 * m));
        int panelPx = Math.Min(w - 2 * m, 2 * (int)Math.Round(StepPanelWidth() * s / 2));
        int leftPx = (w - panelPx) / 2, bottomPx = wh - m, topPx = bottomPx - hPx;
        double panelW = panelPx / s, panelH = hPx / s, left = leftPx / s, below = m / s;
        if (Stage.Width != panelW) Stage.Width = panelW;
        HoldListWidth();
        var at = new Thickness(left, 0, 0, below);
        if (Stage.Margin != at) Stage.Margin = at;
        foreach (var f in new FrameworkElement[] { Chrome, Rim })
        {
            if (f.Width != panelW) f.Width = panelW;
            if (f.Height != panelH) f.Height = panelH;
            if (f.Margin != at) f.Margin = at;
        }
        // The list itself is laid out at the height it is heading to, and only clipped to what the
        // panel shows: its rows are not laid out again while the panel's edge glides.
        double hintDip = MoreHint.Visibility == Visibility.Visible ? HintDip : 0;
        double layoutDip = Math.Round(Math.Min(_listBodyDip + pvLayout + closingLayout, cap) * s) / s;
        double listH = Math.Max(0, layoutDip - hintDip - PadDip);
        if (ResultsList.Height != listH)
        {
            ResultsList.Height = listH;
            relaid = true;
        }
        // A row's or the list's height changed: lay them out now, so the rows' shift and clip below
        // are worked out from the heights they have this frame, not the last.
        if (relaid) ResultsList.UpdateLayout();
        _listShownW = panelW - ListInsetDip;
        ClipList(bodyDip - hintDip - PadDip, _listShownW);
        DrawRowMotion(opening);
        // The selection card follows the rows' shift and clip, and the panel's width.
        bool follow = _rowFx.Count > 0 || _wClock.IsRunning;
        if (follow || _rowFxWas) UpdateHighlight();
        _rowFxWas = follow;
        PlaceShadow(left, topPx / s, panelW, panelH, below);
        _panelPx = (leftPx + ox, topPx + oy, leftPx + panelPx + ox, bottomPx + oy, m);
        FitClickRegion(leftPx + ox - m, topPx + oy - m, leftPx + panelPx + ox + m, bottomPx + oy + m, settled: !_animating);

        if (Root.Opacity != alpha) Root.Opacity = alpha;
        RootXf.CenterX = left + panelW / 2;
        RootXf.CenterY = (bottomPx - hPx * 0.35) / s;
        RootXf.ScaleX = RootXf.ScaleY = k;
    }

    private (int l, int t, int r, int b, int m) _panelPx;   // the panel last drawn, window pixels, and the shadow's room

    /// <summary>A motion has settled: fit the click region to the panel again.</summary>
    private void SettleClickRegion()
    {
        var (l, t, r, b, m) = _panelPx;
        if (r > l) FitClickRegion(l - m, t - m, r + m, b + m, settled: true);
    }

    private IntPtr _previousForeground;   // the window that had focus before the palette opened
    private int _reassertTries;
    private DateTime _lastHidden = DateTime.MinValue;
    private readonly TrayIcon _tray;

    /// <summary>Left click on the tray icon.</summary>
    private void ToggleFromTray()
    {
        // Clicking the tray icon first deactivates (and so hides) a visible palette; without this
        // the same click would immediately reopen it.
        if ((DateTime.UtcNow - _lastHidden).TotalMilliseconds < 500) return;
        if (_visible) HidePalette(restoreFocus: false);
        else ShowPalette();
    }

    private void QuitApp()
    {
        _tray.Dispose();                  // remove the icon before the process disappears
        Environment.Exit(0);
    }

    private void ShowPalette()
    {
        App.Log("ShowPalette");
        BubbleAway();
        _hideClock.Stop();    // reopened while fading out
        _hideT = 0;
        Root.IsHitTestVisible = true;
        _listSettled = false;
        _expandedQuery = null;
        _answerCts?.Cancel();
        ResetMotion();
        var fg = Win32.GetForegroundWindow();
        _previousForeground = fg != _hwnd ? fg : IntPtr.Zero;
        _reassertTries = 0;
        ReadWindowBehind();
        if (DateTime.UtcNow - _clipRead > TimeSpan.FromSeconds(30)) _ = ReadClipboardHistory();
        PrefetchCalendar();

        SetView(View.Results, SearchPlaceholder);
        SetInputText("");
        Items.Clear();
        _latestQuery = "";
        _walkText = null;
        _walkSelected.Clear();
        _reselectTitle = null;
        ResetPreview();
        Resize(recenter: true);
        _shownAt = DateTime.UtcNow;
        _visible = true;
        StartPop();
        _aw.Show();
        RepaintSurface();   // black (see-through) before its first frame on screen
        // The presenter can put its frame styles back on Show; a frame is what Windows draws the
        // white outline for, so take it off again every time.
        var (st, ex) = Win32.StripFrame(_hwnd);
        App.Log($"styles before strip: 0x{st:X} ex 0x{ex:X}");
        // DWM can reset these when a window is hidden and shown again: re-apply every time so
        // the popup keeps its rounded corners and never gets the square 1 px system border.
        int round = 1, none = unchecked((int)0xFFFFFFFE);                   // 1 = DWMWCP_DONOTROUND (the panel rounds itself)
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));     // DWMWA_WINDOW_CORNER_PREFERENCE
        Win32.DwmSetWindowAttribute(_hwnd, 34, ref none, sizeof(int));      // DWMWA_BORDER_COLOR = none
        int ncOff = 1;                                                      // DWMNCRP_DISABLED: no frame, shadow or hairline
        Win32.DwmSetWindowAttribute(_hwnd, 2, ref ncOff, sizeof(int));      // DWMWA_NCRENDERING_POLICY
        // The whole client area is "glass" to DWM: what the panel does not draw is see-through.
        var glass = new Win32.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        Win32.DwmExtendFrameIntoClientArea(_hwnd, ref glass);
        Win32.ForceForeground(_hwnd);
        Activate();
        Input.Focus(FocusState.Programmatic);
        _hooks.RequestFlush();   // the palette has focus: replay anything typed since the trigger
        Enqueue("");   // empty query = most-used items
    }

    /// <param name="restoreFocus">
    /// Give focus back to the window the user was in (Esc / hotkey toggle). Not wanted when a result
    /// was launched (the launched app takes focus) or the user clicked elsewhere.
    /// </param>
    private int _showSeq;

    /// <summary>
    /// Start reading the window the palette opened over (uia.rs). Its selected text, if there is some,
    /// is on Home as a row; a read that finishes after Home was drawn brings the row in then, but only
    /// while nothing has been typed.
    /// </summary>
    private void ReadWindowBehind()
    {
        int show = ++_showSeq;
        var target = _settings.ReadWindow ? _previousForeground : IntPtr.Zero;
        Native.ReadWindow(target);
        if (target == IntPtr.Zero) return;
        Task.Run(() =>
        {
            if (Native.Selection(500) == null) return;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (show != _showSeq || !_visible || _view != View.Results || Input.Text.Length != 0) return;
                if (Items.Any(i => i.Does == "selection")) return;
                Enqueue("");
            });
        });
    }

    private void HidePalette(bool restoreFocus = false, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        App.Log($"HidePalette from {caller} restore={restoreFocus} visible={_visible} sinceShow={(DateTime.UtcNow - _shownAt).TotalMilliseconds:F0}ms");
        if (!_visible) return;
        CancelAsk();
        StopModelView();
        _visible = false;
        _lastHidden = DateTime.UtcNow;
        if (AnimationsOn)
        {
            // Fade out, then hide (FinishHide). Clicks fall through meanwhile.
            Root.IsHitTestVisible = false;
            _hideT = 0;
            _hideClock.Restart();
            EnsureFrames();
        }
        else
        {
            FinishHide();
        }

        // Hiding hands focus to whatever Windows picks (often the desktop), not the app you were in.
        var prev = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (restoreFocus && prev != IntPtr.Zero && prev != _hwnd && Win32.IsWindow(prev) && !Win32.IsIconic(prev))
        {
            Win32.SetForegroundWindow(prev);
        }
    }

    private void FinishHide()
    {
        _hideClock.Stop();
        if (_visible) return;   // reopened in the meantime
        _aw.Hide();
        CheckBubble();   // a reminder that came due while the palette was open shows now
    }

    private void OnActivated(object sender, WindowActivatedEventArgs e)
    {
        if (Environment.GetEnvironmentVariable("PALETTE_LOG") is not null)
        {
            var fg = Win32.GetForegroundWindow();
            App.Log($"activation {e.WindowActivationState} visible={_visible} sinceShow={(DateTime.UtcNow - _shownAt).TotalMilliseconds:F0}ms fg=0x{fg:X} me=0x{_hwnd:X}");
        }
        if (e.WindowActivationState != WindowActivationState.Deactivated || !_visible) return;

        double since = (DateTime.UtcNow - _shownAt).TotalMilliseconds;
        if (since > 400)
        {
            // The user clicked or switched elsewhere. (PALETTE_KEEP keeps it open, to look at it.)
            if (Environment.GetEnvironmentVariable("PALETTE_KEEP") is null) HidePalette(restoreFocus: false);
        }
        else if (_reassertTries < 3)
        {
            // Lost the foreground right after opening (the desktop or another window grabbed it
            // while we were being shown): take it back instead of leaving an unfocused palette.
            _reassertTries++;
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_visible) return;
                Win32.ForceForeground(_hwnd);
                Input.Focus(FocusState.Programmatic);
            });
        }
    }

    private readonly Settings _settings = Settings.Load();
    private readonly ShellHooks _hooks;

    private void HotkeyLoop()
    {
        // The shortcut from the settings (Alt+Space by default), falling back if another app owns it.
        _hotkeyThread = Win32.GetCurrentThreadId();
        RegisterHotkey();

        // Windows key, Win+S, the Copilot key and the search flyout.
        _hooked = _hooks.Install();
        UpdateTip();

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_HOTKEY)
            {
                App.Log($"WM_HOTKEY visible={_visible}");
                _hooks.OnHotkey();
            }
            else if (msg.message == WM_REHOTKEY)
            {
                OnRehotkey();
            }
            else if (msg.message == ShellHooks.WM_FLUSH)
            {
                _hooks.Flush();
            }
            else
            {
                Win32.DispatchMessage(ref msg);   // delivers the out-of-context WinEvent callbacks
            }
        }
    }

    // ------------------------------------------------------------------ input

    private void OnInputChanged(object sender, TextChangedEventArgs e)
    {
        if (_ignoreText != null && Input.Text == _ignoreText)
        {
            _ignoreText = null;
            return;
        }
        _ignoreText = null;
        CancelAsk();
        if (_view == View.Chat) return;   // what is typed is the message
        if (_view == View.Chats)
        {
            ShowChats(Input.Text);
            return;
        }
        if (_view == View.Actions)
        {
            if (_renaming) ShowRename();
            else ShowActions(Input.Text);
            return;
        }
        if (_view == View.Settings)
        {
            ShowSettings(Input.Text);
            return;
        }
        if (_view is View.AllApps or View.Done) SetView(View.Results, SearchPlaceholder);   // typing searches everything
        _previewOn = false;   // a new search starts without a preview; Tab opens it again
        _pvAuto = false;
        Enqueue(Input.Text);
    }

    private void Enqueue(string q)
    {
        _latestQuery = q;
        Interlocked.Increment(ref _queryVersion);
        _wake.Set();
    }

    private static bool Held(VirtualKey k) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(k).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int n = Items.Count;
        int sel = ResultsList.SelectedIndex;
        bool empty = Input.Text.Length == 0;
        bool canAct = _view is View.Results or View.AllApps && n > 0;
        switch (e.Key)
        {
            case VirtualKey.Tab when !Held(VirtualKey.Shift) && canAct:
                // A tap shows or hides the preview (on key up); held for a second, the actions.
                // Auto-repeat sends more key downs while it is held (KeyStatus.WasKeyDown is not
                // reliable for them), and each must not restart the second.
                if (!_tabDown) StartTabHold();
                e.Handled = true;
                return;
            case VirtualKey.Application when canAct:
            case VirtualKey.F10 when Held(VirtualKey.Shift) && canAct:
                if (sel < 0) ResultsList.SelectedIndex = sel = 0;
                EnterActions(sel);
                e.Handled = true;
                return;
            case VirtualKey.Tab when !Held(VirtualKey.Shift) && _view == View.Results && n == 0:
                StartChat(Input.Text);
                e.Handled = true;
                return;
            case VirtualKey.N when _view is View.Chat or View.Chats && Held(VirtualKey.Control):
                NewChat();
                e.Handled = true;
                return;
            case VirtualKey.H when _view == View.Chat && Held(VirtualKey.Control):
                LeaveChatView();
                ShowChats();
                e.Handled = true;
                return;
            case VirtualKey.Delete when _view == View.Chats && Held(VirtualKey.Shift):
                DeleteChatRow(sel);
                e.Handled = true;
                return;
            case VirtualKey.Back when empty && _view == View.Chats:
            case VirtualKey.Escape when _view == View.Chats:
            case VirtualKey.Tab when _view == View.Chats:
                StartChat("");
                e.Handled = true;
                return;
            case VirtualKey.Escape when _view == View.Chat && StopChat():
                // Esc while an answer is written stops it; Esc again leaves.
                e.Handled = true;
                return;
            case VirtualKey.PageUp when _view == View.Chat:
            case VirtualKey.PageDown when _view == View.Chat:
                PageChat(down: e.Key == VirtualKey.PageDown);
                e.Handled = true;
                return;
            case VirtualKey.Tab when _view == View.Chat:
            case VirtualKey.Escape when _view == View.Chat:
            case VirtualKey.Back when empty && _view == View.Chat:
                LeaveChat();
                e.Handled = true;
                return;
            case VirtualKey.Tab when _view == View.Actions:
                if (Held(VirtualKey.Shift)) LeaveActions();
                e.Handled = true;
                return;
            case VirtualKey.Back when empty && _view == View.Actions:
            case VirtualKey.Escape when _view == View.Actions:
                LeaveActions();
                e.Handled = true;
                return;
            case VirtualKey.Back when empty && _view == View.Settings:
            case VirtualKey.Escape when _view == View.Settings:
                StopCapture();
                ShowHome();
                e.Handled = true;
                return;
            case VirtualKey.Back when empty && _view == View.AllApps:
            case VirtualKey.Escape when _view == View.AllApps:
                ShowHome();
                e.Handled = true;
                return;
        }
        if (_view == View.Results && CalendarKey(e.Key))
        {
            e.Handled = true;
            return;
        }
        switch (e.Key)
        {
            case VirtualKey.Down when n > 0 && _view != View.Done:
                Expand();   // a condensed list opens up as the selection moves into it
                ResultsList.SelectedIndex = sel < 0 ? 0 : (sel + 1) % n;
                e.Handled = true;
                break;
            case VirtualKey.Up when n > 0 && _view != View.Done:
                Expand();
                ResultsList.SelectedIndex = sel < 0 ? 0 : (sel - 1 + n) % n;
                e.Handled = true;
                break;
            case VirtualKey.Right:
                // A slider row's level (Controls.cs), else into the selected folder (Browse.cs).
                e.Handled = TryNudgeSlider(sel, +1) || TryBrowseDown(sel);
                break;
            case VirtualKey.Left:
                e.Handled = TryNudgeSlider(sel, -1) || TryBrowseUp(sel);   // or to the folder the result is in
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (_view == View.Actions)
                {
                    RunAction(sel < 0 ? 0 : sel);
                }
                else if (Held(VirtualKey.Control) && sel >= 0)
                {
                    // Ctrl+Shift+Enter runs as administrator, Ctrl+Enter opens the file location
                    // (an app's too).
                    ActOrLaunch(sel, Held(VirtualKey.Shift) ? "admin" : "location");
                }
                else
                {
                    Launch(sel, reveal: false);
                }
                break;
            case VirtualKey.Escape when sel >= 0 && sel < n && Items[sel].Confirming:
                Items[sel].Confirming = false;   // Esc backs out of a shut down / restart / sign out
                UpdateOpenButton();
                e.Handled = true;
                break;
            case VirtualKey.Escape:
                HidePalette(restoreFocus: true);
                e.Handled = true;
                break;
            case VirtualKey.M or VirtualKey.W when Held(VirtualKey.Control) && sel >= 0 && sel < n && Items[sel].IsWindow:
                WindowAct(Items[sel], e.Key == VirtualKey.M ? "minimize" : "close");
                e.Handled = true;
                break;
        }
    }

    private const int TabHoldMs = 1000;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _tabHold;
    private bool _tabDown;                    // Tab went down in a view with rows and is not yet up

    private void StartTabHold()
    {
        _tabDown = true;
        if (_tabHold == null)
        {
            _tabHold = DispatcherQueue.CreateTimer();
            _tabHold.Interval = TimeSpan.FromMilliseconds(TabHoldMs);
            _tabHold.IsRepeating = false;
            _tabHold.Tick += (_, _) =>
            {
                if (!_tabDown) return;
                _tabDown = false;   // the key up that follows does nothing
                if (!_visible || _view is not (View.Results or View.AllApps) || Items.Count == 0) return;
                if (ResultsList.SelectedIndex < 0) ResultsList.SelectedIndex = 0;
                EnterActions(ResultsList.SelectedIndex);
            };
        }
        _tabHold.Stop();
        _tabHold.Start();
    }

    private void OnInputKeyUp(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Tab || !_tabDown) return;
        _tabDown = false;
        _tabHold?.Stop();
        e.Handled = true;
        if (_view is View.Results or View.AllApps) StartChat(Input.Text);   // a tap: the chat
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        // The chat's card is drawn in its row: a click in it (a reasoning trace, a step, a file link) is
        // for what was clicked, not the row, whose "launch" is sending what is typed. Enter and Send send.
        if (_view == View.Chat) return;
        Launch(Items.IndexOf((HitVm)e.ClickedItem), reveal: false);
    }

    /// <summary>Run action <paramref name="id"/> on a row, falling back to the plain behaviour where it does not apply.</summary>
    private void ActOrLaunch(int index, string id)
    {
        if (index >= 0 && index < Items.Count && Items[index].IsAnswer)
        {
            Launch(index, reveal: false);
            return;
        }
        if (Native.Act(_gen, index - AnswerOffset, id) == Native.ActResult.Hide) HidePalette(restoreFocus: false);
        else Launch(index, reveal: id == "location");
    }

    private const string KeyHintsCommand = "Palette: Hide or show key hints";

    private void Launch(int index, bool reveal)
    {
        if (_view == View.Actions)
        {
            RunAction(index);
            return;
        }
        if (_view == View.Settings)
        {
            RunSetting(index);
            return;
        }
        if (_view == View.Chat)
        {
            // While an answer is written, Enter leaves the typing alone (the button, Stop, stops it); while
            // it asks before acting, Enter on an empty field allows.
            if (_chat is { Busy: true })
            {
                if (_pendingApproval is { } ask && Input.Text.Trim().Length == 0) ask.Decide(true);
                return;
            }
            SendChat(Input.Text);
            return;
        }
        if (_view == View.Chats)
        {
            OpenChatRow(index);
            return;
        }
        if (_view == View.Done || index < 0 || index >= Items.Count) return;
        var vm = Items[index];
        if (vm.IsTimer)
        {
            TimerPrimary();   // start, pause, resume or restart (Timer.cs)
            return;
        }
        if (!reveal && vm.ConfirmVerb != null && !vm.Confirming)
        {
            // Shutting down, restarting or signing out asks first: Enter again (or the button) does it.
            vm.Confirming = true;
            UpdateOpenButton();
            return;
        }
        vm.Confirming = false;
        if (vm.Does == "selection" && !reveal)
        {
            UseSelection();
            return;
        }
        if (vm.RawKind == "Ask" && !reveal)
        {
            AskOrCopy(vm);
            return;
        }
        if (vm.RawKind == "Command" && vm.Title == KeyHintsCommand && !reveal)
        {
            // The footer is the shell's: flip it here, and say so on the Done line.
            _settings.KeyHints = !_settings.KeyHints;
            _settings.Save();
            ShowDone(_settings.KeyHints ? "Key hints shown" : "Key hints hidden");
            return;
        }
        if (vm.AnswerUrl != null && (!reveal || vm.IsAnswer))
        {
            OpenUrl(reveal ? vm.AnswerAltUrl ?? vm.AnswerUrl : vm.AnswerUrl);
            HidePalette(restoreFocus: false);
            return;
        }
        int engine = index - AnswerOffset;
        if (vm.IsToggle && !reveal)
        {
            // A quick setting changes in place: say so and stay open, with Undo.
            Native.Launch(_gen, engine, reveal: false);
            ShowDone(vm.Title);
            return;
        }
        if (vm.Does == "autostart" && !reveal) _autostart = !_autostart;   // the engine flips it; the switch follows
        LaunchHit(_gen, engine, vm.CopyText, reveal);
    }

    /// <summary>The selected text becomes the search: the field keeps it, to be added to.</summary>
    private void UseSelection()
    {
        if (Native.Selection(0) is { } text) Browse(text);
    }

    private void LaunchHit(ulong gen, int index, string? copyText, bool reveal)
    {
        if (copyText is { } text && !reveal)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        switch (Native.Launch(gen, index, reveal))
        {
            case Native.LaunchResult.Quit:
                QuitApp();
                break;
            case Native.LaunchResult.Hide:
                // A copied result (calculator) should leave you where you were, ready to paste; a
                // launched app takes focus by itself.
                HidePalette(restoreFocus: copyText is not null && !reveal);
                break;
            case Native.LaunchResult.AllApps:
                ShowAllApps();
                break;
            case Native.LaunchResult.UseSelection:
                UseSelection();
                break;
            case Native.LaunchResult.Settings:
                ShowSettings();
                break;
        }
    }

    // ----------------------------------------------------------------- search

    private void SearchLoop()
    {
        while (true)
        {
            _wake.WaitOne();
            int version = _queryVersion;
            string q = _latestQuery;
            var result = Native.Search(q);
            if (version != _queryVersion) continue;     // superseded while searching
            DispatcherQueue.TryEnqueue(() => ApplyResults(version, q, result));
        }
    }

    private void ApplyResults(int version, string query, Native.SearchResult r)
    {
        if (version != _queryVersion || !_visible || _view is View.Actions or View.Done or View.Settings or View.Chat or View.Chats) return;
        _gen = r.Gen;
        if (_undoPending)
        {
            RunUndo(r);
            return;
        }
        DropStaleAnswer(query);
        Volatile.Write(ref _instantAt, Environment.TickCount64);
        SyncRows(r.Hits);
        // Back from a result's actions, or after a row's control changed it: the same row again
        // (selected directly, so the selection card does not leave for the first row and return).
        int keep = _restoreSelection + AnswerOffset;
        if (_restoreSelection >= 0 && keep < Items.Count)
        {
            if (ResultsList.SelectedIndex != keep) ResultsList.SelectedIndex = keep;
            UpdateOpenButton();
        }
        else
        {
            SelectFirst();
        }
        _restoreSelection = -1;
        ReselectByTitle();
        UpdateCondensed();
        HoldHeight();
        Resize(recenter: false);
        UpdatePreview();
        RequestAnswer(query);
        if (!_quietRefresh) PlayListBounce();
        _quietRefresh = false;
        StartAutoPreview();

        // The instant tier is on screen; ask for meaning-based matches in the background.
        string q = query.Trim();
        // A folder being browsed lists only what is in it.
        if (q.Length >= 3 && !q.StartsWith('>') && !IsBrowsePath(q))
        {
            _semVersion = version;
            _semQuery = q;
            _semGen = r.Gen;
            _semWake.Set();
            _planWake.Set();
        }
    }

    /// <summary>
    /// Makes the list show exactly <paramref name="hits"/> while touching as little as possible:
    /// identical rows stay as they are, differing ones are replaced in place, the tail is trimmed or
    /// extended. Clearing and refilling on every keystroke makes the list visibly flicker.
    /// </summary>
    private void SyncRows(List<Native.Hit> hits)
    {
        int off = AnswerOffset;   // a web answer on top stays there
        for (int h = 0; h < hits.Count; h++)
        {
            int i = h + off;
            var fresh = new HitVm(hits[h]);
            // A row that changed is updated in place rather than replaced, so the list keeps its
            // selection (and does not replay the selection animation) on every keystroke.
            HitVm vm;
            if (i < Items.Count)
            {
                vm = Items[i];
                if (!vm.SameRow(fresh)) vm.CopyFrom(fresh);
            }
            else
            {
                vm = fresh;
                Items.Add(vm);
            }

            if (vm.Icon == null) FillIcon(vm);
        }
        while (Items.Count > hits.Count + off) Items.RemoveAt(Items.Count - 1);
        UpdateMediaRows();
        // A row that offers an answer about the window: the model starts reading it now (AskView.cs).
        if (hits.FirstOrDefault(h => h.Kind == "Ask") is { } ask) Native.AskWarm(ask.Title);
    }

    /// <summary>Appends rows for <paramref name="hits"/> (indices continue from the current list).</summary>
    private void AddRows(List<Native.Hit> hits)
    {
        foreach (var h in hits)
        {
            var vm = new HitVm(h);
            FillIcon(vm);
            Items.Add(vm);
        }
    }

    // ------------------------------------------------------------- semantic

    private int _semVersion;
    private string _semQuery = "";
    private ulong _semGen;
    private long _instantAt;                      // when the instant results last went on screen
    private const int RelatedDelayMs = 700;       // least time they stay alone before meaning matches join
    private readonly AutoResetEvent _semWake = new(false);

    /// <summary>
    /// Waits for typing to pause, then asks the engine for related results. Never in the keystroke
    /// path: the instant results are already on screen; these rows are appended below them.
    /// </summary>
    private void SemanticLoop()
    {
        ulong served = 0;   // a wake-up can outlive the request it announced; never serve a generation twice
        while (true)
        {
            _semWake.WaitOne();
            Thread.Sleep(180);
            int version = _semVersion;
            if (version != _queryVersion) continue;      // typing continued; its own wake-up follows
            ulong gen = _semGen;
            if (gen == served) continue;
            served = gen;
            var r = Native.Semantic(gen, _semQuery);
            if (r.Hits.Count == 0) continue;
            // Let the instant rows be read before the list grows again under them.
            long wait = RelatedDelayMs - (Environment.TickCount64 - Volatile.Read(ref _instantAt));
            if (wait > 0) Thread.Sleep((int)wait);
            if (version != _queryVersion) continue;      // typed on while waiting
            DispatcherQueue.TryEnqueue(() => AppendRelated(version, r));
        }
    }

    private void AppendRelated(int version, Native.Appended r)
    {
        // Drop the rows if the user kept typing or the list was rebuilt in the meantime.
        if (version != _queryVersion || !_visible || r.Gen != _gen || _view is View.Actions or View.Done or View.Settings or View.Chat or View.Chats) return;
        if (r.Replace)
        {
            // Strong meaning matches lead; the engine rebuilt the whole list around them.
            SyncRows(r.Hits);
            SelectFirst();
        }
        else if (r.Base == Items.Count - AnswerOffset)
        {
            AddRows(r.Hits);
        }
        else
        {
            return;
        }
        UpdateCondensed();
        HoldHeight();
        Resize(recenter: false);
        UpdatePreview();
        RequestPassages(version, _semQuery);
    }


    // ------------------------------------------------------------- plans from the model

    private readonly AutoResetEvent _planWake = new(false);

    /// <summary>
    /// Once typing pauses, asks the engine whether the language model reads the request as a plan
    /// ("get me outlook and teams", "make it quieter"); the engine only asks it when the rules did
    /// not. The plan row then leads the list.
    /// </summary>
    private void PlanLoop()
    {
        ulong served = 0;
        while (true)
        {
            _planWake.WaitOne();
            Thread.Sleep(150);
            int version = _semVersion;
            if (version != _queryVersion) continue;
            ulong gen = _semGen;
            if (gen == served) continue;
            served = gen;
            var r = Native.Plan(gen, _semQuery);
            if (r.Hits.Count == 0) continue;
            DispatcherQueue.TryEnqueue(() => ShowPlan(version, r));
        }
    }

    private void ShowPlan(int version, Native.Appended r)
    {
        if (version != _queryVersion || !_visible || r.Gen != _gen || _view is View.Actions or View.Done or View.Settings or View.Chat or View.Chats) return;
        // Enter runs the plan unless a row further down was chosen meanwhile.
        int sel = ResultsList.SelectedIndex;
        bool lead = sel <= AnswerOffset;
        SyncRows(r.Hits);
        // The plan went in above: the row that was chosen moved down one.
        ResultsList.SelectedIndex = Math.Min(lead ? AnswerOffset : sel + 1, Items.Count - 1);
        UpdateOpenButton();
        UpdateCondensed();
        HoldHeight();
        Resize(recenter: false);
        UpdatePreview();
        RequestPassages(version, _semQuery);
    }

    // ------------------------------------------------------------------ icons

    // Icons are fetched by key, not by result: a request still counts after the user has typed on,
    // and every row showing that key gets the picture when it lands.

    /// <summary>Give <paramref name="vm"/> its icon from the cache, or ask for it (UI thread).</summary>
    private void FillIcon(HitVm vm)
    {
        if (vm.IconKey is not { } key) return;
        if (_iconCache.TryGetValue(key, out var bmp)) vm.Icon = bmp;
        else if (_iconFailed.Contains(key)) return;
        else if (_iconPending.Add(key)) _iconJobs.Add(key);
        else if (_iconWarmOnly.Remove(key)) _iconJobs.Add(key);   // on screen now: jump the warm-up queue
    }

    private void IconLoop()
    {
        var queues = new[] { _iconJobs, _iconWarm };
        while (true)
        {
            BlockingCollection<string>.TakeFromAny(queues, out string? taken);   // on-screen rows first
            if (taken is not { } key || !_iconFetched.TryAdd(key, true)) continue;   // queued twice (promoted)
            byte[]? px = Native.Icon(key);
            DispatcherQueue.TryEnqueue(() => IconArrived(key, px));
        }
    }

    private void IconArrived(string key, byte[]? px)
    {
        _iconPending.Remove(key);
        _iconWarmOnly.Remove(key);
        if (px == null)
        {
            // Not for good: the shell can refuse an icon for a moment. The row asks again when it is
            // next shown (FillIcon), up to three times.
            int tries = _iconTries.GetValueOrDefault(key) + 1;
            _iconTries[key] = tries;
            _iconFetched.TryRemove(key, out _);
            if (tries >= 3) _iconFailed.Add(key);
            return;
        }
        var bmp = MakeBitmap(px);
        _iconCache[key] = bmp;
        foreach (var vm in Items)
        {
            if (vm.IconKey == key) vm.Icon = bmp;
        }
    }

    /// <summary>
    /// Fetch every app's icon in the background shortly after start, so rows show theirs at once
    /// instead of popping in. Retries while the catalog is still being built.
    /// </summary>
    private void WarmIcons()
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(attempt == 0 ? 1500 : 5000);
            var keys = Native.IconKeys();
            if (keys.Count < 20 && attempt < 5) continue;   // the catalog is not loaded yet
            DispatcherQueue.TryEnqueue(() =>
            {
                foreach (var key in keys)
                {
                    if (!_iconCache.ContainsKey(key) && !_iconFailed.Contains(key) && _iconPending.Add(key))
                    {
                        _iconWarmOnly.Add(key);
                        _iconWarm.Add(key);
                    }
                }
            });
            return;
        }
    }

    /// <param name="bgra">Premultiplied BGRA from the engine: the bitmap's own layout.</param>
    private static WriteableBitmap MakeBitmap(byte[] bgra)
    {
        int n = Native.IconSize;
        var wb = new WriteableBitmap(n, n);
        bgra.CopyTo(wb.PixelBuffer);
        wb.Invalidate();
        return wb;
    }
}
