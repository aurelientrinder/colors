using System.Globalization;
using System.Text;
using Windows.ApplicationModel.Appointments;

namespace Palette;

/// <summary>One occurrence of an event, in local time. An all-day event ends on the midnight after its last day.</summary>
public sealed record CalEvent(string Title, DateTime Start, DateTime End, bool AllDay, string Location);

/// <summary>"calendar" (the month and a day's events) or "today" (just today's events).</summary>
public sealed record CalendarAnswer(string Query, bool Today) : WebAnswers.Answer(Query);

/// <summary>
/// Where the calendar card gets its events, merged and sorted:
/// <list type="bullet">
/// <item>Google Calendar, through the calendar's secret iCal address (Settings › Add Google Calendar
/// reads it from the clipboard). Nothing is signed in to: the address is the key, kept in settings.json.</item>
/// <item>The calendars Windows already syncs (Outlook, Google or iCloud accounts added in Settings ›
/// Accounts), when Windows lets this program read them.</item>
/// </list>
/// Events from five weeks back to six months ahead are kept in memory for ten minutes.
/// </summary>
public static class CalendarData
{
    public static CalendarAnswer? Detect(string query)
    {
        string q = query.Trim().ToLowerInvariant();
        return q switch
        {
            "calendar" or "my calendar" or "google calendar" or "calendar events" or "events" or "my events"
                => new CalendarAnswer(query.Trim(), false),
            "today" or "my day" or "today's events" or "events today" or "agenda" or "my agenda"
                or "today's agenda" or "schedule" or "my schedule"
                => new CalendarAnswer(query.Trim(), true),
            _ => null,
        };
    }

    // ------------------------------------------------------------ loading

    public static IReadOnlyList<CalEvent> Events { get; private set; } = Array.Empty<CalEvent>();
    public static DateTime LoadedAt { get; private set; } = DateTime.MinValue;
    /// <summary>How many sources answered (feeds, plus Windows' calendars when readable).</summary>
    public static int Sources { get; private set; }
    /// <summary>What went wrong with a feed, for the card to say.</summary>
    public static string? Problem { get; private set; }

    public static DateTime From => DateTime.Today.AddDays(-35);
    public static DateTime To => DateTime.Today.AddDays(190);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly TimeSpan Fresh = TimeSpan.FromMinutes(10);

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("PaletteLauncher/1.0");
        return c;
    }

    public static bool IsStale => DateTime.UtcNow - LoadedAt > Fresh;

    public static void Invalidate() => LoadedAt = DateTime.MinValue;

    public static string UrlOf(string address)
    {
        string a = address.Trim();
        if (a.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase)) a = "https://" + a[9..];
        return a;
    }

    /// <summary>Whether <paramref name="text"/> looks like the address of a calendar feed.</summary>
    public static bool LooksLikeFeed(string text)
    {
        string a = UrlOf(text);
        return a.Length < 600 && !a.Contains(' ') && !a.Contains('\n')
            && (a.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || a.StartsWith("http://", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The feed's events, or an exception saying why not (used to check an address before keeping it).</summary>
    public static async Task<List<CalEvent>> ReadFeed(string address, CancellationToken ct)
    {
        string text = await Http.GetStringAsync(UrlOf(address), ct);
        if (!text.Contains("BEGIN:VCALENDAR", StringComparison.Ordinal)) throw new InvalidDataException("not a calendar");
        return await Task.Run(() => ParseIcs(text, From, To), ct);
    }

    public static async Task Refresh(IReadOnlyList<string> feeds, bool force, CancellationToken ct)
    {
        await Gate.WaitAsync(ct);
        try
        {
            if (!force && !IsStale) return;
            var all = new List<CalEvent>();
            int sources = 0;
            string? problem = null;

            var tasks = feeds.Select(async f =>
            {
                try { return (await ReadFeed(f, ct), (string?)null); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { App.Log("calendar feed failed: " + ex.GetType().Name); return (new List<CalEvent>(), "A calendar feed could not be read"); }
            }).ToList();
            var windows = ReadWindows(ct);
            foreach (var (events, err) in await Task.WhenAll(tasks))
            {
                if (err != null) problem = err;
                else sources++;
                all.AddRange(events);
            }
            var local = await windows;
            if (local != null)
            {
                sources++;
                var seen = new HashSet<(string, DateTime)>(all.Select(e => (e.Title, e.Start)));
                all.AddRange(local.Where(e => seen.Add((e.Title, e.Start))));
            }

            all.Sort((a, b) =>
            {
                int c = a.Start.CompareTo(b.Start);
                return c != 0 ? c : string.CompareOrdinal(a.Title, b.Title);
            });
            Events = all;
            Sources = sources;
            Problem = problem;
            LoadedAt = DateTime.UtcNow;
        }
        finally { Gate.Release(); }
    }

    /// <summary>The calendars Windows syncs, or null when it will not say (no store, or access refused).</summary>
    private static async Task<List<CalEvent>?> ReadWindows(CancellationToken ct)
    {
        try
        {
            var work = Task.Run(async () =>
            {
                var store = await AppointmentManager.RequestStoreAsync(AppointmentStoreAccessType.AllCalendarsReadOnly);
                if (store == null) return null;
                var options = new FindAppointmentsOptions { MaxCount = 1000 };
                foreach (var p in new[] { "Subject", "Location", "StartTime", "Duration", "AllDay" }) options.FetchProperties.Add(p);
                var found = await store.FindAppointmentsAsync(new DateTimeOffset(From), To - From, options);
                var list = new List<CalEvent>();
                foreach (var a in found)
                {
                    var start = a.StartTime.LocalDateTime;
                    var end = start + a.Duration;
                    if (a.AllDay) { start = start.Date; end = end <= start ? start.AddDays(1) : end; }
                    list.Add(new CalEvent(string.IsNullOrWhiteSpace(a.Subject) ? "(No title)" : a.Subject.Trim(), start, end, a.AllDay, a.Location ?? ""));
                }
                return list;
            }, ct);
            if (await Task.WhenAny(work, Task.Delay(5000, ct)) != work) return null;
            return await work;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            App.Log("windows calendars unavailable: " + ex.GetType().Name);
            return null;
        }
    }

    // ------------------------------------------------------------ one day

    public static List<CalEvent> On(DateTime day)
    {
        day = day.Date;
        var next = day.AddDays(1);
        return Events.Where(e => e.Start < next && (e.End > day || e.Start >= day)).ToList();
    }

    public static HashSet<DateTime> Days(DateTime from, DateTime to)
    {
        var set = new HashSet<DateTime>();
        foreach (var e in Events)
        {
            if (e.End <= from || e.Start >= to) continue;
            var d = e.Start.Date < from ? from.Date : e.Start.Date;
            var last = e.AllDay || e.End.TimeOfDay == TimeSpan.Zero ? e.End.Date.AddDays(-1) : e.End.Date;
            if (last < d) last = d;
            for (; d <= last && d < to; d = d.AddDays(1)) set.Add(d);
        }
        return set;
    }

    // ------------------------------------------------------------ iCalendar

    private sealed class Prop
    {
        public string Name = "";
        public Dictionary<string, string> Params = new(StringComparer.OrdinalIgnoreCase);
        public string Value = "";
    }

    private readonly record struct Stamp(DateTime Wall, TimeZoneInfo? Tz, bool AllDay)
    {
        public DateTime Local
        {
            get
            {
                if (AllDay || Tz == null) return Wall;
                try { return TimeZoneInfo.ConvertTime(Wall, Tz, TimeZoneInfo.Local); }
                catch { return Wall; }
            }
        }

        public Stamp At(DateTime wall) => this with { Wall = wall };
    }

    private sealed class Rule
    {
        public string Freq = "";
        public int Interval = 1;
        public int Count;
        public DateTime? Until;
        public List<(int Ord, DayOfWeek Day)> ByDay = new();
        public List<int> ByMonthDay = new();
    }

    private static List<string> Unfold(string text)
    {
        var lines = new List<string>();
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && lines.Count > 0) lines[^1] += line[1..];
            else lines.Add(line);
        }
        return lines;
    }

    private static Prop? ReadProp(string line)
    {
        bool quoted = false;
        int colon = -1;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == '"') quoted = !quoted;
            else if (line[i] == ':' && !quoted) { colon = i; break; }
        }
        if (colon <= 0) return null;
        var parts = new List<string>();
        var sb = new StringBuilder();
        quoted = false;
        foreach (char c in line[..colon])
        {
            if (c == '"') quoted = !quoted;
            if (c == ';' && !quoted) { parts.Add(sb.ToString()); sb.Clear(); }
            else sb.Append(c);
        }
        parts.Add(sb.ToString());
        var p = new Prop { Name = parts[0].ToUpperInvariant(), Value = line[(colon + 1)..] };
        for (int i = 1; i < parts.Count; i++)
        {
            int eq = parts[i].IndexOf('=');
            if (eq > 0) p.Params[parts[i][..eq]] = parts[i][(eq + 1)..].Trim('"');
        }
        return p;
    }

    private static string Unescape(string v) =>
        v.Replace("\\n", " ").Replace("\\N", " ").Replace("\\,", ",").Replace("\\;", ";").Replace("\\\\", "\\");

    private static Stamp? ReadStamp(Prop p, string value)
    {
        string v = value.Trim();
        bool date = (p.Params.TryGetValue("VALUE", out var vt) && vt.Equals("DATE", StringComparison.OrdinalIgnoreCase)) || v.Length == 8;
        if (date)
            return v.Length >= 8 && DateTime.TryParseExact(v[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? new Stamp(d, null, true) : null;
        bool utc = v.EndsWith('Z');
        if (!DateTime.TryParseExact(v.TrimEnd('Z'), "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var dt)) return null;
        TimeZoneInfo? tz = null;
        if (utc) tz = TimeZoneInfo.Utc;
        else if (p.Params.TryGetValue("TZID", out var id))
        {
            try { tz = TimeZoneInfo.FindSystemTimeZoneById(id); } catch { }
        }
        return new Stamp(dt, tz, false);
    }

    private static TimeSpan? ReadDuration(string v)
    {
        var m = System.Text.RegularExpressions.Regex.Match(v, @"^P(?:(\d+)W)?(?:(\d+)D)?(?:T(?:(\d+)H)?(?:(\d+)M)?(?:(\d+)S)?)?$");
        if (!m.Success) return null;
        int G(int i) => m.Groups[i].Success ? int.Parse(m.Groups[i].Value) : 0;
        return TimeSpan.FromDays(G(1) * 7 + G(2)) + TimeSpan.FromHours(G(3)) + TimeSpan.FromMinutes(G(4)) + TimeSpan.FromSeconds(G(5));
    }

    private static DateTime Key(DateTime t) => new(t.Ticks - t.Ticks % TimeSpan.TicksPerMinute);

    private static Rule? ReadRule(string v, Stamp start)
    {
        var r = new Rule();
        foreach (string part in v.Split(';'))
        {
            int eq = part.IndexOf('=');
            if (eq < 0) continue;
            string k = part[..eq].ToUpperInvariant(), val = part[(eq + 1)..];
            switch (k)
            {
                case "FREQ": r.Freq = val.ToUpperInvariant(); break;
                case "INTERVAL": r.Interval = int.TryParse(val, out var iv) ? Math.Max(1, iv) : 1; break;
                case "COUNT": r.Count = int.TryParse(val, out var c) ? c : 0; break;
                case "UNTIL":
                    {
                        var u = ReadStamp(new Prop(), val);
                        if (u is { } us) r.Until = us.AllDay ? us.Wall.AddDays(1).AddTicks(-1) : us.Local;
                        break;
                    }
                case "BYDAY":
                    foreach (string d in val.Split(','))
                    {
                        if (d.Length < 2) continue;
                        DayOfWeek? day = d[^2..].ToUpperInvariant() switch
                        {
                            "MO" => DayOfWeek.Monday, "TU" => DayOfWeek.Tuesday, "WE" => DayOfWeek.Wednesday,
                            "TH" => DayOfWeek.Thursday, "FR" => DayOfWeek.Friday, "SA" => DayOfWeek.Saturday,
                            "SU" => DayOfWeek.Sunday, _ => null,
                        };
                        if (day == null) continue;
                        string ord = d[..^2];
                        r.ByDay.Add((ord.Length == 0 ? 0 : int.TryParse(ord, out var o) ? o : 0, day.Value));
                    }
                    break;
                case "BYMONTHDAY":
                    foreach (string d in val.Split(',')) if (int.TryParse(d, out var md) && md != 0) r.ByMonthDay.Add(md);
                    break;
            }
        }
        return r.Freq is "DAILY" or "WEEKLY" or "MONTHLY" or "YEARLY" ? r : null;
    }

    private static IEnumerable<DateTime> MonthDates(DateTime first, DateTime start, Rule r)
    {
        int dim = DateTime.DaysInMonth(first.Year, first.Month);
        var days = new SortedSet<int>();
        if (r.ByMonthDay.Count > 0)
        {
            foreach (int md in r.ByMonthDay)
            {
                int d = md > 0 ? md : dim + md + 1;
                if (d >= 1 && d <= dim) days.Add(d);
            }
        }
        else if (r.ByDay.Count > 0)
        {
            foreach (var (ord, day) in r.ByDay)
            {
                int firstHit = 1 + (((int)day - (int)first.DayOfWeek) + 7) % 7;
                if (ord == 0)
                {
                    for (int d = firstHit; d <= dim; d += 7) days.Add(d);
                }
                else if (ord > 0)
                {
                    int d = firstHit + 7 * (ord - 1);
                    if (d <= dim) days.Add(d);
                }
                else
                {
                    int lastHit = firstHit + 7 * ((dim - firstHit) / 7);
                    int d = lastHit - 7 * (-ord - 1);
                    if (d >= 1) days.Add(d);
                }
            }
        }
        else if (start.Day <= dim) days.Add(start.Day);
        foreach (int d in days) yield return new DateTime(first.Year, first.Month, d) + start.TimeOfDay;
    }

    /// <summary>Wall-clock times of an event's occurrences from its first, endlessly; the caller stops it.</summary>
    private static IEnumerable<DateTime> Expand(DateTime start, Rule r)
    {
        int iv = r.Interval;
        var t = start.TimeOfDay;
        switch (r.Freq)
        {
            case "DAILY":
                for (int i = 0; ; i++) yield return start.AddDays((long)i * iv);
            case "WEEKLY":
                if (r.ByDay.Count == 0)
                {
                    for (int i = 0; ; i++) yield return start.AddDays(7L * iv * i);
                }
                else
                {
                    var days = r.ByDay.Select(b => b.Day).Distinct().OrderBy(d => ((int)d + 6) % 7).ToList();
                    var weekStart = start.Date.AddDays(-(((int)start.DayOfWeek + 6) % 7));
                    for (int w = 0; ; w++)
                    {
                        var ws = weekStart.AddDays(7L * iv * w);
                        foreach (var d in days)
                        {
                            var c = ws.AddDays(((int)d + 6) % 7) + t;
                            if (c >= start) yield return c;
                        }
                    }
                }
            case "MONTHLY":
                for (int m = 0; ; m++)
                {
                    var first = new DateTime(start.Year, start.Month, 1).AddMonths(m * iv);
                    foreach (var d in MonthDates(first, start, r)) if (d >= start) yield return d;
                }
            case "YEARLY":
                for (int y = 0; ; y++)
                {
                    int year = start.Year + y * iv;
                    if (year > 9998) yield break;
                    if (start.Month == 2 && start.Day == 29 && !DateTime.IsLeapYear(year)) continue;
                    yield return new DateTime(year, start.Month, start.Day) + t;
                }
        }
    }

    /// <summary>Events of an iCalendar text that fall in [from, to), repeating ones expanded.</summary>
    public static List<CalEvent> ParseIcs(string text, DateTime from, DateTime to)
    {
        var events = new List<Dictionary<string, List<Prop>>>();
        Dictionary<string, List<Prop>>? cur = null;
        int depth = 0;   // inside VEVENT: alarms and the like nest deeper and are skipped
        foreach (string line in Unfold(text))
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase)) { cur = new(); depth = 0; continue; }
            if (cur == null) continue;
            if (line.StartsWith("BEGIN:", StringComparison.OrdinalIgnoreCase)) { depth++; continue; }
            if (line.StartsWith("END:", StringComparison.OrdinalIgnoreCase))
            {
                if (depth > 0) { depth--; continue; }
                events.Add(cur);
                cur = null;
                continue;
            }
            if (depth > 0) continue;
            if (ReadProp(line) is not { } p) continue;
            if (!cur.TryGetValue(p.Name, out var list)) cur[p.Name] = list = new();
            list.Add(p);
        }

        var result = new List<CalEvent>();
        var replaced = new HashSet<(string, DateTime)>();   // occurrences moved or edited on their own
        foreach (var ev in events)
        {
            if (ev.TryGetValue("RECURRENCE-ID", out var rid) && ReadStamp(rid[0], rid[0].Value) is { } rs)
                replaced.Add((First(ev, "UID"), Key(rs.Local)));
        }

        foreach (var ev in events)
        {
            if (First(ev, "STATUS").Equals("CANCELLED", StringComparison.OrdinalIgnoreCase)) continue;
            if (!ev.TryGetValue("DTSTART", out var ds) || ReadStamp(ds[0], ds[0].Value) is not { } start) continue;

            TimeSpan length;
            if (ev.TryGetValue("DTEND", out var de) && ReadStamp(de[0], de[0].Value) is { } end)
                length = end.Local - start.Local;
            else if (ev.TryGetValue("DURATION", out var du) && ReadDuration(du[0].Value.Trim()) is { } dd)
                length = dd;
            else
                length = start.AllDay ? TimeSpan.FromDays(1) : TimeSpan.Zero;
            if (length < TimeSpan.Zero) length = TimeSpan.Zero;
            if (start.AllDay && length < TimeSpan.FromDays(1)) length = TimeSpan.FromDays(1);

            string title = Unescape(First(ev, "SUMMARY")).Trim();
            if (title.Length == 0) title = "(No title)";
            string place = Unescape(First(ev, "LOCATION")).Trim();
            string uid = First(ev, "UID");
            bool isOverride = ev.ContainsKey("RECURRENCE-ID");

            void Add(DateTime occ)
            {
                var e = occ + length;
                if (occ < to && (e > from || occ >= from)) result.Add(new CalEvent(title, occ, e, start.AllDay, place));
            }

            if (!ev.TryGetValue("RRULE", out var rr) || isOverride || ReadRule(rr[0].Value, start) is not { } rule)
            {
                Add(start.Local);
                continue;
            }

            var excluded = new HashSet<DateTime>();
            if (ev.TryGetValue("EXDATE", out var exs))
                foreach (var ex in exs)
                    foreach (string v in ex.Value.Split(','))
                        if (ReadStamp(ex, v) is { } es) excluded.Add(Key(es.Local));

            int n = 0, guard = 0;
            foreach (var wall in Expand(start.Wall, rule))
            {
                if (++guard > 30000) break;
                var occ = start.At(wall).Local;
                if (rule.Until is { } until && occ > until) break;
                if (rule.Count > 0 && ++n > rule.Count) break;
                if (occ >= to) break;
                if (excluded.Contains(Key(occ)) || replaced.Contains((uid, Key(occ)))) continue;
                Add(occ);
            }
        }
        return result;
    }

    private static string First(Dictionary<string, List<Prop>> ev, string name) =>
        ev.TryGetValue(name, out var l) && l.Count > 0 ? l[0].Value : "";
}
