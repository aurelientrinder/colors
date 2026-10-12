using System.ComponentModel;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Palette;

/// <summary>
/// One row in the results list. Rows are updated in place as the query changes (see
/// <see cref="CopyFrom"/>): replacing the selected row would drop the list's selection and replay
/// its selection animation on every keystroke.
/// </summary>
public sealed class HitVm : INotifyPropertyChanged
{
    private ImageSource? _icon;

    public HitVm(Native.Hit h, string query)
    {
        Title = h.Title;
        Subtitle = h.Subtitle;
        // Rows found by meaning say so instead of their type; the icon still shows the type.
        Kind = h.Related ? "Related" : h.Kind;
        IconKey = h.IconKey;
        CopyText = h.CopyText;
        Path = h.Path;
        // Calculator / web rows echo the query and related rows matched on meaning, not letters:
        // highlighting either is just noise.
        Query = h.Related || h.Kind is "Calculator" or "Web" ? "" : query;

        (Glyph, Color a, Color b) = h.Kind switch
        {
            "Command" => ("", Rgb(214, 96, 122), Rgb(168, 62, 92)),
            "Calculator" => ("", Rgb(160, 110, 236), Rgb(112, 70, 196)),
            "Web" => ("", Rgb(74, 170, 232), Rgb(40, 122, 196)),
            _ => ("", Rgb(122, 130, 150), Rgb(86, 94, 114)),
        };
        TileBrush = new LinearGradientBrush
        {
            StartPoint = new Windows.Foundation.Point(0, 0),
            EndPoint = new Windows.Foundation.Point(1, 1),
            GradientStops =
            {
                new GradientStop { Color = a, Offset = 0 },
                new GradientStop { Color = b, Offset = 1 },
            },
        };
    }

    private static Color Rgb(byte r, byte g, byte b) => Color.FromArgb(255, r, g, b);

    /// <summary>Turn this row into <paramref name="o"/>, keeping the object (and so its list container).</summary>
    public void CopyFrom(HitVm o)
    {
        Title = o.Title;
        Subtitle = o.Subtitle;
        Kind = o.Kind;
        CopyText = o.CopyText;
        Path = o.Path;
        Query = o.Query;
        Glyph = o.Glyph;
        TileBrush = o.TileBrush;
        if (IconKey != o.IconKey) _icon = o._icon;
        IconKey = o.IconKey;
        Rev++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // every property
    }

    /// <summary>Bumped by <see cref="CopyFrom"/>, so the highlighted title redraws.</summary>
    public int Rev { get; private set; }

    /// <summary>Would this row look identical to <paramref name="o"/>? (Then the list can keep it.)</summary>
    public bool SameRow(HitVm o) =>
        Title == o.Title && Subtitle == o.Subtitle && Kind == o.Kind && IconKey == o.IconKey && Query == o.Query && Path == o.Path;

    public string Title { get; private set; }
    public string Subtitle { get; private set; }
    public string Kind { get; private set; }
    public string? IconKey { get; private set; }
    public string? CopyText { get; private set; }
    /// <summary>Full path when the row is a file (it gets a preview).</summary>
    public string? Path { get; private set; }
    public string Query { get; private set; }
    public string Glyph { get; private set; }
    public Brush TileBrush { get; private set; }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Icon)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IconVisibility)));
        }
    }

    public Visibility IconVisibility => _icon != null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Glyph tile, only for kinds that never get a shell icon.</summary>
    public Visibility TileVisibility => IconKey == null ? Visibility.Visible : Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Attached property that fills a TextBlock with the row title, bolding the characters
/// the query matched (whole-substring match first, then in-order letters per word).
/// </summary>
public static class Hl
{
    public static readonly DependencyProperty VmProperty =
        DependencyProperty.RegisterAttached("Vm", typeof(HitVm), typeof(Hl), new PropertyMetadata(null, OnVmChanged));

    public static HitVm? GetVm(DependencyObject d) => (HitVm?)d.GetValue(VmProperty);
    public static void SetVm(DependencyObject d, HitVm? v) => d.SetValue(VmProperty, v);

    /// <summary>The row's <see cref="HitVm.Rev"/>: changes when the row was updated in place.</summary>
    public static readonly DependencyProperty RevProperty =
        DependencyProperty.RegisterAttached("Rev", typeof(int), typeof(Hl), new PropertyMetadata(0, OnRevChanged));

    public static int GetRev(DependencyObject d) => (int)d.GetValue(RevProperty);
    public static void SetRev(DependencyObject d, int v) => d.SetValue(RevProperty, v);

    private static void OnVmChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Render(d, e.NewValue as HitVm);

    private static void OnRevChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) => Render(d, GetVm(d));

    private static void Render(DependencyObject d, HitVm? vm)
    {
        if (d is not TextBlock tb) return;
        tb.Inlines.Clear();
        if (vm == null) return;

        string title = vm.Title;
        bool[]? mask = Mask(title, vm.Query);
        if (mask == null)
        {
            tb.Inlines.Add(new Run { Text = title });
            return;
        }

        int i = 0;
        while (i < title.Length)
        {
            int j = i;
            while (j < title.Length && mask[j] == mask[i]) j++;
            var run = new Run { Text = title[i..j] };
            if (mask[i]) run.FontWeight = FontWeights.Bold;
            tb.Inlines.Add(run);
            i = j;
        }
    }

    private static bool[]? Mask(string title, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || title.Length == 0) return null;
        var mask = new bool[title.Length];
        bool any = false;

        foreach (var word in query.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            int at = title.IndexOf(word, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
            {
                for (int k = 0; k < word.Length; k++) mask[at + k] = true;
                any = true;
                continue;
            }

            // Fuzzy: letters in order. Skip the word if the title doesn't contain all of them
            // (the file matched on its folder path, not its name), or if they are so spread out
            // that bolding them would just scatter bold letters across the whole name.
            var hit = new List<int>(word.Length);
            int pos = 0;
            foreach (char c in word)
            {
                char cu = char.ToUpperInvariant(c);
                int p = pos;
                while (p < title.Length && char.ToUpperInvariant(title[p]) != cu) p++;
                if (p >= title.Length) { hit.Clear(); break; }
                hit.Add(p);
                pos = p + 1;
            }
            if (hit.Count > 0 && hit[^1] - hit[0] + 1 > word.Length * 2 + 2) hit.Clear();
            foreach (int p in hit) { mask[p] = true; any = true; }
        }
        return any ? mask : null;
    }
}
