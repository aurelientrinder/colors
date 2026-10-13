using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace Palette;

/// <summary>
/// The two views besides search results:
/// <list type="bullet">
/// <item>All apps: every app A to Z, scrolling past the panel's height, as in Start.</item>
/// <item>Actions: what can be done with one result (run as administrator, open file location,
/// pin, uninstall, ...), opened with Tab or a right click. Typing filters them; Esc goes back.</item>
/// </list>
/// The home view (pins, most used, recent files) is simply what the engine returns for an empty
/// query.
/// </summary>
public sealed partial class MainWindow
{
    private enum View { Results, AllApps, Actions }

    private const string SearchPlaceholder = "Search apps, files, settings…";

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
        _view = v;
        Input.PlaceholderText = placeholder;
        FooterEsc.Text = v == View.Results ? "Close" : "Back";
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
        SyncRows(r.Hits, r.Gen, "");
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
        if (_view == View.Actions || index < 0 || index >= Items.Count) return;
        var acts = Native.Actions(_gen, index);
        if (acts.Count == 0) return;
        var vm = Items[index];
        _actFrom = _view;
        _actGen = _gen;
        _actIndex = index;
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
            Items.Add(new HitVm(h, f, GlyphFor(a.Id), a.Hint));
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

    // ------------------------------------------------------------------ scrolling

    // The list scrolls only when it has more rows than the panel shows (All apps, many pins).
    // User scrolling is off on the list (it is drawn flipped, so the wheel would run backwards);
    // the wheel and the selection drive it instead.

    private ScrollViewer? _listScroll;

    private ScrollViewer? ListScroll => _listScroll ??= FindScrollViewer(ResultsList);

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is ScrollViewer sv) return sv;
            if (FindScrollViewer(c) is { } found) return found;
        }
        return null;
    }

    private void ScrollToSelection()
    {
        if (ResultsList.SelectedItem is { } item) ResultsList.ScrollIntoView(item);
    }

    private void OnListWheel(object sender, PointerRoutedEventArgs e)
    {
        if (Items.Count <= MaxRows || ListScroll is not { } sv) return;
        int delta = e.GetCurrentPoint(ResultsList).Properties.MouseWheelDelta;
        // Flipped list: wheel up shows rows further from the search field, i.e. a larger offset.
        sv.ChangeView(null, Math.Max(0, sv.VerticalOffset + delta / 120.0 * RowDip * 2), null, disableAnimation: false);
        e.Handled = true;
    }
}
