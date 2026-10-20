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

/// <summary>
/// The chat's tools (offered by chat.rs TOOLS): each runs off the UI thread and gives the model a short
/// text, and the answer a card to show. All of them only read: nothing is opened, changed or sent.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>What the brain says while a tool runs.</summary>
    private static string ToolHint(string name) => name switch
    {
        "find_files" => "Looking for your files…",
        "search_documents" => "Reading your documents…",
        "get_weather" => "Checking the weather…",
        "get_calendar" => "Reading your calendar…",
        _ => "Looking it up…",
    };

    /// <summary>Run a tool: the text for the model, a card (or null) and small print for under the answer (or null).</summary>
    private async Task<(string result, object? block, string? note)> RunTool(string name, string arguments, CancellationToken ct)
    {
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
        try
        {
            return name switch
            {
                "find_files" => await FindFilesTool(Arg("query"), Arg("period"), Arg("kind")),
                "search_documents" => await SearchDocumentsTool(Arg("question")),
                "get_weather" => await WeatherTool(Arg("place"), ct),
                "get_calendar" => await CalendarTool(Arg("period"), Arg("date"), ct),
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
        var sb = new StringBuilder($"{files.Count} file(s), best first (the user sees them as cards):\n");
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
        return (docs.Text, null, "From your documents: " + string.Join(" · ", docs.Names.Select((n, i) => $"[{i + 1}] {n}")));
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
