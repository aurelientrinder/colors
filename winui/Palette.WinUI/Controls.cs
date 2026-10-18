using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Media.Control;

namespace Palette;

/// <summary>
/// Controls on the rows themselves, to change a setting without leaving the list:
/// <list type="bullet">
/// <item>a switch on on/off settings (Wi-Fi, Bluetooth, dark mode, do not disturb, and Palette's
/// own "Run at startup" and key hints);</item>
/// <item>a slider under the volume and brightness rows, following the drag (Left and Right move it
/// by 5 with the caret at the end of the search);</item>
/// <item>previous, play/pause and next on media requests, with what is playing as the subtitle.</item>
/// </list>
/// Enter still does what the row says. Changes run off the UI thread (radios and WMI take a few
/// ms to tens of ms); a slider's moves are coalesced so only the latest level is sent.
/// </summary>
public sealed partial class MainWindow
{
    private const int SliderStep = 5;
    private bool? _autostart;   // read once at startup (schtasks is slow), then kept in step
    private bool _quietRefresh; // the next results come from a control's change: no bounce

    private void InitControls()
    {
        // Row colours are picked for the theme (HitVm.ToneBrush): bright on dark, deeper on light.
        HitVm.Dark = Root.ActualTheme == ElementTheme.Dark;
        Root.ActualThemeChanged += (_, _) =>
        {
            HitVm.Dark = Root.ActualTheme == ElementTheme.Dark;
            foreach (var vm in Items) vm.CopyFrom(vm);   // rebinds every property, brushes included
            if (ResultsList.SelectedIndex is >= 0 and var i && i < Items.Count) ApplyHighlightTone(Items[i]);
        };
        HitVm.LocalControl = h => h.Kind != "Command" ? null : h.Does switch
        {
            "autostart" when _autostart is { } on => new Native.Control("switch", "autostart", on, 0),
            "keyhints" => new Native.Control("switch", "keyhints", _settings.KeyHints, 0),
            _ => null,
        };
        Task.Run(() =>
        {
            bool on = Native.AutostartEnabled();
            DispatcherQueue.TryEnqueue(() => _autostart = on);
        });
    }

    private HitVm? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as HitVm;

    /// <summary>Select the row a control belongs to, so Enter and the footer follow it.</summary>
    private void SelectRow(HitVm vm)
    {
        int i = Items.IndexOf(vm);
        if (i >= 0 && ResultsList.SelectedIndex != i) ResultsList.SelectedIndex = i;
    }

    /// <summary>
    /// Ask the engine again for the same search, keeping the selected row: a change made from a
    /// control rewrites rows ("Turn Wi-Fi off" becomes "Turn Wi-Fi on").
    /// </summary>
    private void RefreshAfterControl(HitVm vm)
    {
        if (_view != View.Results) return;
        int i = Items.IndexOf(vm);
        if (i >= AnswerOffset) _restoreSelection = i - AnswerOffset;
        _quietRefresh = true;
        Enqueue(Input.Text);
    }

    // ------------------------------------------------------------ switches

    private void OnRowSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch ts || RowOf(sender) is not { ControlKey: { } key } vm) return;
        if (ts.IsOn == vm.SwitchOn) return;   // the binding catching up, not a click
        ApplySwitch(vm, key, ts.IsOn);
    }

    /// <summary>A row's switch was clicked, or Enter was pressed on its row (settings list).</summary>
    private void ApplySwitch(HitVm vm, string key, bool on)
    {
        vm.TouchedAt = Environment.TickCount64;
        vm.SwitchOn = on;
        SelectRow(vm);

        switch (key)
        {
            case "keyhints":
                _settings.KeyHints = on;
                _settings.Save();
                UpdateOpenButton();
                return;
            case "autostart":
                _autostart = on;
                Task.Run(() =>
                {
                    bool ok = Native.SetAutostart(on);
                    bool now = ok ? on : Native.AutostartEnabled();
                    DispatcherQueue.TryEnqueue(() =>
                    {
                        _autostart = now;
                        vm.TouchedAt = 0;
                        vm.SwitchOn = now;
                    });
                });
                return;
        }

        if (_view == View.Settings && ApplySetting(key, on))
        {
            vm.TouchedAt = 0;
            return;
        }

        Task.Run(() =>
        {
            Native.Quick($"{key}:{(on ? "on" : "off")}");
            var now = Native.QuickControl(key);
            DispatcherQueue.TryEnqueue(() =>
            {
                // Show what the setting really is (a radio can refuse), then rewrite the rows.
                vm.TouchedAt = 0;
                if (now != null) vm.SwitchOn = now.On;
                RefreshAfterControl(vm);
            });
        });
    }

    // ------------------------------------------------------------ sliders

    private readonly object _levelLock = new();
    private readonly Dictionary<string, int> _levelPending = new();
    private readonly AutoResetEvent _levelWake = new(false);
    private Thread? _levelThread;

    private void OnRowSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RowOf(sender) is not { } vm || Math.Abs(e.NewValue - vm.SliderValue) < 0.5) return;
        SetLevel(vm, (int)Math.Round(e.NewValue));
        SelectRow(vm);
    }

    /// <summary>Move a row's slider to <paramref name="value"/> and send it (latest wins).</summary>
    private void SetLevel(HitVm vm, int value)
    {
        if (vm.ControlKey is not { } key) return;
        value = Math.Clamp(value, 0, 100);
        vm.TouchedAt = Environment.TickCount64;
        if (key == "volume" && value > 0) vm.Muted = false;   // setting a level unmutes, as Windows' slider does
        vm.SliderValue = value;
        vm.ShowLevel(value);
        lock (_levelLock) _levelPending[key] = value;
        if (_levelThread == null)
        {
            _levelThread = new Thread(LevelLoop) { IsBackground = true, Name = "Palette levels" };
            _levelThread.Start();
        }
        _levelWake.Set();
    }

    private void LevelLoop()
    {
        while (true)
        {
            _levelWake.WaitOne();
            while (true)
            {
                KeyValuePair<string, int> next;
                lock (_levelLock)
                {
                    if (_levelPending.Count == 0) break;
                    next = _levelPending.First();
                    _levelPending.Remove(next.Key);
                }
                Native.Quick($"{next.Key}:{next.Value}");
            }
        }
    }

    /// <summary>The glyph before a slider: mutes and unmutes the volume.</summary>
    private void OnSliderGlyphClick(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { ControlKey: "volume" } vm) return;
        bool mute = !vm.Muted;
        vm.TouchedAt = Environment.TickCount64;
        vm.Muted = mute;
        SelectRow(vm);
        Task.Run(() =>
        {
            Native.Quick($"mute:{(mute ? "on" : "off")}");
            DispatcherQueue.TryEnqueue(() =>
            {
                vm.TouchedAt = 0;
                RefreshAfterControl(vm);   // "Mute" becomes "Unmute"
            });
        });
    }

    /// <summary>Left and Right on a slider row (caret at the end of the search): down or up a step.</summary>
    private bool TryNudgeSlider(int sel, int direction)
    {
        if (!CanBrowseKey || sel < 0 || sel >= Items.Count || !Items[sel].HasSlider) return false;
        var vm = Items[sel];
        int now = (int)Math.Round(vm.SliderValue);
        // From an off-grid level, the first step lands on the grid.
        int to = direction > 0 ? (now / SliderStep + 1) * SliderStep : ((now + SliderStep - 1) / SliderStep - 1) * SliderStep;
        SetLevel(vm, to);
        return true;
    }

    // ------------------------------------------------------------ media

    private GlobalSystemMediaTransportControlsSessionManager? _media;

    private async Task<GlobalSystemMediaTransportControlsSession?> MediaSession()
    {
        try
        {
            _media ??= await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            return _media.GetCurrentSession();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Show what is playing on every media row in the list.</summary>
    private async void UpdateMediaRows()
    {
        if (!Items.Any(v => v.HasMedia)) return;
        var s = await MediaSession();
        string text = "";
        bool playing = false;
        if (s != null)
        {
            try
            {
                var p = await s.TryGetMediaPropertiesAsync();
                text = string.Join(" • ", new[] { p.Title, p.Artist }.Where(x => !string.IsNullOrWhiteSpace(x)));
                playing = s.GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            }
            catch
            {
                // Some players report no properties; the buttons still work.
            }
        }
        else
        {
            text = "Nothing playing";
        }
        foreach (var vm in Items)
        {
            if (!vm.HasMedia) continue;
            vm.NowPlaying = text;
            vm.Playing = playing;
        }
    }

    private async void MediaButton(object sender, Func<GlobalSystemMediaTransportControlsSession, Windows.Foundation.IAsyncOperation<bool>> act)
    {
        if (RowOf(sender) is { } vm) SelectRow(vm);
        if (await MediaSession() is not { } s) return;
        try { await act(s); } catch { }
        // The player reports the new track or state a moment later.
        await Task.Delay(350);
        UpdateMediaRows();
    }

    private void OnMediaPrevious(object sender, RoutedEventArgs e) => MediaButton(sender, s => s.TrySkipPreviousAsync());
    private void OnMediaPlayPause(object sender, RoutedEventArgs e) => MediaButton(sender, s => s.TryTogglePlayPauseAsync());
    private void OnMediaNext(object sender, RoutedEventArgs e) => MediaButton(sender, s => s.TrySkipNextAsync());

    // ------------------------------------------------------------ confirming

    private void OnConfirmRun(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } vm && Items.IndexOf(vm) is >= 0 and var i) Launch(i, reveal: false);
    }

    private void OnConfirmCancel(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } vm) vm.Confirming = false;
        UpdateOpenButton();
    }

    // ------------------------------------------------------------ windows

    private void OnWindowMinimize(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } vm) WindowAct(vm, "minimize");
    }

    private void OnWindowClose(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is { } vm) WindowAct(vm, "close");
    }

    /// <summary>Minimize or close a window row's window, then list the windows again.</summary>
    private async void WindowAct(HitVm vm, string id)
    {
        int i = Items.IndexOf(vm);
        if (i < AnswerOffset || !vm.IsWindow) return;
        SelectRow(vm);
        Native.Act(_gen, i - AnswerOffset, id);
        // A window closes when its app gets round to it (it may ask to save first).
        await Task.Delay(id == "close" ? 400 : 150);
        RefreshAfterControl(vm);
    }

    /// <summary>Extra height the rows' controls take (slider strips) among the first <paramref name="rows"/>.</summary>
    private double ControlsDip(int rows)
    {
        double sum = 0;
        for (int i = 0; i < rows && i < Items.Count; i++) sum += Items[i].ExtraDip;
        return sum;
    }
}
