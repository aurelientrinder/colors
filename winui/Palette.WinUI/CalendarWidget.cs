using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Palette;

/// <summary>
/// The calendar card (events: Calendar.cs). "calendar" shows the month, a dot under each day with
/// events, and the events of the day tapped (today to begin with); "today" shows today's events alone.
/// </summary>
public sealed partial class MainWindow
{
    private const double CalendarCardDip = 270, TodayCardDip = 210;

    private DateTime _calMonth = DateTime.Today;   // the first of the month shown
    private DateTime _calDay = DateTime.Today;     // the day whose events are listed
    private bool _calBuilt;
    private readonly Grid[] _calCells = new Grid[42];

    private bool CalendarOnCard => _visible && _pvVm?.Answer is CalendarAnswer && CalendarPreview.Visibility == Visibility.Visible;

    private void ShowCalendarRow(CalendarAnswer answer)
    {
        int sel = ResultsList.SelectedIndex;
        Items.Insert(0, HitVm.ForCalendar(answer, _settings.CalendarFeeds.Count > 0));
        if (sel <= 0) ResultsList.SelectedIndex = 0;
        else _expandedQuery = _latestQuery;
        UpdateCondensed();
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
    }

    private void RenderCalendar(CalendarAnswer a)
    {
        BuildCalendarCells();
        _calDay = DateTime.Today;
        _calMonth = new DateTime(_calDay.Year, _calDay.Month, 1);
        CalMonth.Visibility = a.Today ? Visibility.Collapsed : Visibility.Visible;
        CalMonthCol.Width = new GridLength(a.Today ? 0 : 224);
        CalendarPreview.ColumnSpacing = a.Today ? 0 : 20;
        CalendarPreview.Height = a.Today ? TodayCardDip : CalendarCardDip;
        DrawCalendar();
        if (CalendarData.IsStale) _ = LoadCalendar();
    }

    private async Task LoadCalendar()
    {
        try { await CalendarData.Refresh(_settings.CalendarFeeds.ToList(), force: false, CancellationToken.None); }
        catch (Exception ex) { App.Log("calendar not loaded: " + ex.Message); }
        DispatcherQueue.TryEnqueue(() => { if (CalendarOnCard) DrawCalendar(); });
    }

    private void BuildCalendarCells()
    {
        if (_calBuilt) return;
        _calBuilt = true;
        for (int i = 0; i < 7; i++)
        {
            CalWeekdays.ColumnDefinitions.Add(new ColumnDefinition());
            CalDays.ColumnDefinitions.Add(new ColumnDefinition());
        }
        for (int r = 0; r < 6; r++) CalDays.RowDefinitions.Add(new RowDefinition());
        var names = CultureInfo.CurrentCulture.DateTimeFormat;
        var first = names.FirstDayOfWeek;
        for (int i = 0; i < 7; i++)
        {
            var t = new TextBlock
            {
                Text = names.GetShortestDayName((DayOfWeek)(((int)first + i) % 7)),
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                Foreground = CalTertiary.Fill,
            };
            Grid.SetColumn(t, i);
            CalWeekdays.Children.Add(t);
        }
        for (int i = 0; i < 42; i++)
        {
            var ring = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            ring.Child = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dot = new Ellipse { Width = 4, Height = 4, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 0), Visibility = Visibility.Collapsed };
            var cell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            cell.Children.Add(ring);
            cell.Children.Add(dot);
            cell.Tapped += OnCalCellTapped;
            Grid.SetColumn(cell, i % 7);
            Grid.SetRow(cell, i / 7);
            CalDays.Children.Add(cell);
            _calCells[i] = cell;
        }
    }

    private void DrawCalendar()
    {
        var today = DateTime.Today;
        var culture = CultureInfo.CurrentCulture;
        bool todayCard = _pvVm?.Answer is CalendarAnswer { Today: true };

        if (!todayCard)
        {
            CalMonthTitle.Text = _calMonth.ToString("MMMM yyyy", culture);
            int lead = ((int)_calMonth.DayOfWeek - (int)culture.DateTimeFormat.FirstDayOfWeek + 7) % 7;
            var gridStart = _calMonth.AddDays(-lead);
            var withEvents = CalendarData.Days(gridStart, gridStart.AddDays(42));
            for (int i = 0; i < 42; i++)
            {
                var date = gridStart.AddDays(i);
                var cell = _calCells[i];
                var ring = (Border)cell.Children[0];
                var dot = (Ellipse)cell.Children[1];
                var text = (TextBlock)ring.Child;
                bool selected = date == _calDay.Date, isToday = date == today;
                cell.Tag = date;
                text.Text = date.Day.ToString(culture);
                text.Opacity = date.Month == _calMonth.Month ? 1 : 0.4;
                text.FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal;
                text.Foreground = selected ? CalOnAccent.Fill : isToday ? CalAccent.Fill : null;
                ring.Background = selected ? CalAccent.Fill : null;
                ring.BorderBrush = isToday && !selected ? CalAccent.Fill : null;
                ring.BorderThickness = new Thickness(isToday && !selected ? 1 : 0);
                dot.Visibility = withEvents.Contains(date) ? Visibility.Visible : Visibility.Collapsed;
                dot.Fill = selected ? CalOnAccent.Fill : CalAccent.Fill;
                dot.Margin = new Thickness(0, 0, 0, 1);
            }
        }

        var day = _calDay.Date;
        CalDayTitle.Text = todayCard
            ? "Today · " + day.ToString("dddd d MMMM", culture)
            : day == today ? "Today · " + day.ToString("d MMMM", culture) : day.ToString("dddd d MMMM", culture);

        CalEvents.Children.Clear();
        var events = CalendarData.On(day);
        bool loading = CalendarData.LoadedAt == DateTime.MinValue;
        if (events.Count == 0)
        {
            string note = loading ? "Loading…"
                : _settings.CalendarFeeds.Count == 0 && CalendarData.Sources == 0
                    ? "No calendar connected yet. Open “Palette: Settings” and choose Add Google Calendar."
                : CalendarData.Problem is { } p ? p + ". Check the address in Palette’s settings."
                : day < today ? "Nothing was planned" : "Nothing planned";
            CalEvents.Children.Add(new TextBlock { Text = note, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = CalSecondary.Fill });
            return;
        }
        var now = DateTime.Now;
        foreach (var e in events) CalEvents.Children.Add(EventRow(e, day, now, culture));
        if (CalendarData.Problem is { } problem)
            CalEvents.Children.Add(new TextBlock { Text = problem, FontSize = 11, Foreground = CalTertiary.Fill });
    }

    private UIElement EventRow(CalEvent e, DateTime day, DateTime now, CultureInfo culture)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition());

        var when = new StackPanel();
        bool startsBefore = e.Start.Date < day, endsAfter = e.End > day.AddDays(1);
        if (e.AllDay || (startsBefore && endsAfter))
        {
            when.Children.Add(new TextBlock { Text = "All day", FontSize = 12, Foreground = CalSecondary.Fill });
        }
        else
        {
            when.Children.Add(new TextBlock { Text = startsBefore ? "…" : e.Start.ToString("t", culture), FontSize = 12, Foreground = CalSecondary.Fill });
            if (e.End > e.Start)
                when.Children.Add(new TextBlock { Text = endsAfter ? "…" : e.End.ToString("t", culture), FontSize = 11, Foreground = CalTertiary.Fill });
        }
        row.Children.Add(when);

        var what = new StackPanel();
        what.Children.Add(new TextBlock { Text = e.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (e.Location.Length > 0)
            what.Children.Add(new TextBlock { Text = e.Location, FontSize = 11, Foreground = CalTertiary.Fill, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(what, 1);
        row.Children.Add(what);

        if (day == now.Date && !e.AllDay && e.End <= now) row.Opacity = 0.5;   // already over
        return row;
    }

    private void OnCalCellTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DateTime date)
        {
            _calDay = date;
            _calMonth = new DateTime(date.Year, date.Month, 1);
            DrawCalendar();
        }
        Input.Focus(FocusState.Programmatic);
    }

    private void OnCalPrev(object sender, RoutedEventArgs e) => MoveCalendarMonth(-1);

    private void OnCalNext(object sender, RoutedEventArgs e) => MoveCalendarMonth(1);

    private void MoveCalendarMonth(int by)
    {
        var next = _calMonth.AddMonths(by);
        var thisMonth = new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1);
        if (next < thisMonth.AddMonths(-1) || next > thisMonth.AddMonths(5)) { Input.Focus(FocusState.Programmatic); return; }   // beyond what is loaded
        _calMonth = next;
        _calDay = next == thisMonth ? DateTime.Today : next;
        DrawCalendar();
        Input.Focus(FocusState.Programmatic);
    }

    // ------------------------------------------------------------ Google Calendar, from the settings list

    /// <summary>Settings › Add Google Calendar: take the secret iCal address from the clipboard, check it and keep it.</summary>
    private async Task AddCalendarFromClipboard(HitVm row)
    {
        const string hint = "Copy the secret iCal address (Google Calendar › Settings › Integrate calendar), then press Enter";
        string? text = null;
        try
        {
            var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
            if (view.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                text = (await view.GetTextAsync())?.Trim();
        }
        catch (Exception ex) { App.Log("clipboard not read for calendar: " + ex.Message); }
        if (string.IsNullOrEmpty(text) || !CalendarData.LooksLikeFeed(text))
        {
            row.Describe(hint);
            return;
        }
        if (_settings.CalendarFeeds.Contains(text))
        {
            row.Describe("That calendar is already added");
            return;
        }
        row.Describe("Checking the address…");
        try
        {
            var events = await CalendarData.ReadFeed(text, CancellationToken.None);
            _settings.CalendarFeeds.Add(text);
            _settings.Save();
            CalendarData.Invalidate();
            if (_view == View.Settings) ShowSettings(Input.Text);
            if (Items.FirstOrDefault(i => i.Does == "gcal") is { } again)
                again.Describe($"Added: {events.Count} events in the coming months. Type “calendar” or “today” to see them");
        }
        catch (Exception ex)
        {
            App.Log("calendar address refused: " + ex.GetType().Name);
            if (_view == View.Settings && Items.FirstOrDefault(i => i.Does == "gcal") is { } r)
                r.Describe("That address did not give a calendar. " + hint);
        }
    }

    private void RemoveCalendars()
    {
        _settings.CalendarFeeds.Clear();
        _settings.Save();
        CalendarData.Invalidate();
        ShowSettings(Input.Text);
        if (Items.FirstOrDefault(i => i.Does == "gcal") is { } r) r.Describe("Google calendars removed");
    }
}
