using System.Globalization;

namespace Palette;

/// <summary>
/// What the bubble (Bubble.cs) shows, one at a time: reminders that have come due, the oldest first,
/// then what the watchers noticed (Watchers.cs), in the order they came. Every few seconds (and whenever
/// a reminder is set, snoozed or done) the queue is looked at again. Reminders that came due while
/// Palette was not running show as missed when it starts. The palette opens where the bubble sits, so
/// the bubble steps away while it is open and comes back after; it also holds back while a full-screen
/// game, video or presentation is in front.
/// </summary>
public sealed partial class MainWindow
{
    private Bubble? _bubble;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reminderTick;
    /// <summary>The notices already announced (glowed and chimed), by key: a snoozed reminder is announced again.</summary>
    private readonly HashSet<string> _announced = new();
    /// <summary>What the watchers noticed and the user has not answered yet.</summary>
    private readonly List<Notice> _notices = new();

    private static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(10);

    private void InitReminders()
    {
        ReminderStore.Changed += () => DispatcherQueue.TryEnqueue(CheckBubble);
        _reminderTick = DispatcherQueue.CreateTimer();
        _reminderTick.Interval = TimeSpan.FromSeconds(5);
        _reminderTick.Tick += (_, _) => { Watch(); CheckBubble(); };
        _reminderTick.Start();
        InitWatchers();
        // A moment after starting, so a reminder missed while Palette was off waits for the desktop.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CheckBubble);
    }

    /// <summary>Something for the bubble, after the reminders due and what came before it.</summary>
    private void Notify(Notice n)
    {
        if (_notices.Any(o => o.Key == n.Key)) return;
        App.Log($"notice: {n.Kind} {n.Title}");
        _notices.Add(n);
        CheckBubble();
    }

    /// <summary>Show the first thing waiting, or put the bubble away when nothing is.</summary>
    private void CheckBubble()
    {
        _notices.RemoveAll(n => n.Expires is { } at && at <= DateTime.Now);
        var due = ReminderStore.Due(DateTime.Now);
        int waiting = due.Count + _notices.Count;
        if (waiting == 0 || FullScreenInFront())
        {
            _bubble?.HideBubble();
            return;
        }
        if (_visible) return;   // the palette is where the bubble would be: after it closes
        var n = due.Count > 0 ? ReminderNotice(due[0]) : _notices[0];
        _bubble ??= MakeBubble();
        if (_bubble.Showing && _bubble.Current is { } shown && shown.Key == n.Key)
        {
            _bubble.SetMore(waiting - 1);   // one came in behind it, or one waiting expired
            return;
        }
        bool announce = _announced.Add(n.Key);
        App.Log($"bubble: {n.Kind} {n.Title} (announce={announce})");
        _bubble.Show(n, waiting - 1, announce);
    }

    private Notice ReminderNotice(Reminder r)
    {
        bool missed = DateTime.Now - r.At > TimeSpan.FromMinutes(2);
        string time = r.At.ToString("t", CultureInfo.CurrentCulture);
        string? chat = r.ChatId == null ? null : ChatStore.List().FirstOrDefault(e => e.Id == r.ChatId)?.Title;
        return new Notice(
            $"reminder:{r.Id}:{r.At.Ticks}", "reminder", r.Text,
            missed ? (r.At.Date == DateTime.Today ? $"missed, {time}" : "missed") : time,
            missed ? "Missed reminder · " + ReminderStore.Describe(r.At) : "Reminder · " + time,
            r.Text,
            chat is { Length: > 0 } t ? $"You asked for it in “{t}”, {SetWhen(r.Created)}." : $"Set {SetWhen(r.Created)}.")
        {
            Link = r.ChatId != null ? "Open in chat" : null,
            Secondary = "Snooze 10 min",
            Primary = "Done",
            Reminder = r,
        };
    }

    private static string SetWhen(DateTime created)
    {
        if (created.Date == DateTime.Today) return "today at " + created.ToString("t", CultureInfo.CurrentCulture);
        if (created.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        return "on " + created.ToString("dddd d MMMM", CultureInfo.CurrentCulture);
    }

    private Bubble MakeBubble()
    {
        var b = new Bubble();
        b.Chose += (n, choice) =>
        {
            if (n.Reminder is { } r) ReminderChosen(r, choice);
            else
            {
                // A watcher's notice is answered by anything done with it.
                _notices.RemoveAll(o => o.Key == n.Key);
                WatcherChosen(n, choice);
                CheckBubble();
            }
        };
        return b;
    }

    private void ReminderChosen(Reminder r, NoticeChoice choice)
    {
        switch (choice)
        {
            case NoticeChoice.Primary or NoticeChoice.Dismiss:
                ReminderStore.Remove(r.Id);
                break;
            case NoticeChoice.Secondary:
                ReminderStore.Snooze(r.Id, SnoozeFor);
                break;
            case NoticeChoice.Link:
                // The conversation it was set in, where it was left: the bubble steps away while the palette is open.
                if (r.ChatId is { } id && _conversation?.Id != id && !(_conversation?.Busy ?? false))
                {
                    if (_conversation is { Messages.Count: > 0 } old) ChatStore.Save(old, makeCurrent: false);
                    _conversation = ChatStore.Open(id) ?? _conversation;
                }
                if (!_visible) ShowPalette();
                StartChat("");
                break;
        }
    }

    /// <summary>The palette is opening: the bubble steps away (what it showed still waits).</summary>
    private void BubbleAway() => _bubble?.HideBubble();
}
