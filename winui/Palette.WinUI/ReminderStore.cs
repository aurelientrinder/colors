using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Palette;

/// <summary>A reminder: what to say, when, and the conversation it was set in (null: set elsewhere).</summary>
public sealed record Reminder(string Id, string Text, DateTime At, DateTime Created, string? ChatId);

/// <summary>
/// The reminders set and not yet done, kept in %LOCALAPPDATA%\palette\reminders.json on this PC. The chat
/// sets, lists and cancels them (ChatTools.cs); when one is due the bubble shows it (Bubble.cs) until it is
/// done or snoozed. A reminder that came due while Palette was not running shows when it starts.
/// </summary>
internal static class ReminderStore
{
    private static readonly object Gate = new();
    private static List<Reminder>? _all;

    /// <summary>A reminder was added, moved or removed (on any thread).</summary>
    public static event Action? Changed;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "reminders.json");

    private static List<Reminder> All()
    {
        if (_all != null) return _all;
        try
        {
            if (File.Exists(FilePath)) _all = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ReminderJson.Default.ListReminder);
        }
        catch (Exception ex)
        {
            App.Log("reminders not loaded: " + ex.Message);
        }
        return _all ??= new();
    }

    private static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(All(), ReminderJson.Default.ListReminder);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", json);
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log("reminders not saved: " + ex.Message);
        }
    }

    /// <summary>Every reminder not yet done, the soonest first.</summary>
    public static List<Reminder> List()
    {
        lock (Gate) return All().OrderBy(r => r.At).ToList();
    }

    /// <summary>The reminders due by <paramref name="now"/>, the oldest first.</summary>
    public static List<Reminder> Due(DateTime now)
    {
        lock (Gate) return All().Where(r => r.At <= now).OrderBy(r => r.At).ToList();
    }

    public static Reminder Add(string text, DateTime at, string? chatId)
    {
        text = text.Trim();
        if (text.Length > 0) text = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        var r = new Reminder(Guid.NewGuid().ToString("N")[..12], text, at, DateTime.Now, chatId);
        lock (Gate)
        {
            All().Add(r);
            Save();
        }
        Changed?.Invoke();
        return r;
    }

    /// <summary>Done, dismissed or cancelled: gone for good. False if it was already gone.</summary>
    public static bool Remove(string id)
    {
        bool gone;
        lock (Gate)
        {
            gone = All().RemoveAll(r => r.Id == id) > 0;
            if (gone) Save();
        }
        if (gone) Changed?.Invoke();
        return gone;
    }

    /// <summary>Put a reminder back after a cancel was undone.</summary>
    public static void Restore(Reminder r)
    {
        lock (Gate)
        {
            if (All().Any(x => x.Id == r.Id)) return;
            All().Add(r);
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Go off again <paramref name="by"/> from now.</summary>
    public static void Snooze(string id, TimeSpan by)
    {
        lock (Gate)
        {
            int i = All().FindIndex(r => r.Id == id);
            if (i < 0) return;
            All()[i] = All()[i] with { At = DateTime.Now + by };
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>The reminders that match <paramref name="about"/> best (words they share, or the words inside them).</summary>
    public static List<Reminder> Find(string about)
    {
        var words = Words(about);
        lock (Gate)
        {
            var scored = All().Select(r =>
            {
                var w = Words(r.Text);
                double score = r.Text.Contains(about.Trim(), StringComparison.OrdinalIgnoreCase) ? 1
                    : words.Count == 0 ? 0 : (double)words.Count(w.Contains) / words.Count;
                return (r, score);
            }).Where(t => t.score >= 0.5).ToList();
            if (scored.Count == 0) return new();
            double top = scored.Max(t => t.score);
            return scored.Where(t => t.score >= top).Select(t => t.r).ToList();
        }
    }

    private static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
        { "the", "a", "an", "to", "my", "of", "about", "reminder", "reminders", "le", "la", "les", "de", "du", "rappel" };

    private static HashSet<string> Words(string s) =>
        Regex.Split(s.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 2 && !Small.Contains(w)).ToHashSet();

    // ------------------------------------------------------------------ when

    /// <summary>
    /// When a reminder the model asked for goes off: <paramref name="inMinutes"/> from now, or at
    /// <paramref name="time"/> ("18:30", "6pm", "6:30 pm"; 09:00 without one) on <paramref name="date"/> (today,
    /// tomorrow, a weekday or YYYY-MM-DD; without one, today, or tomorrow if that time has passed). Null, with
    /// why, when it cannot tell or it is past.
    /// </summary>
    public static DateTime? Resolve(int? inMinutes, string time, string date, DateTime now, out string why)
    {
        why = "";
        if (inMinutes is > 0 and <= 60 * 24 * 366) return now.AddMinutes(inMinutes.Value);
        if (time.Length == 0 && date.Length == 0)
        {
            why = "Say when: give in_minutes, or time (and date).";
            return null;
        }
        TimeSpan tod = new(9, 0, 0);   // a day without a time: the morning
        if (time.Length > 0)
        {
            var m = Regex.Match(time.Trim().ToLowerInvariant(), @"^(\d{1,2})(?:[:h.](\d{2}))?(?::\d{2})?\s*(am|pm|a\.m\.|p\.m\.)?$");
            if (!m.Success)
            {
                why = $"The time {time} is not one I can read: give it as HH:MM.";
                return null;
            }
            int h = int.Parse(m.Groups[1].Value), min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            string ampm = m.Groups[3].Value;
            if (ampm.StartsWith('p') && h < 12) h += 12;
            if (ampm.StartsWith('a') && h == 12) h = 0;
            if (h > 23 || min > 59)
            {
                why = $"The time {time} is not a time of day.";
                return null;
            }
            tod = new TimeSpan(h, min, 0);
        }
        DateTime at;
        if (date.Length > 0)
        {
            if (Day(date, now, tod) is not { } day)
            {
                why = $"The day {date} is not one I can read: give today, tomorrow, a weekday or YYYY-MM-DD.";
                return null;
            }
            at = day + tod;
        }
        else
        {
            at = now.Date + tod;
            if (at <= now) at = at.AddDays(1);
        }
        if (at <= now)
        {
            why = $"{at.ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture)} has already passed.";
            return null;
        }
        return at;
    }

    private static readonly string[] Weekdays = { "sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday" };
    private static readonly string[] Jours = { "dimanche", "lundi", "mardi", "mercredi", "jeudi", "vendredi", "samedi" };

    /// <summary>
    /// The day the model named: today, tomorrow, a weekday ("friday", "next tuesday": the coming one; the
    /// same weekday as today means today if <paramref name="tod"/> is still ahead, else a week on), or YYYY-MM-DD.
    /// </summary>
    private static DateTime? Day(string said, DateTime now, TimeSpan tod)
    {
        string d = said.Trim().ToLowerInvariant();
        if (DateTime.TryParseExact(d, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var exact)) return exact.Date;
        if (d is "today" or "tonight" or "aujourd'hui" or "ce soir") return now.Date;
        if (d is "tomorrow" or "demain") return now.Date.AddDays(1);
        if (d is "day after tomorrow" or "après-demain") return now.Date.AddDays(2);
        bool next = Regex.IsMatch(d, @"^(next|this|coming)\s|\s(prochain|qui vient)$");
        string name = Regex.Replace(d, @"^(next|this|coming|on)\s+|\s+(prochain|qui vient)$", "").Trim();
        int i = Array.IndexOf(Weekdays, name);
        if (i < 0) i = Array.IndexOf(Jours, name);
        if (i < 0) return null;
        int ahead = (i - (int)now.DayOfWeek + 7) % 7;
        if (ahead == 0 && (next || now.Date + tod <= now)) ahead = 7;
        return now.Date.AddDays(ahead);
    }

    /// <summary>"today at 18:30", "tomorrow at 09:00", "Friday 24 October at 09:00", for people and the model.</summary>
    public static string Describe(DateTime at, CultureInfo? culture = null)
    {
        var c = culture ?? CultureInfo.CurrentCulture;
        string time = at.ToString("t", c);
        if (at.Date == DateTime.Today) return $"today at {time}";
        if (at.Date == DateTime.Today.AddDays(1)) return $"tomorrow at {time}";
        if ((at.Date - DateTime.Today).TotalDays < 7) return at.ToString("dddd", c) + $" at {time}";
        return at.ToString(at.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy", c) + $" at {time}";
    }

    /// <summary>"in 25 min", "in 1 h 10 min", "in 3 days".</summary>
    public static string Until(DateTime at)
    {
        var d = at - DateTime.Now;
        if (d.TotalMinutes < 1) return "in less than a minute";
        if (d.TotalHours < 1) return $"in {(int)Math.Ceiling(d.TotalMinutes)} min";
        if (d.TotalHours < 24) return $"in {(int)d.TotalHours} h" + (d.Minutes > 0 ? $" {d.Minutes} min" : "");
        int days = (int)Math.Round(d.TotalDays);
        return days == 1 ? "in a day" : $"in {days} days";
    }
}

[JsonSerializable(typeof(List<Reminder>))]
internal sealed partial class ReminderJson : JsonSerializerContext
{
}
