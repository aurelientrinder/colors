using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palette;

/// <summary>A thing the chat model remembers about the user: one short sentence ("Lives in Toulouse.").</summary>
public sealed record Memory(string Id, string Text, DateTime Created, DateTime Updated);

/// <summary>
/// Long-term memory: what the chat model has been told about the user and asked to keep, across
/// conversations. The model writes it with its remember and forget tools (ChatTools.cs); every chat
/// opens with it in the model's instructions (chat.rs open). Kept in %LOCALAPPDATA%\palette\memory.json,
/// on this PC only; seen and forgotten one by one in Settings.
/// </summary>
internal static class MemoryStore
{
    /// <summary>The most remembered facts a chat is given (the newest first): about 1,500 tokens at most.</summary>
    public const int InPrompt = 80;

    private static readonly object Gate = new();
    private static List<Memory>? _all;

    /// <summary>The file's write time when it was last read or saved here: a different one means it was edited outside.</summary>
    private static DateTime _stamp;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "memory.json");

    private static DateTime Stamp() => File.Exists(FilePath) ? File.GetLastWriteTimeUtc(FilePath) : DateTime.MinValue;

    /// <summary>Everything remembered, read again when the file changed on disk since it was last read or saved.</summary>
    private static List<Memory> All()
    {
        var stamp = Stamp();
        if (_all != null && stamp == _stamp) return _all;
        try
        {
            _all = stamp == DateTime.MinValue ? new() : JsonSerializer.Deserialize(File.ReadAllText(FilePath), MemoryJson.Default.ListMemory);
            _stamp = stamp;
        }
        catch (Exception ex)
        {
            // Half written, or not valid JSON: keep what was known, and try again next time.
            App.Log("memory not loaded: " + ex.Message);
        }
        return _all ??= new();
    }

    /// <summary>Everything remembered, newest first.</summary>
    public static List<Memory> List()
    {
        lock (Gate) return All().OrderByDescending(m => m.Updated).ToList();
    }

    private static void Save()
    {
        try
        {
            string json = JsonSerializer.Serialize(All(), MemoryJson.Default.ListMemory);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", json);
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
            _stamp = Stamp();
        }
        catch (Exception ex)
        {
            App.Log("memory not saved: " + ex.Message);
        }
    }

    /// <summary>The words of a sentence that carry its meaning, lowercased.</summary>
    private static HashSet<string> Words(string s) =>
        s.ToLowerInvariant().Split(' ', ',', '.', ';', ':', '!', '?', '\'', '"', '(', ')', '-')
            .Where(w => w.Length >= 3).ToHashSet();

    /// <summary>How alike two sentences are, by the words they share (0 to 1).</summary>
    private static double Alike(string a, string b)
    {
        var x = Words(a);
        var y = Words(b);
        if (x.Count == 0 || y.Count == 0) return 0;
        return (double)x.Intersect(y).Count() / x.Union(y).Count();
    }

    /// <summary>The memory best matching <paramref name="text"/>, if any is close enough.</summary>
    private static Memory? Closest(string text, double atLeast)
    {
        var best = All().Select(m => (m, score: Alike(m.Text, text))).Where(t => t.score >= atLeast).OrderByDescending(t => t.score).FirstOrDefault();
        return best.m;
    }

    /// <summary>
    /// Remember <paramref name="text"/>. It takes the place of <paramref name="replaces"/> (an older fact it
    /// updates: "Lives in Lyon." for "Lives in Toulouse."), or of a fact that says nearly the same.
    /// Returns what was stored and what it replaced.
    /// </summary>
    public static (Memory saved, Memory? replaced) Remember(string text, string? replaces)
    {
        text = text.Trim();
        if (text.Length > 0 && !".!?".Contains(text[^1])) text += ".";
        lock (Gate)
        {
            var old = (replaces is { Length: > 0 } r ? Closest(r, 0.5) : null) ?? Closest(text, 0.75);
            var now = DateTime.Now;
            Memory saved;
            if (old != null)
            {
                All().Remove(old);
                saved = old with { Text = text, Updated = now };
            }
            else
            {
                saved = new Memory(Guid.NewGuid().ToString("N")[..12], text, now, now);
            }
            All().Add(saved);
            Save();
            return (saved, old);
        }
    }

    /// <summary>Forget the facts about <paramref name="about"/>: the best match, and any that match about as well. Returns them.</summary>
    public static List<Memory> Forget(string about)
    {
        lock (Gate)
        {
            var scored = All().Select(m => (m, score: Math.Max(Alike(m.Text, about),
                m.Text.Contains(about, StringComparison.OrdinalIgnoreCase) ? 1 : 0))).Where(t => t.score >= 0.25).ToList();
            if (scored.Count == 0) return new();
            double top = scored.Max(t => t.score);
            var gone = scored.Where(t => t.score >= top * 0.8).Select(t => t.m).ToList();
            foreach (var m in gone) All().Remove(m);
            Save();
            return gone;
        }
    }

    /// <summary>Forget one fact (Settings, or Undo of a remember).</summary>
    public static void Remove(string id)
    {
        lock (Gate)
        {
            if (All().RemoveAll(m => m.Id == id) > 0) Save();
        }
    }

    /// <summary>Put a fact back (Undo of a forget or of a replacement).</summary>
    public static void Restore(Memory m)
    {
        lock (Gate)
        {
            All().RemoveAll(x => x.Id == m.Id);
            All().Add(m);
            Save();
        }
    }

    /// <summary>Forget everything.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            All().Clear();
            Save();
        }
    }

    /// <summary>What a chat is told it remembers, or "" when there is nothing.</summary>
    public static string ForPrompt()
    {
        var list = List().Take(InPrompt).ToList();
        if (list.Count == 0) return "";
        var sb = new StringBuilder("What you remember about the user from earlier conversations (use it when it helps; do not recite it unasked; ")
            .Append("update it with remember when they tell you it changed):\n");
        foreach (var m in list) sb.Append("- ").Append(m.Text).Append('\n');
        return sb.ToString();
    }
}

/// <summary>One change a message made to the memory: "added", "updated" (Before is what it replaced) or "removed".</summary>
public sealed record MemoryChange(string Kind, Memory Now, Memory? Before = null);

/// <summary>What a message changed in the memory, shown under the answer (with Undo).</summary>
public sealed record MemoryBlock(List<MemoryChange> Changes);

/// <summary>
/// Noticing what the user says about themselves. The chat model was poor at calling a remember tool
/// (it said "I'll remember that" and did nothing), so after each message that may say something
/// personal, the model is asked apart, in a short request of its own, what changes in the profile;
/// it answers in lines (+ new, - removed, ~ old => new), about a second's work, after the answer.
/// </summary>
internal static class MemoryNotice
{
    private const string Instructions =
        "You keep a short profile of lasting facts about the user, for an assistant on their PC. Given the profile and the user's latest " +
        "message, say what changes. Note only lasting facts about the user: their name or what they want to be called, where they live, their " +
        "work or studies, the people in their life, their tastes, health, habits, plans, and how they like to be answered. A message can hold " +
        "several: note each. Ignore passing details (what they ate today, how they feel right now), questions, requests for help, and facts " +
        "about the world. If they ask you to remember something, note it; if they ask you to forget something, remove the matching facts. " +
        "Write each fact as a short sentence without a subject, in the user's language (Lives in Lyon.). Usually nothing changes.\n" +
        "Answer with one line per change and nothing else: + a new fact; - a fact of the profile to remove, as written there; " +
        "~ a fact of the profile => the fact that replaces it. When nothing changes, answer NONE.\n\n" +
        "Examples, with the profile: Lives in Paris. Plays the guitar.\n" +
        "Message: I'm Sam, I just started as a nurse at the Rennes hospital and moved there last week\n" +
        "+ Is called Sam.\n+ Works as a nurse at the Rennes hospital.\n~ Lives in Paris. => Lives in Rennes.\n" +
        "Message: what's a good recipe for crepes?\nNONE\n" +
        "Message: I gave up the guitar, and please remember my daughter Emma turns 6 in March\n" +
        "- Plays the guitar.\n+ Has a daughter, Emma, who turns 6 in March.";

    /// <summary>Words that show a message speaks of the user (or asks to remember or forget): only these are looked at.</summary>
    private static readonly System.Text.RegularExpressions.Regex Personal = new(
        @"\b(i|i'm|im|i've|i'd|i'll|my|me|mine|myself|we|our|remember|forget|call me|je|j'|j’|moi|mon|ma|mes|nous|notre|nos|souviens|oublie|appelle-moi)\b",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool MayBePersonal(string message) => !message.TrimStart().StartsWith('?') && Personal.IsMatch(message);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Ask the model (on <paramref name="port"/>) what <paramref name="message"/> changes, and make those changes.</summary>
    public static async Task<List<MemoryChange>> Notice(int port, string message, CancellationToken ct)
    {
        var profile = MemoryStore.List();
        var user = new StringBuilder("Profile:\n");
        foreach (var m in profile) user.Append("- ").Append(m.Text).Append('\n');
        if (profile.Count == 0) user.Append("(empty)\n");
        user.Append("\nLatest message: ").Append(message);

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("messages");
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", Instructions); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("role", "user"); w.WriteString("content", user.ToString()); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteNumber("max_tokens", 200);
            w.WriteNumber("temperature", 0.1);
            w.WriteBoolean("cache_prompt", true);
            w.WriteStartObject("chat_template_kwargs");
            w.WriteBoolean("enable_thinking", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json"),
        };
        using var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        string reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        App.Log($"memory notice for '{message}': {reply.Replace('\n', '|')}");
        return Apply(reply);
    }

    /// <summary>Make the changes written in <paramref name="reply"/> (the lines + - ~); what was done.</summary>
    public static List<MemoryChange> Apply(string reply)
    {
        var done = new List<MemoryChange>();
        foreach (var raw in reply.Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length < 3) continue;
            string body = line[1..].Trim();
            switch (line[0])
            {
                case '+':
                    var (saved, replaced) = MemoryStore.Remember(body, null);
                    done.Add(replaced == null ? new MemoryChange("added", saved) : new MemoryChange("updated", saved, replaced));
                    break;
                case '~':
                    int arrow = body.IndexOf("=>", StringComparison.Ordinal);
                    if (arrow < 0) break;
                    var (now, before) = MemoryStore.Remember(body[(arrow + 2)..].Trim(), body[..arrow].Trim());
                    done.Add(before == null ? new MemoryChange("added", now) : new MemoryChange("updated", now, before));
                    break;
                case '-':
                    foreach (var gone in MemoryStore.Forget(body)) done.Add(new MemoryChange("removed", gone));
                    break;
            }
        }
        return done;
    }

    /// <summary>Take a change back (Undo under an answer).</summary>
    public static void Undo(MemoryChange c)
    {
        switch (c.Kind)
        {
            case "added": MemoryStore.Remove(c.Now.Id); break;
            case "updated" when c.Before != null: MemoryStore.Restore(c.Before); break;
            case "removed": MemoryStore.Restore(c.Now); break;
        }
    }
}

[JsonSerializable(typeof(List<Memory>))]
internal sealed partial class MemoryJson : JsonSerializerContext
{
}
