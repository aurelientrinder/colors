using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace Palette;

/// <summary>A page the web search found: what it is called, where, and the site's name ("meteoblue.com").</summary>
public sealed record WebSource(string Url, string Title, string Site);

/// <summary>
/// Searching the web for the chat (ChatWeb.cs): DuckDuckGo's plain results page (no account, no key),
/// then the best few pages read for the passages that answer the query. Only the query leaves the PC (to
/// DuckDuckGo, and as a visit to the pages read); nothing of the user's files, memory or conversation.
/// </summary>
internal static class WebSearch
{
    public sealed record Result(string Url, string Title, string Site, string Snippet);

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var h = new HttpClient(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All, AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(8),
        };
        h.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        h.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-GB,en;q=0.9,fr;q=0.8");
        return h;
    }

    private static readonly Regex ResultLink = new(@"<a[^>]*class=""result__a""[^>]*href=""([^""]+)""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex ResultSnippet = new(@"<a[^>]*class=""result__snippet""[^>]*>(.*?)</a>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>When DuckDuckGo last turned a search away (it does after a few in a row): Brave for a while after.</summary>
    private static DateTime _ddgRefused = DateTime.MinValue;

    /// <summary>
    /// The results for <paramref name="query"/>, best first (ads left out): DuckDuckGo's, or Brave's when
    /// DuckDuckGo turns the search away; empty when neither works.
    /// </summary>
    public static async Task<List<Result>> Search(string query, CancellationToken ct)
    {
        if (DateTime.UtcNow - _ddgRefused > TimeSpan.FromMinutes(15))
        {
            var ddg = await SearchDuckDuckGo(query, ct);
            if (ddg.Count > 0) return ddg;
            _ddgRefused = DateTime.UtcNow;
            App.Log("web search: DuckDuckGo gave nothing, trying Brave");
        }
        var brave = await SearchBrave(query, ct);
        if (brave.Count > 0) return brave;
        // Brave too turns away searches that come quickly one after another: once more, a moment later.
        await Task.Delay(1500, ct);
        return await SearchBrave(query, ct);
    }

    private static async Task<List<Result>> SearchDuckDuckGo(string query, CancellationToken ct)
    {
        string html;
        try
        {
            using var form = new FormUrlEncodedContent(new Dictionary<string, string> { ["q"] = query, ["kl"] = "" });
            using var resp = await Http.PostAsync("https://html.duckduckgo.com/html/", form, ct);
            // 202 with a page asking to prove one is human: turned away.
            if (resp.StatusCode != HttpStatusCode.OK) return new();
            html = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            App.Log("web search failed: " + ex.Message);
            return new();
        }
        var links = ResultLink.Matches(html);
        var snippets = ResultSnippet.Matches(html);
        var list = new List<Result>();
        for (int i = 0; i < links.Count; i++)
        {
            string url = RealUrl(WebUtility.HtmlDecode(links[i].Groups[1].Value));
            if (url.Length == 0 || url.Contains("duckduckgo.com/y.js")) continue;   // an ad
            string title = Plain(links[i].Groups[2].Value);
            string snippet = i < snippets.Count ? Plain(snippets[i].Groups[1].Value) : "";
            if (list.Any(r => r.Url == url)) continue;
            list.Add(new Result(url, title, Site(url), snippet));
        }
        return list;
    }

    private static readonly Regex BraveLink = new(@"<a[^>]*href=""(https?://[^""]+)""", RegexOptions.Compiled);
    private static readonly Regex BraveTitle = new(@"class=""title[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BraveSnippet = new(@"class=""(?:snippet-description|content)[^""]*""[^>]*>(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>Brave Search's results page, each web result marked data-type="web".</summary>
    private static async Task<List<Result>> SearchBrave(string query, CancellationToken ct)
    {
        string html;
        try
        {
            html = await Http.GetStringAsync("https://search.brave.com/search?q=" + Uri.EscapeDataString(query), ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            App.Log("brave search failed: " + ex.Message);
            return new();
        }
        var list = new List<Result>();
        foreach (var chunk in html.Split("data-type=\"web\"").Skip(1))
        {
            var a = BraveLink.Match(chunk);
            var t = BraveTitle.Match(chunk);
            if (!a.Success || !t.Success) continue;
            string url = WebUtility.HtmlDecode(a.Groups[1].Value);
            if (url.Contains("brave.com/") || list.Any(r => r.Url == url)) continue;
            var d = BraveSnippet.Match(chunk);
            list.Add(new Result(url, Plain(t.Groups[1].Value), Site(url), d.Success ? Plain(d.Groups[1].Value) : ""));
        }
        return list;
    }

    /// <summary>DuckDuckGo's redirect ("//duckduckgo.com/l/?uddg=https%3A…") as the page it leads to.</summary>
    private static string RealUrl(string href)
    {
        var m = Regex.Match(href, @"[?&]uddg=([^&]+)");
        string url = m.Success ? Uri.UnescapeDataString(m.Groups[1].Value) : href.StartsWith("//") ? "https:" + href : href;
        return url.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? url : "";
    }

    public static string Site(string url)
    {
        try
        {
            string host = new Uri(url).Host;
            return host.StartsWith("www.") ? host[4..] : host;
        }
        catch (UriFormatException)
        {
            return url;
        }
    }

    /// <summary>A bit of HTML as plain text: tags out, entities decoded, spaces collapsed.</summary>
    private static string Plain(string html) =>
        Regex.Replace(WebUtility.HtmlDecode(Regex.Replace(html, "<[^>]+>", "")), @"\s+", " ").Trim();

    // ------------------------------------------------------------------ reading a page

    private static readonly Regex Noise = new(@"<(script|style|noscript|svg|nav|header|footer|form|aside|iframe|template)\b.*?</\1\s*>",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Breaks = new(@"<(/?(p|div|li|ul|ol|h[1-6]|tr|table|section|article|blockquote|dd|dt)\b[^>]*|br\s*/?)>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> Stop = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "and", "for", "with", "what", "when", "where", "which", "who", "how", "are", "is", "was", "this", "that", "from", "about",
        "les", "des", "une", "pour", "dans", "est", "que", "qui", "avec", "sur",
    };

    private static List<string> Terms(string query) =>
        Regex.Split(query.ToLowerInvariant(), @"[^\p{L}\p{N}]+").Where(w => w.Length >= 3 && !Stop.Contains(w)).Distinct().ToList();

    /// <summary>
    /// The passages of the page at <paramref name="url"/> that best match <paramref name="query"/>, in the
    /// page's order, at most <paramref name="max"/> characters; "" when it cannot be read (not HTML, too slow).
    /// </summary>
    public static async Task<string> Read(string url, string query, int max, CancellationToken ct)
    {
        string html;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(6));
            using var resp = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode || resp.Content.Headers.ContentType?.MediaType is not ("text/html" or "application/xhtml+xml")) return "";
            if (resp.Content.Headers.ContentLength > 3_000_000) return "";
            html = await resp.Content.ReadAsStringAsync(cts.Token);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            App.Log($"web page {url} not read: {ex.Message}");
            return "";
        }
        if (html.Length > 3_000_000) html = html[..3_000_000];
        var body = Regex.Match(html, @"<body\b.*", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        string text = Breaks.Replace(Noise.Replace(body.Success ? body.Value : html, " "), "\n");
        text = WebUtility.HtmlDecode(Regex.Replace(text, "<[^>]+>", " "));
        // Sentences, not code: a script's data that slipped through is mostly braces, quotes and symbols.
        var paragraphs = text.Split('\n').Select(p => Regex.Replace(p, @"\s+", " ").Trim())
            .Where(p => p.Length >= 60 && p.Count(c => "{}[]\"\\=<>;|_".Contains(c)) < p.Length * 0.04).ToList();
        if (paragraphs.Count == 0) return "";
        var terms = Terms(query);
        var scored = paragraphs.Select((p, i) =>
        {
            string low = p.ToLowerInvariant();
            double hits = terms.Count(t => low.Contains(t));
            bool figures = Regex.IsMatch(p, @"\d");
            return (i, p, score: hits + (figures ? 0.3 : 0) - (p.Length > 900 ? 0.5 : 0));
        }).Where(t => t.score > 0).OrderByDescending(t => t.score).ToList();
        var chosen = new List<(int i, string p)>();
        int used = 0;
        foreach (var (i, p, _) in scored)
        {
            string piece = p.Length > 600 ? p[..600] + "…" : p;
            if (used + piece.Length > max) continue;
            chosen.Add((i, piece));
            used += piece.Length;
            if (used > max * 0.8) break;
        }
        return string.Join(" … ", chosen.OrderBy(c => c.i).Select(c => c.p));
    }

    /// <summary>
    /// The text the model is given: the results numbered as it cites them, each with its site, its snippet,
    /// and the passages read from it (the first three pages).
    /// </summary>
    public static async Task<(string text, List<WebSource> sources)> Gather(string query, CancellationToken ct)
    {
        var results = (await Search(query, ct)).Take(5).ToList();
        if (results.Count == 0) return ("", new());
        var pages = await Task.WhenAll(results.Select((r, i) => i < 3 ? Read(r.Url, query, 1100, ct) : Task.FromResult("")));
        var sb = new StringBuilder($"Web results for “{query}”, best first. Cite what you use as [1], [2] (each becomes a link to its page); answer from these only, and say so if they do not answer it.\n");
        for (int i = 0; i < results.Count; i++)
        {
            var r = results[i];
            sb.Append($"\n[{i + 1}] {r.Title} ({r.Site})\n");
            if (r.Snippet.Length > 0) sb.Append(r.Snippet).Append('\n');
            if (pages[i].Length > 0) sb.Append("From the page: ").Append(pages[i]).Append('\n');
        }
        return (sb.ToString(), results.Select(r => new WebSource(r.Url, r.Title, r.Site)).ToList());
    }
}
