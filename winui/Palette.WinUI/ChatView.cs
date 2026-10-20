using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Palette;

/// <summary>One message of a chat.</summary>
public sealed class ChatMsg
{
    public required string Role { get; init; }          // "user" or "assistant"
    public string Text { get; set; } = "";
    /// <summary>What is going on before the first word arrives ("Searching your documents…").</summary>
    public string Hint { get; set; } = "";
    /// <summary>What the model was actually sent for a user message (the text, with document excerpts before it).</summary>
    public string Sent { get; set; } = "";
    /// <summary>Small print under an answer: the files it drew on.</summary>
    public string Note { get; set; } = "";
    /// <summary>The model's reasoning, streamed apart from the answer (empty when it did not think).</summary>
    public string Thinking { get; set; } = "";
    /// <summary>How long it thought, in milliseconds (set once the answer begins).</summary>
    public long ThinkMs { get; set; }
    /// <summary>Should the model think before answering (on the user's message and on the answer to it)?</summary>
    public bool Think { get; set; }
    /// <summary>For an answer: the tools it used, round by round (sent back with the conversation).</summary>
    public List<ToolRound> Rounds { get; } = new();
    /// <summary>For an answer: what the tools found, shown as cards under the text (FilesBlock, WeatherBlock, EventsBlock).</summary>
    public List<object> Blocks { get; } = new();
    /// <summary>Where in Text the latest turn of the model begins (text before it was written with tool calls).</summary>
    public int ReplyStart { get; set; }
}

/// <summary>A chat with the model that runs on this PC, shown as a card under one row.</summary>
public sealed record ChatAnswer() : WebAnswers.Answer("chat")
{
    public List<ChatMsg> Messages { get; } = new();
    public Task<Native.ChatOpened>? Opening { get; set; }
    public string Footer { get; set; } = "On this PC";
    public bool Busy { get; set; }
    public string Hint { get; set; } = "Reading the window…";
}

/// <summary>
/// Chat mode: Tab (a tap, with the search field empty or holding a question) turns the list into a
/// conversation with the model, in the same field. The model has read the window in front (and its
/// selection) before the first word is typed; a message that is not about the screen also gets the best
/// passages of your documents. Enter sends, Esc or Tab leaves. Everything runs on this PC (chat.rs).
/// </summary>
public sealed partial class MainWindow
{
    private static readonly Regex AboutFiles = new(
        @"\b(my|in my|from my)\s+(documents?|files?|notes?|pdfs?|papers?|folders?|downloads?|contracts?|lease)\b|\b(mes|dans mes)\s+(documents?|fichiers?|notes?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private ChatAnswer? _chat;
    private CancellationTokenSource? _chatCts;
    private readonly List<(FrameworkElement view, ChatMsg msg)> _chatViews = new();

    /// <summary>
    /// Messages worth reasoning about first: comparing, planning, working something out, debugging. A small
    /// model thinks about "hello" as long as about anything (40 s measured), so it thinks only for these,
    /// for long messages, and when Shift+Enter asks it to.
    /// </summary>
    private static readonly Regex Reasoning = new(
        @"\b(compare|comparison|difference between|versus|pros and cons|trade-?offs?|plan|schedule|should i|which (is|one|should)|calculate|compute|solve|prove|derive|estimate|step by step|debug|figure out|analy[sz]e|reasoning|optimi[sz]e|pourquoi|explique|compare[rz]?|calcule[rz]?|diff[ée]rence entre|devrais-je|planifie[rz]?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Does this message get the model's thinking? (Shift+Enter always does.)</summary>
    private static bool WantsThinking(string message)
    {
        int words = message.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < 4) return false;
        if (words >= 30) return true;                                         // a long, many-part request
        if (Regex.IsMatch(message, @"\d\s*[-+*/x×^%]\s*\d")) return true;     // arithmetic
        if (Reasoning.IsMatch(message)) return true;
        // "why" and "how" questions only when there is more to them than a fact to recall.
        return words >= 10 && Regex.IsMatch(message, @"^\s*(why|how)\b", RegexOptions.IgnoreCase);
    }

    private void CancelChat()
    {
        _chatCts?.Cancel();
        _chatCts = null;
    }

    /// <summary>Tab: into the chat. A question already typed is the first message.</summary>
    private void StartChat(string typed)
    {
        CancelAsk();
        CancelChat();
        var chat = new ChatAnswer();
        _chat = chat;
        Interlocked.Increment(ref _queryVersion);   // drop any search still on its way
        SetView(View.Chat, "Message");
        SetInputText("");
        _latestQuery = "";
        _chatViews.Clear();
        ChatMessages.Children.Clear();
        Items.Clear();
        var vm = new HitVm(new Native.Hit("Chat", "On this PC · Esc to leave", "Ask", null, null), chip: "") { Answer = chat };
        Items.Add(vm);
        ResultsList.SelectedIndex = 0;
        _holdHeight = false;
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
        Input.Focus(FocusState.Programmatic);

        chat.Opening = Task.Run(Native.ChatOpen);
        _ = FinishOpening(chat);
        if (typed.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length >= 3 || typed.EndsWith('?')) SendChat(typed);
    }

    private async Task FinishOpening(ChatAnswer chat)
    {
        var opened = await chat.Opening!;
        if (_chat != chat) return;
        if (!opened.Ok)
        {
            chat.Hint = opened.Error ?? "The model did not start";
        }
        else
        {
            chat.Hint = opened.Source switch
            {
                "selection" => "I've read your selection. Ask me about it, or anything else.",
                "window" => $"I've read the {opened.App} window. Ask me about it, or about your files.",
                _ => "Ask me anything, or about your files.",
            };
            chat.Footer = $"{opened.Model} · on this PC" + (opened.Source == "none" ? "" : opened.Source == "selection" ? " · reading your selection" : $" · reading the {opened.App} window");
        }
        RenderChat(chat);
    }

    /// <summary>Enter in the chat.</summary>
    private void SendChat(string text)
    {
        text = text.Trim();
        if (_chat is not { } chat || text.Length == 0 || chat.Busy) return;
        SetInputText("");
        chat.Busy = true;
        var user = new ChatMsg { Role = "user", Text = text, Sent = text, Think = Held(VirtualKey.Shift) || WantsThinking(text) };
        var answer = new ChatMsg { Role = "assistant", Think = user.Think };
        chat.Messages.Add(user);
        chat.Messages.Add(answer);
        RenderChat(chat);
        var cts = _chatCts = new CancellationTokenSource();
        _ = RunChat(chat, user, answer, cts.Token);
    }

    /// <summary>Rounds of tools one answer may take before it has to answer.</summary>
    private const int MaxToolRounds = 3;

    private async Task RunChat(ChatAnswer chat, ChatMsg user, ChatMsg answer, CancellationToken ct)
    {
        bool failed = false;
        long thinkStart = 0;
        try
        {
            var opened = await chat.Opening!;
            if (ct.IsCancellationRequested) return;
            if (!opened.Ok)
            {
                answer.Text = opened.Error ?? "The model did not start";
                failed = true;
                return;
            }
            // Documents up front when the message asks for them in so many words: a "?" in front, or
            // "my documents". Otherwise the model searches them itself when it needs to (search_documents).
            string message = user.Text;
            if (message.StartsWith('?')) message = message[1..].Trim();
            if (message != user.Text || AboutFiles.IsMatch(message))
            {
                answer.Hint = "Searching your documents…";
                RenderChat(chat);
                var docs = await Task.Run(() => Native.ChatDocs(message));
                if (ct.IsCancellationRequested) return;
                if (docs.Text.Length > 0)
                {
                    user.Sent = docs.Text + "\nMessage: " + message;
                    answer.Note = "From your documents: " + string.Join(" · ", docs.Names.Select((n, i) => $"[{i + 1}] {n}"));
                }
                else if (message != user.Text)
                {
                    user.Sent = message;
                    answer.Note = "Nothing in your documents matched";
                }
            }
            answer.Hint = "";

            // Ask; while the model asks for tools, run them, tell it what they found, and ask again. It
            // thinks (when the message calls for it) before its first move only: with the tools' results
            // in hand, it answers straight away.
            for (int round = 0; ; round++)
            {
                bool lastRound = round >= MaxToolRounds;
                string body = ChatBody(opened.System ?? "", chat, think: user.Think && round == 0, tools: lastRound ? null : opened.Tools);
                var calls = await StreamTurn(chat, answer, opened.Port, body, () => thinkStart, t => thinkStart = t, ct);
                if (calls.Count == 0 || lastRound) break;

                // What the model wrote before calling stays on screen; the next turn writes after it.
                var turn = new ToolRound { Content = answer.Text[answer.ReplyStart..] };
                answer.Rounds.Add(turn);
                foreach (var call in calls)
                {
                    answer.Hint = ToolHint(call.Name);
                    RenderChat(chat);
                    var (result, block, note) = await RunTool(call.Name, call.Arguments, ct);
                    if (ct.IsCancellationRequested) return;
                    call.Result = result;
                    turn.Calls.Add(call);
                    if (block != null) answer.Blocks.Add(block);
                    if (note != null) answer.Note = note;
                }
                answer.Hint = "";
                if (answer.Text.Length > 0 && !answer.Text.EndsWith('\n')) answer.Text += "\n\n";
                answer.ReplyStart = answer.Text.Length;
                RenderChat(chat);
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            App.Log("chat failed: " + ex.Message);
            if (answer.Text.Length == 0) answer.Text = "The model stopped answering. Try again.";
            failed = true;
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                if (answer.Thinking.Length > 0 && answer.ThinkMs == 0 && thinkStart > 0) answer.ThinkMs = Environment.TickCount64 - thinkStart;
                answer.Text = answer.Text.TrimEnd();
                if (answer.Text.Length == 0 && answer.Blocks.Count == 0 && !failed)
                    answer.Text = "I ran out of room before I could answer. Ask again, a little more simply.";
                chat.Busy = false;
                RenderChat(chat);
            }
        }
    }

    /// <summary>
    /// One turn of the model, streamed into <paramref name="answer"/> (its reasoning and the text after
    /// ReplyStart). Returns the tools it asked for, if any.
    /// </summary>
    private async Task<List<ToolCall>> StreamTurn(ChatAnswer chat, ChatMsg answer, int port, string body,
        Func<long> thinkStart, Action<long> setThinkStart, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var resp = await AskHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string before = answer.Text[..answer.ReplyStart], thoughtBefore = answer.Thinking;
        var text = new StringBuilder();
        var thought = new StringBuilder();
        var calls = new SortedDictionary<int, ToolCall>();
        long drawn = 0;
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            string data = line[6..];
            if (data == "[DONE]") break;
            using var doc = JsonDocument.Parse(data);
            if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
            if (!choices[0].TryGetProperty("delta", out var delta)) continue;
            if (delta.TryGetProperty("reasoning_content", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                if (thinkStart() == 0) setThinkStart(Environment.TickCount64);
                thought.Append(reason.GetString());
            }
            if (delta.TryGetProperty("content", out var part) && part.ValueKind == JsonValueKind.String) text.Append(part.GetString());
            // Tool calls come in pieces: the name first, then the arguments a few characters at a time.
            if (delta.TryGetProperty("tool_calls", out var tcs) && tcs.ValueKind == JsonValueKind.Array)
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    int index = tc.TryGetProperty("index", out var ix) && ix.ValueKind == JsonValueKind.Number ? ix.GetInt32() : calls.Count;
                    if (!calls.TryGetValue(index, out var call))
                    {
                        string id = tc.TryGetProperty("id", out var idv) && idv.ValueKind == JsonValueKind.String ? idv.GetString()! : $"call_{answer.Rounds.Count}_{index}";
                        string name = tc.TryGetProperty("function", out var f0) && f0.TryGetProperty("name", out var nv) && nv.ValueKind == JsonValueKind.String ? nv.GetString()! : "";
                        calls[index] = call = new ToolCall { Id = id, Name = name };
                    }
                    if (tc.TryGetProperty("function", out var f) && f.TryGetProperty("arguments", out var av) && av.ValueKind == JsonValueKind.String)
                        call.Arguments += av.GetString();
                }
                if (answer.Hint.Length == 0 && calls.Count > 0)
                {
                    answer.Hint = ToolHint(calls.Values.First().Name);
                    RenderChat(chat);
                }
            }
            SplitThinking(thought, text, out string thinking, out string reply);
            if (thinking.Length > 0 && answer.ThinkMs == 0 && (reply.Length > 0 || calls.Count > 0))
                answer.ThinkMs = Math.Max(1, Environment.TickCount64 - (thinkStart() > 0 ? thinkStart() : Environment.TickCount64));
            answer.Thinking = thoughtBefore + thinking;
            answer.Text = before + reply;
            if (Environment.TickCount64 - drawn > 60)
            {
                drawn = Environment.TickCount64;
                RenderChat(chat);
            }
        }
        return calls.Values.Where(c => c.Name.Length > 0).ToList();
    }

    /// <summary>
    /// The reasoning and the answer so far. The server splits them (reasoning_content); a model that
    /// writes its reasoning inside the answer in think tags is split here.
    /// </summary>
    private static void SplitThinking(StringBuilder thought, StringBuilder text, out string thinking, out string reply)
    {
        string t = text.ToString();
        thinking = thought.ToString();
        int open = t.IndexOf("<think>", StringComparison.Ordinal);
        if (open >= 0)
        {
            int close = t.IndexOf("</think>", open, StringComparison.Ordinal);
            string inner = close < 0 ? t[(open + 7)..] : t[(open + 7)..close];
            thinking += inner;
            t = (t[..open] + (close < 0 ? "" : t[(close + 8)..])).TrimStart();
        }
        reply = t;
    }

    /// <summary>
    /// The request: the system message, the conversation so far (with the tools each answer used and what
    /// they found), and the new message; the tools offered, when <paramref name="tools"/> is given.
    /// Written by hand: no reflection.
    /// </summary>
    private static string ChatBody(string system, ChatAnswer chat, bool think, string? tools)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("messages");
            void Message(string role, string content)
            {
                w.WriteStartObject();
                w.WriteString("role", role);
                w.WriteString("content", content);
                w.WriteEndObject();
            }
            Message("system", system);
            // Older turns stay as they were sent, so the model's cache of the conversation holds; the last
            // message is the one being answered, with the tools used for it so far.
            var turns = chat.Messages.Where(m => m.Role == "user" || m.Text.Length > 0 || m.Rounds.Count > 0).ToList();
            int from = Math.Max(0, turns.Count - 13);   // the last six exchanges and the new message
            foreach (var m in turns.Skip(from))
            {
                if (m.Role == "user")
                {
                    Message("user", m.Sent);
                    continue;
                }
                foreach (var round in m.Rounds)
                {
                    w.WriteStartObject();
                    w.WriteString("role", "assistant");
                    w.WriteString("content", round.Content.Trim());
                    w.WriteStartArray("tool_calls");
                    foreach (var c in round.Calls)
                    {
                        w.WriteStartObject();
                        w.WriteString("id", c.Id);
                        w.WriteString("type", "function");
                        w.WriteStartObject("function");
                        w.WriteString("name", c.Name);
                        w.WriteString("arguments", c.Arguments.Length > 0 ? c.Arguments : "{}");
                        w.WriteEndObject();
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                    foreach (var c in round.Calls)
                    {
                        w.WriteStartObject();
                        w.WriteString("role", "tool");
                        w.WriteString("tool_call_id", c.Id);
                        w.WriteString("content", c.Result);
                        w.WriteEndObject();
                    }
                }
                string final = m.Text[Math.Min(m.ReplyStart, m.Text.Length)..].Trim();
                if (final.Length > 0 && !ReferenceEquals(m, chat.Messages[^1])) Message("assistant", final);
            }
            w.WriteEndArray();
            if (tools != null)
            {
                w.WritePropertyName("tools");
                w.WriteRawValue(tools);
            }
            w.WriteBoolean("stream", true);
            w.WriteNumber("max_tokens", think ? 2000 : 700);
            w.WriteNumber("temperature", 0.3);
            w.WriteNumber("top_p", 0.9);
            w.WriteBoolean("cache_prompt", true);
            w.WriteStartObject("chat_template_kwargs");
            w.WriteBoolean("enable_thinking", think);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    private void RenderChatIfShown(ChatAnswer chat)
    {
        if (_pvVm?.Answer == chat && ChatPreview.Visibility == Visibility.Visible) RenderChat(chat);
    }

    /// <summary>Show the conversation in the card, sized to it (up to a limit, past which it scrolls).</summary>
    private void RenderChat(ChatAnswer chat)
    {
        if (_pvVm?.Answer != chat || ChatPreview.Visibility != Visibility.Visible) return;
        ChatFooter.Text = chat.Footer;
        ChatMessages.Opacity = 1;
        // Follow the model while you are at the bottom; leave the page alone once you scroll up to read.
        bool stick = chat.Messages.Count != _chatViews.Count || ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 48;

        if (chat.Messages.Count == 0)
        {
            // Nothing said yet: what the model has read, in small type.
            if (_chatViews.Count == 0 && ChatMessages.Children.Count == 0)
                ChatMessages.Children.Add(new TextBlock { Text = chat.Hint, Opacity = 0.6, TextWrapping = TextWrapping.Wrap });
            else if (ChatMessages.Children.Count == 1 && ChatMessages.Children[0] is TextBlock hint) hint.Text = chat.Hint;
        }
        else if (_chatViews.Count == 0)
        {
            ChatMessages.Children.Clear();
        }

        for (int i = 0; i < chat.Messages.Count; i++)
        {
            var m = chat.Messages[i];
            if (i >= _chatViews.Count)
            {
                var view = MakeMessageView(m);
                _chatViews.Add((view, m));
                ChatMessages.Children.Add(view);
            }
            UpdateMessageView(_chatViews[i].view, m, i == chat.Messages.Count - 1 && chat.Busy);
        }

        double width = Math.Max(200, (ChatPreview.ActualWidth > 0 ? ChatPreview.ActualWidth : 560) - 40);
        ChatMessages.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        ChatFooter.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        double h = Math.Clamp(ChatMessages.DesiredSize.Height + 12 + 6 + 10 + Math.Max(16, ChatFooter.DesiredSize.Height), 110, 400);
        bool grew = Math.Abs(ChatPreview.Height - h) > 1;
        ChatPreview.Height = h;
        _pvFull = 6 + h + 12;
        if (grew && !_animating) ApplyShape();
        if (stick) ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true);
    }

    private FrameworkElement MakeMessageView(ChatMsg m)
    {
        if (m.Role == "user")
        {
            return new Border
            {
                Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10, 6, 10, 6),
                HorizontalAlignment = HorizontalAlignment.Right,
                MaxWidth = 400,
                Child = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            };
        }
        return new AssistantView(() => { if (_chat is { } c) RenderChat(c); }, MakeBlockView);
    }

    private static void UpdateMessageView(FrameworkElement view, ChatMsg m, bool writing)
    {
        if (view is Border { Child: TextBlock user }) user.Text = m.Text;
        else if (view is AssistantView a) a.Update(m, writing);
    }

    /// <summary>Leave the chat for the home view.</summary>
    private void LeaveChat()
    {
        CancelChat();
        _chat = null;
        _chatViews.Clear();
        ChatMessages.Children.Clear();
        // The card goes at once: left to close with the row's room, it showed as an empty box that
        // shrank under the first result.
        ResetPreview();
        ShowHome();
    }
}
