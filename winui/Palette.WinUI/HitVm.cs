using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Palette;

/// <summary>
/// One row in the results list. Rows are updated in place as the query changes (see
/// <see cref="CopyFrom"/>): replacing the selected row would drop the list's selection and replay
/// its selection animation on every keystroke.
/// </summary>
public sealed class HitVm : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private bool _selected;
    private double _previewHeight;
    private object? _previewContent;

    /// <param name="glyph">Glyph overriding the one for the row's kind (action rows).</param>
    /// <param name="chip">Text after the subtitle, overriding the kind ("" leaves only the subtitle).</param>
    public HitVm(Native.Hit h, string? glyph = null, string? chip = null)
    {
        RawKind = h.Kind;
        Title = h.Title;
        Subtitle = h.Subtitle;
        IconKey = h.IconKey;
        CopyText = h.CopyText;
        Path = h.Path;
        IsActionRow = glyph != null;

        string label = chip ?? (h.Related ? "Related" : h.Pinned ? "Pinned" : KindLabel(h.Kind, h.Path));
        if (h.Kind == "Calculator")
        {
            // The row reads as the sum typed; the answer opens under it (see Preview.cs).
            int at = h.Subtitle.LastIndexOf(" = ", StringComparison.Ordinal);
            Title = at > 0 ? h.Subtitle[..at] : h.Title;
            SubText = "Calculator";
        }
        else if (IsToggle)
        {
            // "Bluetooth is off • [bolt] Action": the badge and its word are separate elements.
            SubText = h.Subtitle.Length > 0 ? h.Subtitle + " •" : "";
        }
        else
        {
            SubText = h.Subtitle.Length > 0 && label.Length > 0 ? $"{h.Subtitle} • {label}" : h.Subtitle + label;
        }

        Glyph = glyph ?? GlyphFor(h.Kind, h.Title);
        (SecondGlyph, SecondLabel, Reveals) = h.Kind switch
        {
            "Toggle" => ("\uE751", "Apply", false),
            "Command" => ("\uE751", "Run", false),
            "Calculator" => ("\uE8C8", "Copy result", false),
            "Window" => ("\uE8A7", "Switch to", false),
            "Web" => ("\uE8A7", "Search the web", false),
            "File" or "Folder" or "App" => ("\uE8A7", "Open file location", true),
            _ => ("\uE8A7", "Open", false),
        };
    }

    /// <summary>The line left after a quick setting ran: a check, what was done, and Undo.</summary>
    public static HitVm Status(string title, string subtitle, bool canUndo)
    {
        var vm = new HitVm(new Native.Hit(title, subtitle, "Status", null, null), chip: "")
        {
            IsStatus = true,
            CanUndo = canUndo,
        };
        vm.SubText = subtitle;
        vm.SecondGlyph = "\uE7A7";
        vm.SecondLabel = "Undo";
        return vm;
    }

    /// <summary>
    /// The row a web answer (weather, a stock, a fact) opens under: the query, to be searched on the
    /// web. It is the app's own, not the engine's, so it has no engine index or actions.
    /// </summary>
    public static HitVm ForAnswer(WebAnswers.Answer answer, string url)
    {
        var vm = new HitVm(new Native.Hit(answer.Query.Trim(), "Search the web", "Web", null, null), chip: "")
        {
            IsAnswer = true,
            Answer = answer,
            AnswerUrl = url,
        };
        vm.SubText = "Search the web • Default browser";
        vm.SecondLabel = "Open in browser";
        return vm;
    }

    /// <summary>
    /// The Weather app's row with its card, for when the engine did not list the app itself
    /// ("weather in Paris"): it opens the app on that place; its second button searches the web.
    /// </summary>
    public static HitVm ForWeatherApp(WebAnswers.Weather w, string searchUrl)
    {
        var vm = new HitVm(new Native.Hit("Weather", w.Place, "App", WebAnswers.WeatherAppKey, null), chip: "")
        {
            IsAnswer = true,
            Answer = w,
            AnswerUrl = WebAnswers.WeatherAppUrl(w),
            AnswerAltUrl = searchUrl,
        };
        vm.SubText = $"{w.Place} • App";
        vm.SecondGlyph = "\uE721";
        vm.SecondLabel = "Search the web";
        return vm;
    }

    /// <summary>Put <paramref name="w"/>'s card under this row, the engine's Weather app, which then opens on that place.</summary>
    public void AttachWeather(WebAnswers.Weather? w)
    {
        Answer = w;
        AnswerUrl = w != null ? WebAnswers.WeatherAppUrl(w) : null;
    }

    private static string KindLabel(string kind, string? path) => kind switch
    {
        "File" when System.IO.Path.GetExtension(path ?? "") is { Length: > 1 } ext => ext[1..].ToUpperInvariant() + " File",
        "Setting" => "Settings",
        "Toggle" or "Status" => "",
        _ => kind,
    };

    /// <summary>Glyph for rows that never get a shell icon; quick settings get the setting's own.</summary>
    private static string GlyphFor(string kind, string title)
    {
        if (kind == "Toggle")
        {
            string t = title.ToLowerInvariant();
            if (t.Contains("dark mode")) return "\uE771";      // Personalize
            if (t.Contains("wi-fi")) return "\uE701";
            if (t.Contains("bluetooth")) return "\uE702";
            if (t.Contains("brightness")) return "\uE706";
            if (t.Contains("unmute") || t.Contains("volume")) return "\uE767";
            if (t.Contains("mute")) return "\uE74F";
            return "\uE945";
        }
        return kind switch
        {
            "Command" => "\uE756",
            "Calculator" => "\uE8EF",
            "Web" => "\uE721",
            "Window" => "\uE737",
            _ => "\uE7C3",
        };
    }

    /// <summary>Turn this row into <paramref name="o"/>, keeping the object (and so its list container).</summary>
    public void CopyFrom(HitVm o)
    {
        RawKind = o.RawKind;
        Title = o.Title;
        Subtitle = o.Subtitle;
        SubText = o.SubText;
        CopyText = o.CopyText;
        Path = o.Path;
        Glyph = o.Glyph;
        SecondGlyph = o.SecondGlyph;
        SecondLabel = o.SecondLabel;
        Reveals = o.Reveals;
        IsActionRow = o.IsActionRow;
        IsStatus = o.IsStatus;   // the Done line becomes a result again when typing resumes
        CanUndo = o.CanUndo;
        if (IconKey != o.IconKey) _icon = o._icon;
        IconKey = o.IconKey;
        Answer = o.Answer;
        AnswerUrl = o.AnswerUrl;
        AnswerAltUrl = o.AnswerAltUrl;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // every property
    }

    /// <summary>Would this row look identical to <paramref name="o"/>? (Then the list can keep it.)</summary>
    public bool SameRow(HitVm o) =>
        RawKind == o.RawKind && Title == o.Title && SubText == o.SubText && IconKey == o.IconKey && Path == o.Path;

    /// <summary>The engine's kind ("File", "Toggle", ...).</summary>
    public string RawKind { get; private set; }
    public string Title { get; private set; }
    public string Subtitle { get; private set; }
    /// <summary>The second line: subtitle and kind ("C:\Docs • PDF File").</summary>
    public string SubText { get; private set; }
    public string? IconKey { get; private set; }
    public string? CopyText { get; private set; }
    /// <summary>Full path when the row is a file (it gets a preview).</summary>
    public string? Path { get; private set; }
    public string Glyph { get; private set; }
    /// <summary>The button beside "More options" on the selected row.</summary>
    public string SecondGlyph { get; private set; }
    public string SecondLabel { get; private set; }
    /// <summary>The second button opens the file location rather than the result.</summary>
    public bool Reveals { get; private set; }
    public bool IsToggle => RawKind == "Toggle";
    public bool IsCalc => RawKind == "Calculator";
    public bool IsStatus { get; private set; }
    public bool CanUndo { get; private set; }
    /// <summary>A web answer's row (<see cref="ForAnswer"/>); it sits above the engine's rows.</summary>
    public bool IsAnswer { get; private set; }
    /// <summary>The card open under the row: on an answer row, or on the engine's Weather app row.</summary>
    public WebAnswers.Answer? Answer { get; set; }
    /// <summary>Where Enter goes instead of the engine's launch.</summary>
    public string? AnswerUrl { get; private set; }
    /// <summary>Where the second button goes, if not to <see cref="AnswerUrl"/>.</summary>
    public string? AnswerAltUrl { get; private set; }
    /// <summary>A row of a result's action list (no buttons of its own).</summary>
    public bool IsActionRow { get; private set; }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            _selected = value;
            Changed(nameof(ButtonsOpacity));
            Changed(nameof(ButtonsShown));
        }
    }

    /// <summary>Height of the preview opened under this row (animated; 0 when closed).</summary>
    public double PreviewHeight
    {
        get => _previewHeight;
        set
        {
            if (_previewHeight == value) return;
            _previewHeight = value;
            Changed(nameof(PreviewHeight));
        }
    }

    /// <summary>The shared preview pane while it belongs to this row, else null.</summary>
    public object? PreviewContent
    {
        get => _previewContent;
        set
        {
            if (_previewContent == value) return;
            _previewContent = value;
            Changed(nameof(PreviewContent));
        }
    }

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            Changed(nameof(Icon));
            Changed(nameof(IconOpacity));
            Changed(nameof(IconVisibility));
            Changed(nameof(GlyphVisibility));
        }
    }

    private static Visibility Vis(bool b) => b ? Visibility.Visible : Visibility.Collapsed;

    public Visibility IconVisibility => Vis(IconKey != null && !IsStatus);
    /// <summary>The shell icon fades in when it arrives rather than popping.</summary>
    public double IconOpacity => _icon != null ? 1 : 0;
    /// <summary>Glyph, only for rows that never get a shell icon.</summary>
    public Visibility GlyphVisibility => Vis(IconKey == null && !IsStatus);
    public Visibility StatusVisibility => Vis(IsStatus);
    public Visibility ActionVisibility => Vis(IsToggle);
    public Visibility SubTextVisibility => Vis(SubText.Length > 0);
    /// <summary>Whether the row has buttons at all; they fade in on selection (<see cref="ButtonsOpacity"/>).</summary>
    public Visibility ButtonsVisibility => Vis(IsStatus ? CanUndo : !IsActionRow);
    public bool ButtonsShown => IsStatus || _selected;
    public double ButtonsOpacity => ButtonsShown ? 1 : 0;
    public Visibility MoreVisibility => Vis(!IsStatus && !IsAnswer);

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
