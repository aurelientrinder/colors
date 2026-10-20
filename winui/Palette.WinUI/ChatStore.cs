using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palette;

/// <summary>
/// The conversation, kept between palette openings and across restarts: Tab picks it up where it was
/// left (Ctrl+N starts a new one). Saved to %LOCALAPPDATA%\palette\chat.json after each answer and on
/// leaving the chat; only on this PC. The last <see cref="Keep"/> messages are kept.
/// </summary>
internal static class ChatStore
{
    public const int Keep = 80;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "chat.json");

    private static readonly object WriteGate = new();

    public sealed record SavedCall(string Id, string Name, string Arguments, string Result);

    public sealed record SavedRound(string Content, List<SavedCall> Calls);

    public sealed record SavedBlock(
        string Kind,
        List<Native.FileFound>? Files = null,
        WebAnswers.Weather? Weather = null,
        string? Title = null,
        List<CalEvent>? Events = null,
        SystemReading? System = null,
        List<SourceDoc>? Sources = null,
        List<MemoryChange>? Memory = null);

    public sealed record SavedMsg(
        string Role, string Text, string Sent, string Note, string Thinking, long ThinkMs, bool Think, int ReplyStart,
        List<SavedRound> Rounds, List<SavedBlock> Blocks);

    public sealed record SavedChat(DateTime Saved, List<SavedMsg> Messages);

    /// <summary>The saved conversation, or an empty list.</summary>
    public static List<ChatMsg> Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new();
            var saved = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ChatJson.Default.SavedChat);
            if (saved == null) return new();
            var list = new List<ChatMsg>();
            foreach (var s in saved.Messages)
            {
                var m = new ChatMsg
                {
                    Role = s.Role, Text = s.Text, Sent = s.Sent, Note = s.Note, Thinking = s.Thinking, ThinkMs = s.ThinkMs,
                    Think = s.Think, ReplyStart = Math.Min(s.ReplyStart, s.Text.Length),
                };
                foreach (var r in s.Rounds)
                {
                    var round = new ToolRound { Content = r.Content };
                    foreach (var c in r.Calls) round.Calls.Add(new ToolCall { Id = c.Id, Name = c.Name, Arguments = c.Arguments, Result = c.Result });
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
        catch (Exception ex)
        {
            App.Log("chat not loaded: " + ex.Message);
            return new();
        }
    }

    /// <summary>Save the conversation (a snapshot is taken here, the write happens off the UI thread).</summary>
    public static void Save(IReadOnlyList<ChatMsg> messages)
    {
        var snapshot = new SavedChat(DateTime.Now, messages.Skip(Math.Max(0, messages.Count - Keep)).Select(m => new SavedMsg(
            m.Role, m.Text, m.Sent, m.Note, m.Thinking, m.ThinkMs, m.Think, m.ReplyStart,
            m.Rounds.Select(r => new SavedRound(r.Content, r.Calls.Select(c => new SavedCall(c.Id, c.Name, c.Arguments, c.Result)).ToList())).ToList(),
            m.Blocks.Select(Saved).Where(b => b != null).Select(b => b!).ToList())).ToList());
        Task.Run(() =>
        {
            try
            {
                string json = JsonSerializer.Serialize(snapshot, ChatJson.Default.SavedChat);
                lock (WriteGate)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
                    string tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    File.Move(tmp, FilePath, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                App.Log("chat not saved: " + ex.Message);
            }
        });
    }

    /// <summary>Forget the conversation (Ctrl+N).</summary>
    public static void Clear()
    {
        try
        {
            lock (WriteGate) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            App.Log("chat not cleared: " + ex.Message);
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
        _ => null,
    };
}

[JsonSerializable(typeof(ChatStore.SavedChat))]
internal sealed partial class ChatJson : JsonSerializerContext
{
}
