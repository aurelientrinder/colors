using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Palette;

/// <summary>
/// What the chat's tools found, as cards inside the answer: files to click (open; right-click shows it in
/// its folder), the weather, a day's events. Built once per block; nothing in them changes afterwards.
/// </summary>
public sealed partial class MainWindow
{
    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>The card for a tool's block.</summary>
    private FrameworkElement MakeBlockView(object block) => block switch
    {
        FilesBlock f => FilesCard(f),
        WeatherBlock w => WeatherCard(w.Weather),
        EventsBlock e => EventsCard(e),
        _ => new Border(),
    };

    private static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = Res("CardBackgroundFillColorDefaultBrush"),
        BorderBrush = Res("CardStrokeColorDefaultBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = padding ?? new Thickness(4),
        Margin = new Thickness(0, 4, 0, 2),
        Child = child,
    };

    /// <summary>A row that lights up under the pointer and does something when clicked.</summary>
    private static Grid Hoverable(Action click, Action? rightClick = null)
    {
        var g = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6, 8, 6) };
        g.PointerEntered += (_, _) => g.Background = Res("SubtleFillColorSecondaryBrush");
        g.PointerExited += (_, _) => g.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        g.Tapped += (_, e) => { e.Handled = true; click(); };
        if (rightClick != null) g.RightTapped += (_, e) => { e.Handled = true; rightClick(); };
        return g;
    }

    // ------------------------------------------------------------------ files

    private FrameworkElement FilesCard(FilesBlock block)
    {
        var rows = new StackPanel { Spacing = 0 };
        foreach (var f in block.Files)
        {
            var row = Hoverable(() => OpenFromChat(f.Path, reveal: false), () => OpenFromChat(f.Path, reveal: true));
            row.ColumnSpacing = 10;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ToolTipService.SetToolTip(row, f.Path + "\nClick to open · right-click to show in its folder");

            var icon = new Image { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center };
            _ = FileIconInto(icon, f.Path);
            row.Children.Add(icon);

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = f.Name, FontSize = 13, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            text.Children.Add(new TextBlock
            {
                Text = $"{ShortFolder(f.Folder)} · {FileWhen(f)}", FontSize = 11, Foreground = Res("TextFillColorTertiaryBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            Grid.SetColumn(text, 1);
            row.Children.Add(text);

            var go = new FontIcon { Glyph = "\uE8A7", FontSize = 12, Foreground = Res("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(go, 2);
            row.Children.Add(go);
            rows.Children.Add(row);
        }
        return Card(rows);
    }

    /// <summary>The file type's icon, from the same cache as the result rows.</summary>
    private async Task FileIconInto(Image image, string path)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        string key = ext.Length > 0 ? "ext:" + ext : "ext:txt";
        if (!_iconCache.TryGetValue(key, out var bmp))
        {
            byte[]? px = await Task.Run(() => Native.Icon(key));
            if (px == null) return;
            bmp = _iconCache.TryGetValue(key, out var again) ? again : _iconCache[key] = MakeBitmap(px);
        }
        image.Source = bmp;
    }

    /// <summary>"C:\Users\me\Documents\Work\Q3" as "Documents › Work › Q3".</summary>
    private static string ShortFolder(string folder)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (folder.StartsWith(home, StringComparison.OrdinalIgnoreCase)) folder = folder[home.Length..].TrimStart('\\');
        var parts = folder.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 3) parts = new[] { "…" }.Concat(parts[^3..]).ToArray();
        return parts.Length == 0 ? folder : string.Join(" › ", parts);
    }

    /// <summary>"opened 2 days ago", or "changed Tuesday" when it was not opened.</summary>
    private static string FileWhen(Native.FileFound f) =>
        f.Opened > 0 && f.Opened >= f.Modified - 60 ? "opened " + Ago(f.Opened) : "changed " + Ago(f.Modified);

    private static string Ago(long unix)
    {
        var t = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;
        var span = DateTime.Now - t;
        if (span.TotalMinutes < 1) return "just now";
        if (span.TotalHours < 1) return $"{(int)span.TotalMinutes} min ago";
        if (t.Date == DateTime.Today) return "today, " + t.ToString("t", CultureInfo.CurrentCulture);
        if (t.Date == DateTime.Today.AddDays(-1)) return "yesterday";
        if (span.TotalDays < 7) return t.ToString("dddd", CultureInfo.CurrentCulture);
        return t.ToString(t.Year == DateTime.Today.Year ? "d MMM" : "d MMM yyyy", CultureInfo.CurrentCulture);
    }

    /// <summary>A file clicked in the chat: open it (or show it in its folder), and get out of the way.</summary>
    private void OpenFromChat(string path, bool reveal)
    {
        try
        {
            if (reveal) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            HidePalette(restoreFocus: false);
        }
        catch (Exception ex)
        {
            App.Log($"could not open {path}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ weather

    private FrameworkElement WeatherCard(WebAnswers.Weather w)
    {
        string T(double c) => $"{Math.Round(WebAnswers.ToUnit(c, _fahrenheit)):0}°";
        var g = Hoverable(() =>
        {
            if (!WebAnswers.HasWeatherApp) return;
            try
            {
                Process.Start(new ProcessStartInfo(WebAnswers.WeatherAppUrl(w)) { UseShellExecute = true });
                HidePalette(restoreFocus: false);
            }
            catch (Exception ex) { App.Log("weather app: " + ex.Message); }
        });
        g.Padding = new Thickness(12, 8, 14, 8);
        g.ColumnSpacing = 12;
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var icon = new TextBlock { Text = w.Icon, FontSize = 30, VerticalAlignment = VerticalAlignment.Center };
        g.Children.Add(icon);
        var temp = new TextBlock { Text = T(w.TempC), FontSize = 30, FontWeight = FontWeights.SemiLight, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(temp, 1);
        g.Children.Add(temp);
        var info = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Spacing = 1 };
        info.Children.Add(new TextBlock { Text = w.Condition, FontSize = 13, FontWeight = FontWeights.SemiBold });
        info.Children.Add(new TextBlock
        {
            Text = $"{w.Place} · H {T(w.HighC)}  L {T(w.LowC)}", FontSize = 11, Foreground = Res("TextFillColorTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(info, 2);
        g.Children.Add(info);
        if (WebAnswers.HasWeatherApp) ToolTipService.SetToolTip(g, "Open in Weather");
        return Card(g, new Thickness(2));
    }

    // ------------------------------------------------------------------ calendar

    private static FrameworkElement EventsCard(EventsBlock block)
    {
        var list = new StackPanel { Spacing = 6, Padding = new Thickness(10, 8, 10, 8) };
        list.Children.Add(new TextBlock
        {
            Text = block.Title.ToUpper(CultureInfo.CurrentCulture), FontSize = 10, FontWeight = FontWeights.SemiBold,
            CharacterSpacing = 60, Foreground = Res("TextFillColorTertiaryBrush"),
        });
        if (block.Events.Count == 0)
            list.Children.Add(new TextBlock { Text = "Nothing on the calendar.", FontSize = 13, Foreground = Res("TextFillColorSecondaryBrush") });
        bool manyDays = block.Events.Select(e => e.Start.Date).Distinct().Count() > 1;
        foreach (var e in block.Events)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(manyDays ? 92 : 48) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            string day = manyDays ? e.Start.ToString("ddd d", CultureInfo.CurrentCulture) + " " : "";
            row.Children.Add(new TextBlock
            {
                Text = day + (e.AllDay ? "all day" : e.Start.ToString("t", CultureInfo.CurrentCulture)), FontSize = 12,
                Foreground = Res("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0),
            });
            var what = new StackPanel();
            what.Children.Add(new TextBlock { Text = e.Title, FontSize = 13, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (e.Location.Length > 0)
                what.Children.Add(new TextBlock { Text = e.Location, FontSize = 11, Foreground = Res("TextFillColorTertiaryBrush"), TextTrimming = TextTrimming.CharacterEllipsis });
            Grid.SetColumn(what, 1);
            row.Children.Add(what);
            list.Children.Add(row);
        }
        return Card(list, new Thickness(0));
    }
}
