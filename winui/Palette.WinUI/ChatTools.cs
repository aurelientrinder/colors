using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Palette;

/// <summary>A tool the model asked for in one turn, and what came of it.</summary>
public sealed class ToolCall
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Arguments { get; set; } = "";
    /// <summary>What the model is told the tool found.</summary>
    public string Result { get; set; } = "";
    /// <summary>What it is doing, for its line in the answer ("Looking through your files from last week").</summary>
    public string Label { get; set; } = "";
    /// <summary>What came of it, once done ("Found 6 files").</summary>
    public string Outcome { get; set; } = "";
    public bool Done { get; set; }
    /// <summary>How long it took, in milliseconds.</summary>
    public long Ms { get; set; }
}

/// <summary>One round of tools within an answer: what the model wrote with the calls, and the calls.</summary>
public sealed class ToolRound
{
    public string Content { get; set; } = "";
    public List<ToolCall> Calls { get; } = new();
}

/// <summary>Files a tool found, shown as clickable rows under the answer.</summary>
public sealed record FilesBlock(List<Native.FileFound> Files);

/// <summary>The weather a tool fetched, shown as a small card.</summary>
public sealed record WeatherBlock(WebAnswers.Weather Weather);

/// <summary>Calendar events a tool read, shown as a short list.</summary>
public sealed record EventsBlock(string Title, List<CalEvent> Events);

/// <summary>A document an answer drew on, numbered as the answer cites it.</summary>
public sealed record SourceDoc(string Path, string Name);

/// <summary>The documents behind an answer: [1] [2] chips to open them.</summary>
public sealed record SourcesBlock(List<SourceDoc> Docs);

/// <summary>The PC's state at one moment. Battery is -1 on a PC without one.</summary>
public sealed record SystemReading(double Cpu, double MemUsedGb, double MemTotalGb, int Battery, bool Charging,
    double DiskFreeGb, double DiskTotalGb, double UptimeHours, DateTime At);

/// <summary>The PC's state, shown as a row of gauges.</summary>
public sealed record SystemBlock(SystemReading Reading);

/// <summary>Reminders a tool set ("set"), cancelled ("cancelled") or listed ("list"): a quiet line under the answer for each set or cancelled one.</summary>
public sealed record ReminderBlock(string Kind, List<Reminder> Reminders);

/// <summary>
/// The chat's tools (offered by chat.rs TOOLS): each runs off the UI thread and gives the model a short
/// text, and the answer a card to show. They only read, except the reminders' (ReminderStore), which
/// Palette alone keeps: nothing is opened, changed or sent elsewhere.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>What a tool is doing, from what the model asked of it: "Looking through your files from last week".</summary>
    private static string ToolLabel(string name, string arguments)
    {
        string Arg(string key)
        {
            try
            {
                using var doc = JsonDocument.Parse(arguments.Length > 0 ? arguments : "{}");
                return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
                    ? v.GetString()!.Trim() : "";
            }
            catch (JsonException) { return ""; }
        }
        string When(string period) => period switch
        {
            "today" => " from today", "yesterday" => " from yesterday", "this_week" => " from this week", "last_week" => " from last week",
            "this_month" => " from this month", "last_month" => " from last month", _ => "",
        };
        switch (name)
        {
            case "find_files":
                string q = Arg("query"), kind = Arg("kind");
                string what = kind switch
                {
                    "image" => "your pictures", "video" => "your videos", "document" => "your documents", "spreadsheet" => "your spreadsheets",
                    "presentation" => "your presentations", "code" => "your code", "audio" => "your music", "3d" => "your 3D models", _ => "your files",
                };
                return $"Looking through {what}{When(Arg("period"))}" + (q.Length > 0 ? $" for “{q}”" : "");
            case "search_documents": return "Reading your documents";
            case "get_weather": return Arg("place") is { Length: > 0 } place ? $"Checking the weather in {place}" : "Checking the weather";
            case "get_calendar":
                return "Reading your calendar" + (Arg("date") is { Length: > 0 } d ? $" for {d}" : Arg("period") switch
                {
                    "tomorrow" => " for tomorrow", "this_week" => " for this week", "next_week" => " for next week", _ => " for today",
                });
            case "get_system_status": return "Checking your PC";
            case "set_reminder": return "Setting a reminder";
            case "list_reminders": return "Checking your reminders";
            case "cancel_reminder": return "Cancelling a reminder";
            case "open": return Arg("target") is { Length: > 0 } t ? $"Opening {t}" : "Opening it";
            case "change_setting":
                if (!Settable.TryGetValue(Arg("setting").ToLowerInvariant().Replace(' ', '_').Replace("-", ""), out var st)) return "Changing the setting";
                return $"Changing {(st.label is "Wi-Fi" or "Bluetooth" ? st.label : st.label.ToLowerInvariant())}";
            case "create_folder": return Arg("name") is { Length: > 0 } f ? $"Making the folder {f}" : "Making a folder";
            case "move_files": return Arg("to") is { Length: > 0 } to ? $"Moving files to {to}" : "Moving files";
            case "rename_file": return "Renaming a file";
            case "recycle_files": return "Putting files in the Recycle Bin";
            case "undo_last_action": return "Undoing the last thing I did";
            default: return "Looking it up";
        }
    }

    /// <summary>What came of a tool, for its line once done: "Found 6 files", "Nothing on the calendar".</summary>
    private static string ToolOutcome(string name, string result, object? block) => (name, block) switch
    {
        (_, FilesBlock f) => f.Files.Count == 1 ? "Found 1 file" : $"Found {f.Files.Count} files",
        (_, SourcesBlock s) => s.Docs.Count == 1 ? "Read 1 document" : $"Read {s.Docs.Count} documents",
        (_, WeatherBlock w) => $"Checked the weather in {w.Weather.Place}",
        (_, EventsBlock e) => e.Events.Count == 0 ? "Nothing on the calendar" : e.Events.Count == 1 ? "Found 1 event" : $"Found {e.Events.Count} events",
        (_, SystemBlock) => "Checked your PC",
        (_, ReminderBlock { Kind: "set" } r) => "Set for " + ReminderStore.Describe(r.Reminders[0].At),
        (_, ReminderBlock { Kind: "cancelled" } r) => r.Reminders.Count == 1 ? "Cancelled it" : $"Cancelled {r.Reminders.Count} reminders",
        (_, ReminderBlock { Kind: "list" } r) => r.Reminders.Count == 0 ? "No reminders set" : r.Reminders.Count == 1 ? "Found 1 reminder" : $"Found {r.Reminders.Count} reminders",
        (_, ActionBlock { State: "asking" or "done" or "undone" } a) => a.Kind == "undo" ? "Undone" : a.Message,
        (_, ActionBlock { State: "denied" }) => "You declined",
        (_, ActionBlock { State: "stopped" }) => "Stopped",
        (_, ActionBlock { State: "failed" }) => "Couldn't do it",
        ("open", _) => result.StartsWith("Opened") ? result.TrimEnd('.') : "Not opened",
        ("move_files" or "rename_file" or "recycle_files" or "create_folder" or "change_setting" or "undo_last_action", _) => "Not done",
        ("set_reminder", _) => "Not set",
        ("cancel_reminder", _) => "Nothing cancelled",
        ("find_files", _) => "No file found",
        ("search_documents", _) => "Nothing in your documents",
        _ => result.Length > 60 ? result[..57] + "…" : result,
    };

    /// <summary>Run a tool: the text for the model, a card (or null) and small print for under the answer (or null).</summary>
    private async Task<(string result, object? block, string? note)> RunTool(string name, string arguments, CancellationToken ct)
    {
        App.Log($"chat tool {name} {arguments}");
        JsonElement args = default;
        try
        {
            using var doc = JsonDocument.Parse(arguments.Length > 0 ? arguments : "{}");
            args = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return ($"The arguments were not valid JSON: {arguments}", null, null);
        }
        string Arg(string key) =>
            args.ValueKind == JsonValueKind.Object && args.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        int? Num(string key) =>
            args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v) ? null
            : v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double d) ? (int)Math.Round(d)
            : v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out int n) ? n : null;
        try
        {
            return name switch
            {
                "find_files" => await FindFilesTool(Arg("query"), Arg("period"), Arg("kind")),
                "search_documents" => await SearchDocumentsTool(Arg("question")),
                "get_weather" => await WeatherTool(Arg("place"), ct),
                "get_calendar" => await CalendarTool(Arg("period"), Arg("date"), ct),
                "get_system_status" => await SystemTool(),
                "set_reminder" => SetReminderTool(Arg("text"), Num("in_minutes"), Arg("time"), Arg("day").Length > 0 ? Arg("day") : Arg("date")),
                "list_reminders" => ListRemindersTool(),
                "cancel_reminder" => CancelReminderTool(Arg("about")),
                "open" => await OpenTool(Arg("target")),
                "change_setting" => await SettingTool(Arg("setting"), Arg("value").Length > 0 ? Arg("value") : Num("value")?.ToString(CultureInfo.InvariantCulture) ?? ""),
                "create_folder" => CreateFolderTool(Arg("name"), Arg("in")),
                "move_files" => await MoveTool(Strings(args, "files"), Arg("to"), ct),
                "rename_file" => await RenameTool(Arg("file"), Arg("new_name"), ct),
                "recycle_files" => await RecycleTool(Strings(args, "files"), ct),
                "undo_last_action" => UndoTool(),
                _ => ($"There is no tool called {name}.", null, null),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"tool {name} failed: {ex.Message}");
            return ($"The tool failed: {ex.Message}", null, null);
        }
    }

    /// <summary>"last_week" as a range of local days, in unix seconds; (0, 0) for any time.</summary>
    private static (long from, long to) Period(string period)
    {
        DateTime today = DateTime.Today, week = CalendarData.WeekStart(today), month = new(today.Year, today.Month, 1);
        (DateTime a, DateTime b)? r = period switch
        {
            "today" => (today, today.AddDays(1)),
            "yesterday" => (today.AddDays(-1), today),
            "this_week" => (week, today.AddDays(1)),
            "last_week" => (week.AddDays(-7), week),
            "this_month" => (month, today.AddDays(1)),
            "last_month" => (month.AddMonths(-1), month),
            _ => null,
        };
        if (r is not { } p) return (0, 0);
        return (new DateTimeOffset(p.a).ToUnixTimeSeconds(), new DateTimeOffset(p.b).ToUnixTimeSeconds());
    }

    private static async Task<(string, object?, string?)> FindFilesTool(string query, string period, string kind)
    {
        var (from, to) = Period(period);
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteString("query", query);
            w.WriteNumber("from", from);
            w.WriteNumber("to", to);
            w.WriteString("kind", kind.Length > 0 ? kind : "any");
            w.WriteNumber("limit", 6);
            w.WriteEndObject();
        }
        string request = Encoding.UTF8.GetString(ms.ToArray());
        var files = await Task.Run(() => Native.FindFiles(request));
        if (files.Count == 0) return ("No file matches.", null, null);
        var sb = new StringBuilder($"{files.Count} file(s), best first (name the ones you mean, with their extension: each name becomes a link):\n");
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            sb.Append($"{i + 1}. {f.Name} in {f.Folder}; changed {When(f.Modified)}");
            if (f.Opened > 0) sb.Append($", opened {When(f.Opened)}");
            sb.Append('\n');
        }
        return (sb.ToString(), new FilesBlock(files), null);
    }

    /// <summary>A unix time as the model reads it best: "Tuesday 14 October 2026 at 16:20".</summary>
    private static string When(long unix) =>
        unix <= 0 ? "at an unknown time"
            : DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("dddd d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);

    private static async Task<(string, object?, string?)> SearchDocumentsTool(string question)
    {
        if (question.Length == 0) return ("Give the question to search for.", null, null);
        var docs = await Task.Run(() => Native.ChatDocs(question));
        if (docs.Text.Length == 0) return ("Nothing in the user's documents matches that.", null, "Nothing in your documents matched");
        var paths = docs.Paths ?? new();
        if (paths.Count != docs.Names.Count)
            return (docs.Text, null, "From your documents: " + string.Join(" · ", docs.Names.Select((n, i) => $"[{i + 1}] {n}")));
        return (docs.Text, new SourcesBlock(paths.Zip(docs.Names, (p, n) => new SourceDoc(p, n)).ToList()), null);
    }

    private (string, object?, string?) SetReminderTool(string text, int? inMinutes, string time, string day)
    {
        if (text.Length == 0) return ("Say what to remind the user of (text).", null, null);
        var at = ReminderStore.Resolve(inMinutes, time, day, DateTime.Now, out string why);
        if (at == null) return ($"Not set: {why}", null, null);
        var r = ReminderStore.Add(text, at.Value, _chat?.Id);
        ActionLog.Add(new ActionRecord(ActionLog.NewId(), "reminder", $"Set a reminder: {r.Text}", DateTime.Now, _chat?.Id, ReminderId: r.Id));
        return ($"Reminder set for {ReminderStore.Describe(r.At, CultureInfo.InvariantCulture)} ({ReminderStore.Until(r.At)}): {r.Text} " +
            "A bubble will pop up above the taskbar then.", new ReminderBlock("set", new() { r }), null);
    }

    private static (string, object?, string?) ListRemindersTool()
    {
        var all = ReminderStore.List();
        if (all.Count == 0) return ("The user has no reminders set.", new ReminderBlock("list", all), null);
        var sb = new StringBuilder($"{all.Count} reminder(s), the soonest first:\n");
        foreach (var r in all) sb.Append($"- {r.Text}, {ReminderStore.Describe(r.At, CultureInfo.InvariantCulture)} ({ReminderStore.Until(r.At)})\n");
        return (sb.ToString(), new ReminderBlock("list", all), null);
    }

    private static (string, object?, string?) CancelReminderTool(string about)
    {
        if (about.Length == 0) return ("Say which reminder to cancel (about).", null, null);
        var found = ReminderStore.Find(about);
        if (found.Count == 0)
        {
            var all = ReminderStore.List();
            return (all.Count == 0 ? "The user has no reminders set." : "No reminder matches that. The reminders set: " +
                string.Join("; ", all.Select(r => $"{r.Text} ({ReminderStore.Describe(r.At, CultureInfo.InvariantCulture)})")) + ".", null, null);
        }
        if (found.Count > 1)
            return ("Several reminders match, so none was cancelled; ask which one: " +
                string.Join("; ", found.Select(r => $"{r.Text} ({ReminderStore.Describe(r.At, CultureInfo.InvariantCulture)})")) + ".", null, null);
        ReminderStore.Remove(found[0].Id);
        return ($"Cancelled the reminder: {found[0].Text} ({ReminderStore.Describe(found[0].At, CultureInfo.InvariantCulture)}).",
            new ReminderBlock("cancelled", found), null);
    }

    private static async Task<(string, object?, string?)> SystemTool()
    {
        var r = await Task.Run(SystemSnapshot.Read);
        string battery = r.Battery < 0 ? "no battery" : $"battery {r.Battery}%{(r.Charging ? ", charging" : "")}";
        string result = FormattableString.Invariant(
            $"Processor {r.Cpu:0}% busy; memory {r.MemUsedGb:0.0} of {r.MemTotalGb:0.0} GB in use; {battery}; disk C: {r.DiskFreeGb:0} GB free of {r.DiskTotalGb:0} GB; running for {r.UptimeHours:0.#} hours.");
        return (result, new SystemBlock(r), null);
    }

    private static async Task<(string, object?, string?)> WeatherTool(string place, CancellationToken ct)
    {
        string query = place.Length > 0 ? $"weather in {place}" : "weather";
        if (await WebAnswers.Fetch(query, ct) is not WebAnswers.Weather w) return ("The weather could not be fetched (no connection, or no such place).", null, null);
        string result = FormattableString.Invariant(
            $"{w.Place}, local time {w.LocalTime:HH:mm}: {w.Condition}, {Math.Round(w.TempC)} °C now, high {Math.Round(w.HighC)} °C, low {Math.Round(w.LowC)} °C today.");
        return (result, new WeatherBlock(w), null);
    }

    private async Task<(string, object?, string?)> CalendarTool(string period, string date, CancellationToken ct)
    {
        var feeds = _settings.CalendarFeeds.ToList();
        if (CalendarData.IsStale) await Task.Run(() => CalendarData.Refresh(feeds, force: false, ct), ct);
        DateTime today = DateTime.Today, week = CalendarData.WeekStart(today);
        (DateTime from, DateTime to, string title) r =
            DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? (d, d.AddDays(1), d.ToString("dddd d MMMM", CultureInfo.CurrentCulture))
            : period switch
            {
                "tomorrow" => (today.AddDays(1), today.AddDays(2), "Tomorrow"),
                "this_week" => (today, week.AddDays(7), "This week"),
                "next_week" => (week.AddDays(7), week.AddDays(14), "Next week"),
                _ => (today, today.AddDays(1), "Today"),
            };
        if (CalendarData.Sources == 0) return ("The user has not connected a calendar (they can add one in Palette's settings).", null, null);
        var events = CalendarData.Events.Where(e => e.Start < r.to && e.End > r.from).OrderBy(e => e.Start).Take(20).ToList();
        if (events.Count == 0) return ($"{r.title}: no events.", new EventsBlock(r.title, events), null);
        var sb = new StringBuilder($"{r.title}, {events.Count} event(s):\n");
        foreach (var e in events)
        {
            string at = e.AllDay ? "all day" : e.Start.ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) + "–" + e.End.ToString("HH:mm", CultureInfo.InvariantCulture);
            sb.Append($"- {e.Title} ({at}{(e.Location.Length > 0 ? ", " + e.Location : "")})\n");
        }
        return (sb.ToString(), new EventsBlock(r.title, events), null);
    }
}

/// <summary>One reading of the PC (the system tool): processor over a third of a second, memory, battery, disk C:, uptime.</summary>
internal static class SystemSnapshot
{
    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);

    public static SystemReading Read()
    {
        double cpu = 0;
        if (GetSystemTimes(out long i0, out long k0, out long u0))
        {
            Thread.Sleep(330);
            if (GetSystemTimes(out long i1, out long k1, out long u1) && k1 + u1 > k0 + u0)
                cpu = Math.Clamp(100.0 * (1 - (double)(i1 - i0) / (k1 + u1 - k0 - u0)), 0, 100);
        }
        const double GB = 1024.0 * 1024 * 1024;
        var m = new MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<MEMORYSTATUSEX>() };
        double used = 0, total = 0;
        if (GlobalMemoryStatusEx(ref m))
        {
            used = (m.ullTotalPhys - m.ullAvailPhys) / GB;
            total = m.ullTotalPhys / GB;
        }
        int battery = -1;
        bool charging = false;
        if (GetSystemPowerStatus(out var ps) && (ps.BatteryFlag & 128) == 0 && ps.BatteryLifePercent <= 100)
        {
            battery = ps.BatteryLifePercent;
            charging = ps.ACLineStatus == 1;
        }
        double free = 0, size = 0;
        try
        {
            var c = new DriveInfo(Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\");
            free = c.AvailableFreeSpace / GB;
            size = c.TotalSize / GB;
        }
        catch (IOException) { }
        return new SystemReading(cpu, used, total, battery, charging, free, size, Environment.TickCount64 / 3_600_000.0, DateTime.Now);
    }
}
