using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
/// to the reasoning), the answer, the cards of what its tools found (weather, calendar, the PC), and small
/// print under it. Files and documents the answer names are links inside its text, not cards.
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
    private readonly RichTextBlock _body = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22, IsTextSelectionEnabled = true };
    private readonly Paragraph _para = new();
    private readonly TextBlock _note = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly StackPanel _blocks = new() { Spacing = 4 };
    private readonly Func<object, FrameworkElement?> _makeBlock;
    private readonly Func<string, string, FrameworkElement> _makeLink;
    private int _blocksSeen;
    private bool _open;

    private readonly Action _onResize;

    public AssistantView(Action onResize, Func<object, FrameworkElement?> makeBlock, Func<string, string, FrameworkElement> makeLink)
    {
        _onResize = onResize;
        _makeBlock = makeBlock;
        _makeLink = makeLink;
        Spacing = 3;
        _body.Blocks.Add(_para);
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
        Children.Add(_timeline);
        Children.Add(_tail);
        Children.Add(_body);
        Children.Add(_blocks);
        Children.Add(_note);
    }

    // ------------------------------------------------------------------ the timeline: text, steps, text…

    /// <summary>What the model wrote and did before its last turn: each round's text, then a line per tool it used.</summary>
    private readonly StackPanel _timeline = new() { Spacing = 4 };
    /// <summary>Dots while the model goes on after its tools, before its next words.</summary>
    private readonly WaitingDots _tail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(2, 2, 0, 2), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly List<(ToolCall call, ToolStep view)> _steps = new();
    private readonly List<(ToolRound round, StackPanel calls)> _rounds = new();

    /// <summary>Draw the rounds of tools not drawn yet, and bring the steps up to date (running, then done).</summary>
    private void SyncTimeline(ChatMsg m)
    {
        for (int i = _rounds.Count; i < m.Rounds.Count; i++)
        {
            var round = m.Rounds[i];
            if (round.Content.Trim().Length > 0)
            {
                var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22, IsTextSelectionEnabled = true };
                var para = new Paragraph();
                text.Blocks.Add(para);
                ChatMarkdown.Fill(para.Inlines, text.FontSize, round.Content.Trim(), _links, (l, shown, k) => LinkInline(l, shown, 1000 * (i + 1) + k));
                _timeline.Children.Add(text);
            }
            var calls = new StackPanel { Spacing = 2, Margin = new Thickness(0, 1, 0, 1) };
            _timeline.Children.Add(calls);
            _rounds.Add((round, calls));
            // The text of the turn that called these is now above them: the next words start afresh.
            _text = "";
            _drawn = 0;
            _marks.Clear();
        }
        foreach (var (round, calls) in _rounds)
        {
            foreach (var call in round.Calls)
            {
                if (_steps.Any(s => ReferenceEquals(s.call, call))) continue;
                var view = new ToolStep(call, _onResize);
                calls.Children.Add(view);
                _steps.Add((call, view));
            }
        }
        foreach (var (call, view) in _steps) view.Update(call);
    }

    public void Update(ChatMsg m, bool writing)
    {
        // What the tools found: files and documents become links in the text; the rest, cards under it.
        bool relink = false;
        for (; _blocksSeen < m.Blocks.Count; _blocksSeen++)
        {
            if (_makeBlock(m.Blocks[_blocksSeen]) is { } card) _blocks.Children.Add(card);
            relink = true;
        }
        if (relink) _links = ChatMarkdown.LinksOf(m.Blocks);
        SyncTimeline(m);

        string reply = m.Text[Math.Min(m.ReplyStart, m.Text.Length)..];
        bool stepRunning = m.Rounds.Any(r => r.Calls.Any(c => !c.Done));
        bool answering = reply.Trim().Length > 0;
        bool thinkingNow = writing && m.Text.Trim().Length == 0 && m.Rounds.Count == 0;
        bool hasThoughts = m.Thinking.Length > 0;
        // After its tools, the model's next words take a moment: dots meanwhile.
        bool waiting = writing && m.Rounds.Count > 0 && !stepRunning && !answering;
        _tail.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        if (waiting) _tail.Start();
        else _tail.Stop();

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
        if (answering && (reply != _text || relink))
        {
            if (writing && reply != _text) _fresh = true;
            _text = reply;
            DrawBody();
        }
        _note.Text = m.Note;
        _note.Visibility = m.Note.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ links to files in the text

    private IReadOnlyList<LinkToken> _links = Array.Empty<LinkToken>();
    private readonly Dictionary<string, InlineUIContainer> _linkViews = new();

    /// <summary>The link for the <paramref name="occurrence"/>th mention of a file, made once and reused at every draw.</summary>
    private Inline LinkInline(LinkToken link, string shown, int occurrence)
    {
        string key = $"{link.Path}|{shown}|{occurrence}";
        if (!_linkViews.TryGetValue(key, out var c))
        {
            var view = _makeLink(link.Path, shown);
            view.Tag = shown.Length;
            _linkViews[key] = c = new InlineUIContainer { Child = view };
        }
        c.Child.Opacity = 1;
        return c;
    }

    // ------------------------------------------------------------------ the answer fading in as it is written

    /// <summary>How long a piece of new text takes to come up from clear to solid.</summary>
    private const double FadeMs = 420;
    /// <summary>The longest a chunk's sweep spreads back in time (chunks come every 60 ms or so).</summary>
    private const double SweepMs = 160;

    private string _text = "";
    private bool _fresh;                                    // new text since the last draw, while writing
    private readonly List<(int len, long at)> _marks = new();   // drawn length of the answer when each chunk came
    private bool _ticking;
    private int _drawn;                                     // the answer's drawn length at the last draw

    /// <summary>Characters an inline stands for: a run's text, a link's name.</summary>
    private static int LengthOf(Inline i) => i switch
    {
        Run r => r.Text.Length,
        InlineUIContainer { Child: FrameworkElement { Tag: int n } } => n,
        _ => 0,
    };

    /// <summary>
    /// The answer, with the newest text fading in: each chunk sweeps from its first character to its
    /// last over the time since the chunk before (so the words come up like a breath across the line,
    /// not one by one), and each character eases from clear to solid over FadeMs.
    /// </summary>
    private void DrawBody()
    {
        ChatMarkdown.Fill(_para.Inlines, _body.FontSize, _text, _links, LinkInline);
        long now = Environment.TickCount64;
        int total = 0;
        foreach (var i in _para.Inlines) total += LengthOf(i);
        if (_fresh)
        {
            _fresh = false;
            // The first chunk after a pause sweeps from where the text stood.
            if (_marks.Count == 0 && total > _drawn) _marks.Add((_drawn, now - (long)SweepMs));
            if (_marks.Count > 0 && total > _marks[^1].len) _marks.Add((total, now));
        }
        _drawn = total;
        // Marks whose chunk is solid already are of no more use (the one before the oldest fading chunk is
        // where its sweep starts).
        while (_marks.Count >= 2 && now - _marks[1].at > FadeMs + SweepMs) _marks.RemoveAt(0);
        if (_marks.Count == 0 || now - _marks[^1].at > FadeMs + SweepMs)
        {
            _marks.Clear();
            return;
        }
        Fade(now);
        if (!_ticking)
        {
            _ticking = true;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    private void OnFrame(object? sender, object e)
    {
        if (_marks.Count == 0)
        {
            _ticking = false;
            CompositionTarget.Rendering -= OnFrame;
            return;
        }
        DrawBody();
    }

    /// <summary>How solid the character at drawn position <paramref name="p"/> is now, 0 to 1.</summary>
    private double Alpha(int p, long now)
    {
        if (p < _marks[0].len) return 1;
        for (int i = 1; i < _marks.Count; i++)
        {
            var (len, at) = _marks[i];
            if (p >= len) continue;
            var (prevLen, prevAt) = _marks[i - 1];
            double start = Math.Max(prevAt, at - SweepMs);
            double appear = start + (at - start) * (p - prevLen + 1) / Math.Max(1, len - prevLen);
            double x = Math.Clamp((now - appear) / FadeMs, 0, 1);
            return 1 - (1 - x) * (1 - x) * (1 - x);   // eases out: quick to show, gentle to settle
        }
        return 1;
    }

    /// <summary>Split the runs from the oldest fading character on into small pieces, each with its own opacity.</summary>
    private void Fade(long now)
    {
        // The first mark is the length before the oldest fading chunk: everything before it is solid.
        int from = _marks[0].len;
        var inlines = _para.Inlines.ToList();
        _para.Inlines.Clear();
        var solid = ((SolidColorBrush)Application.Current.Resources["TextFillColorPrimaryBrush"]).Color;
        int pos = 0;
        foreach (var inline in inlines)
        {
            int len = LengthOf(inline);
            if (inline is InlineUIContainer link)
            {
                // A link comes up whole, as its first character would.
                link.Child.Opacity = pos + len <= from ? 1 : Alpha(pos, now);
                _para.Inlines.Add(link);
                pos += len;
                continue;
            }
            if (inline is not Run run || pos + len <= from)
            {
                pos += len;
                _para.Inlines.Add(inline);
                continue;
            }
            var color = (run.Foreground as SolidColorBrush)?.Color ?? solid;
            string t = run.Text;
            int at = 0;
            if (from > pos)
            {
                _para.Inlines.Add(Piece(run, t[..(from - pos)], null));
                at = from - pos;
            }
            // Pieces of three characters: fine enough for a smooth sweep, few enough to stay cheap.
            while (at < t.Length)
            {
                int n = Math.Min(3, t.Length - at);
                double a = Alpha(pos + at + n - 1, now);
                var c = color;
                c.A = (byte)Math.Round(color.A * a);
                _para.Inlines.Add(Piece(run, t.Substring(at, n), a >= 1 ? null : new SolidColorBrush(c)));
                at += n;
            }
            pos += t.Length;
        }
    }

    private static Run Piece(Run like, string text, Brush? brush)
    {
        var r = new Run
        {
            Text = text, FontWeight = like.FontWeight, FontStyle = like.FontStyle, FontSize = like.FontSize,
            FontFamily = like.FontFamily,
        };
        if (brush != null) r.Foreground = brush;
        else if (like.ReadLocalValue(TextElement.ForegroundProperty) != DependencyProperty.UnsetValue)
            r.Foreground = like.Foreground;
        return r;
    }
}

/// <summary>
/// One thing the model did, as a quiet line in its answer: while it runs, the brain at work and what it is
/// doing ("Looking through your files from last week"); once done, the tool's icon and what came of it
/// ("Found 6 files"). A tap opens what was asked and what was found.
/// </summary>
internal sealed class ToolStep : StackPanel
{
    private readonly BrainIcon _brain = new(13) { VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _icon = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _label = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly WaitingDots _dots = new();
    private readonly FontIcon _chevron = new() { Glyph = "", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
    private readonly TextBlock _detail = new()
    {
        FontSize = 11, TextWrapping = TextWrapping.Wrap, MaxLines = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        Margin = new Thickness(21, 2, 0, 2), Visibility = Visibility.Collapsed,
    };
    private bool _done, _open;

    public ToolStep(ToolCall call, Action onResize)
    {
        Spacing = 0;
        var faint = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        _label.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _icon.Foreground = faint;
        _chevron.Foreground = faint;
        _detail.Foreground = faint;
        _icon.Glyph = call.Name switch
        {
            "find_files" => "",          // folder
            "search_documents" => "",    // document
            "get_weather" => "",         // cloud
            "get_calendar" => "",        // calendar
            "get_system_status" => "",   // PC
            _ => "",
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        row.Children.Add(_brain);
        row.Children.Add(_icon);
        row.Children.Add(_label);
        row.Children.Add(_dots);
        row.Children.Add(_chevron);
        row.Tapped += (_, _) =>
        {
            if (!_done || _detail.Text.Length == 0) return;
            _open = !_open;
            _detail.Visibility = _open ? Visibility.Visible : Visibility.Collapsed;
            _chevron.Glyph = _open ? "" : "";
            onResize();
        };
        Children.Add(row);
        Children.Add(_detail);
        _brain.Start();
        _dots.Start();
    }

    public void Update(ToolCall call)
    {
        if (!call.Done)
        {
            _label.Text = call.Label.Length > 0 ? call.Label : "Looking it up";
            return;
        }
        if (_done) return;
        _done = true;
        _brain.Stop();
        _brain.Visibility = Visibility.Collapsed;
        _dots.Stop();
        _dots.Visibility = Visibility.Collapsed;
        _icon.Visibility = Visibility.Visible;
        _label.Text = call.Outcome.Length > 0 ? call.Outcome : call.Label;
        _label.Foreground = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        string asked = call.Arguments is { Length: > 2 } a && a != "{}" ? $"Asked: {a}\n" : "";
        _detail.Text = (asked + call.Result).Trim();
        _chevron.Visibility = _detail.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Text in an answer that stands for a file: its name (or "[2]" for a cited document), and what to show.</summary>
internal sealed record LinkToken(string Token, string Path, string Label, bool Citation);

/// <summary>The model's markdown (bold, italics, code, headings) as inlines, with the files it names as links.</summary>
internal static class ChatMarkdown
{
    private static readonly Regex Inline = new(@"\*\*(?<b>.+?)\*\*|__(?<b>.+?)__|`(?<c>[^`]+)`|(?<![\w*])\*(?<i>[^*\s][^*]*?)\*(?![\w*])",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex Link = new(@"\[([^\]\n]+)\]\((?:https?://|file:)[^)\s]+\)", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// What in an answer's text is a link: the names of the files its tools found (and their names without
    /// the extension, when distinctive), and [1], [2] for the documents it read. Longest first.
    /// </summary>
    public static IReadOnlyList<LinkToken> LinksOf(IEnumerable<object> blocks)
    {
        var list = new List<LinkToken>();
        foreach (var b in blocks)
        {
            if (b is FilesBlock f)
            {
                foreach (var file in f.Files)
                {
                    list.Add(new LinkToken(file.Name, file.Path, file.Name, false));
                    string stem = System.IO.Path.GetFileNameWithoutExtension(file.Name);
                    if (stem.Length >= 6 && stem != file.Name) list.Add(new LinkToken(stem, file.Path, stem, false));
                }
            }
            else if (b is SourcesBlock s)
            {
                for (int i = 0; i < s.Docs.Count; i++)
                {
                    var d = s.Docs[i];
                    string stem = System.IO.Path.GetFileNameWithoutExtension(d.Name);
                    string label = stem.Length > 28 ? stem[..26] + "…" : stem;
                    list.Add(new LinkToken($"[{i + 1}]", d.Path, label, true));
                    list.Add(new LinkToken(d.Name, d.Path, d.Name, false));
                }
            }
        }
        return list.OrderByDescending(l => l.Token.Length).ToList();
    }

    public static void Fill(InlineCollection inlines, double fontSize, string md,
        IReadOnlyList<LinkToken> links, Func<LinkToken, string, int, Inline> makeLink)
    {
        inlines.Clear();
        md = Link.Replace(md.Replace("\r", "").Trim(), "$1");
        // A name the model dressed up (**Report.docx**, `Report.docx`, "Report.docx") is a link all the same.
        foreach (var l in links)
            md = Regex.Replace(md, @"(\*\*|__|`|""|“|«\s?)(" + Regex.Escape(l.Token) + @")(\*\*|__|`|""|”|\s?»)", "$2",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        var seen = new Dictionary<string, int>();
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
            // Split the line at the names of files; what is between is markdown.
            int at = 0;
            while (at < line.Length)
            {
                (int index, LinkToken? link) best = (-1, null);
                foreach (var l in links)
                {
                    int i = Find(line, l, at);
                    if (i >= 0 && (best.index < 0 || i < best.index)) best = (i, l);
                }
                if (best.link is not { } found)
                {
                    Text(inlines, line[at..], heading, fontSize);
                    break;
                }
                if (best.index > at) Text(inlines, line[at..best.index], heading, fontSize);
                string shown = found.Citation ? found.Label : line.Substring(best.index, found.Token.Length);
                int k = seen.GetValueOrDefault(found.Path + shown);
                seen[found.Path + shown] = k + 1;
                inlines.Add(makeLink(found, shown, k));
                at = best.index + found.Token.Length;
            }
            if (n < lines.Length - 1) inlines.Add(new LineBreak());
        }
    }

    /// <summary>Where <paramref name="l"/> is written in <paramref name="line"/> from <paramref name="from"/> on, as a whole word; -1 if not.</summary>
    private static int Find(string line, LinkToken l, int from)
    {
        for (int i = line.IndexOf(l.Token, from, StringComparison.OrdinalIgnoreCase); i >= 0;
             i = line.IndexOf(l.Token, i + 1, StringComparison.OrdinalIgnoreCase))
        {
            if (l.Citation) return i;
            bool before = i == 0 || !char.IsLetterOrDigit(line[i - 1]);
            int end = i + l.Token.Length;
            bool after = end >= line.Length || !char.IsLetterOrDigit(line[end]) && !(line[end] == '.' && end + 1 < line.Length && char.IsLetterOrDigit(line[end + 1]));
            if (before && after) return i;
        }
        return -1;
    }

    /// <summary>A piece of markdown without links, as runs.</summary>
    private static void Text(InlineCollection inlines, string text, bool heading, double fontSize)
    {
        int at = 0;
        foreach (Match m in Inline.Matches(text))
        {
            if (m.Index > at) inlines.Add(Plain(text[at..m.Index], heading));
            if (m.Groups["b"].Success) inlines.Add(new Run { Text = m.Groups["b"].Value, FontWeight = FontWeights.SemiBold });
            else if (m.Groups["c"].Success)
                inlines.Add(new Run
                {
                    Text = m.Groups["c"].Value, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = fontSize - 1,
                    Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"],
                });
            else inlines.Add(new Run { Text = m.Groups["i"].Value, FontStyle = Windows.UI.Text.FontStyle.Italic });
            at = m.Index + m.Length;
        }
        if (at < text.Length) inlines.Add(Plain(text[at..], heading));
    }

    private static Run Plain(string s, bool heading) =>
        new() { Text = s, FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal };
}
