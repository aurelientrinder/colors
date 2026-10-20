using System.Text.RegularExpressions;

namespace Palette;

/// <summary>The pages a web search found, cited as [1] [2] in the answer: each a link to its page.</summary>
public sealed record WebSourcesBlock(List<WebSource> Sources);

/// <summary>
/// The chat's web search (WebSearch.cs). Nothing goes out unasked: each search shows its query, word for
/// word, on a card under the answer (Allow once, Allow for this chat, Deny), and the model waits. Allowed
/// for the chat, later searches in that conversation go ahead, each still a step on the answer's rail.
/// Declined, nothing is sent and the model answers from what it knows.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>"search the web for…", "look it up online", "google…": a request the model must act on, not describe.</summary>
    private static readonly Regex WebAsk = new(
        @"\b(search (the web|online|the internet|for)|look (it |this |that )?up|on the (web|internet)|online|google|en ligne|sur (internet|le web)|cherche)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static bool AsksWeb(string message) =>
        WebAsk.IsMatch(message) && !Regex.IsMatch(message, @"^\s*(how do i|how to|comment)\b", RegexOptions.IgnoreCase);

    private async Task<(string, object?, string?)> WebSearchTool(string query, CancellationToken ct)
    {
        query = Regex.Replace(query, @"\s+", " ").Trim().Trim('"', '“', '”');
        if (query.Length == 0) return ("Give the search query (query).", null, null);
        if (query.Length > 200) query = query[..200];
        var chat = _acting?.chat;
        ActionBlock? card = null;
        if (chat is not { WebAllowed: true })
        {
            card = new ActionBlock
            {
                Kind = "web", Question = "Search the web for this?", Detail = query, OfferAlways = true,
                Note = "Only these words leave your PC: DuckDuckGo gets them, and the best pages are read. Nothing from your files or memory is sent.",
            };
            if (!await AskFirst(card, ct))
                return ("The user declined the web search: nothing was sent. Answer from what you know, saying it may be out of date, or say you can't tell.", card, null);
            if (card.Always && chat != null) chat.WebAllowed = true;
        }
        var (text, sources) = await WebSearch.Gather(query, ct);
        if (sources.Count == 0)
        {
            card?.Set("failed", $"The web search for “{query}” did not work");
            return ("The web search did not work (no connection, or the search engine refused). Say so; answer from what you know only if you are sure.", card, null);
        }
        card?.Set("done", $"Searched the web for “{query}”" + (card.Always ? " · allowed for the rest of this chat" : ""));
        return (text, new WebSourcesBlock(sources), null);
    }
}
