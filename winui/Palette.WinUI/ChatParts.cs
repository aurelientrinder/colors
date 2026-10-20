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
        var brush = (Brush)Application.Current.Resources[active ? "TextFillColorSecondaryBrush" : "TextFillColorTertiaryBrush"];
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
/// An assistant message, as a timeline (direction B of the chat designs). While the model works, a thin
/// rail runs down the left margin with a node for each thing it does (its thinking, each tool) and its
/// sentences between them; the node at work breathes. Once the answer is written the rail folds into one
/// small chip ("2 steps · 9 s") above it, which opens it again. Then the answer, the cards of what its
/// tools found (weather, calendar, the PC), and small print. Files and documents it names are links.
/// </summary>
internal sealed class AssistantView : StackPanel
{
    /// <summary>How far into the left margin the rail's nodes hang, so the text on it lines up with the answer.</summary>
    public const double Gutter = 30;

    private readonly Grid _railHost = new() { Visibility = Visibility.Collapsed };
    private readonly StackPanel _rail = new() { Spacing = 7 };
    private readonly Grid _chip = new() { Visibility = Visibility.Collapsed, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly StackPanel _chipIcons = new() { Orientation = Orientation.Horizontal, Spacing = 3, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _chipText = new() { FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _chipChevron = new() { Glyph = "\uE70D", FontSize = 9, VerticalAlignment = VerticalAlignment.Center };
    /// <summary>Dots before the first words, and while the model goes on after its tools.</summary>
    private readonly WaitingDots _tail = new() { Visibility = Visibility.Collapsed, Margin = new Thickness(2, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Left };
    private readonly RichTextBlock _body = new() { TextWrapping = TextWrapping.Wrap, LineHeight = 22, IsTextSelectionEnabled = true };
    private readonly Paragraph _para = new();
    private readonly TextBlock _note = new() { FontSize = 11, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private readonly StackPanel _blocks = new() { Spacing = 4 };
    private readonly Func<object, FrameworkElement?> _makeBlock;
    private readonly Func<string, string, FrameworkElement> _makeLink;
    private readonly Action _onResize;
    private int _blocksSeen;
    private bool _expanded;     // the rail opened again from its chip

    public AssistantView(Action onResize, Func<object, FrameworkElement?> makeBlock, Func<string, string, FrameworkElement> makeLink)
    {
        _onResize = onResize;
        _makeBlock = makeBlock;
        _makeLink = makeLink;
        Spacing = 6;
        _body.Blocks.Add(_para);
        var faint = Res("TextFillColorTertiaryBrush");
        _note.Foreground = faint;
        _chipText.Foreground = Res("TextFillColorSecondaryBrush");
        _chipChevron.Foreground = faint;

        // The rail: a line in the margin, behind the nodes.
        var line = new Microsoft.UI.Xaml.Shapes.Rectangle
        {
            Width = 1.5, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(-Gutter + 9.25, 12, 0, 12),
            Fill = Res("ControlStrongStrokeColorDisabledBrush"),
        };
        _railHost.Children.Add(line);
        _railHost.Children.Add(_rail);

        // The chip the rail folds into.
        var chipRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
        chipRow.Children.Add(_chipIcons);
        chipRow.Children.Add(_chipText);
        chipRow.Children.Add(_chipChevron);
        var chipFace = new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = Res("CardStrokeColorDefaultBrush"),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12), Padding = new Thickness(5, 3, 10, 3), Child = chipRow,
        };
        _chip.Children.Add(chipFace);
        _chip.PointerEntered += (_, _) => chipFace.Background = Res("SubtleFillColorSecondaryBrush");
        _chip.PointerExited += (_, _) => chipFace.Background = Res("CardBackgroundFillColorDefaultBrush");
        _chip.Tapped += (_, e) =>
        {
            e.Handled = true;
            _expanded = !_expanded;
            Fold.Turn(_chipChevron, _expanded);
            Fold.Toggle(_railHost, _expanded, onResize);
        };

        Children.Add(_chip);
        Children.Add(_railHost);
        Children.Add(_tail);
        Children.Add(_body);
        Children.Add(_blocks);
        Children.Add(_note);
    }

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    // ------------------------------------------------------------------ the rail's rows

    private RailThink? _thinkRow;
    private readonly List<(ToolCall call, RailStep view)> _steps = new();
    private readonly List<(ToolRound round, StackPanel calls)> _rounds = new();

    /// <summary>A row of the rail: a node in the margin, then what it stands for.</summary>
    public static Grid RailRow(FrameworkElement node, UIElement content)
    {
        var g = new Grid { Margin = new Thickness(-Gutter, 0, 0, 0), ColumnSpacing = Gutter - 20 };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(20) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        node.VerticalAlignment = VerticalAlignment.Top;
        node.HorizontalAlignment = HorizontalAlignment.Center;
        g.Children.Add(node);
        Grid.SetColumn((FrameworkElement)content, 1);
        g.Children.Add(content);
        return g;
    }

    /// <summary>Add what is new to the rail (thinking, each round's sentence and tools) and bring every node up to date.</summary>
    private void SyncRail(ChatMsg m, bool thinkingLive)
    {
        if (_thinkRow == null && (thinkingLive || m.Thinking.Length > 0))
        {
            _thinkRow = new RailThink(_onResize);
            _rail.Children.Insert(0, _thinkRow.Row);
        }
        _thinkRow?.Update(m, thinkingLive);

        for (int i = _rounds.Count; i < m.Rounds.Count; i++)
        {
            var round = m.Rounds[i];
            if (round.Content.Trim().Length > 0)
            {
                var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, LineHeight = 22, IsTextSelectionEnabled = true };
                var para = new Paragraph();
                text.Blocks.Add(para);
                int n = i;
                ChatMarkdown.Fill(para.Inlines, text.FontSize, round.Content.Trim(), _links, (l, shown, k) => LinkInline(l, shown, 1000 * (n + 1) + k));
                var dot = new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 7, Height = 7, Fill = Res("TextFillColorDisabledBrush"), Margin = new Thickness(0, 8, 0, 0) };
                _rail.Children.Add(RailRow(dot, text));
            }
            var calls = new StackPanel { Spacing = 7 };
            _rail.Children.Add(calls);
            _rounds.Add((round, calls));
            // The words of the turn that called these now sit on the rail: the next words start afresh.
            _text = "";
            _drawn = 0;
            _marks.Clear();
        }
        foreach (var (round, calls) in _rounds)
        {
            foreach (var call in round.Calls)
            {
                if (_steps.Any(s => ReferenceEquals(s.call, call))) continue;
                var view = new RailStep(call, _onResize);
                calls.Children.Add(view.Row);
                _steps.Add((call, view));
            }
        }
        foreach (var (call, view) in _steps) view.Update(call);
    }

    /// <summary>The chip's icons and words: the tools used (and the brain, if it thought), steps and time.</summary>
    private void FillChip(ChatMsg m)
    {
        var calls = m.Rounds.SelectMany(r => r.Calls).ToList();
        _chipIcons.Children.Clear();
        var glyphs = new List<string>();
        if (m.Thinking.Length > 0) glyphs.Add("brain");
        foreach (var c in calls)
        {
            string g = RailStep.Glyph(c.Name);
            if (!glyphs.Contains(g)) glyphs.Add(g);
        }
        foreach (var g in glyphs.Take(4))
        {
            var face = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = Res("SubtleFillColorSecondaryBrush") };
            face.Child = g == "brain"
                ? new BrainIcon(11) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
                : new FontIcon { Glyph = g, FontSize = 10, Foreground = Res("TextFillColorSecondaryBrush") };
            _chipIcons.Children.Add(face);
        }
        double secs = (m.ThinkMs + calls.Sum(c => c.Ms)) / 1000.0;
        string time = secs >= 1 ? $"{secs:0} s" : "a moment";
        _chipText.Text = calls.Count switch
        {
            0 => $"Thought for {time}",
            1 => secs >= 1 ? $"1 step · {time}" : "1 step",
            _ => secs >= 1 ? $"{calls.Count} steps · {time}" : $"{calls.Count} steps",
        };
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

        string reply = m.Text[Math.Min(m.ReplyStart, m.Text.Length)..];
        bool answering = reply.Trim().Length > 0;
        bool stepRunning = m.Rounds.Any(r => r.Calls.Any(c => !c.Done));
        // Thinking (or reading documents first) before any word: the brain's node, live.
        bool thinkingLive = writing && !answering && m.Rounds.Count == 0 && (m.Think || m.Thinking.Length > 0 || m.Hint.Length > 0);
        SyncRail(m, thinkingLive);

        bool worked = m.Rounds.Count > 0 || m.Thinking.Length > 0 || thinkingLive;
        if (writing)
        {
            _railHost.Visibility = worked ? Visibility.Visible : Visibility.Collapsed;
            _chip.Visibility = Visibility.Collapsed;
        }
        else
        {
            // Done: the rail folds into its chip (and opens again from it).
            FillChip(m);
            _chip.Visibility = worked ? Visibility.Visible : Visibility.Collapsed;
            _railHost.Visibility = worked && _expanded ? Visibility.Visible : Visibility.Collapsed;
        }

        bool waiting = writing && !answering && !stepRunning && !thinkingLive;
        _tail.Visibility = waiting ? Visibility.Visible : Visibility.Collapsed;
        if (waiting) _tail.Start();
        else _tail.Stop();

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
/// A node on the rail: a small disc with an icon. Live (the model is at it), it is tinted with the accent,
/// its brain works and a ring breathes out of it; done, it is a quiet disc with the tool's icon.
/// </summary>
internal sealed class RailNode : Grid
{
    private readonly Border _disc = new() { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1) };
    private readonly Microsoft.UI.Xaml.Shapes.Ellipse _ring = new() { Width = 20, Height = 20, StrokeThickness = 1.5, Opacity = 0 };
    private readonly ScaleTransform _ringScale = new() { CenterX = 10, CenterY = 10 };
    private readonly BrainIcon _brain = new(12) { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly FontIcon _icon = new() { FontSize = 10, Visibility = Visibility.Collapsed };
    private Storyboard? _breath;
    private bool? _live;

    public RailNode()
    {
        Width = 20;
        Height = 20;
        _ring.RenderTransform = _ringScale;
        _ring.Stroke = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        var face = new Grid();
        face.Children.Add(_brain);
        face.Children.Add(_icon);
        _disc.Child = face;
        Children.Add(_ring);
        Children.Add(_disc);
        _icon.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
    }

    /// <summary>Live (the brain at work), or done showing <paramref name="glyph"/> (null: the brain, still).</summary>
    public void Set(bool live, string? glyph)
    {
        if (_live == live && (live || _icon.Glyph == (glyph ?? ""))) return;
        _live = live;
        if (live)
        {
            _disc.Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
            _disc.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            _brain.Visibility = Visibility.Visible;
            _icon.Visibility = Visibility.Collapsed;
            _brain.Start();
            if (_breath == null)
            {
                var sb = new Storyboard();
                void Anim(DependencyObject t, string p, double from, double to)
                {
                    var a = new DoubleAnimation { From = from, To = to, Duration = TimeSpan.FromMilliseconds(1400), RepeatBehavior = RepeatBehavior.Forever,
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
                    Storyboard.SetTarget(a, t);
                    Storyboard.SetTargetProperty(a, p);
                    sb.Children.Add(a);
                }
                Anim(_ringScale, "ScaleX", 1, 1.7);
                Anim(_ringScale, "ScaleY", 1, 1.7);
                Anim(_ring, "Opacity", 0.55, 0);
                _breath = sb;
                sb.Begin();
            }
        }
        else
        {
            _breath?.Stop();
            _breath = null;
            _ring.Opacity = 0;
            _brain.Stop();
            _disc.Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"];
            _disc.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            _brain.Visibility = glyph == null ? Visibility.Visible : Visibility.Collapsed;
            _icon.Visibility = glyph == null ? Visibility.Collapsed : Visibility.Visible;
            _icon.Glyph = glyph ?? "";
        }
    }
}

/// <summary>A label that shimmers (its opacity breathing) while the step it names is under way.</summary>
internal static class Shimmer
{
    public static Storyboard Start(UIElement target)
    {
        var a = new DoubleAnimation { From = 1, To = 0.45, Duration = TimeSpan.FromMilliseconds(850), AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } };
        Storyboard.SetTarget(a, target);
        Storyboard.SetTargetProperty(a, "Opacity");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
        return sb;
    }
}

/// <summary>
/// The model's thinking on the rail. Live: "Thinking", shimmering, and under it a faint tail of what it is
/// thinking right now, two lines at most. Done: "Thought for 9 s", which opens to the whole reasoning.
/// </summary>
internal sealed class RailThink
{
    public Grid Row { get; }
    private readonly RailNode _node = new();
    private readonly TextBlock _label = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0) };
    private readonly FontIcon _chevron = new() { Glyph = "\uE70D", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
    private readonly Whisper _whisper = new();
    private readonly TextBlock _full = new() { FontSize = 11, LineHeight = 16, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
    private Storyboard? _shimmer;
    private bool _open, _done;

    public RailThink(Action onResize)
    {
        var faint = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        _label.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _chevron.Foreground = faint;
        _full.Foreground = faint;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        head.Children.Add(_label);
        head.Children.Add(_chevron);
        head.Tapped += (_, e) =>
        {
            if (!_done || _full.Text.Length == 0) return;
            e.Handled = true;
            _open = !_open;
            Fold.Turn(_chevron, _open);
            Fold.Toggle(_full, _open, onResize);
        };
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(head);
        content.Children.Add(_whisper);
        content.Children.Add(_full);
        Row = AssistantView.RailRow(_node, content);
    }

    public void Update(ChatMsg m, bool live)
    {
        string thinking = m.Thinking.Trim();
        if (live)
        {
            _node.Set(true, null);
            _label.Text = m.Hint.Length > 0 ? m.Hint.TrimEnd('…', '.') : "Thinking";
            _shimmer ??= Shimmer.Start(_label);
            _whisper.Text = thinking;
            _whisper.Visibility = thinking.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            return;
        }
        _shimmer?.Stop();
        _shimmer = null;
        _label.Opacity = 1;
        _node.Set(false, null);
        _done = true;
        double s = m.ThinkMs / 1000.0;
        _label.Text = thinking.Length == 0 ? "Read your documents" : s < 1 ? "Thought for a moment" : $"Thought for {s:0} s";
        _whisper.Visibility = Visibility.Collapsed;
        _full.Text = thinking;
        _chevron.Visibility = thinking.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>
/// One tool the model used, on the rail: while it runs, the live node and what it is doing ("Looking through
/// your files from last week"), shimmering; once done, the tool's icon and what came of it ("Found 6 files"),
/// which opens to what was asked and what was found.
/// </summary>
internal sealed class RailStep
{
    public Grid Row { get; }
    private readonly RailNode _node = new();
    private readonly TextBlock _label = new() { FontSize = 12, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly FontIcon _chevron = new() { Glyph = "\uE70D", FontSize = 8, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 3, 0, 0), Visibility = Visibility.Collapsed };
    private readonly TextBlock _detail = new()
    {
        FontSize = 11, LineHeight = 16, TextWrapping = TextWrapping.Wrap, MaxLines = 12, TextTrimming = TextTrimming.CharacterEllipsis,
        Visibility = Visibility.Collapsed,
    };
    private Storyboard? _shimmer;
    private bool _done, _open;

    /// <summary>The icon of a tool (Segoe Fluent Icons).</summary>
    public static string Glyph(string tool) => tool switch
    {
        "find_files" => "\uE8B7",          // folder
        "search_documents" => "\uE8A5",    // document
        "get_weather" => "\uE753",         // cloud
        "get_calendar" => "\uE787",        // calendar
        "get_system_status" => "\uE7F4",   // PC
        _ => "\uE946",
    };

    public RailStep(ToolCall call, Action onResize)
    {
        var faint = (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        _label.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _chevron.Foreground = faint;
        _detail.Foreground = faint;
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        head.Children.Add(_label);
        head.Children.Add(_chevron);
        head.Tapped += (_, e) =>
        {
            if (!_done || _detail.Text.Length == 0) return;
            e.Handled = true;
            _open = !_open;
            Fold.Turn(_chevron, _open);
            Fold.Toggle(_detail, _open, onResize);
        };
        var content = new StackPanel { Spacing = 2 };
        content.Children.Add(head);
        content.Children.Add(_detail);
        Row = AssistantView.RailRow(_node, content);
        _node.Set(true, null);
    }

    public void Update(ToolCall call)
    {
        if (!call.Done)
        {
            _label.Text = call.Label.Length > 0 ? call.Label : "Looking it up";
            _shimmer ??= Shimmer.Start(_label);
            return;
        }
        if (_done) return;
        _done = true;
        _shimmer?.Stop();
        _shimmer = null;
        _label.Opacity = 1;
        _node.Set(false, Glyph(call.Name));
        _label.Text = call.Outcome.Length > 0 ? call.Outcome : call.Label;
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

/// <summary>
/// Opening and closing parts of an answer (the rail, a step's details, the reasoning): what opens fades in
/// while it slides down a few pixels; what closes fades out, then folds away. Chevrons turn.
/// </summary>
internal static class Fold
{
    private const double OpenMs = 200, CloseMs = 130;

    public static void Toggle(FrameworkElement target, bool show, Action onResize)
    {
        var shift = target.RenderTransform as TranslateTransform ?? new TranslateTransform();
        target.RenderTransform = shift;
        var sb = new Storyboard();
        void Anim(DependencyObject t, string prop, double to, double ms)
        {
            var a = new DoubleAnimation { To = to, Duration = TimeSpan.FromMilliseconds(ms), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
            Storyboard.SetTarget(a, t);
            Storyboard.SetTargetProperty(a, prop);
            sb.Children.Add(a);
        }
        if (show)
        {
            target.Opacity = 0;
            shift.Y = -6;
            target.Visibility = Visibility.Visible;
            Anim(target, "Opacity", 1, OpenMs);
            Anim(shift, "Y", 0, OpenMs);
            sb.Begin();
            onResize();
        }
        else
        {
            Anim(target, "Opacity", 0, CloseMs);
            Anim(shift, "Y", -4, CloseMs);
            sb.Completed += (_, _) =>
            {
                target.Visibility = Visibility.Collapsed;
                target.Opacity = 1;
                shift.Y = 0;
                onResize();
            };
            sb.Begin();
        }
    }

    /// <summary>Turn a chevron (drawn pointing down) to point up when open, and back.</summary>
    public static void Turn(FontIcon chevron, bool open)
    {
        chevron.Glyph = "\uE70D";
        chevron.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        var turn = chevron.RenderTransform as RotateTransform ?? new RotateTransform();
        chevron.RenderTransform = turn;
        var a = new DoubleAnimation { To = open ? 180 : 0, Duration = TimeSpan.FromMilliseconds(OpenMs), EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(a, turn);
        Storyboard.SetTargetProperty(a, "Angle");
        var sb = new Storyboard();
        sb.Children.Add(a);
        sb.Begin();
    }
}

/// <summary>
/// The model's reasoning as it comes, in a window three lines high: the newest lines at the bottom, the
/// older ones rising out of the top, where they fade away. No ellipsis, no cut words.
/// </summary>
internal sealed class Whisper : Grid
{
    private const double Lines = 3, LineHeight = 16;
    private readonly TextBlock _text = new()
    {
        FontSize = 11, LineHeight = LineHeight, LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        TextWrapping = TextWrapping.Wrap, FontStyle = Windows.UI.Text.FontStyle.Italic, VerticalAlignment = VerticalAlignment.Bottom,
    };
    private readonly RectangleGeometry _clip = new();

    public Whisper()
    {
        Height = Lines * LineHeight;
        Clip = _clip;
        Children.Add(_text);
        SizeChanged += (_, e) => _clip.Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height);
        _text.SizeChanged += (_, _) => Shade();
        Shade();
    }

    /// <summary>The reasoning so far; only its last few hundred characters are laid out (the rest could not show).</summary>
    public string Text
    {
        set
        {
            string t = value.Replace('\n', ' ').Trim();
            if (t.Length > 400)
            {
                t = t[^400..];
                int space = t.IndexOf(' ');
                if (space > 0) t = t[(space + 1)..];
            }
            _text.Text = t;
        }
    }

    /// <summary>
    /// The text's colour, as a gradient over its whole height: clear where it leaves the window at the top,
    /// solid from a line below that on (the gradient follows the text's height as it grows).
    /// </summary>
    private void Shade()
    {
        var c = ((SolidColorBrush)Application.Current.Resources["TextFillColorTertiaryBrush"]).Color;
        double h = Math.Max(_text.ActualHeight, Height);
        double top = Math.Max(0, (h - Height) / h);
        double solid = Math.Min(1, top + LineHeight * 1.2 / h);
        var clear = Windows.UI.Color.FromArgb(0, c.R, c.G, c.B);
        _text.Foreground = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1),
            GradientStops = { new GradientStop { Color = clear, Offset = top }, new GradientStop { Color = c, Offset = solid } },
        };
    }
}
