namespace Palette;

/// <summary>
/// Reminders going off: every few seconds (and whenever one is set, snoozed or done) the reminders due
/// are looked at, and the oldest shows in the bubble (Bubble.cs) until it is done or snoozed, then the
/// next. Those that came due while Palette was not running show as missed when it starts. The palette
/// opens where the bubble sits, so the bubble steps away while it is open and comes back after.
/// </summary>
public sealed partial class MainWindow
{
    private Bubble? _bubble;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reminderTick;
    /// <summary>The reminders already announced (glowed and chimed), by id and time: a snoozed one is announced again.</summary>
    private readonly HashSet<(string, DateTime)> _announced = new();

    private static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(10);

    private void InitReminders()
    {
        ReminderStore.Changed += () => DispatcherQueue.TryEnqueue(CheckReminders);
        _reminderTick = DispatcherQueue.CreateTimer();
        _reminderTick.Interval = TimeSpan.FromSeconds(5);
        _reminderTick.Tick += (_, _) => CheckReminders();
        _reminderTick.Start();
        // A moment after starting, so a reminder missed while Palette was off waits for the desktop.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, CheckReminders);
    }

    /// <summary>Show the oldest reminder due, or put the bubble away when none is.</summary>
    private void CheckReminders()
    {
        var due = ReminderStore.Due(DateTime.Now);
        if (due.Count == 0)
        {
            _bubble?.HideBubble();
            return;
        }
        if (_visible) return;   // the palette is where the bubble would be: after it closes
        var r = due[0];
        _bubble ??= MakeBubble();
        if (_bubble.Showing && _bubble.Current is { } shown && shown.Id == r.Id && shown.At == r.At) return;
        bool announce = _announced.Add((r.Id, r.At));
        string? title = r.ChatId == null ? null : ChatStore.List().FirstOrDefault(e => e.Id == r.ChatId)?.Title;
        App.Log($"reminder due: {r.Text} at {r.At:t} (announce={announce})");
        _bubble.Show(r, due.Count - 1, announce, title);
    }

    private Bubble MakeBubble()
    {
        var b = new Bubble();
        b.Done += r => ReminderStore.Remove(r.Id);
        b.Snoozed += r => ReminderStore.Snooze(r.Id, SnoozeFor);
        b.OpenChat += r =>
        {
            // The conversation it was set in, where it was left: the bubble steps away while the palette is open.
            if (r.ChatId is { } id && _conversation?.Id != id && !(_conversation?.Busy ?? false))
            {
                if (_conversation is { Messages.Count: > 0 } old) ChatStore.Save(old, makeCurrent: false);
                _conversation = ChatStore.Open(id) ?? _conversation;
            }
            if (!_visible) ShowPalette();
            StartChat("");
        };
        return b;
    }

    /// <summary>The palette is opening: the bubble steps away (its reminder stays due).</summary>
    private void BubbleAway() => _bubble?.HideBubble();
}
