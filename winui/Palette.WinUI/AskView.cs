using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;

namespace Palette;

/// <summary>An answer about the window in front, being written by the model (see <see cref="MainWindow.StartAsk"/>).</summary>
public sealed record AskAnswer(string Query) : WebAnswers.Answer(Query)
{
    /// <summary>What the model has written so far.</summary>
    public string Text { get; set; } = "";

    /// <summary>What is going on before the first word arrives.</summary>
    public string Status { get; set; } = "Reading the window…";

    /// <summary>The small print: which model, and what the answer is from.</summary>
    public string Footer { get; set; } = "On this PC";

    public bool Done { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Asking about the window in front ("summarise this", "what does this error mean"; chat.rs). The
/// engine offers a row when the query asks; Enter on it starts the model, and the answer streams into a
/// card under the row, in place. Everything runs on this PC: the engine reads the window and starts
/// llama.cpp's server on 127.0.0.1, this file streams from it. Typing, Esc or closing the palette
/// stops it. Enter on an answered row copies the answer.
/// </summary>
public sealed partial class MainWindow
{
    private static readonly HttpClient AskHttp = new() { Timeout = Timeout.InfiniteTimeSpan };

    private CancellationTokenSource? _askCts;

    /// <summary>Enter on a row that offers an answer: start it, or (when it has one) copy it.</summary>
    private void AskOrCopy(HitVm vm)
    {
        if (vm.Answer is AskAnswer done)
        {
            if (done.Text.Length > 0)
            {
                var dp = new DataPackage();
                dp.SetText(PlainText(done.Text));
                Clipboard.SetContent(dp);
                HidePalette(restoreFocus: true);
            }
            return;
        }
        StartAsk(vm);
    }

    private void StartAsk(HitVm vm)
    {
        CancelAsk();
        var ask = new AskAnswer(vm.Title);
        vm.Answer = ask;
        var cts = _askCts = new CancellationTokenSource();
        UpdatePreview();
        UpdateOpenButton();
        _ = RunAsk(ask, cts.Token);
    }

    /// <summary>Stop writing an answer (typing went on, or the palette closed).</summary>
    private void CancelAsk()
    {
        _askCts?.Cancel();
        _askCts = null;
    }

    private async Task RunAsk(AskAnswer ask, CancellationToken ct)
    {
        try
        {
            var ready = await Task.Run(() => Native.AskPrepare(ask.Query));
            if (ct.IsCancellationRequested) return;
            if (!ready.Ok)
            {
                Finish(ask, error: ready.Error ?? "I couldn't answer that");
                return;
            }
            ask.Footer = $"{ready.Model} · on this PC · from " + ready.Source switch
            {
                "selection" => "your selection",
                "documents" => "your documents: " + string.Join(" · ", (ready.Sources ?? new()).Select((n, i) => $"[{i + 1}] {n}")),
                _ => "the " + ready.App + " window",
            };
            ask.Status = "Thinking…";
            RenderAskIfShown(ask);

            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{ready.Port}{ready.Path ?? "/v1/chat/completions"}")
            {
                Content = new StringContent(ready.Body ?? "{}", Encoding.UTF8, "application/json"),
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
                var root = doc.RootElement;
                // llama.cpp's own endpoint sends {"content", "stop"}; the chat API, choices[0].delta.content.
                if (root.TryGetProperty("content", out var piece))
                {
                    text.Append(piece.GetString());
                }
                else if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0
                         && choices[0].TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var part))
                {
                    text.Append(part.GetString());
                }
                else continue;
                ask.Text = text.ToString();
                if (Environment.TickCount64 - drawn > 60)
                {
                    drawn = Environment.TickCount64;
                    RenderAskIfShown(ask);
                }
            }
            Finish(ask);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            App.Log("ask failed: " + ex.Message);
            if (!ct.IsCancellationRequested) Finish(ask, error: ask.Text.Length > 0 ? null : "The model stopped answering");
        }
    }

    private void Finish(AskAnswer ask, string? error = null)
    {
        ask.Done = true;
        ask.Error = error;
        RenderAskIfShown(ask);
        UpdateOpenButton();
    }

    private void RenderAskIfShown(AskAnswer ask)
    {
        if (_pvVm?.Answer == ask && AskPreview.Visibility == Visibility.Visible) RenderAsk(ask);
    }

    /// <summary>Fill the card, and size it to the text (up to a limit, past which it scrolls).</summary>
    private void RenderAsk(AskAnswer ask)
    {
        string text = PlainText(ask.Text);
        bool placeholder = ask.Error != null || text.Length == 0;
        AskText.Text = ask.Error ?? (text.Length > 0 ? text : ask.Status);
        AskText.Opacity = placeholder ? 0.6 : 1;
        AskFooter.Text = ask.Footer;

        double width = Math.Max(200, (AskPreview.ActualWidth > 0 ? AskPreview.ActualWidth : 560) - 40);
        AskText.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        AskFooter.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        double h = Math.Clamp(AskText.DesiredSize.Height + 14 + 8 + 10 + Math.Max(16, AskFooter.DesiredSize.Height), 84, 340);
        bool grew = Math.Abs(AskPreview.Height - h) > 1;
        AskPreview.Height = h;
        _pvFull = 6 + h + 12;
        if (grew && !_animating) ApplyShape();
        if (!ask.Done) AskScroll.ChangeView(null, AskScroll.ScrollableHeight, null, true);
    }

    /// <summary>The model's text for a plain card: its markdown (bold, headings, bullets) as plain text.</summary>
    private static string PlainText(string s)
    {
        s = s.Replace("**", "").Replace("__", "").Replace("`", "");
        s = Regex.Replace(s, @"^\s{0,3}#{1,6}\s*", "", RegexOptions.Multiline);
        s = Regex.Replace(s, @"^(\s*)[*-]\s+", "$1• ", RegexOptions.Multiline);
        return s.Trim();
    }
}
