using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
        Folder = h.Folder;
        Does = h.Does;
        Related = h.Related;
        IsActionRow = glyph != null;
        SetControl(h.Control ?? (glyph == null ? LocalControl?.Invoke(h) : null));

        string label = chip ?? (h.Related ? "Related" : h.Pinned ? "Pinned" : KindLabel(h.Kind, h.Path));
        if (h.Does == "selection") label = "";
        if (h.Kind == "Calculator")
        {
            // The row reads as the sum typed; the answer opens under it (see Preview.cs).
            int at = h.Subtitle.LastIndexOf(" = ", StringComparison.Ordinal);
            Title = at > 0 ? h.Subtitle[..at] : h.Title;
            SubText = "Calculator";
        }
        else if (IsToggle || IsPlan)
        {
            // "Bluetooth is off • [bolt] Action": the badge and its word are separate elements.
            SubText = h.Subtitle.Length > 0 ? h.Subtitle + " •" : "";
        }
        else
        {
            SubText = h.Subtitle.Length > 0 && label.Length > 0 ? $"{h.Subtitle} • {label}" : h.Subtitle + label;
        }

        Glyph = glyph ?? GlyphFor(h.Kind, h.Title, h.Does, h.Control);
        if (glyph == null) UpdateTone();
        (SecondGlyph, SecondLabel, Reveals) = h.Kind switch
        {
            "Toggle" => ("\uE751", "Apply", false),
            "Plan" => ("\uE768", "Run", false),
            "Command" => ("\uE751", "Run", false),
            "Calculator" => ("\uE8C8", "Copy result", false),
            "Window" => ("\uE8A7", "Switch to", false),
            "Control" => ("\uE768", "Run", false),
            "Clipboard" => ("\uE8C8", "Copy", false),
            "Process" => ("\uE8A7", "Show in folder", false),
            "Info" => ("\uE8C8", "Copy", false),
            "Web" => ("\uE8A7", "Search the web", false),
            "File" or "Folder" or "App" => ("\uE8A7", "Open file location", true),
            _ => ("\uE8A7", "Open", false),
        };
        if (h.Does == "selection") (SecondGlyph, SecondLabel) = ("\uE721", "Search with it");
        if (h.Kind == "Clipboard") Glyph = ClipGlyph(h.Subtitle);
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

    /// <summary>The row a live system reading ("cpu", "gpu") opens under; it opens Task Manager's Performance tab.</summary>
    public static HitVm ForSystem(SystemAnswer a)
    {
        string name = a.Kind switch
        {
            SysKind.Cpu => "CPU",
            SysKind.Gpu => "GPU",
            SysKind.Npu => "NPU",
            SysKind.Memory => "Memory",
            SysKind.Disk => "Disk",
            _ => "Network",
        };
        var vm = new HitVm(new Native.Hit(name, "Live", "System", null, null), chip: "")
        {
            IsAnswer = true,
            Answer = a,
            AnswerUrl = "taskmgr.exe",
        };
        vm.SubText = "Live • Task Manager";
        vm.Glyph = "";
        vm.SecondGlyph = "";
        vm.SecondLabel = "Task Manager";
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

    /// <summary>A copied text's glyph by what it is (the subtitle starts with that: "URL, 3 min ago").</summary>
    private static string ClipGlyph(string subtitle) => subtitle.Split(' ', ',')[0] switch
    {
        "URL" => "\uE774",
        "Email" => "\uE715",
        "Path" => "\uE8B7",
        "Colour" => "\uE790",
        "Phone" => "\uE717",
        "Number" => "\uE8EF",
        "JSON" => "\uE943",
        _ => "\uE8C8",
    };

    private static string KindLabel(string kind, string? path) => kind switch
    {
        "File" when System.IO.Path.GetExtension(path ?? "") is { Length: > 1 } ext => ext[1..].ToUpperInvariant() + " File",
        "Setting" => "Settings",
        "Toggle" or "Status" or "Plan" or "Control" or "Clipboard" or "Info" => "",
        _ => kind,
    };

    /// <summary>
    /// Glyph for rows that never get a shell icon, saying what the row does: a quick setting's own,
    /// a command's (lock, sleep), a request's first step (open, snap, play).
    /// </summary>
    private static string GlyphFor(string kind, string title, string? does, Native.Control? control)
    {
        string t = title.ToLowerInvariant();
        if (kind == "Toggle")
        {
            string key = control?.Key ?? t switch
            {
                _ when t.Contains("dark mode") => "dark",
                _ when t.Contains("wi-fi") => "wifi",
                _ when t.Contains("bluetooth") => "bluetooth",
                _ when t.Contains("brightness") => "brightness",
                _ when t.Contains("do not disturb") => "dnd",
                _ when t.Contains("unmute") || t.Contains("volume") => "volume",
                _ when t.Contains("mute") => "mute",
                _ => "",
            };
            // Dark mode shows where it is going: the moon to turn it on, the sun to go light.
            if (key == "dark") return t.EndsWith(" off") ? "\uE706" : "\uE708";
            if (key == "volume" && t == "mute") key = "mute";
            return SettingGlyph(key) ?? "\uE713";
        }
        if (does is { Length: > 0 })
        {
            string[] w = does.Split(' ');
            string? g = w[0] switch
            {
                "set" when w.Length > 1 => SettingGlyph(w[1] == "mute" && w.Length > 2 && w[2] == "off" ? "volume" : w[1]),
                "media" when w.Length > 1 => w[1] switch
                {
                    "next" => "\uE893",
                    "prev" => "\uE892",
                    "stop" => "\uE71A",
                    _ => "\uE768",
                },
                _ => DoesGlyph(w[0]),
            };
            if (g != null) return g;
        }
        return kind switch
        {
            "Command" => "\uE756",
            "Plan" => "\uE8A7",
            "Calculator" => "\uE8EF",
            "Info" => "\uE946",
            "Web" => "\uE721",
            "Window" => "\uE737",
            _ => "\uE7C3",
        };
    }

    /// <summary>A quick setting's glyph, by its key ("wifi", "volume").</summary>
    private static string? SettingGlyph(string key) => key switch
    {
        "dark" => "\uE708",            // QuietHours (moon)
        "wifi" => "\uE701",
        "bluetooth" => "\uE702",
        "brightness" => "\uE706",
        "volume" => "\uE767",
        "mute" => "\uE74F",
        "dnd" => "\uE7ED",             // RingerSilent
        _ => null,
    };

    /// <summary>A built-in command's or a request step's glyph, by its first word ("lock", "open", "snap").</summary>
    private static string? DoesGlyph(string verb) => verb switch
    {
        "lock" => "\uE72E",
        "sleep" => "\uE708",
        "signout" => "\uE748",         // SwitchUser
        "restart" => "\uE777",         // UpdateRestore
        "shutdown" => "\uE7E8",        // PowerButton
        "start" => "\uE80F",           // Home
        "allapps" => "\uE71D",
        "reindex" => "\uE72C",         // Refresh
        "autostart" => "\uE823",       // Recent
        "keyhints" => "\uE765",        // Keyboard
        "quit" => "\uE711",
        "open" => "\uE8A7",            // OpenInNewWindow
        "focus" => "\uE7C4",           // TaskView
        "close" or "closeall" => "\uE8BB",
        "min" => "\uE921",
        "max" => "\uE922",
        "snap" => "\uE8A9",            // ViewAll (a grid)
        "desktop" => "\uE7F4",
        "settings" => "\uE713",
        "shot" => "\uE722",            // Camera
        "url" => "\uE774",             // Globe
        "selection" => "\uE721",       // Search
        _ => null,
    };

    // ------------------------------------------------------------ colour

    /// <summary>Set by the window: the theme the colours are picked for.</summary>
    public static bool Dark = true;

    /// <summary>
    /// The colour family of a row drawn with the app's own glyph, by what it does: the timer orange,
    /// radios blue, sound teal, light amber, do not disturb purple, power red, media pink. Rows with
    /// a shell icon (apps, files) have none.
    /// </summary>
    public string? Tone { get; private set; }

    private static string? ToneFor(HitVm v)
    {
        if (v.IsTimer) return "orange";
        if (v.IconKey != null || v.IsStatus || v.IsAnswer) return null;
        string? setting = v.ControlKey;
        string first = v.Does?.Split(' ')[0] ?? "";
        if (v.Does is { } d && d.StartsWith("set ")) setting = d.Split(' ')[1];
        if (v.RawKind == "Toggle" && setting == null)
        {
            string t = v.Title.ToLowerInvariant();
            setting = t.Contains("wi-fi") ? "wifi" : t.Contains("bluetooth") ? "bluetooth" : t.Contains("brightness") ? "brightness"
                : t.Contains("dark mode") ? "dark" : t.Contains("disturb") ? "dnd" : t.Contains("mute") || t.Contains("volume") ? "volume" : null;
        }
        return setting switch
        {
            "wifi" or "bluetooth" => "blue",
            "volume" or "mute" => "teal",
            "brightness" or "dark" => "amber",
            "dnd" => "purple",
            _ => first switch
            {
                "shutdown" or "restart" or "signout" or "sleep" or "lock" => "red",
                "media" => "pink",
                _ => null,
            },
        };
    }

    private static readonly Dictionary<(string, bool, int), SolidColorBrush> Brushes = new();

    /// <summary>The tone's colour for the theme: bright on dark, deeper on light so it reads on white.</summary>
    public static SolidColorBrush ToneBrush(string tone, byte alpha = 255)
    {
        var key = (tone, Dark, (int)alpha);
        if (Brushes.TryGetValue(key, out var b)) return b;
        uint rgb = (tone, Dark) switch
        {
            ("orange", true) => 0xF7A23B, ("orange", false) => 0xC25E00,
            ("blue", true) => 0x4CA9F5, ("blue", false) => 0x0F6CBD,
            ("teal", true) => 0x3CC8A8, ("teal", false) => 0x0B7D66,
            ("amber", true) => 0xF2C94C, ("amber", false) => 0x946B00,
            ("purple", true) => 0xA98BF5, ("purple", false) => 0x6B4FBB,
            ("red", true) => 0xF26B6B, ("red", false) => 0xC42B1C,
            ("pink", true) => 0xEE6FA8, (_, _) => 0xB4246B,
        };
        b = new SolidColorBrush(Windows.UI.Color.FromArgb(alpha, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
        Brushes[key] = b;
        return b;
    }

    // Null where the row has no tone: the template then shows its plain twin of the element instead
    // (x:Bind ignores TargetNullValue here and would set a null brush). The slider, progress and
    // confirm button only appear on rows that always have a tone.

    /// <summary>The row glyph's brush: its tone (else the plain text colour).</summary>
    public Brush? GlyphBrush => Tone is { } t ? ToneBrush(t) : null;

    /// <summary>What the row's controls fill with (slider, progress, badge, confirm button): its tone (else the accent).</summary>
    public Brush? FillBrush => Tone is { } t ? ToneBrush(t) : null;

    /// <summary>Text and glyphs on <see cref="FillBrush"/>: near-black on the bright dark-theme tones (else the accent's own).</summary>
    public Brush? OnFillBrush => Tone != null ? (Dark ? _onFillDark ??= new(Windows.UI.Color.FromArgb(255, 0x1B, 0x1B, 0x1B)) : _onFillLight ??= new(Microsoft.UI.Colors.White)) : null;

    // Made once (on the UI thread, where bindings read them): every row refresh reads the brush again.
    private static SolidColorBrush? _onFillDark, _onFillLight;

    private void UpdateTone() => Tone = ToneFor(this);

    // ------------------------------------------------------------ inline controls

    /// <summary>A control the shell gives a row itself (Palette's own settings), or null.</summary>
    public static Func<Native.Hit, Native.Control?>? LocalControl;

    /// <summary>Room the slider strip takes under its row.</summary>
    public const double SliderDip = 40;

    private Native.Control? _control;
    private bool _switchOn, _muted, _playing;
    private double _sliderValue;
    private string _nowPlaying = "";
    /// <summary>When the user last moved this row's control: engine readings older than the move are not shown.</summary>
    public long TouchedAt;

    public Native.Control? Control => _control;
    public string? Does { get; private set; }
    public string? ControlKey => _control?.Key;
    public bool HasSwitch => _control?.Kind == "switch";
    public bool HasSlider => _control?.Kind == "slider";
    public bool HasMedia => _control?.Kind == "media";
    public double ExtraDip => HasSlider ? SliderDip : HasPassage ? PassageDip : 0;

    /// <summary>Take the engine's reading of the control, unless the user moved it a moment ago.</summary>
    public void SetControl(Native.Control? c)
    {
        _control = c;
        if (c == null || Environment.TickCount64 - TouchedAt < 1500) return;
        _switchOn = c.On && c.Kind == "switch";
        _muted = c.On && c.Kind == "slider";
        _sliderValue = c.Value;
    }

    public bool SwitchOn
    {
        get => _switchOn;
        set { if (_switchOn == value) return; _switchOn = value; Changed(nameof(SwitchOn)); }
    }

    public double SliderValue
    {
        get => _sliderValue;
        set
        {
            if (_sliderValue == value) return;
            _sliderValue = value;
            Changed(nameof(SliderValue));
            Changed(nameof(SliderText));
            Changed(nameof(SliderGlyph));
        }
    }

    public bool Muted
    {
        get => _muted;
        set { if (_muted == value) return; _muted = value; Changed(nameof(SliderGlyph)); Changed(nameof(SliderText)); }
    }

    public string SliderText => _muted ? "Muted" : $"{_sliderValue:0}%";

    /// <summary>The slider's leading glyph: the speaker at its level (crossed out when muted), or the sun.</summary>
    public string SliderGlyph => ControlKey switch
    {
        "volume" when _muted || _sliderValue == 0 => "\uE74F",
        "volume" when _sliderValue < 34 => "\uE993",
        "volume" when _sliderValue < 67 => "\uE994",
        "volume" => "\uE995",
        _ => "\uE706",
    };

    public string SliderGlyphLabel => ControlKey == "volume" ? (_muted ? "Unmute" : "Mute") : "Brightness";

    /// <summary>Media rows: whether something is playing, and what.</summary>
    public bool Playing
    {
        get => _playing;
        set { if (_playing == value) return; _playing = value; Changed(nameof(PlayGlyph)); Changed(nameof(PlayLabel)); }
    }

    public string PlayGlyph => _playing ? "\uE769" : "\uE768";
    public string PlayLabel => _playing ? "Pause" : "Play";

    /// <summary>"Song \u2022 Artist" on a media row, replacing its subtitle; empty when nothing reports.</summary>
    public string NowPlaying
    {
        get => _nowPlaying;
        set
        {
            if (_nowPlaying == value) return;
            _nowPlaying = value;
            if (HasMedia)
            {
                SubText = value.Length > 0 ? value + " \u2022" : Subtitle.Length > 0 ? Subtitle + " \u2022" : "";
                Changed(nameof(SubText));
                Changed(nameof(SubTextVisibility));
            }
        }
    }

    /// <summary>After the slider moved: the subtitle's level follows ("Volume 40%").</summary>
    public void ShowLevel(int value)
    {
        string sub = System.Text.RegularExpressions.Regex.Replace(Subtitle, @"\d+%", $"{value}%");
        if (ControlKey == "volume" && !_muted) sub = sub.Replace(", muted", "");
        if (IsToggle && sub.Length > 0) sub += " \u2022";
        if (sub == SubText) return;
        SubText = sub;
        Changed(nameof(SubText));
    }

    public Visibility SwitchVisibility => Vis(HasSwitch);

    // ------------------------------------------------------------ passages

    /// <summary>Room the passage strip takes under its row: two lines of small type.</summary>
    public const double PassageDip = 42;

    private TextBlock? _passage;
    private string? _passageKey;

    /// <summary>Found by meaning rather than by name.</summary>
    public bool Related { get; private set; }

    public bool HasPassage => _passage != null;
    public object? PassageContent => _passage;
    public Visibility PassageVisibility => Vis(HasPassage);

    /// <summary>The file and query the passage under the row was made for (null: none).</summary>
    public string? PassageKey => _passageKey;

    /// <summary>
    /// Show the part of the document that matched, under the row, the matching words in bold. It
    /// fades in rather than popping; the list makes room for it (<see cref="ExtraDip"/>).
    /// </summary>
    public void SetPassage(string key, string text, IReadOnlyList<int[]> marks)
    {
        var tb = new TextBlock
        {
            FontSize = 12,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Top,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            IsHitTestVisible = false,
            Opacity = 0,
            OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(160) },
        };
        var strong = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        int at = 0;
        foreach (var m in marks.OrderBy(m => m[0]))
        {
            int s = Math.Clamp(m[0], at, text.Length), e = Math.Clamp(m[0] + m[1], s, text.Length);
            if (s > at) tb.Inlines.Add(new Run { Text = text[at..s] });
            if (e > s) tb.Inlines.Add(new Run { Text = text[s..e], FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Foreground = strong });
            at = e;
        }
        if (at < text.Length) tb.Inlines.Add(new Run { Text = text[at..] });
        _passage = tb;
        _passageKey = key;
        Changed(nameof(PassageContent));
        Changed(nameof(PassageVisibility));
        Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()?.TryEnqueue(() => tb.Opacity = 1);
    }

    public void ClearPassage()
    {
        if (_passage == null && _passageKey == null) return;
        _passage = null;
        _passageKey = null;
        Changed(nameof(PassageContent));
        Changed(nameof(PassageVisibility));
    }

    // ------------------------------------------------------------ confirming

    private bool _confirming;
    private string _subBeforeConfirm = "";

    /// <summary>What a row asks to be confirmed for, or null: shutting down, restarting, signing out.</summary>
    public string? ConfirmVerb => (RawKind is "Command" or "Plan") ? Does switch
    {
        "shutdown" => "Shut down",
        "restart" => "Restart",
        "signout" => "Sign out",
        _ => null,
    } : null;

    /// <summary>Enter was pressed once on a row that needs confirming: it asks before doing it.</summary>
    public bool Confirming
    {
        get => _confirming;
        set
        {
            if (_confirming == value) return;
            _confirming = value;
            if (value)
            {
                _subBeforeConfirm = SubText;
                SubText = $"Press Enter again to {ConfirmVerb!.ToLowerInvariant()}";
            }
            else
            {
                SubText = _subBeforeConfirm;
            }
            Changed(nameof(SubText));
            Changed(nameof(SubTextVisibility));
            Changed(nameof(ConfirmVisibility));
            Changed(nameof(ButtonsVisibility));
        }
    }

    public string ConfirmLabel => ConfirmVerb ?? "";
    public Visibility ConfirmVisibility => Vis(_confirming);

    // ------------------------------------------------------------ windows

    public bool IsWindow => RawKind == "Window";
    public Visibility WindowButtonsVisibility => Vis(IsWindow);

    // ------------------------------------------------------------ timer

    /// <summary>The timer row (Timer.cs): "5 min timer", its countdown and its buttons.</summary>
    public static HitVm ForTimer(string title, string subtitle)
    {
        var vm = new HitVm(new Native.Hit(title, subtitle, "Timer", null, null), chip: "")
        {
            IsAnswer = true,
            IsTimer = true,
        };
        vm.SubText = subtitle;
        vm.Glyph = "";   // Stopwatch
        vm.UpdateTone();
        return vm;
    }

    private string _timerText = "", _timerGlyph = "", _timerLabel = "Start";
    private double _timerProgress;

    public bool IsTimer { get; private set; }

    /// <summary>Point the timer row at a new state: title, subtitle, countdown, button, progress 0..1.</summary>
    public void SetTimer(string title, string subtitle, string text, string glyph, string label, double progress, bool canCancel)
    {
        if (_timerCanCancel != canCancel) { _timerCanCancel = canCancel; Changed(nameof(TimerCancelVisibility)); }
        if (Title != title) { Title = title; Changed(nameof(Title)); }
        if (SubText != subtitle) { Subtitle = SubText = subtitle; Changed(nameof(SubText)); Changed(nameof(SubTextVisibility)); }
        if (_timerText != text) { _timerText = text; Changed(nameof(TimerText)); }
        if (_timerGlyph != glyph) { _timerGlyph = glyph; Changed(nameof(TimerGlyph)); }
        if (_timerLabel != label) { _timerLabel = label; Changed(nameof(TimerLabel)); }
        if (Math.Abs(_timerProgress - progress) > 0.0005) { _timerProgress = progress; Changed(nameof(TimerPercent)); }
    }

    public string TimerText => _timerText;
    public string TimerGlyph => _timerGlyph;
    public string TimerLabel => _timerLabel;
    public double TimerPercent => _timerProgress * 100;
    public Visibility TimerVisibility => Vis(IsTimer);
    private bool _timerCanCancel;
    public Visibility TimerCancelVisibility => Vis(IsTimer && _timerCanCancel);

    /// <summary>The room the row buttons (two 32-wide, 8 apart) and a gap take, which the switch slides over to give.</summary>
    private const float ButtonsRoomDip = 32 + 8 + 32 + 8;

    /// <summary>The switch sits where the row buttons appear; selected, it moves left of them.</summary>
    public System.Numerics.Vector3 SwitchShift => new(HasSwitch && ButtonsShown ? -ButtonsRoomDip : 0, 0, 0);

    /// <summary>Keeps the title clear of the switch once it has slid left.</summary>
    public Thickness TitleMargin => new(0, 0, HasSwitch && ButtonsShown ? ButtonsRoomDip : 0, 0);
    public Visibility SliderVisibility => Vis(HasSlider);
    public Visibility MediaVisibility => Vis(HasMedia);

    /// <summary>Turn this row into <paramref name="o"/>, keeping the object (and so its list container).</summary>
    public void CopyFrom(HitVm o)
    {
        // A passage belongs to the file it was read from, and only to a row found by meaning.
        if (!o.Related || o.Path != Path) ClearPassage();
        Related = o.Related;
        RawKind = o.RawKind;
        Title = o.Title;
        Subtitle = o.Subtitle;
        SubText = o.SubText;
        CopyText = o.CopyText;
        Path = o.Path;
        Folder = o.Folder;
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
        if (o.Does != Does || o.ControlKey != ControlKey) { _nowPlaying = ""; _playing = false; }
        _confirming = false;   // typing on drops a pending confirmation
        Does = o.Does;
        SetControl(o.Control);
        if (HasMedia && _nowPlaying.Length > 0) SubText = _nowPlaying + " •";
        Tone = o.Tone;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // every property
    }

    /// <summary>Would this row look identical to <paramref name="o"/>? (Then the list can keep it.)</summary>
    public bool SameRow(HitVm o) =>
        RawKind == o.RawKind && Title == o.Title && SubText == o.SubText && IconKey == o.IconKey && Path == o.Path && Folder == o.Folder
        && Does == o.Does && Equals(Control, o.Control);

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
    /// <summary>Full path when the row is a folder (the right arrow browses into it).</summary>
    public string? Folder { get; private set; }
    public string Glyph { get; private set; }
    /// <summary>The button beside "More options" on the selected row.</summary>
    public string SecondGlyph { get; private set; }
    public string SecondLabel { get; private set; }
    /// <summary>The second button opens the file location rather than the result.</summary>
    public bool Reveals { get; private set; }
    public bool IsToggle => RawKind == "Toggle";
    public bool IsCalc => RawKind == "Calculator";
    /// <summary>A request carried out in steps ("open notepad and weather side by side").</summary>
    public bool IsPlan => RawKind == "Plan";
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
            Changed(nameof(SwitchShift));
            Changed(nameof(TitleMargin));
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
    public Visibility ActionVisibility => Vis(IsToggle || IsPlan);
    // The plain and coloured versions of the glyph and the "Action" badge (one of each shows).
    public Visibility PlainGlyphVisibility => Vis(IconKey == null && !IsStatus && Tone == null);
    public Visibility ToneGlyphVisibility => Vis(IconKey == null && !IsStatus && Tone != null);
    public Visibility PlainActionVisibility => Vis((IsToggle || IsPlan) && Tone == null);
    public Visibility ToneActionVisibility => Vis((IsToggle || IsPlan) && Tone != null);
    public Visibility SubTextVisibility => Vis(SubText.Length > 0);
    /// <summary>Whether the row has buttons at all; they fade in on selection (<see cref="ButtonsOpacity"/>).</summary>
    public Visibility ButtonsVisibility => Vis(IsStatus ? CanUndo : !IsActionRow && !IsTimer && !_confirming);
    public bool ButtonsShown => IsStatus || _selected;
    public double ButtonsOpacity => ButtonsShown ? 1 : 0;
    public Visibility MoreVisibility => Vis(!IsStatus && !IsAnswer);

    private void Changed(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
