using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;

namespace Palette;

/// <summary>
/// Earlier chats: Ctrl+H in the chat lists the conversations kept (ChatStore), the latest first, with
/// "New chat" on top; typing filters them by title; Enter opens one where it was left, Shift+Delete
/// deletes it, Esc goes back to the chat. And, after each answer, the work done on the side: a short
/// title for a new conversation, and a running summary of what has scrolled out of the model's view.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>Ctrl+H: the list of conversations.</summary>
    private void ShowChats(string filter = "")
    {
        string f = filter.Trim();
        Interlocked.Increment(ref _queryVersion);
        SetView(View.Chats, "Earlier chats · type to filter");
        if (Input.Text != filter) SetInputText(filter);
        _latestQuery = "";
        ResetPreview();
        Items.Clear();
        if (f.Length == 0) Items.Add(ChatRow("chat:new", "New chat", "Start a new conversation · Ctrl+N"));
        string? current = _conversation?.Id ?? ChatStore.CurrentId;
        foreach (var e in ChatStore.List())
        {
            if (f.Length > 0 && !e.Title.Contains(f, StringComparison.OrdinalIgnoreCase)
                             && !e.Preview.Contains(f, StringComparison.OrdinalIgnoreCase)) continue;
            string when = WhenSaid(e.Updated);
            string sub = (e.Id == current ? "Current · " : "") + $"{when} · {e.Messages} messages" + (e.Preview.Length > 0 ? $" · {e.Preview}" : "");
            Items.Add(ChatRow("chat:" + e.Id, e.Title, sub));
        }
        if (Items.Count > 0) ResultsList.SelectedIndex = f.Length == 0 && Items.Count > 1 ? 1 : 0;
        _holdHeight = false;
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
    }

    private static HitVm ChatRow(string key, string title, string subtitle)
    {
        var vm = new HitVm(new Native.Hit(title, subtitle, "Ask", null, null, Does: key), chip: "");
        vm.Describe(subtitle, null);
        return vm;
    }

    private static string WhenSaid(DateTime t)
    {
        if (t.Date == DateTime.Today) return "Today " + t.ToString("t", CultureInfo.CurrentCulture);
        if (t.Date == DateTime.Today.AddDays(-1)) return "Yesterday";
        if ((DateTime.Today - t.Date).TotalDays < 7) return t.ToString("dddd", CultureInfo.CurrentCulture);
        return t.ToString(t.Year == DateTime.Today.Year ? "d MMMM" : "d MMMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>Enter on the list: open that conversation where it was left (or start a new one).</summary>
    private void OpenChatRow(int index)
    {
        if (index < 0 || index >= Items.Count || Items[index].Does is not { } key || !key.StartsWith("chat:")) return;
        string id = key[5..];
        if (id == "new")
        {
            NewChat();
            return;
        }
        if (_conversation?.Id != id)
        {
            if (_conversation is { Busy: false } old && old.Messages.Count > 0) ChatStore.Save(old, makeCurrent: false);
            _conversation = ChatStore.Open(id);
        }
        StartChat("");
    }

    /// <summary>Shift+Delete on the list: delete that conversation for good.</summary>
    private void DeleteChatRow(int index)
    {
        if (index < 0 || index >= Items.Count || Items[index].Does is not { } key || !key.StartsWith("chat:") || key == "chat:new") return;
        string id = key[5..];
        if (_conversation?.Id == id)
        {
            CancelChat();
            _conversation = null;
        }
        ChatStore.Delete(id);
        ShowChats(Input.Text);
        if (index < Items.Count) ResultsList.SelectedIndex = index;
    }

    // ------------------------------------------------------------------ after each answer

    /// <summary>
    /// The side work after an answer, one request after the other (the model answers one at a time): what
    /// the message said about the user (MemoryNotice), a title for a new conversation, and the running
    /// summary once older messages have left the model's view.
    /// </summary>
    private async Task AfterAnswer(ChatAnswer chat, ChatMsg answer, int port, string message)
    {
        try
        {
            if (_settings.ChatMemory && MemoryNotice.MayBePersonal(message))
            {
                var changes = await MemoryNotice.Notice(port, message, CancellationToken.None);
                if (changes.Count > 0)
                {
                    answer.Blocks.Add(new MemoryBlock(changes));
                    RenderChat(chat);
                }
            }
            bool changed = false;
            if (chat.Title.Length == 0 && answer.Text.Length > 0) changed |= await Title(chat, port);
            changed |= await Summarize(chat, port);
            if (changed || answer.Blocks.Any(b => b is MemoryBlock)) Save(chat);
        }
        catch (Exception ex)
        {
            App.Log("after the answer: " + ex.Message);
        }
    }

    /// <summary>Save a conversation; Tab picks up the one on screen.</summary>
    private void Save(ChatAnswer chat)
    {
        if (!chat.Busy) ChatStore.Save(chat, makeCurrent: ReferenceEquals(chat, _conversation));
    }

    private static readonly HttpClient SideHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>One short request to the model, answered whole (no tools, no thinking).</summary>
    private static async Task<string> AskModel(int port, string system, string user, int maxTokens)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("messages");
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", system); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("role", "user"); w.WriteString("content", user); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteNumber("max_tokens", maxTokens);
            w.WriteNumber("temperature", 0.2);
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
        using var resp = await SideHttp.SendAsync(req);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "").Trim();
    }

    /// <summary>A title of a few words for a new conversation, from its first exchange.</summary>
    private static async Task<bool> Title(ChatAnswer chat, int port)
    {
        var ask = chat.Messages.FirstOrDefault(m => m.Role == "user");
        var reply = chat.Messages.FirstOrDefault(m => m.Role == "assistant" && m.Text.Length > 0);
        if (ask == null || reply == null) return false;
        string said = $"User: {ask.Text}\nAssistant: {(reply.Text.Length > 400 ? reply.Text[..400] : reply.Text)}";
        string title = await AskModel(port,
            "Write a title of two to five words for this conversation, in the user's language, like a note's title. " +
            "Answer with the title alone: no quotes, no full stop.", said, 20);
        title = title.Trim().Trim('"', '“', '”', '«', '»', '*', '.', ' ');
        if (title.Length == 0 || title.Length > 60) return false;
        chat.Title = title;
        return true;
    }

    /// <summary>
    /// Once the model is sent more than <see cref="WindowMax"/> messages after the summary, fold all but the
    /// last few into the conversation's running summary, which the model is given ahead of what it sees: the
    /// start of what it sees moves then, and stays put for the next few exchanges (ChatWindowStart).
    /// </summary>
    private static async Task<bool> Summarize(ChatAnswer chat, int port)
    {
        var shown = Enumerable.Range(chat.SummaryUpTo, chat.Messages.Count - chat.SummaryUpTo).Where(i => Counts(chat.Messages[i])).ToList();
        if (shown.Count <= WindowMax) return false;
        int start = FromQuestion(chat, shown[^WindowKeep]);
        if (start <= chat.SummaryUpTo) return false;
        var part = new StringBuilder();
        for (int i = chat.SummaryUpTo; i < start; i++)
        {
            var m = chat.Messages[i];
            string text = m.Role == "user" ? m.Text : m.Text[Math.Min(m.ReplyStart, m.Text.Length)..];
            if (text.Length > 1200) text = text[..1200] + "…";
            part.Append(m.Role == "user" ? "User: " : "Palette: ").Append(text.Trim()).Append('\n');
        }
        string summary = await AskModel(port,
            "You keep a running summary of a conversation between a user and Palette, an assistant on their PC, so that Palette " +
            "remembers what was said earlier. Fold the new part into the summary so far. At most 150 words of plain prose: what the " +
            "user asked for and why, what was found, done or decided, and the names of files, people, places and numbers that may " +
            "matter later. Answer with the summary alone.",
            (chat.Summary.Length > 0 ? $"Summary so far: {chat.Summary}\n\n" : "") + $"New part of the conversation:\n{part}", 300);
        if (summary.Length == 0) return false;
        chat.Summary = summary;
        chat.SummaryUpTo = start;
        App.Log($"chat summary up to {start}: {summary}");
        return true;
    }
}
