using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Palette;

/// <summary>
/// Inline answers from the web, shown as a card under a "search the web" row, as in the new
/// Windows Search: the weather somewhere, a stock's price and chart, or a fact about something
/// ("how tall is the Eiffel Tower"). Only queries shaped like those questions are sent anywhere,
/// and only once typing pauses:
/// <list type="bullet">
/// <item>weather: Open-Meteo (place names and forecasts; no key);</item>
/// <item>stocks: Yahoo Finance's public quote and chart endpoints;</item>
/// <item>facts: Wikidata (the value) and Wikipedia (summaries and pictures);</item>
/// <item>currency ("50 usd to eur"): Frankfurter's daily European Central Bank rates.</item>
/// </list>
/// Every fetch fails quietly: no card is shown, and the results stay as they were.
/// </summary>
public static class WebAnswers
{
    public enum Kind { Weather, Stock, Fact, Currency }

    public abstract record Answer(string Query);

    /// <summary>Temperatures are kept in Celsius and converted for display.</summary>
    public sealed record Weather(string Query, string Place, double Lat, double Lon, DateTime LocalTime, double TempC,
        double HighC, double LowC, string Condition, string Icon) : Answer(Query);

    /// <summary>The Weather app (MSN Weather): when it is installed, weather is its answer and opens in it.</summary>
    public const string WeatherAppKey = @"shell:AppsFolder\Microsoft.BingWeather_8wekyb3d8bbwe!App";

    private static bool? _hasWeatherApp;

    public static bool HasWeatherApp => _hasWeatherApp ??= HasProtocol("msnweather");

    private static bool HasProtocol(string scheme)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(scheme);
            return k?.GetValue("URL Protocol") != null;
        }
        catch { return false; }
    }

    /// <summary>Is this engine row the Weather app?</summary>
    public static bool IsWeatherApp(string kind, string title, string? iconKey) =>
        kind == "App" && (iconKey?.Contains("Microsoft.BingWeather_", StringComparison.OrdinalIgnoreCase) == true
            || iconKey == null && title is "Weather" or "MSN Weather");

    /// <summary>The Weather app opened on the forecast for <paramref name="w"/>'s place.</summary>
    public static string WeatherAppUrl(Weather w) =>
        "msnweather://forecast?la=" + w.Lat.ToString(CultureInfo.InvariantCulture) +
        "&lo=" + w.Lon.ToString(CultureInfo.InvariantCulture);

    public sealed record Stock(string Query, string Symbol, string Name, string Currency, double Price, double Change,
        double ChangePct, double PreviousClose, IReadOnlyList<double> Series, string Note, string Range) : Answer(Query);

    /// <summary>
    /// A fact (<see cref="Label"/> and <see cref="Value"/>, with <see cref="Secondary"/> beside it
    /// in smaller type), or a summary (<see cref="Text"/>) when the question was "what is ...".
    /// </summary>
    public sealed record Fact(string Query, string Subject, string Label, string Value, string Secondary, string Text,
        string? ImageUrl) : Answer(Query);

    private static readonly HttpClient Http = MakeClient();

    private static HttpClient MakeClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
        // Wikimedia asks clients to name themselves; Yahoo turns away requests that look like no browser.
        c.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) PaletteLauncher/1.0");
        return c;
    }

    private static readonly Dictionary<string, (DateTime at, Answer? answer)> Cache = new();
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(10);

    public static bool Imperial => !RegionInfo.CurrentRegion.IsMetric;

    // ------------------------------------------------------------------ what was asked

    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    private static readonly Regex[] WeatherAsks =
    {
        new(@"^(?:what(?:'s| is) the )?(?:weather|forecast|temperature)(?: like)?(?: (?:in|at|for|near) (?<p>.+?))?(?: (?:today|now|right now))?$", Opt),
        new(@"^(?<p>.+?) (?:weather|forecast|temperature)(?: (?:today|now))?$", Opt),
    };

    private static readonly Regex[] StockAsks =
    {
        new(@"^(?<q>.+?) (?:stock|stocks|share|shares)(?: price| quote| value)?$", Opt),
        new(@"^(?:stock|share|stock price|share price|price of|stock price of|share price of) (?<q>.+)$", Opt),
        new(@"^\$(?<q>[a-z][a-z.\-]{0,9})$", Opt),
    };

    private enum Value { Quantity, Number, Item, Date, Age }

    private sealed record FactAsk(Regex Pattern, string[] Props, string Label, Value Value);

    private static FactAsk F(string pattern, string label, Value v, params string[] props) =>
        new(new Regex(pattern, Opt), props, label, v);

    private static readonly FactAsk[] FactAsks =
    {
        F(@"^(?:how (?:tall|high) is|what is the height of|height of) (?:the )?(?<s>.+)$", "Height", Value.Quantity, "P2048", "P2044"),
        F(@"^(?:how long is|what is the length of|length of) (?:the )?(?<s>.+)$", "Length", Value.Quantity, "P2043"),
        F(@"^(?:how deep is|what is the depth of|depth of) (?:the )?(?<s>.+)$", "Depth", Value.Quantity, "P4511"),
        F(@"^(?:how (?:big|large) is|what is the (?:area|size) of|(?:area|size) of) (?:the )?(?<s>.+)$", "Area", Value.Quantity, "P2046"),
        F(@"^(?:what is the )?population of (?:the )?(?<s>.+)$", "Population", Value.Number, "P1082"),
        F(@"^how many people live in (?:the )?(?<s>.+)$", "Population", Value.Number, "P1082"),
        F(@"^(?:what is the )?capital (?:city )?of (?:the )?(?<s>.+)$", "Capital", Value.Item, "P36"),
        F(@"^(?:what is the )?currency (?:of|in|used in) (?:the )?(?<s>.+)$", "Currency", Value.Item, "P38"),
        F(@"^how old is (?:the )?(?<s>.+)$", "Age", Value.Age, "P569", "P571", "P1619"),
        F(@"^when was (?:the )?(?<s>.+?) born$", "Born", Value.Date, "P569"),
        F(@"^when did (?:the )?(?<s>.+?) die$", "Died", Value.Date, "P570"),
        F(@"^when was (?:the )?(?<s>.+?) (?:founded|established|created|formed)$", "Founded", Value.Date, "P571"),
        F(@"^when was (?:the )?(?<s>.+?) (?:built|opened|completed|finished)$", "Opened", Value.Date, "P1619", "P571"),
        F(@"^when (?:was|did) (?:the )?(?<s>.+?) (?:released|come out|published)$", "Released", Value.Date, "P577"),
        F(@"^who (?:founded|started|created) (?:the )?(?<s>.+)$", "Founded by", Value.Item, "P112", "P170"),
        F(@"^who (?:is|was) the (?:ceo|chief executive|chief executive officer) of (?:the )?(?<s>.+)$", "CEO", Value.Item, "P169"),
        F(@"^who (?:wrote|is the author of|was the author of) (?:the )?(?<s>.+)$", "Author", Value.Item, "P50"),
        F(@"^who (?:directed|is the director of|was the director of) (?:the )?(?<s>.+)$", "Director", Value.Item, "P57"),
        F(@"^who (?:painted|designed|built) (?:the )?(?<s>.+)$", "Creator", Value.Item, "P170", "P84", "P193"),
        F(@"^who is the (?:president|head of state|king|queen|monarch) of (?:the )?(?<s>.+)$", "Head of state", Value.Item, "P35"),
        F(@"^who is the (?:prime minister|head of government|mayor|chancellor) of (?:the )?(?<s>.+)$", "Head of government", Value.Item, "P6"),
    };

    private const string Codes =
        "aud|bgn|brl|cad|chf|cny|czk|dkk|eur|gbp|hkd|huf|idr|ils|inr|isk|jpy|krw|mxn|myr|nok|nzd|php|pln|ron|sek|sgd|thb|try|usd|zar";

    /// <summary>"50 usd to eur", "usd to eur" (one of it), "100 gbp in jpy".</summary>
    private static readonly Regex CurrencyAsk = new(
        @"^(?<n>\d+(?:[.,]\d+)?)?\s*(?<from>" + Codes + @")\s+(?:to|in|into)\s+(?<to>" + Codes + ")$", Opt);

    private static readonly Regex SummaryAsk = new(@"^(?:who|what) (?:is|was|are|were) (?:a |an |the )?(?<s>.+)$", Opt);

    private static string Clean(string q) => q.Trim().TrimEnd('?', '!', '.', ' ');

    /// <summary>Is this query a question one of the answers handles? (Cheap: no network.)</summary>
    public static Kind? Detect(string query)
    {
        string q = Clean(query);
        if (q.Length < 4) return null;
        if (WeatherAsks.Any(r => r.IsMatch(q))) return Kind.Weather;
        if (StockAsks.Any(r => r.IsMatch(q))) return Kind.Stock;
        if (CurrencyAsk.IsMatch(q)) return Kind.Currency;
        if (FactAsks.Any(f => f.Pattern.IsMatch(q)) || SummaryAsk.IsMatch(q)) return Kind.Fact;
        return null;
    }

    public static bool TryCached(string query, out Answer? answer)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(Key(query), out var c) && DateTime.UtcNow - c.at < CacheFor)
            {
                answer = c.answer;
                return true;
            }
        }
        answer = null;
        return false;
    }

    private static string Key(string q) => Clean(q).ToLowerInvariant();

    /// <summary>The answer for <paramref name="query"/>, or null when there is none (or the web is out of reach).</summary>
    public static async Task<Answer?> Fetch(string query, CancellationToken ct)
    {
        if (TryCached(query, out var cached)) return cached;
        string q = Clean(query);
        Answer? a = null;
        try
        {
            a = Detect(q) switch
            {
                Kind.Weather => await FetchWeather(query, q, ct),
                Kind.Stock => await FetchStock(query, q, "1D", ct),
                Kind.Fact => await FetchFact(query, q, ct),
                Kind.Currency => await FetchCurrency(query, q, ct),
                _ => null,
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"web answer for '{q}' failed: {ex.Message}");
        }
        lock (Cache) Cache[Key(query)] = (DateTime.UtcNow, a);
        return a;
    }

    private static async Task<JsonElement> GetJson(string url, CancellationToken ct)
    {
        using var resp = await Http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        await using var s = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct);
        return doc.RootElement.Clone();
    }

    private static string U(string s) => Uri.EscapeDataString(s);

    // ------------------------------------------------------------------ currency

    private static async Task<Answer?> FetchCurrency(string query, string q, CancellationToken ct)
    {
        var m = CurrencyAsk.Match(q);
        string from = m.Groups["from"].Value.ToUpperInvariant(), to = m.Groups["to"].Value.ToUpperInvariant();
        if (from == to) return null;
        double amount = m.Groups["n"].Success
            ? double.Parse(m.Groups["n"].Value.Replace(',', '.'), CultureInfo.InvariantCulture)
            : 1;
        var r = await GetJson($"https://api.frankfurter.dev/v1/latest?base={from}&symbols={to}", ct);
        double rate = r.GetProperty("rates").GetProperty(to).GetDouble();
        string date = DateTime.TryParse(r.GetProperty("date").GetString(), CultureInfo.InvariantCulture,
            DateTimeStyles.None, out var d) ? d.ToString("d MMM", CultureInfo.InvariantCulture) : "";
        string Amount(double v) => v.ToString(v >= 1000 ? "#,##0.##" : v >= 1 ? "0.##" : "0.####", CultureInfo.InvariantCulture);
        return new Fact(query, $"{Amount(amount)} {from}", "Converted", $"{Amount(amount * rate)} {to}",
            $"1 {from} = {Amount(rate)} {to} · European Central Bank, {date}", "", null);
    }

    // ------------------------------------------------------------------ weather

    private static async Task<Answer?> FetchWeather(string query, string q, CancellationToken ct)
    {
        string? place = WeatherAsks.Select(r => r.Match(q)).First(m => m.Success).Groups["p"].Value.Trim();
        double lat, lon;
        string name;
        if (place.Length > 0)
        {
            var geo = await GetJson($"https://geocoding-api.open-meteo.com/v1/search?name={U(place)}&count=1&language=en&format=json", ct);
            if (!geo.TryGetProperty("results", out var res) || res.GetArrayLength() == 0) return null;
            var p = res[0];
            lat = p.GetProperty("latitude").GetDouble();
            lon = p.GetProperty("longitude").GetDouble();
            name = p.GetProperty("name").GetString() ?? place;
        }
        else
        {
            // No place named: here, if Windows lets desktop apps know the location.
            var here = await Here(ct);
            if (here is not { } h) return null;
            (lat, lon, name) = (h.lat, h.lon, await PlaceName(h.lat, h.lon, ct) ?? "Your location");
        }

        var f = await GetJson(
            "https://api.open-meteo.com/v1/forecast?latitude=" + lat.ToString(CultureInfo.InvariantCulture) +
            "&longitude=" + lon.ToString(CultureInfo.InvariantCulture) +
            "&current=temperature_2m,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1", ct);
        var cur = f.GetProperty("current");
        var daily = f.GetProperty("daily");
        int code = cur.GetProperty("weather_code").GetInt32();
        bool day = cur.GetProperty("is_day").GetInt32() == 1;
        var local = DateTime.UtcNow.AddSeconds(f.GetProperty("utc_offset_seconds").GetInt32());
        var (condition, icon) = Describe(code, day);
        return new Weather(query, name, lat, lon, local,
            cur.GetProperty("temperature_2m").GetDouble(),
            daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            condition, icon);
    }

    /// <summary>The town at a point, as the Weather app names where you are.</summary>
    private static async Task<string?> PlaceName(double lat, double lon, CancellationToken ct)
    {
        try
        {
            var r = await GetJson("https://api.bigdatacloud.net/data/reverse-geocode-client?latitude=" +
                lat.ToString(CultureInfo.InvariantCulture) + "&longitude=" + lon.ToString(CultureInfo.InvariantCulture) +
                "&localityLanguage=en", ct);
            foreach (var f in new[] { "city", "locality", "principalSubdivision" })
                if (r.TryGetProperty(f, out var v) && v.GetString() is { Length: > 0 } n) return n;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log("no place name: " + ex.Message);
        }
        return null;
    }

    private static async Task<(double lat, double lon)?> Here(CancellationToken ct)
    {
        try
        {
            var loc = new Windows.Devices.Geolocation.Geolocator { DesiredAccuracy = Windows.Devices.Geolocation.PositionAccuracy.Default };
            var pos = await loc.GetGeopositionAsync(TimeSpan.FromMinutes(30), TimeSpan.FromSeconds(3)).AsTask(ct);
            return (pos.Coordinate.Point.Position.Latitude, pos.Coordinate.Point.Position.Longitude);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log("location unavailable: " + ex.Message);
            return null;
        }
    }

    /// <summary>WMO weather code to words and an emoji (drawn in colour by Segoe UI Emoji).</summary>
    private static (string, string) Describe(int code, bool day) => code switch
    {
        0 => (day ? "Sunny" : "Clear", day ? "☀️" : "\U0001F319"),
        1 => (day ? "Mostly sunny" : "Mostly clear", day ? "\U0001F324️" : "\U0001F319"),
        2 => ("Partly cloudy", day ? "⛅" : "☁️"),
        3 => ("Cloudy", "☁️"),
        45 or 48 => ("Fog", "\U0001F32B️"),
        >= 51 and <= 57 => ("Drizzle", "\U0001F326️"),
        >= 61 and <= 67 => ("Rain", "\U0001F327️"),
        >= 71 and <= 77 => ("Snow", "\U0001F328️"),
        >= 80 and <= 82 => ("Showers", "\U0001F326️"),
        85 or 86 => ("Snow showers", "\U0001F328️"),
        >= 95 => ("Thunderstorms", "⛈️"),
        _ => ("", "\U0001F321️"),
    };

    public static double ToUnit(double c, bool fahrenheit) => fahrenheit ? c * 9 / 5 + 32 : c;

    // ------------------------------------------------------------------ stocks

    public static readonly (string label, string range, string interval)[] Ranges =
    {
        ("1D", "1d", "5m"), ("5D", "5d", "15m"), ("1M", "1mo", "1d"), ("6M", "6mo", "1d"), ("1Y", "1y", "1d"), ("5Y", "5y", "1wk"),
    };

    private static readonly Dictionary<string, (string symbol, string name)> Symbols = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<Answer?> FetchStock(string query, string q, string range, CancellationToken ct)
    {
        string what = StockAsks.Select(r => r.Match(q)).First(m => m.Success).Groups["q"].Value.Trim();
        if (what.Length == 0) return null;
        (string symbol, string name) found;
        lock (Symbols) Symbols.TryGetValue(what, out found);
        if (found.symbol == null)
        {
            var s = await GetJson($"https://query2.finance.yahoo.com/v1/finance/search?q={U(what)}&quotesCount=3&newsCount=0", ct);
            var quote = s.GetProperty("quotes").EnumerateArray().FirstOrDefault(x =>
                x.TryGetProperty("quoteType", out var t) &&
                t.GetString() is "EQUITY" or "ETF" or "INDEX" or "MUTUALFUND" or "CRYPTOCURRENCY");
            if (quote.ValueKind != JsonValueKind.Object) return null;
            string sym = quote.GetProperty("symbol").GetString()!;
            string nm = (quote.TryGetProperty("longname", out var ln) ? ln.GetString() : null)
                ?? (quote.TryGetProperty("shortname", out var sn) ? sn.GetString() : null) ?? sym;
            found = (sym, nm);
            lock (Symbols) Symbols[what] = found;
        }
        return await FetchChart(query, found.symbol, found.name, range, ct);
    }

    /// <summary>The same stock over another range (the card's 1D / 5D / ... menu).</summary>
    public static async Task<Stock?> Rechart(Stock s, string range, CancellationToken ct)
    {
        try { return await FetchChart(s.Query, s.Symbol, s.Name, range, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            App.Log("chart failed: " + ex.Message);
            return null;
        }
    }

    private static async Task<Stock?> FetchChart(string query, string symbol, string name, string range, CancellationToken ct)
    {
        var r = Ranges.FirstOrDefault(x => x.label == range);
        if (r.label == null) r = Ranges[0];
        var c = await GetJson($"https://query1.finance.yahoo.com/v8/finance/chart/{U(symbol)}?range={r.range}&interval={r.interval}", ct);
        var res = c.GetProperty("chart").GetProperty("result")[0];
        var meta = res.GetProperty("meta");
        double price = meta.GetProperty("regularMarketPrice").GetDouble();
        double prev = meta.TryGetProperty("chartPreviousClose", out var pc) ? pc.GetDouble()
            : meta.TryGetProperty("previousClose", out var pc2) ? pc2.GetDouble() : price;
        var series = new List<double>();
        if (res.TryGetProperty("indicators", out var ind) && ind.GetProperty("quote")[0].TryGetProperty("close", out var closes))
        {
            foreach (var v in closes.EnumerateArray())
            {
                if (v.ValueKind == JsonValueKind.Number) series.Add(v.GetDouble());
            }
        }

        // "October 5, 4:00 PM EDT · Market closed", in the exchange's time.
        long t = meta.TryGetProperty("regularMarketTime", out var rt) ? rt.GetInt64() : DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        int off = meta.TryGetProperty("gmtoffset", out var go) ? go.GetInt32() : 0;
        string tz = meta.TryGetProperty("timezone", out var tzv) ? tzv.GetString() ?? "" : "";
        var when = DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.AddSeconds(off);
        bool open = false;
        if (meta.TryGetProperty("currentTradingPeriod", out var ctp) && ctp.TryGetProperty("regular", out var reg))
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            open = now >= reg.GetProperty("start").GetInt64() && now < reg.GetProperty("end").GetInt64();
        }
        string note = $"{when.ToString("MMMM d, h:mm tt", CultureInfo.CurrentCulture)} {tz} · {(open ? "Market open" : "Market closed")}";
        string currency = meta.TryGetProperty("currency", out var cur) ? cur.GetString() ?? "" : "";
        double change = price - prev;
        return new Stock(query, symbol, name, currency, price, change, prev != 0 ? change / prev * 100 : 0, prev, series, note, r.label);
    }

    // ------------------------------------------------------------------ facts

    private static async Task<Answer?> FetchFact(string query, string q, CancellationToken ct)
    {
        foreach (var ask in FactAsks)
        {
            var m = ask.Pattern.Match(q);
            if (!m.Success) continue;
            var fact = await WikidataFact(query, m.Groups["s"].Value.Trim(), ask, ct);
            if (fact != null) return fact;
            break;
        }
        var sm = SummaryAsk.Match(q);
        return sm.Success ? await Summary(query, sm.Groups["s"].Value.Trim(), ct) : null;
    }

    private const string Wd = "https://www.wikidata.org/w/api.php?format=json&";

    private static async Task<Answer?> WikidataFact(string query, string subject, FactAsk ask, CancellationToken ct)
    {
        var search = await GetJson($"{Wd}action=wbsearchentities&search={U(subject)}&language=en&uselang=en&limit=1&type=item", ct);
        var hits = search.GetProperty("search");
        if (hits.GetArrayLength() == 0) return null;
        string id = hits[0].GetProperty("id").GetString()!;
        string label = hits[0].TryGetProperty("label", out var l) ? l.GetString() ?? subject : subject;

        var ent = (await GetJson($"{Wd}action=wbgetentities&ids={id}&props=claims&languages=en", ct))
            .GetProperty("entities").GetProperty(id);
        if (!ent.TryGetProperty("claims", out var claims)) return null;

        string? image = null;
        if (claims.TryGetProperty("P18", out var img) && Best(img) is { } imgClaim && Snak(imgClaim) is { ValueKind: JsonValueKind.String } file)
        {
            image = "https://commons.wikimedia.org/wiki/Special:FilePath/" + U(file.GetString()!) + "?width=320";
        }

        foreach (string prop in ask.Props)
        {
            if (!claims.TryGetProperty(prop, out var list)) continue;
            var (value, secondary, lbl) = ask.Value switch
            {
                Value.Quantity => Quantity(list),
                Value.Number => Number(list),
                Value.Item => await Items(list, ct),
                Value.Date => (Date(list), "", ask.Label),
                Value.Age => Age(list, prop, claims),
                _ => ("", "", ask.Label),
            };
            if (value.Length == 0) continue;
            return new Fact(query, label, lbl.Length > 0 ? lbl : ask.Label, value, secondary, "", image);
        }
        return null;
    }

    /// <summary>The claim to quote: the preferred one, else the most recent by its "point in time", else the first.</summary>
    private static JsonElement? Best(JsonElement list)
    {
        JsonElement? best = null;
        string bestWhen = "";
        foreach (var c in list.EnumerateArray())
        {
            if (c.GetProperty("rank").GetString() == "deprecated" || Snak(c) is null) continue;
            if (c.GetProperty("rank").GetString() == "preferred") return c;
            string when = c.TryGetProperty("qualifiers", out var qs) && qs.TryGetProperty("P585", out var pit)
                ? pit[0].GetProperty("datavalue").GetProperty("value").GetProperty("time").GetString() ?? "" : "";
            if (best == null || string.CompareOrdinal(when, bestWhen) > 0)
            {
                best = c;
                bestWhen = when;
            }
        }
        return best;
    }

    private static JsonElement? Snak(JsonElement claim) =>
        claim.GetProperty("mainsnak").TryGetProperty("datavalue", out var dv) ? dv.GetProperty("value") : null;

    // Units Wikidata uses for heights, lengths and areas, in metres (or square metres).
    private static readonly Dictionary<string, (double toMetric, bool area)> Units = new()
    {
        ["Q11573"] = (1, false), ["Q828224"] = (1000, false), ["Q174728"] = (0.01, false), ["Q3710"] = (0.3048, false),
        ["Q253276"] = (1609.344, false), ["Q482798"] = (0.9144, false), ["Q218593"] = (0.0254, false),
        ["Q25343"] = (1, true), ["Q712226"] = (1e6, true), ["Q232291"] = (2589988.11, true), ["Q35852"] = (1e4, true),
        ["Q81292"] = (4046.86, true),
    };

    private static (string, string, string) Quantity(JsonElement list)
    {
        if (Best(list) is not { } c || Snak(c) is not { } v) return ("", "", "");
        double amount = double.Parse(v.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
        string unit = v.GetProperty("unit").GetString()!.Split('/')[^1];
        if (!Units.TryGetValue(unit, out var u)) return (amount.ToString("N0"), "", "");
        double si = amount * u.toMetric;
        string metric, imperial;
        if (u.area)
        {
            metric = si >= 1e6 ? $"{si / 1e6:N0} km²" : $"{si:N0} m²";
            imperial = si >= 2589988 ? $"{si / 2589988.11:N0} sq mi" : $"{si / 0.092903:N0} sq ft";
        }
        else
        {
            metric = si >= 10000 ? $"{si / 1000:N0} km" : $"{si:N0} m";
            imperial = si >= 16093 ? $"{si / 1609.344:N0} mi" : $"{si / 0.3048:N0} ft";
        }
        return Imperial ? (imperial, metric, "") : (metric, imperial, "");
    }

    private static (string, string, string) Number(JsonElement list)
    {
        if (Best(list) is not { } c || Snak(c) is not { } v) return ("", "", "");
        double amount = double.Parse(v.GetProperty("amount").GetString()!, CultureInfo.InvariantCulture);
        string year = c.TryGetProperty("qualifiers", out var qs) && qs.TryGetProperty("P585", out var pit)
            ? Year(pit[0].GetProperty("datavalue").GetProperty("value").GetProperty("time").GetString()) : "";
        return (amount.ToString("N0"), year.Length > 0 ? $"({year})" : "", "");
    }

    private static async Task<(string, string, string)> Items(JsonElement list, CancellationToken ct)
    {
        var ids = list.EnumerateArray()
            .Where(c => c.GetProperty("rank").GetString() != "deprecated" && Snak(c) is { ValueKind: JsonValueKind.Object })
            .OrderByDescending(c => c.GetProperty("rank").GetString() == "preferred")
            .Select(c => Snak(c)!.Value.GetProperty("id").GetString()!)
            .Distinct().Take(3).ToList();
        if (ids.Count == 0) return ("", "", "");
        var ents = (await GetJson($"{Wd}action=wbgetentities&ids={string.Join('|', ids)}&props=labels&languages=en", ct)).GetProperty("entities");
        var names = ids.Select(i => ents.GetProperty(i).TryGetProperty("labels", out var ls) && ls.TryGetProperty("en", out var en)
            ? en.GetProperty("value").GetString() : null).Where(n => n != null).ToList();
        return names.Count == 0 ? ("", "", "") : (names[0]!, string.Join(", ", names.Skip(1)), "");
    }

    private static (DateTime when, int precision)? Time(JsonElement list)
    {
        if (Best(list) is not { } c || Snak(c) is not { } v) return null;
        string t = v.GetProperty("time").GetString()!;
        int precision = v.GetProperty("precision").GetInt32();
        var m = Regex.Match(t, @"^\+(\d{1,4})-(\d\d)-(\d\d)");
        if (!m.Success) return null;
        int y = int.Parse(m.Groups[1].Value), mo = Math.Max(1, int.Parse(m.Groups[2].Value)), d = Math.Max(1, int.Parse(m.Groups[3].Value));
        return (new DateTime(y, mo, d), precision);
    }

    private static string Date(JsonElement list) => Time(list) is not { } t ? "" : t.precision switch
    {
        >= 11 => t.when.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture),
        10 => t.when.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
        _ => t.when.Year.ToString(),
    };

    private static string Year(string? time) => time is { Length: > 5 } ? time.Substring(1, 4).TrimStart('0') : "";

    private static (string, string, string) Age(JsonElement list, string prop, JsonElement claims)
    {
        if (Time(list) is not { } t) return ("", "", "");
        static string Day((DateTime when, int precision) t) =>
            t.precision >= 11 ? t.when.ToString("MMMM d, yyyy", CultureInfo.CurrentCulture) : t.when.Year.ToString();
        static int Years(DateTime from, DateTime to, bool exact) =>
            to.Year - from.Year - (exact && to.DayOfYear < from.DayOfYear ? 1 : 0);
        if (prop == "P569" && claims.TryGetProperty("P570", out var died) && Time(died) is { } d)
        {
            return ($"{Years(t.when, d.when, t.precision >= 11)} years", $"{Day(t)} – {Day(d)}", "Age at death");
        }
        return ($"{Years(t.when, DateTime.Today, t.precision >= 11)} years", (prop == "P569" ? "Born " : "Since ") + Day(t), "Age");
    }

    private static async Task<Answer?> Summary(string query, string subject, CancellationToken ct)
    {
        var found = await GetJson($"https://en.wikipedia.org/w/api.php?action=opensearch&search={U(subject)}&limit=1&namespace=0&format=json", ct);
        if (found.GetArrayLength() < 2 || found[1].GetArrayLength() == 0) return null;
        string title = found[1][0].GetString()!;
        var s = await GetJson("https://en.wikipedia.org/api/rest_v1/page/summary/" + U(title.Replace(' ', '_')), ct);
        if (s.TryGetProperty("type", out var type) && type.GetString() != "standard") return null;   // disambiguation pages
        string extract = s.TryGetProperty("extract", out var ex) ? ex.GetString() ?? "" : "";
        if (extract.Length == 0) return null;
        string desc = s.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        string? image = s.TryGetProperty("thumbnail", out var th) ? th.GetProperty("source").GetString() : null;
        string name = s.TryGetProperty("title", out var tt) ? tt.GetString() ?? title : title;
        return new Fact(query, name, Capitalise(desc), "", "", extract, image);
    }

    private static string Capitalise(string s) => s.Length > 0 ? char.ToUpper(s[0], CultureInfo.CurrentCulture) + s[1..] : s;
}
