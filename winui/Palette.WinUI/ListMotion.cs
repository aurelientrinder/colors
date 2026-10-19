using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Palette;

/// <summary>
/// The list's motion without laying the list out each frame. A preview opening or closing in a row
/// used to change that row's height every frame, and the list's height with it, so the whole list
/// was laid out again at every frame: 4 to 7 ms, too much to keep up with a 120 Hz screen.
///
/// Now a row takes its preview's full height at once (and keeps it until it has closed), and so
/// does the list; what moves each frame is drawn, not laid out:
/// <list type="bullet">
/// <item>the row is clipped to the part of its preview opened so far, so the preview unrolls;</item>
/// <item>the rows below it are shifted up by the part not yet opened, so they stay where they are
/// on screen while the panel grows above them, as before;</item>
/// <item>the list is clipped to the panel's current height, which grows or shrinks with the panel's
/// edge (MainWindow.ApplyShape).</item>
/// </list>
/// The shift and clip are applied to the rows' visuals on the compositor; the selection card adds
/// them to the row it is on (Motion.cs).
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The shift and clip drawn on each row this frame, by index (rows without any are absent).</summary>
    private readonly Dictionary<int, (double shift, double clip)> _rowFx = new();
    private readonly Dictionary<ListViewItem, (double shift, double clip)> _fxApplied = new();

    /// <summary>
    /// Draw the rows for this frame: <paramref name="opening"/> lists each row whose preview is not
    /// all shown yet, with the height still hidden.
    /// </summary>
    private void DrawRowMotion(List<(int index, double hidden)> opening)
    {
        _rowFx.Clear();
        if (opening.Count > 0)
        {
            opening.Sort((a, b) => a.index.CompareTo(b.index));
            for (int i = 0; i < Items.Count; i++)
            {
                double shift = 0, clip = 0;
                foreach (var (k, hidden) in opening)
                {
                    if (k < i) shift -= hidden;       // below a row still opening: up by what it hides
                    else if (k == i) clip = hidden;   // the row itself: cut to what is open
                }
                if (shift != 0 || clip != 0) _rowFx[i] = (shift, clip);
            }
        }

        // Apply to the rows on screen, and take it off the ones that no longer need it (a row's
        // container may also have been reused for another row since).
        var seen = new HashSet<ListViewItem>();
        foreach (var (i, fx) in _rowFx)
        {
            if (ResultsList.ContainerFromIndex(i) is not ListViewItem c) continue;
            seen.Add(c);
            ApplyRowFx(c, fx);
        }
        foreach (var c in _fxApplied.Keys.Where(c => !seen.Contains(c)).ToList()) ApplyRowFx(c, (0, 0));
    }

    private void ApplyRowFx(ListViewItem c, (double shift, double clip) fx)
    {
        _fxApplied.TryGetValue(c, out var was);   // none applied reads as (0, 0)
        if (was == fx) return;
        if (fx.shift != was.shift)
        {
            ElementCompositionPreview.SetIsTranslationEnabled(c, true);
            ElementCompositionPreview.GetElementVisual(c).Properties.InsertVector3("Translation", new Vector3(0, (float)fx.shift, 0));
        }
        if (fx.clip != was.clip)
        {
            c.Clip = fx.clip > 0
                ? new RectangleGeometry { Rect = new Windows.Foundation.Rect(-16, -16, c.ActualWidth + 32, Math.Max(0, c.ActualHeight - fx.clip) + 16) }
                : null;
        }
        if (fx == (0, 0)) _fxApplied.Remove(c);
        else _fxApplied[c] = fx;
    }

    private const double ListInsetDip = 16;   // the list's left and right margins in the panel
    private double _listShownW = double.MaxValue;   // the list's width the panel shows this frame

    /// <summary>
    /// Clip the list to the part of it the panel shows this frame, <paramref name="shownH"/> high and
    /// <paramref name="shownW"/> wide (none when it shows all of it). The window is see-through:
    /// anything of the list beyond the panel would be drawn beside it.
    /// </summary>
    private void ClipList(double shownH, double shownW)
    {
        double h = ResultsList.Height, w = ResultsList.ActualWidth;
        if ((double.IsNaN(h) || shownH >= h - 0.5) && shownW >= w - 0.5)
        {
            if (ResultsList.Clip != null) ResultsList.Clip = null;
            return;
        }
        // Out to the panel's edges (the list's margins), above the first row and either side: the
        // selection card swells a little past its row.
        var rect = new Windows.Foundation.Rect(-12, -16, Math.Max(0, shownW) + 16, Math.Max(0, shownH) + 16);
        if (ResultsList.Clip is RectangleGeometry g && g.Rect == rect) return;
        ResultsList.Clip = new RectangleGeometry { Rect = rect };
    }

    /// <summary>
    /// While the panel's width glides (the month card), the list is laid out once, at the width it
    /// is heading to, rather than at every frame's: held to the panel's left edge, it is uncovered
    /// as the panel widens, and the panel's edge closes in on it as it narrows. Laying the rows and
    /// the card out afresh at each frame's width cost 4 to 11 ms a frame. The search field still
    /// follows each frame: it is one control, and cheap.
    /// </summary>
    private void HoldListWidth()
    {
        if (_wClock.IsRunning)
        {
            double w = _wTo - ListInsetDip;
            if (ResultsList.Width != w)
            {
                ResultsList.HorizontalAlignment = HorizontalAlignment.Left;
                ResultsList.Width = w;
            }
        }
        else if (!double.IsNaN(ResultsList.Width))
        {
            // Settled: it now stretches to the width it was given, so nothing moves.
            ResultsList.Width = double.NaN;
            ResultsList.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
    }
}
