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
    // PadDip = list margins (6+6) + slack; the divider (1) and footer (36) are added when rows exist.
    private const double WidthDip = 680, InputDip = 64, RowDip = 52, PadDip = 16, DividerDip = 1, FooterDip = 36;

    private readonly AppWindow _aw;
    private readonly IntPtr _hwnd;
    private readonly ShadowWindow _shadow = new();

    public ObservableCollection<HitVm> Items { get; } = new();

    private volatile bool _visible;           // read by the hook thread too
    private DateTime _shownAt = DateTime.MinValue;
    private ulong _gen;                       // generation of the results on screen
    private int _queryVersion;                // bumped on every keystroke
    private string _latestQuery = "";
    private readonly AutoResetEvent _wake = new(false);

    private readonly Dictionary<string, WriteableBitmap> _iconCache = new();
    private readonly HashSet<string> _iconPending = new();
    private readonly BlockingCollection<(ulong gen, int index, string key)> _iconJobs = new();

    public MainWindow()
    {
        InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        _aw = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(_hwnd));

        SystemBackdrop = new GlassBackdrop();
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

        Activated += OnActivated;
        Input.Loaded += (_, _) => HideClearButton();
        ResultsList.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnListWheel), handledEventsToo: true);

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
            options: new TrayIcon.Option[]
            {
                new("Open with the Windows key", () => _settings.WinKeyOpens, on => { _settings.WinKeyOpens = on; _settings.Save(); }),
                new("Replace Windows Search (Win+S, taskbar search)", () => _settings.ReplaceSearch, on => { _settings.ReplaceSearch = on; _settings.Save(); }),
                new("Hide the taskbar search box", Settings.TaskbarSearchHidden, on => _settings.ApplyTaskbarSearch(on)),
            });

        new Thread(SearchLoop) { IsBackground = true, Name = "search" }.Start();
        new Thread(IconLoop) { IsBackground = true, Name = "icons" }.Start();
        new Thread(SemanticLoop) { IsBackground = true, Name = "semantic" }.Start();
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
        var vis = rows > 0 ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = vis;
        Footer.Visibility = vis;
        double need = rows > 0 ? Math.Min(rows, MaxRows) * RowDip + PadDip : 0;   // more rows scroll
        // While typing the list only grows: result counts change with every keystroke, and Related
        // rows arrive a moment after each pause, so following them exactly makes the panel bounce.
        // It shrinks to fit once typing settles (HoldHeight).
        _listBodyDip = _holdHeight && rows > 0 ? Math.Max(need, _listBodyDip) : need;
        // Rows arriving or leaving while the list is up (Related rows, a narrower match) glide the
        // panel to its new height; a jump would show the window shape a frame ahead of the rows.
        if (_visible && AnimationsOn && _listShownDip > 0 && _listBodyDip > 0 && _listShownDip != _listBodyDip) EnsureFrames();
        else _listShownDip = _listBodyDip;

        if (recenter)
        {
            // The window keeps one full size (list + preview, all rows); the region shows the
            // visible panel. Growing a window shows unpainted black for a frame; a region just
            // reveals more of it.
            Win32.GetCursorPos(out var c);
            var area = DisplayArea.GetFromPoint(new PointInt32(c.X, c.Y), DisplayAreaFallback.Nearest);
            var wa = area.WorkArea;
            // The client area (XAML) is smaller than the window by what is left of its frame, and the
            // acrylic backdrop is laid out from the window's corner rather than the client's, so it
            // stops that offset short of the client's right and bottom edges: add both.
            var (ox, oy, cw, ch) = Win32.ClientArea(_hwnd);
            int ncw = Math.Max(0, _aw.Size.Width - cw) + ox, nch = Math.Max(0, _aw.Size.Height - ch) + oy;
            int w = (int)(WidthDip * s) + ncw;
            int ph = (int)((InputDip + DividerDip + MaxRows * RowDip + PadDip + PreviewDip + FooterDip) * s) + nch;
            // Anchored to the bottom of the work area (above the taskbar), with the panel growing upward.
            int gap = (int)(24 * s);
            ph = Math.Min(ph, wa.Height - 2 * gap);
            int x = wa.X + (wa.Width - w) / 2;
            int y = wa.Y + wa.Height - gap - ph;
            _aw.MoveAndResize(new RectInt32(x, y, w, ph));
        }
        if (!_animating) ApplyShape();
    }

    private const int MaxRows = 9;            // rows the panel shows; longer lists (All apps) scroll
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
    /// Select the first row once something is typed, so Enter opens the best match. With an empty
    /// query (the most-used list on open) nothing is selected until an arrow key picks a row.
    /// Touches the selection only when it is not already as wanted.
    /// </summary>
    private void SelectFirst()
    {
        int want = Items.Count > 0 && !string.IsNullOrWhiteSpace(Input.Text) ? 0 : -1;
        if (ResultsList.SelectedIndex != want) ResultsList.SelectedIndex = want;
    }
    private double _listBodyDip;              // height the result rows need
    private double _listShownDip;             // height the list currently has on screen (follows _listBodyDip)

    // ------------------------------------------------------------ shape + animations
    //
    // The window is clipped to a rounded rectangle with a window region. That gives corners far
    // rounder than DWM's fixed 8 px and hides any system border; DWM's own rounding is switched
    // off. Opening "pops": the window fades in while the content grows from 92% to full size with
    // an ease-out. Selecting a file opens its preview under its row: the rows below slide down and
    // the panel grows, and both reverse when the selection moves to something else.

    private const double CornerDip = 22, PopMs = 230, PreviewMs = 200, ListMs = 45;
    private const double PreviewDip = 234;    // the inline preview, with its gaps
    private readonly System.Diagnostics.Stopwatch _popClock = new();
    private readonly System.Diagnostics.Stopwatch _frameClock = System.Diagnostics.Stopwatch.StartNew();
    private double _popT = 1;                 // pop progress, 0..1
    private double _pvU, _pvTarget;           // preview progress (linear) and where it is heading
    private double _lastFrameMs;
    private bool _animating;

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
        _pvTarget = target;
        if (!AnimationsOn || !_visible)
        {
            _pvU = target;
            ApplyShape();
            return;
        }
        if (_pvU != target) EnsureFrames();
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
        }
        if (_listShownDip != _listBodyDip)
        {
            _listShownDip += (_listBodyDip - _listShownDip) * (1 - Math.Exp(-dt / ListMs));
            if (Math.Abs(_listBodyDip - _listShownDip) < 0.5) _listShownDip = _listBodyDip;
            else busy = true;
        }

        ApplyShape();
        if (!busy)
        {
            CompositionTarget.Rendering -= OnFrame;
            _animating = false;
        }
    }

    private void ApplyShape()
    {
        double s = Scale;
        // Shapes are computed in client (XAML) pixels, then shifted by the client area's offset
        // inside the window for the region, which is in window coordinates.
        var (ox, oy, w, wh) = Win32.ClientArea(_hwnd);
        if (w <= 0 || wh <= 0) return;

        // Preview: ease-in-out cubic on its linear progress.
        double u = _pvU;
        double p = u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;
        bool rows = Items.Count > 0;
        double pvDip = rows ? PreviewDip * p : 0;
        double bodyDip = rows ? _listShownDip + pvDip : 0;
        Body.Height = bodyDip;
        // The preview sits under its row; the rows below slide down to make room. It is clipped to
        // the room made so far, so it unrolls rather than overlapping the rows below.
        ShiftRows(pvDip);
        // The list is flipped, so "under" a row (in list order) is visually above it: the pane is
        // measured from the bottom of the body and unrolls upward from the row.
        PreviewPane.Margin = new Thickness(14, 0, 14, 6 + (_pvRow + 1) * RowDip + 4);
        double room = Math.Max(0, pvDip - 8);
        PreviewPane.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 224 - room, 4096, room) };
        PreviewPane.Opacity = p * p;
        PreviewPane.Visibility = p > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Pop: the whole window fades in (a window-level alpha that DWM applies for free) while the
        // content grows from 92% with an ease-out quartic on the GPU. The glass, rim and shadow
        // keep their final shape throughout: reshaping a window every frame makes DWM recompose
        // it, which cost a third of each frame at 90-120 Hz.
        double ease = 1 - Math.Pow(1 - _popT, 4);
        double k = 0.92 + 0.08 * ease;
        double alpha = Math.Clamp(_popT / 0.45, 0, 1);

        double h = Math.Min((InputDip + (rows ? DividerDip + bodyDip + FooterDip : 0)) * s, wh);
        double x0 = 0, cx = WidthDip / 2 * s;
        // Outer edges of the panel, in window pixels. The region is cut a pixel inside them, and the
        // shadow window paints an antialiased rim over the region's stair-stepped edge.
        // The panel is anchored to the bottom of the client area and grows upward.
        int rl = (int)Math.Round(x0) + ox, rr = (int)Math.Round(2 * cx - x0) + ox, rb = wh + oy;
        int rt = rb - (int)Math.Round(h);
        double radius = CornerDip * s;
        if ((rl, rt, rr, rb) != _region)
        {
            _region = (rl, rt, rr, rb);
            int i = 1, d = (int)Math.Round(2 * (radius - i));     // CreateRoundRectRgn takes the ellipse diameter
            // CreateRoundRectRgn leaves out one pixel more on the right and bottom than its arguments say.
            var rgn = Win32.CreateRoundRectRgn(rl + i, rt + i, rr - i + 1, rb - i + 1, d, d);
            Win32.SetWindowRgn(_hwnd, rgn, true);
        }
        _shadow.Draw(new RectInt32(_aw.Position.X, _aw.Position.Y, _aw.Size.Width, _aw.Size.Height), rl, rt, rr, rb, radius, alpha, s,
            Root.ActualTheme == ElementTheme.Dark);

        byte a = (byte)Math.Round(alpha * 255);
        if (a != _windowAlpha)
        {
            _windowAlpha = a;
            Win32.SetWindowAlpha(_hwnd, a);
        }
        RootXf.CenterX = cx / s;
        RootXf.CenterY = (wh - h * 0.35) / s;
        RootXf.ScaleX = RootXf.ScaleY = k;
    }

    private double _rowShift;                 // how far the rows under the preview are pushed down

    /// <summary>Push the rows below the preview's row down by <paramref name="dip"/>.</summary>
    private void ShiftRows(double dip)
    {
        _rowShift = dip;
        for (int i = 0; i < Items.Count; i++)
        {
            if (ResultsList.ContainerFromIndex(i) is UIElement c) ShiftRow(c, i);
        }
    }

    private void ShiftRow(UIElement c, int index)
    {
        double y = index > _pvRow ? _rowShift : 0;
        if (c.RenderTransform is TranslateTransform t) t.Y = y;
        else if (y != 0) c.RenderTransform = new TranslateTransform { Y = y };
    }

    /// <summary>Rows realised later (the list grew) take the current shift too.</summary>
    private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) =>
        ShiftRow(args.ItemContainer, args.ItemIndex);

    private (int, int, int, int) _region;     // the window region last set
    private int _windowAlpha = -1;            // the window alpha last set

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
        _shadow.Dispose();
        Environment.Exit(0);
    }

    private void ShowPalette()
    {
        App.Log("ShowPalette");
        var fg = Win32.GetForegroundWindow();
        _previousForeground = fg != _hwnd ? fg : IntPtr.Zero;
        _reassertTries = 0;

        SetView(View.Results, SearchPlaceholder);
        SetInputText("");
        Items.Clear();
        _latestQuery = "";
        ResetPreview();
        Resize(recenter: true);
        _shownAt = DateTime.UtcNow;
        _visible = true;
        // Reads a status file: keep disk access out of the show path.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            string status = Native.SemanticStatus();
            DispatcherQueue.TryEnqueue(() => FooterStatus.Text = status);
        });
        StartPop();
        _aw.Show();
        // The presenter can put its frame styles back on Show; a frame is what Windows draws the
        // white outline for, so take it off again every time.
        var (st, ex) = Win32.StripFrame(_hwnd);
        App.Log($"styles before strip: 0x{st:X} ex 0x{ex:X}");
        // DWM can reset these when a window is hidden and shown again: re-apply every time so
        // the popup keeps its rounded corners and never gets the square 1 px system border.
        int round = 1, none = unchecked((int)0xFFFFFFFE);                   // 1 = DWMWCP_DONOTROUND (the region rounds)
        Win32.DwmSetWindowAttribute(_hwnd, 33, ref round, sizeof(int));     // DWMWA_WINDOW_CORNER_PREFERENCE
        Win32.DwmSetWindowAttribute(_hwnd, 34, ref none, sizeof(int));      // DWMWA_BORDER_COLOR = none
        int ncOff = 1;                                                      // DWMNCRP_DISABLED: no frame, shadow or hairline
        Win32.DwmSetWindowAttribute(_hwnd, 2, ref ncOff, sizeof(int));      // DWMWA_NCRENDERING_POLICY
        Win32.ForceForeground(_hwnd);
        _shadow.ShowOver(_hwnd);
        Activate();
        Input.Focus(FocusState.Programmatic);
        _hooks.RequestFlush();   // the palette has focus: replay anything typed since the trigger
        Enqueue("");   // empty query = most-used items
    }

    /// <param name="restoreFocus">
    /// Give focus back to the window the user was in (Esc / hotkey toggle). Not wanted when a result
    /// was launched (the launched app takes focus) or the user clicked elsewhere.
    /// </param>
    private void HidePalette(bool restoreFocus = false, [System.Runtime.CompilerServices.CallerMemberName] string caller = "")
    {
        App.Log($"HidePalette from {caller} restore={restoreFocus} visible={_visible} sinceShow={(DateTime.UtcNow - _shownAt).TotalMilliseconds:F0}ms");
        if (!_visible) return;
        _visible = false;
        _lastHidden = DateTime.UtcNow;
        _aw.Hide();
        _shadow.Hide();

        // Hiding hands focus to whatever Windows picks (often the desktop), not the app you were in.
        var prev = _previousForeground;
        _previousForeground = IntPtr.Zero;
        if (restoreFocus && prev != IntPtr.Zero && prev != _hwnd && Win32.IsWindow(prev) && !Win32.IsIconic(prev))
        {
            Win32.SetForegroundWindow(prev);
        }
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
            HidePalette(restoreFocus: false);   // the user clicked or switched elsewhere
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
        // Alt+Space, falling back if another app owns it.
        uint[] combos = { Win32.MOD_ALT, Win32.MOD_CONTROL, Win32.MOD_CONTROL | Win32.MOD_ALT };
        string[] names = { "Alt+Space", "Ctrl+Space", "Ctrl+Alt+Space" };
        int used = -1;
        for (int i = 0; i < combos.Length && used < 0; i++)
        {
            if (Win32.RegisterHotKey(IntPtr.Zero, 1, combos[i] | Win32.MOD_NOREPEAT, Win32.VK_SPACE)) used = i;
        }

        // Windows key, Win+S, the Copilot key and the search flyout.
        bool hooked = _hooks.Install();

        string label = used >= 0 ? names[used] : "";
        if (hooked) label = label.Length > 0 ? label + ", Windows key or Copilot key" : "Windows key or Copilot key";
        _tray.SetTooltip(label.Length > 0 ? $"Palette ({label})" : "Palette (no hotkey available; use this icon)");

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_HOTKEY)
            {
                App.Log($"WM_HOTKEY visible={_visible}");
                _hooks.OnHotkey();
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
        if (_view == View.Actions)
        {
            ShowActions(Input.Text);
            return;
        }
        if (_view == View.AllApps) SetView(View.Results, SearchPlaceholder);   // typing searches everything
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
        switch (e.Key)
        {
            case VirtualKey.Tab when !Held(VirtualKey.Shift) && _view != View.Actions && n > 0:
            case VirtualKey.Application when _view != View.Actions && n > 0:
            case VirtualKey.F10 when Held(VirtualKey.Shift) && _view != View.Actions && n > 0:
                if (sel < 0) ResultsList.SelectedIndex = sel = 0;
                EnterActions(sel);
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
            case VirtualKey.Back when empty && _view == View.AllApps:
            case VirtualKey.Escape when _view == View.AllApps:
                ShowHome();
                e.Handled = true;
                return;
        }
        switch (e.Key)
        {
            case VirtualKey.Down when n > 0:
                // The list is drawn upside down: index 0 is the row next to the search field, so
                // Up (visually up) goes to a higher index and Down comes back toward the field.
                ResultsList.SelectedIndex = ResultsList.SelectedIndex < 0 ? 0 : (ResultsList.SelectedIndex - 1 + n) % n;
                e.Handled = true;
                break;
            case VirtualKey.Up when n > 0:
                ResultsList.SelectedIndex = ResultsList.SelectedIndex < 0 ? 0 : (ResultsList.SelectedIndex + 1) % n;
                e.Handled = true;
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
                    // (an app's too); either falls back to the plain behaviour where it does not apply.
                    string id = Held(VirtualKey.Shift) ? "admin" : "location";
                    if (Native.Act(_gen, sel, id) == Native.ActResult.Hide) HidePalette(restoreFocus: false);
                    else Launch(sel, reveal: id == "location");
                }
                else
                {
                    Launch(sel, reveal: false);
                }
                break;
            case VirtualKey.Escape:
                HidePalette(restoreFocus: true);
                e.Handled = true;
                break;
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        Launch(Items.IndexOf((HitVm)e.ClickedItem), reveal: false);
    }

    private void Launch(int index, bool reveal)
    {
        if (_view == View.Actions)
        {
            RunAction(index);
            return;
        }
        if (index < 0 || index >= Items.Count) return;
        LaunchHit(_gen, index, Items[index].CopyText, reveal);
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
        if (version != _queryVersion || !_visible || _view == View.Actions) return;
        _gen = r.Gen;
        SyncRows(r.Hits, r.Gen, query);
        SelectFirst();
        if (_restoreSelection >= 0)
        {
            // Back from a result's actions: the same row again.
            if (_restoreSelection < Items.Count) ResultsList.SelectedIndex = _restoreSelection;
            _restoreSelection = -1;
        }
        HoldHeight();
        Resize(recenter: false);
        UpdatePreview();

        // The instant tier is on screen; ask for meaning-based matches in the background.
        string q = query.Trim();
        if (q.Length >= 3 && !q.StartsWith('>'))
        {
            _semVersion = version;
            _semQuery = q;
            _semGen = r.Gen;
            _semWake.Set();
        }
    }

    /// <summary>
    /// Makes the list show exactly <paramref name="hits"/> while touching as little as possible:
    /// identical rows stay as they are, differing ones are replaced in place, the tail is trimmed or
    /// extended. Clearing and refilling on every keystroke makes the list visibly flicker.
    /// </summary>
    private void SyncRows(List<Native.Hit> hits, ulong gen, string query)
    {
        var need = new List<(int index, string key)>();
        for (int i = 0; i < hits.Count; i++)
        {
            var fresh = new HitVm(hits[i], query);   // new each time: bold highlighting follows the query
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

            // A kept row may still be waiting for an icon whose request belonged to an older
            // generation, so (re)queue anything that has none yet.
            if (vm.Icon == null && vm.IconKey is { } k)
            {
                if (_iconCache.TryGetValue(k, out var bmp)) vm.Icon = bmp;
                else need.Add((i, k));
            }
        }
        while (Items.Count > hits.Count) Items.RemoveAt(Items.Count - 1);

        foreach (var (index, key) in need)
        {
            if (_iconPending.Add(key)) _iconJobs.Add((gen, index, key));
        }
    }

    /// <summary>Appends rows for <paramref name="hits"/> (indices continue from the current list).</summary>
    private void AddRows(List<Native.Hit> hits, ulong gen, string query)
    {
        var need = new List<(int index, string key)>();
        foreach (var h in hits)
        {
            var vm = new HitVm(h, query);
            if (vm.IconKey is { } k)
            {
                if (_iconCache.TryGetValue(k, out var bmp)) vm.Icon = bmp;
                else need.Add((Items.Count, k));
            }
            Items.Add(vm);
        }
        foreach (var (index, key) in need)
        {
            if (_iconPending.Add(key)) _iconJobs.Add((gen, index, key));
        }
    }

    /// <summary>Rows in front of the list, in order (icons are fetched by key, so the shift is fine).</summary>
    private void InsertRows(List<Native.Hit> hits, ulong gen)
    {
        for (int i = 0; i < hits.Count; i++)
        {
            var vm = new HitVm(hits[i], "");
            if (vm.IconKey is { } k)
            {
                if (_iconCache.TryGetValue(k, out var bmp)) vm.Icon = bmp;
                else if (_iconPending.Add(k)) _iconJobs.Add((gen, i, k));
            }
            Items.Insert(i, vm);
        }
    }

    // ------------------------------------------------------------- semantic

    private int _semVersion;
    private string _semQuery = "";
    private ulong _semGen;
    private readonly AutoResetEvent _semWake = new(false);

    /// <summary>
    /// Waits for typing to pause, then asks the engine for related results. Never in the keystroke
    /// path: the instant results are already on screen; these rows join them next to the search box.
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
            DispatcherQueue.TryEnqueue(() => AppendRelated(version, r));
        }
    }

    private void AppendRelated(int version, Native.Appended r)
    {
        // Drop the rows if the user kept typing or the list was rebuilt in the meantime.
        if (version != _queryVersion || !_visible || r.Gen != _gen || _view == View.Actions) return;
        if (r.Replace)
        {
            // Strong meaning matches lead; the engine rebuilt the whole list around them.
            SyncRows(r.Hits, r.Gen, _semQuery);
            SelectFirst();
        }
        else if (r.Prepend)
        {
            // Next to the search box, ahead of the name matches; the highlight stays on its row.
            var selected = ResultsList.SelectedItem;
            InsertRows(r.Hits, r.Gen);
            if (selected != null) ResultsList.SelectedItem = selected;
        }
        else if (r.Base == Items.Count)
        {
            AddRows(r.Hits, r.Gen, "");
        }
        else
        {
            return;
        }
        HoldHeight();
        Resize(recenter: false);
        UpdatePreview();
    }


    // ------------------------------------------------------------------ icons

    private void IconLoop()
    {
        foreach (var (gen, index, key) in _iconJobs.GetConsumingEnumerable())
        {
            byte[]? px = gen == _gen ? Native.Icon(gen, index, key) : null;
            DispatcherQueue.TryEnqueue(() =>
            {
                _iconPending.Remove(key);
                if (px == null) return;
                var bmp = MakeBitmap(px);
                _iconCache[key] = bmp;
                foreach (var vm in Items)
                {
                    if (vm.IconKey == key) vm.Icon = bmp;
                }
            });
        }
    }

    private static WriteableBitmap MakeBitmap(byte[] rgba)
    {
        int n = Native.IconSize;
        var wb = new WriteableBitmap(n, n);
        var bgra = new byte[rgba.Length];
        for (int i = 0; i < rgba.Length; i += 4)
        {
            bgra[i] = rgba[i + 2];
            bgra[i + 1] = rgba[i + 1];
            bgra[i + 2] = rgba[i];
            bgra[i + 3] = rgba[i + 3];
        }
        bgra.CopyTo(wb.PixelBuffer);
        wb.Invalidate();
        return wb;
    }
}
