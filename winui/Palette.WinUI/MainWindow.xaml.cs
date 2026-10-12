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

    public ObservableCollection<HitVm> Items { get; } = new();

    private bool _visible;
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

        SystemBackdrop = new DesktopAcrylicBackdrop();
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

        // Created off-screen and hidden; the hotkey brings it up.
        _aw.MoveAndResize(new RectInt32(-32000, -32000, 100, 100));
        _aw.Hide();

        _tray = new TrayIcon(
            toggle: () => DispatcherQueue.TryEnqueue(ToggleFromTray),
            isAutostart: Native.AutostartEnabled,
            setAutostart: enable => Native.SetAutostart(enable),
            reindex: Native.Reindex,
            quit: () => DispatcherQueue.TryEnqueue(QuitApp));

        new Thread(SearchLoop) { IsBackground = true, Name = "search" }.Start();
        new Thread(IconLoop) { IsBackground = true, Name = "icons" }.Start();
        new Thread(SemanticLoop) { IsBackground = true, Name = "semantic" }.Start();
        new Thread(HotkeyLoop) { IsBackground = true, Name = "hotkey" }.Start();
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
        double h = InputDip + (rows > 0 ? DividerDip + rows * RowDip + PadDip + FooterDip : 0);
        var vis = rows > 0 ? Visibility.Visible : Visibility.Collapsed;
        Divider.Visibility = vis;
        Footer.Visibility = vis;
        int w = (int)(WidthDip * s);
        _contentPx = (int)(h * s);       // the visible panel; the window itself keeps its full size
        // (growing a window shows unpainted black for a frame; a region just reveals more of it)
        double fullDip = InputDip + DividerDip + (crate_MaxRows) * RowDip + PadDip + FooterDip;
        int ph = (int)(fullDip * s);

        if (recenter)
        {
            Win32.GetCursorPos(out var c);
            var area = DisplayArea.GetFromPoint(new PointInt32(c.X, c.Y), DisplayAreaFallback.Nearest);
            var wa = area.WorkArea;
            ph = Math.Min(ph, wa.Height - (int)(wa.Height * 0.18) - (int)(24 * s));
            int x = wa.X + (wa.Width - w) / 2;
            int y = wa.Y + (int)(wa.Height * 0.18);
            _aw.MoveAndResize(new RectInt32(x, y, w, ph));
        }
        if (!_popRunning) ApplyShape(1);
    }

    private const int crate_MaxRows = 11;     // 8 instant + 3 related
    private int _contentPx;

    // ------------------------------------------------------------ shape + pop animation
    //
    // The window is clipped to a rounded rectangle with a window region. That gives corners far
    // rrounder than DWM's fixed 8 px and hides any system border; DWM's own rounding is switched
    // off. Opening "pops": the region and the content grow from 92% to full size with an ease-out
    // while the content fades in.

    private const double CornerDip = 22, PopMs = 230;
    private readonly System.Diagnostics.Stopwatch _popClock = new();
    private bool _popRunning;

    private void StartPop()
    {
        if (!AnimationsOn)
        {
            ApplyShape(1);
            return;
        }
        _popClock.Restart();
        ApplyShape(0);
        if (!_popRunning)
        {
            _popRunning = true;
            CompositionTarget.Rendering += OnPopFrame;
        }
    }

    private static readonly Windows.UI.ViewManagement.UISettings Ui = new();
    private static bool AnimationsOn
    {
        get { try { return Ui.AnimationsEnabled; } catch { return true; } }
    }

    private void OnPopFrame(object? sender, object e)
    {
        double t = Math.Min(1, _popClock.Elapsed.TotalMilliseconds / PopMs);
        ApplyShape(t);
        if (t >= 1)
        {
            CompositionTarget.Rendering -= OnPopFrame;
            _popRunning = false;
        }
    }

    /// <param name="t">0 = start of the pop, 1 = finished.</param>
    private void ApplyShape(double t)
    {
        double s = Scale;
        int w = _aw.Size.Width, h = Math.Min(_contentPx, _aw.Size.Height);
        if (w <= 0 || h <= 0) return;

        double ease = 1 - Math.Pow(1 - t, 4);                 // ease-out quartic
        double k = 0.92 + 0.08 * ease;                        // size, relative to the window
        double alpha = Math.Clamp(t / 0.45, 0, 1);

        double rw = w * k, rh = h * k;
        double left = (w - rw) / 2, top = (h - rh) * 0.35;    // anchored a little above centre
        int r = (int)(CornerDip * s * 2);                     // CreateRoundRectRgn takes the ellipse diameter
        // Inset a few pixels: Windows paints a white hairline along the very edge of this window
        // (the backdrop's frame highlight), inside the window and so not removable by styles alone.
        int inset = (int)Math.Ceiling(2 * s);
        var rgn = Win32.CreateRoundRectRgn((int)left + inset, (int)top + inset, (int)Math.Ceiling(left + rw) - inset, (int)Math.Ceiling(top + rh) - inset, r, r);
        Win32.SetWindowRgn(_hwnd, rgn, true);

        Root.Opacity = alpha;
        RootXf.CenterX = w / s / 2;
        RootXf.CenterY = h / s * 0.35;
        RootXf.ScaleX = RootXf.ScaleY = k;
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
        var fg = Win32.GetForegroundWindow();
        _previousForeground = fg != _hwnd ? fg : IntPtr.Zero;
        _reassertTries = 0;

        Input.Text = "";
        Items.Clear();
        _latestQuery = "";
        Resize(recenter: true);
        _shownAt = DateTime.UtcNow;
        _visible = true;
        FooterStatus.Text = Native.SemanticStatus();
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
        Activate();
        Input.Focus(FocusState.Programmatic);
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

        // Copilot key (Win+Shift+F23): RegisterHotKey cannot have it, a low-level hook can.
        _kbdProc = CopilotKeyHook;
        bool copilot = Win32.HookKeyboard(_kbdProc) != IntPtr.Zero;

        string label = used >= 0 ? names[used] : "";
        if (copilot) label = label.Length > 0 ? label + " or Copilot key" : "Copilot key";
        _tray.SetTooltip(label.Length > 0 ? $"Palette ({label})" : "Palette (no hotkey available; use this icon)");
        if (used < 0 && !copilot) return;

        while (Win32.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == Win32.WM_HOTKEY)
            {
                App.Log($"WM_HOTKEY visible={_visible}");
                ToggleFromHotkey();
            }
        }
    }

    private void ToggleFromHotkey() =>
        DispatcherQueue.TryEnqueue(() => { if (_visible) HidePalette(restoreFocus: true); else ShowPalette(); });

    private Win32.LowLevelKeyboardProc? _kbdProc; // referenced so the GC cannot collect the hook delegate
    private bool _copilotDown;

    private IntPtr CopilotKeyHook(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && System.Runtime.InteropServices.Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam).vkCode == Win32.VK_F23)
        {
            int m = (int)wParam;
            if (m == Win32.WM_KEYDOWN || m == Win32.WM_SYSKEYDOWN)
            {
                if (!_copilotDown)
                {
                    _copilotDown = true;
                    App.Log($"copilot key visible={_visible}");
                    Win32.MaskStartMenu();
                    ToggleFromHotkey();
                }
            }
            else
            {
                _copilotDown = false;
            }
            return (IntPtr)1; // swallow: Copilot / Microsoft 365 never sees it
        }
        return Win32.CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    // ------------------------------------------------------------------ input

    private void OnInputChanged(object sender, TextChangedEventArgs e) => Enqueue(Input.Text);

    private void Enqueue(string q)
    {
        _latestQuery = q;
        Interlocked.Increment(ref _queryVersion);
        _wake.Set();
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        int n = Items.Count;
        switch (e.Key)
        {
            case VirtualKey.Down when n > 0:
                ResultsList.SelectedIndex = (ResultsList.SelectedIndex + 1) % n;
                e.Handled = true;
                break;
            case VirtualKey.Up when n > 0:
                ResultsList.SelectedIndex = (ResultsList.SelectedIndex - 1 + n) % n;
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                bool ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
                    .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
                Launch(ResultsList.SelectedIndex, reveal: ctrl);
                e.Handled = true;
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
        if (index < 0 || index >= Items.Count) return;
        var vm = Items[index];
        if (vm.CopyText is { } text && !reveal)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
        }
        switch (Native.Launch(_gen, index, reveal))
        {
            case Native.LaunchResult.Quit:
                QuitApp();
                break;
            case Native.LaunchResult.Hide:
                // A copied result (calculator) should leave you where you were, ready to paste; a
                // launched app takes focus by itself.
                HidePalette(restoreFocus: vm.CopyText is not null && !reveal);
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
        if (version != _queryVersion || !_visible) return;
        _gen = r.Gen;
        SyncRows(r.Hits, r.Gen, query);
        ResultsList.SelectedIndex = Items.Count > 0 ? 0 : -1;
        Resize(recenter: false);

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
            var vm = i < Items.Count && Items[i].SameRow(fresh) ? Items[i] : fresh;

            // A kept row may still be waiting for an icon whose request belonged to an older
            // generation, so (re)queue anything that has none yet.
            if (vm.Icon == null && vm.IconKey is { } k)
            {
                if (_iconCache.TryGetValue(k, out var bmp)) vm.Icon = bmp;
                else need.Add((i, k));
            }
            if (ReferenceEquals(vm, fresh))
            {
                if (i < Items.Count) Items[i] = vm;
                else Items.Add(vm);
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

    // ------------------------------------------------------------- semantic

    private int _semVersion;
    private string _semQuery = "";
    private ulong _semGen;
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
            DispatcherQueue.TryEnqueue(() => AppendRelated(version, r));
        }
    }

    private void AppendRelated(int version, Native.Appended r)
    {
        // Drop the rows if the user kept typing or the list was rebuilt in the meantime.
        if (version != _queryVersion || !_visible || r.Gen != _gen) return;
        if (r.Replace)
        {
            // Strong meaning matches lead; the engine rebuilt the whole list around them.
            SyncRows(r.Hits, r.Gen, _semQuery);
            ResultsList.SelectedIndex = Items.Count > 0 ? 0 : -1;
        }
        else if (r.Base == Items.Count)
        {
            AddRows(r.Hits, r.Gen, "");
        }
        else
        {
            return;
        }
        Resize(recenter: false);
    }


    // ------------------------------------------------------------------ icons

    private void IconLoop()
    {
        foreach (var (gen, index, key) in _iconJobs.GetConsumingEnumerable())
        {
            byte[]? px = gen == _gen ? Native.Icon(gen, index) : null;
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
