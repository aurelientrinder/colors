using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Palette;

/// <summary>
/// A small brain, drawn as strokes, that pulses and sways while the model thinks and sits still once
/// it has finished. The folds light up one after another, as if something were going on inside.
/// </summary>
internal sealed class BrainIcon : Grid
{
    private const string Ns = "xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'";
    // Two hemispheres meeting at the middle, and the folds inside each.
    private static readonly string[] Outline =
    {
        "M12,5 C11,3.5 8.5,3.5 7.5,5 C5.5,5 4.5,7 5,8.5 C3.5,9.5 3.5,12 5,13 C4.5,15 6,17 8,16.8 C8.5,18.5 11,19 12,17.5 Z",
        "M12,5 C13,3.5 15.5,3.5 16.5,5 C18.5,5 19.5,7 19,8.5 C20.5,9.5 20.5,12 19,13 C19.5,15 18,17 16,16.8 C15.5,18.5 13,19 12,17.5 Z",
    };
    private static readonly string[] Folds =
    {
        "M8,9 C9.5,9 10.5,10 10.5,11.5",
        "M16,9 C14.5,9 13.5,10 13.5,11.5",
        "M8,13 L10,13",
        "M16,13 L14,13",
    };

    private readonly Canvas _canvas = new() { Width = 24, Height = 24 };
    private readonly List<Path> _folds = new();
    private readonly List<Path> _all = new();
    private readonly ScaleTransform _scale = new() { CenterX = 12, CenterY = 12 };
    private readonly RotateTransform _rotate = new() { CenterX = 12, CenterY = 14 };
    private Storyboard? _going;

    public BrainIcon(double size = 18)
    {
        Width = Height = size;
        Children.Add(new Viewbox { Child = _canvas });
        _canvas.RenderTransform = new TransformGroup { Children = { _scale, _rotate } };
        foreach (var d in Outline) Add(d, fold: false);
        Add("M12,5 L12,17.5", fold: false);
        foreach (var d in Folds) Add(d, fold: true);
        Tone(false);
    }

    private void Add(string data, bool fold)
    {
        var p = (Path)XamlReader.Load($"<Path {Ns} Data='{data}' StrokeThickness='1.5' StrokeStartLineCap='Round' StrokeEndLineCap='Round' StrokeLineJoin='Round'/>");
        _canvas.Children.Add(p);
        _all.Add(p);
        if (fold) _folds.Add(p);
    }

    /// <summary>Accent while thinking, quiet once done.</summary>
    private void Tone(bool active)
    {
        var brush = (Brush)Application.Current.Resources[active ? "AccentTextFillColorPrimaryBrush" : "TextFillColorTertiaryBrush"];
        foreach (var p in _all) p.Stroke = brush;
    }

    public void Start()
    {
        if (_going != null) return;
        Tone(true);
        var sb = new Storyboard();
        void Animate(DependencyObject target, string prop, double from, double to, double ms, double beginMs = 0)
        {
            var a = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = TimeSpan.FromMilliseconds(ms),
                BeginTime = TimeSpan.FromMilliseconds(beginMs),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            };
            Storyboard.SetTarget(a, target);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        Animate(_scale, "ScaleX", 0.94, 1.1, 650);
        Animate(_scale, "ScaleY", 0.94, 1.1, 650);
        Animate(_rotate, "Angle", -6, 6, 1100);
        for (int i = 0; i < _folds.Count; i++) Animate(_folds[i], "Opacity", 0.15, 1, 420, i * 180);
        _going = sb;
        sb.Begin();
    }

    public void Stop()
    {
        if (_going == null) return;
        _going.Stop();
        _going = null;
        _scale.ScaleX = _scale.ScaleY = 1;
        _rotate.Angle = 0;
        foreach (var f in _folds) f.Opacity = 1;
        Tone(false);
    }
}

/// <summary>Three dots that rise and fall in turn, after the word "Thinking".</summary>
internal sealed class WaitingDots : StackPanel
{
    private Storyboard? _going;

    public WaitingDots()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 3;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(2, 0, 0, 5);
        for (int i = 0; i < 3; i++)
            Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
            {
                Width = 3, Height = 3, Opacity = 0.25,
                Fill = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            });
    }

    public void Start()
    {
        if (_going != null) return;
        var sb = new Storyboard();
        for (int i = 0; i < Children.Count; i++)
        {
            var a = new DoubleAnimation
            {
                From = 0.25, To = 1, Duration = TimeSpan.FromMilliseconds(380), BeginTime = TimeSpan.FromMilliseconds(i * 160),
                AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            };
            Storyboard.SetTarget(a, Children[i]);
            Storyboard.SetTargetProperty(a, "Opacity");
            sb.Children.Add(a);
        }
        _going = sb;
        sb.Begin();
    }

    public void Stop()
    {
        _going?.Stop();
        _going = null;
    }
}

/// <summary>
/// An assistant message: a line for the model's thinking (the brain, then "Thought for 6 s", which opens
/// to the reasoning), the answer, and small print under it.
/// </summary>
internal sealed class AssistantView : StackPanel
{
    private readonly Grid _think = new() { Visibility = Visibility.Collapsed };
    private readonly BrainIcon _brain = new();
    private readonly TextBlock _label = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly WaitingDots _dots = new() { Visibility = Visibility.Collapsed };
    private readonly FontIcon _chevron = new() { Glyph = "", FontSize = 9, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _reasoning = new()
    {
        FontSize = 12, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed,
        Margin = new Thickness(24, 0, 0, 2), LineHeight = 17,
    };
    private readonly TextBlock _body = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 21, IsTextSelectionEnabled = true };
    private readonly TextBlock _note = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private bool _open;

    public AssistantView(Action onResize)
    {
        Spacing = 3;
        var dim = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        var faint = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        _label.Foreground = dim;
        _chevron.Foreground = faint;
        _reasoning.Foreground = faint;
        _note.Foreground = faint;

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        row.Children.Add(_brain);
        row.Children.Add(_label);
        row.Children.Add(_dots);
        row.Children.Add(_chevron);
        _think.Children.Add(row);
        // A tap on the line opens the reasoning (once there is some).
        _think.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _think.Tapped += (_, _) =>
        {
            if (_reasoning.Text.Length == 0) return;
            _open = !_open;
            _reasoning.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Glyph = _open ? "" : "";
            onResize();
        };

        Children.Add(_think);
        Children.Add(_reasoning);
        Children.Add(_body);
        Children.Add(_note);
    }

    public void Update(ChatMsg m, bool writing)
    {
        bool answering = m.Text.Length > 0;
        bool thinkingNow = writing && !answering;
        bool hasThoughts = m.Thinking.Length > 0;

        // The thinking line: animated while the model has not begun to answer, then a summary.
        _think.Visibility = thinkingNow || hasThoughts ? Visibility.Visible : Visibility.Collapsed;
        if (thinkingNow)
        {
            // The brain for thinking or searching; a message answered straight away just gets the dots.
            bool busy = m.Think || hasThoughts || m.Hint.Length > 0;
            _brain.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            if (busy) _brain.Start();
            _dots.Visibility = Visibility.Visible;
            _dots.Start();
            _label.Text = m.Hint.Length > 0 ? m.Hint.TrimEnd('…', '.') : busy ? "Thinking" : "";
            _chevron.Visibility = hasThoughts ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            _brain.Stop();
            _brain.Visibility = Visibility.Visible;
            _dots.Stop();
            _dots.Visibility = Visibility.Collapsed;
            if (hasThoughts)
            {
                double s = m.ThinkMs / 1000.0;
                _label.Text = s < 1 ? "Thought for a moment" : $"Thought for {s:0} s";
                _chevron.Visibility = Visibility.Visible;
            }
        }
        _reasoning.Text = m.Thinking.Trim();
        if (!hasThoughts && _open)
        {
            _open = false;
            _reasoning.Visibility = Visibility.Collapsed;
        }

        _body.Visibility = answering ? Visibility.Visible : Visibility.Collapsed;
        if (answering) ChatMarkdown.Fill(_body, m.Text);
        else if (!thinkingNow && !writing) _body.Visibility = Visibility.Collapsed;
        _note.Text = m.Note;
        _note.Visibility = m.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>The model's markdown (bold, italics, code, headings, bullets) as the runs of a text block.</summary>
internal static class ChatMarkdown
{
    private static readonly Regex Inline = new(@"\*\*(?<b>.+?)\*\*|__(?<b>.+?)__|`(?<c>[^`]+)`|(?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]\n]+)\]\((?:https?://|file:)[^)\s]+\)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static void Fill(TextBlock tb, string md)
    {
        tb.Inlines.Clear();
        md = Link.Replace(md.Replace("\r", "").Trim(), "$1");
        var lines = md.Split('\n');
        for (int n = 0; n < lines.Length; n++)
        {
            string line = lines[n];
            bool heading = false;
            var h = Regex.Match(line, @"^\s{0,3}#{1,6}\s*");
            if (h.Success)
            {
                heading = true;
                line = line[h.Length..];
            }
            line = Regex.Replace(line, @"^(\s*)[*-]\s+", "$1• ");
            int at = 0;
            foreach (Match m in Inline.Matches(line))
            {
                if (m.Index > at) tb.Inlines.Add(Plain(line[at..m.Index], heading));
                if (m.Groups["b"].Success) tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = m.Groups["b"].Value, FontWeight = FontWeights.SemiBold });
                else if (m.Groups["c"].Success)
                    tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
                    {
                        Text = m.Groups["c"].Value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = tb.FontSize - 1,
                        Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                    });
                else tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = m.Groups["i"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic });
                at = m.Index + m.Length;
            }
            if (at < line.Length) tb.Inlines.Add(Plain(line[at..], heading));
            if (n < lines.Length - 1) tb.Inlines.Add(new Microsoft.UI.Xaml.Documents.LineBreak());
        }
    }

    private static Microsoft.UI.Xaml.Documents.Run Plain(string s, bool heading) =>
        new() { Text = s, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
}
