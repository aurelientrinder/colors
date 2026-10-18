using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.ApplicationModel.DataTransfer;

namespace Palette;

/// <summary>
/// The views besides search results:
/// <list type="bullet">
/// <item>All apps: every app A to Z, scrolling past the panel's height, as in Start.</item>
/// <item>Actions: what can be done with one result (run as administrator, open file location,
/// pin, uninstall, ...), opened by holding Tab, the menu key, a right click or the row's More
/// button. Typing filters
/// them; Esc goes back.</item>
/// <item>Done: a quick setting just ran; one line says so, with Undo. Typing searches again.</item>
/// </list>
/// The home view (pins, most used, recent files) is simply what the engine returns for an empty
/// query.
/// </summary>
public sealed partial class MainWindow
{
    private enum View { Results, AllApps, Actions, Done }

    private const string SearchPlaceholder = "Search";

    private View _view = View.Results;
    private View _actFrom;                    // the view the actions were opened from
    private ulong _actGen;                    // the result the actions are for
    private int _actIndex;
    private string? _actCopyText;
    private string _actTitle = "";
    private List<Native.ActionRow> _actions = new();
    private List<Native.ActionRow> _shownActions = new();
    private string _savedQuery = "";
    private int _restoreSelection = -1;       // reselect this row when the results come back
    private string? _ignoreText;              // Input.Text set by code: its TextChanged is not typing

    /// <summary>Set the search text from code. TextChanged arrives later, so it is recognised by value.</summary>
    private void SetInputText(string text)
    {
        if (Input.Text == text) return;
        _ignoreText = text;
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    private void SetView(View v, string placeholder)
    {
        if (v != _view) PlayListEntrance(forward: v != View.Results);
        _view = v;
        Input.PlaceholderText = placeholder;
        UpdateCondensed();
        UpdateOpenButton();
    }

    /// <summary>The search field's button says what Enter does to the selected row.</summary>
    private void UpdateOpenButton()
    {
        int i = ResultsList.SelectedIndex;
        bool show = _view != View.Done && i >= 0 && i < Items.Count;
        OpenButton.Opacity = show ? 1 : 0;   // fades (OpacityTransition)
        OpenButton.IsHitTestVisible = show;
        KeyHints.Opacity = show && _settings.KeyHints ? 1 : 0;
        if (!show) return;
        KeyHints.Text = KeyHintsFor(Items[i]);
        var row = Items[i];
        OpenButton.Content = _view == View.Actions ? "Run"
            : row.IsTimer ? row.TimerLabel
            : row.Confirming ? row.ConfirmLabel
            : row.IsAnswer ? "Open" : row.RawKind switch
        {
            "Window" => "Switch to",
            "Command" => "Run",
            "Calculator" => "Copy result",
            "Web" => "Search the web",
            "Toggle" => "Apply",
            "Plan" => "Run",
            _ => "Open",
        };
    }

    /// <summary>The keys that do something for this row, as a footer line in the search card.</summary>
    private string KeyHintsFor(HitVm row)
    {
        if (_view == View.Actions) return "Esc Back";
        if (row.Confirming) return $"↵ {row.ConfirmLabel}  ·  Esc Cancel";
        if (row.IsTimer) return $"↵ {row.TimerLabel}";
        var keys = new List<string>();
        if (row.IsWindow) keys.Add("Ctrl+M Minimize  ·  Ctrl+W Close");
        if (!row.IsAnswer && PathOf(row) is var (_, isFolder))
        {
            keys.Add(isFolder ? "→ Browse" : "← Show folder");
        }
        if (row.HasSlider) keys.Add("← → Adjust");
        if (!row.IsAnswer && row.Reveals) keys.Add("Ctrl+↵ Location");
        keys.Add("Hold Tab Actions");
        return string.Join("  ·  ", keys);
    }

    private void OnOpenButtonClick(object sender, RoutedEventArgs e) => Launch(ResultsList.SelectedIndex, reveal: false);

    private void OnRowMoreClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HitVm vm || Items.IndexOf(vm) is not (>= 0 and var i)) return;
        ResultsList.SelectedIndex = i;
        EnterActions(i);
    }

    private void OnRowSecondClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not HitVm vm) return;
        if (vm.IsStatus)
        {
            Undo();
            return;
        }
        int i = Items.IndexOf(vm);
        if (i < 0) return;
        if (vm.AnswerAltUrl != null)
        {
            OpenUrl(vm.AnswerAltUrl);
            HidePalette(restoreFocus: false);
        }
        else if (vm.Reveals) ActOrLaunch(i, "location");
        else Launch(i, reveal: false);
    }

    // ------------------------------------------------------------------ done

    private string? _undoQuery;               // what to search for to reverse the setting just changed
    private bool _undoPending;                // the next results are for Undo: run their quick setting

    /// <summary>A quick setting ran: keep the palette open on one line saying so.</summary>
    private void ShowDone(string title)
    {
        _undoQuery = Inverse(title);
        Interlocked.Increment(ref _queryVersion);   // drop any search still on its way
        SetView(View.Done, SearchPlaceholder);
        SetInputText("");
        _latestQuery = "";
        _holdHeight = false;
        Items.Clear();
        Items.Add(HitVm.Status(title, "Done", canUndo: _undoQuery != null));
        Resize(recenter: false);
        UpdatePreview();
        UpdateOpenButton();
    }

    /// <summary>The request that undoes "Turn dark mode on" etc.; null when there is none.</summary>
    private static string? Inverse(string title) => title switch
    {
        "Mute" => "unmute",
        "Unmute" => "mute",
        _ when title.EndsWith(" on", StringComparison.Ordinal) => title[..^3] + " off",
        _ when title.EndsWith(" off", StringComparison.Ordinal) => title[..^4] + " on",
        _ => null,
    };

    private void Undo()
    {
        if (_undoQuery is not { } q) return;
        _undoPending = true;
        SetView(View.Results, SearchPlaceholder);
        Enqueue(q);   // the field stays empty: ApplyResults runs the setting and shows Done again
    }

    /// <summary>Results for <see cref="Undo"/>: run their quick setting instead of showing them.</summary>
    private void RunUndo(Native.SearchResult r)
    {
        _undoPending = false;
        int i = r.Hits.FindIndex(h => h.Kind == "Toggle");
        if (i < 0)
        {
            ShowHome();
            return;
        }
        Native.Launch(r.Gen, i, reveal: false);
        ShowDone(r.Hits[i].Title);
    }

    // ------------------------------------------------------------------ all apps

    private void ShowAllApps(int select = 0)
    {
        var r = Native.AllApps();
        Interlocked.Increment(ref _queryVersion);   // drop any search still on its way
        SetView(View.AllApps, "All apps · type to search");
        SetInputText("");
        _gen = r.Gen;
        _holdHeight = false;
        SyncRows(r.Hits);
        if (Items.Count > 0) ResultsList.SelectedIndex = Math.Clamp(select, 0, Items.Count - 1);
        Resize(recenter: false);
        ScrollToSelection();
        UpdatePreview();
    }

    /// <summary>Back from All apps (or a search started there) to the home view.</summary>
    private void ShowHome()
    {
        SetView(View.Results, SearchPlaceholder);
        SetInputText("");
        Enqueue("");
    }

    // ------------------------------------------------------------------ actions

    private void EnterActions(int index)
    {
        if (_view == View.Actions || index < 0 || index >= Items.Count || Items[index].IsAnswer) return;
        int engine = index - AnswerOffset;
        var acts = Native.Actions(_gen, engine);
        if (acts.Count == 0) return;
        var vm = Items[index];
        _actFrom = _view;
        _actGen = _gen;
        _actIndex = engine;
        _actCopyText = vm.CopyText;
        _actTitle = vm.Title;
        _actions = acts;
        _savedQuery = Input.Text;
        SetView(View.Actions, $"Actions for {vm.Title}");
        SetInputText("");
        ShowActions("");
        Input.Focus(FocusState.Programmatic);
    }

    private static string GlyphFor(string id) => id switch
    {
        "open" => "",
        "admin" => "",
        "openwith" => "",
        "location" => "",
        "terminal" => "",
        "copypath" => "",
        "pin" => "",
        "unpin" => "",
        "pinlater" => "",
        "pinearlier" => "",
        "properties" => "",
        "uninstall" => "",
        "close" => "",
        _ => "",
    };

    /// <summary>List the actions whose name contains <paramref name="filter"/>.</summary>
    private void ShowActions(string filter)
    {
        string f = filter.Trim();
        _shownActions = _actions.Where(a => f.Length == 0 || a.Title.Contains(f, StringComparison.OrdinalIgnoreCase)).ToList();
        Items.Clear();
        foreach (var a in _shownActions)
        {
            var h = new Native.Hit(a.Title, _actTitle, "Command", null, a.Copy);
            Items.Add(new HitVm(h, GlyphFor(a.Id), a.Hint));
        }
        if (Items.Count > 0) ResultsList.SelectedIndex = 0;
        _holdHeight = false;
        Resize(recenter: false);
        UpdatePreview();
    }

    /// <summary>Back to the list the actions were opened from, with the same row selected.</summary>
    private void LeaveActions()
    {
        if (_view != View.Actions) return;
        if (_actFrom == View.AllApps)
        {
            ShowAllApps(_actIndex);
            return;
        }
        SetView(View.Results, SearchPlaceholder);
        SetInputText(_savedQuery);
        _restoreSelection = _actIndex;
        Enqueue(_savedQuery);
    }

    private void RunAction(int row)
    {
        if (row < 0 || row >= _shownActions.Count) return;
        var a = _shownActions[row];
        if (a.Id == "open")
        {
            LaunchHit(_actGen, _actIndex, _actCopyText, reveal: false);
            return;
        }
        if (a.Copy is { } text)
        {
            var dp = new DataPackage();
            dp.SetText(text);
            Clipboard.SetContent(dp);
            HidePalette(restoreFocus: true);
            return;
        }
        switch (Native.Act(_actGen, _actIndex, a.Id))
        {
            case Native.ActResult.Hide:
                HidePalette(restoreFocus: false);   // what was opened takes the focus
                break;
            case Native.ActResult.Refresh:
                LeaveActions();                      // pins changed: show the list again
                break;
        }
    }

    private void OnRowRightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (_view == View.Actions || (e.OriginalSource as FrameworkElement)?.DataContext is not HitVm vm) return;
        int i = Items.IndexOf(vm);
        if (i < 0) return;
        ResultsList.SelectedIndex = i;
        EnterActions(i);
        e.Handled = true;
    }
}
