using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        var user = new ChatMsg { Role = "user", Text = text, Sent = text };
        var answer = new ChatMsg { Role = "assistant" };
        chat.Messages.Add(user);
        chat.Messages.Add(answer);
        RenderChat(chat);
        var cts = _chatCts = new CancellationTokenSource();
        _ = RunChat(chat, user, answer, cts.Token);
    }

    private async Task RunChat(ChatAnswer chat, ChatMsg user, ChatMsg answer, CancellationToken ct)
    {
        try
        {
            var opened = await chat.Opening!;
            if (ct.IsCancellationRequested) return;
            if (!opened.Ok)
            {
                answer.Text = opened.Error ?? "The model did not start";
                return;
            }
            // Documents, when the message is about your files: a "?" in front says so, so do words like
            // "my documents", and so does a chat with no window read. Not for every follow-up: the search
            // takes a moment, and what you have just been told is about the window.
            string message = user.Text;
            if (message.StartsWith('?')) message = message[1..].Trim();
            if (message != user.Text || opened.Source == "none" || AboutFiles.IsMatch(message))
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

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{opened.Port}/v1/chat/completions")
            {
                Content = new StringContent(ChatBody(opened.System ?? "", chat), Encoding.UTF8, "application/json"),
            };
            using var resp = await AskHttp.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = new StringBuilder();
            long drawn = 0;
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
                string data = line[6..];
                if (data == "[DONE]") break;
                using var doc = JsonDocument.Parse(data);
                if (!doc.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                if (!choices[0].TryGetProperty("delta", out var delta) || !delta.TryGetProperty("content", out var part)) continue;
                text.Append(part.GetString());
                answer.Text = text.ToString();
                if (Environment.TickCount64 - drawn > 60)
                {
                    drawn = Environment.TickCount64;
                    RenderChat(chat);
                }
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
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                chat.Busy = false;
                RenderChat(chat);
            }
        }
    }

    /// <summary>The request: the system message, the conversation so far, and the new message. Written by hand: no reflection.</summary>
    private static string ChatBody(string system, ChatAnswer chat)
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
            // The last pair is the message being answered (its answer is empty); older turns stay as they
            // were sent, so the model's cache of the conversation holds.
            var turns = chat.Messages.Where(m => m.Role == "user" || m.Text.Length > 0).ToList();
            int from = Math.Max(0, turns.Count - 13);   // the last six exchanges and the new message
            foreach (var m in turns.Skip(from)) Message(m.Role, m.Role == "user" ? m.Sent : m.Text);
            w.WriteEndArray();
            w.WriteBoolean("stream", true);
            w.WriteNumber("max_tokens", 700);
            w.WriteNumber("temperature", 0.3);
            w.WriteNumber("top_p", 0.9);
            w.WriteBoolean("cache_prompt", true);
            w.WriteStartObject("chat_template_kwargs");
            w.WriteBoolean("enable_thinking", false);
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
        ChatScroll.ChangeView(null, ChatScroll.ScrollableHeight, null, true);
    }

    private static FrameworkElement MakeMessageView(ChatMsg m)
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
                Child = new TextBlock { TextWrapping = TextWrapping.Wrap },
            };
        }
        var stack = new StackPanel { Spacing = 3 };
        stack.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 21 });
        stack.Children.Add(new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
            Visibility = Visibility.Collapsed,
        });
        return stack;
    }

    private static void UpdateMessageView(FrameworkElement view, ChatMsg m, bool writing)
    {
        if (view is Border { Child: TextBlock user })
        {
            user.Text = m.Text;
            return;
        }
        if (view is not StackPanel s) return;
        var body = (TextBlock)s.Children[0];
        var note = (TextBlock)s.Children[1];
        string text = ChatText(m.Text);
        body.Text = text.Length > 0 ? text : writing ? (m.Hint.Length > 0 ? m.Hint : "…") : "";
        body.Opacity = text.Length > 0 ? 1 : 0.5;
        note.Text = m.Note;
        note.Visibility = m.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The model's markdown (bold, headings, bullets) as plain text.</summary>
    private static string ChatText(string s)
    {
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^(\s*)[*-]\s+", "$1• ", RegexOptions.Multiline);
        return s.Trim();
    }

    /// <summary>Leave the chat for the home view.</summary>
    private void LeaveChat()
    {
        CancelChat();
        _chat = null;
        _chatViews.Clear();
        ChatMessages.Children.Clear();
        ShowHome();
    }
}
