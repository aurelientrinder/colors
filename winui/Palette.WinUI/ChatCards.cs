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
        SourcesBlock d => SourcesCard(d),
        SystemBlock y => SystemCard(y.Reading),
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
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            ToolTipService.SetToolTip(row, f.Path + "\nClick to open · right-click to show in its folder");

            var icon = new Image { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Center, Stretch = Stretch.UniformToFill };
            var frame = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(4), Child = icon };
            _ = FileIconInto(icon, f.Path);
            _ = ThumbnailInto(icon, frame, f.Path);
            row.Children.Add(frame);

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

    /// <summary>Files whose picture says more than their type's icon.</summary>
    private static readonly HashSet<string> Thumbed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".heic", ".bmp", ".tif", ".tiff",
        ".mp4", ".mov", ".mkv", ".avi", ".webm", ".pdf", ".docx", ".pptx", ".xlsx",
    };

    /// <summary>The file's own picture (a photo, a PDF's first page) from the shell, over the icon when there is one.</summary>
    private static async Task ThumbnailInto(Image image, Border frame, string path)
    {
        if (!Thumbed.Contains(Path.GetExtension(path))) return;
        try
        {
            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem, 64,
                Windows.Storage.FileProperties.ThumbnailOptions.ResizeThumbnail);
            // Without a picture of its own the shell hands back the type's icon: keep ours then.
            if (thumb == null || thumb.Type != Windows.Storage.FileProperties.ThumbnailType.Image) return;
            var bmp = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
            await bmp.SetSourceAsync(thumb);
            image.Width = image.Height = 32;
            image.Source = bmp;
            frame.BorderBrush = Res("CardStrokeColorDefaultBrush");
            frame.BorderThickness = new Thickness(1);
        }
        catch (Exception ex)
        {
            App.Log($"no thumbnail for {path}: {ex.Message}");
        }
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

    // ------------------------------------------------------------------ documents behind an answer

    private FrameworkElement SourcesCard(SourcesBlock block)
    {
        var rows = new StackPanel();
        for (int i = 0; i < block.Docs.Count; i++)
        {
            var d = block.Docs[i];
            var row = Hoverable(() => OpenFromChat(d.Path, reveal: false), () => OpenFromChat(d.Path, reveal: true));
            row.Padding = new Thickness(8, 4, 8, 4);
            row.ColumnSpacing = 8;
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            ToolTipService.SetToolTip(row, d.Path + "\nClick to open · right-click to show in its folder");
            var badge = new Border
            {
                Background = Res("AccentFillColorDefaultBrush"), CornerRadius = new CornerRadius(4), Padding = new Thickness(5, 0, 5, 1),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), FontSize = 11, FontWeight = FontWeights.SemiBold, Foreground = Res("TextOnAccentFillColorPrimaryBrush") },
            };
            row.Children.Add(badge);
            var icon = new Image { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
            _ = FileIconInto(icon, d.Path);
            Grid.SetColumn(icon, 1);
            row.Children.Add(icon);
            var name = new TextBlock { Text = d.Name, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(name, 2);
            row.Children.Add(name);
            rows.Children.Add(row);
        }
        return Card(rows, new Thickness(2));
    }

    // ------------------------------------------------------------------ the PC

    private static FrameworkElement SystemCard(SystemReading r)
    {
        var g = new Grid { ColumnSpacing = 14, Padding = new Thickness(12, 10, 12, 10) };
        var gauges = new List<(string label, string value, double fraction, bool warn)>
        {
            ("Processor", $"{r.Cpu:0}%", r.Cpu / 100, r.Cpu > 85),
            ("Memory", string.Format(CultureInfo.CurrentCulture, "{0:0.0}/{1:0} GB", r.MemUsedGb, r.MemTotalGb),
                r.MemTotalGb > 0 ? r.MemUsedGb / r.MemTotalGb : 0, r.MemTotalGb > 0 && r.MemUsedGb / r.MemTotalGb > 0.9),
        };
        if (r.Battery >= 0) gauges.Add(("Battery" + (r.Charging ? " ⚡" : ""), $"{r.Battery}%", r.Battery / 100.0, r.Battery < 20 && !r.Charging));
        if (r.DiskTotalGb > 0)
            gauges.Add(("Disk C:", string.Format(CultureInfo.CurrentCulture, "{0:0} GB free", r.DiskFreeGb), 1 - r.DiskFreeGb / r.DiskTotalGb, r.DiskFreeGb / r.DiskTotalGb < 0.1));
        for (int i = 0; i < gauges.Count; i++)
        {
            var (label, value, fraction, warn) = gauges[i];
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var cell = new StackPanel { Spacing = 3 };
            cell.Children.Add(new TextBlock { Text = label, FontSize = 11, Foreground = Res("TextFillColorTertiaryBrush") });
            cell.Children.Add(new TextBlock { Text = value, FontSize = 14, FontWeight = FontWeights.SemiBold });
            double f = Math.Clamp(fraction, 0, 1);
            var bar = new Grid { Height = 4, CornerRadius = new CornerRadius(2), Background = Res("ControlStrongFillColorDisabledBrush") };
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(f, 0.001), GridUnitType.Star) });
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(1 - f, 0.001), GridUnitType.Star) });
            bar.Children.Add(new Border
            {
                CornerRadius = new CornerRadius(2),
                Background = Res(warn ? "SystemFillColorCriticalBrush" : "AccentFillColorDefaultBrush"),
            });
            cell.Children.Add(bar);
            Grid.SetColumn(cell, i);
            g.Children.Add(cell);
        }
        return Card(g, new Thickness(0));
    }
}
