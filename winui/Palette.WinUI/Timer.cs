using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;

namespace Palette;

/// <summary>
/// A countdown timer in the list: "timer 5 min", "10 second timer", "timer 1h30m", "timer 2:30"
/// put a row on top with the time and a start button; Enter starts it. One timer runs at a time.
/// While it runs, "timer" (or opening the palette on home) shows it with pause and cancel. When it
/// ends, the alarm sound plays and the tray icon says so, whether or not the palette is open.
/// </summary>
public sealed partial class MainWindow
{
    private enum TimerState { Running, Paused, Done }

    private sealed class PaletteTimer
    {
        public TimeSpan Duration;
        public DateTime EndsAt;          // while running
        public TimeSpan Left;            // while paused
        public TimerState State;
        public DateTime FinishedAt;
    }

    private PaletteTimer? _timer;
    private string? _timerStartedFor;    // the query a timer was started from: it shows that timer now, not a new one
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _timerTick;
    private System.Threading.Timer? _timerAlarm;

    private HitVm? TimerRow => Items.Count > 0 && Items[0].IsTimer ? Items[0] : null;

    // ------------------------------------------------------------ reading the query

    private static readonly Regex TimerWords = new(@"\b(set|start|a|an|for|of|timer|countdown|and)\b", RegexOptions.Compiled);
    private static readonly Regex Clock = new(@"^(\d{1,2}):(\d{1,2})(?::(\d{1,2}))?$", RegexOptions.Compiled);
    private static readonly Regex Part = new(@"(\d+(?:\.\d+)?)\s*([a-z]*)", RegexOptions.Compiled);

    /// <summary>
    /// Whether <paramref name="query"/> asks about a timer: false when not; true with a duration
    /// for a new one ("timer 5 min"), or with null for the running one ("timer").
    /// </summary>
    private static bool ReadTimer(string query, out TimeSpan? duration)
    {
        duration = null;
        string q = query.Trim().ToLowerInvariant();
        if (!Regex.IsMatch(q, @"\b(timer|countdown)\b")) return false;
        string rest = Regex.Replace(TimerWords.Replace(q, " "), @"\s+", " ").Trim();
        if (rest.Length == 0) return true;

        var clock = Clock.Match(rest);
        if (clock.Success)
        {
            int a = int.Parse(clock.Groups[1].Value), b = int.Parse(clock.Groups[2].Value);
            duration = clock.Groups[3].Success
                ? new TimeSpan(a, b, int.Parse(clock.Groups[3].Value))
                : new TimeSpan(0, a, b);
        }
        else
        {
            var total = TimeSpan.Zero;
            int consumed = 0;
            foreach (Match m in Part.Matches(rest))
            {
                double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                total += m.Groups[2].Value switch
                {
                    "" or "m" or "min" or "mins" or "minute" or "minutes" => TimeSpan.FromMinutes(n),
                    "h" or "hr" or "hrs" or "hour" or "hours" => TimeSpan.FromHours(n),
                    "s" or "sec" or "secs" or "second" or "seconds" => TimeSpan.FromSeconds(n),
                    _ => TimeSpan.MinValue,
                };
                if (total < TimeSpan.Zero) return false;   // an unknown unit
                consumed += m.Value.Replace(" ", "").Length;
            }
            if (consumed != rest.Replace(" ", "").Length) return false;   // words that are not a duration
            duration = total;
        }
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromHours(24)) return false;
        return true;
    }

    /// <summary>"5 min", "1 h 30 min", "45 s".</summary>
    private static string Describe(TimeSpan d)
    {
        var parts = new List<string>();
        if (d.Hours + d.Days * 24 > 0) parts.Add($"{d.Hours + d.Days * 24} h");
        if (d.Minutes > 0) parts.Add($"{d.Minutes} min");
        if (d.Seconds > 0) parts.Add($"{d.Seconds} s");
        return parts.Count > 0 ? string.Join(" ", parts) : "0 s";
    }

    /// <summary>"4:59", "1:29:59": the countdown, rounded up so it reads 0:00 only at the end.</summary>
    private static string Countdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        var t = TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    private TimeSpan TimerLeft(PaletteTimer t) => t.State switch
    {
        TimerState.Running => t.EndsAt - DateTime.Now,
        TimerState.Paused => t.Left,
        _ => TimeSpan.Zero,
    };

    // ------------------------------------------------------------ the row

    /// <summary>
    /// Put, update or remove the timer row for <paramref name="query"/>. True when the row is
    /// showing (other answers then stay away).
    /// </summary>
    private bool ShowTimerFor(string query)
    {
        if (_view != View.Results) return false;
        bool asks = ReadTimer(query, out var draft);
        if (_timerStartedFor != null && !SameQuery(_timerStartedFor, query)) _timerStartedFor = null;
        if (_timerStartedFor != null) draft = null;   // started from this query: show that timer
        bool home = query.Trim().Length == 0;
        if (!(asks && (draft != null || _timer != null)) && !(home && _timer != null))
        {
            RemoveTimerRow();
            return false;
        }

        var row = TimerRow;
        if (row == null)
        {
            int sel = ResultsList.SelectedIndex;
            row = HitVm.ForTimer("Timer", "");
            Items.Insert(0, row);
            if (sel <= 0) ResultsList.SelectedIndex = 0;
            else _expandedQuery = _latestQuery;
        }
        _timerDraft = draft;
        RenderTimerRow(row);
        UpdateCondensed();
        Resize(recenter: false);
        UpdateOpenButton();
        return true;
    }

    private TimeSpan? _timerDraft;   // the duration the row offers to start, or null when it shows _timer

    private void RemoveTimerRow()
    {
        if (TimerRow == null) return;
        Items.RemoveAt(0);
        _timerDraft = null;
        UpdateCondensed();
        Resize(recenter: false);
    }

    private void RenderTimerRow(HitVm row)
    {
        if (_timerDraft is { } d)
        {
            string sub = _timer is { State: not TimerState.Done } running
                ? $"Press Enter to start • replaces the {Countdown(TimerLeft(running))} left"
                : "Press Enter to start";
            row.SetTimer($"{Describe(d)} timer", sub, Countdown(d), "", "Start", 0, canCancel: false);
            return;
        }
        if (_timer is not { } t) return;
        double done = t.Duration.TotalSeconds > 0 ? 1 - TimerLeft(t).TotalSeconds / t.Duration.TotalSeconds : 1;
        switch (t.State)
        {
            case TimerState.Running:
                row.SetTimer($"{Describe(t.Duration)} timer", $"Ends at {t.EndsAt.ToString("t", CultureInfo.CurrentCulture)}",
                    Countdown(TimerLeft(t)), "", "Pause", done, canCancel: true);
                break;
            case TimerState.Paused:
                row.SetTimer($"{Describe(t.Duration)} timer", "Paused", Countdown(t.Left), "", "Resume", done, canCancel: true);
                break;
            default:
                row.SetTimer("Time's up", $"{Describe(t.Duration)} timer • finished at {t.FinishedAt.ToString("t", CultureInfo.CurrentCulture)}",
                    "0:00", "", "Restart", 1, canCancel: true);
                break;
        }
    }

    // ------------------------------------------------------------ running it

    /// <summary>Enter, or the row's first button: start, pause, resume or restart.</summary>
    private void TimerPrimary()
    {
        if (_timerDraft is { } d)
        {
            StartTimer(d);
            _timerStartedFor = _latestQuery;
            _timerDraft = null;
        }
        else if (_timer is { } t)
        {
            switch (t.State)
            {
                case TimerState.Running:
                    t.Left = t.EndsAt - DateTime.Now;
                    t.State = TimerState.Paused;
                    _timerAlarm?.Dispose();
                    _timerAlarm = null;
                    break;
                case TimerState.Paused:
                    t.EndsAt = DateTime.Now + t.Left;
                    t.State = TimerState.Running;
                    ArmAlarm(t);
                    break;
                default:
                    StartTimer(t.Duration);
                    break;
            }
        }
        if (TimerRow is { } row) RenderTimerRow(row);
        UpdateOpenButton();
    }

    private void StartTimer(TimeSpan d)
    {
        _timerAlarm?.Dispose();
        var t = _timer = new PaletteTimer { Duration = d, EndsAt = DateTime.Now + d, State = TimerState.Running };
        ArmAlarm(t);
        if (_timerTick == null)
        {
            _timerTick = DispatcherQueue.CreateTimer();
            _timerTick.Interval = TimeSpan.FromMilliseconds(200);
            _timerTick.Tick += (_, _) =>
            {
                if (_timer == null) { _timerTick.Stop(); return; }
                if (_visible && TimerRow is { } row && _timerDraft == null) RenderTimerRow(row);
            };
        }
        _timerTick.Start();
    }

    private void ArmAlarm(PaletteTimer t)
    {
        _timerAlarm?.Dispose();
        var due = t.EndsAt - DateTime.Now;
        if (due < TimeSpan.Zero) due = TimeSpan.Zero;
        _timerAlarm = new System.Threading.Timer(_ => DispatcherQueue.TryEnqueue(() => TimerFinished(t)), null, due, Timeout.InfiniteTimeSpan);
    }

    private void TimerFinished(PaletteTimer t)
    {
        if (_timer != t || t.State != TimerState.Running) return;
        t.State = TimerState.Done;
        t.FinishedAt = DateTime.Now;
        _timerAlarm?.Dispose();
        _timerAlarm = null;
        _timerTick?.Stop();
        PlayAlarm();
        _tray.ShowBalloon("Time's up", $"Your {Describe(t.Duration)} timer has finished.");
        if (TimerRow is { } row) RenderTimerRow(row);
        UpdateOpenButton();
    }

    private void CancelTimer()
    {
        _timerAlarm?.Dispose();
        _timerAlarm = null;
        _timerTick?.Stop();
        _timer = null;
        _timerStartedFor = null;
        // The row stays only if the search still asks for a new one ("timer 5 min").
        ShowTimerFor(Input.Text);
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>Windows' own alarm sound (Settings › Sound › Alarm), else the notification one.</summary>
    private static void PlayAlarm()
    {
        const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_ALIAS = 0x10000;
        if (!PlaySound("Notification.Looping.Alarm", IntPtr.Zero, SND_ALIAS | SND_ASYNC | SND_NODEFAULT))
            PlaySound("SystemNotification", IntPtr.Zero, SND_ALIAS | SND_ASYNC);
    }

    private void OnTimerToggle(object sender, RoutedEventArgs e) => TimerPrimary();

    private void OnTimerCancel(object sender, RoutedEventArgs e) => CancelTimer();
}
