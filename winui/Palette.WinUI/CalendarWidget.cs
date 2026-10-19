using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.System;

namespace Palette;

/// <summary>
/// The calendar card (events: Calendar.cs). "calendar" shows the month, a dot
/// under each day with events, and the events of the day tapped; "today", "tomorrow" or a weekday show that
/// day's events; "this week" and "next week" a line per day. In a day's list, Down and Up move through
/// the events and Enter opens one's details (its notes, a link, a map); PageUp and PageDown move by month,
/// day or week.
/// </summary>
public sealed partial class MainWindow
{
    private const double CalendarCardDip = 270, DayCardDip = 230;

    private CalMode _calMode;
    private DateTime _calMonth = DateTime.Today;   // the first of the month shown
    private DateTime _calDay = DateTime.Today;     // the day whose events are listed
    private DateTime _calWeek = DateTime.Today;    // the first day of the week listed
    private List<CalEvent> _calList = new();       // the day's events on the card
    private int _calCursor = -1;                   // the event the arrow keys are on
    private CalEvent? _calOpen;                    // the event whose details show
    private bool _calBuilt;
    private readonly Grid[] _calCells = new Grid[42];

    private bool CalendarShowing => _pvVm?.Answer is CalendarAnswer && CalendarPreview.Visibility == Visibility.Visible;

    /// <summary>Only the month card needs the room.</summary>
    private bool CalendarWide => _pvVm?.Answer is CalendarAnswer { Mode: CalMode.Month } && CalendarPreview.Visibility == Visibility.Visible;

    private bool CalendarOnCard => _visible && CalendarShowing;

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
        _calMode = a.Mode;
        _calDay = a.Mode == CalMode.Day ? a.Day : DateTime.Today;
        _calWeek = a.Mode == CalMode.Week ? a.Day : CalendarData.WeekStart(DateTime.Today);
        _calMonth = new DateTime(_calDay.Year, _calDay.Month, 1);
        _calCursor = -1;
        _calOpen = null;
        bool month = a.Mode == CalMode.Month;
        CalMonth.Visibility = month ? Visibility.Visible : Visibility.Collapsed;
        CalMonthCol.Width = new GridLength(month ? 236 : 0);
        CalendarPreview.ColumnSpacing = month ? 22 : 0;
        CalendarPreview.Height = a.Mode == CalMode.Day ? DayCardDip : CalendarCardDip;
        DrawCalendar();
        if (CalendarData.IsStale) _ = LoadCalendar();
    }

    private bool _calRedraw;   // fresh events arrived mid-animation: drawn once it settles (OnFrame)

    private async Task LoadCalendar()
    {
        var feeds = _settings.CalendarFeeds.ToList();
        // Fetching, parsing and merging all off the UI thread: nothing of it lands in a frame.
        try { await Task.Run(() => CalendarData.Refresh(feeds, force: false, CancellationToken.None)); }
        catch (Exception ex) { App.Log("calendar not loaded: " + ex.Message); }
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!CalendarOnCard) return;
            if (_animating) _calRedraw = true;
            else DrawCalendar();
        });
    }

    /// <summary>The palette is opening: bring the events up to date before a calendar card asks for them.</summary>
    private void PrefetchCalendar()
    {
        if ((_settings.CalendarFeeds.Count > 0 || CalendarData.Sources > 0) && CalendarData.IsStale) _ = LoadCalendar();
    }

    /// <summary>Lay the card out once at start, so its first opening does not do it in the middle of the motion.</summary>
    private void WarmCalendar()
    {
        try
        {
            BuildCalendarCells();
            _calMode = CalMode.Month;
            DrawCalendar();
            CalendarPreview.Visibility = Visibility.Visible;
            CalendarPreview.Measure(new Windows.Foundation.Size(WideDip, CalendarCardDip));
            CalendarPreview.Arrange(new Windows.Foundation.Rect(0, 0, WideDip, CalendarCardDip));
        }
        catch (Exception ex) { App.Log("calendar warm-up skipped: " + ex.Message); }
        finally { CalendarPreview.Visibility = Visibility.Collapsed; }
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
            var ring = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
            ring.Child = new TextBlock { FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            var dots = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) };
            for (int d = 0; d < 3; d++) dots.Children.Add(new Ellipse { Width = 4, Height = 4, Visibility = Visibility.Collapsed });
            var cell = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            cell.Children.Add(ring);
            cell.Children.Add(dots);
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

        if (_calMode == CalMode.Month)
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
                var dots = (StackPanel)cell.Children[1];
                var text = (TextBlock)ring.Child;
                bool selected = date == _calDay.Date, isToday = date == today;
                cell.Tag = date;
                text.Text = date.Day.ToString(culture);
                text.Opacity = date.Month == _calMonth.Month ? 1 : 0.4;
                text.FontWeight = isToday ? FontWeights.SemiBold : FontWeights.Normal;
                // Null is no brush at all in WinUI, not the default: clear it to take the theme's text colour.
                if (selected) text.Foreground = CalOnAccent.Fill;
                else if (isToday) text.Foreground = CalAccent.Fill;
                else text.ClearValue(TextBlock.ForegroundProperty);
                ring.Background = selected ? CalAccent.Fill : null;
                ring.BorderBrush = isToday && !selected ? CalAccent.Fill : null;
                ring.BorderThickness = new Thickness(isToday && !selected ? 1 : 0);
                withEvents.TryGetValue(date, out var titles);
                for (int d = 0; d < 3; d++)
                {
                    var dot = (Ellipse)dots.Children[d];
                    bool on = titles != null && d < titles.Count;
                    dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                    if (on) dot.Fill = ColorOf(titles![d]);
                }
            }
        }

        CalEvents.Children.Clear();
        if (_calMode == CalMode.Week) DrawWeek(today, culture);
        else DrawDay(today, culture);
    }

    private string? CalendarNote(bool loading, bool nothing, DateTime day, DateTime today)
    {
        if (!nothing) return null;
        if (loading) return "Loading…";
        if (_settings.CalendarFeeds.Count == 0 && CalendarData.Sources == 0)
            return "No calendar connected yet. Open “Palette: Settings” and choose Add Google Calendar.";
        if (CalendarData.Problem is { } p) return p + ". Check the address in Palette’s settings.";
        return day < today ? "Nothing was planned" : "Nothing planned";
    }

    private void DrawDay(DateTime today, CultureInfo culture)
    {
        var day = _calDay.Date;
        string when = day.ToString("dddd d MMMM", culture);
        CalDayTitle.Text = day == today ? "Today · " + when
            : day == today.AddDays(1) ? "Tomorrow · " + when
            : day == today.AddDays(-1) ? "Yesterday · " + when : when;

        _calList = CalendarData.On(day);
        if (_calCursor >= _calList.Count) _calCursor = -1;
        if (_calOpen != null && !_calList.Contains(_calOpen)) _calOpen = null;
        bool loading = CalendarData.LoadedAt == DateTime.MinValue;
        if (CalendarNote(loading, _calList.Count == 0, day, today) is { } note)
        {
            CalEvents.Children.Add(new TextBlock { Text = note, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = CalSecondary.Fill });
            return;
        }
        var now = DateTime.Now;
        UIElement? cursored = null;
        for (int i = 0; i < _calList.Count; i++)
        {
            var row = EventRow(_calList[i], i, day, now, culture);
            if (i == _calCursor) cursored = row;
            CalEvents.Children.Add(row);
        }
        if (CalendarData.Problem is { } problem)
            CalEvents.Children.Add(new TextBlock { Text = problem, FontSize = 11, Foreground = CalTertiary.Fill });
        if (cursored is FrameworkElement fe) fe.StartBringIntoView();
    }

    private void DrawWeek(DateTime today, CultureInfo culture)
    {
        var start = _calWeek.Date;
        var end = start.AddDays(6);
        var thisWeek = CalendarData.WeekStart(today);
        string name = start == thisWeek ? "This week" : start == thisWeek.AddDays(7) ? "Next week" : start == thisWeek.AddDays(-7) ? "Last week" : "Week";
        CalDayTitle.Text = $"{name} · {start.ToString("d MMM", culture)} – {end.ToString("d MMM", culture)}";

        bool loading = CalendarData.LoadedAt == DateTime.MinValue;
        var now = DateTime.Now;
        bool any = false;
        for (int d = 0; d < 7; d++)
        {
            var day = start.AddDays(d);
            var events = CalendarData.On(day);
            any |= events.Count > 0;
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = day.ToString("ddd d", culture),
                FontSize = 12,
                FontWeight = day == today ? FontWeights.SemiBold : FontWeights.Normal,
                Foreground = day == today ? CalAccent.Fill : CalSecondary.Fill,
            });
            var lines = new StackPanel { Spacing = 3 };
            if (events.Count == 0)
                lines.Children.Add(new TextBlock { Text = "–", FontSize = 12, Foreground = CalTertiary.Fill });
            foreach (var e in events)
            {
                var line = new Grid { ColumnSpacing = 8 };
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
                line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                line.ColumnDefinitions.Add(new ColumnDefinition());
                line.Children.Add(new Border { Background = ColorOf(e.Title), CornerRadius = new CornerRadius(1.5) });
                bool spans = e.AllDay || e.Start.Date < day;
                var time = new TextBlock { Text = spans ? "All day" : e.Start.ToString("t", culture), FontSize = 12, Foreground = CalSecondary.Fill };
                Grid.SetColumn(time, 1);
                line.Children.Add(time);
                var title = new TextBlock { Text = e.Title, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
                if (e.Location.Length > 0) title.Text += "  ·  " + e.Location;
                Grid.SetColumn(title, 2);
                line.Children.Add(title);
                if (day == now.Date && !e.AllDay && e.End <= now) line.Opacity = 0.5;
                lines.Children.Add(line);
            }
            Grid.SetColumn(lines, 1);
            row.Children.Add(lines);
            CalEvents.Children.Add(row);
        }
        if (!any && CalendarNote(loading, true, start, today) is { } note && (loading || CalendarData.Sources == 0 || CalendarData.Problem != null))
        {
            CalEvents.Children.Clear();
            CalEvents.Children.Add(new TextBlock { Text = note, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = CalSecondary.Fill });
        }
    }

    private UIElement EventRow(CalEvent e, int index, DateTime day, DateTime now, CultureInfo culture)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(58) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(new Border { Background = ColorOf(e.Title), CornerRadius = new CornerRadius(1.5) });

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
        Grid.SetColumn(when, 1);
        row.Children.Add(when);

        var what = new StackPanel();
        what.Children.Add(new TextBlock { Text = e.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
        if (e.Location.Length > 0)
            what.Children.Add(new TextBlock { Text = e.Location, FontSize = 11, Foreground = CalTertiary.Fill, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(what, 2);
        row.Children.Add(what);

        if (day == now.Date && !e.AllDay && e.End <= now) row.Opacity = 0.5;   // already over

        var box = new StackPanel { Spacing = 6 };
        box.Children.Add(row);
        if (ReferenceEquals(e, _calOpen)) box.Children.Add(Details(e, culture));

        var accent = ((SolidColorBrush)CalAccent.Fill).Color;
        var frame = new Border
        {
            Child = box,
            Tag = index,
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(6, 5, 6, 5),
            Background = new SolidColorBrush(index == _calCursor
                ? Windows.UI.Color.FromArgb(0x30, accent.R, accent.G, accent.B)
                : Microsoft.UI.Colors.Transparent),
        };
        frame.Tapped += OnCalEventTapped;
        return frame;
    }

    private static readonly Regex Url = new(@"https?://[^\s<>)""']+", RegexOptions.Compiled);

    /// <summary>What opening an event shows: when, the notes, and a link or a map to open.</summary>
    private UIElement Details(CalEvent e, CultureInfo culture)
    {
        var panel = new StackPanel { Spacing = 4, Margin = new Thickness(71, 0, 0, 2) };
        string span = e.AllDay
            ? (e.End.Date.AddDays(-1) > e.Start.Date ? $"{e.Start.ToString("ddd d MMM", culture)} – {e.End.AddDays(-1).ToString("ddd d MMM", culture)}" : e.Start.ToString("dddd d MMMM", culture))
            : e.Start.Date == e.End.Date || e.End.TimeOfDay == TimeSpan.Zero && e.End.Date == e.Start.Date.AddDays(1)
                ? $"{e.Start.ToString("t", culture)} – {e.End.ToString("t", culture)}  ·  {Describe(e.End - e.Start)}"
                : $"{e.Start.ToString("ddd d MMM t", culture)} – {e.End.ToString("ddd d MMM t", culture)}";
        panel.Children.Add(new TextBlock { Text = span, FontSize = 12, Foreground = CalSecondary.Fill });
        if (e.Location.Length > 0)
            panel.Children.Add(new TextBlock { Text = e.Location, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        if (e.Notes.Length > 0)
            panel.Children.Add(new TextBlock { Text = e.Notes, FontSize = 12, TextWrapping = TextWrapping.Wrap, Foreground = CalSecondary.Fill, IsTextSelectionEnabled = false });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        string? link = Url.Match(e.Notes).Success ? Url.Match(e.Notes).Value : Url.Match(e.Location).Success ? Url.Match(e.Location).Value : null;
        if (link != null) buttons.Children.Add(CalButton("Open link", link));
        if (e.Location.Length > 0 && !Url.IsMatch(e.Location))
            buttons.Children.Add(CalButton("Map", "https://www.google.com/maps/search/?api=1&query=" + Uri.EscapeDataString(e.Location)));
        if (buttons.Children.Count > 0) panel.Children.Add(buttons);
        return panel;
    }

    private Button CalButton(string text, string url)
    {
        var b = new Button { Content = text, FontSize = 12, Tag = url };
        if (Root.Resources.TryGetValue("CardTextButtonStyle", out var style) && style is Style s) b.Style = s;
        b.Click += (_, _) =>
        {
            OpenUrl(url);
            Input.Focus(FocusState.Programmatic);
        };
        return b;
    }

    private static readonly Windows.UI.Color[] EventColors =
    {
        Windows.UI.Color.FromArgb(255, 0x4C, 0x9B, 0xF5),   // blue
        Windows.UI.Color.FromArgb(255, 0xE5, 0x6B, 0x6F),   // red
        Windows.UI.Color.FromArgb(255, 0x4F, 0xC0, 0x8D),   // green
        Windows.UI.Color.FromArgb(255, 0xF2, 0xA9, 0x3B),   // amber
        Windows.UI.Color.FromArgb(255, 0xA9, 0x7B, 0xE8),   // violet
        Windows.UI.Color.FromArgb(255, 0x3F, 0xC1, 0xC9),   // teal
        Windows.UI.Color.FromArgb(255, 0xE8, 0x7C, 0xB5),   // pink
        Windows.UI.Color.FromArgb(255, 0x9A, 0xB8, 0x4A),   // olive
    };

    /// <summary>A colour for an event by its title, so a course or a meeting keeps its colour day after day.</summary>
    private static SolidColorBrush ColorOf(string title)
    {
        uint h = 2166136261;
        foreach (char c in title.Trim().ToLowerInvariant()) h = (h ^ c) * 16777619;
        return new SolidColorBrush(EventColors[h % (uint)EventColors.Length]);
    }

    // ------------------------------------------------------------ keys and taps

    /// <summary>Keys the card takes while it is in front; true when it did.</summary>
    private bool CalendarKey(VirtualKey key)
    {
        if (!CalendarOnCard || ResultsList.SelectedIndex > 0) return false;
        switch (key)
        {
            case VirtualKey.Down when _calMode != CalMode.Week && _calCursor < _calList.Count - 1:
                _calCursor++;
                DrawCalendar();
                return true;
            case VirtualKey.Up when _calMode != CalMode.Week && _calCursor >= 0:
                _calCursor--;
                if (_calCursor < 0) _calOpen = null;
                DrawCalendar();
                return true;
            case VirtualKey.Enter when _calMode != CalMode.Week && _calCursor >= 0 && _calCursor < _calList.Count && !Held(VirtualKey.Control):
                ToggleDetails(_calList[_calCursor]);
                return true;
            case VirtualKey.PageDown:
                MoveCalendar(+1);
                return true;
            case VirtualKey.PageUp:
                MoveCalendar(-1);
                return true;
        }
        return false;
    }

    private void ToggleDetails(CalEvent e)
    {
        _calOpen = ReferenceEquals(_calOpen, e) ? null : e;
        DrawCalendar();
    }

    /// <summary>Earlier or later: a month on the month card, a day on a day's, a week on a week's.</summary>
    private void MoveCalendar(int by)
    {
        switch (_calMode)
        {
            case CalMode.Month:
                MoveCalendarMonth(by);
                return;
            case CalMode.Week:
                _calWeek = _calWeek.AddDays(7 * by);
                break;
            default:
                _calDay = _calDay.AddDays(by);
                break;
        }
        _calCursor = -1;
        _calOpen = null;
        DrawCalendar();
    }

    private void OnCalEventTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is int i && i >= 0 && i < _calList.Count)
        {
            _calCursor = i;
            ToggleDetails(_calList[i]);
        }
        Input.Focus(FocusState.Programmatic);
    }

    private void OnCalCellTapped(object sender, TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is DateTime date)
        {
            _calDay = date;
            _calMonth = new DateTime(date.Year, date.Month, 1);
            _calCursor = -1;
            _calOpen = null;
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
        _calCursor = -1;
        _calOpen = null;
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
