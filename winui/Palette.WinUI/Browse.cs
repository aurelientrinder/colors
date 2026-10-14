using System.Text.RegularExpressions;
using Windows.System;

namespace Palette;

/// <summary>
/// Walking through folders with the arrow keys; Left goes up a level, Right goes down:
/// <list type="bullet">
/// <item>Left on a file or folder result shows the folder it is in, with it selected. Left again
/// shows that folder's parent, with the folder just left selected, and so on.</item>
/// <item>Right on a folder lists what is in it.</item>
/// <item>Right on a file, once a walk has started from a search, returns to that search with its
/// row selected again.</item>
/// </list>
/// A walk is the search text becoming a folder's path ("C:\Users\me\Projects\"), which the engine
/// answers with the folder's entries; typing on filters them. Both keys only act with the caret at
/// the end of the text and a result with a path selected, so they still move the caret while
/// editing a search for an app or a setting.
/// </summary>
public sealed partial class MainWindow
{
    private string? _walkText;                // the search a walk started from, to come back to
    private int _walkRow = -1;                // and its selected row
    private string? _reselectTitle;           // select the row with this title when the results come back
    /// <summary>Per folder walked up from, the entry selected in it: coming back down finds it again.</summary>
    private readonly Dictionary<string, string> _walkSelected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A path into a folder, as the engine browses it (crates/palette-core/src/browse.rs).</summary>
    private static readonly Regex BrowsePath = new(@"^\s*(?:[A-Za-z]:|~)[\\/]", RegexOptions.Compiled);

    private static bool IsBrowsePath(string q) => BrowsePath.IsMatch(q);

    private static bool EndsWithSeparator(string s) => s.EndsWith('\\') || s.EndsWith('/');

    private bool CanBrowseKey =>
        _view == View.Results && Input.SelectionLength == 0 && Input.SelectionStart == Input.Text.Length
        && !Held(VirtualKey.Control) && !Held(VirtualKey.Shift) && !Held(VirtualKey.Menu);

    /// <summary>The file or folder a result stands for, and whether it is a folder.</summary>
    private static (string Path, bool IsFolder)? PathOf(HitVm row) =>
        row.Folder is { } d ? (d.TrimEnd('\\'), true) : row.Path is { } f ? (f, false) : null;

    /// <summary>Left arrow: where the selected result is, or one folder up.</summary>
    private bool TryBrowseUp(int sel)
    {
        if (!CanBrowseKey) return false;
        string text = Input.Text;

        if (IsBrowsePath(text))
        {
            if (!EndsWithSeparator(text))
            {
                // A filter was typed: Left clears it, keeping the selected entry selected.
                int cut = text.LastIndexOfAny(['\\', '/']);
                if (sel >= 0 && sel < Items.Count) _reselectTitle = Items[sel].Title;
                Browse(text[..(cut + 1)]);
                return true;
            }
            // Showing a folder: its parent, with this folder selected there.
            string folder = text.TrimEnd('\\', '/');
            if (sel >= 0 && sel < Items.Count && !Items[sel].IsAnswer) _walkSelected[folder] = Items[sel].Title;
            int at = folder.LastIndexOfAny(['\\', '/']);
            if (at < 0 || folder.Length == 2 && folder[1] == ':') return false;   // the profile or a drive's root
            _reselectTitle = folder[(at + 1)..];
            Browse(folder[..(at + 1)]);
            return true;
        }

        // A search: show the folder the selected result is in.
        if (sel < 0 || sel >= Items.Count || PathOf(Items[sel]) is not var (path, _)) return false;
        if (System.IO.Path.GetDirectoryName(path) is not { Length: > 0 } parent) return false;
        _walkText = text;
        _walkRow = sel - AnswerOffset;
        _walkSelected.Clear();
        _reselectTitle = System.IO.Path.GetFileName(path);
        _walkSelected[parent.TrimEnd('\\')] = _reselectTitle;
        Browse(parent.TrimEnd('\\') + "\\");
        return true;
    }

    /// <summary>Right arrow: into the selected folder; on a file, back to the search the walk began at.</summary>
    private bool TryBrowseDown(int sel)
    {
        if (!CanBrowseKey || sel < 0 || sel >= Items.Count) return false;
        if (PathOf(Items[sel]) is not var (path, isFolder)) return false;
        string text = Input.Text;

        if (isFolder)
        {
            // From a plain search, remember it so a file can bring the walk back to it.
            if (!IsBrowsePath(text))
            {
                _walkText = text;
                _walkRow = sel - AnswerOffset;
                _walkSelected.Clear();
            }
            // Back down a folder that was walked up out of: the entry it was left from.
            _reselectTitle = _walkSelected.GetValueOrDefault(path);
            Browse(path + "\\");
            return true;
        }

        if (IsBrowsePath(text) && _walkText is { } back)
        {
            _walkText = null;
            _restoreSelection = _walkRow;
            Browse(back);
            return true;
        }
        return false;
    }

    /// <summary>Search for <paramref name="text"/> as if typed, the caret after it.</summary>
    private void Browse(string text)
    {
        Input.Text = text;
        Input.SelectionStart = text.Length;
    }

    /// <summary>After a step up: the folder just left (or the file looked for) is selected.</summary>
    private void ReselectByTitle()
    {
        if (_reselectTitle is not { } title) return;
        _reselectTitle = null;
        for (int i = AnswerOffset; i < Items.Count; i++)
        {
            if (string.Equals(Items[i].Title, title, StringComparison.OrdinalIgnoreCase))
            {
                ResultsList.SelectedIndex = i;
                return;
            }
        }
    }
}
