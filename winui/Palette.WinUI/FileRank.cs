using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palette;

/// <summary>
/// Which of the files find_files gathered are the one the user means (ChatTools.cs FindFilesTool). The
/// engine casts a wide net (tools.rs) and says what it knows of each file; the chat model, asked on its
/// own, judges them by what they are, from all it knows of apps, their makers, formats and sites: the
/// app the user describes as "something like xflr5" is flow5, and comes before a "wing.png" that only
/// shares a word with the description.
/// </summary>
public sealed partial class MainWindow
{
    private const string RankInstructions =
        "You help find a file on the user's PC. You are given what the user said, the search the assistant made, and numbered candidate " +
        "files, each with its folder, when it was changed or opened, and what is known of it. Decide which files are what the user is " +
        "looking for. Judge each by what it is, from everything you know (apps and what they do, their makers, their other names, older " +
        "and newer versions and successors, file formats, websites, projects), not by whether its name repeats the user's words: a file " +
        "that never mentions those words but is exactly the thing described ranks above one whose name matches but is something else, " +
        "and a file inside a project or an app's folder is not the project or the app itself. Keep to what the user said about when, " +
        "which kind (an application, a photo, a document) and where, and to their corrections in later messages.\n" +
        "Use every clue: the site it came from, the program's own description, the libraries it ships (a library named after a known " +
        "solver or engine tells what the app does), the folder it is in.\n" +
        "Answer with one line for each file that could be it, best first, at most 6: its number, a colon, and why it fits in at most " +
        "eight words (3: Darktable, a photo editor like Lightroom). Several may fit: list them all, the user will choose; leave out " +
        "only files that are clearly something else. When none could be it, answer NONE.";

    private static readonly HttpClient RankHttp = new() { Timeout = TimeSpan.FromSeconds(40) };

    /// <summary>
    /// The candidates that fit, best first, each with why (indexes into <paramref name="files"/>); empty when
    /// none fits; null when the model could not be asked (then the engine's order stands).
    /// </summary>
    private async Task<List<(int index, string why)>?> RankFiles(List<Native.FileFound> files, string query, string period, string kind,
        string folder, CancellationToken ct)
    {
        if (_acting is not { } a || a.chat.Opening is not { IsCompletedSuccessfully: true } opening || !opening.Result.Ok) return null;
        int port = opening.Result.Port;

        var said = a.chat.Messages.Where(m => m.Role == "user" && m.Text.Trim().Length > 0).TakeLast(3).Select(m => m.Text.Trim()).ToList();
        var user = new StringBuilder("What the user said, latest last:\n");
        foreach (var s in said) user.Append("- ").Append(s.Length > 400 ? s[..400] + "…" : s).Append('\n');
        user.Append("\nThe assistant searched for: ").Append(query.Length > 0 ? query : "(anything)");
        if (period.Length > 0 && period != "any") user.Append(", ").Append(period.Replace('_', ' '));
        if (kind.Length > 0 && kind != "any") user.Append(", kind ").Append(kind).Append(" (the assistant's guess: what the user said comes first)");
        if (folder.Length > 0 && folder != "any") user.Append(", in ").Append(folder);
        // Short lines: the model reads every word before it answers, and the candidates are most of what it reads.
        user.Append("\n\nCandidates:\n");
        for (int i = 0; i < files.Count; i++)
        {
            var f = files[i];
            user.Append($"{i + 1}. {f.Name} — {ShortFolder(f.Folder)}, ");
            user.Append(f.Opened > f.Modified ? $"opened {Day(f.Opened)}" : $"changed {Day(f.Modified)}");
            if (f.About.Length > 0) user.Append("; ").Append(f.About);
            user.Append('\n');
        }

        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            w.WriteStartArray("messages");
            w.WriteStartObject(); w.WriteString("role", "system"); w.WriteString("content", RankInstructions); w.WriteEndObject();
            w.WriteStartObject(); w.WriteString("role", "user"); w.WriteString("content", user.ToString()); w.WriteEndObject();
            w.WriteEndArray();
            w.WriteNumber("max_tokens", 120);
            w.WriteNumber("temperature", 0);
            // The instructions stay cached between searches; the chat's own cache comes back after (the server keeps it).
            w.WriteBoolean("cache_prompt", true);
            w.WriteStartObject("chat_template_kwargs");
            w.WriteBoolean("enable_thinking", false);
            w.WriteEndObject();
            w.WriteEndObject();
        }
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/v1/chat/completions")
            {
                Content = new StringContent(Encoding.UTF8.GetString(ms.ToArray()), Encoding.UTF8, "application/json"),
            };
            using var resp = await RankHttp.SendAsync(req, ct);
            resp.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            string reply = doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
            App.Log($"file rank for '{query}': {reply.Replace('\n', '|')}");
            return ParseRank(reply, files.Count);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"file rank failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>"3: flow5, the successor of xflr5" lines as (index, why), in order, each file once; empty for NONE.</summary>
    internal static List<(int index, string why)> ParseRank(string reply, int count)
    {
        var picked = new List<(int, string)>();
        foreach (Match m in Regex.Matches(reply, @"^\s*[-*]?\s*#?(\d{1,2})\s*[:.)\-–]\s*(.*)$", RegexOptions.Multiline))
        {
            int i = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) - 1;
            if (i < 0 || i >= count || picked.Any(p => p.Item1 == i)) continue;
            picked.Add((i, m.Groups[2].Value.Trim()));
            if (picked.Count == 6) break;
        }
        return picked;
    }

    /// <summary>A unix time as a day: "Tue 20 Oct", with the year when it is not this one.</summary>
    private static string Day(long unix)
    {
        if (unix <= 0) return "at an unknown time";
        var d = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        return d.ToString(d.Year == DateTime.Now.Year ? "ddd d MMM" : "ddd d MMM yyyy", CultureInfo.InvariantCulture);
    }
}
