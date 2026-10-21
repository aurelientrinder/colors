using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palette;

/// <summary>
/// The conversations, kept between palette openings and across restarts, only on this PC: each in
/// %LOCALAPPDATA%\palette\chats\&lt;id&gt;.json, with an index (titles, dates, which one is current) in
/// chats\index.json. Tab picks up the current one; Ctrl+N puts it away and starts another; Ctrl+H lists
/// them (ChatList.cs). A conversation keeps its last <see cref="Keep"/> messages.
/// </summary>
internal static class ChatStore
{
    public const int Keep = 80;

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "chats");

    private static string IndexPath => Path.Combine(Dir, "index.json");

    /// <summary>Where the single conversation was kept before there were several (moved on first load).</summary>
    private static string OldPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "chat.json");

    private static readonly object Gate = new();

    public sealed record SavedCall(string Id, string Name, string Arguments, string Result, string? Label = null, string? Outcome = null, long Ms = 0);

    public sealed record SavedRound(string Content, List<SavedCall> Calls);

    public sealed record SavedBlock(
        string Kind,
        List<Native.FileFound>? Files = null,
        WebAnswers.Weather? Weather = null,
        string? Title = null,
        List<CalEvent>? Events = null,
        SystemReading? System = null,
        List<SourceDoc>? Sources = null,
        List<MemoryChange>? Memory = null,
        List<Reminder>? Reminders = null,
        SavedAction? Action = null,
        List<WebSource>? Web = null);

    /// <summary>An action under an answer (ChatActions.cs): one still asking when Palette closed was never done.</summary>
    public sealed record SavedAction(string Kind, string Question, List<string> Paths, string State, string Message, ActionRecord? Record);

    public sealed record SavedMsg(
        string Role, string Text, string Sent, string Note, string Thinking, long ThinkMs, bool Think, int ReplyStart,
        List<SavedRound> Rounds, List<SavedBlock> Blocks, string? LateThinking = null, long LateThinkMs = 0);

    public sealed record SavedChat(DateTime Saved, List<SavedMsg> Messages, string? Id = null, string? Title = null,
        string? Summary = null, int SummaryUpTo = 0);

    /// <summary>A conversation as the list shows it.</summary>
    public sealed record Entry(string Id, string Title, DateTime Updated, int Messages, string Preview);

    public sealed record Index(string? Current, List<Entry> Entries);

    // ------------------------------------------------------------------ the index

    private static Index? _index;

    private static Index Idx()
    {
        if (_index != null) return _index;
        try
        {
            if (File.Exists(IndexPath)) _index = JsonSerializer.Deserialize(File.ReadAllText(IndexPath), ChatJson.Default.Index);
        }
        catch (Exception ex)
        {
            App.Log("chat index not loaded: " + ex.Message);
        }
        _index ??= new Index(null, new());
        Migrate();
        return _index;
    }

    /// <summary>The one conversation of before becomes the current one of the list.</summary>
    private static void Migrate()
    {
        try
        {
            if (!File.Exists(OldPath)) return;
            var old = JsonSerializer.Deserialize(File.ReadAllText(OldPath), ChatJson.Default.SavedChat);
            if (old != null && old.Messages.Count > 0)
            {
                string id = ChatAnswer.NewId();
                Directory.CreateDirectory(Dir);
                File.WriteAllText(PathOf(id), JsonSerializer.Serialize(old with { Id = id }, ChatJson.Default.SavedChat));
                var first = old.Messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "Chat";
                _index = new Index(id, _index!.Entries.Append(new Entry(id, Shorten(first, 50), old.Saved, old.Messages.Count, Shorten(first, 90))).ToList());
                WriteIndex();
            }
            File.Delete(OldPath);
        }
        catch (Exception ex)
        {
            App.Log("old chat not moved: " + ex.Message);
        }
    }

    private static void WriteIndex()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(IndexPath + ".tmp", JsonSerializer.Serialize(_index!, ChatJson.Default.Index));
        File.Move(IndexPath + ".tmp", IndexPath, overwrite: true);
    }

    private static string PathOf(string id) => Path.Combine(Dir, id + ".json");

    private static string Shorten(string s, int max)
    {
        s = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";
    }

    /// <summary>The conversations, the latest first.</summary>
    public static List<Entry> List()
    {
        lock (Gate) return Idx().Entries.OrderByDescending(e => e.Updated).ToList();
    }

    public static string? CurrentId
    {
        get { lock (Gate) return Idx().Current; }
    }

    // ------------------------------------------------------------------ one conversation

    /// <summary>The current conversation, or a new one.</summary>
    public static ChatAnswer LoadCurrent()
    {
        string? id;
        lock (Gate) id = Idx().Current;
        return (id != null ? Open(id) : null) ?? new ChatAnswer();
    }

    /// <summary>A conversation from the list (it becomes the current one), or null if it is gone.</summary>
    public static ChatAnswer? Open(string id)
    {
        try
        {
            if (!File.Exists(PathOf(id))) return null;
            var saved = JsonSerializer.Deserialize(File.ReadAllText(PathOf(id)), ChatJson.Default.SavedChat);
            if (saved == null) return null;
            var chat = new ChatAnswer { Id = id, Title = saved.Title ?? "", Summary = saved.Summary ?? "", SummaryUpTo = saved.SummaryUpTo };
            chat.Messages.AddRange(Messages(saved));
            chat.SummaryUpTo = Math.Min(chat.SummaryUpTo, chat.Messages.Count);
            SetCurrent(id);
            return chat;
        }
        catch (Exception ex)
        {
            App.Log("chat not loaded: " + ex.Message);
            return null;
        }
    }

    /// <summary>Which conversation Tab picks up (null: a new one).</summary>
    public static void SetCurrent(string? id)
    {
        lock (Gate)
        {
            if (Idx().Current == id) return;
            _index = Idx() with { Current = id };
            try { WriteIndex(); }
            catch (Exception ex) { App.Log("chat index not saved: " + ex.Message); }
        }
    }

    private static List<ChatMsg> Messages(SavedChat saved)
    {
        var list = new List<ChatMsg>();
        foreach (var s in saved.Messages)
        {
            var m = new ChatMsg
            {
                Role = s.Role, Text = s.Text, Sent = s.Sent, Note = s.Note, Thinking = s.Thinking, ThinkMs = s.ThinkMs,
                Think = s.Think, ReplyStart = Math.Min(s.ReplyStart, s.Text.Length),
                LateThinking = s.LateThinking ?? "", LateThinkMs = s.LateThinkMs,
            };
            foreach (var r in s.Rounds)
            {
                var round = new ToolRound { Content = r.Content };
                foreach (var c in r.Calls) round.Calls.Add(new ToolCall { Id = c.Id, Name = c.Name, Arguments = c.Arguments, Result = c.Result, Label = c.Label ?? "", Outcome = c.Outcome ?? "", Done = true, Ms = c.Ms });
                m.Rounds.Add(round);
            }
            foreach (var b in s.Blocks)
            {
                object? block = b.Kind switch
                {
                    "files" when b.Files != null => new FilesBlock(b.Files),
                    "weather" when b.Weather != null => new WeatherBlock(b.Weather),
                    "events" when b.Events != null => new EventsBlock(b.Title ?? "", b.Events),
                    "system" when b.System != null => new SystemBlock(b.System),
                    "sources" when b.Sources != null => new SourcesBlock(b.Sources),
                    "memory" when b.Memory != null => new MemoryBlock(b.Memory),
                    "web" when b.Web != null => new WebSourcesBlock(b.Web),
                    "reminders" when b.Reminders != null => new ReminderBlock(b.Title ?? "set", b.Reminders),
                    "action" when b.Action is { } a => new ActionBlock
                    {
                        Kind = a.Kind, Question = a.Question, Paths = a.Paths, Record = a.Record,
                        State = a.State is "asking" or "working" ? "stopped" : a.State,
                        Message = a.State == "asking" ? "Not done: Palette closed before you answered"
                            : a.State == "working" ? "Stopped: Palette closed while it was being done" : a.Message,
                    },
                    _ => null,
                };
                if (block != null) m.Blocks.Add(block);
            }
            list.Add(m);
        }
        // An answer cut off by a restart while it was being written has nothing after the question.
        if (list.Count > 0 && list[^1].Role == "user") list.RemoveAt(list.Count - 1);
        return list;
    }

    /// <summary>Save a conversation and make it the current one (a snapshot is taken here, the write happens off the UI thread).</summary>
    public static void Save(ChatAnswer chat, bool makeCurrent = true)
    {
        var messages = chat.Messages;
        if (messages.Count == 0) return;
        int skip = Math.Max(0, messages.Count - Keep);
        var snapshot = new SavedChat(DateTime.Now, messages.Skip(skip).Select(m => new SavedMsg(
            m.Role, m.Text, m.Sent, m.Note, m.Thinking, m.ThinkMs, m.Think, m.ReplyStart,
            m.Rounds.Select(r => new SavedRound(r.Content, r.Calls.Select(c => new SavedCall(c.Id, c.Name, c.Arguments, c.Result, c.Label, c.Outcome, c.Ms)).ToList())).ToList(),
            m.Blocks.Select(Saved).Where(b => b != null).Select(b => b!).ToList(), m.LateThinking, m.LateThinkMs)).ToList(),
            chat.Id, chat.Title, chat.Summary, Math.Max(0, chat.SummaryUpTo - skip));
        string firstAsk = messages.FirstOrDefault(m => m.Role == "user")?.Text ?? "";
        string lastAsk = messages.LastOrDefault(m => m.Role == "user")?.Text ?? "";
        var entry = new Entry(chat.Id, chat.Title.Length > 0 ? chat.Title : Shorten(firstAsk, 50), snapshot.Saved, snapshot.Messages.Count, Shorten(lastAsk, 90));
        Task.Run(() =>
        {
            try
            {
                string json = JsonSerializer.Serialize(snapshot, ChatJson.Default.SavedChat);
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    File.WriteAllText(PathOf(entry.Id) + ".tmp", json);
                    File.Move(PathOf(entry.Id) + ".tmp", PathOf(entry.Id), overwrite: true);
                    var entries = Idx().Entries.Where(e => e.Id != entry.Id).Append(entry).ToList();
                    _index = new Index(makeCurrent ? entry.Id : Idx().Current, entries);
                    WriteIndex();
                }
            }
            catch (Exception ex)
            {
                App.Log("chat not saved: " + ex.Message);
            }
        });
    }

    /// <summary>Delete a conversation for good.</summary>
    public static void Delete(string id)
    {
        lock (Gate)
        {
            try
            {
                File.Delete(PathOf(id));
                var idx = Idx();
                _index = new Index(idx.Current == id ? null : idx.Current, idx.Entries.Where(e => e.Id != id).ToList());
                WriteIndex();
            }
            catch (Exception ex)
            {
                App.Log("chat not deleted: " + ex.Message);
            }
        }
    }

    private static SavedBlock? Saved(object block) => block switch
    {
        FilesBlock f => new SavedBlock("files", Files: f.Files),
        WeatherBlock w => new SavedBlock("weather", Weather: w.Weather),
        EventsBlock e => new SavedBlock("events", Title: e.Title, Events: e.Events),
        SystemBlock s => new SavedBlock("system", System: s.Reading),
        SourcesBlock s => new SavedBlock("sources", Sources: s.Docs),
        MemoryBlock m => new SavedBlock("memory", Memory: m.Changes),
        ReminderBlock r => new SavedBlock("reminders", Title: r.Kind, Reminders: r.Reminders),
        WebSourcesBlock w => new SavedBlock("web", Web: w.Sources),
        ActionBlock a => new SavedBlock("action", Action: new SavedAction(a.Kind, a.Question, a.Paths.ToList(), a.State, a.Message, a.Record)),
        _ => null,
    };
}

[JsonSerializable(typeof(ChatStore.SavedChat))]
[JsonSerializable(typeof(ChatStore.Index))]
internal sealed partial class ChatJson : JsonSerializerContext
{
}
