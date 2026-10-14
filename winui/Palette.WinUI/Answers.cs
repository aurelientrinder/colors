using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;

namespace Palette;

/// <summary>
/// Answers in the list, as in the new Windows Search:
/// <list type="bullet">
/// <item>Web answers (<see cref="WebAnswers"/>): once typing pauses on a question they handle,
/// a "search the web" row for the query is put on top, with its card (weather, a stock, a
/// fact) open under it.</item>
/// <item>The condensed list: while an answer leads (a web answer, a sum, a quick setting), only
/// it shows, with "Scroll or click to see more results" under it; a scroll, a click there or an
/// arrow key brings back the rest.</item>
/// </list>
/// The answer row is the app's own, so it shifts the engine's rows down by one:
/// <see cref="AnswerOffset"/> turns a list index into the engine's.
/// </summary>
public sealed partial class MainWindow
{
    private const double HintDip = 56;          // the "Scroll or click to see more results" line
    private CancellationTokenSource? _answerCts;
    private string? _expandedQuery;              // the query whose condensed list was opened up
    private int _condensedRows;                  // rows shown while condensed; 0 shows them all
    private bool _fahrenheit = WebAnswers.Imperial;

    private int AnswerOffset => Items.Count > 0 && Items[0].IsAnswer ? 1 : 0;
    private HitVm? AnswerRow => AnswerOffset == 1 ? Items[0] : null;
    /// <summary>The leading row's card, whether on an answer row or on the Weather app's own row.</summary>
    private WebAnswers.Answer? LeadAnswer => Items.Count > 0 ? Items[0].Answer : null;

    private void InitAnswers()
    {
        // The list does not scroll while condensed, so the wheel reaches the body and opens it up.
        Body.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler(OnBodyWheel), handledEventsToo: true);
        foreach (var (label, _, _) in WebAnswers.Ranges)
        {
            var item = new MenuFlyoutItem { Text = label, Tag = label };
            item.Click += OnStockRangeClick;
            StockRangeMenu.Items.Add(item);
        }
    }

    // ------------------------------------------------------------ web answers

    private static bool SameQuery(string a, string b) =>
        string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Before showing results for <paramref name="query"/>: an answer to another query goes.</summary>
    private void DropStaleAnswer(string query)
    {
        if (LeadAnswer is not { } a || SameQuery(a.Query, query)) return;
        if (Items[0].IsAnswer) Items.RemoveAt(0);
        else Items[0].AttachWeather(null);
    }

    /// <summary>After showing results for <paramref name="query"/>: look for an answer once typing pauses.</summary>
    private void RequestAnswer(string query)
    {
        _answerCts?.Cancel();
        _answerCts = null;
        if (!_settings.WebAnswers || _view != View.Results || LeadAnswer != null || WebAnswers.Detect(query) == null) return;
        if (WebAnswers.TryCached(query, out var cached))
        {
            if (cached != null) ShowAnswerRow(cached);
            return;
        }
        var cts = _answerCts = new CancellationTokenSource();
        _ = FetchAnswer(query, cts.Token);
    }

    private async Task FetchAnswer(string query, CancellationToken ct)
    {
        try
        {
            await Task.Delay(300, ct);   // a pause in typing, not every keystroke
            var a = await WebAnswers.Fetch(query, ct);
            if (a == null || ct.IsCancellationRequested || !_visible || _view != View.Results || !SameQuery(_latestQuery, query)) return;
            if (LeadAnswer == null) ShowAnswerRow(a);
        }
        catch (OperationCanceledException) { }
    }

    private void ShowAnswerRow(WebAnswers.Answer answer)
    {
        string url = "https://www.google.com/search?q=" + Uri.EscapeDataString(answer.Query.Trim());
        int sel = ResultsList.SelectedIndex;
        if (answer is WebAnswers.Weather w && WebAnswers.HasWeatherApp)
        {
            // Weather is the Weather app's: its card goes under the app's own row when the engine
            // led with it ("weather"), or under a row for the app put on top ("weather in Paris").
            var top = Items.Count > 0 ? Items[0] : null;
            if (top != null && WebAnswers.IsWeatherApp(top.RawKind, top.Title, top.IconKey)) top.AttachWeather(w);
            else
            {
                var vm = HitVm.ForWeatherApp(w, url);
                FillIcon(vm);
                Items.Insert(0, vm);
            }
        }
        else
        {
            Items.Insert(0, HitVm.ForAnswer(answer, url));
        }
        if (sel <= 0) ResultsList.SelectedIndex = 0;
        else _expandedQuery = _latestQuery;   // the user has moved on down the list: leave it open
        UpdateCondensed();
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
    }

    private static void OpenUrl(string url)
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) { App.Log("could not open " + url + ": " + ex.Message); }
    }

    // ------------------------------------------------------------ condensed list

    /// <summary>Show only the leading answer (and the line under it) or the whole list.</summary>
    private void UpdateCondensed()
    {
        int n = 0;
        if (_view == View.Results && Items.Count > 0 && ResultsList.SelectedIndex <= 0
            && !(_expandedQuery != null && SameQuery(_expandedQuery, _latestQuery)))
        {
            var top = Items[0];
            n = top.Answer != null || top.IsCalc || top.IsPlan ? 1 : top.IsToggle ? 2 : 0;
        }
        if (n >= Items.Count) n = 0;   // nothing more to see
        if (n == _condensedRows) return;
        _condensedRows = n;
        MoreHint.Visibility = n > 0 ? Visibility.Visible : Visibility.Collapsed;
        ScrollViewer.SetVerticalScrollMode(ResultsList, n > 0 ? ScrollMode.Disabled : ScrollMode.Enabled);
    }

    /// <summary>Open up a condensed list; false when it was not condensed.</summary>
    private bool Expand()
    {
        if (_condensedRows == 0) return false;
        _expandedQuery = _latestQuery;
        UpdateCondensed();
        Resize(recenter: false);
        return true;
    }

    private void OnMoreHintTapped(object sender, TappedRoutedEventArgs e) => Expand();

    private void OnBodyWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Expand()) e.Handled = true;
    }

    // ------------------------------------------------------------ cards

    private void ShowWebAnswer(HitVm vm)
    {
        ClearPreview();
        FilePreview.Visibility = Visibility.Collapsed;
        double h = 0;
        switch (vm.Answer)
        {
            case WebAnswers.Weather w:
                RenderWeather(w);
                WeatherPreview.Visibility = Visibility.Visible;
                h = WeatherPreview.Height;
                break;
            case WebAnswers.Stock s:
                RenderStock(s);
                StockPreview.Visibility = Visibility.Visible;
                h = StockPreview.Height;
                break;
            case WebAnswers.Fact f:
                RenderFact(f);
                FactPreview.Visibility = Visibility.Visible;
                h = FactPreview.Height;
                break;
        }
        _pvFull = 6 + h + 12;   // the pane's gap above and margin below, as for a sum
    }

    private void RenderWeather(WebAnswers.Weather w)
    {
        string T(double c) => $"{Math.Round(WebAnswers.ToUnit(c, _fahrenheit)):0}°";
        WeatherPlace.Text = $"{w.Place} · {w.LocalTime.ToString("t", CultureInfo.CurrentCulture)}";
        WeatherTemp.Text = T(w.TempC);
        WeatherHiLo.Text = $"H {T(w.HighC)}   L {T(w.LowC)}";
        WeatherCondition.Text = w.Condition;
        WeatherIcon.Text = w.Icon;
        WeatherF.Opacity = _fahrenheit ? 1 : 0.5;   // the unit in use reads stronger
        WeatherC.Opacity = _fahrenheit ? 0.5 : 1;
    }

    private void OnWeatherUnitClick(object sender, RoutedEventArgs e)
    {
        _fahrenheit = (sender as FrameworkElement)?.Tag as string == "F";
        if (_pvVm?.Answer is WebAnswers.Weather w) RenderWeather(w);
    }

    private void RenderStock(WebAnswers.Stock s)
    {
        StockSymbol.Text = s.Symbol;
        StockName.Text = s.Currency is { Length: > 0 } c && c != "USD" ? $"{s.Name} · {c}" : s.Name;
        StockPrice.Text = s.Price.ToString("N2", CultureInfo.CurrentCulture);
        bool up = s.Change >= 0;
        StockChange.Text = (up ? "+" : "−") + Math.Abs(s.ChangePct).ToString("N2", CultureInfo.CurrentCulture) + "%";
        // The system's success and critical colours for the theme.
        bool dark = Root.ActualTheme == ElementTheme.Dark;
        var tone = new SolidColorBrush(up
            ? (dark ? Windows.UI.Color.FromArgb(255, 0x6C, 0xCB, 0x5F) : Windows.UI.Color.FromArgb(255, 0x0F, 0x7B, 0x0F))
            : (dark ? Windows.UI.Color.FromArgb(255, 0xFF, 0x99, 0xA4) : Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C)));
        StockChange.Foreground = tone;
        StockLine.Stroke = tone;
        StockNote.Text = s.Note;
        StockRangeText.Text = s.Range;
        DrawChart(s);
    }

    private void OnStockChartSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_pvVm?.Answer is WebAnswers.Stock s) DrawChart(s);
    }

    /// <summary>The price line, scaled to the box, with the previous close dotted across it.</summary>
    private void DrawChart(WebAnswers.Stock s)
    {
        double w = StockChart.ActualWidth, h = StockChart.ActualHeight;
        var pts = new PointCollection();
        if (w <= 0 || h <= 0 || s.Series.Count < 2)
        {
            StockLine.Points = pts;
            StockBaseline.Visibility = Visibility.Collapsed;
            return;
        }
        double min = Math.Min(s.Series.Min(), s.PreviousClose), max = Math.Max(s.Series.Max(), s.PreviousClose);
        if (max - min < 1e-9) max = min + 1;
        double Y(double v) => h - 2 - (v - min) / (max - min) * (h - 4);
        for (int i = 0; i < s.Series.Count; i++) pts.Add(new Point(i * w / (s.Series.Count - 1), Y(s.Series[i])));
        StockLine.Points = pts;
        StockBaseline.X1 = 0;
        StockBaseline.X2 = w;
        StockBaseline.Y1 = StockBaseline.Y2 = Y(s.PreviousClose);
        StockBaseline.Visibility = Visibility.Visible;
    }

    private async void OnStockRangeClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string range || _pvVm is not { Answer: WebAnswers.Stock s } vm) return;
        var fresh = await WebAnswers.Rechart(s, range, CancellationToken.None);
        if (fresh == null || vm.Answer != s) return;
        vm.Answer = fresh;
        if (_pvVm == vm) RenderStock(fresh);
    }

    private void OnCardFlyoutClosed(object? sender, object e) => Input.Focus(FocusState.Programmatic);

    private void RenderFact(WebAnswers.Fact f)
    {
        bool summary = f.Text.Length > 0;
        FactSubject.Text = f.Subject;
        FactLabel.Text = f.Label;
        FactLabel.Visibility = f.Label.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        FactLabel.Margin = new Thickness(0, summary ? 2 : 14, 0, 0);
        FactLabel.FontWeight = summary ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        FactValue.Text = f.Value;
        FactSecondary.Text = f.Secondary;
        FactValueRow.Visibility = summary ? Visibility.Collapsed : Visibility.Visible;
        FactText.Text = f.Text;
        FactText.Visibility = summary ? Visibility.Visible : Visibility.Collapsed;
        FactImage.Source = f.ImageUrl is { } u ? new BitmapImage(new Uri(u)) { DecodePixelWidth = 300 } : null;
        FactImageFrame.Visibility = f.ImageUrl != null ? Visibility.Visible : Visibility.Collapsed;
        FactPreview.Height = summary ? 176 : 128;
    }

    /// <summary>"2*(3+4)" as "2 × (3 + 4)": the sum as it reads, for the answer card.</summary>
    private static string PrettySum(string expr)
    {
        var sb = new System.Text.StringBuilder();
        char prev = ' ';
        foreach (char ch in expr)
        {
            if (ch == ' ') continue;
            bool binary = "+-*/^×÷".Contains(ch) && (char.IsLetterOrDigit(prev) || prev is ')' or '.' or '%');
            string op = ch switch { '*' => "×", '/' => "÷", '-' => "−", _ => ch.ToString() };
            if (binary) sb.Append(' ').Append(op).Append(' ');
            else sb.Append(ch == '-' ? "−" : ch.ToString());
            prev = ch;
        }
        return sb.ToString();
    }
}
